using Hexa.NET.SDL2;
using IndyNG.Engine.Data;
using IndyNG.Engine.Game;

namespace IndyNG.Engine.Rendering;

/// <summary>
/// Renders the game using SDL2
/// </summary>
public unsafe class GameRenderer : IDisposable
{
    private readonly SDLRenderer* _renderer;
    private readonly GameData _gameData;
    private readonly int _scale;

    private SDLTexture* _tileAtlas;
    private int _atlasWidth;
    private int _atlasHeight;
    private int _tilesPerRow;

    private const int TILE_SIZE = 32;

    public GameRenderer(SDLRenderer* renderer, GameData gameData, int scale)
    {
        _renderer = renderer;
        _gameData = gameData;
        _scale = scale;

        CreateTileAtlas();
    }

    private void CreateTileAtlas()
    {
        if (_gameData.Tiles.Count == 0)
        {
            Console.WriteLine("No tiles to create atlas");
            return;
        }

        // Calculate atlas dimensions (power of 2)
        _tilesPerRow = 32; // 32 tiles per row
        int rows = (_gameData.Tiles.Count + _tilesPerRow - 1) / _tilesPerRow;
        _atlasWidth = _tilesPerRow * TILE_SIZE;
        _atlasHeight = rows * TILE_SIZE;

        Console.WriteLine($"Creating tile atlas: {_atlasWidth}x{_atlasHeight} ({_gameData.Tiles.Count} tiles)");

        // Create texture (ARGB8888 format to match our palette)
        _tileAtlas = SDL.CreateTexture(
            _renderer,
            (uint)SDLPixelFormatEnum.Argb8888,
            (int)SDLTextureAccess.Static,
            _atlasWidth, _atlasHeight);

        if (_tileAtlas == null)
        {
            Console.WriteLine($"Failed to create tile atlas: {SDL.GetErrorS()}");
            return;
        }

        SDL.SetTextureBlendMode(_tileAtlas, SDLBlendMode.Blend);

        // Create pixel buffer
        var pixels = new uint[_atlasWidth * _atlasHeight];

        // Use the standard Desktop Adventures palette, slightly darkened for Indiana Jones
        // Build adjusted palette (reduce brightness by ~15%)
        uint[] adjustedPalette = new uint[256];
        for (int i = 0; i < 256; i++)
        {
            uint color = Palette.GetColor((byte)i);
            byte a = (byte)((color >> 24) & 0xFF);
            byte r = (byte)(((color >> 16) & 0xFF) * 85 / 100);  // Reduce to 85%
            byte g = (byte)(((color >> 8) & 0xFF) * 85 / 100);
            byte b = (byte)((color & 0xFF) * 85 / 100);
            adjustedPalette[i] = (uint)((a << 24) | (r << 16) | (g << 8) | b);
        }

        // Copy tiles to atlas
        for (int i = 0; i < _gameData.Tiles.Count; i++)
        {
            var tile = _gameData.Tiles[i];
            int atlasX = (i % _tilesPerRow) * TILE_SIZE;
            int atlasY = (i / _tilesPerRow) * TILE_SIZE;

            for (int y = 0; y < TILE_SIZE; y++)
            {
                for (int x = 0; x < TILE_SIZE; x++)
                {
                    int srcIdx = y * TILE_SIZE + x;
                    int dstIdx = (atlasY + y) * _atlasWidth + (atlasX + x);

                    byte colorIdx = tile.PixelData[srcIdx];
                    pixels[dstIdx] = adjustedPalette[colorIdx];
                }
            }
        }

        // Upload to texture
        fixed (uint* pixelPtr = pixels)
        {
            SDL.UpdateTexture(_tileAtlas, null, pixelPtr, _atlasWidth * 4);
        }

        Console.WriteLine("Tile atlas created successfully");
    }

