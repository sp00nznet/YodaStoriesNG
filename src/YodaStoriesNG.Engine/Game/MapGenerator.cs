namespace YodaStoriesNG.Engine.Game;

/// <summary>
/// World size affects the number of puzzles and complexity.
/// </summary>
public enum WorldSize
{
    Small = 1,
    Medium = 2,
    Large = 3,
    XtraLarge = 4  // Special 15x15 grid with all 15 missions
}

/// <summary>
/// Generates the world map layout with sector types.
/// Supports 10x10 (Small/Medium/Large) and 15x15 (XtraLarge) grids.
/// This is a direct port of the webfun reference implementation.
///
/// The algorithm works in phases:
/// 1. Initialize center 4 cells around spaceport
/// 2. Expand outward in 3 iterations, placing Empty/Candidate/Blockade sectors
/// 3. Place travel zones that connect to islands at edges
/// 4. Build islands along edges for travel destinations
/// 5. Determine puzzle locations and ordering
/// </summary>
public class MapGenerator
{
    // Dynamic grid size - 10 for Small/Medium/Large, 15 for XtraLarge
    private int _mapWidth = 10;
    private int _mapHeight = 10;

    /// <summary>Current grid width.</summary>
    public int MapWidth => _mapWidth;

    /// <summary>Current grid height.</summary>
    public int MapHeight => _mapHeight;

    // Distance from center lookup table - generated dynamically for the current grid size
    private int[] _distanceToCenter = new int[100];

    // State during generation
    private int _minX, _minY;
    private int _altX, _altY;
    private int _variance;
    private int _probability;
    private int _threshold;
    private int _travelThreshold;
    private SectorType _lastType;
    private int _remainingSectors;
    private int _placedSectors;
    private int _blockadeCount;
    private int _travelCount;
    private int _placedTravels;
    private int _puzzleCount;

    // The generated maps - dynamically sized
    private SectorType[] _typeMap = new SectorType[100];
    private int[] _orderMap = new int[100];

    /// <summary>
    /// Generates the distance-to-center lookup table for the current grid size.
    /// </summary>
    private void GenerateDistanceTable()
    {
        int size = _mapWidth * _mapHeight;
        _distanceToCenter = new int[size];

        int centerX1 = _mapWidth / 2 - 1;
        int centerX2 = _mapWidth / 2;
        int centerY1 = _mapHeight / 2 - 1;
        int centerY2 = _mapHeight / 2;

        for (int y = 0; y < _mapHeight; y++)
        {
            for (int x = 0; x < _mapWidth; x++)
            {
                // Calculate distance from center region
                int dx = Math.Min(Math.Abs(x - centerX1), Math.Abs(x - centerX2));
                int dy = Math.Min(Math.Abs(y - centerY1), Math.Abs(y - centerY2));
                int distance = Math.Max(dx, dy) + 1;

                // Cap at half the grid size
                int maxDist = (_mapWidth + 1) / 2;
                _distanceToCenter[x + y * _mapWidth] = Math.Min(distance, maxDist);
            }
        }
    }

    // Seeded random for deterministic generation
    private Random _random = new();
    private int _seed;

    /// <summary>
    /// The generated sector type map (10x10 = 100 entries).
    /// </summary>
    public SectorType[] TypeMap => _typeMap;

    /// <summary>
    /// The puzzle ordering map (-1 = no puzzle, 0+ = puzzle order).
    /// </summary>
    public int[] OrderMap => _orderMap;

    /// <summary>
    /// Number of puzzles in the generated world.
    /// </summary>
    public int PuzzleCount => _orderMap.Max() + 1;

    /// <summary>
    /// Number of blockades placed.
    /// </summary>
    public int BlockadeCount => _blockadeCount;

    /// <summary>
    /// Number of travel zones placed.
    /// </summary>
    public int TravelCount => _travelCount;

    /// <summary>
    /// Gets the distance to center for a grid position.
    /// </summary>
    public int GetDistanceToCenter(int x, int y)
    {
        if (x < 0 || x >= _mapWidth || y < 0 || y >= _mapHeight) return (_mapWidth + 1) / 2;
        return _distanceToCenter[x + _mapWidth * y];
    }

