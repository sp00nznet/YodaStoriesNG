using Hexa.NET.SDL2;
using YodaStoriesNG.Engine.Data;
using YodaStoriesNG.Engine.Game;
using YodaStoriesNG.Engine.Rendering;

namespace YodaStoriesNG.Engine.UI;

/// <summary>
/// A separate debug window that displays the world map, player position,
/// quest items, teleporters, and mission objectives.
/// Similar to the original game's Locator Map.
/// </summary>
public class DebugMapWindow : IDisposable
{
    private readonly GameState _state;
    private readonly WorldGenerator _worldGenerator;
    private readonly GameData _gameData;
    private readonly TileRenderer _tileRenderer;

    private unsafe SDLWindow* _window;
    private unsafe SDLRenderer* _renderer;
    private unsafe SDLTexture* _tileAtlas;
    private int _tilesPerRow;
    private BitmapFont? _font;

    private const int CellSize = 32; // Same as tile size
    private const int Padding = 10;
    private const int LegendWidth = 180;

    // Dynamic grid size - calculated from world map
    private int _gridWidth = 10;
    private int _gridHeight = 10;

    private bool _isOpen = false;
    private uint _windowId;

    // Map tile IDs - these will be found by scanning tiles with Map flag
    private int _tileEmpty = -1;
    private int _tileSpaceport = -1;
    private int _tilePuzzleUnsolved = -1;
    private int _tilePuzzleSolved = -1;
    private int _tileGateway = -1;
    private int _tileWall = -1;
    private int _tileObjective = -1;
    private int _tileVisited = -1;

    public bool IsOpen => _isOpen;

    public DebugMapWindow(GameState state, WorldGenerator worldGenerator, GameData gameData)
    {
        _state = state;
        _worldGenerator = worldGenerator;
        _gameData = gameData;
        _tileRenderer = new TileRenderer();

        // Find map tiles
        FindMapTiles();
    }

    private void FindMapTiles()
    {
        // First try to find by tile flags
        var mapTiles = new List<int>();
        for (int i = 0; i < _gameData.Tiles.Count; i++)
        {
            var tile = _gameData.Tiles[i];
            if (!tile.IsMap) continue;
            mapTiles.Add(i);

            var flags = tile.Flags;

            if ((flags & TileFlags.MapHome) != 0)
                _tileSpaceport = i;
            else if ((flags & TileFlags.MapPuzzleSolved) != 0)
                _tilePuzzleSolved = i;
            else if ((flags & TileFlags.MapPuzzleUnsolved) != 0)
                _tilePuzzleUnsolved = i;
            else if ((flags & TileFlags.MapGateway) != 0)
                _tileGateway = i;
            else if ((flags & TileFlags.MapWall) != 0)
                _tileWall = i;
            else if ((flags & TileFlags.MapObjective) != 0)
                _tileObjective = i;
            else if (_tileEmpty < 0)
                _tileEmpty = i;
        }

        Console.WriteLine($"[DebugMap] Found {mapTiles.Count} tiles with Map flag: [{string.Join(",", mapTiles.Take(20))}]");

        // If flags didn't work, use known tile positions from the atlas
        // Map tiles are at approximately pixel (590, 560) in the tile atlas
        // Tile atlas uses 47 tiles per row (from actual game output)
        // With 47 tiles per row and 32x32 pixel tiles:
        // Col = 590/32 = 18, Row = 560/32 = 17
        // TileId = 17 * 47 + 18 = 817
        if (_tileEmpty < 0 || _tileSpaceport < 0)
        {
            Console.WriteLine("[DebugMap] Using hardcoded map tile positions...");
            // Atlas uses 47 tiles per row (ceil(sqrt(2123)) = 47)
            int tilesPerRow = 47;
            int startCol = 590 / 32; // 18
            int startRow = 560 / 32; // 17
            int baseId = startRow * tilesPerRow + startCol;

            Console.WriteLine($"[DebugMap] Calculated base tile ID: {baseId} (row={startRow}, col={startCol}, tilesPerRow={tilesPerRow})");

            // Map tiles layout based on user's reference:
            // Starting at tile 817: empty, various map icons in sequence
            if (baseId >= 0 && baseId < _gameData.Tiles.Count)
            {
                _tileEmpty = baseId;
                if (baseId + 1 < _gameData.Tiles.Count) _tileSpaceport = baseId + 1;
                if (baseId + 2 < _gameData.Tiles.Count) _tilePuzzleUnsolved = baseId + 2;
                if (baseId + 3 < _gameData.Tiles.Count) _tilePuzzleSolved = baseId + 3;
                if (baseId + 4 < _gameData.Tiles.Count) _tileGateway = baseId + 4;
                if (baseId + 5 < _gameData.Tiles.Count) _tileWall = baseId + 5;
                if (baseId + 6 < _gameData.Tiles.Count) _tileObjective = baseId + 6;
            }
        }

        _tileVisited = _tilePuzzleSolved >= 0 ? _tilePuzzleSolved : _tilePuzzleUnsolved;

        Console.WriteLine($"[DebugMap] Map tiles: Empty={_tileEmpty}, Spaceport={_tileSpaceport}, " +
            $"PuzzleUnsolved={_tilePuzzleUnsolved}, PuzzleSolved={_tilePuzzleSolved}, " +
            $"Gateway={_tileGateway}, Wall={_tileWall}, Objective={_tileObjective}");
    }

