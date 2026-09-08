using Hexa.NET.SDL2;
using YodaStoriesNG.Engine.Game;
using YodaStoriesNG.Engine.Rendering;

namespace YodaStoriesNG.Engine.UI;

/// <summary>
/// The menu bar for platforms that do not have user32.dll - which is to say Linux and
/// macOS. Same menus as <see cref="NativeMenuBar"/>, drawn with SDL into the strip the
/// window reserves above the game area (<see cref="GameRenderer.MenuBarHeight"/>), so it
/// covers no gameplay and needs no window manager cooperation.
///
/// Items carry their own action rather than sitting in a parallel array indexed by a
/// switch, which is how the earlier version of this file managed to label Asset Viewer
/// "F2" while the engine bound it to F4.
/// </summary>
public unsafe class MenuBar : IMenuBar
{
    public const int Height = 22;
    private const int ItemHeight = 22;
    private const int Escape = 27;

    private readonly BitmapFont _font;
    private SDLRenderer* _renderer;
    private uint _windowId;

    private readonly Menu[] _menus;
    private int _openMenu = -1;
    private int _hoveredItem = -1;

    private sealed record Item(string Label, Action? Run);

    private sealed class Menu
    {
        public required string Title { get; init; }
        public required Item[] Items { get; init; }
        public int X { get; set; }
        public int TitleWidth { get; set; }
        public int Width { get; set; }
    }

    public event Action<WorldSize>? OnNewGame;
    public event Action? OnSaveGame;
    public event Action? OnSaveGameAs;
    public event Action? OnLoadGame;
    public event Action? OnExit;
    public event Action? OnAssetViewer;
    public event Action? OnScriptEditor;
    public event Action? OnMapViewer;
    public event Action? OnSaveInspector;
    public event Action? OnZoneEditor;
    public event Action? OnEnableBot;
    public event Action? OnDisableBot;
    public event Action<int>? OnSetScale;
    public event Action? OnShowKeyboardControls;
    public event Action? OnShowControllerControls;
    public event Action? OnSelectDataFile;
    public event Action? OnShowAbout;
    public event Action? OnShowHighScores;

    public bool IsMenuOpen => _openMenu >= 0;

    public MenuBar(BitmapFont font)
    {
        _font = font;

        // The events are subscribed after construction, so each item invokes the field
        // rather than capturing a handler that is still null right now.
        _menus = new[]
        {
            new Menu
            {
                Title = "File",
                Items = new[]
                {
                    new Item("New Game: Small", () => OnNewGame?.Invoke(WorldSize.Small)),
                    new Item("New Game: Medium", () => OnNewGame?.Invoke(WorldSize.Medium)),
                    new Item("New Game: Large", () => OnNewGame?.Invoke(WorldSize.Large)),
                    new Item("New Game: X-tra Large", () => OnNewGame?.Invoke(WorldSize.XtraLarge)),
                    Divider(),
                    new Item("Save Game", () => OnSaveGame?.Invoke()),
                    new Item("Save As...", () => OnSaveGameAs?.Invoke()),
                    new Item("Load Game", () => OnLoadGame?.Invoke()),
                    Divider(),
                    new Item("Exit", () => OnExit?.Invoke()),
                },
            },
            new Menu
            {
                Title = "Debug",
                Items = new[]
                {
                    new Item("Asset Viewer (F4)", () => OnAssetViewer?.Invoke()),
                    new Item("Script Editor (F3)", () => OnScriptEditor?.Invoke()),
                    new Item("Map Viewer (F2)", () => OnMapViewer?.Invoke()),
                    new Item("Save Editor (F8)", () => OnSaveInspector?.Invoke()),
                    new Item("Zone Editor (F9)", () => OnZoneEditor?.Invoke()),
                    Divider(),
                    new Item("Enable Bot", () => OnEnableBot?.Invoke()),
                    new Item("Disable Bot", () => OnDisableBot?.Invoke()),
                },
            },
            new Menu
            {
                Title = "Config",
                Items = new[]
                {
                    new Item("Graphics: 1x Scale (F5)", () => OnSetScale?.Invoke(1)),
                    new Item("Graphics: 2x Scale (F6)", () => OnSetScale?.Invoke(2)),
                    new Item("Graphics: 4x Scale (F7)", () => OnSetScale?.Invoke(4)),
                    Divider(),
                    new Item("Keyboard Controls", () => OnShowKeyboardControls?.Invoke()),
                    new Item("Controller Controls", () => OnShowControllerControls?.Invoke()),
                    Divider(),
                    new Item("Select Data File...", () => OnSelectDataFile?.Invoke()),
                },
            },
            new Menu
            {
                Title = "About",
                Items = new[]
                {
                    new Item("About Desktop Adventures NG", () => OnShowAbout?.Invoke()),
                    new Item("High Scores", () => OnShowHighScores?.Invoke()),
                },
            },
        };
    }