    /// <summary>
    /// Generates a world map with the given seed and size.
    /// </summary>
    /// <param name="seed">Random seed (-1 for random)</param>
    /// <param name="size">World size (Small/Medium/Large/XtraLarge)</param>
    public void Generate(int seed, WorldSize size)
    {
        // Set grid size based on world size
        if (size == WorldSize.XtraLarge)
        {
            _mapWidth = 15;
            _mapHeight = 15;
        }
        else
        {
            _mapWidth = 10;
            _mapHeight = 10;
        }

        // Initialize distance table for this size
        GenerateDistanceTable();

        // Initialize random
        _seed = seed >= 0 ? seed : Environment.TickCount;
        _random = new Random(_seed);

        _travelCount = 0;
        _placedTravels = 0;
        _blockadeCount = 0;
        _puzzleCount = 0;
        _placedSectors = 0;

        // Determine random counts for travels and blockades
        DetermineCounts();

        // Waste a random number (matches reference)
        _random.Next();

        // Place spaceport in one of the 4 center cells
        int centerOffset = _mapWidth / 2 - 1;
        int spaceportX = (_random.Next() % 2) + centerOffset;
        int spaceportY = (_random.Next() % 2) + centerOffset;

        InitializeTypeMap(spaceportX, spaceportY);
        InitializeOrderMap();

        // Get puzzle count ranges based on world size
        var ranges = GetRangesForSize(size);

        // Calculate items to place in first iteration
        int itemsToPlace = _travelCount + _blockadeCount + RandomInRange(ranges[0]);

        // Phase 1-3: Expand outward from center, placing sectors
        DeterminePuzzleLocations(2, Math.Min(itemsToPlace, 12));
        DeterminePuzzleLocations(3, RandomInRange(ranges[1]));
        DeterminePuzzleLocations(4, RandomInRange(ranges[2]));

        // Phase 4: Place additional sectors at edges
        DetermineAdditionalPuzzleLocations(RandomInRange(ranges[3]));

        // Phase 5: Build islands for travel destinations
        PlaceIslands(_placedTravels);

        // Phase 6: Determine actual puzzle locations and ordering
        PlaceIntermediateWorldThing();

        Console.WriteLine($"[MapGenerator] Generated world: seed={_seed}, size={size}");
        Console.WriteLine($"  Puzzles: {PuzzleCount}, Blockades: {_blockadeCount}, Travels: {_travelCount}");
        PrintTypeMap();
    }

    private void DetermineCounts()
    {
        _travelCount = _random.Next() % 3;      // 0-2 travels
        _blockadeCount = _random.Next() % 4;    // 0-3 blockades
        _placedSectors = 0;
        _placedTravels = 0;
    }

    private (int min, int max)[] GetRangesForSize(WorldSize size)
    {
        return size switch
        {
            WorldSize.Small => new[] { (5, 8), (4, 6), (1, 1), (1, 1) },
            WorldSize.Medium => new[] { (5, 9), (5, 9), (4, 8), (3, 8) },
            WorldSize.Large => new[] { (6, 12), (6, 12), (6, 11), (4, 11) },
            // XtraLarge: 15x15 grid with scaled up puzzle counts (~2.25x more area)
            WorldSize.XtraLarge => new[] { (10, 18), (10, 18), (10, 16), (8, 16) },
            _ => new[] { (5, 9), (5, 9), (4, 8), (3, 8) }
        };
    }

    private int RandomInRange((int min, int max) range)
    {
        return range.min + (_random.Next() % (range.max - range.min + 1));
    }

    private void InitializeTypeMap(int spaceportX, int spaceportY)
    {
        int size = _mapWidth * _mapHeight;
        _typeMap = new SectorType[size];
        for (int i = 0; i < size; i++)
            _typeMap[i] = SectorType.None;

        // Initialize center 4 cells as Empty
        int centerX = _mapWidth / 2 - 1;
        int centerY = _mapHeight / 2 - 1;
        _typeMap[centerX + centerY * _mapWidth] = SectorType.Empty;
        _typeMap[(centerX + 1) + centerY * _mapWidth] = SectorType.Empty;
        _typeMap[centerX + (centerY + 1) * _mapWidth] = SectorType.Empty;
        _typeMap[(centerX + 1) + (centerY + 1) * _mapWidth] = SectorType.Empty;

        // Place spaceport
        _typeMap[spaceportX + _mapWidth * spaceportY] = SectorType.Spaceport;
    }

