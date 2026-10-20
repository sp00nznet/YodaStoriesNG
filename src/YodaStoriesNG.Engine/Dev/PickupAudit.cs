using YodaStoriesNG.Engine.Data;
using YodaStoriesNG.Engine.Parsing;

namespace YodaStoriesNG.Engine.Game;

public unsafe partial class GameEngine
{
    /// <summary>Inspect original scripted pickups and replay representative encounters without SDL.</summary>
    public static int AuditPickups(string path)
    {
        var data = new DtaParser().Parse(path);
        var targets = new HashSet<(int Zone, int X, int Y, int Tile)>();
        var handoffs = new HashSet<(int Zone, int X, int Y, int Tile)>();
        foreach (var zone in data.Zones)
        foreach (var action in zone.Actions)
        foreach (var condition in action.Conditions.Where(c => c.Opcode == ConditionOpcode.Bump && c.Arguments.Count >= 3))
        {
            int x = condition.Arguments[0], y = condition.Arguments[1], tile = condition.Arguments[2];
            if (tile < 0 || tile >= data.Tiles.Count) continue;
            var sprite = data.Tiles[tile];
            if (sprite.IsObject || !(sprite.IsItem || sprite.IsWeapon || sprite.IsCharacter)) continue;
            var key = (zone.Id, x, y, tile); targets.Add(key);
            if (action.Instructions.Any(i => i.Opcode == InstructionOpcode.RemoveTile && i.Arguments.Count >= 3 &&
                    i.Arguments[0] == x && i.Arguments[1] == y && i.Arguments[2] == 1) &&
                action.Instructions.Any(i => i.Opcode == InstructionOpcode.DropItem && i.Arguments.Count >= 3 &&
                    i.Arguments[0] == tile && i.Arguments[1] == x && i.Arguments[2] == y)) handoffs.Add(key);
        }
        Console.WriteLine($"Pickup audit: {data.Zones.Count} zones, {data.Tiles.Count} tiles; {targets.Count} scripted positions, " +
            $"{targets.Select(t => t.Tile).Distinct().Count()} tile types; {handoffs.Count} explicit pickup handoff positions.");

        static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        GameEngine Scene(int zoneId, int x, int y, ushort tile, bool smooth)
        {
            var original = data.Zones[zoneId];
            var zone = new Zone { Id = zoneId, Width = original.Width, Height = original.Height,
                Type = original.Type, TileGrid = (ushort[,,])original.TileGrid.Clone(),
                // Isolate the authored encounter; unrelated flight/mission scripts are audited separately.
                Actions = original.Actions.Where(a => a.Conditions.Any(c =>
                    c.Opcode is ConditionOpcode.Bump or ConditionOpcode.PlacedItemIs or ConditionOpcode.PlacedItemIsNot &&
                    c.Arguments.Count >= 2 && c.Arguments[0] == x && c.Arguments[1] == y)).ToList() };
            zone.SetTile(x, y, 1, tile);
            var e = new GameEngine("") { _gameData = data, _smoothMovement = smooth,
                _smoothMovementRequested = smooth, _showingTitleScreen = false };
            e._state.CurrentZone = zone; e._state.CurrentZoneId = zoneId;
            e._state.PlayerX = x; e._state.PlayerY = y + 1; e._state.PlayerDirection = Direction.Up;
            e._actionExecutor = new ActionExecutor(data, e._state);
            e._actionExecutor.IsDialogueActive = () => e._messages.HasDialogue;
            e._actionExecutor.OnDialogue += (speaker, text) => e._messages.ShowDialogue(speaker, text);
            e._actionExecutor.OnItemAdded += e.RegisterScriptedItem;
            return e;
        }
        static void Finish(GameEngine e)
        {
            for (int frame = 0; frame < 512; frame++)
            {
                e._messages.ClearDialogue();
                if (e._actionExecutor?.IsBusy != true) return;
                e._actionExecutor.ExecuteZoneActions(ActionTrigger.Walk);
            }
            throw new InvalidOperationException("scripted pickup did not finish");
        }
        int checks = 0;
        foreach (bool smooth in new[] { false, true })
        {
            foreach (var (zone, x, y, tile) in new[] {
                (93,9,10,794), (316,12,3,1212), (401,4,1,1353),
                (307,1,7,442), (263,4,3,1197), (149,2,10,513) })
            {
                var e = Scene(zone, x, y, (ushort)tile, smooth);
                int oldX = e._state.PlayerX, oldY = e._state.PlayerY;
                e.TryMovePlayer(0,-1,Direction.Up);
                Require(e._state.PlayerX == oldX && e._state.PlayerY == oldY, $"walked into scripted target {zone}:{tile}");
                if (e._actionExecutor!.IsBusy)
                    Require(!e._state.Inventory.Contains(tile), $"collected before the script finished {zone}:{tile}");
                Finish(e);
                Require(e._state.Inventory.Count(i => i == tile) == 1, $"missing/duplicate scripted award {zone}:{tile}");
                Require(e._state.CurrentZone!.GetTile(x,y,1) != tile, $"award recreated its bump target {zone}:{tile}");
                if (tile == 794) Require(e._state.HasLocator, "scripted R2 pickup did not enable hints");
                if (tile == 513) Require(e._state.Weapons.Contains(tile) && e._state.SelectedWeapon == null,
                    "scripted weapon pickup did not register ownership or auto-equipped");
                Finish(e);
                Require(e._state.Inventory.Count(i => i == tile) == 1, "idle updates duplicated an award");
                checks++;
            }
            var pilot = Scene(391,8,6,1214,smooth);
            pilot.TryMovePlayer(0,-1,Direction.Up); Finish(pilot);
            Require(!pilot._state.Inventory.Contains(1214) && pilot._state.CurrentZone!.GetTile(8,6,1) == 1214,
                "stranded pilot became a pickup before rescue");
            checks++;

            var flavor = Scene(207,5,1,418,smooth);
            flavor.TryMovePlayer(0,-1,Direction.Up); Finish(flavor);
            Require(!flavor.IsScriptedActorTile(flavor._state.CurrentZone!,5,1), "finished flavor text permanently locked an item");
            flavor.TryMovePlayer(0,-1,Direction.Up);
            if (smooth) flavor.UpdateSmoothMovement(1.0/6);
            Require(flavor._state.Inventory.Count(i=>i==418)==1, "item was not collectible after its flavor text");
            checks++;

            // An unmet flavor condition must not block other items sharing that condition.
            flavor = Scene(207,7,2,425,smooth);
            flavor._state.CurrentZone!.SetTile(0,0,0,1);
            Require(!flavor.IsScriptedActorTile(flavor._state.CurrentZone,7,2), "inactive flavor condition blocked collection");
            checks++;
        }
        Console.WriteLine($"Pickup audit PASSED: {checks} encounter checks across classic and smooth movement. No game window opened.");
        return 0;
    }
}
