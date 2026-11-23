using YodaStoriesNG.Engine.Data;
using YodaStoriesNG.Engine.Game;

namespace YodaStoriesNG.Engine.Bot;

/// <summary>
/// A* pathfinding for zone navigation.
/// </summary>
public class Pathfinder
{
    private readonly GameData _gameData;
    private readonly GameState? _state;
    public Func<Zone, int, int, bool>? AdditionalWalkability { get; set; }
    public (int Zone, int X, int Y)? TransitionTarget { get; set; }
    public bool AllowDiagonal { get; set; }
    public bool PreferCardinal { get; set; }
    public bool AvoidFloorSwitches { get; set; }

    public IEnumerable<(int X, int Y)> WalkingNeighbors(Zone zone, int x, int y,
        List<NPC>? npcs = null, int? zoneId = null)
    {
        foreach (var (dx, dy) in new[] { (0,-1),(0,1),(-1,0),(1,0),(-1,-1),(1,-1),(-1,1),(1,1) })
        {
            int nx = x + dx, ny = y + dy;
            if (!IsWalkable(zone, nx, ny, npcs, zoneId)) continue;
            if (dx != 0 && dy != 0)
            {
                if (!AllowDiagonal || !DiagonalSideClear(zone, x + dx, y, npcs) ||
                    !DiagonalSideClear(zone, x, y + dy, npcs)) continue;
                // Boarding and scripted bump interactions remain cardinal.
                // Open doorways can have a diagonal-only authored approach.
                if (zone.Objects.Any(h => h.Enabled && h.X == nx && h.Y == ny && h.Type is
                    ZoneObjectType.Teleporter or
                    ZoneObjectType.VehicleToPrimary or ZoneObjectType.VehicleToSecondary or
                    ZoneObjectType.XWingFromDagobah or ZoneObjectType.XWingToDagobah) ||
                    zone.Actions.Any(a => a.Conditions.Any(c =>
                        c.Arguments.Count >= 3 && c.Arguments[0] == nx && c.Arguments[1] == ny &&
                        (c.Opcode == ConditionOpcode.Bump && zone.GetTile(nx,ny,1) == unchecked((ushort)c.Arguments[2]) ||
                         c.Opcode == ConditionOpcode.PlacedItemIs && c.Arguments.Count >= 4 && c.Arguments[2] is >= 0 and <= 2 &&
                         zone.GetTile(nx,ny,c.Arguments[2]) == unchecked((ushort)c.Arguments[3]))))) continue;
            }
            yield return (nx, ny);
        }
    }

    private bool DiagonalSideClear(Zone zone, int x, int y, List<NPC>? npcs)
    {
        if (x < 0 || y < 0 || x >= zone.Width || y >= zone.Height ||
            npcs?.Any(n => n.IsEnabled && n.IsAlive && n.X == x && n.Y == y) == true) return false;
        int tile = zone.GetTile(x,y,1);
        if (TileCollision.IsGroundBlocked(_gameData,zone,x,y) ||
            _state?.SmoothMovementEnabled==true && tile<_gameData.Tiles.Count && _gameData.Tiles[tile].IsObject) return false;
        // Side cells are not entered. Static NPC art follows the same corner
        // rule as wall art, while IsWalkable still validates the destination.
        return tile == ushort.MaxValue || tile < _gameData.Tiles.Count;
    }

    public bool IsUnrelatedTransition(Zone zone, int x, int y)
    {
        bool intended = TransitionTarget is { } goal && goal.Zone == zone.Id && goal.X == x && goal.Y == y;
        if (intended) return false;
        if (AvoidFloorSwitches && zone.Actions.Any(a=>a.Conditions.Any(c=>
            c.Opcode==ConditionOpcode.StandingOn && c.Arguments.Count>=3 && c.Arguments[0]==x && c.Arguments[1]==y &&
            zone.GetTile(x,y,0)==unchecked((ushort)c.Arguments[2])) && CouldTrigger(zone,a) &&
            HasRouteDisruptingEffect(zone,a))) return true;
        if (zone.Objects.Any(h => h.Enabled && h.X == x && h.Y == y && h.Type is
            ZoneObjectType.DoorEntrance or ZoneObjectType.DoorExit or ZoneObjectType.Teleporter or
            ZoneObjectType.VehicleToPrimary or ZoneObjectType.VehicleToSecondary or
            ZoneObjectType.XWingFromDagobah or ZoneObjectType.XWingToDagobah)) return true;
        // Cockpit scripts may board from a different cell than the transport hotspot.
        return zone.Actions.Any(a => a.Conditions.Any(c =>
            c.Opcode is ConditionOpcode.Bump or ConditionOpcode.StandingOn or ConditionOpcode.HeroIsAt && c.Arguments.Count >= 2 &&
            c.Arguments[0] == x && c.Arguments[1] == y &&
            (c.Opcode==ConditionOpcode.HeroIsAt && !a.Conditions.Any(g=>g.Opcode is
                ConditionOpcode.Bump or ConditionOpcode.PlacedItemIs or ConditionOpcode.PlacedItemIsNot) || c.Arguments.Count>=3 &&
                c.Opcode!=ConditionOpcode.HeroIsAt &&
             zone.GetTile(x,y,c.Opcode==ConditionOpcode.Bump ? 1 : 0)==unchecked((ushort)c.Arguments[2]))) &&
            a.Instructions.Any(i => i.Opcode is InstructionOpcode.ChangeZone or InstructionOpcode.HideHero) && CouldTrigger(zone,a));
    }