    private void InitializeOrderMap()
    {
        int size = _mapWidth * _mapHeight;
        _orderMap = new int[size];
        for (int i = 0; i < size; i++)
            _orderMap[i] = -1;
    }

    private SectorType GetType(int x, int y)
    {
        if (x < 0 || x >= _mapWidth || y < 0 || y >= _mapHeight) return SectorType.None;
        return _typeMap[x + _mapWidth * y];
    }

    private void SetType(int x, int y, SectorType type)
    {
        if (x < 0 || x >= _mapWidth || y < 0 || y >= _mapHeight) return;
        _typeMap[x + _mapWidth * y] = type;
    }

    private int GetOrder(int x, int y)
    {
        if (x < 0 || x >= _mapWidth || y < 0 || y >= _mapHeight) return -1;
        return _orderMap[x + _mapWidth * y];
    }

    private void SetOrder(int x, int y, int order)
    {
        if (x < 0 || x >= _mapWidth || y < 0 || y >= _mapHeight) return;
        _orderMap[x + _mapWidth * y] = order;
    }

    private SectorType BlockadeTypeFor(int xdiff, int ydiff)
    {
        if (xdiff == 0 && ydiff == 1) return SectorType.BlockNorth;
        if (xdiff == 0 && ydiff == -1) return SectorType.BlockSouth;
        if (xdiff == -1 && ydiff == 0) return SectorType.BlockEast;
        if (xdiff == 1 && ydiff == 0) return SectorType.BlockWest;
        return SectorType.None;
    }

    private bool IsFree(SectorType type)
    {
        return type == SectorType.None || type == SectorType.KeptFree;
    }

    private bool IsLessThanCandidate(SectorType type)
    {
        return type == SectorType.None ||
               type == SectorType.Empty ||
               type == SectorType.TravelStart ||
               type == SectorType.TravelEnd ||
               type == SectorType.Island ||
               type == SectorType.Spaceport;
    }

    private bool IsBlockade(SectorType type)
    {
        return type == SectorType.BlockWest ||
               type == SectorType.BlockEast ||
               type == SectorType.BlockNorth ||
               type == SectorType.BlockSouth;
    }

    private void PlaceBlockade(int x, int y, int xdif, int ydif)
    {
        SetType(x, y, BlockadeTypeFor(xdif, ydif));
        SetType(x - ydif, y - xdif, SectorType.KeptFree);
        SetType(x + ydif, y + xdif, SectorType.KeptFree);
        SetType(x - xdif, y - ydif, SectorType.Candidate);
        SetType(x - ydif - xdif, y - ydif - xdif, SectorType.KeptFree);
        SetType(x + ydif - xdif, y - ydif + xdif, SectorType.KeptFree);

        _remainingSectors--;
        _placedSectors += 2;
        _blockadeCount--;
    }

    private void ExtendBlockade(int x, int y, int xdif, int ydif)
    {
        SetType(x, y, SectorType.Candidate);
        SetType(x - ydif, y - xdif, SectorType.KeptFree);
        SetType(x + ydif, y + xdif, SectorType.KeptFree);

        _placedSectors++;
        _remainingSectors--;
    }

    private bool HandleNeighbor(int x, int y, int iteration, int xdif, int ydif)
    {
        var neighbor = GetType(x + xdif, y + ydif);
        var neighborOtherAxisBefore = GetType(x + ydif, y + xdif);
        var neighborOtherAxisAfter = GetType(x - ydif, y - xdif);

        if (IsFree(neighbor)) return false;

        _lastType = GetType(x + xdif, y + ydif);

        var blockadeType = BlockadeTypeFor(xdif, ydif);
        bool canPlaceBlockade = IsFree(neighborOtherAxisBefore) && IsFree(neighborOtherAxisAfter);

        if ((canPlaceBlockade && neighbor == SectorType.Candidate) || neighbor == blockadeType)
        {
            ExtendBlockade(x, y, xdif, ydif);
        }

        if (neighbor == SectorType.Candidate) return true;
        if (IsBlockade(neighbor)) return true;

        bool shouldPlaceBlockade = _blockadeCount > 0 && (_random.Next() % _probability) < _threshold;
        bool isWithinBlockadeRange = GetDistanceToCenter(x + xdif, y + ydif) < iteration;

        // Check if all neighbors are less than candidate
        bool allNeighborsAreFree = IsLessThanCandidate(GetType(x - 1, y)) &&
                                   IsLessThanCandidate(GetType(x + 1, y)) &&
                                   IsLessThanCandidate(GetType(x, y - 1)) &&
                                   IsLessThanCandidate(GetType(x, y + 1));

        if (shouldPlaceBlockade && canPlaceBlockade && isWithinBlockadeRange)
        {
            PlaceBlockade(x, y, xdif, ydif);
            return true;
        }

        if (!allNeighborsAreFree) return true;

        if (!shouldPlaceBlockade || (ydif != 0 && !canPlaceBlockade) || (xdif != 0 && isWithinBlockadeRange))
        {
            _typeMap[x + 10 * y] = SectorType.Empty;
            _placedSectors++;
            _remainingSectors--;
            return true;
        }

        return true;
    }