    public unsafe void Open()
    {
        if (_isOpen) return;

        // Calculate grid size from current world
        var world = _worldGenerator.CurrentWorld;
        if (world?.Grid != null)
        {
            _gridHeight = world.Grid.GetLength(0);
            _gridWidth = world.Grid.GetLength(1);
        }
        else
        {
            _gridWidth = 10;
            _gridHeight = 10;
        }

        int windowWidth = CellSize * _gridWidth + Padding * 2 + LegendWidth;
        int windowHeight = CellSize * _gridHeight + Padding * 2;

        _window = SDL.CreateWindow(
            "World Map",
            100, 100,
            windowWidth, windowHeight,
            (uint)(SDLWindowFlags.Shown | SDLWindowFlags.Resizable));

        if (_window == null)
        {
            Console.WriteLine($"Failed to create debug window: {SDL.GetErrorS()}");
            return;
        }

        _renderer = SDL.CreateRenderer(_window, -1,
            (uint)(SDLRendererFlags.Accelerated | SDLRendererFlags.Presentvsync));

        if (_renderer == null)
        {
            SDL.DestroyWindow(_window);
            _window = null;
            Console.WriteLine($"Failed to create debug renderer: {SDL.GetErrorS()}");
            return;
        }

        // Create tile atlas for this renderer
        CreateTileAtlas();

        // Initialize font for this renderer
        _font = new BitmapFont();
        _font.Initialize(_renderer);

        _windowId = SDL.GetWindowID(_window);
        _isOpen = true;
        Console.WriteLine("[DebugMap] Window opened");
    }

    private unsafe void CreateTileAtlas()
    {
        if (_gameData.Tiles.Count == 0) return;

        _tilesPerRow = (int)Math.Ceiling(Math.Sqrt(_gameData.Tiles.Count));
        var (pixels, width, height) = _tileRenderer.CreateTileAtlas(_gameData.Tiles, _tilesPerRow);

        _tileAtlas = SDL.CreateTexture(
            _renderer,
            (uint)SDLPixelFormatEnum.Argb8888,
            (int)SDLTextureAccess.Static,
            width, height);

        if (_tileAtlas == null)
        {
            Console.WriteLine($"[DebugMap] Failed to create tile atlas: {SDL.GetErrorS()}");
            return;
        }

        SDL.SetTextureBlendMode(_tileAtlas, SDLBlendMode.Blend);

        fixed (uint* pixelPtr = pixels)
        {
            SDL.UpdateTexture(_tileAtlas, null, pixelPtr, width * 4);
        }

        Console.WriteLine($"[DebugMap] Created tile atlas: {width}x{height}");
    }

    public unsafe void Close()
    {
        if (!_isOpen) return;

        _font?.Dispose();
        _font = null;

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

        _isOpen = false;
        Console.WriteLine("[DebugMap] Window closed");
    }

    public void Toggle()
    {
        if (_isOpen)
            Close();
        else
            Open();
    }

    public unsafe bool HandleEvent(SDLEvent* evt)
    {
        if (!_isOpen) return false;

        if (evt->Type == (uint)SDLEventType.Windowevent &&
            evt->Window.WindowID == _windowId)
        {
            if (evt->Window.Event == (byte)SDLWindowEventID.Close)
            {
                Close();
                return true;
            }
        }

        return false;
    }

    public unsafe void Render()
    {
        if (!_isOpen || _renderer == null) return;

        var world = _worldGenerator.CurrentWorld;
        if (world == null) return;

        // Clear background
        SDL.SetRenderDrawColor(_renderer, 20, 20, 40, 255);
        SDL.RenderClear(_renderer);

        // Draw grid
        RenderGrid(world);

        // Draw legend
        RenderLegend(world);

        SDL.RenderPresent(_renderer);
    }

