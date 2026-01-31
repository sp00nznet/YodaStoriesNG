using Hexa.NET.SDL2;
using YodaStoriesNG.Engine.Data;
using YodaStoriesNG.Engine.Game;
using YodaStoriesNG.Engine.Rendering;

namespace YodaStoriesNG.Engine.UI;

/// <summary>
/// Represents a position to highlight in the game world when viewing scripts.
/// </summary>
public struct ScriptHighlight
{
    public int X { get; set; }
    public int Y { get; set; }
    public HighlightType Type { get; set; }
    public string Label { get; set; }
}

public enum HighlightType
{
    Position,       // Generic position reference (cyan)
    Tile,           // Tile placement/check (yellow)
    Door,           // Door/teleporter (green)
    NPC,            // NPC/character (magenta)
    Item,           // Item location (orange)
    Trigger         // Trigger/hotspot (blue)
}

/// <summary>
/// A debug window that displays IACT scripts for all zones.
/// Allows browsing through zones and viewing their action scripts.
/// </summary>
public class ScriptViewerWindow : IDisposable
{
    private readonly GameState _state;
    private readonly GameData _gameData;

    private unsafe SDLWindow* _window;
    private unsafe SDLRenderer* _renderer;
    private BitmapFont? _font;

    private const int WindowWidth = 550;
    private const int WindowHeight = 650;

    private bool _isOpen = false;
    private uint _windowId;
    private int _scrollOffset = 0;
    private int _currentZoneIndex = 0;
    private List<int> _zonesWithScripts = new();
    private List<string> _scriptLines = new();

    // Highlights for the game renderer
    private List<ScriptHighlight> _highlights = new();

    public bool IsOpen => _isOpen;

    /// <summary>
    /// Returns the zone ID currently being viewed, or -1 if not viewing.
    /// </summary>
    public int ViewingZoneId => _isOpen && _zonesWithScripts.Count > 0
        ? _zonesWithScripts[_currentZoneIndex]
        : -1;

    /// <summary>
    /// Gets the list of highlights to render in the game world.
    /// Only returns highlights if viewing the current zone.
    /// </summary>
    public IReadOnlyList<ScriptHighlight> GetHighlightsForZone(int zoneId)
    {
        if (_isOpen && ViewingZoneId == zoneId)
            return _highlights;
        return Array.Empty<ScriptHighlight>();
    }

    public ScriptViewerWindow(GameState state, GameData gameData)
    {
        _state = state;
        _gameData = gameData;
    }

    public unsafe void Open()
    {
        if (_isOpen) return;

        _window = SDL.CreateWindow(
            "IACT Script Viewer",
            620, 50,
            WindowWidth, WindowHeight,
            (uint)(SDLWindowFlags.Shown | SDLWindowFlags.Resizable));

        if (_window == null)
        {
            Console.WriteLine($"Failed to create script viewer window: {SDL.GetErrorS()}");
            return;
        }

        _renderer = SDL.CreateRenderer(_window, -1,
            (uint)(SDLRendererFlags.Accelerated | SDLRendererFlags.Presentvsync));

        if (_renderer == null)
        {
            SDL.DestroyWindow(_window);
            _window = null;
            Console.WriteLine($"Failed to create script viewer renderer: {SDL.GetErrorS()}");
            return;
        }

        _font = new BitmapFont();
        _font.Initialize(_renderer);

        _windowId = SDL.GetWindowID(_window);
        _isOpen = true;

        // Find all zones with scripts
        FindZonesWithScripts();

        // Start at current zone if it has scripts
        JumpToCurrentZone();

        RefreshScripts();
        Console.WriteLine($"[ScriptViewer] Window opened - {_zonesWithScripts.Count} zones have IACT scripts");
    }

    private void FindZonesWithScripts()
    {
        _zonesWithScripts.Clear();
        for (int i = 0; i < _gameData.Zones.Count; i++)
        {
            var zone = _gameData.Zones[i];
            if (zone.Actions.Count > 0)
            {
                _zonesWithScripts.Add(i);
            }
        }
    }

