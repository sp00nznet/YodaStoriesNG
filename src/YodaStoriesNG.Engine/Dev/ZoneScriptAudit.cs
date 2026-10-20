using System.Text;
using YodaStoriesNG.Engine.Data;
using YodaStoriesNG.Engine.Parsing;

namespace YodaStoriesNG.Engine.Game;

public unsafe partial class GameEngine
{
    /// <summary>Static coverage audit of every native zone, plus headless enemy-drop regressions.</summary>
    public static int AuditZoneScripts(string path, string reportPath)
    {
        var data = new DtaParser().Parse(path);
        var findings = new List<(string Kind, int Zone, int Action, string Detail)>();
        var resolved = new List<(string Kind, int Zone, int Action, string Detail)>();
        var conditions = new Dictionary<ConditionOpcode, (int Count, HashSet<int> Zones)>();
        var instructions = new Dictionary<InstructionOpcode, (int Count, HashSet<int> Zones)>();
        void Finding(string kind, Zone z, int a, string detail) =>
            (kind.StartsWith("Fixed:") ? resolved : findings).Add((kind,z.Id,a,detail));
        void Point(Zone z, int a, string op, IReadOnlyList<short> args, int x, int y, int? layer = null)
        {
            if (args[x] < 0 || args[y] < 0 || args[x] >= z.Width || args[y] >= z.Height ||
                layer.HasValue && (args[layer.Value] < 0 || args[layer.Value] > 2))
                Finding("Coordinate review",z,a,$"{op}({string.Join(',',args)}) — may be an authored off-map animation/sentinel");
        }
        foreach (var z in data.Zones)
        for (int a = 0; a < z.Actions.Count; a++)
        {
            var action = z.Actions[a];
            foreach (var c in action.Conditions)
            {
                var entry=conditions.GetValueOrDefault(c.Opcode,(0,new HashSet<int>()));
                entry.Item2.Add(z.Id); conditions[c.Opcode]=(entry.Item1+1,entry.Item2);
                var args=c.Arguments;
                if (!Enum.IsDefined(c.Opcode) || c.Opcode==ConditionOpcode.Unused)
                    Finding("Unhandled condition",z,a,$"0x{(int)c.Opcode:X2}: unsupported conditions now evaluate false");
                if (args.Count>=3 && c.Opcode is ConditionOpcode.Bump or ConditionOpcode.Standing)
                    Point(z,a,c.Opcode.ToString(),args,0,1);
                if (args.Count>=4 && c.Opcode is ConditionOpcode.TileAtIs or ConditionOpcode.IsVariable)
                    Point(z,a,c.Opcode.ToString(),args,1,2,3);
                if (args.Count>=5 && c.Opcode is ConditionOpcode.PlacedItemIs or ConditionOpcode.PlacedItemIsNot)
                    Point(z,a,c.Opcode.ToString(),args,0,1,2);
                if (args.Count>=1 && c.Opcode==ConditionOpcode.MonsterIsDead && (args[0]<0 || args[0]>=z.Monsters.Count))
                    Finding("Invalid monster reference",z,a,$"MonsterIsDead({args[0]}); monster count {z.Monsters.Count}");
                if (c.Opcode==ConditionOpcode.DropsQuestItemAt)
                {
                    Finding("Fixed: quest-marker condition",z,a,"DropsQuestItemAt reads the enabled native quest-item hotspot at the authored coordinates");
                    if (args.Count>=2 && !z.Objects.Any(h=>(int)h.Type==0 && h.X==args[0] && h.Y==args[1]))
                        Finding("Missing authored quest marker",z,a,$"DropsQuestItemAt({args[0]},{args[1]}) has no matching hotspot; left false (zone 160 also has a later correct action for its actual marker)");
                }
                if (c.Opcode==ConditionOpcode.HasNoActiveMonsters && z.Actions.Any(other=>other.Instructions.Any(i=>i.Opcode is InstructionOpcode.DisableMonster or InstructionOpcode.DisableAllMonsters)))
                    Finding("Fixed: inactive monsters",z,a,"Disabled/dead monsters no longer block HasNoActiveMonsters; enabled living native actors count regardless of hostility");
                if (c.Opcode==ConditionOpcode.ZoneIsSolved)
                    Finding("Fixed: solved-state consistency",z,a,"ZoneIsSolved sees generated and saved progress; script and pickup solves propagate to all rooms of the sector");
            }
            foreach (var i in action.Instructions)
            {
                var entry=instructions.GetValueOrDefault(i.Opcode,(0,new HashSet<int>()));
                entry.Item2.Add(z.Id); instructions[i.Opcode]=(entry.Item1+1,entry.Item2);
                var args=i.Arguments;
                if (!Enum.IsDefined(i.Opcode)) Finding("Unhandled instruction",z,a,$"0x{(int)i.Opcode:X2}: logged and skipped");
                if (args.Count>=3 && i.Opcode is InstructionOpcode.PlaceTile or InstructionOpcode.RemoveTile or InstructionOpcode.SetVariable)
                    Point(z,a,i.Opcode.ToString(),args,0,1,2);
                if (args.Count>=5 && i.Opcode==InstructionOpcode.MoveTile)
                { Point(z,a,"MoveTile source",args,0,1,2); Point(z,a,"MoveTile destination",args,3,4,2); }
                if (args.Count>=1 && i.Opcode is InstructionOpcode.EnableMonster or InstructionOpcode.DisableMonster && (args[0]<0 || args[0]>=z.Monsters.Count))
                    Finding("Invalid monster reference",z,a,$"{i.Opcode}({args[0]}); monster count {z.Monsters.Count}");
                if (args.Count>=1 && i.Opcode is InstructionOpcode.EnableHotspot or InstructionOpcode.DisableHotspot && (args[0]<0 || args[0]>=z.Objects.Count))
                    Finding("Invalid hotspot reference",z,a,$"{i.Opcode}({args[0]}); hotspot count {z.Objects.Count}");
                if (args.Count>=3 && i.Opcode==InstructionOpcode.ChangeZone)
                {
                    var dest=data.Zones.FirstOrDefault(d=>d.Id==args[0]);
                    if (dest==null) Finding("Zone-change review",z,a,$"ChangeZone({string.Join(',',args)}) — dynamic travel sentinel or missing zone");
                    else if (args[1]<0 || args[2]<0 || args[1]>=dest.Width || args[2]>=dest.Height)
                        Finding("Zone-change review",z,a,$"ChangeZone({string.Join(',',args)}) — destination outside {dest.Width}x{dest.Height}");
                }
                if (i.Opcode==InstructionOpcode.StopSound)
                    Finding("Fixed: StopSound dispatch",z,a,"StopSound stops active effects and clears queued audio; native music-only semantics are uncertain");
                if (i.Opcode==InstructionOpcode.DrawTile)
                {
                    if (args.Count>=3) Point(z,a,"DrawTile position",args,0,1);
                    if (args.Count>=3 && (args[2]<0 || args[2]>2))
                        Finding("Fixed: temporary drawing",z,a,$"DrawTile({args[0]},{args[1]},{args[2]}) renders a temporary frame without mutating collision layers");
                    else Finding("Drawing review",z,a,"DrawTile mutates map/collision like PlaceTile; temporary drawing semantics need confirmation");
                }
                if (i.Opcode==InstructionOpcode.AddItem && args.Count>=1 && args[0]<0)
                    Finding("Fixed: negative inventory item",z,a,$"AddItem({args[0]}) is ignored as in the reference engine; old negative saved items are filtered on load");
                if (i.Opcode==InstructionOpcode.RemoveItem && args.Count>=1 && args[0]>=0 && args[0]<data.Tiles.Count && data.Tiles[args[0]].IsWeapon)
                    Finding("Fixed: weapon removal",z,a,$"RemoveItem({args[0]}) consumes one copy including equipped ownership; reserves and ammo remain when copies exist");
            }
        }
        static void Require(bool ok,string message) { if(!ok) throw new InvalidOperationException(message); }
        int monsters=0, drops=0, questDrops=0, checks=0;
        var affected=new HashSet<int>();
        foreach(var original in data.Zones)
        {
            var zone=new Zone { Id=original.Id, Width=original.Width,Height=original.Height,
                TileGrid=(ushort[,,])original.TileGrid.Clone(), Monsters=original.Monsters };
            var e=new GameEngine("") { _gameData=data };
            e._state.CurrentZone=zone; e._state.CurrentZoneId=zone.Id;
            e.InitializeZoneNPCs();
            var native=original.Monsters.Where(m=>m.CharacterId!=ushort.MaxValue).ToList();
            Require(e._state.ZoneNPCs.Count==native.Count,$"zone {zone.Id}: actor indexing mismatch");
            for(int m=0;m<native.Count;m++)
            {
                var npc=e._state.ZoneNPCs[m]; var source=native[m]; monsters++;
                Require(npc.DropsLoot==source.DropsLoot,$"zone {zone.Id} monster {m}: drop flag lost");
                Require(npc.LootItemId==(source.Loot>0 ? source.Loot-1 : -1),$"zone {zone.Id} monster {m}: wrong loot decoding");
                checks+=2;
                if(source.DropsLoot) { drops++; if(source.Loot<=0) { questDrops++; affected.Add(zone.Id); } }
            }
        }
        // Replay zone 289's real metadata, with a generated quest reward assigned.
        foreach(bool smooth in new[]{false,true})
        {
            var original=data.Zones[289];
            var zone=new Zone { Id=289,Width=original.Width,Height=original.Height,
                TileGrid=(ushort[,,])original.TileGrid.Clone(),Monsters=original.Monsters,
                Objects=original.Objects.Select(o=>new ZoneObject {Type=o.Type,X=o.X,Y=o.Y,Argument=430,Enabled=false}).ToList() };
            var e=new GameEngine("") {_gameData=data,_smoothMovement=smooth,_smoothMovementRequested=smooth,_showingTitleScreen=false};
            Require(e._state.ZoneNPCs.Count==0,"new scene contains actors");
            e._state.CurrentZone=zone;e._state.CurrentZoneId=289;e.InitializeZoneNPCs();
            Require(e._state.ZoneNPCs.Count==original.Monsters.Count,"replay actor indexing mismatch");
            var carrier=e._state.ZoneNPCs.Single(n=>n.StartX==2 && n.StartY==4);carrier.Health=0;e.HandleNPCDeath(carrier);
            Require(zone.GetTile(carrier.X,carrier.Y,1)==430,"zone 289 quest drop missing");checks++;
            e._state.PlayerX=2;e._state.PlayerY=5;
            e.TryMovePlayer(0,-1,Direction.Up);
            if(smooth) e.UpdateSmoothMovement(1.0/6);
            Require(e._state.Inventory.Count(i=>i==430)==1,"zone 289 quest reward cannot be collected");checks++;
            e.HandleNPCDeath(carrier);
            Require(zone.GetTile(carrier.X,carrier.Y,1)==ushort.MaxValue,"death awarded the quest item twice");checks++;
            carrier=e._state.ZoneNPCs.Single(n=>n.StartX==5 && n.StartY==14);carrier.Health=0;e.HandleNPCDeath(carrier);
            Require(zone.GetTile(carrier.X,carrier.Y,1)==513,"zone 289 explicit blaster drop missing");checks++;
        }
        int scriptChecks = AuditScriptCorrections(data);
        var report=new StringBuilder();
        report.AppendLine("# Zone script audit\n");
        report.AppendLine($"Scanned **{data.Zones.Count} zones**, **{data.Zones.Sum(z=>z.Actions.Count)} actions**, **{conditions.Values.Sum(e=>e.Count)} conditions**, **{instructions.Values.Sum(e=>e.Count)} instructions**, and **{monsters} monsters** from the original data. No game window was opened.\n");
        report.AppendLine("## Confirmed and fixed: enemy death rewards\n");
        report.AppendLine($"The engine discarded the native death-drop flag and treated zero/-1 loot as no reward. Its fallback also searched ordinary crates instead of native quest-item hotspots (type 0). Both are corrected. There are {drops} flagged death drops, including {questDrops} generated-quest drops across {affected.Count} zones. Explicit carried items no longer override a false death-drop flag.\n");
        report.AppendLine("Zones with generated-quest death drops: "+string.Join(", ",affected.Order())+".\n");
        report.AppendLine($"Headless regression checks: **{checks} passed** (every monster's flag/loot decoding, plus zone 289 quest reward, collection, explicit weapon reward and duplicate-death protection in both movement modes).\n");
        report.AppendLine($"Additional script regression checks: **{scriptChecks} passed**, including every native DrawTile tuple and quest-marker condition, all solved-state/active-monster conditions, scripted weapon removals, partial/full redraw, transient reset, invalid inventory recovery and sound dispatch. These are headless state checks; actual audio output and visual quality have not been observed.\n");
        report.AppendLine("## Corrected script behavior\n");
        foreach (var group in resolved.GroupBy(f=>f.Kind).OrderBy(g=>g.Key))
        {
            report.AppendLine($"### {group.Key[7..]}: {group.Count()} occurrences\n");
            report.AppendLine(group.First().Detail+".\n");
            report.AppendLine("Zones: "+string.Join(", ",group.Select(f=>f.Zone).Distinct().Order())+".\n");
        }
        report.AppendLine("Reference behavior checked against WebFun's [quest-marker condition](https://codeberg.org/cyco/WebFun/src/branch/master/src/engine/script/conditions/drops-quest-item-at.ts), [temporary drawing disassembly](https://codeberg.org/cyco/WebFun/src/branch/master/src/engine/script/instructions/draw-tile.ts), [active-monster condition](https://codeberg.org/cyco/WebFun/src/branch/master/src/engine/script/conditions/has-no-active-monsters.ts), [negative AddItem handling](https://codeberg.org/cyco/WebFun/src/branch/master/src/engine/script/instructions/add-item.ts), and solved-sector operations. StopSound is also a no-op in WebFun; stopping all currently playing effects here is an explicit engine policy pending original-game audio confirmation.\n");
        report.AppendLine("## Remaining data issues requiring follow-up\n");
        report.AppendLine("These are source/data findings, not proof every listed action fails during play. Coordinate findings may be deliberate off-map animations; generated rooms and travel sentinels require runtime context.\n");
        report.AppendLine("All opcodes present in the data have named switch cases. Out-of-range hotspot indices below are authored references with no matching hotspot; they remain safely ignored rather than guessing which puzzle object was intended.\n");
        foreach(var group in findings.GroupBy(f=>f.Kind).OrderBy(g=>g.Key))
        {
            report.AppendLine($"### {group.Key}: {group.Count()} occurrences\n");
            foreach(var f in group)report.AppendLine($"- Zone {f.Zone}, action {f.Action} (zero-based): {f.Detail}");
            report.AppendLine();
        }
        report.AppendLine("## Opcode coverage\n\n| Kind | Opcode | Name | Uses | Zones |\n|---|---|---|---:|---:|");
        foreach(var p in conditions.OrderBy(p=>(int)p.Key)) report.AppendLine($"| Condition | 0x{(int)p.Key:X2} | {p.Key} | {p.Value.Count} | {p.Value.Zones.Count} |");
        foreach(var p in instructions.OrderBy(p=>(int)p.Key)) report.AppendLine($"| Instruction | 0x{(int)p.Key:X2} | {p.Key} | {p.Value.Count} | {p.Value.Zones.Count} |");
        report.AppendLine("\n## Limits\n\nThis audit checks every stored script against implemented opcode families and meaningful coordinate/reference arguments. Unused argument slots contain arbitrary data and are intentionally ignored. It does not exhaustively simulate every mission, inventory, random roll, movement path or script-state combination. Wait resumes on the next update and redraw delay arguments are currently ignored; timing fidelity still needs review. Passing the audit does not prove all puzzles are playable.\n\nRe-run: `dotnet run --project src/YodaStoriesNG.Engine -- --zone-script-audit Yoda/yodesk.dta docs/ZONE-SCRIPT-AUDIT.md`.");
        File.WriteAllText(reportPath,report.ToString());
        Console.WriteLine($"Zone script audit: {checks + scriptChecks} regression checks passed; {resolved.Count} corrected occurrences; {findings.Count} remaining review occurrences. Report: {Path.GetFullPath(reportPath)}");
        return 0;
    }
}
