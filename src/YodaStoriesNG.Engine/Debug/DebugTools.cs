using YodaStoriesNG.Engine.Data;
using YodaStoriesNG.Engine.Game;

namespace YodaStoriesNG.Engine.Debug;

/// <summary>
/// Debug tools for inspecting game state, zones, and IACT scripts.
/// Press D in-game to dump current state to console.
/// </summary>
public class DebugTools
{
    private readonly GameData _gameData;
    private readonly GameState _state;
    private readonly WorldGenerator? _worldGenerator;

    public DebugTools(GameData gameData, GameState state, WorldGenerator? worldGenerator = null)
    {
        _gameData = gameData;
        _state = state;
        _worldGenerator = worldGenerator;
    }

    /// <summary>
    /// Dumps all debug information to console.
    /// </summary>
    public void DumpAll()
    {
        Console.WriteLine("\n" + new string('=', 80));
        Console.WriteLine("                              DEBUG DUMP");
        Console.WriteLine(new string('=', 80));

        DumpGameState();
        DumpCurrentZoneInfo();
        DumpZoneScripts();
        DumpInventory();
        DumpMissionProgress();

        Console.WriteLine(new string('=', 80) + "\n");
    }

    /// <summary>
    /// Dumps current game state (player position, health, etc.)
    /// </summary>
    public void DumpGameState()
    {
        Console.WriteLine("\n[GAME STATE]");
        Console.WriteLine($"  Zone ID: {_state.CurrentZoneId}");
        Console.WriteLine($"  Player Position: ({_state.PlayerX}, {_state.PlayerY})");
        Console.WriteLine($"  Player Direction: {_state.PlayerDirection}");
        Console.WriteLine($"  Health: {_state.Health}/{_state.MaxHealth}");
        Console.WriteLine($"  Games Won: {_state.GamesWon}");
        Console.WriteLine($"  Is Game Over: {_state.IsGameOver}");
        Console.WriteLine($"  Is Game Won: {_state.IsGameWon}");

        if (_state.CurrentZone != null)
        {
            Console.WriteLine($"  Zone Size: {_state.CurrentZone.Width}x{_state.CurrentZone.Height}");
            Console.WriteLine($"  Zone Planet: {_state.CurrentZone.Planet}");
            Console.WriteLine($"  Zone Type: {_state.CurrentZone.Type}");
        }
    }

    /// <summary>
    /// Dumps detailed information about the current zone.
    /// </summary>
    public void DumpCurrentZoneInfo()
    {
        var zone = _state.CurrentZone;
        if (zone == null)
        {
            Console.WriteLine("\n[ZONE INFO] No zone loaded");
            return;
        }

        Console.WriteLine($"\n[ZONE INFO] Zone {zone.Id}");
        Console.WriteLine($"  Size: {zone.Width}x{zone.Height}");
        Console.WriteLine($"  Planet: {zone.Planet}");
        Console.WriteLine($"  Type: {zone.Type}");

        // Zone objects
        Console.WriteLine($"\n  Objects ({zone.Objects.Count}):");
        foreach (var obj in zone.Objects)
        {
            string argInfo = GetObjectArgumentInfo(obj);
            Console.WriteLine($"    {obj.Type,-20} at ({obj.X,2},{obj.Y,2}) {argInfo}");
        }

        // Zone NPCs
        Console.WriteLine($"\n  NPCs ({_state.ZoneNPCs.Count} active):");
        foreach (var npc in _state.ZoneNPCs)
        {
            string charName = npc.CharacterId < _gameData.Characters.Count
                ? _gameData.Characters[npc.CharacterId].Name ?? $"Char#{npc.CharacterId}"
                : $"Char#{npc.CharacterId}";
            Console.WriteLine($"    {charName,-20} at ({npc.X,2},{npc.Y,2}) HP:{npc.Health,3} " +
                $"Hostile:{(npc.IsHostile ? "Y" : "N")} Enabled:{(npc.IsEnabled ? "Y" : "N")}");
        }

        // Special tiles at player location
        Console.WriteLine($"\n  Tiles at player ({_state.PlayerX},{_state.PlayerY}):");
        for (int layer = 0; layer < 3; layer++)
        {
            var tileId = zone.GetTile(_state.PlayerX, _state.PlayerY, layer);
            if (tileId != 0xFFFF && tileId < _gameData.Tiles.Count)
            {
                var tile = _gameData.Tiles[tileId];
                var tileName = _gameData.TileNames.TryGetValue(tileId, out var name) ? name : "";
                Console.WriteLine($"    Layer {layer}: Tile {tileId} ({tileName}) flags=0x{(int)tile.Flags:X5}");
            }
            else
            {
                Console.WriteLine($"    Layer {layer}: (empty)");
            }
        }
    }

