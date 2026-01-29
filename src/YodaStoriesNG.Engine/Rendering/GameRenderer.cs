using Hexa.NET.SDL2;
using YodaStoriesNG.Engine.Data;
using YodaStoriesNG.Engine.UI;

namespace YodaStoriesNG.Engine.Rendering;

/// <summary>
/// SDL2-based renderer for the game.
/// </summary>
public unsafe class GameRenderer : IDisposable
{
    // SDL init flags
    private const uint SDL_INIT_VIDEO = 0x00000020;
    private const uint SDL_INIT_AUDIO = 0x00000010;
    private const int SDL_WINDOWPOS_CENTERED = 0x2FFF0000;

    private SDLWindow* _window;
    private SDLRenderer* _renderer;
    private SDLTexture* _tileAtlas;
    private int _atlasWidth;
    private int _atlasHeight;
    private int _tilesPerRow;

    private readonly GameData _gameData;
    private readonly TileRenderer _tileRenderer;

    // Screen dimensions (9 tiles visible at once)
    public const int ViewportTilesX = 9;
    public const int ViewportTilesY = 9;
    public const int Scale = 2; // 2x scaling for better visibility
    public const int WindowWidth = ViewportTilesX * Tile.Width * Scale;
    public const int WindowHeight = ViewportTilesY * Tile.Height * Scale + 100 * Scale; // Extra space for HUD

    public bool IsInitialized => _window != null;

    public GameRenderer(GameData gameData)
    {
        _gameData = gameData;
        _tileRenderer = new TileRenderer();
    }

    public bool Initialize(string title = "Yoda Stories NG")
    {
        if (SDL.Init(SDL_INIT_VIDEO | SDL_INIT_AUDIO) < 0)
        {
            Console.WriteLine($"SDL_Init failed: {SDL.GetErrorS()}");
            return false;
        }

        _window = SDL.CreateWindow(
            title,
            (int)SDL_WINDOWPOS_CENTERED,
            (int)SDL_WINDOWPOS_CENTERED,
            WindowWidth,
            WindowHeight,
            (uint)SDLWindowFlags.Shown);

        if (_window == null)
        {
            Console.WriteLine($"SDL_CreateWindow failed: {SDL.GetErrorS()}");
            return false;
        }

        _renderer = SDL.CreateRenderer(_window, -1,
            (uint)(SDLRendererFlags.Accelerated | SDLRendererFlags.Presentvsync));

        if (_renderer == null)
        {
            Console.WriteLine($"SDL_CreateRenderer failed: {SDL.GetErrorS()}");
            return false;
        }

        // Create tile atlas texture
        CreateTileAtlas();

        return true;
    }

    private void CreateTileAtlas()
    {
        if (_gameData.Tiles.Count == 0)
            return;

        // Calculate atlas dimensions (aim for roughly square)
        _tilesPerRow = (int)Math.Ceiling(Math.Sqrt(_gameData.Tiles.Count));
        var (pixels, width, height) = _tileRenderer.CreateTileAtlas(_gameData.Tiles, _tilesPerRow);
        _atlasWidth = width;
        _atlasHeight = height;

        // Create SDL texture
        _tileAtlas = SDL.CreateTexture(
            _renderer,
            (uint)SDLPixelFormatEnum.Argb8888,
            (int)SDLTextureAccess.Static,
            width,
            height);

        if (_tileAtlas == null)
        {
            Console.WriteLine($"Failed to create tile atlas: {SDL.GetErrorS()}");
            return;
        }

        // Enable alpha blending
        SDL.SetTextureBlendMode(_tileAtlas, SDLBlendMode.Blend);

        // Upload pixel data
        fixed (uint* pixelPtr = pixels)
        {
            SDL.UpdateTexture(_tileAtlas, null, pixelPtr, width * 4);
        }

        Console.WriteLine($"Created tile atlas: {width}x{height} ({_gameData.Tiles.Count} tiles, {_tilesPerRow} per row)");
    }

