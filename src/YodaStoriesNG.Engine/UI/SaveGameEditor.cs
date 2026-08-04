using Hexa.NET.SDL2;
using YodaStoriesNG.Engine;
using YodaStoriesNG.Engine.Game;
using YodaStoriesNG.Engine.Rendering;

namespace YodaStoriesNG.Engine.UI;

/// <summary>
/// Debug window for editing saved game state.
/// </summary>
public unsafe class SaveGameEditor
{
    private SDLWindow* _window;
    private SDLRenderer* _renderer;
    private BitmapFont? _font;
    private bool _isOpen;

    private const int WindowWidth = 600;
    private const int WindowHeight = 700;

    // State
    private List<SaveFileInfo> _saveFiles = new();
    private int _selectedFileIndex = -1;
    private SaveGameData? _loadedSave;
    private int _scrollOffset = 0;
    private int _maxScroll = 0;

    // Tabs
    private enum Tab { Overview, Inventory, World, Zones, Variables }
    private Tab _currentTab = Tab.Overview;

    // Editing state
    private bool _isEditing = false;
    private string _editField = "";
    private string _editBuffer = "";
    private int _editCursorPos = 0;
    private bool _hasUnsavedChanges = false;
    private int _editFieldIndex = -1;  // For list items
    private List<EditableField> _currentFields = new();

    // Events for game integration
    public event Action<string>? OnLoadSaveFile;
    public event Action<SaveGameData>? OnApplySaveData;

    public bool IsOpen => _isOpen;

    private record EditableField(string Name, int Y, int FieldIndex, Func<string> GetValue, Action<string> SetValue);