    private void TryPlacingTravel(int itemIdx, int iteration, SectorType lastTime)
    {
        if (_typeMap[itemIdx] != SectorType.Empty) return;
        if (_travelCount <= _placedTravels) return;
        if ((_random.Next() & 7) >= _travelThreshold) return;
        if (lastTime == SectorType.TravelStart) return;
        if (iteration <= 2) return;

        _typeMap[itemIdx] = SectorType.TravelStart;
        _placedTravels++;
    }

    private void DeterminePuzzleLocations(int iteration, int puzzleCountToPlace)
    {
        _remainingSectors = puzzleCountToPlace;

        // Calculate bounds based on grid size
        // For 10x10: center is 4-5, for 15x15: center is 6-8
        int center = _mapWidth / 2;

        switch (iteration)
        {
            case 2:
                _minX = center - 2; _minY = center - 2;
                _altX = center + 1; _altY = center + 1;
                _variance = 4;
                _probability = 9;
                _threshold = 2;
                _travelThreshold = 1;
                break;
            case 3:
                _minX = center - 3; _minY = center - 3;
                _altX = center + 2; _altY = center + 2;
                _variance = 6;
                _probability = 4;
                _threshold = 2;
                _travelThreshold = 3;
                break;
            case 4:
                _minX = 1; _minY = 1;
                _altX = _mapWidth - 2; _altY = _mapHeight - 2;
                _variance = _mapWidth - 2;
                _threshold = 1;
                _probability = 5;
                _travelThreshold = 6;
                break;
            default:
                return;
        }

        // Scale iterations based on grid size
        int maxIterations = _mapWidth == 15 ? 324 : 144;

        for (int i = 0; i <= maxIterations && _remainingSectors > 0; i++)
        {
            int x, y;
            if (_random.Next() % 2 != 0)
            {
                x = _random.Next() % 2 != 0 ? _minX : _altX;
                y = (_random.Next() % _variance) + _minY;
            }
            else
            {
                y = _random.Next() % 2 != 0 ? _minY : _altY;
                x = (_random.Next() % _variance) + _minX;
            }

            // Clamp to valid range
            x = Math.Clamp(x, 0, _mapWidth - 1);
            y = Math.Clamp(y, 0, _mapHeight - 1);

            int itemIdx = x + _mapWidth * y;
            if (_typeMap[itemIdx] != SectorType.None) continue;

            // Try each direction
            if (!HandleNeighbor(x, y, iteration, -1, 0) &&
                !HandleNeighbor(x, y, iteration, 1, 0) &&
                !HandleNeighbor(x, y, iteration, 0, -1))
            {
                HandleNeighbor(x, y, iteration, 0, 1);
            }

            TryPlacingTravel(itemIdx, iteration, _lastType);
        }
    }

