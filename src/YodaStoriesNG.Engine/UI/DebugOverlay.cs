using YodaStoriesNG.Engine.Data;
using YodaStoriesNG.Engine.Game;

namespace YodaStoriesNG.Engine.UI;

/// <summary>
/// In-game debug overlay that renders on screen.
/// Press F1 to toggle, arrow keys to navigate, Enter to select.
/// </summary>
public class DebugOverlay
{
    private readonly GameData _gameData;
    private readonly GameState _state;
    private readonly WorldGenerator? _worldGenerator;

    public bool IsVisible { get; set; } = false;
    public int CurrentTab { get; private set; } = 0;
    public int ScrollOffset { get; private set; } = 0;

    private readonly string[] _tabs = { "State", "Zone", "Scripts", "Inventory", "Map" };

    public DebugOverlay(GameData gameData, GameState state, WorldGenerator? worldGenerator = null)
    {
        _gameData = gameData;
        _state = state;
        _worldGenerator = worldGenerator;
    }

    public void Toggle() => IsVisible = !IsVisible;

    public void NextTab()
    {
        CurrentTab = (CurrentTab + 1) % _tabs.Length;
        ScrollOffset = 0;
    }

    public void PrevTab()
    {
        CurrentTab = (CurrentTab - 1 + _tabs.Length) % _tabs.Length;
        ScrollOffset = 0;
    }

    public void ScrollUp() => ScrollOffset = Math.Max(0, ScrollOffset - 1);
    public void ScrollDown() => ScrollOffset++;

    public string[] GetTabs() => _tabs;

    /// <summary>
    /// Gets the content lines for a specific tab.
    /// </summary>
    public List<string> GetTabContent(int tab)
    {
        return tab switch
        {
            0 => GetStateContent(),
            1 => GetZoneContent(),
            2 => GetScriptsContent(),
            3 => GetInventoryContent(),
            4 => GetMapContent(),
            _ => new List<string> { "Unknown tab" }
        };
    }

    private List<string> GetStateContent()
    {
        var lines = new List<string>
        {
            "=== GAME STATE ===",
            "",
            $"Zone ID: {_state.CurrentZoneId}",
            $"Position: ({_state.PlayerX}, {_state.PlayerY})",
            $"Direction: {_state.PlayerDirection}",
            $"Health: {_state.Health} / {_state.MaxHealth}",
            "",
            $"Games Won: {_state.GamesWon}",
            $"Game Over: {_state.IsGameOver}",
            $"Game Won: {_state.IsGameWon}",
            "",
            "=== MISSION ==="
        };

        var mission = _worldGenerator?.CurrentWorld?.Mission;
        if (mission != null)
        {
            lines.Add($"Mission {mission.MissionNumber}/15: {mission.Name}");
            lines.Add($"Planet: {mission.Planet}");
            lines.Add($"Step: {mission.CurrentStep + 1}/{mission.PuzzleChain.Count}");
            lines.Add($"Completed: {mission.IsCompleted}");
        }
        else
        {
            lines.Add("No mission active");
        }

        return lines;
    }

    private List<string> GetZoneContent()
    {
        var lines = new List<string>();
        var zone = _state.CurrentZone;

        if (zone == null)
        {
            lines.Add("No zone loaded");
            return lines;
        }

        lines.Add($"=== ZONE {zone.Id} ===");
        lines.Add($"Size: {zone.Width}x{zone.Height}");
        lines.Add($"Planet: {zone.Planet}");
        lines.Add($"Type: {zone.Type}");
        lines.Add("");
        lines.Add($"=== OBJECTS ({zone.Objects.Count}) ===");

        foreach (var obj in zone.Objects)
        {
            lines.Add($"  {obj.Type} at ({obj.X},{obj.Y}) arg={obj.Argument}");
        }

        lines.Add("");
        lines.Add($"=== NPCs ({_state.ZoneNPCs.Count}) ===");

        foreach (var npc in _state.ZoneNPCs)
        {
            string name = npc.CharacterId < _gameData.Characters.Count
                ? _gameData.Characters[npc.CharacterId].Name ?? $"#{npc.CharacterId}"
                : $"#{npc.CharacterId}";
            lines.Add($"  {name} at ({npc.X},{npc.Y}) HP:{npc.Health}");
        }

        return lines;
    }

