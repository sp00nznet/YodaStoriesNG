using Hexa.NET.SDL2;
using YodaStoriesNG.Engine.Data;
using YodaStoriesNG.Engine.Game;
using YodaStoriesNG.Engine.Rendering;

namespace YodaStoriesNG.Engine.UI;

/// <summary>
/// Visual zone editor for viewing and editing zone tiles, objects, and properties.
/// </summary>
public unsafe class ZoneEditorWindow : IDisposable
{
    private readonly GameState _state;
    private readonly GameData _gameData;
    private readonly TileRenderer _tileRenderer;

    private SDLWindow* _window;
    private SDLRenderer* _renderer;
    private SDLTexture* _tileAtlas;
    private BitmapFont? _font;

    private int _windowWidth = 900;
    private int _windowHeight = 700;
    private bool _isOpen;
    private uint _windowId;

    // Zoom and pan
    private int _tileScale = 2; // 1x, 2x, or 3x tile size
    private int _scrollX = 0;
    private int _scrollY = 0;
    private bool _isDragging = false;
    private int _dragStartX, _dragStartY;
    private int _dragScrollStartX, _dragScrollStartY;

    // Zone selection
    private int _selectedZoneId = -1;
    private Zone? _selectedZone => _selectedZoneId >= 0 && _selectedZoneId < _gameData.Zones.Count
        ? _gameData.Zones[_selectedZoneId] : null;

    // Layer visibility
    private bool _showLayer0 = true; // Floor
    private bool _showLayer1 = true; // Objects
    private bool _showLayer2 = true; // Roof
    private bool _showObjects = true; // Zone objects overlay
    private bool _showGrid = true;

    // Selected tile/layer
    private int _selectedX = -1;
    private int _selectedY = -1;
    private int _selectedLayer = 1; // Default to object layer

    // Tile palette
    private int _paletteTileId = -1;
    private bool _isPaletteOpen = false;
    private int _paletteScrollOffset = 0;

    // UI layout
    private const int LeftPanelWidth = 200;
    private const int TopBarHeight = 30;
    private const int StatusBarHeight = 25;
    private int _tilesPerRow;

    // Events
    public event Action<int>? OnTeleportToZone;
    public event Action<int>? OnZoneSelected;

    public bool IsOpen => _isOpen;
    public int SelectedZoneId => _selectedZoneId;

    public ZoneEditorWindow(GameState state, GameData gameData)
    {
        _state = state;
        _gameData = gameData;
        _tileRenderer = new TileRenderer();
    }

