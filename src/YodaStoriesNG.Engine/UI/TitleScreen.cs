using Hexa.NET.SDL2;
using YodaStoriesNG.Engine.Data;
using YodaStoriesNG.Engine.Rendering;

namespace YodaStoriesNG.Engine.UI;

/// <summary>
/// Title screen shown when the game starts.
/// Displays the original game startup image from the DTA file with X-Wing flyby animation.
/// </summary>
public unsafe class TitleScreen : IDisposable
{
    private readonly BitmapFont _font;
    private readonly byte[] _startupImageData;
    private readonly List<Tile> _tiles;
    private SDLRenderer* _renderer;
    private SDLTexture* _startupTexture;
    private SDLTexture* _xwingTexture;

    private const int StartupImageWidth = 288;
    private const int StartupImageHeight = 288;

    // X-Wing flyby animation state
    private double _xwingX = -100;
    private double _xwingY = 120;
    private double _xwingAngle = 0;
    private DateTime _lastUpdate = DateTime.Now;
    private readonly Random _random = new();

    // X-Wing tile IDs (2x2 grid)
    private const int XWingTileTopLeft = 948;
    private const int XWingTileTopRight = 949;
    private const int XWingTileBottomLeft = 950;
    private const int XWingTileBottomRight = 951;

    public bool IsActive { get; private set; } = true;

    public event System.Action? OnStartGame;

    public TitleScreen(BitmapFont font, byte[] startupImageData, List<Tile> tiles)
    {
        _font = font;
        _startupImageData = startupImageData;
        _tiles = tiles;
        _xwingX = -100;
        _xwingY = 80 + _random.Next(200);
    }

    public void SetRenderer(SDLRenderer* renderer)
    {
        _renderer = renderer;
        CreateStartupTexture();
        CreateXWingTexture();
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

    private void CreateXWingTexture()
    {
        if (_renderer == null || _tiles == null || _tiles.Count <= XWingTileBottomRight)
            return;

        // Create a 64x64 texture for the 2x2 X-Wing (each tile is 32x32)
        const int tileSize = 32;
        const int textureSize = tileSize * 2;

        var pixels = new uint[textureSize * textureSize];

        // Copy the 4 tiles into the texture
        var tileOffsets = new[] {
            (XWingTileTopLeft, 0, 0),
            (XWingTileTopRight, tileSize, 0),
            (XWingTileBottomLeft, 0, tileSize),
            (XWingTileBottomRight, tileSize, tileSize)
        };

        foreach (var (tileId, offsetX, offsetY) in tileOffsets)
        {
            var tile = _tiles[tileId];
            for (int y = 0; y < tileSize; y++)
            {
                for (int x = 0; x < tileSize; x++)
                {
                    int srcIndex = y * tileSize + x;
                    int dstIndex = (offsetY + y) * textureSize + (offsetX + x);
                    pixels[dstIndex] = Palette.GetColor(tile.PixelData[srcIndex]);
                }
            }
        }

        _xwingTexture = SDL.CreateTexture(
            _renderer,
            (uint)SDLPixelFormatEnum.Argb8888,
            (int)SDLTextureAccess.Static,
            textureSize, textureSize);

        if (_xwingTexture != null)
        {
            SDL.SetTextureBlendMode(_xwingTexture, SDLBlendMode.Blend);
            fixed (uint* pixelPtr = pixels)
            {
                SDL.UpdateTexture(_xwingTexture, null, pixelPtr, textureSize * 4);
            }
            Console.WriteLine("Created X-Wing texture for title screen animation");
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

        // Update X-Wing animation
        UpdateXWingAnimation();

        // Dark background with stars
        SDL.SetRenderDrawColor(_renderer, 0, 0, 10, 255);
        SDL.RenderClear(_renderer);

        // Simple star field
        RenderStars();

        int centerX = 400;
        int centerY = 300;

        // Render X-Wing flyby (behind the title image)
        RenderXWing();

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

    private void UpdateXWingAnimation()
    {
        var now = DateTime.Now;
        var deltaTime = (now - _lastUpdate).TotalSeconds;
        _lastUpdate = now;

        // Move X-Wing across the screen
        _xwingX += 150 * deltaTime; // pixels per second

        // Slight wave motion
        _xwingAngle = Math.Sin(_xwingX / 50.0) * 5;

        // Reset when off screen
        if (_xwingX > 900)
        {
            _xwingX = -100;
            _xwingY = 60 + _random.Next(250);
        }
    }

    private void RenderStars()
    {
        // Simple pseudo-random stars based on position
        SDL.SetRenderDrawColor(_renderer, 255, 255, 255, 255);
        for (int i = 0; i < 50; i++)
        {
            int x = (i * 17 + 23) % 800;
            int y = (i * 31 + 7) % 576;
            // Twinkling effect
            var brightness = (byte)(150 + (int)(100 * Math.Sin(DateTime.Now.Ticks / 5000000.0 + i)));
            SDL.SetRenderDrawColor(_renderer, brightness, brightness, brightness, 255);
            var starRect = new SDLRect { X = x, Y = y, W = 2, H = 2 };
            SDL.RenderFillRect(_renderer, &starRect);
        }
    }

    private void RenderXWing()
    {
        if (_xwingTexture == null) return;

        int x = (int)_xwingX;
        int y = (int)_xwingY;
        int size = 64;

        var dstRect = new SDLRect { X = x, Y = y, W = size, H = size };

        // Render with slight rotation for more dynamic look
        var center = new SDLPoint { X = size / 2, Y = size / 2 };
        SDL.RenderCopyEx(_renderer, _xwingTexture, null, &dstRect,
            _xwingAngle, &center, SDLRendererFlip.None);
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
        if (_xwingTexture != null)
        {
            SDL.DestroyTexture(_xwingTexture);
            _xwingTexture = null;
        }
    }
}
