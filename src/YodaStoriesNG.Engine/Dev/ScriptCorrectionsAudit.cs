using YodaStoriesNG.Engine.Data;
using ScriptAction = YodaStoriesNG.Engine.Data.Action;

namespace YodaStoriesNG.Engine.Game;

public unsafe partial class GameEngine
{
    private static int AuditScriptCorrections(GameData data)
    {
        int checks = 0;
        void Require(bool ok, string message)
        {
            if (!ok) throw new InvalidOperationException(message);
            checks++;
        }
        static Instruction Op(InstructionOpcode opcode, params short[] args) =>
            new() { Opcode = opcode, Arguments = args.ToList() };
        static Zone Copy(Zone source) => new()
        {
            Id = source.Id, Width = source.Width, Height = source.Height,
            TileGrid = (ushort[,,])source.TileGrid.Clone(),
            Objects = source.Objects.Select(o => new ZoneObject
                { Type = o.Type, X = o.X, Y = o.Y, Argument = o.Argument, Enabled = o.Enabled }).ToList()
        };
        static (GameState State, ActionExecutor Executor) Scene(GameData data, Zone zone, params Instruction[] ops)
        {
            zone.Actions = new() { new ScriptAction { Instructions = ops.ToList() } };
            var state = new GameState { CurrentZone = zone, CurrentZoneId = zone.Id };
            return (state, new ActionExecutor(data, state));
        }
        bool Holds(Condition condition, GameState state, WorldMap? world = null)
        {
            state.SetCounter(state.CurrentZoneId, 0);
            state.CurrentZone!.Actions = new()
            {
                new ScriptAction { Conditions = new() { condition },
                    Instructions = new() { Op(InstructionOpcode.SetCounter, 1234) } }
            };
            var executor = new ActionExecutor(data, state) { CurrentWorld = world };
            executor.ExecuteZoneActions(ActionTrigger.Walk);
            return state.GetCounter(state.CurrentZoneId) == 1234;
        }

        // Exercise every native DrawTile tuple, including its unused garbage slots.
        foreach (var original in data.Zones)
        foreach (var draw in original.Actions.SelectMany(a => a.Instructions).Where(i => i.Opcode == InstructionOpcode.DrawTile))
        {
            var zone = Copy(original);
            var (state, executor) = Scene(data, zone, draw, Op(InstructionOpcode.Wait), Op(InstructionOpcode.Redraw));
            executor.ExecuteZoneActions(ActionTrigger.Walk);
            int x = draw.Arguments[0], y = draw.Arguments[1], tile = draw.Arguments[2];
            Require(executor.DrawnTiles.GetValueOrDefault((x, y), -1) == tile, $"zone {zone.Id}: native DrawTile frame missing");
            Require(Enumerable.Range(0, 3).All(l => zone.GetTile(x, y, l) == original.GetTile(x, y, l)),
                $"zone {zone.Id}: temporary drawing mutated collision data");
            Require(executor.IsBusy, "temporary frame did not survive Wait");
            executor.ExecuteZoneActions(ActionTrigger.Walk);
            Require(executor.DrawnTiles.Count == 0 && !executor.IsBusy, "Redraw did not clear temporary frames");
        }
        var baseZone = Copy(data.Zones[95]);
        var (_, paint) = Scene(data, baseZone,
            Op(InstructionOpcode.DrawTile, 1, 1, 1), Op(InstructionOpcode.DrawTile, 2, 2, 2),
            Op(InstructionOpcode.DrawTile, 3, 3, 3), Op(InstructionOpcode.Wait),
            Op(InstructionOpcode.SetTileNeedsDisplay, 1, 1), Op(InstructionOpcode.SetRectNeedsDisplay, 2, 2, 1, 1),
            Op(InstructionOpcode.Wait));
        paint.ExecuteZoneActions(ActionTrigger.Walk); paint.ExecuteZoneActions(ActionTrigger.Walk);
        Require(paint.DrawnTiles.Count == 1 && paint.DrawnTiles.ContainsKey((3, 3)), "partial redraw cleared the wrong frames");
        paint.ResetForZone(); Require(paint.DrawnTiles.Count == 0, "temporary art survived zone replacement");

        foreach (var original in data.Zones)
        foreach (var c in original.Actions.SelectMany(a => a.Conditions))
        {
            if (c.Opcode == ConditionOpcode.DropsQuestItemAt)
            {
                var zone = Copy(original);
                var (state, _) = Scene(data, zone);
                foreach (var h in zone.Objects) h.Enabled = false;
                Require(!Holds(c, state), $"zone {zone.Id}: disabled reward marker matched");
                var marker = zone.Objects.FirstOrDefault(h => (int)h.Type == 0 && h.X == c.Arguments[0] && h.Y == c.Arguments[1]);
                if (marker == null)
                {
                    // Zone 160 contains a stale (16,15) test beside the actual
                    // (16,16) marker and a later correct action. Keep it false;
                    // inject a matching marker only to check this opcode's positive path.
                    Require(!Holds(c, state), "missing authored marker matched");
                    marker = new ZoneObject { Type = (ZoneObjectType)0, X = c.Arguments[0], Y = c.Arguments[1] };
                    zone.Objects.Add(marker);
                }
                marker.Enabled = true;
                Require(Holds(c, state), $"zone {zone.Id}: layout condition required a nonexistent drop event");
            }
            if (c.Opcode == ConditionOpcode.HasNoActiveMonsters)
            {
                var (state, _) = Scene(data, Copy(original));
                state.ZoneNPCs.Add(new NPC { IsEnabled = false, Health = 10, IsHostile = true });
                state.ZoneNPCs.Add(new NPC { IsEnabled = true, Health = 0, IsHostile = true });
                Require(Holds(c, state), "disabled/dead monsters blocked completion");
                state.ZoneNPCs.Add(new NPC { IsEnabled = true, Health = 10, IsHostile = false });
                Require(!Holds(c, state), "active nonhostile native monster was ignored");
            }
            if (c.Opcode == ConditionOpcode.ZoneIsSolved)
            {
                var (state, _) = Scene(data, Copy(original));
                var sector = new Sector { Zone = original.Id };
                var world = new WorldMap(); world.Sectors[original.Id] = sector;
                Require(!Holds(c, state, world), "unsolved zone matched solved condition");
                sector.Solved = true;
                Require(Holds(c, state, world), "generated solved state was invisible to scripts");
                sector.Solved = false; state.MarkZoneSolved(original.Id);
                Require(Holds(c, state, world), "saved solved state was invisible to scripts");
            }
        }

        // MarkAsSolved and generated pickup progress must agree for every room of a sector.
        var root = Copy(data.Zones[149]);
        var (progress, mark) = Scene(data, root, Op(InstructionOpcode.MarkAsSolved));
        var shared = new Sector { Zone = root.Id, FindItem = 430 };
        var map = new WorldMap(); map.Sectors[root.Id] = shared; map.Sectors[9999] = shared;
        mark.CurrentWorld = map; mark.ExecuteZoneActions(ActionTrigger.Walk);
        Require(shared.Solved && progress.IsZoneSolved(root.Id) && progress.IsZoneSolved(9999), "script solve did not propagate to rooms");
        shared.Solved = false; progress.SolvedZones.Clear();
        var puzzles = new SectorPuzzles(data, progress, map); puzzles.OnPickedUp(430);
        Require(shared.Solved && progress.IsZoneSolved(root.Id) && progress.IsZoneSolved(9999), "pickup solve did not propagate to scripts/saves");
        shared.Solved = false; puzzles.SyncMission();
        Require(shared.Solved, "saved solved state was not restored to its sector");

        var invalidAdd = data.Zones[622].Actions.SelectMany(a => a.Instructions)
            .Single(i => i.Opcode == InstructionOpcode.AddItem && i.Arguments[0] == -1);
        var (bag, inventoryScript) = Scene(data, Copy(data.Zones[622]), invalidAdd, Op(InstructionOpcode.AddItem, 430));
        int added = 0; inventoryScript.OnItemAdded += _ => added++;
        inventoryScript.ExecuteZoneActions(ActionTrigger.Walk);
        Require(bag.Inventory.SequenceEqual(new[] { 430 }) && added == 1, "negative AddItem created an invalid item or event");
        var saved = new SaveGameData { Inventory = new() { -1, 430 }, SelectedItem = -1 };
        SaveGameManager.ApplyToGameState(saved, bag);
        Require(bag.Inventory.SequenceEqual(new[] { 430 }) && bag.SelectedItem == null, "legacy invalid inventory item was not discarded");

        foreach (var original in data.Zones)
        foreach (var remove in original.Actions.SelectMany(a => a.Instructions).Where(i =>
            i.Opcode == InstructionOpcode.RemoveItem && i.Arguments[0] >= 0 &&
            i.Arguments[0] < data.Tiles.Count && data.Tiles[i.Arguments[0]].IsWeapon))
        {
            int weapon = remove.Arguments[0];
            var (state, executor) = Scene(data, Copy(original), remove);
            state.Weapons.Add(weapon); state.SelectedWeapon = weapon;
            state.InitializeWeaponAmmo(weapon, 10, 10);
            executor.ExecuteZoneActions(ActionTrigger.Walk);
            Require(!state.HasItem(weapon) && state.SelectedWeapon == null && !state.WeaponAmmo.ContainsKey(weapon),
                "script failed to remove the equipped copy");
            state.Weapons.Add(weapon); state.SelectedWeapon = weapon;
            state.Inventory.Add(weapon); state.Inventory.Add(weapon);
            executor.ExecuteZoneActions(ActionTrigger.Walk);
            Require(state.SelectedWeapon == weapon && state.Inventory.Count(i => i == weapon) == 1,
                "removing a spare discarded the equipped weapon or all copies");
            state.SelectedWeapon = null;
            executor.ExecuteZoneActions(ActionTrigger.Walk);
            Require(!state.Weapons.Contains(weapon), "last unequipped copy left phantom weapon ownership");
        }
        var (_, sound) = Scene(data, Copy(root), Op(InstructionOpcode.StopSound));
        int stopped = 0; sound.OnStopSound += () => stopped++;
        sound.ExecuteZoneActions(ActionTrigger.Walk);
        Require(stopped == 1, "StopSound was not dispatched");
        new Audio.SoundManager("").StopAllSounds(); // Safe before an audio device is opened.
        var (unknown, _) = Scene(data, Copy(root));
        Require(!Holds(new Condition { Opcode = (ConditionOpcode)0xFFFF }, unknown), "unknown condition authorized an action");
        Require(!Holds(new Condition { Opcode = ConditionOpcode.MonsterIsDead, Arguments = new() { 999 } }, unknown),
            "nonexistent monster was counted as defeated");
        Console.WriteLine($"Script corrections: {checks} regression checks passed.");
        return checks;
    }
}
