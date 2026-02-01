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

    // Palette animation support
    private HashSet<int> _animatedTileIds = new();
    private uint[] _atlasPixels = Array.Empty<uint>();

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

        // Calculate atlas dimensions
        _tilesPerRow = 32; // 32 tiles per row
        int rows = (_gameData.Tiles.Count + _tilesPerRow - 1) / _tilesPerRow;
        _atlasWidth = _tilesPerRow * TILE_SIZE;
        _atlasHeight = rows * TILE_SIZE;

        Console.WriteLine($"Creating tile atlas: {_atlasWidth}x{_atlasHeight} ({_gameData.Tiles.Count} tiles)");

        // Create pixel buffer and track animated tiles
        _atlasPixels = new uint[_atlasWidth * _atlasHeight];
        _animatedTileIds.Clear();

        // Copy tiles to atlas using the loaded palette
        for (int i = 0; i < _gameData.Tiles.Count; i++)
        {
            var tile = _gameData.Tiles[i];
            int atlasX = (i % _tilesPerRow) * TILE_SIZE;
            int atlasY = (i / _tilesPerRow) * TILE_SIZE;
            bool hasAnimatedPixel = false;

            for (int y = 0; y < TILE_SIZE; y++)
            {
                for (int x = 0; x < TILE_SIZE; x++)
                {
                    int srcIdx = y * TILE_SIZE + x;
                    int dstIdx = (atlasY + y) * _atlasWidth + (atlasX + x);

                    byte colorIdx = tile.PixelData[srcIdx];
                    _atlasPixels[dstIdx] = Palette.GetColor(colorIdx);

                    if (Palette.IsAnimatedIndex(colorIdx))
                        hasAnimatedPixel = true;
                }
            }

            if (hasAnimatedPixel)
                _animatedTileIds.Add(i);
        }

        // Create texture with STREAMING access for animated updates
        _tileAtlas = SDL.CreateTexture(
            _renderer,
            (uint)SDLPixelFormatEnum.Argb8888,
            (int)SDLTextureAccess.Streaming,
            _atlasWidth, _atlasHeight);

        if (_tileAtlas == null)
        {
            Console.WriteLine($"Failed to create tile atlas: {SDL.GetErrorS()}");
            return;
        }

        SDL.SetTextureBlendMode(_tileAtlas, SDLBlendMode.Blend);

        // Upload to texture
        fixed (uint* pixelPtr = _atlasPixels)
        {
            SDL.UpdateTexture(_tileAtlas, null, pixelPtr, _atlasWidth * 4);
        }

        Console.WriteLine($"Tile atlas created successfully ({_animatedTileIds.Count} animated tiles)");
    }

    /// <summary>
    /// Refreshes tiles that use animated palette colors.
    /// Call this when Palette.UpdateAnimation() returns true.
    /// </summary>
    public void RefreshAnimatedTiles()
    {
        if (_tileAtlas == null || _animatedTileIds.Count == 0)
            return;

        // Update only tiles that have animated palette indices
        foreach (var tileIndex in _animatedTileIds)
        {
            var tile = _gameData.Tiles[tileIndex];
            int atlasX = (tileIndex % _tilesPerRow) * TILE_SIZE;
            int atlasY = (tileIndex / _tilesPerRow) * TILE_SIZE;

            for (int y = 0; y < TILE_SIZE; y++)
            {
                for (int x = 0; x < TILE_SIZE; x++)
                {
                    int srcIdx = y * TILE_SIZE + x;
                    int dstIdx = (atlasY + y) * _atlasWidth + (atlasX + x);

                    byte colorIdx = tile.PixelData[srcIdx];
                    _atlasPixels[dstIdx] = Palette.GetColor(colorIdx);
                }
            }

            // Update just this tile's region in the texture
            var rect = new SDLRect { X = atlasX, Y = atlasY, W = TILE_SIZE, H = TILE_SIZE };
            fixed (uint* pixelPtr = _atlasPixels)
            {
                // Calculate pointer to start of this tile's region
                uint* tilePtr = pixelPtr + (atlasY * _atlasWidth + atlasX);
                SDL.UpdateTexture(_tileAtlas, &rect, tilePtr, _atlasWidth * 4);
            }
        }
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