    private bool HasRouteDisruptingEffect(Zone zone,Data.Action action)
    {
        if(action.Instructions.Any(i=>i.Opcode is InstructionOpcode.PlaceTile or InstructionOpcode.RemoveTile or
            InstructionOpcode.MoveTile or InstructionOpcode.ChangeZone or InstructionOpcode.HideHero or
            InstructionOpcode.MoveHeroTo or InstructionOpcode.MoveHeroBy)) return true;
        // A floor plate can first set a counter and let a later Walk action
        // perform the relocation. Follow that authored chain so the route
        // avoids the plate even when the teleport is several script actions away.
        if(_state==null)
            return action.Instructions.Any(i=>i.Opcode is InstructionOpcode.SetCounter or InstructionOpcode.SetRandom or
                       InstructionOpcode.AddToCounter or InstructionOpcode.RollDice) &&
                zone.Actions.Any(a=>a.Instructions.Any(i=>i.Opcode is InstructionOpcode.HideHero or
                    InstructionOpcode.MoveHeroTo or InstructionOpcode.MoveHeroBy));
        var movement=ScriptedPassage.ArrivalAction(zone,action,_state,_gameData.GameType);
        return movement!=null && ScriptedPassage.Arrival(movement,_gameData.GameType)!=null;
    }

    // Reject only guards whose current value is known. Unknown guards remain
    // conservative; an inactive random-layout branch must not wall off a floor.
    private bool CouldTrigger(Zone zone,Data.Action action)
    {
        if(_state==null) return true;
        if(_state.DisabledActions.Contains((zone.Id,zone.Actions.IndexOf(action)))) return false;
        // A room's initializer can still choose its counters, random value and
        // layout. Their default values do not prove a future trigger inactive.
        if(zone.Id!=_state.CurrentZoneId && !_state.VisitedZones.Contains(zone.Id)) return true;
        foreach(var condition in action.Conditions)
        {
            var a=condition.Arguments;
            if(a.Count==0) continue;
            int random=_state.ZoneRandom.GetValueOrDefault(zone.Id),counter=_state.GetCounter(zone.Id);
            bool possible=condition.Opcode switch {
                ConditionOpcode.RandomIs=>random==a[0],
                ConditionOpcode.RandomIsNot=>random!=a[0],
                ConditionOpcode.RandomIsGreaterThan=>random>a[0],
                ConditionOpcode.RandomIsLessThan=>random<a[0],
                ConditionOpcode.CounterIs=>counter==a[0],
                ConditionOpcode.CounterIsNot=>counter!=a[0],
                ConditionOpcode.SectorCounterIs=>_state.GetSectorCounter(zone.Id)==a[0],
                ConditionOpcode.SectorCounterIsNot=>_state.GetSectorCounter(zone.Id)!=a[0],
                ConditionOpcode.SectorCounterIsGreaterThan=>_state.GetSectorCounter(zone.Id)>a[0],
                ConditionOpcode.SectorCounterIsLessThan=>_state.GetSectorCounter(zone.Id)<a[0],
                ConditionOpcode.TileAtIs or ConditionOpcode.IsVariable when a.Count>=4=>
                    zone.GetTile(a[1],a[2],a[3])==unchecked((ushort)a[0]),
                ConditionOpcode.ZoneNotInitialized=>!_state.VisitedZones.Contains(zone.Id),
                _=>true };
            if(!possible) return false;
        }
        return true;
    }

    // Temporary blocklist for positions that movement failed on (cleared on zone change)
    private readonly HashSet<(int zoneId, int x, int y)> _blockedPositions = new();
    // Permanent blocklist for positions like X-Wings (NOT cleared on zone change)
    private readonly HashSet<(int zoneId, int x, int y)> _permanentBlockedPositions = new();
    private int _currentZoneId = -1;

