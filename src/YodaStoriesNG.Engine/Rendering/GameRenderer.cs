using Hexa.NET.SDL2;
using YodaStoriesNG.Engine.Data;
using YodaStoriesNG.Engine.Game;
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
    private readonly BitmapFont _font;

    // Screen dimensions - Widescreen layout with HUD on right side
    public const int ViewportTilesX = 9;
    public const int ViewportTilesY = 9;
    public const int Scale = 2; // 2x scaling for better visibility
    public const int GameAreaWidth = ViewportTilesX * Tile.Width * Scale;  // 576 pixels
    public const int GameAreaHeight = ViewportTilesY * Tile.Height * Scale; // 576 pixels
    public const int SidebarWidth = 220; // HUD sidebar on right
    public const int WindowWidth = GameAreaWidth + SidebarWidth;  // 796 pixels wide
    public const int WindowHeight = GameAreaHeight; // 576 pixels tall (no bottom HUD)

    // Legacy constants for portrait mode (preserved for future use)
    // public const int PortraitWindowWidth = ViewportTilesX * Tile.Width * Scale;
    // public const int PortraitWindowHeight = ViewportTilesY * Tile.Height * Scale + 100 * Scale;

    public bool IsInitialized => _window != null;

    public BitmapFont GetFont() => _font;
    public SDLRenderer* GetRenderer() => _renderer;
    public uint GetWindowID() => _window != null ? SDL.GetWindowID(_window) : 0;

    private int _currentScale = 2;
    public int CurrentScale => _currentScale;

    /// <summary>
    /// Sets the window scale (1x, 2x or 4x).
    /// </summary>
    public void SetWindowScale(int scale)
    {
        if (_window == null || _renderer == null) return;

        _currentScale = scale;
        int newWidth = WindowWidth * scale / 2;  // Base is 2x
        int newHeight = WindowHeight * scale / 2;

        SDL.SetWindowSize(_window, newWidth, newHeight);

        // Set logical size so all rendering scales properly
        // The logical size stays at the base resolution, SDL handles the scaling
        SDL.RenderSetLogicalSize(_renderer, WindowWidth, WindowHeight);

        Console.WriteLine($"Window scale set to {scale}x ({newWidth}x{newHeight}), logical size: {WindowWidth}x{WindowHeight}");
    }

    /// <summary>
    /// Temporarily disables logical scaling for rendering UI at fixed physical size.
    /// Call RestoreLogicalSize() after rendering.
    /// </summary>
    public void DisableLogicalSize()
    {
        if (_renderer == null) return;
        SDL.RenderSetLogicalSize(_renderer, 0, 0);
    }

    /// <summary>
    /// Restores logical scaling after DisableLogicalSize().
    /// </summary>
    public void RestoreLogicalSize()
    {
        if (_renderer == null) return;
        SDL.RenderSetLogicalSize(_renderer, WindowWidth, WindowHeight);
    }

    /// <summary>
    /// Gets the current physical window size.
    /// </summary>
    public (int width, int height) GetPhysicalWindowSize()
    {
        return (WindowWidth * _currentScale / 2, WindowHeight * _currentScale / 2);
    }

    public GameRenderer(GameData gameData)
    {
        _gameData = gameData;
        _tileRenderer = new TileRenderer();
        _font = new BitmapFont();
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

        // Initialize bitmap font
        if (!_font.Initialize(_renderer))
        {
            Console.WriteLine("Warning: Failed to initialize bitmap font");
        }

        // Set initial logical size for consistent scaling behavior
        SDL.RenderSetLogicalSize(_renderer, WindowWidth, WindowHeight);

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

    private void RenderTileLayer(Zone zone, int layer, int cameraX, int cameraY)
    {
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

                var screenPosX = screenX * Tile.Width * Scale;
                var screenPosY = screenY * Tile.Height * Scale;
                RenderTile(tileId, screenPosX, screenPosY);
            }
        }
    }

    /// <summary>
    /// Renders a single tile at the specified screen position.
    /// </summary>
    public void RenderTile(int tileId, int x, int y)
    {
        if (_tileAtlas == null || tileId < 0 || tileId >= _gameData.Tiles.Count)
            return;

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
    /// Renders the HUD (health, inventory, etc.) on the right sidebar.
    /// </summary>
    public void RenderHUD(int health, int maxHealth, List<int> inventory, int? selectedWeapon, int? selectedItem = null)
    {
        var hudX = GameAreaWidth;  // Right side of game area
        var hudY = 0;

        // Sidebar background
        SDL.SetRenderDrawColor(_renderer, 30, 30, 35, 255);
        var hudRect = new SDLRect { X = hudX, Y = hudY, W = SidebarWidth, H = WindowHeight };
        SDL.RenderFillRect(_renderer, &hudRect);

        // Sidebar border
        SDL.SetRenderDrawColor(_renderer, 80, 80, 100, 255);
        var borderRect = new SDLRect { X = hudX, Y = 0, W = 2, H = WindowHeight };
        SDL.RenderFillRect(_renderer, &borderRect);

        // === HEALTH SECTION ===
        var sectionY = 15;
        _font.RenderText(_renderer, "HEALTH", hudX + 10, sectionY, 1, 200, 200, 200, 255);
        sectionY += 18;

        // Health bar background
        SDL.SetRenderDrawColor(_renderer, 60, 20, 20, 255);
        var healthBg = new SDLRect { X = hudX + 10, Y = sectionY, W = SidebarWidth - 20, H = 24 };
        SDL.RenderFillRect(_renderer, &healthBg);

        // Health bar fill
        var healthWidth = (int)((float)health / maxHealth * (SidebarWidth - 20));
        SDL.SetRenderDrawColor(_renderer, 180, 40, 40, 255);
        var healthRect = new SDLRect { X = hudX + 10, Y = sectionY, W = healthWidth, H = 24 };
        SDL.RenderFillRect(_renderer, &healthRect);

        // Health bar border
        SDL.SetRenderDrawColor(_renderer, 200, 100, 100, 255);
        SDL.RenderDrawRect(_renderer, &healthBg);

        // Health text centered
        var healthText = $"{health}/{maxHealth}";
        var textWidth = _font.GetTextWidth(healthText);
        _font.RenderText(_renderer, healthText, hudX + (SidebarWidth - textWidth) / 2, sectionY + 7, 1, 255, 255, 255, 255);

        // === WEAPON SECTION ===
        sectionY += 45;
        _font.RenderText(_renderer, "WEAPON [TAB]", hudX + 10, sectionY, 1, 200, 200, 200, 255);
        sectionY += 18;

        // Weapon slot
        var weaponSlotX = hudX + 10;
        var weaponSlotY = sectionY;
        var weaponSlotSize = Tile.Width * Scale;

        // Weapon slot background
        SDL.SetRenderDrawColor(_renderer, 50, 50, 30, 255);
        var weaponSlotRect = new SDLRect { X = weaponSlotX, Y = weaponSlotY, W = weaponSlotSize, H = weaponSlotSize };
        SDL.RenderFillRect(_renderer, &weaponSlotRect);

        // Weapon slot border (gold)
        SDL.SetRenderDrawColor(_renderer, 200, 180, 50, 255);
        SDL.RenderDrawRect(_renderer, &weaponSlotRect);

        // Weapon tile
        if (selectedWeapon.HasValue && selectedWeapon.Value > 0 && selectedWeapon.Value < _gameData.Tiles.Count)
        {
            RenderTile(selectedWeapon.Value, weaponSlotX, weaponSlotY);
        }
        else
        {
            _font.RenderText(_renderer, "FISTS", weaponSlotX + 8, weaponSlotY + 24, 1, 150, 150, 150, 255);
        }

        // === INVENTORY SECTION ===
        sectionY += weaponSlotSize + 20;
        _font.RenderText(_renderer, "INVENTORY [1-8]", hudX + 10, sectionY, 1, 200, 200, 200, 255);
        sectionY += 18;

        // Inventory grid (2x4 layout)
        var slotSize = 44;
        var slotPadding = 4;
        var gridStartX = hudX + 10;
        var gridStartY = sectionY;

        for (int i = 0; i < 8; i++)
        {
            var col = i % 2;
            var row = i / 2;
            var slotX = gridStartX + col * (slotSize + slotPadding);
            var slotY = gridStartY + row * (slotSize + slotPadding);

            // Slot background
            SDL.SetRenderDrawColor(_renderer, 40, 40, 45, 255);
            var slotRect = new SDLRect { X = slotX, Y = slotY, W = slotSize, H = slotSize };
            SDL.RenderFillRect(_renderer, &slotRect);

            // Highlight selected item (green border)
            if (selectedItem.HasValue && i < inventory.Count && inventory[i] == selectedItem.Value)
            {
                SDL.SetRenderDrawColor(_renderer, 50, 255, 50, 255);
                var innerRect = new SDLRect { X = slotX - 2, Y = slotY - 2, W = slotSize + 4, H = slotSize + 4 };
                SDL.RenderDrawRect(_renderer, &innerRect);
            }
            else
            {
                SDL.SetRenderDrawColor(_renderer, 80, 80, 90, 255);
            }
            SDL.RenderDrawRect(_renderer, &slotRect);

            // Item tile
            if (i < inventory.Count && inventory[i] > 0 && inventory[i] < _gameData.Tiles.Count)
            {
                // Center the tile in the slot
                var tileX = slotX + (slotSize - Tile.Width) / 2;
                var tileY = slotY + (slotSize - Tile.Height) / 2;
                RenderTileUnscaled(inventory[i], tileX, tileY);
            }

            // Slot number (bottom right corner)
            _font.RenderText(_renderer, $"{i + 1}", slotX + slotSize - 10, slotY + slotSize - 12, 1, 120, 120, 130, 200);
        }

        // === CONTROLS SECTION ===
        sectionY = gridStartY + 4 * (slotSize + slotPadding) + 15;
        SDL.SetRenderDrawColor(_renderer, 50, 50, 60, 255);
        var controlsBg = new SDLRect { X = hudX + 5, Y = sectionY, W = SidebarWidth - 10, H = WindowHeight - sectionY - 5 };
        SDL.RenderFillRect(_renderer, &controlsBg);

        sectionY += 8;
        _font.RenderText(_renderer, "CONTROLS", hudX + 10, sectionY, 1, 150, 180, 200, 255);
        sectionY += 16;
        _font.RenderText(_renderer, "WASD - Move", hudX + 10, sectionY, 1, 130, 130, 140, 255);
        sectionY += 12;
        _font.RenderText(_renderer, "SPACE - Action", hudX + 10, sectionY, 1, 130, 130, 140, 255);
        sectionY += 12;
        _font.RenderText(_renderer, "O - Objective", hudX + 10, sectionY, 1, 130, 130, 140, 255);
        sectionY += 12;
        _font.RenderText(_renderer, "X - Travel", hudX + 10, sectionY, 1, 130, 130, 140, 255);
        sectionY += 12;
        _font.RenderText(_renderer, "R - Restart", hudX + 10, sectionY, 1, 130, 130, 140, 255);
    }

    /// <summary>
    /// Renders zone info overlay.
    /// </summary>
    public void RenderZoneInfo(int zoneId, string planetName, int width, int height)
    {
        // Zone info bar at top of game area (not over sidebar)
        SDL.SetRenderDrawColor(_renderer, 0, 0, 0, 180);
        SDL.SetRenderDrawBlendMode(_renderer, SDLBlendMode.Blend);
        var infoRect = new SDLRect { X = 0, Y = 0, W = GameAreaWidth, H = 20 };
        SDL.RenderFillRect(_renderer, &infoRect);

        // Planet color indicator
        byte r = 255, g = 255, b = 255;
        switch (planetName.ToLower())
        {
            case "desert": r = 255; g = 200; b = 100; break;  // Tatooine
            case "snow": r = 200; g = 220; b = 255; break;    // Hoth
            case "forest": r = 100; g = 200; b = 100; break;  // Endor
            case "swamp": r = 100; g = 150; b = 100; break;   // Dagobah
        }

        // Draw planet indicator square
        SDL.SetRenderDrawColor(_renderer, r, g, b, 255);
        var planetRect = new SDLRect { X = 5, Y = 5, W = 10, H = 10 };
        SDL.RenderFillRect(_renderer, &planetRect);

        // Draw zone info text
        _font.RenderText(_renderer, $"Zone {zoneId} - {planetName} ({width}x{height})", 20, 6, 1, r, g, b, 255);
    }

    /// <summary>
    /// Renders the bot status indicator.
    /// </summary>
    public void RenderBotStatus(string currentTask)
    {
        // Bot status bar below zone info
        SDL.SetRenderDrawColor(_renderer, 0, 80, 0, 200);
        SDL.SetRenderDrawBlendMode(_renderer, SDLBlendMode.Blend);
        var botRect = new SDLRect { X = 0, Y = 22, W = GameAreaWidth, H = 18 };
        SDL.RenderFillRect(_renderer, &botRect);

        // Draw bot indicator
        SDL.SetRenderDrawColor(_renderer, 0, 255, 0, 255);
        var indicatorRect = new SDLRect { X = 5, Y = 26, W = 8, H = 8 };
        SDL.RenderFillRect(_renderer, &indicatorRect);

        // Draw bot status text
        string statusText = $"BOT: {currentTask}";
        if (statusText.Length > 50)
            statusText = statusText.Substring(0, 50) + "...";
        _font.RenderText(_renderer, statusText, 18, 24, 1, 0, 255, 100, 255);
    }

    /// <summary>
    /// Renders highlight boxes at specified world positions.
    /// Used by the script viewer to show referenced positions.
    /// </summary>
    public void RenderHighlights(IReadOnlyList<UI.ScriptHighlight> highlights, int cameraX, int cameraY)
    {
        if (highlights.Count == 0) return;

        SDL.SetRenderDrawBlendMode(_renderer, SDLBlendMode.Blend);

        foreach (var h in highlights)
        {
            // Check if within viewport
            if (h.X < cameraX || h.X >= cameraX + ViewportTilesX ||
                h.Y < cameraY || h.Y >= cameraY + ViewportTilesY)
                continue;

            var screenX = (h.X - cameraX) * Tile.Width * Scale;
            var screenY = (h.Y - cameraY) * Tile.Height * Scale;
            var size = Tile.Width * Scale;

            // Color based on highlight type
            byte r = 255, g = 255, b = 255;
            switch (h.Type)
            {
                case UI.HighlightType.Position:
                    r = 0; g = 255; b = 255; // Cyan
                    break;
                case UI.HighlightType.Tile:
                    r = 255; g = 255; b = 0; // Yellow
                    break;
                case UI.HighlightType.Door:
                    r = 0; g = 255; b = 0; // Green
                    break;
                case UI.HighlightType.NPC:
                    r = 255; g = 0; b = 255; // Magenta
                    break;
                case UI.HighlightType.Item:
                    r = 255; g = 150; b = 0; // Orange
                    break;
                case UI.HighlightType.Trigger:
                    r = 100; g = 150; b = 255; // Blue
                    break;
            }

            // Draw pulsing highlight box
            var pulse = (byte)(150 + (int)(50 * Math.Sin(DateTime.Now.Ticks / 1000000.0)));

            // Outer glow
            SDL.SetRenderDrawColor(_renderer, r, g, b, (byte)(pulse / 3));
            var outerRect = new SDLRect { X = screenX - 4, Y = screenY - 4, W = size + 8, H = size + 8 };
            SDL.RenderFillRect(_renderer, &outerRect);

            // Border
            SDL.SetRenderDrawColor(_renderer, r, g, b, pulse);
            var rect = new SDLRect { X = screenX, Y = screenY, W = size, H = size };
            SDL.RenderDrawRect(_renderer, &rect);

            // Inner border
            var innerRect = new SDLRect { X = screenX + 2, Y = screenY + 2, W = size - 4, H = size - 4 };
            SDL.RenderDrawRect(_renderer, &innerRect);

            // Draw label if there's room
            if (!string.IsNullOrEmpty(h.Label))
            {
                SDL.SetRenderDrawColor(_renderer, 0, 0, 0, 180);
                var labelBg = new SDLRect { X = screenX, Y = screenY - 12, W = h.Label.Length * 8 + 4, H = 12 };
                SDL.RenderFillRect(_renderer, &labelBg);
                _font.RenderText(_renderer, h.Label, screenX + 2, screenY - 10, 1, r, g, b, 255);
            }
        }
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
        var gameArea = new SDLRect { X = 0, Y = 0, W = GameAreaWidth, H = GameAreaHeight };
        SDL.SetRenderDrawBlendMode(_renderer, SDLBlendMode.Blend);
        SDL.RenderFillRect(_renderer, &gameArea);
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
        var gameArea = new SDLRect { X = 0, Y = 0, W = GameAreaWidth, H = GameAreaHeight };
        SDL.SetRenderDrawBlendMode(_renderer, SDLBlendMode.Blend);
        SDL.RenderFillRect(_renderer, &gameArea);
    }

    /// <summary>
    /// Renders a melee slash effect.
    /// </summary>
    public void RenderMeleeSlash(int playerScreenX, int playerScreenY, int direction, double progress)
    {
        // Calculate slash position based on direction
        int offsetX = 0, offsetY = 0;

        // Direction: 0=Up, 1=Down, 2=Left, 3=Right
        switch (direction)
        {
            case 0: offsetY = -Tile.Height * Scale / 2; break;
            case 1: offsetY = Tile.Height * Scale / 2; break;
            case 2: offsetX = -Tile.Width * Scale / 2; break;
            case 3: offsetX = Tile.Width * Scale / 2; break;
        }

        // Animate the slash - it expands then fades
        var slashProgress = 1.0 - progress;  // 0 to 1
        var alpha = (byte)((1.0 - slashProgress) * 255);
        var size = (int)(8 + slashProgress * 24);

        SDL.SetRenderDrawBlendMode(_renderer, SDLBlendMode.Blend);

        // Draw lightsaber-like slash (green/cyan color)
        SDL.SetRenderDrawColor(_renderer, 100, 255, 150, alpha);

        var centerX = playerScreenX + Tile.Width * Scale / 2 + offsetX;
        var centerY = playerScreenY + Tile.Height * Scale / 2 + offsetY;

        // Draw slash line based on direction
        if (direction == 0 || direction == 1)
        {
            // Vertical slash - horizontal swing
            var slashRect = new SDLRect
            {
                X = centerX - size / 2,
                Y = centerY - 4,
                W = size,
                H = 8
            };
            SDL.RenderFillRect(_renderer, &slashRect);
        }
        else
        {
            // Horizontal slash - vertical swing
            var slashRect = new SDLRect
            {
                X = centerX - 4,
                Y = centerY - size / 2,
                W = 8,
                H = size
            };
            SDL.RenderFillRect(_renderer, &slashRect);
        }

        // Draw glow effect
        SDL.SetRenderDrawColor(_renderer, 200, 255, 200, (byte)(alpha / 2));
        var glowRect = new SDLRect
        {
            X = centerX - size / 2 - 2,
            Y = centerY - size / 2 - 2,
            W = size + 4,
            H = size + 4
        };
        SDL.RenderDrawRect(_renderer, &glowRect);
    }

    /// <summary>
    /// Renders a weapon attack animation using the weapon's tile sprite.
    /// </summary>
    public void RenderWeaponAttack(int weaponTileId, int playerScreenX, int playerScreenY, int direction, double progress)
    {
        if (weaponTileId <= 0 || weaponTileId >= _gameData.Tiles.Count)
        {
            // Fallback to melee slash for invalid weapon
            RenderMeleeSlash(playerScreenX, playerScreenY, direction, progress);
            return;
        }

        // Calculate weapon position based on direction and progress
        int offsetX = 0, offsetY = 0;
        double angle = 0;

        // Swing animation - weapon moves in an arc
        var swingProgress = Math.Sin(progress * Math.PI);  // 0 -> 1 -> 0

        // Direction: 0=Up, 1=Down, 2=Left, 3=Right
        switch (direction)
        {
            case 0: // Up
                offsetY = -(int)(Tile.Height * Scale * (0.5 + swingProgress * 0.5));
                offsetX = (int)((swingProgress - 0.5) * Tile.Width * Scale);
                break;
            case 1: // Down
                offsetY = (int)(Tile.Height * Scale * (0.5 + swingProgress * 0.5));
                offsetX = (int)((0.5 - swingProgress) * Tile.Width * Scale);
                break;
            case 2: // Left
                offsetX = -(int)(Tile.Width * Scale * (0.5 + swingProgress * 0.5));
                offsetY = (int)((swingProgress - 0.5) * Tile.Height * Scale);
                break;
            case 3: // Right
                offsetX = (int)(Tile.Width * Scale * (0.5 + swingProgress * 0.5));
                offsetY = (int)((0.5 - swingProgress) * Tile.Height * Scale);
                break;
        }

        // Render the weapon tile
        var weaponX = playerScreenX + offsetX;
        var weaponY = playerScreenY + offsetY;

        // Get tile from atlas
        int tilesPerRow = (_atlasWidth + Tile.Width - 1) / Tile.Width;
        int atlasX = (weaponTileId % tilesPerRow) * Tile.Width;
        int atlasY = (weaponTileId / tilesPerRow) * Tile.Height;

        var srcRect = new SDLRect { X = atlasX, Y = atlasY, W = Tile.Width, H = Tile.Height };
        var dstRect = new SDLRect { X = weaponX, Y = weaponY, W = Tile.Width * Scale, H = Tile.Height * Scale };

        SDL.RenderCopy(_renderer, _tileAtlas, &srcRect, &dstRect);

        // Add lightsaber glow effect for tile 510 (lightsaber)
        if (weaponTileId == 510)
        {
            SDL.SetRenderDrawBlendMode(_renderer, SDLBlendMode.Blend);
            SDL.SetRenderDrawColor(_renderer, 100, 255, 100, (byte)(100 * swingProgress));
            var glowRect = new SDLRect
            {
                X = weaponX - 4,
                Y = weaponY - 4,
                W = Tile.Width * Scale + 8,
                H = Tile.Height * Scale + 8
            };
            SDL.RenderDrawRect(_renderer, &glowRect);
        }
    }

    /// <summary>
    /// Renders a projectile (blaster bolt).
    /// </summary>
    public void RenderProjectile(int screenX, int screenY, Game.ProjectileType type)
    {
        SDL.SetRenderDrawBlendMode(_renderer, SDLBlendMode.Blend);

        // Draw blaster bolt (red/orange)
        byte r = 255, g = 100, b = 50;
        if (type == Game.ProjectileType.HeavyBlaster)
        {
            g = 50; b = 150;  // More purple for heavy
        }

        // Core
        SDL.SetRenderDrawColor(_renderer, 255, 255, 200, 255);
        var coreRect = new SDLRect { X = screenX + 12, Y = screenY + 12, W = 8, H = 8 };
        SDL.RenderFillRect(_renderer, &coreRect);

        // Glow
        SDL.SetRenderDrawColor(_renderer, r, g, b, 180);
        var glowRect = new SDLRect { X = screenX + 8, Y = screenY + 8, W = 16, H = 16 };
        SDL.RenderFillRect(_renderer, &glowRect);

        // Outer glow
        SDL.SetRenderDrawColor(_renderer, r, g, b, 80);
        var outerRect = new SDLRect { X = screenX + 4, Y = screenY + 4, W = 24, H = 24 };
        SDL.RenderDrawRect(_renderer, &outerRect);
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
    /// Renders a debug overlay with tabbed content.
    /// </summary>
    public void RenderDebugOverlay(string[] tabs, int currentTab, List<string> lines, int scrollOffset)
    {
        // Semi-transparent background
        int panelX = 20, panelY = 20;
        int panelWidth = WindowWidth - 40;
        int panelHeight = WindowHeight - 40;

        SDL.SetRenderDrawBlendMode(_renderer, SDLBlendMode.Blend);
        SDL.SetRenderDrawColor(_renderer, 0, 0, 0, 220);
        var bgRect = new SDLRect { X = panelX, Y = panelY, W = panelWidth, H = panelHeight };
        SDL.RenderFillRect(_renderer, &bgRect);

        // Border
        SDL.SetRenderDrawColor(_renderer, 0, 200, 0, 255);
        SDL.RenderDrawRect(_renderer, &bgRect);

        // Tabs
        int tabWidth = panelWidth / tabs.Length;
        for (int i = 0; i < tabs.Length; i++)
        {
            var tabRect = new SDLRect { X = panelX + i * tabWidth, Y = panelY, W = tabWidth, H = 20 };

            if (i == currentTab)
            {
                SDL.SetRenderDrawColor(_renderer, 0, 150, 0, 255);
                SDL.RenderFillRect(_renderer, &tabRect);
            }
            SDL.SetRenderDrawColor(_renderer, 0, 200, 0, 255);
            SDL.RenderDrawRect(_renderer, &tabRect);

            // Tab label - use RGB values (scale=1, r, g, b, a)
            if (i == currentTab)
                _font.RenderText(_renderer, tabs[i], panelX + i * tabWidth + 5, panelY + 4, 1, 255, 255, 255, 255);
            else
                _font.RenderText(_renderer, tabs[i], panelX + i * tabWidth + 5, panelY + 4, 1, 0, 255, 0, 255);
        }

        // Content area
        int contentY = panelY + 25;
        int lineHeight = 14;
        int maxLines = (panelHeight - 50) / lineHeight;

        for (int i = 0; i < maxLines && scrollOffset + i < lines.Count; i++)
        {
            var line = lines[scrollOffset + i];
            byte r = 0, g = 255, b = 0;  // Default green

            if (line.StartsWith("===")) { r = 255; g = 255; b = 0; }  // Yellow for headers
            else if (line.StartsWith("  ")) { r = 136; g = 255; b = 136; }  // Light green for indented

            _font.RenderText(_renderer, line, panelX + 10, contentY + i * lineHeight, 1, r, g, b, 255);
        }

        // Scrollbar
        if (lines.Count > maxLines)
        {
            float ratio = (float)scrollOffset / Math.Max(1, lines.Count - maxLines);
            int sbHeight = Math.Max(20, (panelHeight - 50) * maxLines / lines.Count);
            int sbY = contentY + (int)((panelHeight - 50 - sbHeight) * ratio);

            SDL.SetRenderDrawColor(_renderer, 0, 150, 0, 200);
            var sbRect = new SDLRect { X = panelX + panelWidth - 15, Y = sbY, W = 10, H = sbHeight };
            SDL.RenderFillRect(_renderer, &sbRect);
        }

        // Help text
        _font.RenderText(_renderer, "F1:Close  Left/Right:Tabs  Up/Down:Scroll",
            panelX + 10, panelY + panelHeight - 18, 1, 136, 136, 136, 255);
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
        // Render regular messages (top-right of game area, not sidebar)
        var messageY = 30;
        foreach (var msg in messages)
        {
            // Skip messages that look like NPC dialogue (leaked from action scripts)
            // Filter: longer messages with dialogue-like text, or any Dialogue type that ended up here
            if (msg.Type == MessageType.Dialogue)
                continue;  // Dialogue messages belong in the dialogue box, not here

            // Also filter Info messages that look like leaked dialogue
            if (msg.Type == MessageType.Info && msg.Text.Length > 40)
            {
                // Contains NPC dialogue patterns
                if (msg.Text.Contains(":") && (msg.Text.Contains("\"") || msg.Text.Contains("?")))
                    continue;
                // Contains mission/Yoda text patterns
                if (msg.Text.Contains("mission") || msg.Text.Contains("Force") ||
                    msg.Text.Contains("you must") || msg.Text.Contains("HYPERSPACE"))
                    continue;
            }

            var alpha = (byte)(Math.Min(1.0, msg.TimeRemaining) * 255);
            RenderMessageBox(msg.Text, GameAreaWidth - 10, messageY, alpha, msg.Type, alignRight: true);
            messageY += 28;
        }

        // Render dialogue box (bottom of game area)
        if (dialogue != null)
        {
            var dialogueY = GameAreaHeight - 70;
            RenderDialogueBox(dialogue.Text, 10, dialogueY);
        }
    }

    private void RenderMessageBox(string text, int x, int y, byte alpha, MessageType type, bool alignRight = false)
    {
        // Calculate text width using font
        var textWidth = _font.GetTextWidth(text);
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
        var bgRect = new SDLRect { X = boxX, Y = y, W = boxWidth, H = 20 };
        SDL.RenderFillRect(_renderer, &bgRect);

        // Draw border
        SDL.SetRenderDrawColor(_renderer, 255, 255, 255, alpha);
        SDL.RenderDrawRect(_renderer, &bgRect);

        // Draw text using bitmap font
        _font.RenderText(_renderer, text, boxX + 8, y + 6, 1, 255, 255, 255, alpha);
    }

    private void RenderDialogueBox(string text, int x, int y)
    {
        var boxWidth = GameAreaWidth - 20;  // Fit within game area only
        var maxCharsPerLine = (boxWidth - 20) / 8;  // 8 pixels per character

        // Word wrap the text
        var wrappedLines = new List<string>();
        var words = text.Split(' ');
        var currentLine = "";

        foreach (var word in words)
        {
            if (currentLine.Length + word.Length + 1 <= maxCharsPerLine)
            {
                currentLine += (currentLine.Length > 0 ? " " : "") + word;
            }
            else
            {
                if (currentLine.Length > 0)
                    wrappedLines.Add(currentLine);
                currentLine = word;
            }
        }
        if (currentLine.Length > 0)
            wrappedLines.Add(currentLine);

        // Calculate box height based on lines
        var boxHeight = Math.Max(40, 16 + wrappedLines.Count * 12 + 16);

        // Draw background
        SDL.SetRenderDrawColor(_renderer, 20, 20, 50, 230);
        SDL.SetRenderDrawBlendMode(_renderer, SDLBlendMode.Blend);
        var bgRect = new SDLRect { X = x, Y = y, W = boxWidth, H = boxHeight };
        SDL.RenderFillRect(_renderer, &bgRect);

        // Draw border
        SDL.SetRenderDrawColor(_renderer, 200, 200, 255, 255);
        SDL.RenderDrawRect(_renderer, &bgRect);

        // Draw inner border
        var innerRect = new SDLRect { X = x + 2, Y = y + 2, W = boxWidth - 4, H = boxHeight - 4 };
        SDL.RenderDrawRect(_renderer, &innerRect);

        // Draw text using bitmap font
        var textY = y + 8;
        foreach (var line in wrappedLines)
        {
            _font.RenderText(_renderer, line, x + 10, textY);
            textY += 12;
        }

        // Draw "Press Space" hint at bottom right
        _font.RenderText(_renderer, "[SPACE]", x + boxWidth - 70, y + boxHeight - 14, 1, 150, 150, 200, 255);
    }

    public void Dispose()
    {
        _font?.Dispose();

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
