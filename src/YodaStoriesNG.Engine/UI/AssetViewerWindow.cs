using Hexa.NET.SDL2;
using YodaStoriesNG.Engine.Data;
using YodaStoriesNG.Engine.Rendering;

namespace YodaStoriesNG.Engine.UI;

/// <summary>
/// A debug window that displays game assets (tiles, characters) with identification.
/// </summary>
public class AssetViewerWindow : IDisposable
{
    private readonly GameData _gameData;
    private readonly TileRenderer _tileRenderer;

    private unsafe SDLWindow* _window;
    private unsafe SDLRenderer* _renderer;
    private unsafe SDLTexture* _tileAtlas;
    private BitmapFont? _font;

    private int _windowWidth = 800;
    private int _windowHeight = 650;
    private const int TileSize = 36;
    private const int TilePadding = 2;
    private const int TabHeight = 28;
    private const int InfoPanelHeight = 160;

    private bool _isOpen = false;
    private uint _windowId;
    private int _scrollOffset = 0;
    private int _tilesPerRow;
    private int _selectedTile = -1;
    private AssetTab _currentTab = AssetTab.All;

    private List<int> _filteredTiles = new();

    public bool IsOpen => _isOpen;

    private enum AssetTab
    {
        All,
        Items,
        Weapons,
        Characters,
        Map,
        Floor,
        Objects,
        Roof,
        Draggable,
        Transparent
    }

    public AssetViewerWindow(GameData gameData)
    {
        _gameData = gameData;
        _tileRenderer = new TileRenderer();
    }

    public unsafe void Open()
    {
        if (_isOpen) return;

        _window = SDL.CreateWindow(
            "Asset Viewer",
            50, 100,
            _windowWidth, _windowHeight,
            (uint)(SDLWindowFlags.Shown | SDLWindowFlags.Resizable));

        if (_window == null)
        {
            Console.WriteLine($"Failed to create asset viewer window: {SDL.GetErrorS()}");
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

        RefreshFilter();
        Console.WriteLine("[AssetViewer] Window opened");
    }

    private unsafe void CreateTileAtlas()
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

    public unsafe void Close()
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

    public unsafe bool HandleEvent(SDLEvent* evt)
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

        if (evt->Type == (uint)SDLEventType.Mousewheel && evt->Wheel.WindowID == _windowId)
        {
            _scrollOffset -= evt->Wheel.Y * 3;
            _scrollOffset = Math.Max(0, _scrollOffset);
            return true;
        }

        if (evt->Type == (uint)SDLEventType.Mousebuttondown && evt->Button.WindowID == _windowId)
        {
            int mx = evt->Button.X;
            int my = evt->Button.Y;

            // Tab clicks
            if (my < TabHeight)
            {
                int tabX = 5;
                foreach (AssetTab tab in Enum.GetValues<AssetTab>())
                {
                    int tabWidth = tab.ToString().Length * 8 + 12;
                    if (mx >= tabX && mx < tabX + tabWidth)
                    {
                        _currentTab = tab;
                        _scrollOffset = 0;
                        _selectedTile = -1;
                        RefreshFilter();
                        return true;
                    }
                    tabX += tabWidth + 3;
                }
            }

            // Tile grid clicks
            int gridTop = TabHeight + 5;
            int gridHeight = _windowHeight - TabHeight - InfoPanelHeight - 10;

            if (my >= gridTop && my < gridTop + gridHeight)
            {
                int tilesPerRow = (_windowWidth - 20) / (TileSize + TilePadding);
                int col = (mx - 5) / (TileSize + TilePadding);
                int row = (my - gridTop) / (TileSize + TilePadding) + _scrollOffset;
                int index = row * tilesPerRow + col;

                if (col >= 0 && col < tilesPerRow && index >= 0 && index < _filteredTiles.Count)
                {
                    _selectedTile = _filteredTiles[index];
                }
                return true;
            }
        }

        return false;
    }