    public void Render(GameEngine engine)
    {
        if (_tileAtlas == null || engine.CurrentZone == null) return;

        var zone = engine.CurrentZone;
        int offsetX = 0;
        int offsetY = 0;

        // Center view on player for larger zones
        if (zone.Width > 10)
        {
            offsetX = engine.PlayerX - 5;
            offsetX = Math.Max(0, Math.Min(offsetX, zone.Width - 10));
        }
        if (zone.Height > 10)
        {
            offsetY = engine.PlayerY - 5;
            offsetY = Math.Max(0, Math.Min(offsetY, zone.Height - 10));
        }

        // Draw floor layer (0)
        for (int y = 0; y < Math.Min(zone.Height, 10); y++)
        {
            for (int x = 0; x < Math.Min(zone.Width, 10); x++)
            {
                int worldX = x + offsetX;
                int worldY = y + offsetY;

                var tileId = zone.GetTile(worldX, worldY, 0);
                DrawTile(tileId, x * TILE_SIZE * _scale, y * TILE_SIZE * _scale);
            }
        }

        // Draw middle layer (1) - objects/walls
        for (int y = 0; y < Math.Min(zone.Height, 10); y++)
        {
            for (int x = 0; x < Math.Min(zone.Width, 10); x++)
            {
                int worldX = x + offsetX;
                int worldY = y + offsetY;

                var tileId = zone.GetTile(worldX, worldY, 1);
                if (tileId != 0xFFFF)
                    DrawTile(tileId, x * TILE_SIZE * _scale, y * TILE_SIZE * _scale);
            }
        }

        // Draw NPCs
        foreach (var npc in engine.ZoneNPCs.Where(n => n.IsEnabled && n.IsAlive))
        {
            int screenX = (npc.X - offsetX) * TILE_SIZE * _scale;
            int screenY = (npc.Y - offsetY) * TILE_SIZE * _scale;

            if (screenX >= 0 && screenX < 10 * TILE_SIZE * _scale &&
                screenY >= 0 && screenY < 10 * TILE_SIZE * _scale)
            {
                // Get NPC tile from character data
                if (npc.CharacterId < _gameData.Characters.Count)
                {
                    var character = _gameData.Characters[npc.CharacterId];
                    var frame = character.Frames.WalkDown[0];
                    DrawTile(frame, screenX, screenY);
                }
            }
        }

        // Draw player
        int playerScreenX = (engine.PlayerX - offsetX) * TILE_SIZE * _scale;
        int playerScreenY = (engine.PlayerY - offsetY) * TILE_SIZE * _scale;

        // Get player tile (character 0)
        if (_gameData.Characters.Count > 0)
        {
            var playerChar = _gameData.Characters[0];
            ushort playerTile = engine.PlayerDirection switch
            {
                Direction.Up => playerChar.Frames.WalkUp[0],
                Direction.Down => playerChar.Frames.WalkDown[0],
                Direction.Left => playerChar.Frames.WalkLeft[0],
                Direction.Right => playerChar.Frames.WalkRight[0],
                _ => playerChar.Frames.WalkDown[0]
            };
            DrawTile(playerTile, playerScreenX, playerScreenY);
        }

        // Draw top layer (2) - overlays
        for (int y = 0; y < Math.Min(zone.Height, 10); y++)
        {
            for (int x = 0; x < Math.Min(zone.Width, 10); x++)
            {
                int worldX = x + offsetX;
                int worldY = y + offsetY;

                var tileId = zone.GetTile(worldX, worldY, 2);
                if (tileId != 0xFFFF)
                    DrawTile(tileId, x * TILE_SIZE * _scale, y * TILE_SIZE * _scale);
            }
        }

        // Draw HUD
        DrawHUD(engine);
    }

    private void DrawTile(int tileId, int screenX, int screenY)
    {
        if (tileId < 0 || tileId >= _gameData.Tiles.Count || tileId == 0xFFFF)
            return;

        int atlasX = (tileId % _tilesPerRow) * TILE_SIZE;
        int atlasY = (tileId / _tilesPerRow) * TILE_SIZE;

        var srcRect = new SDLRect { X = atlasX, Y = atlasY, W = TILE_SIZE, H = TILE_SIZE };
        var dstRect = new SDLRect { X = screenX, Y = screenY, W = TILE_SIZE * _scale, H = TILE_SIZE * _scale };

        SDL.RenderCopy(_renderer, _tileAtlas, &srcRect, &dstRect);
    }

    private void DrawHUD(GameEngine engine)
    {
        // HUD background
        int hudY = 10 * TILE_SIZE * _scale;

        SDL.SetRenderDrawColor(_renderer, 40, 40, 40, 255);
        var hudRect = new SDLRect { X = 0, Y = hudY, W = 10 * TILE_SIZE * _scale, H = 64 };
        SDL.RenderFillRect(_renderer, &hudRect);

        // Draw inventory items
        int invX = 10;
        foreach (var itemId in engine.Inventory.Take(8))
        {
            DrawTile(itemId, invX, hudY + 16);
            invX += TILE_SIZE * _scale + 5;
        }

        // Zone info
        SDL.SetRenderDrawColor(_renderer, 200, 200, 200, 255);
        // Note: Text rendering would require SDL_ttf, for now just show zone number
    }

    public void Dispose()
    {
        if (_tileAtlas != null)
        {
            SDL.DestroyTexture(_tileAtlas);
            _tileAtlas = null;
        }
    }
}