    private string GetObjectArgumentInfo(ZoneObject obj)
    {
        return obj.Type switch
        {
            ZoneObjectType.DoorEntrance => $"-> Zone {obj.Argument}",
            ZoneObjectType.DoorExit => $"-> Zone {obj.Argument}",
            ZoneObjectType.Teleporter => $"-> Zone {obj.Argument}",
            ZoneObjectType.PuzzleNPC => GetCharacterName(obj.Argument),
            ZoneObjectType.CrateItem => GetItemName(obj.Argument),
            ZoneObjectType.CrateWeapon => GetItemName(obj.Argument),
            ZoneObjectType.LocatorItem => GetItemName(obj.Argument),
            ZoneObjectType.Lock => GetItemName(obj.Argument),
            _ => obj.Argument > 0 ? $"Arg={obj.Argument}" : ""
        };
    }

    private string GetCharacterName(int charId)
    {
        if (charId < 0 || charId >= _gameData.Characters.Count)
            return $"Char#{charId}";
        var name = _gameData.Characters[charId].Name;
        return !string.IsNullOrEmpty(name) ? $"[{name}]" : $"Char#{charId}";
    }

    private string GetItemName(int tileId)
    {
        if (tileId <= 0 || tileId >= _gameData.Tiles.Count)
            return "";
        if (_gameData.TileNames.TryGetValue(tileId, out var name))
            return $"[{name}]";
        return $"Item#{tileId}";
    }

    /// <summary>
    /// Dumps all IACT scripts for the current zone.
    /// </summary>
    public void DumpZoneScripts()
    {
        var zone = _state.CurrentZone;
        if (zone == null)
        {
            Console.WriteLine("\n[IACT SCRIPTS] No zone loaded");
            return;
        }

        if (zone.Actions.Count == 0)
        {
            Console.WriteLine($"\n[IACT SCRIPTS] Zone {zone.Id} has no scripts");
            return;
        }

        Console.WriteLine($"\n[IACT SCRIPTS] Zone {zone.Id} has {zone.Actions.Count} action(s)");

        for (int actionIdx = 0; actionIdx < zone.Actions.Count; actionIdx++)
        {
            var action = zone.Actions[actionIdx];
            Console.WriteLine($"\n  Action #{actionIdx}:");

            // Conditions
            Console.WriteLine($"    Conditions ({action.Conditions.Count}):");
            foreach (var cond in action.Conditions)
            {
                string condText = FormatCondition(cond);
                Console.WriteLine($"      {condText}");
            }

            // Instructions
            Console.WriteLine($"    Instructions ({action.Instructions.Count}):");
            foreach (var instr in action.Instructions)
            {
                string instrText = FormatInstruction(instr);
                Console.WriteLine($"      {instrText}");
            }
        }
    }

    private string FormatCondition(Condition cond)
    {
        var args = string.Join(", ", cond.Arguments);
        var text = !string.IsNullOrEmpty(cond.Text) ? $" \"{cond.Text}\"" : "";
        return $"{cond.Opcode}({args}){text}";
    }

    private string FormatInstruction(Instruction instr)
    {
        var args = string.Join(", ", instr.Arguments);
        var text = !string.IsNullOrEmpty(instr.Text) ? $" \"{TruncateText(instr.Text, 40)}\"" : "";
        return $"{instr.Opcode}({args}){text}";
    }

    private string TruncateText(string text, int maxLen)
    {
        if (text.Length <= maxLen) return text;
        return text.Substring(0, maxLen - 3) + "...";
    }

    /// <summary>
    /// Dumps player inventory.
    /// </summary>
    public void DumpInventory()
    {
        Console.WriteLine("\n[INVENTORY]");

        // Items
        Console.WriteLine($"  Items ({_state.Inventory.Count}):");
        foreach (var itemId in _state.Inventory)
        {
            var itemName = GetItemName(itemId);
            var selected = _state.SelectedItem == itemId ? " [SELECTED]" : "";
            Console.WriteLine($"    Tile {itemId}: {itemName}{selected}");
        }

        // Weapons
        Console.WriteLine($"\n  Weapons ({_state.Weapons.Count}):");
        for (int i = 0; i < _state.Weapons.Count; i++)
        {
            var weaponId = _state.Weapons[i];
            var weaponName = GetItemName(weaponId);
            var selected = i == _state.CurrentWeaponIndex ? " [EQUIPPED]" : "";
            Console.WriteLine($"    Tile {weaponId}: {weaponName}{selected}");
        }
    }