    private static Item Divider() => new("-", null);

    private static bool IsDivider(Item item) => item.Run == null;

    public void SetRenderer(SDLRenderer* renderer, uint windowId)
    {
        _renderer = renderer;
        _windowId = windowId;
        MeasureMenus();
    }

    /// <summary>
    /// Titles are laid out left to right from whatever the font actually measures, so
    /// renaming a menu cannot silently overlap the next one.
    /// </summary>
    private void MeasureMenus()
    {
        int x = 10;
        foreach (var menu in _menus)
        {
            menu.TitleWidth = _font.GetTextWidth(menu.Title);
            menu.X = x;
            x += menu.TitleWidth + 20;

            int widest = 0;
            foreach (var item in menu.Items)
                widest = Math.Max(widest, _font.GetTextWidth(item.Label));
            menu.Width = widest + 30;
        }
    }

    public bool HandleEvent(SDLEvent* evt)
    {
        var type = (SDLEventType)evt->Type;

        if (type == SDLEventType.Mousebuttondown && evt->Button.WindowID != _windowId) return false;
        if (type == SDLEventType.Mousemotion && evt->Motion.WindowID != _windowId) return false;
        if (type == SDLEventType.Keydown && evt->Key.WindowID != _windowId) return false;

        switch (type)
        {
            case SDLEventType.Mousebuttondown:
                return HandleClick(evt->Button.X, evt->Button.Y);

            case SDLEventType.Mousemotion:
                HandleHover(evt->Motion.X, evt->Motion.Y);
                return false;

            case SDLEventType.Keydown when evt->Key.Keysym.Sym == Escape && IsMenuOpen:
                Close();
                return true;
        }

        return false;
    }

    private bool HandleClick(int x, int y)
    {
        if (y < Height)
        {
            var clicked = MenuAt(x);
            _openMenu = clicked >= 0 && clicked != _openMenu ? clicked : -1;
            _hoveredItem = -1;
            return true;
        }

        if (!IsMenuOpen)
            return false;

        var menu = _menus[_openMenu];
        var item = ItemAt(_openMenu, x, y);
        Close();

        // Clicking away from the dropdown only dismisses it.
        if (item >= 0)
            menu.Items[item].Run?.Invoke();

        return true;
    }

    private void HandleHover(int x, int y)
    {
        if (!IsMenuOpen)
            return;

        // Sliding along the bar with a menu open switches menus, the way a real one does.
        if (y < Height)
        {
            var hovered = MenuAt(x);
            if (hovered >= 0 && hovered != _openMenu)
            {
                _openMenu = hovered;
                _hoveredItem = -1;
            }
            return;
        }

        _hoveredItem = ItemAt(_openMenu, x, y);
    }

    private int MenuAt(int x)
    {
        for (int i = 0; i < _menus.Length; i++)
        {
            if (x >= _menus[i].X - 5 && x < _menus[i].X + _menus[i].TitleWidth + 5)
                return i;
        }
        return -1;
    }