    /// <summary>
    /// Renders a zone at the specified camera offset.
    /// </summary>
    public void RenderZone(Zone zone, int cameraX, int cameraY)
    {
        // Clear screen
        SDL.SetRenderDrawColor(_renderer, 0, 0, 0, 255);
        SDL.RenderClear(_renderer);

        // Render tile layers
        for (int layer = 0; layer < 3; layer++)
        {
            RenderTileLayer(zone, layer, cameraX, cameraY);
        }
    }

    private static bool _debugRendered = false;

    private void RenderTileLayer(Zone zone, int layer, int cameraX, int cameraY)
    {
        int tilesRendered = 0;
        for (int screenY = 0; screenY < ViewportTilesY; screenY++)
        {
            for (int screenX = 0; screenX < ViewportTilesX; screenX++)
            {
                var worldX = cameraX + screenX;
                var worldY = cameraY + screenY;

                if (worldX < 0 || worldX >= zone.Width || worldY < 0 || worldY >= zone.Height)
                    continue;

                var tileId = zone.GetTile(worldX, worldY, layer);
                if (tileId == 0xFFFF || tileId >= _gameData.Tiles.Count)
                    continue;

                var tile = _gameData.Tiles[(int)tileId];

                // Skip fully transparent tiles in upper layers
                if (layer > 0 && !tile.IsTransparent && tile.PixelData[0] == 0)
                    continue;

                RenderTile(tileId, screenX * Tile.Width * Scale, screenY * Tile.Height * Scale);
                tilesRendered++;
            }
        }

        if (!_debugRendered && layer == 0)
        {
            Console.WriteLine($"Layer {layer}: Rendered {tilesRendered} tiles");
            _debugRendered = true;
        }
    }

    private static bool _debugAtlas = false;

    /// <summary>
    /// Renders a single tile at the specified screen position.
    /// </summary>
    public void RenderTile(int tileId, int x, int y)
    {
        if (_tileAtlas == null || tileId < 0 || tileId >= _gameData.Tiles.Count)
        {
            if (!_debugAtlas)
            {
                Console.WriteLine($"RenderTile skipped: atlas={_tileAtlas != null}, tileId={tileId}, tileCount={_gameData.Tiles.Count}");
                _debugAtlas = true;
            }
            return;
        }

        // Calculate source rectangle in atlas
        var atlasX = (tileId % _tilesPerRow) * Tile.Width;
        var atlasY = (tileId / _tilesPerRow) * Tile.Height;

        var srcRect = new SDLRect
        {
            X = atlasX,
            Y = atlasY,
            W = Tile.Width,
            H = Tile.Height
        };

        var dstRect = new SDLRect
        {
            X = x,
            Y = y,
            W = Tile.Width * Scale,
            H = Tile.Height * Scale
        };

        SDL.RenderCopy(_renderer, _tileAtlas, &srcRect, &dstRect);
    }

    /// <summary>
    /// Renders a sprite (character/object) at the specified world position.
    /// </summary>
    public void RenderSprite(int tileId, int worldX, int worldY, int cameraX, int cameraY)
    {
        var screenX = (worldX - cameraX) * Tile.Width * Scale;
        var screenY = (worldY - cameraY) * Tile.Height * Scale;
        RenderTile(tileId, screenX, screenY);
    }

    /// <summary>
    /// Renders text on screen (placeholder - uses colored rectangles for now).
    /// </summary>
    public void RenderText(string text, int x, int y, byte r = 255, byte g = 255, byte b = 255)
    {
        // TODO: Implement proper text rendering with TTF
        // For now, just draw a placeholder rectangle
        SDL.SetRenderDrawColor(_renderer, r, g, b, 255);
        var rect = new SDLRect { X = x, Y = y, W = text.Length * 8, H = 16 };
        SDL.RenderDrawRect(_renderer, &rect);
    }

