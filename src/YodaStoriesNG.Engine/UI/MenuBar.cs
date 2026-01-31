using Hexa.NET.SDL2;
using YodaStoriesNG.Engine.Game;
using YodaStoriesNG.Engine.Rendering;

namespace YodaStoriesNG.Engine.UI;

/// <summary>
/// A simple menu bar for the game.
/// </summary>
public unsafe class MenuBar
{
    private readonly BitmapFont _font;
    private SDLRenderer* _renderer;
    private uint _windowId;

    public const int Height = 22;

    private int _openMenu = -1; // -1 = none, 0 = File, 1 = Debug, 2 = Config, 3 = About
    private int _hoveredItem = -1;

    // Menu structure
    private readonly string[] _menus = { "File", "Debug", "Config", "About" };
    private readonly string[][] _menuItems = {
        new[] { "New Game: Small", "New Game: Medium", "New Game: Large", "New Game: X-tra Large", "-", "Save Game", "Save As...", "Load Game", "-", "Exit" },
        new[] { "Asset Viewer (F2)", "Script Editor (F3)", "Map Viewer (F4)", "-", "Enable Bot", "Disable Bot" },
        new[] { "Graphics: 1x Scale", "Graphics: 2x Scale", "Graphics: 4x Scale", "-", "Keyboard Controls", "Controller Controls", "-", "Select Data File..." },
        new[] { "About Yoda Stories NG" }
    };

    // Menu positions (consistent 5px gap between menus)
    private readonly int[] _menuX = { 10, 60, 125, 190 };
    private readonly int[] _menuWidths = { 45, 60, 60, 55 };

    // Events
    public event Action<WorldSize>? OnNewGame;
    public event Action? OnSaveGame;
    public event Action? OnSaveGameAs;
    public event Action? OnLoadGame;
    public event Action? OnExit;
    public event Action? OnAssetViewer;
    public event Action? OnScriptEditor;
    public event Action? OnMapViewer;
    public event Action? OnEnableBot;
    public event Action? OnDisableBot;
    public event Action<int>? OnSetScale;
    public event Action? OnShowKeyboardControls;
    public event Action? OnShowControllerControls;
    public event Action? OnSelectDataFile;
    public event Action? OnShowAbout;

    public bool IsMenuOpen => _openMenu >= 0;

    public MenuBar(BitmapFont font)
    {
        _font = font;
    }

    public void SetRenderer(SDLRenderer* renderer, uint windowId)
    {
        _renderer = renderer;
        _windowId = windowId;
    }