    private int ItemAt(int menuIndex, int x, int y)
    {
        if (menuIndex < 0)
            return -1;

        var menu = _menus[menuIndex];
        if (x < menu.X || x >= menu.X + menu.Width)
            return -1;

        int index = (y - Height) / ItemHeight;
        if (index < 0 || index >= menu.Items.Length || IsDivider(menu.Items[index]))
            return -1;

        return index;
    }

    public void Render()
    {
        if (_renderer == null)
            return;

        SDL.SetRenderDrawColor(_renderer, 45, 48, 55, 255);
        var bar = new SDLRect { X = 0, Y = 0, W = GameRenderer.WindowWidth, H = Height };
        SDL.RenderFillRect(_renderer, &bar);

        SDL.SetRenderDrawColor(_renderer, 30, 32, 38, 255);
        var border = new SDLRect { X = 0, Y = Height - 1, W = GameRenderer.WindowWidth, H = 1 };
        SDL.RenderFillRect(_renderer, &border);

        for (int i = 0; i < _menus.Length; i++)
        {
            bool isOpen = _openMenu == i;
            if (isOpen)
            {
                SDL.SetRenderDrawColor(_renderer, 60, 65, 75, 255);
                var highlight = new SDLRect
                {
                    X = _menus[i].X - 5,
                    Y = 0,
                    W = _menus[i].TitleWidth + 10,
                    H = Height,
                };
                SDL.RenderFillRect(_renderer, &highlight);
            }

            byte shade = isOpen ? (byte)255 : (byte)200;
            _font.RenderText(_renderer, _menus[i].Title, _menus[i].X, 5, 1, shade, shade, shade, 255);
        }

        if (IsMenuOpen)
            RenderDropdown(_menus[_openMenu]);
    }

    private void RenderDropdown(Menu menu)
    {
        int top = Height;
        int height = menu.Items.Length * ItemHeight;

        SDL.SetRenderDrawColor(_renderer, 0, 0, 0, 100);
        var shadow = new SDLRect { X = menu.X + 3, Y = top + 3, W = menu.Width, H = height };
        SDL.RenderFillRect(_renderer, &shadow);

        SDL.SetRenderDrawColor(_renderer, 50, 53, 60, 255);
        var background = new SDLRect { X = menu.X, Y = top, W = menu.Width, H = height };
        SDL.RenderFillRect(_renderer, &background);

        SDL.SetRenderDrawColor(_renderer, 70, 75, 85, 255);
        SDL.RenderDrawRect(_renderer, &background);

        for (int i = 0; i < menu.Items.Length; i++)
        {
            int y = top + i * ItemHeight;

            if (IsDivider(menu.Items[i]))
            {
                SDL.SetRenderDrawColor(_renderer, 70, 75, 85, 255);
                var line = new SDLRect
                {
                    X = menu.X + 5,
                    Y = y + ItemHeight / 2,
                    W = menu.Width - 10,
                    H = 1,
                };
                SDL.RenderFillRect(_renderer, &line);
                continue;
            }

            if (i == _hoveredItem)
            {
                SDL.SetRenderDrawColor(_renderer, 70, 100, 140, 255);
                var highlight = new SDLRect
                {
                    X = menu.X + 2,
                    Y = y + 2,
                    W = menu.Width - 4,
                    H = ItemHeight - 4,
                };
                SDL.RenderFillRect(_renderer, &highlight);
            }

            byte shade = i == _hoveredItem ? (byte)255 : (byte)200;
            _font.RenderText(_renderer, menu.Items[i].Label, menu.X + 10, y + 5, 1, shade, shade, shade, 255);
        }
    }

    public void Close()
    {
        _openMenu = -1;
        _hoveredItem = -1;
    }

    public void Dispose()
    {
    }
}
