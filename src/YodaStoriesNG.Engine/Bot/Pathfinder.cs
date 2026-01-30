using YodaStoriesNG.Engine.Data;
using YodaStoriesNG.Engine.Game;

namespace YodaStoriesNG.Engine.Bot;

/// <summary>
/// A* pathfinding for zone navigation.
/// </summary>
public class Pathfinder
{
    private readonly GameData _gameData;

    // Temporary blocklist for positions that movement failed on (cleared on zone change)
    private readonly HashSet<(int zoneId, int x, int y)> _blockedPositions = new();
    // Permanent blocklist for positions like X-Wings (NOT cleared on zone change)
    private readonly HashSet<(int zoneId, int x, int y)> _permanentBlockedPositions = new();
    private int _currentZoneId = -1;

    public Pathfinder(GameData gameData)
    {
        _gameData = gameData;
    }

    /// <summary>
    /// Marks a position as blocked (movement failed there). Cleared on zone change.
    /// </summary>
    public void MarkBlocked(int zoneId, int x, int y)
    {
        _blockedPositions.Add((zoneId, x, y));
    }

    /// <summary>
    /// Marks a position as permanently blocked (like X-Wing tiles). NOT cleared on zone change.
    /// </summary>
    public void MarkPermanentlyBlocked(int zoneId, int x, int y)
    {
        _permanentBlockedPositions.Add((zoneId, x, y));
    }

    /// <summary>
    /// Clears temporary blocked positions (call when zone changes).
    /// </summary>
    public void ClearBlockedPositions(int newZoneId)
    {
        if (newZoneId != _currentZoneId)
        {
            _blockedPositions.Clear();
            _currentZoneId = newZoneId;
        }
    }

    /// <summary>
    /// Checks if a position is in the blocklist (temporary OR permanent).
    /// </summary>
    public bool IsTemporarilyBlocked(int zoneId, int x, int y)
    {
        return _blockedPositions.Contains((zoneId, x, y)) ||
               _permanentBlockedPositions.Contains((zoneId, x, y));
    }

    /// <summary>
    /// Finds a path from start to end position within a zone using A* algorithm.
    /// </summary>
    /// <returns>List of positions to follow, or null if no path exists.</returns>
    public List<(int X, int Y)>? FindPath(Zone zone, int startX, int startY, int endX, int endY, List<NPC>? npcs = null, int? zoneId = null)
    {
        // Clear blocklist if zone changed
        if (zoneId.HasValue)
            ClearBlockedPositions(zoneId.Value);

        return FindPathInternal(zone, startX, startY, endX, endY, npcs, zoneId);
    }

    /// <summary>
    /// Finds a path without clearing the blocklist. Use when recalculating after a blocked move.
    /// </summary>
    public List<(int X, int Y)>? FindPathWithoutClearing(Zone zone, int startX, int startY, int endX, int endY, List<NPC>? npcs = null, int? zoneId = null)
    {
        return FindPathInternal(zone, startX, startY, endX, endY, npcs, zoneId);
    }

    private List<(int X, int Y)>? FindPathInternal(Zone zone, int startX, int startY, int endX, int endY, List<NPC>? npcs, int? zoneId)
    {

        // If already at destination
        if (startX == endX && startY == endY)
            return new List<(int X, int Y)>();

        // If target is not walkable, find nearest walkable position
        if (!IsWalkable(zone, endX, endY, npcs, zoneId))
        {
            var nearestWalkable = FindNearestWalkable(zone, endX, endY, npcs, zoneId);
            if (nearestWalkable == null)
                return null;
            (endX, endY) = nearestWalkable.Value;
        }

        var openSet = new PriorityQueue<PathNode, int>();
        var closedSet = new HashSet<(int, int)>();
        var cameFrom = new Dictionary<(int, int), (int, int)>();
        var gScore = new Dictionary<(int, int), int>();
        var fScore = new Dictionary<(int, int), int>();

        var startNode = new PathNode(startX, startY);
        gScore[(startX, startY)] = 0;
        fScore[(startX, startY)] = Heuristic(startX, startY, endX, endY);
        openSet.Enqueue(startNode, fScore[(startX, startY)]);

        int maxIterations = 1000; // Prevent infinite loops
        int iterations = 0;

        while (openSet.Count > 0 && iterations++ < maxIterations)
        {
            var current = openSet.Dequeue();
            var currentPos = (current.X, current.Y);

            if (current.X == endX && current.Y == endY)
            {
                // Reconstruct path
                return ReconstructPath(cameFrom, (endX, endY));
            }

            if (closedSet.Contains(currentPos))
                continue;

            closedSet.Add(currentPos);

            // Check all neighbors (4-directional)
            var neighbors = new (int dx, int dy)[] { (0, -1), (0, 1), (-1, 0), (1, 0) };
            foreach (var (dx, dy) in neighbors)
            {
                int nx = current.X + dx;
                int ny = current.Y + dy;
                var neighborPos = (nx, ny);

                if (closedSet.Contains(neighborPos))
                    continue;

                if (!IsWalkable(zone, nx, ny, npcs, zoneId))
                    continue;

                int tentativeGScore = gScore[currentPos] + 1;

                if (!gScore.ContainsKey(neighborPos) || tentativeGScore < gScore[neighborPos])
                {
                    cameFrom[neighborPos] = currentPos;
                    gScore[neighborPos] = tentativeGScore;
                    fScore[neighborPos] = tentativeGScore + Heuristic(nx, ny, endX, endY);
                    openSet.Enqueue(new PathNode(nx, ny), fScore[neighborPos]);
                }
            }
        }

        return null; // No path found
    }