    /// <summary>
    /// Renders the HUD (health, inventory, etc.).
    /// </summary>
    public void RenderHUD(int health, int maxHealth, List<int> inventory, int? selectedWeapon, int? selectedItem = null)
    {
        var hudY = ViewportTilesY * Tile.Height * Scale;

        // Background
        SDL.SetRenderDrawColor(_renderer, 40, 40, 40, 255);
        var hudRect = new SDLRect { X = 0, Y = hudY, W = WindowWidth, H = 100 * Scale };
        SDL.RenderFillRect(_renderer, &hudRect);

        // Health bar background
        SDL.SetRenderDrawColor(_renderer, 80, 0, 0, 255);
        var healthBg = new SDLRect { X = 10, Y = hudY + 10, W = 150, H = 20 };
        SDL.RenderFillRect(_renderer, &healthBg);

        // Health bar fill
        var healthWidth = (int)((float)health / maxHealth * 150);
        SDL.SetRenderDrawColor(_renderer, 200, 0, 0, 255);
        var healthRect = new SDLRect { X = 10, Y = hudY + 10, W = healthWidth, H = 20 };
        SDL.RenderFillRect(_renderer, &healthRect);

        // Health bar border
        SDL.SetRenderDrawColor(_renderer, 255, 255, 255, 255);
        var healthBorder = new SDLRect { X = 10, Y = hudY + 10, W = 150, H = 20 };
        SDL.RenderDrawRect(_renderer, &healthBorder);

        // Weapon slot (on left side)
        var weaponSlotX = 10;
        var weaponSlotY = hudY + 40;

        // Weapon slot background
        SDL.SetRenderDrawColor(_renderer, 80, 80, 40, 255);
        var weaponSlotRect = new SDLRect { X = weaponSlotX, Y = weaponSlotY, W = Tile.Width * Scale, H = Tile.Height * Scale };
        SDL.RenderFillRect(_renderer, &weaponSlotRect);

        // Weapon slot border
        SDL.SetRenderDrawColor(_renderer, 255, 200, 0, 255);
        SDL.RenderDrawRect(_renderer, &weaponSlotRect);

        // Weapon tile
        if (selectedWeapon.HasValue && selectedWeapon.Value > 0 && selectedWeapon.Value < _gameData.Tiles.Count)
        {
            RenderTile(selectedWeapon.Value, weaponSlotX, weaponSlotY);
        }

        // Inventory slots (8 slots)
        for (int i = 0; i < 8; i++)
        {
            var slotX = 90 + i * (Tile.Width * Scale + 8);
            var slotY = hudY + 40;

            // Slot background
            SDL.SetRenderDrawColor(_renderer, 60, 60, 60, 255);
            var slotRect = new SDLRect { X = slotX, Y = slotY, W = Tile.Width * Scale, H = Tile.Height * Scale };
            SDL.RenderFillRect(_renderer, &slotRect);

            // Highlight selected item
            if (selectedItem.HasValue && i < inventory.Count && inventory[i] == selectedItem.Value)
            {
                SDL.SetRenderDrawColor(_renderer, 0, 255, 0, 255);
            }
            else
            {
                SDL.SetRenderDrawColor(_renderer, 100, 100, 100, 255);
            }
            SDL.RenderDrawRect(_renderer, &slotRect);

            // Item tile
            if (i < inventory.Count && inventory[i] > 0 && inventory[i] < _gameData.Tiles.Count)
            {
                RenderTile(inventory[i], slotX, slotY);
            }

            // Slot number indicator (small colored square for slot position)
            SDL.SetRenderDrawColor(_renderer, 150, 150, 150, 255);
            var numRect = new SDLRect { X = slotX + 2, Y = slotY + Tile.Height * Scale - 6, W = 6, H = 6 };
            SDL.RenderFillRect(_renderer, &numRect);
        }
    }

