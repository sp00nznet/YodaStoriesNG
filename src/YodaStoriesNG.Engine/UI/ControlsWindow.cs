using Hexa.NET.SDL2;
using YodaStoriesNG.Engine.Game;
using YodaStoriesNG.Engine.Rendering;

namespace YodaStoriesNG.Engine.UI;

/// <summary>
/// Window that displays and allows editing keyboard and controller control mappings.
/// </summary>
public unsafe class ControlsWindow : IDisposable
{
    private SDLWindow* _window;
    private SDLRenderer* _renderer;
    private BitmapFont? _font;
    private bool _isOpen = false;
    private uint _windowId;
    private int _scrollOffset = 0;

    private const int WindowWidth = 650;
    private const int WindowHeight = 550;
    private const int LineHeight = 24;

    // Which control set to show
    private bool _showController = false;

    // Keyboard binding editing
    private KeyBindings _keyBindings;
    private string? _editingAction = null;
    private bool _editingAlternate = false;
    private int _hoveredRow = -1;

    public bool IsOpen => _isOpen;

    // Controller controls (read-only display)
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

    public ControlsWindow()
    {
        _keyBindings = KeyBindings.Load();
    }

    public KeyBindings GetKeyBindings() => _keyBindings;

    public void Open(bool showController = false)
    {
        if (_isOpen)
        {
            Close();
        }

        _showController = showController;
        _scrollOffset = 0;
        _editingAction = null;
        _hoveredRow = -1;

        string title = showController ? "Controller Controls" : "Keyboard Controls (Click to Edit)";

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

        if (evt->Type == (uint)SDLEventType.Mousemotion && evt->Motion.WindowID == _windowId)
        {
            if (!_showController && _editingAction == null)
            {
                int my = evt->Motion.Y;
                _hoveredRow = GetRowAtY(my);
            }
            return false;
        }

        if (evt->Type == (uint)SDLEventType.Mousebuttondown && evt->Button.WindowID == _windowId)
        {
            if (!_showController)
            {
                int mx = evt->Button.X;
                int my = evt->Button.Y;

                // Check for reset button click
                if (my >= WindowHeight - 35 && mx >= WindowWidth - 140 && mx < WindowWidth - 20)
                {
                    _keyBindings.SetDefaults();
                    _keyBindings.Save();
                    return true;
                }

                var clickedAction = GetActionAtY(my);
                if (clickedAction != null && _editingAction == null)
                {
                    _editingAction = clickedAction;
                    // Right half = alternate binding
                    _editingAlternate = mx > WindowWidth / 2 + 50;
                    return true;
                }
            }
            return true;
        }

        if (evt->Type == (uint)SDLEventType.Keydown && evt->Key.WindowID == _windowId)
        {
            var keyCode = (int)evt->Key.Keysym.Sym;

            // If editing a binding, capture the key
            if (_editingAction != null && !_showController)
            {
                if (keyCode == (int)SDLKeyCode.Escape)
                {
                    // Cancel editing
                    _editingAction = null;
                }
                else if (keyCode == (int)SDLKeyCode.Delete || keyCode == (int)SDLKeyCode.Backspace)
                {
                    // Clear the binding
                    if (_editingAlternate)
                        _keyBindings.ClearAlternate(_editingAction);
                    else
                        _keyBindings.SetPrimary(_editingAction, 0);
                    _keyBindings.Save();
                    _editingAction = null;
                }
                else
                {
                    // Set the new binding
                    if (_editingAlternate)
                        _keyBindings.SetAlternate(_editingAction, keyCode);
                    else
                        _keyBindings.SetPrimary(_editingAction, keyCode);
                    _keyBindings.Save();
                    _editingAction = null;
                }
                return true;
            }

            if (keyCode == (int)SDLKeyCode.Escape)
            {
                Close();
                return true;
            }
        }

        return false;
    }

    private int GetRowAtY(int y)
    {
        int contentY = 50 - _scrollOffset * LineHeight;
        int row = 0;

        foreach (var (category, actions) in KeyBindings.Categories)
        {
            if (y >= contentY && y < contentY + LineHeight)
                return -1; // Category header
            contentY += LineHeight;

            foreach (var action in actions)
            {
                if (y >= contentY && y < contentY + LineHeight)
                    return row;
                contentY += LineHeight;
                row++;
            }
            contentY += 10; // Gap between categories
        }
        return -1;
    }

    private string? GetActionAtY(int y)
    {
        int contentY = 50 - _scrollOffset * LineHeight;

        foreach (var (category, actions) in KeyBindings.Categories)
        {
            contentY += LineHeight; // Skip category header

            foreach (var action in actions)
            {
                if (y >= contentY && y < contentY + LineHeight)
                    return action;
                contentY += LineHeight;
            }
            contentY += 10; // Gap between categories
        }
        return null;
    }

