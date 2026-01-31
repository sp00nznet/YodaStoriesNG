using Hexa.NET.SDL2;
using YodaStoriesNG.Engine.Rendering;

namespace YodaStoriesNG.Engine.UI;

/// <summary>
/// Title screen shown when the game starts.
/// Displays the original game startup image from the DTA file.
/// </summary>
public unsafe class TitleScreen : IDisposable
{
    private readonly BitmapFont _font;
    private readonly byte[] _startupImageData;
    private SDLRenderer* _renderer;
    private SDLTexture* _startupTexture;

    private const int StartupImageWidth = 288;
    private const int StartupImageHeight = 288;

    public bool IsActive { get; private set; } = true;

    public event System.Action? OnStartGame;

    public TitleScreen(BitmapFont font, byte[] startupImageData)
    {
        _font = font;
        _startupImageData = startupImageData;
    }

    public void SetRenderer(SDLRenderer* renderer)
    {
        _renderer = renderer;
        CreateStartupTexture();
    }

    private void CreateStartupTexture()
    {
        if (_renderer == null || _startupImageData.Length == 0)
            return;

        // Expected size is 288x288 = 82944 bytes
        if (_startupImageData.Length != StartupImageWidth * StartupImageHeight)
        {
            Console.WriteLine($"Warning: Startup image size mismatch. Expected {StartupImageWidth * StartupImageHeight}, got {_startupImageData.Length}");
            return;
        }

        // Convert indexed pixel data to ARGB32 using the palette
        var pixels = new uint[StartupImageWidth * StartupImageHeight];
        for (int i = 0; i < _startupImageData.Length; i++)
        {
            pixels[i] = Palette.GetColor(_startupImageData[i]);
        }

        // Create texture
        _startupTexture = SDL.CreateTexture(
            _renderer,
            (uint)SDLPixelFormatEnum.Argb8888,
            (int)SDLTextureAccess.Static,
            StartupImageWidth, StartupImageHeight);

        if (_startupTexture != null)
        {
            fixed (uint* pixelPtr = pixels)
            {
                SDL.UpdateTexture(_startupTexture, null, pixelPtr, StartupImageWidth * 4);
            }
            Console.WriteLine("Created startup screen texture from game assets");
        }
    }

    public bool HandleEvent(SDLEvent* evt)
    {
        if (!IsActive) return false;

        if (evt->Type == (uint)SDLEventType.Keydown)
        {
            var key = evt->Key.Keysym.Sym;

            // Enter, Space, or any key starts the game
            if (key == 13 || key == 32 || (key >= 32 && key < 127))
            {
                StartGame();
                return true;
            }
        }

        if (evt->Type == (uint)SDLEventType.Mousebuttondown)
        {
            StartGame();
            return true;
        }

        return true; // Consume all events while title screen is active
    }

    private void StartGame()
    {
        IsActive = false;
        OnStartGame?.Invoke();
    }

    public void Render()
    {
        if (!IsActive || _renderer == null) return;

        // Dark background
        SDL.SetRenderDrawColor(_renderer, 0, 0, 0, 255);
        SDL.RenderClear(_renderer);

        int centerX = 400;
        int centerY = 300;

        // Render the startup image centered on screen
        if (_startupTexture != null)
        {
            int imgX = centerX - StartupImageWidth / 2;
            int imgY = centerY - StartupImageHeight / 2 - 30; // Offset up a bit for prompt

            var dstRect = new SDLRect { X = imgX, Y = imgY, W = StartupImageWidth, H = StartupImageHeight };
            SDL.RenderCopy(_renderer, _startupTexture, null, &dstRect);
        }
        else
        {
            // Fallback if no texture - show simple text
            RenderTextCentered("YODA STORIES", centerX, 200, 2, 100, 255, 100);
            RenderTextCentered("Next Generation", centerX, 240, 1, 150, 180, 100);
        }

        // Prompt - pulsing (below the image)
        var pulse = (byte)(180 + (int)(50 * Math.Sin(DateTime.Now.Ticks / 2000000.0)));
        int promptY = centerY + StartupImageHeight / 2 + 10;
        RenderTextCentered("Press any key or click to start", centerX, promptY, 1, pulse, 255, pulse);
    }

    private void RenderTextCentered(string text, int centerX, int y, int scale, byte r, byte g, byte b)
    {
        int width = _font.GetTextWidth(text) * scale;
        _font.RenderText(_renderer, text, centerX - width / 2, y, scale, r, g, b, 255);
    }

    public void Show()
    {
        IsActive = true;
    }

    public void Hide()
    {
        IsActive = false;
    }

    public void Dispose()
    {
        if (_startupTexture != null)
        {
            SDL.DestroyTexture(_startupTexture);
            _startupTexture = null;
        }
    }
}