    public void Open()
    {
        if (_isOpen) return;

        _window = SDL.CreateWindow(
            "Save Game Editor",
            100, 100,
            WindowWidth, WindowHeight,
            (uint)(SDLWindowFlags.Shown | SDLWindowFlags.Resizable));

        if (_window == null)
        {
            Console.WriteLine("Failed to create Save Game Editor window");
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

        _isOpen = true;
        RefreshSaveFiles();
    }

    public void Close()
    {
        if (!_isOpen) return;

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

    private void RefreshSaveFiles()
    {
        _saveFiles = SaveGameManager.ListSaveFiles()
            .OrderByDescending(f => f.LastModified)
            .ToList();
        _selectedFileIndex = _saveFiles.Count > 0 ? 0 : -1;
        _loadedSave = null;

        if (_selectedFileIndex >= 0)
        {
            LoadSelectedSave();
        }
    }

    private void LoadSelectedSave()
    {
        if (_selectedFileIndex < 0 || _selectedFileIndex >= _saveFiles.Count)
            return;

        var saveInfo = _saveFiles[_selectedFileIndex];
        _loadedSave = SaveGameManager.LoadGame(saveInfo.FilePath);
        _scrollOffset = 0;
    }

    public bool HandleEvent(SDLEvent* evt)
    {
        if (!_isOpen || _window == null) return false;

        uint windowId = SDL.GetWindowID(_window);

        if (evt->Type == (uint)SDLEventType.Windowevent &&
            evt->Window.WindowID == windowId &&
            evt->Window.Event == (byte)SDLWindowEventID.Close)
        {
            Close();
            return true;
        }

        if (evt->Type == (uint)SDLEventType.Keydown && evt->Key.WindowID == windowId)
        {
            var key = evt->Key.Keysym.Sym;

            // Handle editing mode input
            if (_isEditing)
            {
                return HandleEditInput(key, evt->Key.Keysym.Mod);
            }

            // Tab switching with number keys
            if (key >= '1' && key <= '5')
            {
                _currentTab = (Tab)(key - '1');
                _scrollOffset = 0;
                _currentFields.Clear();
                return true;
            }

            // File selection with up/down
            if (key == (int)SDLKeyCode.Up && _selectedFileIndex > 0)
            {
                _selectedFileIndex--;
                LoadSelectedSave();
                return true;
            }
            if (key == (int)SDLKeyCode.Down && _selectedFileIndex < _saveFiles.Count - 1)
            {
                _selectedFileIndex++;
                LoadSelectedSave();
                return true;
            }

            // Scroll with Page Up/Down
            if (key == (int)SDLKeyCode.Pageup)
            {
                _scrollOffset = Math.Max(0, _scrollOffset - 10);
                return true;
            }
            if (key == (int)SDLKeyCode.Pagedown)
            {
                _scrollOffset = Math.Min(_maxScroll, _scrollOffset + 10);
                return true;
            }

            // Refresh with F5
            if (key == (int)SDLKeyCode.F5)
            {
                RefreshSaveFiles();
                return true;
            }

            // Save with Ctrl+S
            if (key == 's' && (evt->Key.Keysym.Mod & (ushort)SDLKeymod.Ctrl) != 0)
            {
                SaveCurrentFile();
                return true;
            }

            // Escape to close
            if (key == 27)
            {
                Close();
                return true;
            }
        }

        if (evt->Type == (uint)SDLEventType.Mousewheel && evt->Wheel.WindowID == windowId)
        {
            _scrollOffset = Math.Clamp(_scrollOffset - evt->Wheel.Y * 3, 0, Math.Max(0, _maxScroll));
            return true;
        }

        if (evt->Type == (uint)SDLEventType.Mousebuttondown && evt->Button.WindowID == windowId)
        {
            int mx = evt->Button.X;
            int my = evt->Button.Y;

            // Tab bar clicks (y < 30)
            if (my < 30)
            {
                int tabWidth = WindowWidth / 5;
                int tabIndex = mx / tabWidth;
                if (tabIndex >= 0 && tabIndex < 5)
                {
                    _currentTab = (Tab)tabIndex;
                    _scrollOffset = 0;
                }
                return true;
            }

            // File list clicks (left panel, x < 150)
            if (mx < 150 && my >= 30 && my < WindowHeight - 30)
            {
                int fileIndex = (my - 30) / 18;
                if (fileIndex >= 0 && fileIndex < _saveFiles.Count)
                {
                    _selectedFileIndex = fileIndex;
                    LoadSelectedSave();
                }
                return true;
            }

            // Content area click - check for editable fields
            if (mx >= 155 && my >= 35 && my < WindowHeight - 30)
            {
                // Find clicked field
                foreach (var field in _currentFields)
                {
                    if (my >= field.Y - 2 && my < field.Y + 14)
                    {
                        StartEditing(field);
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private void StartEditing(EditableField field)
    {
        _isEditing = true;
        _editField = field.Name;
        _editBuffer = field.GetValue();
        _editCursorPos = _editBuffer.Length;
        _editFieldIndex = field.FieldIndex;
    }

    private bool HandleEditInput(int key, ushort mod)
    {
        if (key == 27) // Escape - cancel
        {
            _isEditing = false;
            _editBuffer = "";
            return true;
        }

        if (key == 13 || key == 1073741912) // Enter - apply
        {
            ApplyEdit();
            return true;
        }

        if (key == 8) // Backspace
        {
            if (_editCursorPos > 0 && _editBuffer.Length > 0)
            {
                _editBuffer = _editBuffer.Remove(_editCursorPos - 1, 1);
                _editCursorPos--;
            }
            return true;
        }

        if (key == 127) // Delete
        {
            if (_editCursorPos < _editBuffer.Length)
            {
                _editBuffer = _editBuffer.Remove(_editCursorPos, 1);
            }
            return true;
        }

        if (key == 1073741904) // Left
        {
            if (_editCursorPos > 0) _editCursorPos--;
            return true;
        }

        if (key == 1073741903) // Right
        {
            if (_editCursorPos < _editBuffer.Length) _editCursorPos++;
            return true;
        }

        // Printable characters
        if (key >= 32 && key <= 126)
        {
            char ch = (char)key;
            bool shift = (mod & (ushort)SDLKeymod.Shift) != 0;
            if (shift && key >= 'a' && key <= 'z')
                ch = (char)(key - 32);

            _editBuffer = _editBuffer.Insert(_editCursorPos, ch.ToString());
            _editCursorPos++;
            return true;
        }

        return true;
    }

    private void ApplyEdit()
    {
        if (_loadedSave == null)
        {
            _isEditing = false;
            return;
        }

        // Find the field and apply the new value
        var field = _currentFields.FirstOrDefault(f => f.Name == _editField && f.FieldIndex == _editFieldIndex);
        if (field != null)
        {
            try
            {
                field.SetValue(_editBuffer);
                _hasUnsavedChanges = true;
                Console.WriteLine($"[SaveEditor] Set {_editField} = {_editBuffer}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SaveEditor] Failed to set {_editField}: {ex.Message}");
            }
        }

        _isEditing = false;
        _editBuffer = "";
    }

    private void SaveCurrentFile()
    {
        if (_loadedSave == null || _selectedFileIndex < 0 || _selectedFileIndex >= _saveFiles.Count)
            return;

        var filePath = _saveFiles[_selectedFileIndex].FilePath;
        if (SaveGameManager.SaveGameData(filePath, _loadedSave))
        {
            _hasUnsavedChanges = false;
            Console.WriteLine($"[SaveEditor] Saved to {filePath}");
        }
        else
        {
            Console.WriteLine($"[SaveEditor] Failed to save to {filePath}");
        }
    }

    public void Render()
    {
        if (!_isOpen || _renderer == null || _font == null) return;

        // Clear
        SDL.SetRenderDrawColor(_renderer, 30, 30, 35, 255);
        SDL.RenderClear(_renderer);

        // Tab bar
        RenderTabBar();

        // File list (left panel)
        RenderFileList();

        // Content area (right panel)
        RenderContent();

        // Status bar
        RenderStatusBar();

        Dev.Capture.Flush(_renderer, "save-editor");
        SDL.RenderPresent(_renderer);
    }

    private void RenderTabBar()
    {
        int tabWidth = WindowWidth / 5;
        string[] tabNames = { "Overview", "Inventory", "World", "Zones", "Variables" };

        for (int i = 0; i < 5; i++)
        {
            bool selected = (int)_currentTab == i;

            // Tab background
            SDL.SetRenderDrawColor(_renderer, selected ? (byte)60 : (byte)45, selected ? (byte)65 : (byte)48, selected ? (byte)75 : (byte)55, 255);
            var tabRect = new SDLRect { X = i * tabWidth, Y = 0, W = tabWidth - 1, H = 28 };
            SDL.RenderFillRect(_renderer, &tabRect);

            // Tab text
            byte textColor = selected ? (byte)255 : (byte)180;
            _font!.RenderText(_renderer, $"{i + 1}:{tabNames[i]}", i * tabWidth + 5, 8, 1, textColor, textColor, textColor, 255);
        }
    }

    private void RenderFileList()
    {
        // Panel background
        SDL.SetRenderDrawColor(_renderer, 40, 42, 48, 255);
        var panelRect = new SDLRect { X = 0, Y = 30, W = 148, H = WindowHeight - 60 };
        SDL.RenderFillRect(_renderer, &panelRect);

        // Border
        SDL.SetRenderDrawColor(_renderer, 60, 65, 75, 255);
        SDL.RenderDrawRect(_renderer, &panelRect);

        _font!.RenderText(_renderer, "Save Files:", 5, 35, 1, 200, 200, 200, 255);

        int y = 55;
        for (int i = 0; i < _saveFiles.Count && y < WindowHeight - 50; i++)
        {
            bool selected = i == _selectedFileIndex;

            if (selected)
            {
                SDL.SetRenderDrawColor(_renderer, 70, 100, 140, 255);
                var highlightRect = new SDLRect { X = 2, Y = y - 2, W = 144, H = 16 };
                SDL.RenderFillRect(_renderer, &highlightRect);
            }

            byte textColor = selected ? (byte)255 : (byte)180;
            string fileName = _saveFiles[i].FileName;
            string displayName = fileName.Length > 18 ? fileName.Substring(0, 15) + "..." : fileName;
            _font.RenderText(_renderer, displayName, 5, y, 1, textColor, textColor, textColor, 255);
            y += 18;
        }

        if (_saveFiles.Count == 0)
        {
            _font.RenderText(_renderer, "(No saves)", 5, 55, 1, 150, 150, 150, 255);
        }
    }

    private void RenderContent()
    {
        int contentX = 155;
        int contentY = 35;
        int contentWidth = WindowWidth - contentX - 5;
        int contentHeight = WindowHeight - contentY - 35;

        // Content background
        SDL.SetRenderDrawColor(_renderer, 35, 37, 42, 255);
        var contentRect = new SDLRect { X = contentX, Y = contentY, W = contentWidth, H = contentHeight };
        SDL.RenderFillRect(_renderer, &contentRect);

        if (_loadedSave == null)
        {
            _font!.RenderText(_renderer, "Select a save file", contentX + 10, contentY + 10, 1, 150, 150, 150, 255);
            return;
        }

        // Set clip rect for scrolling
        SDL.RenderSetClipRect(_renderer, &contentRect);

        int y = contentY + 5 - _scrollOffset;
        int lineHeight = 16;
        int startY = y;

        switch (_currentTab)
        {
            case Tab.Overview:
                y = RenderOverviewTab(contentX + 5, y, lineHeight);
                break;
            case Tab.Inventory:
                y = RenderInventoryTab(contentX + 5, y, lineHeight);
                break;
            case Tab.World:
                y = RenderWorldTab(contentX + 5, y, lineHeight);
                break;
            case Tab.Zones:
                y = RenderZonesTab(contentX + 5, y, lineHeight);
                break;
            case Tab.Variables:
                y = RenderVariablesTab(contentX + 5, y, lineHeight);
                break;
        }

        _maxScroll = Math.Max(0, (y - startY) - contentHeight + 20);

        // Clear clip rect
        SDL.RenderSetClipRect(_renderer, null);
    }

    private int RenderOverviewTab(int x, int y, int lh)
    {
        var save = _loadedSave!;
        _currentFields.Clear();

        RenderSection(ref y, lh, "Save Info");
        RenderLine(ref y, x, lh, $"Version: {save.Version}");
        RenderLine(ref y, x, lh, $"Saved: {save.SaveTime:yyyy-MM-dd HH:mm:ss}");
        y += lh;

        RenderSection(ref y, lh, "Player (Click to Edit)");
        RenderEditableLine(ref y, x, lh, "PlayerX", save.PlayerX.ToString(),
            () => save.PlayerX.ToString(),
            v => { if (int.TryParse(v, out var val)) save.PlayerX = val; });
        RenderEditableLine(ref y, x, lh, "PlayerY", save.PlayerY.ToString(),
            () => save.PlayerY.ToString(),
            v => { if (int.TryParse(v, out var val)) save.PlayerY = val; });
        RenderEditableLine(ref y, x, lh, "Health", save.Health.ToString(),
            () => save.Health.ToString(),
            v => { if (int.TryParse(v, out var val)) save.Health = val; });
        RenderEditableLine(ref y, x, lh, "MaxHealth", save.MaxHealth.ToString(),
            () => save.MaxHealth.ToString(),
            v => { if (int.TryParse(v, out var val)) save.MaxHealth = val; });
        RenderEditableLine(ref y, x, lh, "CurrentZone", save.CurrentZoneId.ToString(),
            () => save.CurrentZoneId.ToString(),
            v => { if (int.TryParse(v, out var val)) save.CurrentZoneId = val; });
        RenderEditableLine(ref y, x, lh, "HasLocator", save.HasLocator.ToString(),
            () => save.HasLocator.ToString(),
            v => { save.HasLocator = v.ToLower() == "true" || v == "1"; });
        y += lh;

        RenderSection(ref y, lh, "Progress");
        RenderEditableLine(ref y, x, lh, "GamesWon", save.GamesWon.ToString(),
            () => save.GamesWon.ToString(),
            v => { if (int.TryParse(v, out var val)) save.GamesWon = val; });
        RenderLine(ref y, x, lh, $"Zones Visited: {save.VisitedZones?.Count ?? 0}");
        RenderLine(ref y, x, lh, $"Zones Solved: {save.SolvedZones?.Count ?? 0}");
        RenderLine(ref y, x, lh, $"Objects Collected: {save.CollectedObjects?.Count ?? 0}");
        y += lh;

        if (save.WorldData != null)
        {
            RenderSection(ref y, lh, "Mission");
            RenderLine(ref y, x, lh, $"Planet: {save.WorldData.Planet}");
            RenderLine(ref y, x, lh, $"Mission #: {save.WorldData.MissionNumber}");
        }

        if (save.MissionData != null)
        {
            RenderLine(ref y, x, lh, $"Name: {save.MissionData.Name ?? "Unknown"}");
            RenderEditableLine(ref y, x, lh, "MissionStep", save.MissionData.CurrentStep.ToString(),
                () => save.MissionData.CurrentStep.ToString(),
                v => { if (int.TryParse(v, out var val)) save.MissionData.CurrentStep = val; });
            RenderLine(ref y, x, lh, $"Completed: {save.MissionData.IsCompleted}");
        }

        return y;
    }

    private void RenderEditableLine(ref int y, int x, int lh, string name, string value, Func<string> getter, Action<string> setter, int fieldIndex = -1)
    {
        bool isCurrentEdit = _isEditing && _editField == name && _editFieldIndex == fieldIndex;

        // Register as editable field
        _currentFields.Add(new EditableField(name, y, fieldIndex, getter, setter));

        // Calculate label width for proper value positioning
        int labelWidth = Math.Min(130, _font!.GetTextWidth(name + ":"));
        int valueX = x + labelWidth + 5;

        if (isCurrentEdit)
        {
            // Render edit box
            SDL.SetRenderDrawColor(_renderer, 50, 60, 70, 255);
            var editBox = new SDLRect { X = valueX - 2, Y = y - 2, W = 150, H = lh + 2 };
            SDL.RenderFillRect(_renderer, &editBox);
            SDL.SetRenderDrawColor(_renderer, 100, 150, 200, 255);
            SDL.RenderDrawRect(_renderer, &editBox);

            _font.RenderText(_renderer, $"{name}:", x, y, 1, 100, 180, 255, 255);
            _font.RenderText(_renderer, _editBuffer, valueX, y, 1, 255, 255, 255, 255);

            // Cursor
            if ((DateTime.Now.Millisecond / 500) % 2 == 0)
            {
                int cursorX = valueX + (_font.GetTextWidth(_editBuffer.Substring(0, _editCursorPos)));
                SDL.SetRenderDrawColor(_renderer, 255, 255, 255, 255);
                var cursor = new SDLRect { X = cursorX, Y = y, W = 1, H = 12 };
                SDL.RenderFillRect(_renderer, &cursor);
            }
        }
        else
        {
            // Render as clickable field
            _font.RenderText(_renderer, $"{name}:", x, y, 1, 100, 180, 255, 255);
            _font.RenderText(_renderer, value, valueX, y, 1, 255, 220, 100, 255);  // Yellow = editable
        }
        y += lh;
    }

    private int RenderInventoryTab(int x, int y, int lh)
    {
        var save = _loadedSave!;
        _currentFields.Clear();

        RenderSection(ref y, lh, $"Inventory ({save.Inventory?.Count ?? 0} items) - Click to edit");
        if (save.Inventory != null)
        {
            for (int i = 0; i < save.Inventory.Count; i++)
            {
                string marker = save.SelectedItem == save.Inventory[i] ? " [SEL]" : "";
                int idx = i;
                RenderEditableLine(ref y, x, lh, $"  Item[{i}]{marker}", save.Inventory[i].ToString(),
                    () => save.Inventory[idx].ToString(),
                    v => { if (int.TryParse(v, out var val)) save.Inventory[idx] = val; },
                    idx);
            }
        }
        // Add new item button
        RenderEditableLine(ref y, x, lh, "  [+] Add Item", "(tile ID)",
            () => "",
            v => {
                if (int.TryParse(v, out var val) && val > 0 && val < 65535)
                {
                    save.Inventory ??= new List<int>();
                    save.Inventory.Add(val);
                    _hasUnsavedChanges = true;
                }
            },
            -100);  // Special index for add
        y += lh;

        RenderSection(ref y, lh, $"Weapons ({save.Weapons?.Count ?? 0})");
        if (save.Weapons != null)
        {
            for (int i = 0; i < save.Weapons.Count; i++)
            {
                string marker = i == save.CurrentWeaponIndex ? " [EQ]" : "";
                int weaponId = save.Weapons[i];
                string ammoStr = "";
                if (save.WeaponAmmo != null && save.WeaponAmmo.TryGetValue(weaponId, out var ammo))
                {
                    ammoStr = $" ammo:{ammo.CurrentAmmo}/{ammo.MaxAmmo}";
                }
                int idx = i;
                RenderEditableLine(ref y, x, lh, $"  Wpn[{i}]{marker}", $"{weaponId}{ammoStr}",
                    () => save.Weapons[idx].ToString(),
                    v => { if (int.TryParse(v, out var val)) save.Weapons[idx] = val; },
                    idx + 1000);  // Offset to avoid collision with inventory indices
            }
        }
        // Add new weapon button
        RenderEditableLine(ref y, x, lh, "  [+] Add Weapon", "(tile ID)",
            () => "",
            v => {
                if (int.TryParse(v, out var val) && val > 0 && val < 65535)
                {
                    save.Weapons ??= new List<int>();
                    save.Weapons.Add(val);
                    _hasUnsavedChanges = true;
                }
            },
            -101);  // Special index for add weapon

        return y;
    }

    private int RenderWorldTab(int x, int y, int lh)
    {
        var save = _loadedSave!;

        if (save.WorldData == null)
        {
            RenderLine(ref y, x, lh, "No world data saved");
            return y;
        }

        var world = save.WorldData;

        RenderSection(ref y, lh, "World Info");
        RenderLine(ref y, x, lh, $"Planet: {world.Planet}");
        RenderLine(ref y, x, lh, $"Grid Size: {world.GridWidth} x {world.GridHeight}");
        RenderLine(ref y, x, lh, $"Total Zones: {world.Connections?.Count ?? 0}");
        y += lh;

        RenderSection(ref y, lh, "Special Zones");
        RenderLine(ref y, x, lh, $"Starting Zone: {world.StartingZoneId}");
        RenderLine(ref y, x, lh, $"Landing Zone: {world.LandingZoneId}");
        RenderLine(ref y, x, lh, $"Objective Zone: {world.ObjectiveZoneId}");
        RenderLine(ref y, x, lh, $"Yoda Zone: {world.YodaZoneId}");
        RenderLine(ref y, x, lh, $"X-Wing Zone: {world.XWingZoneId}");
        RenderLine(ref y, x, lh, $"The Force Zone: {world.TheForceZoneId}");
        y += lh;

        RenderSection(ref y, lh, "Dagobah Zones");
        if (world.DagobahZones != null)
        {
            RenderLine(ref y, x, lh, $"  {string.Join(", ", world.DagobahZones)}");
        }
        y += lh;

        RenderSection(ref y, lh, "Required Items");
        if (world.RequiredItems != null && world.RequiredItems.Count > 0)
        {
            foreach (var item in world.RequiredItems)
            {
                RenderLine(ref y, x, lh, $"  Tile #{item}");
            }
        }
        else
        {
            RenderLine(ref y, x, lh, "  (none)");
        }

        return y;
    }

    private int RenderZonesTab(int x, int y, int lh)
    {
        var save = _loadedSave!;

        RenderSection(ref y, lh, $"Visited Zones ({save.VisitedZones?.Count ?? 0})");
        if (save.VisitedZones != null && save.VisitedZones.Count > 0)
        {
            var zoneStr = string.Join(", ", save.VisitedZones.Take(30));
            if (save.VisitedZones.Count > 30) zoneStr += "...";
            RenderLine(ref y, x, lh, $"  {zoneStr}");
        }
        y += lh;

        RenderSection(ref y, lh, $"Solved Zones ({save.SolvedZones?.Count ?? 0})");
        if (save.SolvedZones != null && save.SolvedZones.Count > 0)
        {
            var zoneStr = string.Join(", ", save.SolvedZones.Take(30));
            if (save.SolvedZones.Count > 30) zoneStr += "...";
            RenderLine(ref y, x, lh, $"  {zoneStr}");
        }
        y += lh;

        RenderSection(ref y, lh, $"Collected Objects ({save.CollectedObjects?.Count ?? 0})");
        if (save.CollectedObjects != null)
        {
            foreach (var obj in save.CollectedObjects.Take(20))
            {
                RenderLine(ref y, x, lh, $"  {obj}");
            }
            if (save.CollectedObjects.Count > 20)
            {
                RenderLine(ref y, x, lh, $"  ... and {save.CollectedObjects.Count - 20} more");
            }
        }

        return y;
    }

    private int RenderVariablesTab(int x, int y, int lh)
    {
        var save = _loadedSave!;
        _currentFields.Clear();

        RenderSection(ref y, lh, $"Game Variables ({save.Variables?.Count ?? 0})");
        if (save.Variables != null && save.Variables.Count > 0)
        {
            foreach (var kvp in save.Variables.OrderBy(k => k.Key))
            {
                string name = GetVariableName(kvp.Key);
                string displayName = name != null ? $"{name} [{kvp.Key}]" : $"Var[{kvp.Key}]";
                int varId = kvp.Key;
                RenderEditableLine(ref y, x, lh, displayName, kvp.Value.ToString(),
                    () => save.Variables.TryGetValue(varId, out var v) ? v.ToString() : "0",
                    v => { if (int.TryParse(v, out var val)) save.Variables[varId] = val; },
                    varId);
            }
        }
        else
        {
            RenderLine(ref y, x, lh, "  (no variables set)");
        }
        y += lh;

        RenderSection(ref y, lh, $"Counters ({save.Counters?.Count ?? 0})");
        if (save.Counters != null && save.Counters.Count > 0)
        {
            foreach (var kvp in save.Counters.OrderBy(k => k.Key))
            {
                int cntId = kvp.Key;
                RenderEditableLine(ref y, x, lh, $"Counter[{kvp.Key}]", kvp.Value.ToString(),
                    () => save.Counters.TryGetValue(cntId, out var v) ? v.ToString() : "0",
                    v => { if (int.TryParse(v, out var val)) save.Counters[cntId] = val; },
                    cntId + 10000);
            }
        }
        else
        {
            RenderLine(ref y, x, lh, "  (no counters set)");
        }

        return y;
    }

    private static string? GetVariableName(int varId)
    {
        // Known Yoda Stories variables
        return varId switch
        {
            1 => "NPC_X",
            2 => "NPC_Y",
            3 => "NPC_CharId",
            998 => "OnDagobah",
            999 => "XWingAvail",
            _ when varId >= 1000 && varId < 2000 => $"ZoneInit[{varId - 1000}]",
            _ when varId >= 2000 && varId < 3000 => $"ZoneSolved[{varId - 2000}]",
            _ when varId >= 3000 => $"Script[{varId - 3000}]",
            _ => null
        };
    }

    private void RenderSection(ref int y, int lh, string title)
    {
        _font!.RenderText(_renderer, title, 160, y, 1, 100, 180, 255, 255);
        y += lh + 2;
    }

    private void RenderLine(ref int y, int x, int lh, string text)
    {
        _font!.RenderText(_renderer, text, x, y, 1, 200, 200, 200, 255);
        y += lh;
    }

    private void RenderStatusBar()
    {
        SDL.SetRenderDrawColor(_renderer, 45, 48, 55, 255);
        var statusRect = new SDLRect { X = 0, Y = WindowHeight - 25, W = WindowWidth, H = 25 };
        SDL.RenderFillRect(_renderer, &statusRect);

        string statusText;
        if (_isEditing)
        {
            statusText = "EDITING: Enter=Apply | Escape=Cancel";
        }
        else if (_hasUnsavedChanges)
        {
            statusText = "UNSAVED CHANGES - Ctrl+S:Save | Click fields to edit | ESC:Close";
        }
        else
        {
            statusText = "Click yellow fields to edit | Ctrl+S:Save | F5:Refresh | ESC:Close";
        }

        byte r = _hasUnsavedChanges ? (byte)255 : (byte)150;
        byte g = _hasUnsavedChanges ? (byte)200 : (byte)150;
        byte b = _hasUnsavedChanges ? (byte)100 : (byte)150;
        _font!.RenderText(_renderer, statusText, 5, WindowHeight - 20, 1, r, g, b, 255);
    }
}