    private void JumpToCurrentZone()
    {
        int currentZoneId = _state.CurrentZoneId;
        int index = _zonesWithScripts.IndexOf(currentZoneId);
        if (index >= 0)
        {
            _currentZoneIndex = index;
        }
    }

    public unsafe void Close()
    {
        if (!_isOpen) return;

        _font?.Dispose();
        _font = null;

        if (_renderer != null)
        {
            SDL.DestroyRenderer(_renderer);
            _renderer = null;
        }

        if (_window != null)
        {
            SDL.DestroyWindow(_window);
            _window = null;
        }

        _isOpen = false;
        Console.WriteLine("[ScriptViewer] Window closed");
    }

    public void Toggle()
    {
        if (_isOpen)
            Close();
        else
            Open();
    }

    public unsafe bool HandleEvent(SDLEvent* evt)
    {
        if (!_isOpen) return false;

        if (evt->Type == (uint)SDLEventType.Windowevent &&
            evt->Window.WindowID == _windowId)
        {
            if (evt->Window.Event == (byte)SDLWindowEventID.Close)
            {
                Close();
                return true;
            }
        }

        // Handle scroll
        if (evt->Type == (uint)SDLEventType.Mousewheel &&
            evt->Wheel.WindowID == _windowId)
        {
            _scrollOffset -= evt->Wheel.Y * 3;
            _scrollOffset = Math.Max(0, Math.Min(_scrollOffset, Math.Max(0, _scriptLines.Count - 35)));
            return true;
        }

        // Handle keyboard for zone navigation
        if (evt->Type == (uint)SDLEventType.Keydown &&
            SDL.GetWindowID(SDL.GetKeyboardFocus()) == _windowId)
        {
            var key = evt->Key.Keysym.Sym;

            if (key == 1073741903) // Right arrow - next zone
            {
                NextZone();
                return true;
            }
            else if (key == 1073741904) // Left arrow - prev zone
            {
                PrevZone();
                return true;
            }
            else if (key == 1073741899) // Page Up
            {
                _scrollOffset = Math.Max(0, _scrollOffset - 20);
                return true;
            }
            else if (key == 1073741902) // Page Down
            {
                _scrollOffset = Math.Min(Math.Max(0, _scriptLines.Count - 35), _scrollOffset + 20);
                return true;
            }
            else if (key == 1073741898) // Home
            {
                _scrollOffset = 0;
                return true;
            }
            else if (key == 1073741901) // End
            {
                _scrollOffset = Math.Max(0, _scriptLines.Count - 35);
                return true;
            }
        }

        // Handle mouse clicks for navigation buttons
        if (evt->Type == (uint)SDLEventType.Mousebuttondown &&
            evt->Button.WindowID == _windowId)
        {
            int mx = evt->Button.X;
            int my = evt->Button.Y;

            // Prev button
            if (mx >= 10 && mx <= 60 && my >= 8 && my <= 28)
            {
                PrevZone();
                return true;
            }
            // Next button
            if (mx >= 70 && mx <= 120 && my >= 8 && my <= 28)
            {
                NextZone();
                return true;
            }
            // "Go to Current" button
            if (mx >= 400 && mx <= 540 && my >= 8 && my <= 28)
            {
                JumpToCurrentZone();
                RefreshScripts();
                return true;
            }
        }

        return false;
    }

    private void NextZone()
    {
        if (_zonesWithScripts.Count == 0) return;
        _currentZoneIndex = (_currentZoneIndex + 1) % _zonesWithScripts.Count;
        _scrollOffset = 0;
        RefreshScripts();
    }

    private void PrevZone()
    {
        if (_zonesWithScripts.Count == 0) return;
        _currentZoneIndex = (_currentZoneIndex - 1 + _zonesWithScripts.Count) % _zonesWithScripts.Count;
        _scrollOffset = 0;
        RefreshScripts();
    }