    private List<string> GetScriptsContent()
    {
        var lines = new List<string>();
        var zone = _state.CurrentZone;

        if (zone == null)
        {
            lines.Add("No zone loaded");
            return lines;
        }

        lines.Add($"=== IACT SCRIPTS ({zone.Actions.Count}) ===");
        lines.Add("");

        for (int i = 0; i < zone.Actions.Count; i++)
        {
            var action = zone.Actions[i];
            lines.Add($"--- Action #{i} ---");
            lines.Add("IF:");

            foreach (var cond in action.Conditions)
            {
                var args = string.Join(",", cond.Arguments);
                lines.Add($"  {cond.Opcode}({args})");
            }

            lines.Add("THEN:");

            foreach (var instr in action.Instructions)
            {
                var args = string.Join(",", instr.Arguments);
                var text = !string.IsNullOrEmpty(instr.Text) ? $" \"{Truncate(instr.Text, 30)}\"" : "";
                lines.Add($"  {instr.Opcode}({args}){text}");
            }

            lines.Add("");
        }

        return lines;
    }

    private List<string> GetInventoryContent()
    {
        var lines = new List<string>
        {
            "=== INVENTORY ===",
            ""
        };

        lines.Add($"Items ({_state.Inventory.Count}):");
        for (int i = 0; i < _state.Inventory.Count; i++)
        {
            var itemId = _state.Inventory[i];
            var selected = _state.SelectedItem == itemId ? " [SELECTED]" : "";
            var name = _gameData.TileNames.TryGetValue(itemId, out var n) ? n : $"Item #{itemId}";
            lines.Add($"  {i + 1}. {name}{selected}");
        }

        lines.Add("");
        lines.Add($"Weapons ({_state.Weapons.Count}):");
        for (int i = 0; i < _state.Weapons.Count; i++)
        {
            var weaponId = _state.Weapons[i];
            var equipped = i == _state.CurrentWeaponIndex ? " [EQUIPPED]" : "";
            var name = _gameData.TileNames.TryGetValue(weaponId, out var n) ? n : $"Weapon #{weaponId}";
            lines.Add($"  {name}{equipped}");
        }

        return lines;
    }

    private List<string> GetMapContent()
    {
        var lines = new List<string>();
        var world = _worldGenerator?.CurrentWorld;

        if (world?.Grid == null)
        {
            lines.Add("No world generated");
            return lines;
        }

        lines.Add("=== WORLD MAP (10x10) ===");
        lines.Add("L=Landing  G=Goal  F=Force  .=Empty");
        lines.Add("");

        // Header
        lines.Add("     0    1    2    3    4    5    6    7    8    9");

        for (int y = 0; y < 10; y++)
        {
            var row = $"  {y} ";
            for (int x = 0; x < 10; x++)
            {
                var zoneId = world.Grid[y, x];
                bool isLanding = (x == world.LandingPosition.x && y == world.LandingPosition.y);
                bool isGoal = (x == world.ObjectivePosition.x && y == world.ObjectivePosition.y);
                bool isForce = world.TheForceZoneId.HasValue &&
                               (x == world.TheForcePosition.x && y == world.TheForcePosition.y);

                if (isLanding) row += "  L  ";
                else if (isGoal) row += "  G  ";
                else if (isForce) row += "  F  ";
                else if (zoneId.HasValue) row += $"{zoneId.Value,4} ";
                else row += "  .  ";
            }
            lines.Add(row);
        }

        lines.Add("");
        lines.Add($"Landing: Zone {world.LandingZoneId} at ({world.LandingPosition.x},{world.LandingPosition.y})");
        lines.Add($"Goal: Zone {world.ObjectiveZoneId} at ({world.ObjectivePosition.x},{world.ObjectivePosition.y})");
        if (world.TheForceZoneId.HasValue)
            lines.Add($"Force: Zone {world.TheForceZoneId} at ({world.TheForcePosition.x},{world.TheForcePosition.y})");

        return lines;
    }

    private string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max - 3) + "...";
}