    private void DetermineAdditionalPuzzleLocations(int travelsToPlace)
    {
        int lastX = _mapWidth - 1;
        int lastY = _mapHeight - 1;
        int maxIterations = _mapWidth == 15 ? 900 : 400;

        for (int i = 0; i <= maxIterations && travelsToPlace > 0; i++)
        {
            int x, y;
            bool isVertical = _random.Next() % 2 != 0;

            if (isVertical)
            {
                x = _random.Next() % 2 != 0 ? 0 : lastX;
                y = _random.Next() % _mapHeight;
            }
            else
            {
                y = _random.Next() % 2 != 0 ? 0 : lastY;
                x = _random.Next() % _mapWidth;
            }

            int worldIdx = x + _mapWidth * y;
            if (_typeMap[worldIdx] != SectorType.None) continue;

            var itemBefore = GetType(x - 1, y);
            var itemAfter = GetType(x + 1, y);
            var itemAbove = GetType(x, y - 1);
            var itemBelow = GetType(x, y + 1);

            int yDiff = 0, xDiff = 0;

            if (isVertical && x == lastX && itemBefore != SectorType.KeptFree)
            {
                xDiff = 1; yDiff = 0;
            }
            else if (isVertical && x == 0 && itemAfter != SectorType.KeptFree)
            {
                xDiff = -1; yDiff = 0;
            }
            else if (!isVertical && y == lastY && itemAbove != SectorType.KeptFree)
            {
                xDiff = 0; yDiff = 1;
            }
            else if (!isVertical && y == 0 && itemBelow != SectorType.KeptFree)
            {
                xDiff = 0; yDiff = -1;
            }

            if (xDiff == 0 && yDiff == 0) continue;

            var itemNeighbor = GetType(x - xDiff, y - yDiff);
            if (itemNeighbor == SectorType.None) continue;

            switch (itemNeighbor)
            {
                case SectorType.Empty:
                case SectorType.TravelStart:
                case SectorType.Spaceport:
                    _typeMap[worldIdx] = SectorType.Empty;
                    break;
                case SectorType.Candidate:
                    _typeMap[worldIdx] = SectorType.Candidate;
                    if (xDiff == 0)
                    {
                        if (x > 0) _typeMap[worldIdx - 1] = SectorType.KeptFree;
                        if (x < lastX) _typeMap[worldIdx + 1] = SectorType.KeptFree;
                    }
                    else if (yDiff == 0)
                    {
                        if (y > 0) _typeMap[worldIdx - _mapWidth] = SectorType.KeptFree;
                        if (y < lastY) _typeMap[worldIdx + _mapWidth] = SectorType.KeptFree;
                    }
                    continue;
                case SectorType.BlockEast:
                    if (xDiff != 1) continue;
                    if (itemBelow > SectorType.None && itemBelow <= SectorType.BlockNorth) continue;
                    if (itemAbove > SectorType.None && itemAbove <= SectorType.BlockNorth) continue;
                    _typeMap[worldIdx] = SectorType.Candidate;
                    if (y > 0) _typeMap[worldIdx - _mapWidth] = SectorType.KeptFree;
                    if (y < lastY) _typeMap[worldIdx + _mapWidth] = SectorType.KeptFree;
                    break;
                case SectorType.BlockWest:
                    if (xDiff != -1) continue;
                    if (itemAbove > SectorType.None && itemAbove <= SectorType.BlockNorth) continue;
                    if (itemBelow > SectorType.None && itemBelow <= SectorType.BlockNorth) continue;
                    _typeMap[worldIdx] = SectorType.Candidate;
                    if (y > 0) _typeMap[worldIdx - _mapWidth] = SectorType.KeptFree;
                    if (y < lastY) _typeMap[worldIdx + _mapWidth] = SectorType.KeptFree;
                    break;
                case SectorType.BlockNorth:
                    if (yDiff != -1) continue;
                    if (itemBefore > SectorType.None && itemBefore <= SectorType.BlockNorth) continue;
                    if (itemAfter > SectorType.None && itemAfter <= SectorType.BlockNorth) continue;
                    _typeMap[worldIdx] = SectorType.Candidate;
                    if (x > 0) _typeMap[worldIdx - 1] = SectorType.KeptFree;
                    if (x < lastX) _typeMap[worldIdx + 1] = SectorType.KeptFree;
                    break;
                case SectorType.BlockSouth:
                    if (yDiff != 1) continue;
                    if (itemBefore > SectorType.None && itemBefore <= SectorType.BlockNorth) continue;
                    if (itemAfter > SectorType.None && itemAfter <= SectorType.BlockNorth) continue;
                    _typeMap[worldIdx] = SectorType.Candidate;
                    if (x > 0) _typeMap[worldIdx - 1] = SectorType.KeptFree;
                    if (x < lastX) _typeMap[worldIdx + 1] = SectorType.KeptFree;
                    break;
                default:
                    continue;
            }

            _placedSectors++;
            travelsToPlace--;
        }
    }

    #region Island Building