    public void RefreshScripts()
    {
        _scriptLines.Clear();
        _highlights.Clear();

        if (_zonesWithScripts.Count == 0)
        {
            _scriptLines.Add("No zones with IACT scripts found");
            return;
        }

        int zoneId = _zonesWithScripts[_currentZoneIndex];
        var zone = _gameData.Zones[zoneId];

        // Extract highlights from the zone
        ExtractHighlights(zone);

        // Header
        _scriptLines.Add($"=== ZONE {zoneId}: {zone.Type} ({zone.Planet}) ===");
        _scriptLines.Add($"Size: {zone.Width}x{zone.Height} | IACT Actions: {zone.Actions.Count}");
        _scriptLines.Add($"Zone {_currentZoneIndex + 1} of {_zonesWithScripts.Count} with scripts");
        _scriptLines.Add("");

        // Zone objects summary
        var npcs = zone.Objects.Where(o => o.Type == ZoneObjectType.PuzzleNPC).ToList();
        var items = zone.Objects.Where(o =>
            o.Type == ZoneObjectType.CrateItem ||
            o.Type == ZoneObjectType.LocatorItem ||
            o.Type == ZoneObjectType.CrateWeapon).ToList();
        var doors = zone.Objects.Where(o =>
            o.Type == ZoneObjectType.DoorEntrance ||
            o.Type == ZoneObjectType.DoorExit).ToList();
        var triggers = zone.Objects.Where(o => o.Type == ZoneObjectType.Trigger).ToList();

        _scriptLines.Add($"Objects: {npcs.Count} NPCs, {items.Count} items, {doors.Count} doors, {triggers.Count} triggers");
        _scriptLines.Add("");

        // Each IACT action
        for (int i = 0; i < zone.Actions.Count; i++)
        {
            var action = zone.Actions[i];
            _scriptLines.Add($"========== IACT #{i} ==========");

            // Conditions
            if (action.Conditions.Count > 0)
            {
                _scriptLines.Add("CONDITIONS (all must be true):");
                foreach (var cond in action.Conditions)
                {
                    _scriptLines.Add($"  - {FormatCondition(cond)}");
                }
            }
            else
            {
                _scriptLines.Add("CONDITIONS: (none - always executes)");
            }

            _scriptLines.Add("");

            // Instructions
            _scriptLines.Add("INSTRUCTIONS:");
            foreach (var instr in action.Instructions)
            {
                var lines = FormatInstruction(instr);
                foreach (var line in lines)
                {
                    _scriptLines.Add($"  > {line}");
                }
            }
            _scriptLines.Add("");
        }

        if (zone.Actions.Count == 0)
        {
            _scriptLines.Add("(No IACT scripts in this zone)");
        }
    }