    /// <summary>
    /// Dumps mission progress.
    /// </summary>
    public void DumpMissionProgress()
    {
        var world = _worldGenerator?.CurrentWorld;
        var mission = world?.Mission;

        Console.WriteLine("\n[MISSION PROGRESS]");

        if (mission == null)
        {
            Console.WriteLine("  No mission active");
            return;
        }

        Console.WriteLine($"  Mission: {mission.Name}");
        Console.WriteLine($"  Planet: {mission.Planet}");
        Console.WriteLine($"  Description: {mission.Description}");
        Console.WriteLine($"  Completed: {mission.IsCompleted}");
        Console.WriteLine($"  Current Step: {mission.CurrentStep + 1}/{mission.PuzzleChain.Count}");

        if (mission.PuzzleChain.Count > 0)
        {
            Console.WriteLine("\n  Puzzle Chain:");
            for (int i = 0; i < mission.PuzzleChain.Count; i++)
            {
                var step = mission.PuzzleChain[i];
                var status = step.IsCompleted ? "[X]" : (i == mission.CurrentStep ? "[>]" : "[ ]");
                var reqItem = GetItemName(step.RequiredItemId);
                var rewItem = GetItemName(step.RewardItemId);
                Console.WriteLine($"    {status} Step {i + 1}: Need {reqItem} -> Get {rewItem}");
                if (!string.IsNullOrEmpty(step.Hint))
                    Console.WriteLine($"        Hint: {step.Hint}");
            }
        }
    }

    /// <summary>
    /// Dumps all scripts for a specific zone by ID.
    /// </summary>
    public void DumpZoneScriptsById(int zoneId)
    {
        if (zoneId < 0 || zoneId >= _gameData.Zones.Count)
        {
            Console.WriteLine($"Invalid zone ID: {zoneId}");
            return;
        }

        var zone = _gameData.Zones[zoneId];
        Console.WriteLine($"\n[IACT SCRIPTS] Zone {zoneId} ({zone.Planet}, {zone.Type})");

        if (zone.Actions.Count == 0)
        {
            Console.WriteLine("  No scripts");
            return;
        }

        for (int actionIdx = 0; actionIdx < zone.Actions.Count; actionIdx++)
        {
            var action = zone.Actions[actionIdx];
            Console.WriteLine($"\n  Action #{actionIdx}:");

            Console.WriteLine("    IF:");
            foreach (var cond in action.Conditions)
            {
                Console.WriteLine($"      {FormatCondition(cond)}");
            }

            Console.WriteLine("    THEN:");
            foreach (var instr in action.Instructions)
            {
                Console.WriteLine($"      {FormatInstruction(instr)}");
            }
        }
    }

    /// <summary>
    /// Searches for zones with specific script opcodes.
    /// </summary>
    public void FindZonesWithOpcode(string opcodeSearch)
    {
        Console.WriteLine($"\n[SEARCH] Looking for scripts with '{opcodeSearch}'...\n");

        var searchLower = opcodeSearch.ToLower();
        int found = 0;

        foreach (var zone in _gameData.Zones.Where(z => z.Width > 0))
        {
            foreach (var action in zone.Actions)
            {
                bool matches = action.Conditions.Any(c => c.Opcode.ToString().ToLower().Contains(searchLower)) ||
                               action.Instructions.Any(i => i.Opcode.ToString().ToLower().Contains(searchLower));

                if (matches)
                {
                    Console.WriteLine($"Zone {zone.Id} ({zone.Planet}, {zone.Type}):");
                    foreach (var cond in action.Conditions.Where(c => c.Opcode.ToString().ToLower().Contains(searchLower)))
                    {
                        Console.WriteLine($"  Condition: {FormatCondition(cond)}");
                    }
                    foreach (var instr in action.Instructions.Where(i => i.Opcode.ToString().ToLower().Contains(searchLower)))
                    {
                        Console.WriteLine($"  Instruction: {FormatInstruction(instr)}");
                    }
                    found++;
                }
            }
        }

        Console.WriteLine($"\nFound {found} matching script(s)");
    }

    /// <summary>
    /// Dumps counters and variables.
    /// </summary>
    public void DumpVariables()
    {
        Console.WriteLine("\n[VARIABLES & COUNTERS]");

        Console.WriteLine("  Counters:");
        var counters = _state.Counters.Where(kv => kv.Value != 0).OrderBy(kv => kv.Key);
        foreach (var (key, value) in counters)
        {
            Console.WriteLine($"    Counter[{key}] = {value}");
        }
        if (!counters.Any())
            Console.WriteLine("    (none)");

        Console.WriteLine("\n  Variables:");
        var variables = _state.Variables.Where(kv => kv.Value != 0).OrderBy(kv => kv.Key);
        foreach (var (key, value) in variables)
        {
            Console.WriteLine($"    Var[{key}] = {value}");
        }
        if (!variables.Any())
            Console.WriteLine("    (none)");

        Console.WriteLine("\n  Solved Zones:");
        var solved = _state.SolvedZones.OrderBy(z => z);
        Console.WriteLine($"    {string.Join(", ", solved)}");
        if (!solved.Any())
            Console.WriteLine("    (none)");
    }
}