    public void Open()
    {
        if (_isOpen) return;

        _window = SDL.CreateWindow(
            "Zone Editor",
            50, 50,
            _windowWidth, _windowHeight,
            (uint)(SDLWindowFlags.Shown | SDLWindowFlags.Resizable));

        if (_window == null)
        {
            Console.WriteLine($"[ZoneEditor] Failed to create window: {SDL.GetErrorS()}");
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

        CreateTileAtlas();

        _font = new BitmapFont();
        _font.Initialize(_renderer);

        _windowId = SDL.GetWindowID(_window);
        _isOpen = true;

        // Default to current zone
        _selectedZoneId = _state.CurrentZoneId;
        CenterViewOnZone();

        Console.WriteLine("[ZoneEditor] Window opened");
    }

    private void CreateTileAtlas()
    {
        if (_gameData.Tiles.Count == 0) return;

        _tilesPerRow = (int)Math.Ceiling(Math.Sqrt(_gameData.Tiles.Count));
        var (pixels, width, height) = _tileRenderer.CreateTileAtlas(_gameData.Tiles, _tilesPerRow);

        _tileAtlas = SDL.CreateTexture(
            _renderer,
            (uint)SDLPixelFormatEnum.Argb8888,
            (int)SDLTextureAccess.Static,
            width, height);

        if (_tileAtlas == null) return;

        SDL.SetTextureBlendMode(_tileAtlas, SDLBlendMode.Blend);

        fixed (uint* pixelPtr = pixels)
        {
            SDL.UpdateTexture(_tileAtlas, null, pixelPtr, width * 4);
        }
    }

    public void Close()
    {
        if (!_isOpen) return;

        _font?.Dispose();
        _font = null;

        if (_tileAtlas != null)
        {
            SDL.DestroyTexture(_tileAtlas);
            _tileAtlas = null;
        }

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

    public void SetZone(int zoneId)
    {
        if (zoneId >= 0 && zoneId < _gameData.Zones.Count)
        {
            _selectedZoneId = zoneId;
            CenterViewOnZone();
            OnZoneSelected?.Invoke(zoneId);
        }
    }

    private void CenterViewOnZone()
    {
        var zone = _selectedZone;
        if (zone == null) return;

        int displayTileSize = Tile.Width * _tileScale;
        int zonePixelWidth = zone.Width * displayTileSize;
        int zonePixelHeight = zone.Height * displayTileSize;

        int viewportWidth = _windowWidth - LeftPanelWidth;
        int viewportHeight = _windowHeight - TopBarHeight - StatusBarHeight;

        _scrollX = (zonePixelWidth - viewportWidth) / 2;
        _scrollY = (zonePixelHeight - viewportHeight) / 2;
        _scrollX = Math.Max(0, _scrollX);
        _scrollY = Math.Max(0, _scrollY);
    }

    public bool HandleEvent(SDLEvent* evt)
    {
        if (!_isOpen) return false;

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

        // Mouse wheel - zoom or scroll
        if (evt->Type == (uint)SDLEventType.Mousewheel && evt->Wheel.WindowID == _windowId)
        {
            int mx, my;
            SDL.GetMouseState(&mx, &my);

            if (mx > LeftPanelWidth)
            {
                // In zone view - scroll vertically
                _scrollY -= evt->Wheel.Y * 32;
                _scrollY = Math.Max(0, _scrollY);
            }
            else if (_isPaletteOpen)
            {
                // In palette - scroll tiles
                _paletteScrollOffset -= evt->Wheel.Y * 3;
                _paletteScrollOffset = Math.Max(0, _paletteScrollOffset);
            }
            return true;
        }

        // Mouse button down
        if (evt->Type == (uint)SDLEventType.Mousebuttondown && evt->Button.WindowID == _windowId)
        {
            int mx = evt->Button.X;
            int my = evt->Button.Y;

            // Left panel clicks
            if (mx < LeftPanelWidth)
            {
                return HandleLeftPanelClick(mx, my, evt->Button.Button);
            }

            // Zone view clicks
            if (mx > LeftPanelWidth && my > TopBarHeight && my < _windowHeight - StatusBarHeight)
            {
                if (evt->Button.Button == 1) // Left click - select tile
                {
                    return HandleZoneClick(mx, my);
                }
                else if (evt->Button.Button == 2) // Middle click - start drag
                {
                    _isDragging = true;
                    _dragStartX = mx;
                    _dragStartY = my;
                    _dragScrollStartX = _scrollX;
                    _dragScrollStartY = _scrollY;
                    return true;
                }
                else if (evt->Button.Button == 3) // Right click - place tile
                {
                    return HandleTilePlacement(mx, my);
                }
            }

            // Top bar clicks
            if (my < TopBarHeight)
            {
                return HandleTopBarClick(mx, my);
            }

            return true;
        }

        // Mouse button up
        if (evt->Type == (uint)SDLEventType.Mousebuttonup && evt->Button.WindowID == _windowId)
        {
            _isDragging = false;
            return false;
        }

        // Mouse motion
        if (evt->Type == (uint)SDLEventType.Mousemotion && evt->Motion.WindowID == _windowId)
        {
            if (_isDragging)
            {
                _scrollX = _dragScrollStartX - (evt->Motion.X - _dragStartX);
                _scrollY = _dragScrollStartY - (evt->Motion.Y - _dragStartY);
                _scrollX = Math.Max(0, _scrollX);
                _scrollY = Math.Max(0, _scrollY);
                return true;
            }
        }

        // Keyboard
        if (evt->Type == (uint)SDLEventType.Keydown && SDL.GetWindowID(SDL.GetKeyboardFocus()) == _windowId)
        {
            return HandleKeyboard((int)evt->Key.Keysym.Sym);
        }

        return false;
    }

    private bool HandleLeftPanelClick(int mx, int my)
    {
        // Zone list area
        int listY = TopBarHeight + 80;
        int listEndY = _windowHeight - StatusBarHeight - 200;

        if (my >= listY && my < listEndY)
        {
            int clickedIndex = (my - listY) / 18;
            // Simple zone selection - could be enhanced with filtering
            int zoneId = clickedIndex + (_selectedZoneId > 10 ? _selectedZoneId - 10 : 0);
            if (zoneId >= 0 && zoneId < _gameData.Zones.Count)
            {
                _selectedZoneId = zoneId;
                _selectedX = -1;
                _selectedY = -1;
                CenterViewOnZone();
                OnZoneSelected?.Invoke(zoneId);
            }
            return true;
        }

        // Layer toggles
        int toggleY = _windowHeight - StatusBarHeight - 180;
        if (my >= toggleY && my < toggleY + 100)
        {
            int toggleIndex = (my - toggleY) / 20;
            switch (toggleIndex)
            {
                case 0: _showLayer0 = !_showLayer0; break;
                case 1: _showLayer1 = !_showLayer1; break;
                case 2: _showLayer2 = !_showLayer2; break;
                case 3: _showObjects = !_showObjects; break;
                case 4: _showGrid = !_showGrid; break;
            }
            return true;
        }

        return false;
    }

    private bool HandleLeftPanelClick(int mx, int my, byte button)
    {
        return HandleLeftPanelClick(mx, my);
    }

    private bool HandleZoneClick(int mx, int my)
    {
        var zone = _selectedZone;
        if (zone == null) return false;

        int displayTileSize = Tile.Width * _tileScale;
        int viewX = mx - LeftPanelWidth + _scrollX;
        int viewY = my - TopBarHeight + _scrollY;

        int tileX = viewX / displayTileSize;
        int tileY = viewY / displayTileSize;

        if (tileX >= 0 && tileX < zone.Width && tileY >= 0 && tileY < zone.Height)
        {
            _selectedX = tileX;
            _selectedY = tileY;
            return true;
        }

        return false;
    }

    private bool HandleTilePlacement(int mx, int my)
    {
        var zone = _selectedZone;
        if (zone == null || _paletteTileId < 0) return false;

        int displayTileSize = Tile.Width * _tileScale;
        int viewX = mx - LeftPanelWidth + _scrollX;
        int viewY = my - TopBarHeight + _scrollY;

        int tileX = viewX / displayTileSize;
        int tileY = viewY / displayTileSize;

        if (tileX >= 0 && tileX < zone.Width && tileY >= 0 && tileY < zone.Height)
        {
            zone.SetTile(tileX, tileY, _selectedLayer, (ushort)_paletteTileId);
            Console.WriteLine($"[ZoneEditor] Placed tile {_paletteTileId} at ({tileX},{tileY}) layer {_selectedLayer}");
            return true;
        }

        return false;
    }

    private bool HandleTopBarClick(int mx, int my)
    {
        // Layer selector buttons
        int buttonX = LeftPanelWidth + 10;
        int buttonWidth = 50;

        for (int i = 0; i < 3; i++)
        {
            if (mx >= buttonX && mx < buttonX + buttonWidth)
            {
                _selectedLayer = i;
                return true;
            }
            buttonX += buttonWidth + 5;
        }

        // Zoom buttons
        buttonX = _windowWidth - 150;
        if (mx >= buttonX && mx < buttonX + 30)
        {
            _tileScale = Math.Max(1, _tileScale - 1);
            return true;
        }
        buttonX += 50;
        if (mx >= buttonX && mx < buttonX + 30)
        {
            _tileScale = Math.Min(4, _tileScale + 1);
            return true;
        }

        return false;
    }

    private bool HandleKeyboard(int key)
    {
        switch (key)
        {
            case 27: // ESC
                Close();
                return true;

            case 'g':
            case 'G': // Go to current zone
                _selectedZoneId = _state.CurrentZoneId;
                CenterViewOnZone();
                return true;

            case 't':
            case 'T': // Teleport
                if (_selectedZoneId >= 0)
                    OnTeleportToZone?.Invoke(_selectedZoneId);
                return true;

            case '1': _selectedLayer = 0; return true;
            case '2': _selectedLayer = 1; return true;
            case '3': _selectedLayer = 2; return true;

            case '0': _showLayer0 = !_showLayer0; return true;
            case 1073741899: // Page Up - prev zone
                if (_selectedZoneId > 0)
                {
                    _selectedZoneId--;
                    CenterViewOnZone();
                }
                return true;
            case 1073741902: // Page Down - next zone
                if (_selectedZoneId < _gameData.Zones.Count - 1)
                {
                    _selectedZoneId++;
                    CenterViewOnZone();
                }
                return true;

            case '-':
            case 1073741910: // Numpad -
                _tileScale = Math.Max(1, _tileScale - 1);
                return true;

            case '=':
            case '+':
            case 1073741911: // Numpad +
                _tileScale = Math.Min(4, _tileScale + 1);
                return true;
        }

        return false;
    }

    public void Render()
    {
        if (!_isOpen || _renderer == null) return;

        // Clear
        SDL.SetRenderDrawColor(_renderer, 25, 25, 30, 255);
        SDL.RenderClear(_renderer);

        // Render components
        RenderLeftPanel();
        RenderTopBar();
        RenderZoneView();
        RenderStatusBar();

        SDL.RenderPresent(_renderer);
    }

    private void RenderLeftPanel()
    {
        // Background
        SDL.SetRenderDrawColor(_renderer, 35, 38, 45, 255);
        var bg = new SDLRect { X = 0, Y = 0, W = LeftPanelWidth, H = _windowHeight };
        SDL.RenderFillRect(_renderer, &bg);

        // Border
        SDL.SetRenderDrawColor(_renderer, 60, 65, 75, 255);
        var border = new SDLRect { X = LeftPanelWidth - 1, Y = 0, W = 1, H = _windowHeight };
        SDL.RenderFillRect(_renderer, &border);

        int y = 8;

        // Header
        _font?.RenderText(_renderer, "ZONE EDITOR", 8, y, 1, 255, 200, 100, 255);
        y += 18;

        // Current zone info
        var zone = _selectedZone;
        if (zone != null)
        {
            _font?.RenderText(_renderer, $"Zone {_selectedZoneId}", 8, y, 1, 255, 255, 255, 255);
            y += 14;
            _font?.RenderText(_renderer, $"{zone.Planet} {zone.Width}x{zone.Height}", 8, y, 1, 180, 180, 200, 255);
            y += 14;
            _font?.RenderText(_renderer, $"Type: {zone.Type}", 8, y, 1, 150, 150, 170, 255);
            y += 14;
            _font?.RenderText(_renderer, $"Flags: {zone.Flags}", 8, y, 1, 150, 150, 170, 255);
            y += 14;
            _font?.RenderText(_renderer, $"Actions: {zone.Actions.Count}", 8, y, 1, 150, 150, 170, 255);
            y += 14;
            _font?.RenderText(_renderer, $"Objects: {zone.Objects.Count}", 8, y, 1, 150, 150, 170, 255);
        }
        else
        {
            _font?.RenderText(_renderer, "No zone selected", 8, y, 1, 150, 150, 150, 255);
        }

        y += 20;

        // Zone list header
        SDL.SetRenderDrawColor(_renderer, 45, 48, 55, 255);
        var listHeader = new SDLRect { X = 0, Y = y, W = LeftPanelWidth, H = 18 };
        SDL.RenderFillRect(_renderer, &listHeader);
        _font?.RenderText(_renderer, "ZONES", 8, y + 3, 1, 200, 180, 100, 255);
        y += 20;

        // Zone list (simplified - shows zones around current selection)
        int listHeight = _windowHeight - y - 200;
        int visibleZones = listHeight / 18;
        int startZone = Math.Max(0, _selectedZoneId - visibleZones / 2);
        int endZone = Math.Min(_gameData.Zones.Count, startZone + visibleZones);

        for (int i = startZone; i < endZone && y < _windowHeight - 200; i++)
        {
            var z = _gameData.Zones[i];
            bool isSelected = i == _selectedZoneId;
            bool isCurrent = i == _state.CurrentZoneId;

            if (isSelected)
            {
                SDL.SetRenderDrawColor(_renderer, 60, 80, 110, 255);
                var sel = new SDLRect { X = 2, Y = y, W = LeftPanelWidth - 4, H = 16 };
                SDL.RenderFillRect(_renderer, &sel);
            }

            byte r = 160, g = 160, b = 160;
            if (isSelected) { r = 255; g = 255; b = 255; }
            if (isCurrent) { r = 100; g = 255; b = 100; }

            string label = $"{i}: {z.Type}";
            if (z.Actions.Count > 0) label += "*";
            _font?.RenderText(_renderer, label, 8, y + 2, 1, r, g, b, 255);
            y += 18;
        }

        // Layer toggles
        y = _windowHeight - StatusBarHeight - 180;
        _font?.RenderText(_renderer, "LAYERS", 8, y - 16, 1, 200, 180, 100, 255);

        RenderToggle(8, y, "Floor (0)", _showLayer0, 255, 200, 100);
        RenderToggle(8, y + 20, "Object (1)", _showLayer1, 100, 255, 100);
        RenderToggle(8, y + 40, "Roof (2)", _showLayer2, 100, 200, 255);
        RenderToggle(8, y + 60, "Zone Objects", _showObjects, 255, 100, 255);
        RenderToggle(8, y + 80, "Grid", _showGrid, 150, 150, 150);

        // Selected tile info
        y = _windowHeight - StatusBarHeight - 60;
        if (_selectedX >= 0 && _selectedY >= 0 && zone != null)
        {
            _font?.RenderText(_renderer, $"Selected: ({_selectedX},{_selectedY})", 8, y, 1, 200, 200, 200, 255);
            y += 14;

            for (int layer = 0; layer < 3; layer++)
            {
                int tileId = zone.GetTile(_selectedX, _selectedY, layer);
                if (tileId > 0 && tileId < 0xFFFF)
                {
                    string name = GetTileName(tileId);
                    _font?.RenderText(_renderer, $"L{layer}: {name}", 8, y, 1, 150, 150, 170, 255);
                    y += 12;
                }
            }
        }
    }

    private void RenderToggle(int x, int y, string label, bool value, byte r, byte g, byte b)
    {
        SDL.SetRenderDrawColor(_renderer, value ? r : (byte)60, value ? g : (byte)60, value ? b : (byte)60, 255);
        var box = new SDLRect { X = x, Y = y, W = 12, H = 12 };
        if (value)
            SDL.RenderFillRect(_renderer, &box);
        else
            SDL.RenderDrawRect(_renderer, &box);

        _font?.RenderText(_renderer, label, x + 16, y + 2, 1, 180, 180, 180, 255);
    }

    private void RenderTopBar()
    {
        // Background
        SDL.SetRenderDrawColor(_renderer, 40, 43, 52, 255);
        var bg = new SDLRect { X = LeftPanelWidth, Y = 0, W = _windowWidth - LeftPanelWidth, H = TopBarHeight };
        SDL.RenderFillRect(_renderer, &bg);

        // Layer selector
        int buttonX = LeftPanelWidth + 10;
        string[] layerNames = { "Floor", "Object", "Roof" };
        for (int i = 0; i < 3; i++)
        {
            bool selected = _selectedLayer == i;
            SDL.SetRenderDrawColor(_renderer, selected ? (byte)80 : (byte)50, selected ? (byte)100 : (byte)55, selected ? (byte)140 : (byte)65, 255);
            var btn = new SDLRect { X = buttonX, Y = 4, W = 50, H = 22 };
            SDL.RenderFillRect(_renderer, &btn);
            _font?.RenderText(_renderer, layerNames[i], buttonX + 5, 9, 1, 200, 200, 200, 255);
            buttonX += 55;
        }

        // Zoom indicator
        buttonX = _windowWidth - 120;
        _font?.RenderText(_renderer, $"Zoom: {_tileScale}x", buttonX, 9, 1, 150, 150, 170, 255);

        // Keyboard hints
        _font?.RenderText(_renderer, "G=Current T=Teleport 1-3=Layer", LeftPanelWidth + 200, 9, 1, 100, 100, 120, 255);
    }

    private void RenderZoneView()
    {
        var zone = _selectedZone;
        if (zone == null) return;

        int viewportX = LeftPanelWidth;
        int viewportY = TopBarHeight;
        int viewportW = _windowWidth - LeftPanelWidth;
        int viewportH = _windowHeight - TopBarHeight - StatusBarHeight;

        // Set clip rect
        var clipRect = new SDLRect { X = viewportX, Y = viewportY, W = viewportW, H = viewportH };
        SDL.RenderSetClipRect(_renderer, &clipRect);

        int displayTileSize = Tile.Width * _tileScale;

        // Render tiles
        for (int y = 0; y < zone.Height; y++)
        {
            for (int x = 0; x < zone.Width; x++)
            {
                int screenX = viewportX + x * displayTileSize - _scrollX;
                int screenY = viewportY + y * displayTileSize - _scrollY;

                // Skip if off-screen
                if (screenX + displayTileSize < viewportX || screenX > viewportX + viewportW ||
                    screenY + displayTileSize < viewportY || screenY > viewportY + viewportH)
                    continue;

                // Render each visible layer
                if (_showLayer0) RenderTileAt(zone.GetTile(x, y, 0), screenX, screenY, displayTileSize);
                if (_showLayer1) RenderTileAt(zone.GetTile(x, y, 1), screenX, screenY, displayTileSize);
                if (_showLayer2) RenderTileAt(zone.GetTile(x, y, 2), screenX, screenY, displayTileSize);

                // Grid
                if (_showGrid)
                {
                    SDL.SetRenderDrawColor(_renderer, 40, 40, 50, 100);
                    var gridRect = new SDLRect { X = screenX, Y = screenY, W = displayTileSize, H = displayTileSize };
                    SDL.RenderDrawRect(_renderer, &gridRect);
                }
            }
        }

        // Render zone objects overlay
        if (_showObjects)
        {
            foreach (var obj in zone.Objects)
            {
                int screenX = viewportX + obj.X * displayTileSize - _scrollX;
                int screenY = viewportY + obj.Y * displayTileSize - _scrollY;

                var (r, g, b, label) = GetObjectColor(obj.Type);
                SDL.SetRenderDrawColor(_renderer, r, g, b, 180);
                var objRect = new SDLRect { X = screenX + 2, Y = screenY + 2, W = displayTileSize - 4, H = displayTileSize - 4 };
                SDL.RenderDrawRect(_renderer, &objRect);

                if (_tileScale >= 2)
                {
                    _font?.RenderText(_renderer, label, screenX + 4, screenY + displayTileSize - 12, 1, r, g, b, 255);
                }
            }
        }

        // Selection highlight
        if (_selectedX >= 0 && _selectedY >= 0)
        {
            int screenX = viewportX + _selectedX * displayTileSize - _scrollX;
            int screenY = viewportY + _selectedY * displayTileSize - _scrollY;

            double pulse = Math.Sin(SDL.GetTicks() / 200.0) * 0.5 + 0.5;
            byte intensity = (byte)(155 + 100 * pulse);

            SDL.SetRenderDrawColor(_renderer, intensity, 255, intensity, 255);
            for (int i = 0; i < 2; i++)
            {
                var selRect = new SDLRect { X = screenX + i, Y = screenY + i, W = displayTileSize - i * 2, H = displayTileSize - i * 2 };
                SDL.RenderDrawRect(_renderer, &selRect);
            }
        }

        // Clear clip rect
        SDL.RenderSetClipRect(_renderer, null);
    }

    private void RenderTileAt(int tileId, int screenX, int screenY, int displaySize)
    {
        if (tileId <= 0 || tileId >= _gameData.Tiles.Count || _tileAtlas == null)
            return;

        int atlasX = (tileId % _tilesPerRow) * Tile.Width;
        int atlasY = (tileId / _tilesPerRow) * Tile.Height;

        var srcRect = new SDLRect { X = atlasX, Y = atlasY, W = Tile.Width, H = Tile.Height };
        var dstRect = new SDLRect { X = screenX, Y = screenY, W = displaySize, H = displaySize };

        SDL.RenderCopy(_renderer, _tileAtlas, &srcRect, &dstRect);
    }

    private (byte r, byte g, byte b, string label) GetObjectColor(ZoneObjectType type)
    {
        return type switch
        {
            ZoneObjectType.DoorEntrance => (100, 255, 100, "DR"),
            ZoneObjectType.DoorExit => (100, 200, 100, "EX"),
            ZoneObjectType.Lock => (255, 200, 100, "LK"),
            ZoneObjectType.PuzzleNPC => (255, 100, 255, "NPC"),
            ZoneObjectType.CrateItem => (255, 200, 50, "IT"),
            ZoneObjectType.CrateWeapon => (255, 150, 50, "WP"),
            ZoneObjectType.LocatorItem => (200, 200, 255, "LOC"),
            ZoneObjectType.Trigger => (100, 180, 255, "TR"),
            ZoneObjectType.SpawnLocation => (255, 100, 100, "SP"),
            ZoneObjectType.Teleporter => (150, 100, 255, "TP"),
            ZoneObjectType.VehicleToSecondary or ZoneObjectType.VehicleToPrimary => (100, 255, 255, "XV"),
            ZoneObjectType.XWingFromDagobah or ZoneObjectType.XWingToDagobah => (100, 255, 255, "XW"),
            _ => (150, 150, 150, "??")
        };
    }

    private void RenderStatusBar()
    {
        int y = _windowHeight - StatusBarHeight;

        SDL.SetRenderDrawColor(_renderer, 35, 38, 45, 255);
        var bg = new SDLRect { X = 0, Y = y, W = _windowWidth, H = StatusBarHeight };
        SDL.RenderFillRect(_renderer, &bg);

        int textY = y + 6;

        // Zone info
        _font?.RenderText(_renderer, $"Zone {_selectedZoneId} of {_gameData.Zones.Count}", 10, textY, 1, 150, 150, 170, 255);

        // Layer
        string[] layerNames = { "Floor", "Object", "Roof" };
        _font?.RenderText(_renderer, $"Layer: {layerNames[_selectedLayer]}", 180, textY, 1, 150, 150, 170, 255);

        // Scroll position
        _font?.RenderText(_renderer, $"Scroll: {_scrollX},{_scrollY}", 330, textY, 1, 100, 100, 120, 255);

        // Help
        _font?.RenderText(_renderer, "PgUp/PgDn: Navigate | +/-: Zoom | Middle-drag: Pan", _windowWidth - 400, textY, 1, 100, 100, 120, 255);
    }

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

    public void Dispose()
    {
        Close();
    }
}
