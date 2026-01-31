using Hexa.NET.SDL2;
using YodaStoriesNG.Engine.Data;
using YodaStoriesNG.Engine.Game;
using YodaStoriesNG.Engine.Rendering;

namespace YodaStoriesNG.Engine.UI;

/// <summary>
/// Displays high scores for both Force Factor (Yoda) and Indy Quotient (Indy).
/// </summary>
public unsafe class HighScoreWindow : IDisposable
{
    private SDLWindow* _window;
    private SDLRenderer* _renderer;
    private BitmapFont? _font;
    private bool _isOpen = false;
    private uint _windowId;
    private int _selectedTab = 0; // 0 = Yoda, 1 = Indy

    private const int WindowWidth = 450;
    private const int WindowHeight = 400;

    public bool IsOpen => _isOpen;

    public void Open()
    {
        if (_isOpen)
        {
            if (_window != null)
                SDL.RaiseWindow(_window);
            return;
        }

        _window = SDL.CreateWindow(
            "High Scores",
            200, 150,
            WindowWidth, WindowHeight,
            (uint)SDLWindowFlags.Shown);

        if (_window == null)
        {
            Console.WriteLine($"Failed to create high score window: {SDL.GetErrorS()}");
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
            if (keyCode == (int)SDLKeyCode.Escape)
            {
                Close();
                return true;
            }
            if (keyCode == (int)SDLKeyCode.Tab || keyCode == (int)SDLKeyCode.Left || keyCode == (int)SDLKeyCode.Right)
            {
                _selectedTab = _selectedTab == 0 ? 1 : 0;
                return true;
            }
        }

        if (evt->Type == (uint)SDLEventType.Mousebuttondown && evt->Button.WindowID == _windowId)
        {
            int mx = evt->Button.X;
            int my = evt->Button.Y;

            // Check tab clicks
            if (my >= 50 && my <= 80)
            {
                if (mx < WindowWidth / 2)
                    _selectedTab = 0;
                else
                    _selectedTab = 1;
                return true;
            }

            // Check close button
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

    public void Render()
    {
        if (!_isOpen || _renderer == null || _font == null) return;

        // Background
        SDL.SetRenderDrawColor(_renderer, 25, 30, 40, 255);
        SDL.RenderClear(_renderer);

        // Header
        SDL.SetRenderDrawColor(_renderer, 40, 45, 60, 255);
        var headerRect = new SDLRect { X = 0, Y = 0, W = WindowWidth, H = 45 };
        SDL.RenderFillRect(_renderer, &headerRect);

        RenderTextCentered("HIGH SCORES", WindowWidth / 2, 15, 2, 255, 215, 0);

        // Tabs
        int tabWidth = WindowWidth / 2;

        // Yoda tab
        SDL.SetRenderDrawColor(_renderer, _selectedTab == 0 ? (byte)60 : (byte)40,
            _selectedTab == 0 ? (byte)80 : (byte)45,
            _selectedTab == 0 ? (byte)60 : (byte)55, 255);
        var yodaTab = new SDLRect { X = 0, Y = 50, W = tabWidth, H = 30 };
        SDL.RenderFillRect(_renderer, &yodaTab);
        RenderTextCentered("Force Factor", tabWidth / 2, 57, 1,
            _selectedTab == 0 ? (byte)100 : (byte)150,
            _selectedTab == 0 ? (byte)255 : (byte)150,
            _selectedTab == 0 ? (byte)100 : (byte)150);

        // Indy tab
        SDL.SetRenderDrawColor(_renderer, _selectedTab == 1 ? (byte)80 : (byte)40,
            _selectedTab == 1 ? (byte)60 : (byte)45,
            _selectedTab == 1 ? (byte)40 : (byte)55, 255);
        var indyTab = new SDLRect { X = tabWidth, Y = 50, W = tabWidth, H = 30 };
        SDL.RenderFillRect(_renderer, &indyTab);
        RenderTextCentered("Indy Quotient", tabWidth + tabWidth / 2, 57, 1,
            _selectedTab == 1 ? (byte)255 : (byte)150,
            _selectedTab == 1 ? (byte)200 : (byte)150,
            _selectedTab == 1 ? (byte)100 : (byte)150);

        // Score list
        var gameType = _selectedTab == 0 ? GameType.YodaStories : GameType.IndianaJones;
        var scores = HighScoreManager.GetScores(gameType);

        int y = 95;
        int rank = 1;

        // Column headers
        _font.RenderText(_renderer, "#", 20, y, 1, 120, 120, 150, 255);
        _font.RenderText(_renderer, "Score", 50, y, 1, 120, 120, 150, 255);
        _font.RenderText(_renderer, "Rating", 120, y, 1, 120, 120, 150, 255);
        _font.RenderText(_renderer, "Size", 280, y, 1, 120, 120, 150, 255);
        _font.RenderText(_renderer, "Time", 340, y, 1, 120, 120, 150, 255);
        y += 25;

        if (scores.Count == 0)
        {
            RenderTextCentered("No scores yet!", WindowWidth / 2, y + 50, 1, 150, 150, 150);
            RenderTextCentered("Complete a 15-mission cycle", WindowWidth / 2, y + 75, 1, 120, 120, 140);
        }
        else
        {
            foreach (var score in scores.Take(10))
            {
                byte r = rank == 1 ? (byte)255 : rank == 2 ? (byte)200 : rank == 3 ? (byte)180 : (byte)160;
                byte g = rank == 1 ? (byte)215 : rank == 2 ? (byte)200 : rank == 3 ? (byte)150 : (byte)160;
                byte b = rank == 1 ? (byte)0 : rank == 2 ? (byte)200 : rank == 3 ? (byte)100 : (byte)170;

                _font.RenderText(_renderer, $"{rank}", 20, y, 1, r, g, b, 255);
                _font.RenderText(_renderer, $"{score.Score}", 50, y, 1, 200, 200, 200, 255);
                _font.RenderText(_renderer, score.Rating, 120, y, 1, 180, 180, 200, 255);
                _font.RenderText(_renderer, score.WorldSize.ToString()[0].ToString(), 280, y, 1, 150, 150, 180, 255);
                _font.RenderText(_renderer, $"{(int)score.Time.TotalMinutes}:{score.Time.Seconds:D2}", 340, y, 1, 150, 150, 180, 255);

                y += 22;
                rank++;
            }
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
        _font.RenderText(_renderer, "Tab/Arrow: Switch game | ESC: Close", 90, WindowHeight - 18, 1, 100, 100, 120, 255);

        SDL.RenderPresent(_renderer);
    }

    private void RenderTextCentered(string text, int centerX, int y, int scale, byte r, byte g, byte b)
    {
        int width = _font!.GetTextWidth(text) * scale;
        _font.RenderText(_renderer, text, centerX - width / 2, y, scale, r, g, b, 255);
    }

    public void Dispose() => Close();
}