    private void PlaceIslands(int count)
    {
        for (int i = 0; i < count; i++)
        {
            PlaceIsland();
        }
    }

    private void PlaceIsland()
    {
        for (int i = 0; i <= 200; i++)
        {
            switch (_random.Next() % 4)
            {
                case 0: // West
                    if (PlaceIslandWest()) return;
                    break;
                case 1: // North
                    if (PlaceIslandNorth()) return;
                    break;
                case 2: // South
                    if (PlaceIslandSouth()) return;
                    break;
                case 3: // East
                    if (PlaceIslandEast()) return;
                    break;
            }
        }
    }

    private (int start, int length) FindRun(int fixedCoord, bool isVertical, int neighborDx, int neighborDy)
    {
        int start = 0, length = 0, currentRun = 0;
        int maxCoord = isVertical ? _mapHeight : _mapWidth;

        for (int i = 0; i < maxCoord; i++)
        {
            int x = isVertical ? fixedCoord : i;
            int y = isVertical ? i : fixedCoord;

            var current = GetType(x, y);
            var neighbor = GetType(x + neighborDx, y + neighborDy);

            if (current != SectorType.None || (neighbor != SectorType.None && neighbor != SectorType.KeptFree))
            {
                if (length < currentRun)
                {
                    length = currentRun;
                    start = i - currentRun;
                }
                currentRun = 0;
            }
            else
            {
                currentRun++;
            }
        }

        if (length < currentRun)
        {
            length = currentRun;
            start = maxCoord - currentRun;
        }

        return (start, length);
    }

    private void BuildIsland(int fixedCoord, int start, int length, bool isVertical)
    {
        for (int i = start; i < start + length; i++)
        {
            int x = isVertical ? fixedCoord : i;
            int y = isVertical ? i : fixedCoord;
            SetType(x, y, SectorType.Island);
        }

        // Place TravelEnd at one end
        int endIdx = _random.Next() % 2;
        int endPos = endIdx == 0 ? start : start + length - 1;
        int ex = isVertical ? fixedCoord : endPos;
        int ey = isVertical ? endPos : fixedCoord;
        SetType(ex, ey, SectorType.TravelEnd);
    }

    private bool VerifyShortRun(ref int start, ref int length)
    {
        if (length < 3) return false;
        int maxIslandLen = _mapWidth == 15 ? 6 : 4;
        int thresholdStart = _mapWidth == 15 ? 11 : 7;

        if (length == 3)
        {
            if (0 < start && start < thresholdStart) return false;
            if (start == 0) length = 2;
            if (start == thresholdStart) length = 2;
        }
        else if (length >= 4)
        {
            length = Math.Min(length - 2, maxIslandLen);
        }

        if (start > 0 && start + length < _mapWidth) start++;
        return true;
    }

    private bool VerifyLongRun(ref int start, ref int length)
    {
        int maxIslandLen = _mapWidth == 15 ? 6 : 4;
        if (length < 4) return false;
        length = Math.Min(length - 2, maxIslandLen);
        if (start > 0 && start + length < _mapWidth) start++;
        return true;
    }

    private bool PlaceIslandWest()
    {
        var (start, length) = FindRun(0, true, 1, 0);
        if (!VerifyShortRun(ref start, ref length)) return false;
        BuildIsland(0, start, length, true);
        return true;
    }

    private bool PlaceIslandNorth()
    {
        var (start, length) = FindRun(0, false, 0, 1);
        if (!VerifyShortRun(ref start, ref length)) return false;
        BuildIsland(0, start, length, false);
        return true;
    }

    private bool PlaceIslandSouth()
    {
        int lastY = _mapHeight - 1;
        var (start, length) = FindRun(lastY, false, 0, -1);
        if (!VerifyLongRun(ref start, ref length)) return false;
        BuildIsland(lastY, start, length, false);
        return true;
    }

    private bool PlaceIslandEast()
    {
        int lastX = _mapWidth - 1;
        var (start, length) = FindRun(lastX, true, -1, 0);
        if (!VerifyLongRun(ref start, ref length)) return false;
        BuildIsland(lastX, start, length, true);
        return true;
    }

    #endregion

    #region Puzzle Placement

