using Hexa.NET.SDL2;
using YodaStoriesNG.Engine.Rendering;

namespace YodaStoriesNG.Engine.UI;

/// <summary>
/// About window showing game info, GitHub link, and legal notice.
/// </summary>
public unsafe class AboutWindow : IDisposable
{
    private SDLWindow* _window;
    private SDLRenderer* _renderer;
    private BitmapFont? _font;
    private bool _isOpen = false;
    private uint _windowId;

    private const int WindowWidth = 520;
    private const int WindowHeight = 400;

    public bool IsOpen => _isOpen;

    public void Open()
    {
        if (_isOpen)
        {
            // Bring to front
            if (_window != null)
                SDL.RaiseWindow(_window);
            return;
        }

        _window = SDL.CreateWindow(
            "About Yoda Stories NG",
            200, 150,
            WindowWidth, WindowHeight,
            (uint)(SDLWindowFlags.Shown));

        if (_window == null)
        {
            Console.WriteLine($"Failed to create about window: {SDL.GetErrorS()}");
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

        if (evt->Type == (uint)SDLEventType.Keydown && evt->Key.WindowID == _windowId)
        {
            var keyCode = (int)evt->Key.Keysym.Sym;
            if (keyCode == (int)SDLKeyCode.Escape || keyCode == (int)SDLKeyCode.Return)
            {
                Close();
                return true;
            }
        }

        if (evt->Type == (uint)SDLEventType.Mousebuttondown && evt->Button.WindowID == _windowId)
        {
            int mx = evt->Button.X;
            int my = evt->Button.Y;

            // Check if clicked on GitHub link area
            if (my >= 140 && my <= 160 && mx >= 50 && mx <= 450)
            {
                OpenGitHub();
                return true;
            }

            // Check if clicked on Close button
            if (my >= WindowHeight - 50 && my <= WindowHeight - 20 &&
                mx >= WindowWidth / 2 - 50 && mx <= WindowWidth / 2 + 50)
            {
                Close();
                return true;
            }

            return true;
        }

        return false;
    }

    private void OpenGitHub()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://github.com/sp00nznet/YodaStoriesNG",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to open GitHub: {ex.Message}");
        }
    }

    public void Render()
    {
        if (!_isOpen || _renderer == null || _font == null) return;

        // Background
        SDL.SetRenderDrawColor(_renderer, 30, 35, 45, 255);
        SDL.RenderClear(_renderer);

        // Header
        SDL.SetRenderDrawColor(_renderer, 45, 50, 65, 255);
        var headerRect = new SDLRect { X = 0, Y = 0, W = WindowWidth, H = 60 };
        SDL.RenderFillRect(_renderer, &headerRect);

        // Title
        RenderTextCentered("DESKTOP ADVENTURES NG", WindowWidth / 2, 15, 2, 100, 255, 100);
        RenderTextCentered("Yoda Stories & Indiana Jones", WindowWidth / 2, 42, 1, 180, 180, 180);

        // Version
        int y = 80;
        _font.RenderText(_renderer, "Version: 0.1.0", 50, y, 1, 200, 200, 200, 255);
        y += 25;

        // Built with
        _font.RenderText(_renderer, "Built with: C# / .NET 8 / SDL2", 50, y, 1, 150, 150, 180, 255);
        y += 35;

        // GitHub link (clickable)
        _font.RenderText(_renderer, "GitHub:", 50, y, 1, 150, 150, 180, 255);
        _font.RenderText(_renderer, "github.com/sp00nznet/YodaStoriesNG", 120, y, 1, 100, 180, 255, 255);

        // Underline for link
        SDL.SetRenderDrawColor(_renderer, 100, 180, 255, 255);
        var linkRect = new SDLRect { X = 120, Y = y + 12, W = 280, H = 1 };
        SDL.RenderFillRect(_renderer, &linkRect);
        y += 40;

        // Legal notice section
        SDL.SetRenderDrawColor(_renderer, 40, 45, 55, 255);
        var legalRect = new SDLRect { X = 20, Y = y, W = WindowWidth - 40, H = 120 };
        SDL.RenderFillRect(_renderer, &legalRect);

        y += 10;
        _font.RenderText(_renderer, "LEGAL NOTICE", 35, y, 1, 255, 200, 100, 255);
        y += 20;

        var legalLines = new[]
        {
            "This is a fan project not affiliated with or endorsed by",
            "LucasArts, Disney, or any related entities.",
            "Star Wars, Yoda Stories, and Indiana Jones are trademarks",
            "of Lucasfilm Ltd. You must own a legal copy to play."
        };

        foreach (var line in legalLines)
        {
            _font.RenderText(_renderer, line, 35, y, 1, 160, 160, 170, 255);
            y += 16;
        }

        // Close button
        int btnX = WindowWidth / 2 - 50;
        int btnY = WindowHeight - 50;
        SDL.SetRenderDrawColor(_renderer, 60, 80, 100, 255);
        var btnRect = new SDLRect { X = btnX, Y = btnY, W = 100, H = 30 };
        SDL.RenderFillRect(_renderer, &btnRect);

        SDL.SetRenderDrawColor(_renderer, 80, 100, 130, 255);
        SDL.RenderDrawRect(_renderer, &btnRect);

        RenderTextCentered("Close", WindowWidth / 2, btnY + 8, 1, 200, 200, 200);

        // Footer hint
        _font.RenderText(_renderer, "Press ESC or Enter to close", 150, WindowHeight - 18, 1, 100, 100, 120, 255);

        SDL.RenderPresent(_renderer);
    }

    private void RenderTextCentered(string text, int centerX, int y, int scale, byte r, byte g, byte b)
    {
        int width = _font!.GetTextWidth(text) * scale;
        _font.RenderText(_renderer, text, centerX - width / 2, y, scale, r, g, b, 255);
    }

    public void Dispose() => Close();
}