    public Pathfinder(GameData gameData,GameState? state=null)
    {
        _gameData = gameData;
        _state = state;
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
    /// Forgets the temporary blocks - most are an enemy that was standing there and has moved
    /// on. Returns whether there were any.
    /// </summary>
    public bool ForgetTemporaryBlocks()
    {
        bool any = _blockedPositions.Count > 0;
        _blockedPositions.Clear();
        return any;
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

        // Exact movement must reach its requested tile. Interaction callers use
        // FindPathToAdjacent explicitly; silently substituting another square
        // made the planner prove pickups that the executor could never perform.
        if (!IsWalkable(zone, endX, endY, npcs, zoneId))
            return null;

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

            foreach (var (nx, ny) in WalkingNeighbors(zone, current.X, current.Y, npcs, zoneId))
            {
                int dx = nx - current.X, dy = ny - current.Y;
                var neighborPos = (nx, ny);

                if (closedSet.Contains(neighborPos))
                    continue;

                if (!IsWalkable(zone, nx, ny, npcs, zoneId))
                    continue;

                // A block only gives way if walking into it pushes it somewhere free, which
                // depends on the direction. (The engine refuses otherwise, and the bot used to
                // plan straight through push-block mazes and get stuck in them.)
                if (IsDraggableAt(zone, nx, ny) && !IsFreeFloor(zone, nx + dx, ny + dy))
                    continue;

                int tentativeGScore = gScore[currentPos] + (dx != 0 && dy != 0 ? (PreferCardinal ? 30 : 14) : 10);

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

        if (IsUnrelatedTransition(zone, x, y)) return false;

        // Check temporary blocklist
        if (zoneId.HasValue && IsTemporarilyBlocked(zoneId.Value, x, y))
            return false;

        if (TileCollision.IsGroundBlocked(_gameData, zone, x, y) ||
            AdditionalWalkability?.Invoke(zone, x, y) == false) return false;

        // Check middle layer for blocking tiles
        // This must match TryMovePlayer's collision logic in GameEngine.cs
        var middleTile = zone.GetTile(x, y, 1);
        if (middleTile != 0xFFFF && middleTile < _gameData.Tiles.Count)
        {
            var tile = _gameData.Tiles[middleTile];
            // Pushing/pulling is a separate action; a walking route cannot cross a block.
            if (tile.IsDraggable || tile.IsObject)
                return false;
            // A character picture with an authored bump/use script is an actor
            // hotspot in the engine too. The bot must route around it unless it
            // is the explicit transition destination; otherwise its first move
            // can be rejected by TryMovePlayer even though A* included the cell.
            bool intendedTransition = TransitionTarget is { } target &&
                target.Zone == zone.Id && target.X == x && target.Y == y;
            if (!intendedTransition && tile.IsCharacter &&
                (zone.Objects.Any(obj=>obj.Enabled && obj.X==x && obj.Y==y &&
                    obj.Type is ZoneObjectType.SpawnLocation or ZoneObjectType.PuzzleNPC) ||
                 ScriptedActorTiles.IsTradeTarget(zone,x,y,middleTile) ||
                 zone.Actions.Select((action,index)=>(action,index)).Any(pair=>
                    (_state == null || !_state.DisabledActions.Contains((zone.Id,pair.index))) &&
                    pair.action.Conditions.Any(c=>c.Opcode==ConditionOpcode.Bump && c.Arguments.Count>=3 &&
                        c.Arguments[0]==x && c.Arguments[1]==y && c.Arguments[2]==middleTile))))
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

        // Roofs occlude actors but do not block their ground path.

        return true;
    }

    private bool IsDraggableAt(Zone zone, int x, int y)
    {
        var id = zone.GetTile(x, y, 1);
        return id != 0xFFFF && id < _gameData.Tiles.Count && _gameData.Tiles[id].IsDraggable;
    }

    /// <summary>Somewhere a pushed block can land: inside the zone with nothing solid on it.</summary>
    private bool IsFreeFloor(Zone zone, int x, int y)
    {
        if (x < 0 || x >= zone.Width || y < 0 || y >= zone.Height) return false;
        var id = zone.GetTile(x, y, 1);
        return id == 0xFFFF || id < _gameData.Tiles.Count && !_gameData.Tiles[id].IsObject;
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
    public static (int X,int Y)[] InteractionCells(int x,int y,bool diagonal=false,bool extendedCardinal=false) => extendedCardinal
        ? InteractionCells(x,y,diagonal).Concat(new[] { (x,y-2),(x,y+2),(x-2,y),(x+2,y) }).ToArray()
        : diagonal
        ? new[] { (x,y-1),(x,y+1),(x-1,y),(x+1,y),(x-1,y-1),(x+1,y-1),(x-1,y+1),(x+1,y+1) }
        : new[] { (x,y-1),(x,y+1),(x-1,y),(x+1,y) };

    public (List<(int X, int Y)>? Path, (int X, int Y) FinalPos)? FindPathToAdjacent(
        Zone zone, int startX, int startY, int targetX, int targetY, List<NPC>? npcs = null, int? zoneId = null, bool allowDiagonalInteraction = false, bool extendedCardinalInteraction = false)
    {
        // Try all adjacent positions to the target
        var adjacent = InteractionCells(0,0,allowDiagonalInteraction,extendedCardinalInteraction);
        // An authored landing may leave the actor on a doorway/return trigger.
        // It can interact from there without first proving it could walk back
        // onto that intentionally avoided transition tile.
        if(adjacent.Contains((startX-targetX,startY-targetY)))
            return (new List<(int X,int Y)>(),(startX,startY));
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
        int dx = Math.Abs(x1 - x2), dy = Math.Abs(y1 - y2);
        return 10 * Math.Max(dx, dy) + 4 * Math.Min(dx, dy);
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