    private void RefreshFilter()
    {
        _filteredTiles.Clear();

        for (int i = 0; i < _gameData.Tiles.Count; i++)
        {
            var tile = _gameData.Tiles[i];
            bool include = _currentTab switch
            {
                AssetTab.All => true,
                AssetTab.Items => tile.IsItem,
                AssetTab.Weapons => tile.IsWeapon,
                AssetTab.Characters => tile.IsCharacter,
                AssetTab.Map => tile.IsMap,
                AssetTab.Floor => tile.IsFloor,
                AssetTab.Objects => tile.IsObject,
                AssetTab.Roof => tile.IsRoof,
                AssetTab.Draggable => tile.IsDraggable,
                AssetTab.Transparent => tile.IsTransparent && !tile.IsFloor && !tile.IsObject,
                _ => true
            };

            if (include)
                _filteredTiles.Add(i);
        }
    }

    public unsafe void Render()
    {
        if (!_isOpen || _renderer == null) return;

        SDL.SetRenderDrawColor(_renderer, 25, 25, 30, 255);
        SDL.RenderClear(_renderer);

        RenderTabs();
        RenderTileGrid();
        RenderTileInfo();

        SDL.RenderPresent(_renderer);
    }

    private unsafe void RenderTabs()
    {
        SDL.SetRenderDrawColor(_renderer, 35, 35, 45, 255);
        var tabBg = new SDLRect { X = 0, Y = 0, W = _windowWidth, H = TabHeight };
        SDL.RenderFillRect(_renderer, &tabBg);

        int tabX = 5;
        foreach (AssetTab tab in Enum.GetValues<AssetTab>())
        {
            string label = tab.ToString();
            int tabWidth = label.Length * 8 + 12;
            bool isSelected = tab == _currentTab;

            SDL.SetRenderDrawColor(_renderer, isSelected ? (byte)70 : (byte)45,
                isSelected ? (byte)70 : (byte)45, isSelected ? (byte)90 : (byte)55, 255);
            var rect = new SDLRect { X = tabX, Y = 3, W = tabWidth, H = TabHeight - 6 };
            SDL.RenderFillRect(_renderer, &rect);

            _font?.RenderText(_renderer, label, tabX + 6, 8, 1,
                isSelected ? (byte)255 : (byte)160,
                isSelected ? (byte)255 : (byte)160,
                isSelected ? (byte)255 : (byte)160, 255);

            tabX += tabWidth + 3;
        }

        // Count
        _font?.RenderText(_renderer, $"({_filteredTiles.Count})", tabX + 10, 8, 1, 100, 100, 120, 255);
    }

    private unsafe void RenderTileGrid()
    {
        int gridTop = TabHeight + 5;
        int gridHeight = _windowHeight - TabHeight - InfoPanelHeight - 10;
        int tilesPerRow = Math.Max(1, (_windowWidth - 20) / (TileSize + TilePadding));
        int visibleRows = gridHeight / (TileSize + TilePadding);

        for (int row = 0; row < visibleRows; row++)
        {
            for (int col = 0; col < tilesPerRow; col++)
            {
                int index = (_scrollOffset + row) * tilesPerRow + col;
                if (index >= _filteredTiles.Count) break;

                int tileId = _filteredTiles[index];
                int x = 5 + col * (TileSize + TilePadding);
                int y = gridTop + row * (TileSize + TilePadding);

                // Background
                bool isSelected = tileId == _selectedTile;
                SDL.SetRenderDrawColor(_renderer, isSelected ? (byte)80 : (byte)40,
                    isSelected ? (byte)80 : (byte)40, isSelected ? (byte)100 : (byte)50, 255);
                var bgRect = new SDLRect { X = x - 1, Y = y - 1, W = TileSize + 2, H = TileSize + 2 };
                SDL.RenderFillRect(_renderer, &bgRect);

                // Tile
                RenderTile(tileId, x, y, TileSize);
            }
        }

        // Scrollbar
        int totalRows = (_filteredTiles.Count + tilesPerRow - 1) / tilesPerRow;
        if (totalRows > visibleRows)
        {
            int maxScroll = totalRows - visibleRows;
            int scrollbarHeight = gridHeight;
            int thumbHeight = Math.Max(20, scrollbarHeight * visibleRows / totalRows);
            int thumbY = gridTop + (_scrollOffset * (scrollbarHeight - thumbHeight)) / Math.Max(1, maxScroll);

            SDL.SetRenderDrawColor(_renderer, 40, 40, 50, 255);
            var scrollBg = new SDLRect { X = _windowWidth - 12, Y = gridTop, W = 10, H = scrollbarHeight };
            SDL.RenderFillRect(_renderer, &scrollBg);

            SDL.SetRenderDrawColor(_renderer, 80, 80, 100, 255);
            var scrollThumb = new SDLRect { X = _windowWidth - 11, Y = thumbY, W = 8, H = thumbHeight };
            SDL.RenderFillRect(_renderer, &scrollThumb);
        }
    }