    private unsafe void RenderGrid(WorldMap world)
    {
        if (world.Grid == null || world.TypeMap == null) return;

        // Get actual grid dimensions
        int gridHeight = world.Grid.GetLength(0);
        int gridWidth = world.Grid.GetLength(1);

        // Find current zone position
        int currentGridX = -1, currentGridY = -1;
        for (int y = 0; y < gridHeight; y++)
        {
            for (int x = 0; x < gridWidth; x++)
            {
                if (world.Grid[y, x] == _state.CurrentZoneId)
                {
                    currentGridX = x;
                    currentGridY = y;
                    break;
                }
            }
            if (currentGridX >= 0) break;
        }

        // Draw each cell
        for (int y = 0; y < gridHeight; y++)
        {
            for (int x = 0; x < gridWidth; x++)
            {
                int screenX = Padding + x * CellSize;
                int screenY = Padding + y * CellSize;

                var zoneId = world.Grid[y, x];
                var sectorType = world.TypeMap[y, x];
                bool isCurrent = (x == currentGridX && y == currentGridY);
                bool isVisited = zoneId.HasValue && _state.IsZoneVisited(zoneId.Value);
                bool isSolved = zoneId.HasValue && _state.IsZoneSolved(zoneId.Value);

                // Select appropriate tile
                int tileId = GetTileForSector(zoneId, sectorType, isVisited, isSolved, world);

                // Only render if we have a valid tile (skip empty/unexplored cells)
                if (tileId >= 0)
                {
                    if (_tileAtlas != null)
                    {
                        RenderTile(tileId, screenX, screenY);
                    }
                    else
                    {
                        // Fallback: draw colored rectangle
                        var color = GetColorForSector(zoneId, sectorType, isVisited);
                        SDL.SetRenderDrawColor(_renderer, color.r, color.g, color.b, 255);
                        var rect = new SDLRect { X = screenX, Y = screenY, W = CellSize, H = CellSize };
                        SDL.RenderFillRect(_renderer, &rect);
                    }

                    // Draw grid lines for filled cells only
                    SDL.SetRenderDrawColor(_renderer, 60, 60, 80, 255);
                    var borderRect = new SDLRect { X = screenX, Y = screenY, W = CellSize, H = CellSize };
                    SDL.RenderDrawRect(_renderer, &borderRect);
                }

                // Draw current position highlight (always, even on empty cells)
                if (isCurrent)
                {
                    DrawCurrentMarker(screenX, screenY);
                }
            }
        }
    }

    private int GetTileForSector(int? zoneId, SectorType sectorType, bool isVisited, bool isSolved, WorldMap world)
    {
        // Empty/None sectors should show nothing (return -1 to skip rendering)
        if (zoneId == null || sectorType == SectorType.None || sectorType == SectorType.KeptFree)
            return -1; // Don't render anything

        // Empty and Candidate sectors are just traversable terrain, not quest locations
        // Only show them if visited
        if (sectorType == SectorType.Empty || sectorType == SectorType.Candidate)
        {
            if (isVisited && _tileVisited >= 0)
                return _tileVisited;
            return -1; // Don't show unvisited empty zones
        }

        // Objective zone - highest priority
        if (zoneId == world.ObjectiveZoneId && _tileObjective >= 0)
            return _tileObjective;

        // Spaceport (home)
        if (sectorType == SectorType.Spaceport && _tileSpaceport >= 0)
            return _tileSpaceport;

        // Blockade zones
        if (sectorType == SectorType.BlockNorth || sectorType == SectorType.BlockSouth ||
            sectorType == SectorType.BlockEast || sectorType == SectorType.BlockWest)
        {
            if (isSolved && _tilePuzzleSolved >= 0)
                return _tilePuzzleSolved;
            return _tileWall >= 0 ? _tileWall : _tilePuzzleUnsolved;
        }

        // Travel zones (gateways)
        if (sectorType == SectorType.TravelStart || sectorType == SectorType.TravelEnd)
        {
            return _tileGateway >= 0 ? _tileGateway : _tilePuzzleUnsolved;
        }

        // Puzzle zones
        if (sectorType == SectorType.Puzzle)
        {
            if (isSolved && _tilePuzzleSolved >= 0)
                return _tilePuzzleSolved;
            if (isVisited && _tileVisited >= 0)
                return _tileVisited;
            if (_tilePuzzleUnsolved >= 0)
                return _tilePuzzleUnsolved;
        }

        // Island zones
        if (sectorType == SectorType.Island)
        {
            if (isSolved && _tilePuzzleSolved >= 0)
                return _tilePuzzleSolved;
            return _tilePuzzleUnsolved >= 0 ? _tilePuzzleUnsolved : -1;
        }

        return -1; // Unknown sector type - don't render
    }