    private void PlaceIntermediateWorldThing()
    {
        _placedSectors = 0;

        // Count sector types
        _travelCount = 0;
        _blockadeCount = 0;
        _puzzleCount = 0;

        int size = _mapWidth * _mapHeight;
        for (int i = 0; i < size; i++)
        {
            switch (_typeMap[i])
            {
                case SectorType.Empty:
                case SectorType.Island:
                case SectorType.Candidate:
                    _puzzleCount++;
                    break;
                case SectorType.BlockNorth:
                case SectorType.BlockEast:
                case SectorType.BlockSouth:
                case SectorType.BlockWest:
                    _blockadeCount++;
                    break;
                case SectorType.TravelStart:
                    _travelCount++;
                    break;
            }
        }

        // Scale puzzle count based on grid size (15x15 should have ~2.25x more puzzles)
        int basePuzzleCount = (_puzzleCount / 4) +
            (_random.Next() % ((_puzzleCount / 5) + 1)) -
            _blockadeCount - _travelCount - 2;

        int minPuzzles = _mapWidth == 15 ? 10 : 4;
        int totalPuzzleCount = Math.Max(minPuzzles, basePuzzleCount);

        _placedSectors = 0;

        ChoosePuzzlesBehindBlockades();
        ChoosePuzzlesOnIslands();
        ChooseAdditionalPuzzles(totalPuzzleCount);
        MakeSureLastPuzzleIsNotTooCloseToCenter();
    }

    private void ChoosePuzzlesBehindBlockades()
    {
        for (int y = 0; y < _mapHeight; y++)
        {
            for (int x = 0; x < _mapWidth; x++)
            {
                int smallStepX = 0, smallStepY = 0;
                int largeStepX = 0, largeStepY = 0;

                switch (GetType(x, y))
                {
                    case SectorType.BlockWest:
                        smallStepX = -1; largeStepX = -2;
                        break;
                    case SectorType.BlockEast:
                        smallStepX = 1; largeStepX = 2;
                        break;
                    case SectorType.BlockNorth:
                        smallStepY = -1; largeStepY = -2;
                        break;
                    case SectorType.BlockSouth:
                        smallStepY = 1; largeStepY = 2;
                        break;
                    default:
                        continue;
                }

                int smallX = x + smallStepX;
                int smallY = y + smallStepY;
                int largeX = x + largeStepX;
                int largeY = y + largeStepY;

                if (GetType(smallX, smallY) != SectorType.Candidate) continue;

                int puzzleX, puzzleY;
                if (largeX < 0 || largeX >= _mapWidth || largeY < 0 || largeY >= _mapHeight ||
                    GetType(largeX, largeY) != SectorType.Candidate)
                {
                    puzzleX = smallX;
                    puzzleY = smallY;
                }
                else
                {
                    puzzleX = largeX;
                    puzzleY = largeY;
                }

                SetType(puzzleX, puzzleY, SectorType.Puzzle);
                SetOrder(puzzleX, puzzleY, _placedSectors);
                _placedSectors++;
            }
        }
    }

    private void ChoosePuzzlesOnIslands()
    {
        for (int y = 0; y < _mapHeight; y++)
        {
            for (int x = 0; x < _mapWidth; x++)
            {
                if (GetType(x, y) != SectorType.TravelEnd) continue;

                // Find island orientation and traverse to end
                int stepX = 0, stepY = 0;
                if (GetType(x - 1, y) == SectorType.Island) { stepX = -1; }
                else if (GetType(x + 1, y) == SectorType.Island) { stepX = 1; }
                else if (GetType(x, y - 1) == SectorType.Island) { stepY = -1; }
                else if (GetType(x, y + 1) == SectorType.Island) { stepY = 1; }

                // Walk to the end of the island
                int puzzleX = x, puzzleY = y;
                while (true)
                {
                    int nextX = puzzleX + stepX;
                    int nextY = puzzleY + stepY;
                    if (nextX < 0 || nextX >= _mapWidth || nextY < 0 || nextY >= _mapHeight) break;
                    if (GetType(nextX, nextY) != SectorType.Island) break;
                    puzzleX = nextX;
                    puzzleY = nextY;
                }

                SetType(puzzleX, puzzleY, SectorType.Puzzle);
                SetOrder(puzzleX, puzzleY, _placedSectors);
                _placedSectors++;
            }
        }
    }