    private unsafe void RenderTile(int tileId, int x, int y, int size)
    {
        if (_tileAtlas == null || tileId < 0 || tileId >= _gameData.Tiles.Count)
            return;

        var atlasX = (tileId % _tilesPerRow) * Tile.Width;
        var atlasY = (tileId / _tilesPerRow) * Tile.Height;

        var srcRect = new SDLRect { X = atlasX, Y = atlasY, W = Tile.Width, H = Tile.Height };
        var dstRect = new SDLRect { X = x, Y = y, W = size, H = size };

        SDL.RenderCopy(_renderer, _tileAtlas, &srcRect, &dstRect);
    }

    private unsafe void RenderTileInfo()
    {
        int infoY = _windowHeight - InfoPanelHeight;

        SDL.SetRenderDrawColor(_renderer, 30, 32, 40, 255);
        var bgRect = new SDLRect { X = 0, Y = infoY, W = _windowWidth, H = InfoPanelHeight };
        SDL.RenderFillRect(_renderer, &bgRect);

        // Border
        SDL.SetRenderDrawColor(_renderer, 50, 55, 70, 255);
        var borderRect = new SDLRect { X = 0, Y = infoY, W = _windowWidth, H = 1 };
        SDL.RenderFillRect(_renderer, &borderRect);

        if (_selectedTile < 0 || _selectedTile >= _gameData.Tiles.Count)
        {
            _font?.RenderText(_renderer, "Click a tile to view details", 15, infoY + 20, 1, 120, 120, 140, 255);
            return;
        }

        var tile = _gameData.Tiles[_selectedTile];

        // Large preview
        RenderTile(_selectedTile, 10, infoY + 10, 80);

        // Info text
        int textX = 110;
        int textY = infoY + 8;
        int lineHeight = 14;

        // Title
        _font?.RenderText(_renderer, $"Tile #{_selectedTile}", textX, textY, 1, 255, 255, 100, 255);
        textY += lineHeight + 2;

        // Name
        if (_gameData.TileNames.TryGetValue(_selectedTile, out var name))
        {
            _font?.RenderText(_renderer, $"Name: {name}", textX, textY, 1, 100, 255, 100, 255);
            textY += lineHeight;
        }

        // Type
        string classification = ClassifyTile(tile);
        _font?.RenderText(_renderer, $"Type: {classification}", textX, textY, 1, 200, 200, 255, 255);
        textY += lineHeight;

        // Flags
        var flags = new List<string>();
        if (tile.IsTransparent) flags.Add("Transparent");
        if (tile.IsFloor) flags.Add("Floor");
        if (tile.IsObject) flags.Add("Object");
        if (tile.IsDraggable) flags.Add("Draggable");
        if (tile.IsRoof) flags.Add("Roof");
        if (tile.IsMap) flags.Add("Map");
        if (tile.IsWeapon) flags.Add("Weapon");
        if (tile.IsItem) flags.Add("Item");
        if (tile.IsCharacter) flags.Add("Character");

        if (flags.Count > 0)
        {
            string flagStr = string.Join(", ", flags);
            if (flagStr.Length > 50) flagStr = flagStr.Substring(0, 50) + "...";
            _font?.RenderText(_renderer, $"Flags: {flagStr}", textX, textY, 1, 180, 180, 180, 255);
            textY += lineHeight;
        }

        // Raw flags
        _font?.RenderText(_renderer, $"Raw: 0x{(uint)tile.Flags:X8}", textX, textY, 1, 120, 120, 140, 255);
        textY += lineHeight;

        // Extra info based on type
        if (tile.IsWeapon)
        {
            _font?.RenderText(_renderer, $"Weapon: {GetWeaponType(tile)}", textX, textY, 1, 255, 180, 100, 255);
            textY += lineHeight;
        }
        if (tile.IsItem)
        {
            _font?.RenderText(_renderer, $"Item: {GetItemType(tile)}", textX, textY, 1, 100, 255, 180, 255);
            textY += lineHeight;
        }
        if (tile.IsMap)
        {
            _font?.RenderText(_renderer, $"Map: {GetMapType(tile)}", textX, textY, 1, 180, 100, 255, 255);
            textY += lineHeight;
        }

        // Character association
        var charMatch = _gameData.Characters.FirstOrDefault(c =>
            c.Frames?.WalkDown?.Contains((ushort)_selectedTile) == true ||
            c.Frames?.WalkUp?.Contains((ushort)_selectedTile) == true ||
            c.Frames?.WalkLeft?.Contains((ushort)_selectedTile) == true ||
            c.Frames?.WalkRight?.Contains((ushort)_selectedTile) == true);

        if (charMatch != null)
        {
            _font?.RenderText(_renderer, $"Character: {charMatch.Name} ({charMatch.Type})", textX, textY, 1, 255, 200, 100, 255);
        }
    }

