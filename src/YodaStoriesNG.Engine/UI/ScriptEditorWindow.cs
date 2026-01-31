using Hexa.NET.SDL2;
using YodaStoriesNG.Engine.Data;
using YodaStoriesNG.Engine.Game;
using YodaStoriesNG.Engine.Rendering;

namespace YodaStoriesNG.Engine.UI;

/// <summary>
/// A visual WYSIWYG-style IACT script editor/viewer.
/// Shows scripts in a readable format with highlighting support.
/// </summary>
public class ScriptEditorWindow : IDisposable
{
    private readonly GameState _state;
    private readonly GameData _gameData;

    private unsafe SDLWindow* _window;
    private unsafe SDLRenderer* _renderer;
    private BitmapFont? _font;

    private int _windowWidth = 700;
    private int _windowHeight = 600;

    private bool _isOpen = false;
    private uint _windowId;
    private int _scrollOffset = 0;
    private int _selectedZoneIndex = 0;
    private int _selectedActionIndex = 0;
    private List<int> _zonesWithScripts = new();
    private List<ScriptHighlight> _highlights = new();

    // UI layout
    private const int LeftPanelWidth = 220;
    private const int TopBarHeight = 60;
    private const int LineHeight = 16;
    private int _zoneListScrollOffset = 0;
    private int _totalActionCount = 0;

    // Planet grouping
    private Dictionary<string, List<int>> _zonesByPlanet = new();
    private List<string> _planetOrder = new() { "Desert", "Forest", "Snow", "Swamp", "None" };
    private Dictionary<string, bool> _planetExpanded = new();
    private string? _selectedPlanet = null;
    private bool _isDraggingScrollbar = false;

    // Edit mode
    private bool _isEditMode = false;
    private int _editingConditionIndex = -1;
    private int _editingInstructionIndex = -1;
    private string _editBuffer = "";
    private int _editCursorPos = 0;
    private bool _hasUnsavedChanges = false;

    /// <summary>
    /// Event fired when user requests to teleport to the viewed zone.
    /// </summary>
    public event System.Action<int>? OnTeleportToZone;

    /// <summary>
    /// Event fired when user wants to jump to the bot's current zone.
    /// </summary>
    public event System.Action? OnJumpToBot;

    public bool IsOpen => _isOpen;

    public int ViewingZoneId => _isOpen && _zonesWithScripts.Count > 0
        ? _zonesWithScripts[_selectedZoneIndex]
        : -1;

    /// <summary>
    /// Returns highlights for the specified zone.
    /// Highlights show when viewing the current zone OR when the script editor is focused on that zone.
    /// </summary>
    public IReadOnlyList<ScriptHighlight> GetHighlightsForZone(int zoneId)
    {
        if (!_isOpen || _zonesWithScripts.Count == 0)
            return Array.Empty<ScriptHighlight>();

        // Always return highlights for the zone being viewed in the editor
        if (ViewingZoneId == zoneId)
            return _highlights;

        return Array.Empty<ScriptHighlight>();
    }

    public ScriptEditorWindow(GameState state, GameData gameData)
    {
        _state = state;
        _gameData = gameData;
    }

    public unsafe void Open()
    {
        if (_isOpen) return;

        _window = SDL.CreateWindow(
            "IACT Script Editor",
            100, 50,
            _windowWidth, _windowHeight,
            (uint)(SDLWindowFlags.Shown | SDLWindowFlags.Resizable));

        if (_window == null)
        {
            Console.WriteLine($"Failed to create script editor window: {SDL.GetErrorS()}");
            return;
        }

        _renderer = SDL.CreateRenderer(_window, -1,
            (uint)(SDLRendererFlags.Accelerated | SDLRendererFlags.Presentvsync));

        if (_renderer == null)
        {
            SDL.DestroyWindow(_window);
            _window = null;
            return;
        }

        _font = new BitmapFont();
        _font.Initialize(_renderer);

        _windowId = SDL.GetWindowID(_window);
        _isOpen = true;

        FindZonesWithScripts();
        JumpToCurrentZone();
        RefreshHighlights();
        Console.WriteLine($"[ScriptEditor] Opened - {_zonesWithScripts.Count} zones with scripts");
    }

    private void FindZonesWithScripts()
    {
        _zonesWithScripts.Clear();
        _zonesByPlanet.Clear();
        _totalActionCount = 0;

        // Initialize planet groups
        foreach (var planet in _planetOrder)
        {
            _zonesByPlanet[planet] = new List<int>();
            if (!_planetExpanded.ContainsKey(planet))
                _planetExpanded[planet] = true; // Start expanded
        }

        for (int i = 0; i < _gameData.Zones.Count; i++)
        {
            var zone = _gameData.Zones[i];
            var actionCount = zone.Actions.Count;
            if (actionCount > 0)
            {
                _zonesWithScripts.Add(i);
                _totalActionCount += actionCount;

                // Group by planet
                string planetName = zone.Planet.ToString();
                if (!_zonesByPlanet.ContainsKey(planetName))
                    planetName = "None";
                _zonesByPlanet[planetName].Add(i);
            }
        }

        // Set initial selection to first planet with zones
        if (_selectedPlanet == null)
        {
            foreach (var planet in _planetOrder)
            {
                if (_zonesByPlanet[planet].Count > 0)
                {
                    _selectedPlanet = planet;
                    break;
                }
            }
        }
    }