    private void ExtractHighlights(Zone zone)
    {
        var seen = new HashSet<(int, int)>();

        // Extract from zone objects first
        foreach (var obj in zone.Objects)
        {
            var key = (obj.X, obj.Y);
            if (seen.Contains(key)) continue;
            seen.Add(key);

            var type = obj.Type switch
            {
                ZoneObjectType.DoorEntrance or ZoneObjectType.DoorExit or ZoneObjectType.Teleporter => HighlightType.Door,
                ZoneObjectType.PuzzleNPC => HighlightType.NPC,
                ZoneObjectType.CrateItem or ZoneObjectType.CrateWeapon or ZoneObjectType.LocatorItem => HighlightType.Item,
                ZoneObjectType.Trigger => HighlightType.Trigger,
                _ => (HighlightType?)null
            };

            if (type.HasValue)
            {
                _highlights.Add(new ScriptHighlight
                {
                    X = obj.X,
                    Y = obj.Y,
                    Type = type.Value,
                    Label = obj.Type.ToString()
                });
            }
        }

        // Extract positions from IACT scripts
        foreach (var action in zone.Actions)
        {
            // From conditions
            foreach (var cond in action.Conditions)
            {
                var args = cond.Arguments;
                switch (cond.Opcode)
                {
                    case ConditionOpcode.Bump:
                    case ConditionOpcode.Standing:
                        if (args.Count >= 2)
                            AddHighlight(args[0], args[1], HighlightType.Position, cond.Opcode.ToString(), seen);
                        break;

                    case ConditionOpcode.TileAtIs:
                        if (args.Count >= 2)
                            AddHighlight(args[0], args[1], HighlightType.Tile, "TileCheck", seen);
                        break;
                }
            }

            // From instructions
            foreach (var instr in action.Instructions)
            {
                var args = instr.Arguments;
                switch (instr.Opcode)
                {
                    case InstructionOpcode.PlaceTile:
                    case InstructionOpcode.RemoveTile:
                    case InstructionOpcode.DrawTile:
                        if (args.Count >= 2)
                            AddHighlight(args[0], args[1], HighlightType.Tile, instr.Opcode.ToString(), seen);
                        break;

                    case InstructionOpcode.MoveTile:
                        if (args.Count >= 2)
                            AddHighlight(args[0], args[1], HighlightType.Tile, "MoveTile", seen);
                        break;

                    case InstructionOpcode.MoveHeroTo:
                        if (args.Count >= 2)
                            AddHighlight(args[0], args[1], HighlightType.Position, "Teleport", seen);
                        break;

                    case InstructionOpcode.EnableHotspot:
                    case InstructionOpcode.DisableHotspot:
                        // Hotspots are referenced by index, but we can show a trigger marker
                        // at any matching trigger object
                        break;
                }
            }
        }
    }

    private void AddHighlight(int x, int y, HighlightType type, string label, HashSet<(int, int)> seen)
    {
        // Validate coordinates are within zone bounds
        if (x < 0 || y < 0 || x >= 255 || y >= 255) return;

        var key = (x, y);
        if (seen.Contains(key)) return;
        seen.Add(key);

        _highlights.Add(new ScriptHighlight
        {
            X = x,
            Y = y,
            Type = type,
            Label = label
        });
    }

    private string FormatCondition(Condition cond)
    {
        var args = cond.Arguments;

        return cond.Opcode switch
        {
            ConditionOpcode.ZoneNotInitialized => "Zone not initialized (first visit)",
            ConditionOpcode.ZoneEntered => "Zone just entered",
            ConditionOpcode.Bump => $"Player bumped tile at ({GetArg(args, 0)}, {GetArg(args, 1)})",
            ConditionOpcode.PlacedItemIs => $"Placed item == {GetTileName(GetArg(args, 0))}",
            ConditionOpcode.Standing => $"Player standing at ({GetArg(args, 0)}, {GetArg(args, 1)})",
            ConditionOpcode.CounterIs => $"Counter[{GetArg(args, 0)}] == {GetArg(args, 1)}",
            ConditionOpcode.RandomIs => $"LastRandom == {GetArg(args, 0)}",
            ConditionOpcode.RandomIsGreaterThan => $"LastRandom > {GetArg(args, 0)}",
            ConditionOpcode.RandomIsLessThan => $"LastRandom < {GetArg(args, 0)}",
            ConditionOpcode.EnterByPlane => "Entered zone via X-Wing",
            ConditionOpcode.TileAtIs => $"Tile at ({GetArg(args, 0)},{GetArg(args, 1)}) layer {GetArg(args, 2)} == {GetTileName(GetArg(args, 3))}",
            ConditionOpcode.MonsterIsDead => $"Monster[{GetArg(args, 0)}] is dead",
            ConditionOpcode.HasNoActiveMonsters => "All monsters are dead",
            ConditionOpcode.HasItem => $"Player has {GetTileName(GetArg(args, 0))}",
            ConditionOpcode.RequiredItemIs => $"Zone required item == {GetTileName(GetArg(args, 0))}",
            ConditionOpcode.EndingIs => $"Game ending == {GetArg(args, 0)}",
            ConditionOpcode.ZoneIsSolved => $"Zone {GetArg(args, 0)} is solved",
            ConditionOpcode.NoItemPlaced => "No item has been placed",
            ConditionOpcode.ItemIsPlaced => "An item has been placed",
            ConditionOpcode.HealthIsLessThan => $"Player health < {GetArg(args, 0)}",
            ConditionOpcode.HealthIsGreaterThan => $"Player health > {GetArg(args, 0)}",
            ConditionOpcode.FindItemIs => $"Find puzzle item == {GetTileName(GetArg(args, 0))}",
            ConditionOpcode.NpcIs => $"Current NPC == {GetCharName(GetArg(args, 0))}",
            ConditionOpcode.HasNpc => "Interacting with an NPC",
            ConditionOpcode.GamesWonIs => $"Games won == {GetArg(args, 0)}",
            ConditionOpcode.DroppedItemIs => $"Dropped item == {GetTileName(GetArg(args, 0))}",
            _ => $"{cond.Opcode}({string.Join(", ", args)})"
        };
    }

