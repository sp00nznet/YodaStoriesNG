using Hexa.NET.SDL2;
using YodaStoriesNG.Engine.Rendering;

namespace YodaStoriesNG.Engine.UI;

/// <summary>
/// Window that displays keyboard and controller control mappings.
/// </summary>
public unsafe class ControlsWindow : IDisposable
{
    private SDLWindow* _window;
    private SDLRenderer* _renderer;
    private BitmapFont? _font;
    private bool _isOpen = false;
    private uint _windowId;
    private int _scrollOffset = 0;

    private const int WindowWidth = 600;
    private const int WindowHeight = 500;

    // Which control set to show
    private bool _showController = false;

    public bool IsOpen => _isOpen;

    // Keyboard controls
    private static readonly (string action, string key)[] KeyboardControls = new[]
    {
        ("Movement", ""),
        ("Move Up", "Arrow Up / W"),
        ("Move Down", "Arrow Down / S"),
        ("Move Left", "Arrow Left / A"),
        ("Move Right", "Arrow Right / D"),
        ("Pull Block", "Shift + Direction"),
        ("", ""),
        ("Actions", ""),
        ("Use Item / Attack / Talk", "Space"),
        ("Toggle Weapon", "Tab"),
        ("Select Inventory 1-8", "1, 2, 3, 4, 5, 6, 7, 8"),
        ("Travel (X-Wing)", "X"),
        ("Show Objective", "O"),
        ("", ""),
        ("Game", ""),
        ("New Game / Restart", "R"),
        ("Toggle Sound", "M"),
        ("Quit", "Escape"),
        ("", ""),
        ("Debug", ""),
        ("Toggle Debug Overlay", "F1"),
        ("Toggle Map Viewer", "F2"),
        ("Toggle Script Editor", "F3"),
        ("Toggle Asset Viewer", "F4"),
        ("Next Zone", "N"),
        ("Previous Zone", "P"),
        ("Find Zone with Content", "F"),
        ("Inspect (Console)", "I"),
        ("Toggle Bot", "B"),
    };

    // Controller controls
    private static readonly (string action, string button)[] ControllerControls = new[]
    {
        ("Movement", ""),
        ("Move", "D-Pad / Left Stick"),
        ("", ""),
        ("Actions", ""),
        ("Use Item / Attack / Talk", "A Button"),
        ("Dismiss Dialogue", "B Button"),
        ("Travel (X-Wing)", "X Button"),
        ("Show Objective", "Y Button"),
        ("Toggle Weapon", "LB / RB"),
        ("", ""),
        ("Game", ""),
        ("New Game / Restart", "Start"),
        ("Quit", "Back/Select"),
        ("", ""),
        ("Notes", ""),
        ("Analog Deadzone", "8000 (of 32768)"),
        ("Movement Rate", "Varies with stick"),
    };

    public void Open(bool showController = false)
    {
        if (_isOpen)
        {
            Close();
        }

        _showController = showController;
        _scrollOffset = 0;

        string title = showController ? "Controller Controls" : "Keyboard Controls";

        _window = SDL.CreateWindow(
            title,
            150, 100,
            WindowWidth, WindowHeight,
            (uint)(SDLWindowFlags.Shown));

        if (_window == null)
        {
            Console.WriteLine($"Failed to create controls window: {SDL.GetErrorS()}");
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
    }

    public void Close()
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
        }

        if (evt->Type == (uint)SDLEventType.Mousewheel && evt->Wheel.WindowID == _windowId)
        {
            _scrollOffset -= evt->Wheel.Y * 2;
            _scrollOffset = Math.Max(0, _scrollOffset);
            return true;
        }

        if (evt->Type == (uint)SDLEventType.Keydown && evt->Key.WindowID == _windowId)
        {
            if (evt->Key.Keysym.Sym == 27) // Escape
            {
                Close();
                return true;
            }
        }

        return false;
    }

    public void Render()
    {
        if (!_isOpen || _renderer == null || _font == null) return;

        // Background
        SDL.SetRenderDrawColor(_renderer, 30, 32, 40, 255);
        SDL.RenderClear(_renderer);

        // Header
        SDL.SetRenderDrawColor(_renderer, 45, 48, 58, 255);
        var headerRect = new SDLRect { X = 0, Y = 0, W = WindowWidth, H = 40 };
        SDL.RenderFillRect(_renderer, &headerRect);

        string title = _showController ? "CONTROLLER CONTROLS (Xbox)" : "KEYBOARD CONTROLS";
        int titleWidth = _font.GetTextWidth(title);
        _font.RenderText(_renderer, title, WindowWidth / 2 - titleWidth / 2, 12, 1, 255, 255, 100, 255);

        // Controls list
        int y = 50 - _scrollOffset * 20;
        int leftCol = 30;
        int rightCol = 300;
        int lineHeight = 22;

        var controls = _showController ? ControllerControls : KeyboardControls;

        foreach (var (action, binding) in controls)
        {
            if (y > 40 && y < WindowHeight - 30)
            {
                if (string.IsNullOrEmpty(action) && string.IsNullOrEmpty(binding))
                {
                    // Empty line for spacing
                }
                else if (string.IsNullOrEmpty(binding))
                {
                    // Section header
                    SDL.SetRenderDrawColor(_renderer, 50, 55, 70, 255);
                    var sectionRect = new SDLRect { X = 20, Y = y - 2, W = WindowWidth - 40, H = lineHeight };
                    SDL.RenderFillRect(_renderer, &sectionRect);

                    _font.RenderText(_renderer, action, leftCol, y, 1, 100, 200, 255, 255);
                }
                else
                {
                    // Control binding
                    _font.RenderText(_renderer, action, leftCol, y, 1, 200, 200, 200, 255);
                    _font.RenderText(_renderer, binding, rightCol, y, 1, 150, 255, 150, 255);
                }
            }
            y += lineHeight;
        }

        // Footer
        SDL.SetRenderDrawColor(_renderer, 40, 42, 50, 255);
        var footerRect = new SDLRect { X = 0, Y = WindowHeight - 35, W = WindowWidth, H = 35 };
        SDL.RenderFillRect(_renderer, &footerRect);

        _font.RenderText(_renderer, "Press ESC to close", WindowWidth / 2 - 70, WindowHeight - 22, 1, 120, 120, 140, 255);

        SDL.RenderPresent(_renderer);
    }

    public void Dispose() => Close();
}