    private string ClassifyTile(Tile tile)
    {
        if (tile.IsWeapon) return "Weapon";
        if (tile.IsItem) return "Inventory Item";
        if (tile.IsCharacter) return "Character Sprite";
        if (tile.IsMap) return "Map Icon";
        if (tile.IsFloor) return "Floor/Ground";
        if (tile.IsRoof) return "Roof/Overlay";
        if (tile.IsObject) return "Object/Wall";
        if (tile.IsDraggable) return "Pushable Block";
        if (tile.IsTransparent) return "Transparent";
        return "Unknown";
    }

    private string GetWeaponType(Tile tile)
    {
        var flags = (uint)tile.Flags;
        var types = new List<string>();
        if ((flags & (1 << 16)) != 0) types.Add("Light Blaster");
        if ((flags & (1 << 17)) != 0) types.Add("Heavy Blaster");
        if ((flags & (1 << 18)) != 0) types.Add("Lightsaber");
        if ((flags & (1 << 19)) != 0) types.Add("The Force");
        return types.Count > 0 ? string.Join(", ", types) : "Standard";
    }

    private string GetItemType(Tile tile)
    {
        var flags = (uint)tile.Flags;
        var types = new List<string>();
        if ((flags & (1 << 16)) != 0) types.Add("Keycard");
        if ((flags & (1 << 17)) != 0) types.Add("Puzzle1");
        if ((flags & (1 << 18)) != 0) types.Add("Puzzle2");
        if ((flags & (1 << 19)) != 0) types.Add("Puzzle3");
        if ((flags & (1 << 20)) != 0) types.Add("Locator");
        if ((flags & (1 << 22)) != 0) types.Add("Health Pack");
        return types.Count > 0 ? string.Join(", ", types) : "General";
    }

    private string GetMapType(Tile tile)
    {
        var flags = (uint)tile.Flags;
        var types = new List<string>();
        if ((flags & (1 << 17)) != 0) types.Add("Home");
        if ((flags & (1 << 18)) != 0) types.Add("Puzzle Solved");
        if ((flags & (1 << 19)) != 0) types.Add("Puzzle Unsolved");
        if ((flags & (1 << 20)) != 0) types.Add("Gateway");
        if ((flags & (1 << 21)) != 0) types.Add("Wall");
        if ((flags & (1 << 22)) != 0) types.Add("Objective");
        return types.Count > 0 ? string.Join(", ", types) : "Basic";
    }

    public void Dispose() => Close();
}