    private List<string> FormatInstruction(Instruction instr)
    {
        var result = new List<string>();
        var args = instr.Arguments;

        string main = instr.Opcode switch
        {
            InstructionOpcode.PlaceTile => $"PLACE tile {GetTileName(GetArg(args, 3))} at ({GetArg(args, 0)},{GetArg(args, 1)}) layer {GetArg(args, 2)}",
            InstructionOpcode.RemoveTile => $"REMOVE tile at ({GetArg(args, 0)},{GetArg(args, 1)}) layer {GetArg(args, 2)}",
            InstructionOpcode.MoveTile => $"MOVE tile from ({GetArg(args, 0)},{GetArg(args, 1)})",
            InstructionOpcode.DrawTile => $"DRAW tile {GetTileName(GetArg(args, 3))} at ({GetArg(args, 0)},{GetArg(args, 1)})",
            InstructionOpcode.SpeakHero => "LUKE SAYS:",
            InstructionOpcode.SpeakNpc => "NPC SAYS:",
            InstructionOpcode.Wait => $"WAIT {GetArg(args, 0)} ms",
            InstructionOpcode.Redraw => "REDRAW zone",
            InstructionOpcode.PlaySound => $"PLAY sound #{GetArg(args, 0)}",
            InstructionOpcode.StopSound => $"STOP sound #{GetArg(args, 0)}",
            InstructionOpcode.RollDice => $"ROLL random 0-{GetArg(args, 0)}",
            InstructionOpcode.SetCounter => $"SET counter[{GetArg(args, 0)}] = {GetArg(args, 1)}",
            InstructionOpcode.AddToCounter => $"ADD {GetArg(args, 1)} to counter[{GetArg(args, 0)}]",
            InstructionOpcode.SetVariable => $"SET variable[{GetArg(args, 0)}] = {GetArg(args, 1)}",
            InstructionOpcode.HideHero => "HIDE Luke",
            InstructionOpcode.ShowHero => "SHOW Luke",
            InstructionOpcode.MoveHeroTo => $"TELEPORT Luke to ({GetArg(args, 0)}, {GetArg(args, 1)})",
            InstructionOpcode.MoveHeroBy => $"MOVE Luke by ({GetArg(args, 0)}, {GetArg(args, 1)})",
            InstructionOpcode.DisableAction => $"DISABLE action #{GetArg(args, 0)}",
            InstructionOpcode.EnableHotspot => $"ENABLE hotspot #{GetArg(args, 0)}",
            InstructionOpcode.DisableHotspot => $"DISABLE hotspot #{GetArg(args, 0)}",
            InstructionOpcode.EnableMonster => $"SPAWN monster #{GetArg(args, 0)}",
            InstructionOpcode.DisableMonster => $"DESPAWN monster #{GetArg(args, 0)}",
            InstructionOpcode.EnableAllMonsters => "SPAWN all monsters",
            InstructionOpcode.DisableAllMonsters => "DESPAWN all monsters",
            InstructionOpcode.DropItem => $"DROP {GetTileName(GetArg(args, 0))} in zone",
            InstructionOpcode.AddItem => $"GIVE player {GetTileName(GetArg(args, 0))}",
            InstructionOpcode.RemoveItem => $"TAKE {GetTileName(GetArg(args, 0))} from player",
            InstructionOpcode.MarkAsSolved => "*** MARK ZONE SOLVED ***",
            InstructionOpcode.WinGame => "*** WIN GAME ***",
            InstructionOpcode.LoseGame => "*** LOSE GAME ***",
            InstructionOpcode.ChangeZone => $"GOTO zone {GetArg(args, 0)} at ({GetArg(args, 1)},{GetArg(args, 2)})",
            InstructionOpcode.SetZoneType => $"SET zone type = {GetArg(args, 0)}",
            InstructionOpcode.AddHealth => $"HEAL player +{GetArg(args, 0)}",
            InstructionOpcode.SubtractHealth => $"DAMAGE player -{GetArg(args, 0)}",
            InstructionOpcode.SetHealth => $"SET health = {GetArg(args, 0)}",
            InstructionOpcode.SpeakNpc2 => "NPC SAYS:",
            _ => $"{instr.Opcode}({string.Join(", ", args)})"
        };

        result.Add(main);

        // Add dialogue text on separate lines
        if (!string.IsNullOrEmpty(instr.Text))
        {
            // Word wrap long text
            var text = instr.Text.Replace("\n", " ");
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var line = "    \"";
            foreach (var word in words)
            {
                if (line.Length + word.Length > 60)
                {
                    result.Add(line);
                    line = "     " + word;
                }
                else
                {
                    line += (line.Length > 5 ? " " : "") + word;
                }
            }
            if (line.Length > 5)
                result.Add(line + "\"");
        }

        return result;
    }