    public bool HandleEvent(SDLEvent* evt)
    {
        // Only handle events for our window
        if (evt->Type == (uint)SDLEventType.Mousebuttondown && evt->Button.WindowID != _windowId)
            return false;
        if (evt->Type == (uint)SDLEventType.Mousemotion && evt->Motion.WindowID != _windowId)
            return false;
        if (evt->Type == (uint)SDLEventType.Keydown && evt->Key.WindowID != _windowId)
            return false;

        if (evt->Type == (uint)SDLEventType.Mousebuttondown)
        {
            int mx = evt->Button.X;
            int my = evt->Button.Y;

            // Check if clicking on menu bar
            if (my < Height)
            {
                for (int i = 0; i < _menus.Length; i++)
                {
                    if (mx >= _menuX[i] && mx < _menuX[i] + _menuWidths[i])
                    {
                        _openMenu = _openMenu == i ? -1 : i;
                        _hoveredItem = -1;
                        return true;
                    }
                }
                _openMenu = -1;
                return true;
            }

            // Check if clicking on open menu items
            if (_openMenu >= 0)
            {
                int menuX = _menuX[_openMenu];
                int menuY = Height;
                int itemHeight = 22;
                int menuWidth = GetMenuWidth(_openMenu);

                if (mx >= menuX && mx < menuX + menuWidth)
                {
                    var items = _menuItems[_openMenu];
                    for (int i = 0; i < items.Length; i++)
                    {
                        if (items[i] == "-") continue; // Skip separators

                        int itemY = menuY + i * itemHeight;
                        if (my >= itemY && my < itemY + itemHeight)
                        {
                            ExecuteMenuItem(_openMenu, i);
                            _openMenu = -1;
                            return true;
                        }
                    }
                }

                // Click outside menu closes it
                _openMenu = -1;
                return true;
            }
        }

        if (evt->Type == (uint)SDLEventType.Mousemotion)
        {
            int mx = evt->Motion.X;
            int my = evt->Motion.Y;

            // Highlight menu items on hover
            if (_openMenu >= 0)
            {
                int menuX = _menuX[_openMenu];
                int menuY = Height;
                int itemHeight = 22;
                int menuWidth = GetMenuWidth(_openMenu);

                _hoveredItem = -1;
                if (mx >= menuX && mx < menuX + menuWidth)
                {
                    var items = _menuItems[_openMenu];
                    for (int i = 0; i < items.Length; i++)
                    {
                        if (items[i] == "-") continue;
                        int itemY = menuY + i * itemHeight;
                        if (my >= itemY && my < itemY + itemHeight)
                        {
                            _hoveredItem = i;
                            break;
                        }
                    }
                }

                // Switch menus on hover
                if (my < Height)
                {
                    for (int i = 0; i < _menus.Length; i++)
                    {
                        if (mx >= _menuX[i] && mx < _menuX[i] + _menuWidths[i])
                        {
                            if (_openMenu != i)
                            {
                                _openMenu = i;
                                _hoveredItem = -1;
                            }
                            break;
                        }
                    }
                }
            }
        }

        if (evt->Type == (uint)SDLEventType.Keydown)
        {
            // ESC closes menu
            if (evt->Key.Keysym.Sym == 27 && _openMenu >= 0)
            {
                _openMenu = -1;
                return true;
            }
        }

        return false;
    }

    private int GetMenuWidth(int menuIndex)
    {
        int maxWidth = 0;
        foreach (var item in _menuItems[menuIndex])
        {
            int width = _font.GetTextWidth(item);
            if (width > maxWidth) maxWidth = width;
        }
        return maxWidth + 30;
    }

    private void ExecuteMenuItem(int menu, int item)
    {
        switch (menu)
        {
            case 0: // File
                switch (item)
                {
                    case 0: OnNewGame?.Invoke(WorldSize.Small); break;
                    case 1: OnNewGame?.Invoke(WorldSize.Medium); break;
                    case 2: OnNewGame?.Invoke(WorldSize.Large); break;
                    case 3: OnNewGame?.Invoke(WorldSize.XtraLarge); break;
                    case 5: OnSaveGame?.Invoke(); break;      // Quick Save
                    case 6: OnSaveGameAs?.Invoke(); break;    // Save As...
                    case 7: OnLoadGame?.Invoke(); break;      // Load Game
                    case 9: OnExit?.Invoke(); break;          // Exit
                }
                break;
            case 1: // Debug
                switch (item)
                {
                    case 0: OnAssetViewer?.Invoke(); break;
                    case 1: OnScriptEditor?.Invoke(); break;
                    case 2: OnMapViewer?.Invoke(); break;
                    case 4: OnEnableBot?.Invoke(); break;
                    case 5: OnDisableBot?.Invoke(); break;
                }
                break;
            case 2: // Config
                switch (item)
                {
                    case 0: OnSetScale?.Invoke(1); break;
                    case 1: OnSetScale?.Invoke(2); break;
                    case 2: OnSetScale?.Invoke(4); break;
                    case 4: OnShowKeyboardControls?.Invoke(); break;
                    case 5: OnShowControllerControls?.Invoke(); break;
                    case 7: OnSelectDataFile?.Invoke(); break;
                }
                break;
            case 3: // About
                switch (item)
                {
                    case 0: OnShowAbout?.Invoke(); break;
                }
                break;
        }
    }