    /// <summary>
    /// Renders zone info overlay.
    /// </summary>
    public void RenderZoneInfo(int zoneId, string planetName, int width, int height)
    {
        // Zone info bar at top of screen
        SDL.SetRenderDrawColor(_renderer, 0, 0, 0, 180);
        var infoRect = new SDLRect { X = 0, Y = 0, W = WindowWidth, H = 24 };
        SDL.RenderFillRect(_renderer, &infoRect);

        // Zone indicator squares
        SDL.SetRenderDrawColor(_renderer, 255, 255, 255, 255);
        var zoneRect = new SDLRect { X = 5, Y = 5, W = 14, H = 14 };
        SDL.RenderDrawRect(_renderer, &zoneRect);

        // Planet color indicator
        byte r = 255, g = 255, b = 255;
        switch (planetName.ToLower())
        {
            case "desert": r = 255; g = 200; b = 100; break;  // Tatooine
            case "snow": r = 200; g = 220; b = 255; break;    // Hoth
            case "forest": r = 100; g = 200; b = 100; break;  // Endor
            case "swamp": r = 100; g = 150; b = 100; break;   // Dagobah
        }
        SDL.SetRenderDrawColor(_renderer, r, g, b, 255);
        var planetRect = new SDLRect { X = 7, Y = 7, W = 10, H = 10 };
        SDL.RenderFillRect(_renderer, &planetRect);
    }

    /// <summary>
    /// Renders a screen overlay for damage/attack feedback.
    /// </summary>
    public void RenderDamageOverlay(double intensity)
    {
        if (intensity <= 0)
            return;

        var alpha = (byte)(intensity * 100);
        SDL.SetRenderDrawColor(_renderer, 255, 0, 0, alpha);
        var fullScreen = new SDLRect { X = 0, Y = 0, W = WindowWidth, H = ViewportTilesY * Tile.Height * Scale };
        SDL.SetRenderDrawBlendMode(_renderer, SDLBlendMode.Blend);
        SDL.RenderFillRect(_renderer, &fullScreen);
    }

    /// <summary>
    /// Renders a screen overlay for attack feedback.
    /// </summary>
    public void RenderAttackOverlay(double intensity)
    {
        if (intensity <= 0)
            return;

        var alpha = (byte)(intensity * 80);
        SDL.SetRenderDrawColor(_renderer, 255, 255, 0, alpha);
        var fullScreen = new SDLRect { X = 0, Y = 0, W = WindowWidth, H = ViewportTilesY * Tile.Height * Scale };
        SDL.SetRenderDrawBlendMode(_renderer, SDLBlendMode.Blend);
        SDL.RenderFillRect(_renderer, &fullScreen);
    }

    private void RenderTileUnscaled(int tileId, int x, int y)
    {
        if (_tileAtlas == null || tileId < 0 || tileId >= _gameData.Tiles.Count)
            return;

        var atlasX = (tileId % _tilesPerRow) * Tile.Width;
        var atlasY = (tileId / _tilesPerRow) * Tile.Height;

        var srcRect = new SDLRect { X = atlasX, Y = atlasY, W = Tile.Width, H = Tile.Height };
        var dstRect = new SDLRect { X = x, Y = y, W = Tile.Width, H = Tile.Height };

        SDL.RenderCopy(_renderer, _tileAtlas, &srcRect, &dstRect);
    }

    /// <summary>
    /// Presents the rendered frame.
    /// </summary>
    public void Present()
    {
        SDL.RenderPresent(_renderer);
    }

    /// <summary>
    /// Polls for SDL events.
    /// </summary>
    public bool PollEvent(out SDLEvent evt)
    {
        fixed (SDLEvent* evtPtr = &evt)
        {
            return SDL.PollEvent(evtPtr) != 0;
        }
    }