    private short GetArg(List<short> args, int index)
    {
        return index < args.Count ? args[index] : (short)0;
    }

    private string GetTileName(int tileId)
    {
        if (tileId < 0 || tileId >= _gameData.Tiles.Count)
            return $"Tile#{tileId}";

        if (_gameData.TileNames.TryGetValue(tileId, out var name))
            return $"\"{name}\" (#{tileId})";

        var tile = _gameData.Tiles[tileId];
        if (tile.IsWeapon) return $"[Weapon #{tileId}]";
        if (tile.IsItem) return $"[Item #{tileId}]";
        if (tile.IsCharacter) return $"[Character #{tileId}]";

        return $"Tile#{tileId}";
    }

    private string GetCharName(int charId)
    {
        if (charId < 0 || charId >= _gameData.Characters.Count)
            return $"Character#{charId}";
        var ch = _gameData.Characters[charId];
        return $"{ch.Name ?? "Unknown"} (#{charId})";
    }

    public unsafe void Render()
    {
        if (!_isOpen || _renderer == null) return;

        // Clear background
        SDL.SetRenderDrawColor(_renderer, 25, 30, 40, 255);
        SDL.RenderClear(_renderer);

        // Navigation bar
        RenderNavBar();

        // Render script lines
        int startY = 40;
        int lineHeight = 12;
        int maxLines = (WindowHeight - startY - 25) / lineHeight;

        for (int i = _scrollOffset; i < Math.Min(_scrollOffset + maxLines, _scriptLines.Count); i++)
        {
            var line = _scriptLines[i];
            int y = startY + (i - _scrollOffset) * lineHeight;

            // Color based on content
            byte r = 200, g = 200, b = 200;

            if (line.StartsWith("==="))
            {
                r = 255; g = 220; b = 100; // Yellow header
            }
            else if (line.StartsWith("====="))
            {
                r = 100; g = 180; b = 255; // Blue IACT header
            }
            else if (line.StartsWith("CONDITIONS"))
            {
                r = 150; g = 255; b = 150; // Green
            }
            else if (line.StartsWith("INSTRUCTIONS"))
            {
                r = 255; g = 180; b = 100; // Orange
            }
            else if (line.Contains("***"))
            {
                r = 50; g = 255; b = 50; // Bright green for important
            }
            else if (line.StartsWith("  -"))
            {
                r = 180; g = 255; b = 180; // Light green for conditions
            }
            else if (line.StartsWith("  >"))
            {
                r = 255; g = 200; b = 150; // Light orange for instructions
            }
            else if (line.Contains("\""))
            {
                r = 200; g = 200; b = 255; // Light blue for dialogue
            }
            else if (line.StartsWith("Objects:") || line.StartsWith("Size:") || line.StartsWith("Zone "))
            {
                r = 150; g = 150; b = 170; // Gray for info
            }

            _font?.RenderText(_renderer, line, 10, y, 1, r, g, b, 255);
        }

        // Scroll bar
        if (_scriptLines.Count > maxLines)
        {
            int scrollBarHeight = WindowHeight - startY - 25;
            int thumbHeight = Math.Max(20, scrollBarHeight * maxLines / _scriptLines.Count);
            int thumbY = startY + (_scrollOffset * (scrollBarHeight - thumbHeight)) / Math.Max(1, _scriptLines.Count - maxLines);

            SDL.SetRenderDrawColor(_renderer, 50, 55, 70, 255);
            var scrollBg = new SDLRect { X = WindowWidth - 14, Y = startY, W = 10, H = scrollBarHeight };
            SDL.RenderFillRect(_renderer, &scrollBg);

            SDL.SetRenderDrawColor(_renderer, 100, 110, 140, 255);
            var scrollThumb = new SDLRect { X = WindowWidth - 14, Y = thumbY, W = 10, H = thumbHeight };
            SDL.RenderFillRect(_renderer, &scrollThumb);
        }

        // Footer
        SDL.SetRenderDrawColor(_renderer, 35, 40, 55, 255);
        var footer = new SDLRect { X = 0, Y = WindowHeight - 22, W = WindowWidth, H = 22 };
        SDL.RenderFillRect(_renderer, &footer);
        _font?.RenderText(_renderer, "Arrow keys: Prev/Next zone | Mouse wheel: Scroll | PgUp/PgDn: Fast scroll",
            10, WindowHeight - 17, 1, 130, 130, 150, 255);

        SDL.RenderPresent(_renderer);
    }