    private (byte r, byte g, byte b) GetColorForSector(int? zoneId, SectorType sectorType, bool isVisited)
    {
        if (zoneId == null || sectorType == SectorType.None || sectorType == SectorType.KeptFree)
            return (40, 40, 60); // Empty

        if (sectorType == SectorType.Spaceport)
            return (100, 200, 100); // Green

        if (sectorType == SectorType.BlockNorth || sectorType == SectorType.BlockSouth ||
            sectorType == SectorType.BlockEast || sectorType == SectorType.BlockWest)
            return (200, 50, 50); // Red

        if (sectorType == SectorType.TravelStart || sectorType == SectorType.TravelEnd)
            return (50, 150, 200); // Blue

        if (isVisited)
            return (60, 80, 120); // Dark blue

        return (200, 200, 50); // Yellow (unvisited puzzle)
    }

    private unsafe void RenderTile(int tileId, int x, int y)
    {
        if (_tileAtlas == null || tileId < 0 || tileId >= _gameData.Tiles.Count)
            return;

        var atlasX = (tileId % _tilesPerRow) * Tile.Width;
        var atlasY = (tileId / _tilesPerRow) * Tile.Height;

        var srcRect = new SDLRect { X = atlasX, Y = atlasY, W = Tile.Width, H = Tile.Height };
        var dstRect = new SDLRect { X = x, Y = y, W = CellSize, H = CellSize };

        SDL.RenderCopy(_renderer, _tileAtlas, &srcRect, &dstRect);
    }

    private unsafe void DrawCurrentMarker(int x, int y)
    {
        // Draw pulsing border
        double pulse = Math.Sin(SDL.GetTicks() / 200.0) * 0.5 + 0.5;
        byte intensity = (byte)(155 + 100 * pulse);

        SDL.SetRenderDrawColor(_renderer, intensity, 50, 50, 255);

        // Draw thick border
        for (int i = 0; i < 3; i++)
        {
            var rect = new SDLRect { X = x + i, Y = y + i, W = CellSize - i * 2, H = CellSize - i * 2 };
            SDL.RenderDrawRect(_renderer, &rect);
        }
    }

    private unsafe void RenderLegend(WorldMap world)
    {
        int legendX = Padding + _gridWidth * CellSize + 10;
        int legendY = Padding;
        int lineHeight = 20;

        // Title
        DrawText(legendX, legendY, "WORLD MAP", (255, 255, 255));
        legendY += lineHeight + 5;

        // Current zone info
        var zone = _state.CurrentZone;
        if (zone != null)
        {
            DrawText(legendX, legendY, $"Zone: {_state.CurrentZoneId}", (200, 200, 200));
            legendY += lineHeight;

            string typeName = zone.Type.ToString();
            DrawText(legendX, legendY, $"Type: {typeName}", (200, 200, 200));
            legendY += lineHeight;
        }

        legendY += 10;

        // Mission info
        if (world.Mission != null)
        {
            DrawText(legendX, legendY, $"Mission {world.Mission.MissionNumber}/15", (255, 215, 0));
            legendY += lineHeight;

            var step = world.Mission.CurrentPuzzleStep;
            if (step != null)
            {
                // Word wrap the hint
                string hint = step.Hint ?? "No hint";
                var lines = WordWrap(hint, 22);
                foreach (var line in lines.Take(4))
                {
                    DrawText(legendX, legendY, line, (200, 200, 100));
                    legendY += lineHeight - 4;
                }
            }
            else if (world.Mission.IsCompleted)
            {
                DrawText(legendX, legendY, "COMPLETE!", (50, 255, 50));
            }
        }

        legendY += 15;

        // Player position
        DrawText(legendX, legendY, $"Pos: {_state.PlayerX},{_state.PlayerY}", (150, 150, 150));
        legendY += lineHeight;

        // Inventory count
        DrawText(legendX, legendY, $"Items: {_state.Inventory.Count}", (150, 150, 150));
        legendY += lineHeight;

        // Health
        DrawText(legendX, legendY, $"HP: {_state.Health}/{_state.MaxHealth}", (150, 150, 150));
    }

    private List<string> WordWrap(string text, int maxChars)
    {
        var lines = new List<string>();
        if (string.IsNullOrEmpty(text)) return lines;

        var words = text.Split(' ');
        var currentLine = "";

        foreach (var word in words)
        {
            if (currentLine.Length + word.Length + 1 > maxChars)
            {
                if (currentLine.Length > 0)
                    lines.Add(currentLine);
                currentLine = word;
            }
            else
            {
                currentLine = currentLine.Length > 0 ? currentLine + " " + word : word;
            }
        }

        if (currentLine.Length > 0)
            lines.Add(currentLine);

        return lines;
    }

    private unsafe void DrawText(int x, int y, string text, (byte r, byte g, byte b) color)
    {
        _font?.RenderText(_renderer, text, x, y, 1, color.r, color.g, color.b, 255);
    }

    public void Dispose()
    {
        Close();
    }
}
