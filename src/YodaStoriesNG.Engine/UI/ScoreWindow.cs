using Hexa.NET.SDL2;
using YodaStoriesNG.Engine.Data;
using YodaStoriesNG.Engine.Game;
using YodaStoriesNG.Engine.Rendering;

namespace YodaStoriesNG.Engine.UI;

/// <summary>
/// Displays the end-game score (Force Factor for Yoda, Indy Quotient for Indiana Jones).
/// </summary>
public unsafe class ScoreWindow : IDisposable
{
    private SDLWindow* _window;
    private SDLRenderer* _renderer;
    private BitmapFont? _font;
    private bool _isOpen = false;
    private uint _windowId;
    private GameType _gameType;

    private int _totalScore;
    private int _timeScore;
    private int _puzzleScore;
    private int _difficultyScore;
    private int _explorationScore;
    private int _gamesWon;
    private TimeSpan _elapsedTime;

    private const int WindowWidth = 400;
    private const int WindowHeight = 350;

    public bool IsOpen => _isOpen;
    public event System.Action? OnClose;

    public void Show(GameState state, GameType gameType)
    {
        if (_isOpen) return;

        _gameType = gameType;
        var (total, time, puzzles, difficulty, exploration) = state.CalculateScore();
        _totalScore = total;
        _timeScore = time;
        _puzzleScore = puzzles;
        _difficultyScore = difficulty;
        _explorationScore = exploration;
        _gamesWon = state.GamesWon;
        _elapsedTime = DateTime.Now - state.GameStartTime;

        string title = gameType == GameType.IndianaJones ? "Indy Quotient" : "Force Factor";

        _window = SDL.CreateWindow(
            title,
            200, 150,
            WindowWidth, WindowHeight,
            (uint)(SDLWindowFlags.Shown));

        if (_window == null)
        {
            Console.WriteLine($"Failed to create score window: {SDL.GetErrorS()}");
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
        OnClose?.Invoke();
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
            if (keyCode == (int)SDLKeyCode.Escape || keyCode == (int)SDLKeyCode.Return ||
                keyCode == (int)SDLKeyCode.Space)
            {
                Close();
                return true;
            }
        }

        if (evt->Type == (uint)SDLEventType.Mousebuttondown && evt->Button.WindowID == _windowId)
        {
            int my = evt->Button.Y;
            // Check if clicked on OK button
            if (my >= WindowHeight - 55 && my <= WindowHeight - 25)
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

        // Background gradient
        SDL.SetRenderDrawColor(_renderer, 20, 30, 50, 255);
        SDL.RenderClear(_renderer);

        // Header
        string title = _gameType == GameType.IndianaJones ? "INDY QUOTIENT" : "FORCE FACTOR";
        SDL.SetRenderDrawColor(_renderer, 40, 60, 90, 255);
        var headerRect = new SDLRect { X = 0, Y = 0, W = WindowWidth, H = 50 };
        SDL.RenderFillRect(_renderer, &headerRect);

        byte r = _gameType == GameType.IndianaJones ? (byte)255 : (byte)100;
        byte g = _gameType == GameType.IndianaJones ? (byte)200 : (byte)255;
        byte b = _gameType == GameType.IndianaJones ? (byte)100 : (byte)100;
        RenderTextCentered(title, WindowWidth / 2, 18, 2, r, g, b);

        // Score breakdown
        int y = 70;
        int labelX = 30;
        int valueX = WindowWidth - 80;

        // Total score (large)
        RenderTextCentered($"Total Score: {_totalScore}", WindowWidth / 2, y, 1, 255, 255, 255);
        y += 35;

        // Separator
        SDL.SetRenderDrawColor(_renderer, 60, 80, 120, 255);
        var sepRect = new SDLRect { X = 30, Y = y, W = WindowWidth - 60, H = 1 };
        SDL.RenderFillRect(_renderer, &sepRect);
        y += 15;

        // Score components
        RenderScoreLine("Time Bonus:", _timeScore, 200, labelX, valueX, y);
        y += 22;
        RenderScoreLine("Puzzles Solved:", _puzzleScore, 100, labelX, valueX, y);
        y += 22;
        RenderScoreLine("Difficulty:", _difficultyScore, 100, labelX, valueX, y);
        y += 22;
        RenderScoreLine("Exploration:", _explorationScore, 100, labelX, valueX, y);
        y += 35;

        // Time elapsed
        string timeStr = $"Time: {(int)_elapsedTime.TotalMinutes}:{_elapsedTime.Seconds:D2}";
        _font.RenderText(_renderer, timeStr, labelX, y, 1, 150, 150, 180, 255);
        y += 22;

        // Games won
        string wonStr = $"Games Won: {_gamesWon}";
        _font.RenderText(_renderer, wonStr, labelX, y, 1, 150, 150, 180, 255);
        y += 30;

        // Rating
        string rating = GetRating(_totalScore);
        RenderTextCentered(rating, WindowWidth / 2, y, 1, 255, 220, 100);

        // OK button
        int btnX = WindowWidth / 2 - 50;
        int btnY = WindowHeight - 55;
        SDL.SetRenderDrawColor(_renderer, 60, 100, 80, 255);
        var btnRect = new SDLRect { X = btnX, Y = btnY, W = 100, H = 30 };
        SDL.RenderFillRect(_renderer, &btnRect);
        SDL.SetRenderDrawColor(_renderer, 80, 130, 100, 255);
        SDL.RenderDrawRect(_renderer, &btnRect);
        RenderTextCentered("OK", WindowWidth / 2, btnY + 8, 1, 200, 255, 200);

        // Footer
        _font.RenderText(_renderer, "Press Enter or click to continue", 85, WindowHeight - 18, 1, 100, 100, 120, 255);

        SDL.RenderPresent(_renderer);
    }

    private void RenderScoreLine(string label, int score, int max, int labelX, int valueX, int y)
    {
        _font?.RenderText(_renderer, label, labelX, y, 1, 180, 180, 200, 255);
        string valueStr = $"{score} / {max}";
        _font?.RenderText(_renderer, valueStr, valueX, y, 1, 200, 200, 220, 255);

        // Progress bar
        int barX = labelX + 140;
        int barWidth = valueX - barX - 20;
        int barHeight = 10;
        int barY = y + 3;

        SDL.SetRenderDrawColor(_renderer, 40, 50, 70, 255);
        var bgRect = new SDLRect { X = barX, Y = barY, W = barWidth, H = barHeight };
        SDL.RenderFillRect(_renderer, &bgRect);

        int fillWidth = (int)(barWidth * score / (float)max);
        byte barR = score >= max * 0.8 ? (byte)100 : score >= max * 0.5 ? (byte)200 : (byte)200;
        byte barG = score >= max * 0.8 ? (byte)200 : score >= max * 0.5 ? (byte)200 : (byte)100;
        byte barB = 100;
        SDL.SetRenderDrawColor(_renderer, barR, barG, barB, 255);
        var fillRect = new SDLRect { X = barX, Y = barY, W = fillWidth, H = barHeight };
        SDL.RenderFillRect(_renderer, &fillRect);
    }

    private string GetRating(int score)
    {
        if (score >= 450) return "LEGENDARY HERO!";
        if (score >= 400) return "Jedi Master";
        if (score >= 350) return "Jedi Knight";
        if (score >= 300) return "Padawan";
        if (score >= 250) return "Force Sensitive";
        if (score >= 200) return "Adventurer";
        return "Beginner";
    }

    private void RenderTextCentered(string text, int centerX, int y, int scale, byte r, byte g, byte b)
    {
        int width = _font!.GetTextWidth(text) * scale;
        _font.RenderText(_renderer, text, centerX - width / 2, y, scale, r, g, b, 255);
    }

    public void Dispose() => Close();
}