    private unsafe void RenderNavBar()
    {
        // Background
        SDL.SetRenderDrawColor(_renderer, 40, 45, 60, 255);
        var navBg = new SDLRect { X = 0, Y = 0, W = WindowWidth, H = 35 };
        SDL.RenderFillRect(_renderer, &navBg);

        // Prev button
        SDL.SetRenderDrawColor(_renderer, 60, 80, 120, 255);
        var prevBtn = new SDLRect { X = 10, Y = 8, W = 50, H = 20 };
        SDL.RenderFillRect(_renderer, &prevBtn);
        _font?.RenderText(_renderer, "< Prev", 14, 13, 1, 200, 200, 255, 255);

        // Next button
        SDL.SetRenderDrawColor(_renderer, 60, 80, 120, 255);
        var nextBtn = new SDLRect { X = 70, Y = 8, W = 50, H = 20 };
        SDL.RenderFillRect(_renderer, &nextBtn);
        _font?.RenderText(_renderer, "Next >", 74, 13, 1, 200, 200, 255, 255);

        // Current zone info
        if (_zonesWithScripts.Count > 0)
        {
            int zoneId = _zonesWithScripts[_currentZoneIndex];
            var zone = _gameData.Zones[zoneId];
            string info = $"Zone {zoneId} ({zone.Type}) - {zone.Actions.Count} IACT scripts";
            _font?.RenderText(_renderer, info, 135, 13, 1, 255, 255, 200, 255);
        }

        // Go to Current button
        SDL.SetRenderDrawColor(_renderer, 80, 100, 60, 255);
        var currentBtn = new SDLRect { X = 400, Y = 8, W = 140, H = 20 };
        SDL.RenderFillRect(_renderer, &currentBtn);
        _font?.RenderText(_renderer, "Go to Current Zone", 405, 13, 1, 200, 255, 200, 255);
    }

    public void Dispose()
    {
        Close();
    }
}
