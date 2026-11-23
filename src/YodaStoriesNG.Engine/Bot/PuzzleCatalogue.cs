using System.Security.Cryptography;
using System.Text.Json;
using YodaStoriesNG.Engine.Data;

namespace YodaStoriesNG.Engine.Bot;

/// <summary>Conditional contracts compiled from native data, not a claim that a puzzle is solved.</summary>
public sealed record PuzzleClause(string Opcode, short[] Arguments);
public sealed record PuzzleActionContract(int Index, string Trigger, PuzzleClause[] Requires,
    PuzzleClause[] Effects, string[] Reads, string[] Writes, int[] Producers, string[] Unsupported);
public sealed record PuzzleHotspotContract(int Index,string Type,int X,int Y,int NativeArgument);
public sealed record PuzzleActorContract(int Index,int Character,int X,int Y,bool DropsLoot,int Loot);
public sealed record PuzzleTemplate(int Zone, string? Name, string Type, string[] Mechanics,
    int[] RequiredItems, int[] ProvidedItems, int[] GoalItems, int[] LinkedRooms,
    PuzzleHotspotContract[] Hotspots,PuzzleActorContract[] Actors,PuzzleActionContract[] Actions, string[] Guidelines);
public sealed record PuzzleDefinitionContract(int Id, string Type, int Item1, int? Item2, int[] MetadataReferences);
public sealed record PuzzleCatalogue(int SchemaVersion, string Game, string DataFingerprint,
    int PuzzleDefinitions, PuzzleDefinitionContract[] Definitions, PuzzleTemplate[] Templates)
{
    public static PuzzleCatalogue Compile(GameData data)
    {
        var templates=data.Zones.Select(zone=>
        {
            var reads=zone.Actions.Select(a=>a.Conditions.SelectMany(ReadSlots).Distinct().Order().ToArray()).ToArray();
            var writes=zone.Actions.Select(a=>a.Instructions.SelectMany(WriteSlots).Distinct().Order().ToArray()).ToArray();
            var contracts=zone.Actions.Select((action,index)=>new PuzzleActionContract(index,Trigger(action),
                action.Conditions.Select(c=>new PuzzleClause(c.Opcode.ToString(),c.Arguments.ToArray())).ToArray(),
                action.Instructions.Select(i=>new PuzzleClause(i.Opcode.ToString(),i.Arguments.ToArray())).ToArray(),
                reads[index],writes[index],Enumerable.Range(0,zone.Actions.Count)
                    .Where(i=>i!=index && writes[i].Intersect(reads[index]).Any()).ToArray(),
                action.Conditions.Where(c=>!Enum.IsDefined(c.Opcode) || c.Opcode==ConditionOpcode.Unused)
                    .Select(c=>$"condition:{(int)c.Opcode:X}")
                    .Concat(action.Instructions.Where(i=>!Enum.IsDefined(i.Opcode)).Select(i=>$"instruction:{(int)i.Opcode:X}"))
                    .Distinct().ToArray())).ToArray();
            var conditions=zone.Actions.SelectMany(a=>a.Conditions).ToArray();
            var instructions=zone.Actions.SelectMany(a=>a.Instructions).ToArray();
            var mechanics=new List<string>();
            if(conditions.Any(c=>c.Opcode==ConditionOpcode.ZoneNotInitialized) || instructions.Any(i=>i.Opcode==InstructionOpcode.RollDice)) mechanics.Add("initialization-and-variants");
            if(zone.RequiredItems.Count>0 || conditions.Any(c=>c.Opcode==ConditionOpcode.PlacedItemIs)) mechanics.Add("item-exchange");
            if(zone.TileGrid.Cast<ushort>().Any(t=>t<data.Tiles.Count && data.Tiles[t].IsDraggable)) mechanics.Add("push-pull");
            if(conditions.Any(c=>c.Opcode==ConditionOpcode.StandingOn)) mechanics.Add("floor-switches");
            if(conditions.Any(c=>c.Opcode is ConditionOpcode.MonsterIsDead or ConditionOpcode.HasNoActiveMonsters)) mechanics.Add("combat-gated");
            if(instructions.Any(i=>i.Opcode is InstructionOpcode.MoveHeroTo or InstructionOpcode.MoveHeroBy)) mechanics.Add("scripted-relocation");
            if(zone.Objects.Any(h=>h.Type is ZoneObjectType.DoorEntrance or ZoneObjectType.DoorExit) || instructions.Any(i=>i.Opcode==InstructionOpcode.ChangeZone)) mechanics.Add("room-traversal");
            if(instructions.Any(i=>i.Opcode==InstructionOpcode.WinGame)) mechanics.Add("authored-victory");
            if(mechanics.Count==0) mechanics.Add("navigation-or-pickup");
            return new PuzzleTemplate(zone.Id,zone.Name,zone.Type.ToString(),mechanics.ToArray(),
                zone.RequiredItems.Select(i=>(int)i).ToArray(),zone.ProvidedItems.Select(i=>(int)i).ToArray(),
                zone.GoalItems.Select(i=>(int)i).ToArray(),zone.Objects.Where(h=>h.Type==ZoneObjectType.DoorEntrance && h.FileArgument<data.Zones.Count)
                    .Select(h=>(int)h.FileArgument).Concat(instructions.Where(i=>i.Opcode==InstructionOpcode.ChangeZone && i.Arguments.Count>=3 && i.Arguments[0]>=0 && i.Arguments[0]<data.Zones.Count)
                    .Select(i=>(int)i.Arguments[0])).Distinct().Order().ToArray(),
                zone.Objects.Select((h,i)=>new PuzzleHotspotContract(i,h.Type.ToString(),h.X,h.Y,h.FileArgument)).ToArray(),
                zone.Monsters.Select((m,i)=>new PuzzleActorContract(i,m.CharacterId,m.X,m.Y,m.DropsLoot,m.Loot)).ToArray(),contracts,
                mechanics.Select(Guideline).ToArray());
        }).ToArray();
        // Covers immutable interpreter inputs, including action argument padding;
        // individual plans additionally bind the complete generated mutable state.
        var fingerprint=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new {
            Game=data.GameType.ToString(),Tiles=data.Tiles.Select(t=>(uint)t.Flags),
            Definitions=data.Puzzles.Select(p=>new {p.Id,p.Type,p.Item1,p.Item2}),Templates=templates })));
        var definitions=data.Puzzles.Select(p=>new PuzzleDefinitionContract(p.Id,p.Type.ToString(),p.Item1,p.Item2,
            data.Zones.Where(z=>z.RequiredItems.Contains(p.Item1) || z.ProvidedItems.Contains(p.Item1) || z.GoalItems.Contains(p.Item1) ||
                p.Item2 is ushort second && (z.RequiredItems.Contains(second) || z.ProvidedItems.Contains(second) || z.GoalItems.Contains(second)))
                .Select(z=>z.Id).ToArray())).ToArray();
        return new(1,data.GameType.ToString(),fingerprint,data.Puzzles.Count,definitions,templates);
    }

    private static string Trigger(Data.Action a) => a.Conditions.Any(c=>c.Opcode is ConditionOpcode.ZoneNotInitialized or ConditionOpcode.ZoneEntered or ConditionOpcode.EnterByPlane) ? "entry" :
        a.Conditions.Any(c=>c.Opcode is ConditionOpcode.PlacedItemIs or ConditionOpcode.PlacedItemIsNot) ? "use-item" :
        a.Conditions.Any(c=>c.Opcode==ConditionOpcode.Bump) ? "bump" :
        a.Conditions.Any(c=>c.Opcode is ConditionOpcode.StandingOn or ConditionOpcode.HeroIsAt) ? "walk" : "automatic";

    private static string Guideline(string mechanic) => mechanic switch {
        "initialization-and-variants"=>"Enter through a real doorway; bind the rolled layout and selected guards before planning.",
        "item-exchange"=>"Resolve generated item parameters; approach the authored target; verify consumption and the actual reward separately.",
        "push-pull"=>"Search joint player/block states with cardinal pushes and pulls; replay floor and bump scripts after every move.",
        "floor-switches"=>"Track occupied cells and counter/tile effects; do not assume a released pressure plate remains active.",
        "combat-gated"=>"Expose live enemy prerequisites; do not invent a dead actor or a loot drop in an offline proof.",
        "scripted-relocation"=>"Replay the complete script; verify the resulting map, landing component, visibility, and onward route.",
        "room-traversal"=>"Keep directed doorway returns and entry coordinates; search the associated room family together.",
        "authored-victory"=>"Require the native WinGame result; collecting the artifact alone does not prove victory.",
        _=>"Route to the real pickup or interaction and verify its resulting state." };

    private static IEnumerable<string> ReadSlots(Condition c)
    {
        var a=c.Arguments;
        if(c.Opcode is ConditionOpcode.TileAtIs or ConditionOpcode.IsVariable && a.Count>=4) yield return $"tile:{a[1]},{a[2]},{a[3]}";
        if(c.Opcode==ConditionOpcode.StandingOn && a.Count>=3) yield return $"tile:{a[0]},{a[1]},0";
        if(c.Opcode==ConditionOpcode.Bump && a.Count>=3) yield return $"tile:{a[0]},{a[1]},1";
        if(c.Opcode is ConditionOpcode.PlacedItemIs or ConditionOpcode.PlacedItemIsNot && a.Count>=5) { yield return $"tile:{a[0]},{a[1]},{a[2]}"; yield return $"item:{a[4]}"; }
        if(c.Opcode.ToString().StartsWith("Counter",StringComparison.Ordinal)) yield return "counter";
        if(c.Opcode.ToString().StartsWith("SectorCounter",StringComparison.Ordinal)) yield return "sector-counter";
        if(c.Opcode.ToString().StartsWith("Random",StringComparison.Ordinal)) yield return "random";
        if(c.Opcode==ConditionOpcode.HasItem && a.Count>0) yield return $"item:{a[0]}";
        if(c.Opcode is ConditionOpcode.MonsterIsDead or ConditionOpcode.HasNoActiveMonsters) yield return "actors";
        if(c.Opcode==ConditionOpcode.ZoneIsSolved) yield return "solved";
    }

    private static IEnumerable<string> WriteSlots(Instruction i)
    {
        var a=i.Arguments;
        if(i.Opcode is InstructionOpcode.PlaceTile or InstructionOpcode.RemoveTile or InstructionOpcode.SetVariable or InstructionOpcode.MoveTile && a.Count>=3)
            yield return $"tile:{a[0]},{a[1]},{a[2]}";
        if(i.Opcode==InstructionOpcode.MoveTile && a.Count>=5) yield return $"tile:{a[3]},{a[4]},{a[2]}";
        if(i.Opcode is InstructionOpcode.SetCounter or InstructionOpcode.AddToCounter) yield return "counter";
        if(i.Opcode is InstructionOpcode.SetSectorCounter or InstructionOpcode.AddToSectorCounter) yield return "sector-counter";
        if(i.Opcode is InstructionOpcode.SetRandom or InstructionOpcode.RollDice) yield return "random";
        if(i.Opcode is InstructionOpcode.AddItem or InstructionOpcode.RemoveItem or InstructionOpcode.DropItem && a.Count>0) yield return $"item:{a[0]}";
        if(i.Opcode is InstructionOpcode.EnableMonster or InstructionOpcode.DisableMonster or InstructionOpcode.EnableAllMonsters or InstructionOpcode.DisableAllMonsters) yield return "actors";
        if(i.Opcode==InstructionOpcode.MarkAsSolved) yield return "solved";
        if(i.Opcode is InstructionOpcode.EnableHotspot or InstructionOpcode.DisableHotspot && a.Count>0) yield return $"hotspot:{a[0]}";
    }
}