    /// <summary>
    /// Checks if a position is walkable (no blocking tiles or NPCs).
    /// </summary>
    public bool IsWalkable(Zone zone, int x, int y, List<NPC>? npcs = null, int? zoneId = null)
    {
        // Bounds check
        if (x < 0 || x >= zone.Width || y < 0 || y >= zone.Height)
            return false;

        // Check temporary blocklist
        if (zoneId.HasValue && IsTemporarilyBlocked(zoneId.Value, x, y))
            return false;

        // Check middle layer for blocking tiles
        // This must match TryMovePlayer's collision logic in GameEngine.cs
        var middleTile = zone.GetTile(x, y, 1);
        if (middleTile != 0xFFFF && middleTile < _gameData.Tiles.Count)
        {
            var tile = _gameData.Tiles[middleTile];
            // Block if it's an object (matching game engine's IsObject check)
            // Draggable objects can be pushed, so they're technically passable for pathfinding
            if (tile.IsObject && !tile.IsDraggable)
                return false;
        }

        // Check for NPCs (excluding dead ones)
        if (npcs != null)
        {
            foreach (var npc in npcs)
            {
                if (npc.IsEnabled && npc.IsAlive && npc.X == x && npc.Y == y)
                    return false;
            }
        }

        // Note: Top layer (roofs) are non-colliding per TileFlags definition
        // The game engine only checks middle layer for collision

        return true;
    }

    /// <summary>
    /// Finds the nearest walkable position to the target.
    /// </summary>
    public (int X, int Y)? FindNearestWalkable(Zone zone, int targetX, int targetY, List<NPC>? npcs = null, int? zoneId = null)
    {
        if (IsWalkable(zone, targetX, targetY, npcs, zoneId))
            return (targetX, targetY);

        // Search in expanding squares
        for (int radius = 1; radius <= 10; radius++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                for (int dy = -radius; dy <= radius; dy++)
                {
                    if (Math.Abs(dx) != radius && Math.Abs(dy) != radius)
                        continue; // Only perimeter

                    int x = targetX + dx;
                    int y = targetY + dy;

                    if (IsWalkable(zone, x, y, npcs, zoneId))
                        return (x, y);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the direction to move from current position to next position.
    /// </summary>
    public Direction? GetDirection(int fromX, int fromY, int toX, int toY)
    {
        int dx = toX - fromX;
        int dy = toY - fromY;

        if (dx > 0) return Direction.Right;
        if (dx < 0) return Direction.Left;
        if (dy > 0) return Direction.Down;
        if (dy < 0) return Direction.Up;

        return null;
    }

    /// <summary>
    /// Finds path to adjacent position next to target (for interaction).
    /// </summary>
    public (List<(int X, int Y)>? Path, (int X, int Y) FinalPos)? FindPathToAdjacent(
        Zone zone, int startX, int startY, int targetX, int targetY, List<NPC>? npcs = null, int? zoneId = null)
    {
        // Try all adjacent positions to the target
        var adjacent = new (int dx, int dy)[] { (0, -1), (0, 1), (-1, 0), (1, 0) };
        List<(int X, int Y)>? bestPath = null;
        (int X, int Y) bestFinalPos = (0, 0);

        foreach (var (dx, dy) in adjacent)
        {
            int adjX = targetX + dx;
            int adjY = targetY + dy;

            if (!IsWalkable(zone, adjX, adjY, npcs, zoneId))
                continue;

            var path = FindPath(zone, startX, startY, adjX, adjY, npcs, zoneId);
            if (path != null && (bestPath == null || path.Count < bestPath.Count))
            {
                bestPath = path;
                bestFinalPos = (adjX, adjY);
            }
        }

        if (bestPath != null)
            return (bestPath, bestFinalPos);

        return null;
    }

    private static int Heuristic(int x1, int y1, int x2, int y2)
    {
        return Math.Abs(x1 - x2) + Math.Abs(y1 - y2); // Manhattan distance
    }

    private static List<(int X, int Y)> ReconstructPath(Dictionary<(int, int), (int, int)> cameFrom, (int, int) current)
    {
        var path = new List<(int X, int Y)> { current };
        while (cameFrom.ContainsKey(current))
        {
            current = cameFrom[current];
            path.Add(current);
        }
        path.Reverse();
        // Remove starting position from path
        if (path.Count > 0)
            path.RemoveAt(0);
        return path;
    }

    private class PathNode
    {
        public int X { get; }
        public int Y { get; }

        public PathNode(int x, int y)
        {
            X = x;
            Y = y;
        }
    }
}