    public void JumpToCurrentZone()
    {
        int idx = _zonesWithScripts.IndexOf(_state.CurrentZoneId);
        if (idx >= 0)
        {
            _selectedZoneIndex = idx;
            _selectedActionIndex = 0;
            _scrollOffset = 0;
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
    }

    public void Toggle()
    {
        if (_isOpen) Close();
        else Open();
    }

    public unsafe bool HandleEvent(SDLEvent* evt)
    {
        if (!_isOpen) return false;

        // Window events
        if (evt->Type == (uint)SDLEventType.Windowevent && evt->Window.WindowID == _windowId)
        {
            if (evt->Window.Event == (byte)SDLWindowEventID.Close)
            {
                Close();
                return true;
            }
            if (evt->Window.Event == (byte)SDLWindowEventID.Resized)
            {
                _windowWidth = evt->Window.Data1;
                _windowHeight = evt->Window.Data2;
                return true;
            }
        }

        // Mouse wheel scroll
        if (evt->Type == (uint)SDLEventType.Mousewheel && evt->Wheel.WindowID == _windowId)
        {
            int mx, my;
            SDL.GetMouseState(&mx, &my);

            // Wheel.Y > 0 when scrolling away from user (up)
            // For natural scrolling: scroll up = content moves down = offset decreases
            int scrollAmount = evt->Wheel.Y * 20;

            if (mx < LeftPanelWidth)
            {
                // Scroll zone list
                _zoneListScrollOffset -= scrollAmount;
                _zoneListScrollOffset = Math.Max(0, _zoneListScrollOffset);
            }
            else
            {
                // Scroll content
                _scrollOffset -= evt->Wheel.Y * 3;
                _scrollOffset = Math.Max(0, _scrollOffset);
            }
            return true;
        }

        // Mouse click
        if (evt->Type == (uint)SDLEventType.Mousebuttondown && evt->Button.WindowID == _windowId)
        {
            int mx = evt->Button.X;
            int my = evt->Button.Y;

            // Left panel - scrollbar drag start
            if (mx >= LeftPanelWidth - 12 && mx < LeftPanelWidth && my > TopBarHeight + 45 && my < _windowHeight - 70)
            {
                _isDraggingScrollbar = true;
                HandleScrollbarDrag(my);
                return true;
            }

            // Left panel - zone list with planet groups
            if (mx < LeftPanelWidth - 12 && my > TopBarHeight + 45 && my < _windowHeight - 70)
            {
                HandleZoneListClick(my);
                return true;
            }

            // Content area click - check if in edit mode
            if (_isEditMode && mx > LeftPanelWidth && my > TopBarHeight)
            {
                HandleContentClick(mx, my);
                return true;
            }

            return true;
        }

        // Mouse button up - stop scrollbar drag
        if (evt->Type == (uint)SDLEventType.Mousebuttonup && evt->Button.WindowID == _windowId)
        {
            _isDraggingScrollbar = false;
            return false;
        }

        // Mouse motion - scrollbar drag
        if (evt->Type == (uint)SDLEventType.Mousemotion && evt->Motion.WindowID == _windowId)
        {
            if (_isDraggingScrollbar)
            {
                HandleScrollbarDrag(evt->Motion.Y);
                return true;
            }
        }

        // Keyboard
        if (evt->Type == (uint)SDLEventType.Keydown && SDL.GetWindowID(SDL.GetKeyboardFocus()) == _windowId)
        {
            var key = evt->Key.Keysym.Sym;

            if (key == 1073741906) // Up
            {
                if (_selectedZoneIndex > 0)
                {
                    _selectedZoneIndex--;
                    _selectedActionIndex = 0;
                    _scrollOffset = 0;
                    RefreshHighlights();
                }
                return true;
            }
            if (key == 1073741905) // Down
            {
                if (_selectedZoneIndex < _zonesWithScripts.Count - 1)
                {
                    _selectedZoneIndex++;
                    _selectedActionIndex = 0;
                    _scrollOffset = 0;
                    RefreshHighlights();
                }
                return true;
            }
            if (key == 1073741903) // Right - next action
            {
                int zoneId = _zonesWithScripts[_selectedZoneIndex];
                var zone = _gameData.Zones[zoneId];
                if (zone.Actions.Count > 0)
                {
                    _selectedActionIndex = (_selectedActionIndex + 1) % zone.Actions.Count;
                    _scrollOffset = 0;
                    RefreshHighlights();
                }
                return true;
            }
            if (key == 1073741904) // Left - prev action
            {
                int zoneId = _zonesWithScripts[_selectedZoneIndex];
                var zone = _gameData.Zones[zoneId];
                if (zone.Actions.Count > 0)
                {
                    _selectedActionIndex = (_selectedActionIndex - 1 + zone.Actions.Count) % zone.Actions.Count;
                    _scrollOffset = 0;
                    RefreshHighlights();
                }
                return true;
            }
            if (key == 'g' || key == 'G') // G = Go to current zone in list
            {
                JumpToCurrentZone();
                RefreshHighlights();
                return true;
            }
            if (key == 't' || key == 'T') // T = Teleport player to viewed zone
            {
                if (_zonesWithScripts.Count > 0)
                {
                    int zoneId = _zonesWithScripts[_selectedZoneIndex];
                    OnTeleportToZone?.Invoke(zoneId);
                }
                return true;
            }
            if (key == 'b' || key == 'B') // B = Jump to bot's zone
            {
                OnJumpToBot?.Invoke();
                return true;
            }
            if (key == 'e' || key == 'E') // E = Toggle edit mode
            {
                _isEditMode = !_isEditMode;
                _editingConditionIndex = -1;
                _editingInstructionIndex = -1;
                _editBuffer = "";
                return true;
            }
        }

        // Handle text input in edit mode
        if (_isEditMode && (_editingConditionIndex >= 0 || _editingInstructionIndex >= 0))
        {
            if (evt->Type == (uint)SDLEventType.Keydown && SDL.GetWindowID(SDL.GetKeyboardFocus()) == _windowId)
            {
                var key = (int)evt->Key.Keysym.Sym;

                if (key == 8) // Backspace
                {
                    if (_editCursorPos > 0 && _editBuffer.Length > 0)
                    {
                        _editBuffer = _editBuffer.Remove(_editCursorPos - 1, 1);
                        _editCursorPos--;
                        _hasUnsavedChanges = true;
                    }
                    return true;
                }
                if (key == 127) // Delete
                {
                    if (_editCursorPos < _editBuffer.Length)
                    {
                        _editBuffer = _editBuffer.Remove(_editCursorPos, 1);
                        _hasUnsavedChanges = true;
                    }
                    return true;
                }
                if (key == 1073741904) // Left arrow
                {
                    if (_editCursorPos > 0) _editCursorPos--;
                    return true;
                }
                if (key == 1073741903) // Right arrow
                {
                    if (_editCursorPos < _editBuffer.Length) _editCursorPos++;
                    return true;
                }
                if (key == 1073741898) // Home
                {
                    _editCursorPos = 0;
                    return true;
                }
                if (key == 1073741901) // End
                {
                    _editCursorPos = _editBuffer.Length;
                    return true;
                }
                if (key == 13 || key == 1073741912) // Enter or Keypad Enter
                {
                    ApplyEdit();
                    return true;
                }
                if (key == 27) // Escape
                {
                    CancelEdit();
                    return true;
                }

                // Handle printable characters
                char ch = '\0';
                bool shift = (evt->Key.Keysym.Mod & (ushort)SDLKeymod.Shift) != 0;

                if (key >= 32 && key <= 126)
                {
                    ch = (char)key;
                    // Apply shift for uppercase/symbols
                    if (shift && key >= 'a' && key <= 'z')
                        ch = (char)(key - 32);
                    else if (shift)
                    {
                        ch = key switch
                        {
                            '1' => '!', '2' => '@', '3' => '#', '4' => '$', '5' => '%',
                            '6' => '^', '7' => '&', '8' => '*', '9' => '(', '0' => ')',
                            '-' => '_', '=' => '+', '[' => '{', ']' => '}', '\\' => '|',
                            ';' => ':', '\'' => '"', ',' => '<', '.' => '>', '/' => '?',
                            '`' => '~',
                            _ => ch
                        };
                    }
                }

                if (ch != '\0')
                {
                    _editBuffer = _editBuffer.Insert(_editCursorPos, ch.ToString());
                    _editCursorPos++;
                    _hasUnsavedChanges = true;
                    return true;
                }
            }
        }

        return false;
    }

    private void ApplyEdit()
    {
        if (_zonesWithScripts.Count == 0) return;

        int zoneId = _zonesWithScripts[_selectedZoneIndex];
        var zone = _gameData.Zones[zoneId];
        if (_selectedActionIndex >= zone.Actions.Count) return;

        var action = zone.Actions[_selectedActionIndex];

        if (_editingConditionIndex >= 0 && _editingConditionIndex < action.Conditions.Count)
        {
            // Try to parse condition text back to arguments (simplified)
            var cond = action.Conditions[_editingConditionIndex];
            cond.Text = _editBuffer;
            Console.WriteLine($"[ScriptEditor] Updated condition {_editingConditionIndex} text: {_editBuffer}");
        }
        else if (_editingInstructionIndex >= 0 && _editingInstructionIndex < action.Instructions.Count)
        {
            // Update instruction text
            var instr = action.Instructions[_editingInstructionIndex];
            instr.Text = _editBuffer;
            Console.WriteLine($"[ScriptEditor] Updated instruction {_editingInstructionIndex} text: {_editBuffer}");
        }

        _editingConditionIndex = -1;
        _editingInstructionIndex = -1;
        _editBuffer = "";
        _hasUnsavedChanges = true;
    }

    private void CancelEdit()
    {
        _editingConditionIndex = -1;
        _editingInstructionIndex = -1;
        _editBuffer = "";
    }

    private void HandleZoneListClick(int clickY)
    {
        // Calculate which item was clicked based on scroll position
        int listStartY = TopBarHeight + 46;
        int itemHeight = 22;

        // Build the same virtual list as in render
        var items = new List<(string type, string planet, int zoneId)>();
        foreach (var planet in _planetOrder)
        {
            if (!_zonesByPlanet.ContainsKey(planet) || _zonesByPlanet[planet].Count == 0)
                continue;
            items.Add(("planet", planet, -1));
            if (_planetExpanded.GetValueOrDefault(planet, true))
            {
                foreach (var zoneId in _zonesByPlanet[planet])
                    items.Add(("zone", planet, zoneId));
            }
        }

        // Find which item was clicked
        int clickedItemIndex = (_zoneListScrollOffset + clickY - listStartY) / itemHeight;
        if (clickedItemIndex < 0 || clickedItemIndex >= items.Count)
            return;

        var item = items[clickedItemIndex];
        if (item.type == "planet")
        {
            // Toggle planet expansion
            _planetExpanded[item.planet] = !_planetExpanded.GetValueOrDefault(item.planet, true);
        }
        else
        {
            // Select zone
            int idx = _zonesWithScripts.IndexOf(item.zoneId);
            if (idx >= 0)
            {
                _selectedZoneIndex = idx;
                _selectedActionIndex = 0;
                _scrollOffset = 0;
                RefreshHighlights();
            }
        }
    }

    private void HandleContentClick(int mx, int my)
    {
        if (_zonesWithScripts.Count == 0) return;

        int zoneId = _zonesWithScripts[_selectedZoneIndex];
        var zone = _gameData.Zones[zoneId];
        if (_selectedActionIndex >= zone.Actions.Count) return;

        var action = zone.Actions[_selectedActionIndex];

        // Calculate which line was clicked based on scroll position
        int contentY = TopBarHeight + 10;
        int clickedLine = (_scrollOffset * LineHeight + my - contentY) / LineHeight;

        // Track line positions as we render
        int line = 0;

        // Zone objects section header
        line++; // "ZONE OBJECTS" header
        int zoneObjCount = zone.Objects.Count(o =>
            o.Type == ZoneObjectType.DoorEntrance || o.Type == ZoneObjectType.DoorExit ||
            o.Type == ZoneObjectType.Lock || o.Type == ZoneObjectType.PuzzleNPC ||
            o.Type == ZoneObjectType.CrateItem || o.Type == ZoneObjectType.CrateWeapon ||
            o.Type == ZoneObjectType.LocatorItem || o.Type == ZoneObjectType.Trigger);
        line += Math.Max(1, zoneObjCount);
        line++; // spacing

        // Conditions section header
        line++; // "CONDITIONS" header
        int condStart = line;
        int condCount = action.Conditions.Count;
        if (condCount == 0) condCount = 1; // "(none)" line
        line += condCount;
        line++; // spacing

        // Instructions section header
        line++; // "INSTRUCTIONS" header
        int instrStart = line;

        // Check if clicked on a condition
        if (clickedLine >= condStart && clickedLine < condStart + action.Conditions.Count)
        {
            int condIdx = clickedLine - condStart;
            if (condIdx >= 0 && condIdx < action.Conditions.Count)
            {
                _editingConditionIndex = condIdx;
                _editingInstructionIndex = -1;
                _editBuffer = action.Conditions[condIdx].Text ?? "";
                _editCursorPos = _editBuffer.Length;
                Console.WriteLine($"[ScriptEditor] Editing condition {condIdx}");
                return;
            }
        }

        // Check if clicked on an instruction
        if (clickedLine >= instrStart)
        {
            // Count lines including dialogue text
            int currentLine = instrStart;
            for (int i = 0; i < action.Instructions.Count; i++)
            {
                var instr = action.Instructions[i];
                int instrLines = 1;
                if (!string.IsNullOrEmpty(instr.Text))
                {
                    instrLines += WordWrap(instr.Text, 70).Count;
                }

                if (clickedLine >= currentLine && clickedLine < currentLine + instrLines)
                {
                    _editingInstructionIndex = i;
                    _editingConditionIndex = -1;
                    _editBuffer = instr.Text ?? "";
                    _editCursorPos = _editBuffer.Length;
                    Console.WriteLine($"[ScriptEditor] Editing instruction {i}");
                    return;
                }
                currentLine += instrLines;
            }
        }
    }

    private void HandleScrollbarDrag(int mouseY)
    {
        // Calculate scroll position based on mouse Y
        int listStartY = TopBarHeight + 46;
        int listEndY = _windowHeight - 80;
        int visibleHeight = listEndY - listStartY;

        // Build virtual list to get total height
        int itemHeight = 22;
        int totalItems = 0;
        foreach (var planet in _planetOrder)
        {
            if (!_zonesByPlanet.ContainsKey(planet) || _zonesByPlanet[planet].Count == 0)
                continue;
            totalItems++; // planet header
            if (_planetExpanded.GetValueOrDefault(planet, true))
                totalItems += _zonesByPlanet[planet].Count;
        }

        int totalHeight = totalItems * itemHeight;
        int maxScroll = Math.Max(0, totalHeight - visibleHeight);

        // Map mouse position to scroll position
        float scrollPercent = (float)(mouseY - listStartY) / visibleHeight;
        _zoneListScrollOffset = (int)(scrollPercent * maxScroll);
        _zoneListScrollOffset = Math.Clamp(_zoneListScrollOffset, 0, maxScroll);
    }

    /// <summary>
    /// Jumps to a specific zone by ID (used for bot teleport).
    /// </summary>
    public void JumpToZone(int zoneId)
    {
        int idx = _zonesWithScripts.IndexOf(zoneId);
        if (idx >= 0)
        {
            _selectedZoneIndex = idx;
            _selectedActionIndex = 0;
            _scrollOffset = 0;
            RefreshHighlights();
        }
    }

    private void RefreshHighlights()
    {
        _highlights.Clear();
        if (_zonesWithScripts.Count == 0) return;

        int zoneId = _zonesWithScripts[_selectedZoneIndex];
        var zone = _gameData.Zones[zoneId];

        // Add zone object highlights
        foreach (var obj in zone.Objects)
        {
            var type = obj.Type switch
            {
                ZoneObjectType.DoorEntrance or ZoneObjectType.DoorExit or ZoneObjectType.Teleporter => HighlightType.Door,
                ZoneObjectType.PuzzleNPC => HighlightType.NPC,
                ZoneObjectType.CrateItem or ZoneObjectType.CrateWeapon or ZoneObjectType.LocatorItem => HighlightType.Item,
                ZoneObjectType.Trigger => HighlightType.Trigger,
                ZoneObjectType.Lock => HighlightType.Door,
                _ => (HighlightType?)null
            };

            if (type.HasValue)
            {
                _highlights.Add(new ScriptHighlight
                {
                    X = obj.X,
                    Y = obj.Y,
                    Type = type.Value,
                    Label = $"{obj.Type}"
                });
            }
        }

        // Add highlights from current action
        if (_selectedActionIndex < zone.Actions.Count)
        {
            var action = zone.Actions[_selectedActionIndex];
            ExtractActionHighlights(action);
        }
    }

    private void ExtractActionHighlights(Data.Action action)
    {
        var seen = new HashSet<(int, int)>();
        foreach (var h in _highlights)
            seen.Add((h.X, h.Y));

        foreach (var cond in action.Conditions)
        {
            var args = cond.Arguments;
            switch (cond.Opcode)
            {
                case ConditionOpcode.Bump:
                case ConditionOpcode.StandingOn:
                case ConditionOpcode.HeroIsAt:
                    if (args.Count >= 2)
                        AddHighlight(args[0], args[1], HighlightType.Position, cond.Opcode.ToString(), seen);
                    break;
                case ConditionOpcode.TileAtIs:
                case ConditionOpcode.IsVariable:
                    if (args.Count >= 2)
                        AddHighlight(args[0], args[1], HighlightType.Tile, cond.Opcode.ToString(), seen);
                    break;
                case ConditionOpcode.PlacedItemIs:
                case ConditionOpcode.PlacedItemIsNot:
                case ConditionOpcode.NoItemPlaced:
                    if (args.Count >= 2)
                        AddHighlight(args[0], args[1], HighlightType.Item, "PlaceItem", seen);
                    break;
                case ConditionOpcode.DropsQuestItemAt:
                    if (args.Count >= 2)
                        AddHighlight(args[0], args[1], HighlightType.Item, "DropQuest", seen);
                    break;
            }
        }

        foreach (var instr in action.Instructions)
        {
            var args = instr.Arguments;
            switch (instr.Opcode)
            {
                case InstructionOpcode.PlaceTile:
                case InstructionOpcode.RemoveTile:
                case InstructionOpcode.SetVariable:
                    if (args.Count >= 2)
                        AddHighlight(args[0], args[1], HighlightType.Tile, instr.Opcode.ToString(), seen);
                    break;
                case InstructionOpcode.MoveTile:
                    if (args.Count >= 2)
                        AddHighlight(args[0], args[1], HighlightType.Tile, "MoveFrom", seen);
                    if (args.Count >= 5)
                        AddHighlight(args[3], args[4], HighlightType.Tile, "MoveTo", seen);
                    break;
                case InstructionOpcode.DrawTile:
                case InstructionOpcode.SetTileNeedsDisplay:
                    if (args.Count >= 2)
                        AddHighlight(args[0], args[1], HighlightType.Tile, instr.Opcode.ToString(), seen);
                    break;
                case InstructionOpcode.MoveHeroTo:
                    if (args.Count >= 2)
                        AddHighlight(args[0], args[1], HighlightType.Position, "Teleport", seen);
                    break;
                case InstructionOpcode.SpeakNpc:
                    if (args.Count >= 2)
                        AddHighlight(args[0], args[1], HighlightType.NPC, "Speech", seen);
                    break;
                case InstructionOpcode.DropItem:
                    if (args.Count >= 3)
                        AddHighlight(args[1], args[2], HighlightType.Item, "DropItem", seen);
                    break;
            }
        }
    }

    private void AddHighlight(int x, int y, HighlightType type, string label, HashSet<(int, int)> seen)
    {
        if (x < 0 || y < 0 || x > 255 || y > 255) return;
        var key = (x, y);
        if (seen.Contains(key)) return;
        seen.Add(key);

        _highlights.Add(new ScriptHighlight { X = x, Y = y, Type = type, Label = label });
    }

    public unsafe void Render()
    {
        if (!_isOpen || _renderer == null) return;

        // Clear
        SDL.SetRenderDrawColor(_renderer, 20, 22, 28, 255);
        SDL.RenderClear(_renderer);

        RenderZoneList();
        RenderTopBar();
        RenderActionContent();

        SDL.RenderPresent(_renderer);
    }

    private (byte r, byte g, byte b) GetPlanetColor(string planet)
    {
        return planet switch
        {
            "Desert" => (255, 200, 100),  // Sandy yellow
            "Forest" => (100, 200, 100),  // Green
            "Snow" => (180, 200, 255),    // Ice blue
            "Swamp" => (100, 150, 100),   // Dark green
            _ => (150, 150, 150)          // Gray
        };
    }

    private unsafe void RenderZoneList()
    {
        // Background
        SDL.SetRenderDrawColor(_renderer, 30, 32, 40, 255);
        var bg = new SDLRect { X = 0, Y = TopBarHeight, W = LeftPanelWidth, H = _windowHeight - TopBarHeight };
        SDL.RenderFillRect(_renderer, &bg);

        // Border
        SDL.SetRenderDrawColor(_renderer, 60, 65, 80, 255);
        var border = new SDLRect { X = LeftPanelWidth - 1, Y = TopBarHeight, W = 1, H = _windowHeight - TopBarHeight };
        SDL.RenderFillRect(_renderer, &border);

        // Header
        int y = TopBarHeight + 8;
        _font?.RenderText(_renderer, "ZONE BROWSER", 8, y, 1, 200, 180, 100, 255);
        y += 14;
        _font?.RenderText(_renderer, $"{_zonesWithScripts.Count} zones, {_totalActionCount} scripts", 8, y, 1, 120, 120, 140, 255);
        y += 16;

        // Separator
        SDL.SetRenderDrawColor(_renderer, 50, 55, 65, 255);
        var sep = new SDLRect { X = 5, Y = y, W = LeftPanelWidth - 15, H = 1 };
        SDL.RenderFillRect(_renderer, &sep);
        y += 8;

        int listStartY = y;
        int listEndY = _windowHeight - 70;
        int visibleHeight = listEndY - listStartY;

        // Build virtual list of items (planets + zones)
        var items = new List<(string type, string planet, int zoneId)>();
        foreach (var planet in _planetOrder)
        {
            if (!_zonesByPlanet.ContainsKey(planet) || _zonesByPlanet[planet].Count == 0)
                continue;
            items.Add(("planet", planet, -1));
            if (_planetExpanded.GetValueOrDefault(planet, true))
            {
                foreach (var zoneId in _zonesByPlanet[planet])
                    items.Add(("zone", planet, zoneId));
            }
        }

        // Handle scrolling
        int itemHeight = 22;
        int totalHeight = items.Count * itemHeight;
        int maxScroll = Math.Max(0, totalHeight - visibleHeight);
        _zoneListScrollOffset = Math.Clamp(_zoneListScrollOffset, 0, maxScroll);

        // Render visible items
        int scrollY = _zoneListScrollOffset;
        int itemY = listStartY - (scrollY % itemHeight);
        int startIdx = scrollY / itemHeight;

        for (int i = startIdx; i < items.Count && itemY < listEndY; i++)
        {
            var item = items[i];
            if (itemY + itemHeight < listStartY)
            {
                itemY += itemHeight;
                continue;
            }

            if (item.type == "planet")
            {
                // Planet header
                var color = GetPlanetColor(item.planet);
                bool expanded = _planetExpanded.GetValueOrDefault(item.planet, true);
                int count = _zonesByPlanet[item.planet].Count;

                SDL.SetRenderDrawColor(_renderer, 40, 45, 55, 255);
                var headerBg = new SDLRect { X = 3, Y = itemY, W = LeftPanelWidth - 18, H = itemHeight - 2 };
                SDL.RenderFillRect(_renderer, &headerBg);

                string arrow = expanded ? "v" : ">";
                _font?.RenderText(_renderer, arrow, 8, itemY + 5, 1, 150, 150, 150, 255);
                _font?.RenderText(_renderer, $"{item.planet} ({count})", 22, itemY + 5, 1, color.r, color.g, color.b, 255);
            }
            else
            {
                // Zone entry
                int zoneId = item.zoneId;
                var zone = _gameData.Zones[zoneId];
                bool isSelected = _zonesWithScripts.IndexOf(zoneId) == _selectedZoneIndex;
                bool isCurrent = zoneId == _state.CurrentZoneId;

                if (isSelected)
                {
                    SDL.SetRenderDrawColor(_renderer, 50, 80, 120, 255);
                    var sel = new SDLRect { X = 3, Y = itemY, W = LeftPanelWidth - 18, H = itemHeight - 2 };
                    SDL.RenderFillRect(_renderer, &sel);
                }

                byte r = 160, g = 160, b = 160;
                if (isSelected) { r = 255; g = 255; b = 255; }
                if (isCurrent) { r = 100; g = 255; b = 100; }

                string label = $"  Zone {zoneId}";
                if (zone.Actions.Count > 1)
                    label += $" ({zone.Actions.Count} scripts)";
                _font?.RenderText(_renderer, label, 12, itemY + 5, 1, r, g, b, 255);
            }
            itemY += itemHeight;
        }

        // Scrollbar
        if (totalHeight > visibleHeight)
        {
            int scrollbarHeight = Math.Max(30, (visibleHeight * visibleHeight) / totalHeight);
            int scrollbarY = listStartY + (_zoneListScrollOffset * (visibleHeight - scrollbarHeight)) / maxScroll;

            // Track
            SDL.SetRenderDrawColor(_renderer, 40, 42, 50, 255);
            var track = new SDLRect { X = LeftPanelWidth - 10, Y = listStartY, W = 6, H = visibleHeight };
            SDL.RenderFillRect(_renderer, &track);

            // Thumb
            SDL.SetRenderDrawColor(_renderer, _isDraggingScrollbar ? (byte)120 : (byte)80, _isDraggingScrollbar ? (byte)120 : (byte)85, _isDraggingScrollbar ? (byte)140 : (byte)100, 255);
            var thumb = new SDLRect { X = LeftPanelWidth - 10, Y = scrollbarY, W = 6, H = scrollbarHeight };
            SDL.RenderFillRect(_renderer, &thumb);
        }

        // Footer with keyboard hints
        int footerY = _windowHeight - 75;
        SDL.SetRenderDrawColor(_renderer, 35, 38, 48, 255);
        var footerBg = new SDLRect { X = 0, Y = footerY - 5, W = LeftPanelWidth, H = 80 };
        SDL.RenderFillRect(_renderer, &footerBg);

        _font?.RenderText(_renderer, "Click planet to collapse", 8, footerY, 1, 90, 90, 110, 255);
        footerY += 11;
        _font?.RenderText(_renderer, "G = Current zone", 8, footerY, 1, 90, 90, 110, 255);
        footerY += 11;
        _font?.RenderText(_renderer, "B = Bot's zone", 8, footerY, 1, 100, 180, 255, 255);
        footerY += 11;
        _font?.RenderText(_renderer, "T = TELEPORT here", 8, footerY, 1, 255, 180, 80, 255);
        footerY += 11;
        _font?.RenderText(_renderer, "E = Toggle edit mode", 8, footerY, 1, 255, 200, 100, 255);
        footerY += 11;
        if (_isEditMode)
        {
            _font?.RenderText(_renderer, "[EDIT MODE]", 8, footerY, 1, 100, 255, 100, 255);
            if (_hasUnsavedChanges)
            {
                footerY += 11;
                _font?.RenderText(_renderer, "*Unsaved changes*", 8, footerY, 1, 255, 200, 100, 255);
            }
        }
        else
        {
            _font?.RenderText(_renderer, "[VIEW MODE]", 8, footerY, 1, 150, 150, 180, 255);
        }
    }

    private unsafe void RenderTopBar()
    {
        // Background
        SDL.SetRenderDrawColor(_renderer, 35, 40, 50, 255);
        var bg = new SDLRect { X = 0, Y = 0, W = _windowWidth, H = TopBarHeight };
        SDL.RenderFillRect(_renderer, &bg);

        // Bottom border
        SDL.SetRenderDrawColor(_renderer, 50, 55, 65, 255);
        var borderLine = new SDLRect { X = 0, Y = TopBarHeight - 1, W = _windowWidth, H = 1 };
        SDL.RenderFillRect(_renderer, &borderLine);

        if (_zonesWithScripts.Count == 0) return;

        int zoneId = _zonesWithScripts[_selectedZoneIndex];
        var zone = _gameData.Zones[zoneId];
        bool isCurrentZone = zoneId == _state.CurrentZoneId;
        var planetColor = GetPlanetColor(zone.Planet.ToString());

        // Left side: Zone info
        int x = 10;
        int y = 8;

        // Zone header with planet color indicator
        SDL.SetRenderDrawColor(_renderer, planetColor.r, planetColor.g, planetColor.b, 255);
        var indicator = new SDLRect { X = x, Y = y + 2, W = 4, H = 12 };
        SDL.RenderFillRect(_renderer, &indicator);

        _font?.RenderText(_renderer, $"Zone {zoneId}", x + 10, y, 1, 255, 255, 255, 255);
        _font?.RenderText(_renderer, $"  ({zone.Planet}, {zone.Width}x{zone.Height})", x + 70, y, 1, planetColor.r, planetColor.g, planetColor.b, 255);

        y += 16;
        if (_isEditMode)
        {
            if (_editingConditionIndex >= 0)
            {
                _font?.RenderText(_renderer, $"Editing condition {_editingConditionIndex + 1} - Enter to save, ESC to cancel", x + 10, y, 1, 100, 255, 100, 255);
            }
            else if (_editingInstructionIndex >= 0)
            {
                _font?.RenderText(_renderer, $"Editing instruction {_editingInstructionIndex + 1} - Enter to save, ESC to cancel", x + 10, y, 1, 100, 255, 100, 255);
            }
            else
            {
                _font?.RenderText(_renderer, "EDIT MODE - Click on condition/instruction text to edit", x + 10, y, 1, 255, 200, 100, 255);
            }
        }
        else if (isCurrentZone)
        {
            _font?.RenderText(_renderer, "YOU ARE HERE - Highlights visible on map", x + 10, y, 1, 100, 255, 130, 255);
        }
        else
        {
            _font?.RenderText(_renderer, "Press T to teleport here", x + 10, y, 1, 255, 180, 80, 255);
        }

        // Right side: Script navigation (only if multiple scripts)
        if (zone.Actions.Count > 0)
        {
            int rightX = _windowWidth - 250;
            y = 8;

            if (zone.Actions.Count > 1)
            {
                // Script selector with arrow keys hint
                _font?.RenderText(_renderer, $"Script {_selectedActionIndex + 1} of {zone.Actions.Count}", rightX, y, 1, 200, 200, 255, 255);
                y += 14;
                _font?.RenderText(_renderer, "Use Left/Right arrows", rightX, y, 1, 120, 120, 150, 255);
            }
            else
            {
                _font?.RenderText(_renderer, "1 Script", rightX, y, 1, 150, 150, 180, 255);
            }

            // Show condition/instruction counts
            y += 14;
            var action = zone.Actions[_selectedActionIndex];
            _font?.RenderText(_renderer, $"{action.Conditions.Count} cond, {action.Instructions.Count} instr", rightX, y, 1, 100, 100, 130, 255);
        }
    }

    private unsafe void RenderActionContent()
    {
        if (_zonesWithScripts.Count == 0) return;

        int zoneId = _zonesWithScripts[_selectedZoneIndex];
        var zone = _gameData.Zones[zoneId];

        int contentX = LeftPanelWidth + 10;
        int contentY = TopBarHeight + 10;
        int contentWidth = _windowWidth - LeftPanelWidth - 20;
        int y = contentY - _scrollOffset * LineHeight;

        // Zone objects summary
        y = RenderSection(y, "ZONE OBJECTS", 255, 200, 100);
        y = RenderZoneObjects(zone, y, contentX);
        y += LineHeight;

        if (_selectedActionIndex >= zone.Actions.Count) return;

        var action = zone.Actions[_selectedActionIndex];

        // Conditions
        y = RenderSection(y, "CONDITIONS (all must be true)", 100, 255, 150);
        if (action.Conditions.Count == 0)
        {
            y = RenderLine(y, contentX, "(none - always executes)", 150, 150, 150);
        }
        else
        {
            for (int i = 0; i < action.Conditions.Count; i++)
            {
                y = RenderCondition(action.Conditions[i], y, contentX, i);
            }
        }
        y += LineHeight;

        // Instructions
        y = RenderSection(y, "INSTRUCTIONS", 255, 180, 100);
        for (int i = 0; i < action.Instructions.Count; i++)
        {
            y = RenderInstruction(action.Instructions[i], y, contentX, i);
        }
    }

    private unsafe int RenderSection(int y, string title, byte r, byte g, byte b)
    {
        int contentX = LeftPanelWidth + 10;
        if (y > 0 && y < _windowHeight)
        {
            SDL.SetRenderDrawColor(_renderer, 40, 45, 55, 255);
            var bg = new SDLRect { X = contentX - 5, Y = y - 2, W = _windowWidth - LeftPanelWidth - 10, H = LineHeight + 2 };
            SDL.RenderFillRect(_renderer, &bg);
            _font?.RenderText(_renderer, title, contentX, y, 1, r, g, b, 255);
        }
        return y + LineHeight + 4;
    }

    private unsafe int RenderLine(int y, int x, string text, byte r, byte g, byte b)
    {
        if (y > 0 && y < _windowHeight)
            _font?.RenderText(_renderer, text, x, y, 1, r, g, b, 255);
        return y + LineHeight;
    }

    private unsafe int RenderZoneObjects(Zone zone, int y, int x)
    {
        var doors = zone.Objects.Where(o => o.Type == ZoneObjectType.DoorEntrance || o.Type == ZoneObjectType.DoorExit || o.Type == ZoneObjectType.Lock).ToList();
        var npcs = zone.Objects.Where(o => o.Type == ZoneObjectType.PuzzleNPC).ToList();
        var items = zone.Objects.Where(o => o.Type == ZoneObjectType.CrateItem || o.Type == ZoneObjectType.CrateWeapon || o.Type == ZoneObjectType.LocatorItem).ToList();
        var triggers = zone.Objects.Where(o => o.Type == ZoneObjectType.Trigger).ToList();

        foreach (var door in doors)
        {
            string info = door.Type == ZoneObjectType.Lock
                ? $"LOCK at ({door.X},{door.Y}) - needs item #{door.Argument}"
                : $"DOOR at ({door.X},{door.Y}) -> Zone {door.Argument}";
            y = RenderLine(y, x + 10, info, 100, 255, 100);
        }

        foreach (var npc in npcs)
        {
            string name = GetCharacterName(npc.Argument);
            y = RenderLine(y, x + 10, $"NPC at ({npc.X},{npc.Y}): {name}", 255, 100, 255);
        }

        foreach (var item in items)
        {
            string name = GetTileName(item.Argument);
            y = RenderLine(y, x + 10, $"ITEM at ({item.X},{item.Y}): {name}", 255, 180, 100);
        }

        foreach (var trigger in triggers)
        {
            y = RenderLine(y, x + 10, $"TRIGGER at ({trigger.X},{trigger.Y})", 100, 180, 255);
        }

        if (doors.Count == 0 && npcs.Count == 0 && items.Count == 0 && triggers.Count == 0)
        {
            y = RenderLine(y, x + 10, "(no interactive objects)", 120, 120, 120);
        }

        return y;
    }

    private unsafe int RenderCondition(Condition cond, int y, int x, int condIndex = -1)
    {
        string text = FormatCondition(cond);

        // Highlight if this is the item being edited
        bool isEditing = _isEditMode && _editingConditionIndex == condIndex;
        if (isEditing && y > 0 && y < _windowHeight)
        {
            SDL.SetRenderDrawColor(_renderer, 60, 80, 60, 255);
            var editBg = new SDLRect { X = x, Y = y - 2, W = _windowWidth - x - 10, H = LineHeight + 2 };
            SDL.RenderFillRect(_renderer, &editBg);
        }
        else if (_isEditMode && condIndex >= 0 && y > 0 && y < _windowHeight)
        {
            // Hover highlight in edit mode
            SDL.SetRenderDrawColor(_renderer, 40, 45, 50, 255);
            var hoverBg = new SDLRect { X = x, Y = y - 2, W = _windowWidth - x - 10, H = LineHeight + 2 };
            SDL.RenderFillRect(_renderer, &hoverBg);
        }

        if (isEditing)
        {
            // Show edit box for condition text (if applicable)
            _font?.RenderText(_renderer, $"IF {text}", x + 10, y, 1, 150, 255, 150, 255);
            y += LineHeight;

            // Show text edit box if condition has text
            if (y > 0 && y < _windowHeight)
            {
                SDL.SetRenderDrawColor(_renderer, 50, 60, 70, 255);
                var editBox = new SDLRect { X = x + 20, Y = y - 2, W = _windowWidth - x - 40, H = LineHeight + 4 };
                SDL.RenderFillRect(_renderer, &editBox);
                SDL.SetRenderDrawColor(_renderer, 100, 200, 150, 255);
                SDL.RenderDrawRect(_renderer, &editBox);

                string displayText = _editBuffer.Length > 0 ? _editBuffer : "(enter text)";
                _font?.RenderText(_renderer, displayText, x + 25, y, 1, 255, 255, 255, 255);

                // Render cursor
                if ((DateTime.Now.Millisecond / 500) % 2 == 0)
                {
                    int cursorX = x + 25 + (_font?.GetTextWidth(_editBuffer.Substring(0, _editCursorPos)) ?? 0);
                    SDL.SetRenderDrawColor(_renderer, 255, 255, 255, 255);
                    var cursor = new SDLRect { X = cursorX, Y = y, W = 1, H = 12 };
                    SDL.RenderFillRect(_renderer, &cursor);
                }
            }
            return y + LineHeight;
        }

        return RenderLine(y, x + 10, $"IF {text}", 150, 255, 150);
    }

    private unsafe int RenderInstruction(Instruction instr, int y, int x, int instrIndex = -1)
    {
        var (text, color) = FormatInstruction(instr);

        // Highlight if this is the item being edited
        bool isEditing = _isEditMode && _editingInstructionIndex == instrIndex;
        if (isEditing && y > 0 && y < _windowHeight)
        {
            SDL.SetRenderDrawColor(_renderer, 60, 80, 60, 255);
            var editBg = new SDLRect { X = x, Y = y - 2, W = _windowWidth - x - 10, H = LineHeight + 2 };
            SDL.RenderFillRect(_renderer, &editBg);
        }
        else if (_isEditMode && instrIndex >= 0 && y > 0 && y < _windowHeight)
        {
            // Hover highlight in edit mode
            SDL.SetRenderDrawColor(_renderer, 40, 45, 50, 255);
            var hoverBg = new SDLRect { X = x, Y = y - 2, W = _windowWidth - x - 10, H = LineHeight + 2 };
            SDL.RenderFillRect(_renderer, &hoverBg);
        }

        y = RenderLine(y, x + 10, text, color.r, color.g, color.b);

        // Render dialogue text on separate lines (or edit box if editing)
        if (isEditing)
        {
            // Show edit box
            if (y > 0 && y < _windowHeight)
            {
                SDL.SetRenderDrawColor(_renderer, 50, 60, 70, 255);
                var editBox = new SDLRect { X = x + 20, Y = y - 2, W = _windowWidth - x - 40, H = LineHeight + 4 };
                SDL.RenderFillRect(_renderer, &editBox);
                SDL.SetRenderDrawColor(_renderer, 100, 150, 200, 255);
                SDL.RenderDrawRect(_renderer, &editBox);

                // Render edit buffer with cursor
                string displayText = _editBuffer;
                if (_editBuffer.Length == 0) displayText = "(enter text)";
                _font?.RenderText(_renderer, displayText, x + 25, y, 1, 255, 255, 255, 255);

                // Render cursor (blinking)
                if ((DateTime.Now.Millisecond / 500) % 2 == 0)
                {
                    int cursorX = x + 25 + (_font?.GetTextWidth(_editBuffer.Substring(0, _editCursorPos)) ?? 0);
                    SDL.SetRenderDrawColor(_renderer, 255, 255, 255, 255);
                    var cursor = new SDLRect { X = cursorX, Y = y, W = 1, H = 12 };
                    SDL.RenderFillRect(_renderer, &cursor);
                }
            }
            y += LineHeight;
        }
        else if (!string.IsNullOrEmpty(instr.Text))
        {
            var lines = WordWrap(instr.Text, 70);
            foreach (var line in lines)
            {
                y = RenderLine(y, x + 30, $"\"{line}\"", 180, 180, 255);
            }
        }

        return y;
    }

    private string FormatCondition(Condition cond)
    {
        var a = cond.Arguments;
        return cond.Opcode switch
        {
            ConditionOpcode.ZoneNotInitialized => "zone not initialized (first visit)",
            ConditionOpcode.ZoneEntered => "zone just entered",
            ConditionOpcode.Bump => $"player bumps ({Arg(a,0)},{Arg(a,1)}) with tile {GetTileName(Arg(a,2))}",
            ConditionOpcode.PlacedItemIs => $"item {GetTileName(Arg(a,3))} placed at ({Arg(a,0)},{Arg(a,1)})",
            ConditionOpcode.StandingOn => $"hero at ({Arg(a,0)},{Arg(a,1)}) on floor {GetTileName(Arg(a,2))}",
            ConditionOpcode.CounterIs => $"counter == {Arg(a,0)}",
            ConditionOpcode.RandomIs => $"random == {Arg(a,0)}",
            ConditionOpcode.RandomIsGreaterThan => $"random > {Arg(a,0)}",
            ConditionOpcode.RandomIsLessThan => $"random < {Arg(a,0)}",
            ConditionOpcode.EnterByPlane => "entered by X-Wing",
            ConditionOpcode.TileAtIs => $"tile at ({Arg(a,0)},{Arg(a,1)}) layer {Arg(a,2)} == {GetTileName(Arg(a,3))}",
            ConditionOpcode.MonsterIsDead => $"monster #{Arg(a,0)} is dead",
            ConditionOpcode.HasNoActiveMonsters => "all monsters dead",
            ConditionOpcode.HasItem => Arg(a,0) == -1 ? "has zone's puzzle item" : $"has item {GetTileName(Arg(a,0))}",
            ConditionOpcode.RequiredItemIs => $"required item == {GetTileName(Arg(a,0))}",
            ConditionOpcode.EndingIs => $"goal item == {Arg(a,0)}",
            ConditionOpcode.ZoneIsSolved => "zone is solved",
            ConditionOpcode.NoItemPlaced => $"no item placed at ({Arg(a,0)},{Arg(a,1)})",
            ConditionOpcode.HasGoalItem => "has the goal item",
            ConditionOpcode.HealthIsLessThan => $"health < {Arg(a,0)}",
            ConditionOpcode.HealthIsGreaterThan => $"health > {Arg(a,0)}",
            ConditionOpcode.FindItemIs => $"zone find item == {GetTileName(Arg(a,0))}",
            ConditionOpcode.PlacedItemIsNot => $"placed item != {GetTileName(Arg(a,3))} at ({Arg(a,0)},{Arg(a,1)})",
            ConditionOpcode.HeroIsAt => $"hero at ({Arg(a,0)},{Arg(a,1)})",
            ConditionOpcode.SectorCounterIs => $"sector counter == {Arg(a,0)}",
            ConditionOpcode.SectorCounterIsLessThan => $"sector counter < {Arg(a,0)}",
            ConditionOpcode.SectorCounterIsGreaterThan => $"sector counter > {Arg(a,0)}",
            ConditionOpcode.GamesWonIs => $"games won == {Arg(a,0)}",
            ConditionOpcode.DropsQuestItemAt => $"drops quest item at ({Arg(a,0)},{Arg(a,1)})",
            ConditionOpcode.HasAnyRequiredItem => "has any required item",
            ConditionOpcode.CounterIsNot => $"counter != {Arg(a,0)}",
            ConditionOpcode.RandomIsNot => $"random != {Arg(a,0)}",
            ConditionOpcode.SectorCounterIsNot => $"sector counter != {Arg(a,0)}",
            ConditionOpcode.IsVariable => $"var at ({Arg(a,0)},{Arg(a,1)},{Arg(a,2)}) == {Arg(a,3)}",
            ConditionOpcode.GamesWonIsGreaterThan => $"games won > {Arg(a,0)}",
            ConditionOpcode.CounterIsGreaterThan => $"counter > {Arg(a,0)}",
            ConditionOpcode.CounterIsLessThan => $"counter < {Arg(a,0)}",
            ConditionOpcode.DroppedItemIs => $"dropped item == {GetTileName(Arg(a,0))}",
            ConditionOpcode.Unused => "(unused condition)",
            _ => $"OPCODE_{(int)cond.Opcode:X2}({string.Join(",", a)})"
        };
    }

    private (string text, (byte r, byte g, byte b) color) FormatInstruction(Instruction instr)
    {
        var a = instr.Arguments;
        (byte r, byte g, byte b) normal = (255, 200, 150);
        (byte r, byte g, byte b) important = (50, 255, 50);
        (byte r, byte g, byte b) speech = (200, 200, 255);

        string text = instr.Opcode switch
        {
            InstructionOpcode.PlaceTile => $"PLACE {GetTileName(Arg(a,3))} at ({Arg(a,0)},{Arg(a,1)}) layer {Arg(a,2)}",
            InstructionOpcode.RemoveTile => $"REMOVE tile at ({Arg(a,0)},{Arg(a,1)}) layer {Arg(a,2)}",
            InstructionOpcode.MoveTile => $"MOVE tile from ({Arg(a,0)},{Arg(a,1)}) to ({Arg(a,3)},{Arg(a,4)})",
            InstructionOpcode.DrawTile => $"DRAW {GetTileName(Arg(a,3))} at ({Arg(a,0)},{Arg(a,1)})",
            InstructionOpcode.SpeakHero => "LUKE SAYS:",
            InstructionOpcode.SpeakNpc => $"NPC at ({Arg(a,0)},{Arg(a,1)}) SAYS:",
            InstructionOpcode.SetTileNeedsDisplay => $"REDRAW tile at ({Arg(a,0)},{Arg(a,1)})",
            InstructionOpcode.SetRectNeedsDisplay => $"REDRAW rect ({Arg(a,0)},{Arg(a,1)}) {Arg(a,2)}x{Arg(a,3)}",
            InstructionOpcode.Wait => "WAIT one tick",
            InstructionOpcode.Redraw => "REDRAW screen",
            InstructionOpcode.PlaySound => $"PLAY sound #{Arg(a,0)}",
            InstructionOpcode.StopSound => "STOP sound",
            InstructionOpcode.RollDice => $"ROLL random 1-{Arg(a,0)}",
            InstructionOpcode.SetCounter => $"SET counter = {Arg(a,0)}",
            InstructionOpcode.AddToCounter => $"ADD {Arg(a,0)} to counter",
            InstructionOpcode.SetVariable => $"SET var at ({Arg(a,0)},{Arg(a,1)},{Arg(a,2)}) = {Arg(a,3)}",
            InstructionOpcode.HideHero => "HIDE Luke",
            InstructionOpcode.ShowHero => "SHOW Luke",
            InstructionOpcode.MoveHeroTo => $"TELEPORT Luke to ({Arg(a,0)},{Arg(a,1)})",
            InstructionOpcode.MoveHeroBy => $"MOVE Luke by ({Arg(a,0)},{Arg(a,1)})",
            InstructionOpcode.DisableAction => "DISABLE this action",
            InstructionOpcode.EnableHotspot => $"ENABLE hotspot #{Arg(a,0)}",
            InstructionOpcode.DisableHotspot => $"DISABLE hotspot #{Arg(a,0)}",
            InstructionOpcode.EnableMonster => $"SPAWN monster #{Arg(a,0)}",
            InstructionOpcode.DisableMonster => $"DESPAWN monster #{Arg(a,0)}",
            InstructionOpcode.EnableAllMonsters => "SPAWN all monsters",
            InstructionOpcode.DisableAllMonsters => "DESPAWN all monsters",
            InstructionOpcode.DropItem => Arg(a,0) == -1
                ? $"DROP zone's find item at ({Arg(a,1)},{Arg(a,2)})"
                : $"DROP {GetTileName(Arg(a,0))} at ({Arg(a,1)},{Arg(a,2)})",
            InstructionOpcode.AddItem => $"GIVE player {GetTileName(Arg(a,0))}",
            InstructionOpcode.RemoveItem => $"TAKE {GetTileName(Arg(a,0))} from player",
            InstructionOpcode.MarkAsSolved => "*** MARK ZONE SOLVED ***",
            InstructionOpcode.WinGame => "*** WIN GAME ***",
            InstructionOpcode.LoseGame => "*** LOSE GAME ***",
            InstructionOpcode.ChangeZone => $"GO TO Zone {Arg(a,0)} at ({Arg(a,1)},{Arg(a,2)})",
            InstructionOpcode.SetSectorCounter => $"SET sector counter = {Arg(a,0)}",
            InstructionOpcode.AddToSectorCounter => $"ADD {Arg(a,0)} to sector counter",
            InstructionOpcode.SetRandom => $"SET random = {Arg(a,0)}",
            InstructionOpcode.AddHealth => $"HEAL +{Arg(a,0)}",
            InstructionOpcode.SubtractHealth => $"DAMAGE -{Arg(a,0)}",
            InstructionOpcode.SetHealth => $"SET health = {Arg(a,0)}",
            InstructionOpcode.SpeakNpc2 => $"NPC2 at ({Arg(a,0)},{Arg(a,1)}) SAYS:",
            _ => $"OPCODE_{(int)instr.Opcode:X2}({string.Join(",", a)})"
        };

        var color = instr.Opcode switch
        {
            InstructionOpcode.MarkAsSolved or InstructionOpcode.WinGame => important,
            InstructionOpcode.LoseGame => ((byte)255, (byte)80, (byte)80),
            InstructionOpcode.SpeakHero or InstructionOpcode.SpeakNpc or InstructionOpcode.SpeakNpc2 => speech,
            InstructionOpcode.ChangeZone => ((byte)150, (byte)200, (byte)255),
            InstructionOpcode.AddItem or InstructionOpcode.DropItem => ((byte)255, (byte)220, (byte)100),
            InstructionOpcode.RemoveItem => ((byte)255, (byte)150, (byte)100),
            _ => normal
        };

        return (text, color);
    }

    private short Arg(List<short> args, int index) => index < args.Count ? args[index] : (short)0;

    private string GetTileName(int id)
    {
        if (id < 0) return $"[{id}]";
        if (id >= _gameData.Tiles.Count) return $"#{id}";
        if (_gameData.TileNames.TryGetValue(id, out var name)) return $"\"{name}\"";
        var tile = _gameData.Tiles[id];
        if (tile.IsItem) return $"[Item #{id}]";
        if (tile.IsWeapon) return $"[Weapon #{id}]";
        return $"#{id}";
    }

    private string GetCharacterName(int id)
    {
        if (id < 0 || id >= _gameData.Characters.Count) return $"#{id}";
        var ch = _gameData.Characters[id];
        return ch.Name ?? $"#{id}";
    }

    private List<string> WordWrap(string text, int maxLen)
    {
        var result = new List<string>();
        text = text.Replace("\n", " ").Replace("\r", "");
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var line = "";
        foreach (var word in words)
        {
            if (line.Length + word.Length + 1 > maxLen)
            {
                if (line.Length > 0) result.Add(line);
                line = word;
            }
            else
            {
                line += (line.Length > 0 ? " " : "") + word;
            }
        }
        if (line.Length > 0) result.Add(line);
        return result;
    }

    public void Dispose() => Close();
}