    public void Render()
    {
        if (_renderer == null) return;

        // Menu bar background
        SDL.SetRenderDrawColor(_renderer, 45, 48, 55, 255);
        var barRect = new SDLRect { X = 0, Y = 0, W = 800, H = Height };
        SDL.RenderFillRect(_renderer, &barRect);

        // Menu bar bottom border
        SDL.SetRenderDrawColor(_renderer, 30, 32, 38, 255);
        var borderRect = new SDLRect { X = 0, Y = Height - 1, W = 800, H = 1 };
        SDL.RenderFillRect(_renderer, &borderRect);

        // Render menu titles
        for (int i = 0; i < _menus.Length; i++)
        {
            bool isOpen = _openMenu == i;

            if (isOpen)
            {
                SDL.SetRenderDrawColor(_renderer, 60, 65, 75, 255);
                var highlight = new SDLRect { X = _menuX[i] - 5, Y = 0, W = _menuWidths[i] + 10, H = Height };
                SDL.RenderFillRect(_renderer, &highlight);
            }

            byte r = isOpen ? (byte)255 : (byte)200;
            byte g = isOpen ? (byte)255 : (byte)200;
            byte b = isOpen ? (byte)255 : (byte)200;
            _font.RenderText(_renderer, _menus[i], _menuX[i], 5, 1, r, g, b, 255);
        }

        // Render open dropdown menu
        if (_openMenu >= 0)
        {
            RenderDropdown(_openMenu);
        }
    }

    private void RenderDropdown(int menuIndex)
    {
        var items = _menuItems[menuIndex];
        int menuX = _menuX[menuIndex];
        int menuY = Height;
        int itemHeight = 22;
        int menuWidth = GetMenuWidth(menuIndex);
        int menuHeight = items.Length * itemHeight;

        // Shadow
        SDL.SetRenderDrawColor(_renderer, 0, 0, 0, 100);
        var shadow = new SDLRect { X = menuX + 3, Y = menuY + 3, W = menuWidth, H = menuHeight };
        SDL.RenderFillRect(_renderer, &shadow);

        // Background
        SDL.SetRenderDrawColor(_renderer, 50, 53, 60, 255);
        var bg = new SDLRect { X = menuX, Y = menuY, W = menuWidth, H = menuHeight };
        SDL.RenderFillRect(_renderer, &bg);

        // Border
        SDL.SetRenderDrawColor(_renderer, 70, 75, 85, 255);
        SDL.RenderDrawRect(_renderer, &bg);

        // Items
        for (int i = 0; i < items.Length; i++)
        {
            int itemY = menuY + i * itemHeight;

            if (items[i] == "-")
            {
                // Separator
                SDL.SetRenderDrawColor(_renderer, 70, 75, 85, 255);
                var sep = new SDLRect { X = menuX + 5, Y = itemY + itemHeight / 2, W = menuWidth - 10, H = 1 };
                SDL.RenderFillRect(_renderer, &sep);
            }
            else
            {
                // Highlight hovered item
                if (i == _hoveredItem)
                {
                    SDL.SetRenderDrawColor(_renderer, 70, 100, 140, 255);
                    var highlight = new SDLRect { X = menuX + 2, Y = itemY + 2, W = menuWidth - 4, H = itemHeight - 4 };
                    SDL.RenderFillRect(_renderer, &highlight);
                }

                byte r = i == _hoveredItem ? (byte)255 : (byte)200;
                byte g = i == _hoveredItem ? (byte)255 : (byte)200;
                byte b = i == _hoveredItem ? (byte)255 : (byte)200;
                _font.RenderText(_renderer, items[i], menuX + 10, itemY + 5, 1, r, g, b, 255);
            }
        }
    }

    public void Close()
    {
        _openMenu = -1;
        _hoveredItem = -1;
    }
}