    public void Render()
    {
        if (!_isOpen || _renderer == null || _font == null) return;

        // Background
        SDL.SetRenderDrawColor(_renderer, 30, 32, 40, 255);
        SDL.RenderClear(_renderer);

        // Header
        SDL.SetRenderDrawColor(_renderer, 45, 48, 58, 255);
        var headerRect = new SDLRect { X = 0, Y = 0, W = WindowWidth, H = 45 };
        SDL.RenderFillRect(_renderer, &headerRect);

        string title = _showController ? "CONTROLLER CONTROLS (Xbox)" : "KEYBOARD CONTROLS";
        int titleWidth = _font.GetTextWidth(title);
        _font.RenderText(_renderer, title, WindowWidth / 2 - titleWidth / 2, 10, 1, 255, 255, 100, 255);

        if (!_showController)
        {
            _font.RenderText(_renderer, "Click a binding to change it. Press DEL to clear.", 20, 28, 1, 120, 120, 150, 255);
        }

        if (_showController)
        {
            RenderControllerControls();
        }
        else
        {
            RenderKeyboardControls();
        }

        // Footer
        SDL.SetRenderDrawColor(_renderer, 40, 42, 50, 255);
        var footerRect = new SDLRect { X = 0, Y = WindowHeight - 35, W = WindowWidth, H = 35 };
        SDL.RenderFillRect(_renderer, &footerRect);

        _font.RenderText(_renderer, "Press ESC to close", 20, WindowHeight - 22, 1, 120, 120, 140, 255);

        if (!_showController)
        {
            // Reset button
            SDL.SetRenderDrawColor(_renderer, 80, 60, 60, 255);
            var resetRect = new SDLRect { X = WindowWidth - 140, Y = WindowHeight - 30, W = 120, H = 25 };
            SDL.RenderFillRect(_renderer, &resetRect);
            _font.RenderText(_renderer, "Reset Defaults", WindowWidth - 130, WindowHeight - 22, 1, 200, 150, 150, 255);
        }

        SDL.RenderPresent(_renderer);
    }

    private void RenderKeyboardControls()
    {
        int y = 50 - _scrollOffset * LineHeight;
        int leftCol = 30;
        int primaryCol = 280;
        int altCol = 450;
        int row = 0;

        // Column headers
        if (y > 40)
        {
            _font!.RenderText(_renderer, "Action", leftCol, y, 1, 150, 150, 180, 255);
            _font!.RenderText(_renderer, "Primary", primaryCol, y, 1, 150, 150, 180, 255);
            _font!.RenderText(_renderer, "Alternate", altCol, y, 1, 150, 150, 180, 255);
        }
        y += LineHeight + 5;

        foreach (var (category, actions) in KeyBindings.Categories)
        {
            if (y > 40 && y < WindowHeight - 40)
            {
                // Category header
                SDL.SetRenderDrawColor(_renderer, 50, 55, 70, 255);
                var sectionRect = new SDLRect { X = 20, Y = y - 2, W = WindowWidth - 40, H = LineHeight };
                SDL.RenderFillRect(_renderer, &sectionRect);
                _font!.RenderText(_renderer, category, leftCol, y, 1, 100, 200, 255, 255);
            }
            y += LineHeight;

            foreach (var action in actions)
            {
                if (y > 40 && y < WindowHeight - 40)
                {
                    bool isEditing = _editingAction == action;
                    bool isHovered = _hoveredRow == row && _editingAction == null;

                    // Highlight row
                    if (isEditing || isHovered)
                    {
                        SDL.SetRenderDrawColor(_renderer, isEditing ? (byte)70 : (byte)45,
                            isEditing ? (byte)70 : (byte)48, isEditing ? (byte)90 : (byte)58, 255);
                        var rowRect = new SDLRect { X = 20, Y = y - 2, W = WindowWidth - 40, H = LineHeight };
                        SDL.RenderFillRect(_renderer, &rowRect);
                    }

                    // Action name
                    string displayName = KeyBindings.DisplayNames.TryGetValue(action, out var dn) ? dn : action;
                    _font!.RenderText(_renderer, displayName, leftCol, y, 1, 200, 200, 200, 255);

                    // Primary binding
                    if (isEditing && !_editingAlternate)
                    {
                        _font!.RenderText(_renderer, "Press a key...", primaryCol, y, 1, 255, 255, 100, 255);
                    }
                    else
                    {
                        var primaryKey = _keyBindings.Primary.TryGetValue(action, out var pk) ? KeyBindings.GetKeyName(pk) : "-";
                        _font!.RenderText(_renderer, primaryKey, primaryCol, y, 1, 150, 255, 150, 255);
                    }

                    // Alternate binding
                    if (isEditing && _editingAlternate)
                    {
                        _font!.RenderText(_renderer, "Press a key...", altCol, y, 1, 255, 255, 100, 255);
                    }
                    else
                    {
                        var altKey = _keyBindings.Alternate.TryGetValue(action, out var ak) ? KeyBindings.GetKeyName(ak) : "-";
                        _font!.RenderText(_renderer, altKey, altCol, y, 1, 150, 200, 255, 255);
                    }
                }
                y += LineHeight;
                row++;
            }
            y += 10; // Gap between categories
        }
    }

    private void RenderControllerControls()
    {
        int y = 55 - _scrollOffset * LineHeight;
        int leftCol = 30;
        int rightCol = 300;

        foreach (var (action, binding) in ControllerControls)
        {
            if (y > 40 && y < WindowHeight - 40)
            {
                if (string.IsNullOrEmpty(action) && string.IsNullOrEmpty(binding))
                {
                    // Empty line for spacing
                }
                else if (string.IsNullOrEmpty(binding))
                {
                    // Section header
                    SDL.SetRenderDrawColor(_renderer, 50, 55, 70, 255);
                    var sectionRect = new SDLRect { X = 20, Y = y - 2, W = WindowWidth - 40, H = LineHeight };
                    SDL.RenderFillRect(_renderer, &sectionRect);

                    _font!.RenderText(_renderer, action, leftCol, y, 1, 100, 200, 255, 255);
                }
                else
                {
                    // Control binding
                    _font!.RenderText(_renderer, action, leftCol, y, 1, 200, 200, 200, 255);
                    _font!.RenderText(_renderer, binding, rightCol, y, 1, 150, 255, 150, 255);
                }
            }
            y += LineHeight;
        }
    }

    public void Dispose() => Close();
}