    /// <summary>
    /// Renders game messages on screen.
    /// </summary>
    public void RenderMessages(IReadOnlyList<GameMessage> messages, GameMessage? dialogue)
    {
        // Render regular messages (top-right corner)
        var messageY = 30;
        foreach (var msg in messages)
        {
            var alpha = (byte)(Math.Min(1.0, msg.TimeRemaining) * 255);
            RenderMessageBox(msg.Text, WindowWidth - 10, messageY, alpha, msg.Type, alignRight: true);
            messageY += 28;
        }

        // Render dialogue box (bottom of game area)
        if (dialogue != null)
        {
            var dialogueY = ViewportTilesY * Tile.Height * Scale - 60;
            RenderDialogueBox(dialogue.Text, 10, dialogueY);
        }
    }

    private void RenderMessageBox(string text, int x, int y, byte alpha, MessageType type, bool alignRight = false)
    {
        // Calculate text width (rough estimate: 7 pixels per character)
        var textWidth = text.Length * 7;
        var boxWidth = textWidth + 16;
        var boxX = alignRight ? x - boxWidth : x;

        // Background color based on message type
        byte r = 0, g = 0, b = 0;
        switch (type)
        {
            case MessageType.Pickup: r = 0; g = 100; b = 0; break;
            case MessageType.Combat: r = 150; g = 50; b = 0; break;
            case MessageType.System: r = 0; g = 50; b = 150; break;
            default: r = 30; g = 30; b = 30; break;
        }

        // Draw background
        SDL.SetRenderDrawColor(_renderer, r, g, b, (byte)(alpha * 0.8));
        SDL.SetRenderDrawBlendMode(_renderer, SDLBlendMode.Blend);
        var bgRect = new SDLRect { X = boxX, Y = y, W = boxWidth, H = 24 };
        SDL.RenderFillRect(_renderer, &bgRect);

        // Draw border
        SDL.SetRenderDrawColor(_renderer, 255, 255, 255, alpha);
        SDL.RenderDrawRect(_renderer, &bgRect);

        // Draw text representation (simple rectangles for each character)
        SDL.SetRenderDrawColor(_renderer, 255, 255, 255, alpha);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != ' ')
            {
                var charRect = new SDLRect { X = boxX + 8 + i * 7, Y = y + 6, W = 5, H = 12 };
                SDL.RenderFillRect(_renderer, &charRect);
            }
        }
    }

    private void RenderDialogueBox(string text, int x, int y)
    {
        var boxWidth = WindowWidth - 20;

        // Draw background
        SDL.SetRenderDrawColor(_renderer, 20, 20, 50, 230);
        SDL.SetRenderDrawBlendMode(_renderer, SDLBlendMode.Blend);
        var bgRect = new SDLRect { X = x, Y = y, W = boxWidth, H = 56 };
        SDL.RenderFillRect(_renderer, &bgRect);

        // Draw border
        SDL.SetRenderDrawColor(_renderer, 200, 200, 255, 255);
        SDL.RenderDrawRect(_renderer, &bgRect);

        // Draw inner border
        var innerRect = new SDLRect { X = x + 2, Y = y + 2, W = boxWidth - 4, H = 52 };
        SDL.RenderDrawRect(_renderer, &innerRect);

        // Draw text representation
        SDL.SetRenderDrawColor(_renderer, 255, 255, 255, 255);
        var textX = x + 10;
        var textY = y + 10;
        var maxCharsPerLine = (boxWidth - 20) / 7;

        for (int i = 0; i < text.Length; i++)
        {
            // Word wrap
            if (i > 0 && i % maxCharsPerLine == 0)
            {
                textY += 16;
                textX = x + 10;
            }

            if (text[i] != ' ')
            {
                var charRect = new SDLRect { X = textX, Y = textY, W = 5, H = 12 };
                SDL.RenderFillRect(_renderer, &charRect);
            }
            textX += 7;
        }

        // Draw "Press Space to continue" indicator
        SDL.SetRenderDrawColor(_renderer, 150, 150, 200, 255);
        var indicatorRect = new SDLRect { X = x + boxWidth - 30, Y = y + 46, W = 20, H = 6 };
        SDL.RenderFillRect(_renderer, &indicatorRect);
    }

    public void Dispose()
    {
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

        SDL.Quit();
    }
}