    private void ChooseAdditionalPuzzles(int totalPuzzleCount)
    {
        int maxCount = _mapWidth == 15 ? 10000 : 5000;
        int lastX = _mapWidth - 1;
        int lastY = _mapHeight - 1;
        int maxIterations = _mapWidth == 15 ? 450 : 200;

        for (int i = 0; i <= maxIterations; i++)
        {
            maxCount--;
            if (maxCount == 0) break;
            if (_placedSectors >= totalPuzzleCount) break;

            int x = _random.Next() % _mapWidth;
            int y;

            if (i >= 50 || x == 0 || x == lastX)
            {
                y = _random.Next() % _mapHeight;
            }
            else
            {
                y = (_random.Next() & 1) < 1 ? lastY : 0;
            }

            if (_placedSectors >= totalPuzzleCount) break;

            int distance = GetDistanceToCenter(x, y);
            int minDistance = _mapWidth == 15 ? 4 : 3;
            int lateIterThreshold = _mapWidth == 15 ? 225 : 150;

            if (distance >= minDistance || i >= lateIterThreshold)
            {
                var item = GetType(x, y);
                if ((item == SectorType.Empty || item == SectorType.Candidate) &&
                    (x == 0 || GetType(x - 1, y) != SectorType.Puzzle) &&
                    (x == lastX || GetType(x + 1, y) != SectorType.Puzzle) &&
                    (y == 0 || GetType(x, y - 1) != SectorType.Puzzle) &&
                    (y == lastY || GetType(x, y + 1) != SectorType.Puzzle))
                {
                    SetType(x, y, SectorType.Puzzle);
                    SetOrder(x, y, _placedSectors);
                    _placedSectors++;
                }
            }

            if (distance < minDistance && i < lateIterThreshold) i--;
        }
    }

    private void MakeSureLastPuzzleIsNotTooCloseToCenter()
    {
        // Find position of last puzzle
        int lastX = -1, lastY = -1;
        for (int y = 0; y < _mapHeight; y++)
        {
            for (int x = 0; x < _mapWidth; x++)
            {
                if (GetOrder(x, y) == _placedSectors - 1)
                {
                    lastX = x;
                    lastY = y;
                    break;
                }
            }
            if (lastX >= 0) break;
        }

        if (lastX < 0) return;

        int minDistance = _mapWidth == 15 ? 4 : 3;

        // If last puzzle is too close to center, swap with one further out
        if (GetDistanceToCenter(lastX, lastY) < minDistance)
        {
            for (int y = 0; y < _mapHeight; y++)
            {
                for (int x = 0; x < _mapWidth; x++)
                {
                    if (GetOrder(x, y) >= 0 &&
                        GetDistanceToCenter(x, y) >= minDistance &&
                        (x != lastX || y != lastY))
                    {
                        int tempOrder = GetOrder(x, y);
                        SetOrder(x, y, _placedSectors - 1);
                        SetOrder(lastX, lastY, tempOrder);
                        return;
                    }
                }
            }
        }
    }

    #endregion

    /// <summary>
    /// Prints the type map for debugging.
    /// </summary>
    public void PrintTypeMap()
    {
        Console.WriteLine($"\n  Type Map ({_mapWidth}x{_mapHeight}):");

        // Print header row
        Console.Write("   ");
        for (int x = 0; x < _mapWidth; x++)
            Console.Write($" {x,2} ");
        Console.WriteLine();

        for (int y = 0; y < _mapHeight; y++)
        {
            Console.Write($"{y,2} ");
            for (int x = 0; x < _mapWidth; x++)
            {
                var type = GetType(x, y);
                string ch = type switch
                {
                    SectorType.None => " · ",
                    SectorType.Empty => " E ",
                    SectorType.Candidate => " C ",
                    SectorType.Puzzle => $"P{GetOrder(x, y):D2}",
                    SectorType.Spaceport => " S ",
                    SectorType.BlockNorth => "B↑ ",
                    SectorType.BlockSouth => "B↓ ",
                    SectorType.BlockEast => "B→ ",
                    SectorType.BlockWest => "B← ",
                    SectorType.TravelStart => "T→ ",
                    SectorType.TravelEnd => "←T ",
                    SectorType.Island => " I ",
                    SectorType.KeptFree => " # ",
                    _ => " ? "
                };
                Console.Write(ch + " ");
            }
            Console.WriteLine();
        }
        Console.WriteLine();
    }
}
