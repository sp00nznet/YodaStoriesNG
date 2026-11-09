using YodaStoriesNG.Engine.Data;
using YodaStoriesNG.Engine.Game;

namespace YodaStoriesNG.Engine.Bot;

/// <summary>
/// Bot action execution helpers. Handles low-level actions like moving, attacking, and interacting.
/// </summary>
public class BotActions
{
    private readonly GameState _state;
    private readonly GameData _gameData;
    private readonly Pathfinder _pathfinder;

    // Current path being followed
    private List<(int X, int Y)>? _currentPath;
    private int _currentPathIndex;

    // Movement timing
    public Action<string,object?>? Trace { get; set; }
    public event Action<NPC>? OnRouteBlockedByEnemy;
    public Func<bool>? MovementPending { get; set; }

    private double _moveTimer;
    private const double MoveCooldown = 0.1; // 100ms between moves

    // Action state
    private BotActionState _actionState = BotActionState.Idle;
    private int _targetX, _targetY;
    private int _walkTargetX, _walkTargetY;
    private NPC? _targetNpc;
    private int? _itemToUse;
    private (int Zone,int X,int Y,int NextX,int NextY)? _pickupNudge;
    private bool _bumpTarget;
    private NPC? _waitingForFriendly;
    private double _friendlyWaitTime;
    private bool _yieldedForFriendly;
    public bool IsWaitingForFriendly => _waitingForFriendly!=null;
    private (int X,int Y)? _friendlyRefuge;
    private Direction? _pushDirection;

    public BotActions(GameState state, GameData gameData, Pathfinder pathfinder)
    {
        _state = state;
        _gameData = gameData;
        _pathfinder = pathfinder;
    }

    /// <summary>
    /// Current action state.
    /// </summary>
    public BotActionState State => _actionState;

    /// <summary>
    /// Whether the bot is currently performing an action.
    /// </summary>
    public bool IsBusy => MovementPending?.Invoke() == true || _actionState != BotActionState.Idle && _actionState != BotActionState.Completed;

    /// <summary>
    /// Whether the current action has completed.
    /// </summary>
    public bool IsCompleted => _actionState == BotActionState.Completed && MovementPending?.Invoke() != true;

    /// <summary>
    /// Gets description of current action.
    /// </summary>
    public string CurrentActionDescription { get; private set; } = "";

    /// <summary>
    /// Event for when bot wants to perform an action.
    /// </summary>
    public event Action<BotActionType, int, int, Direction>? OnAction;

    /// <summary>
    /// Starts moving to a target position.
    /// </summary>
    public bool MoveTo(int x, int y, bool useTransition = false)
    {
        if (_state.CurrentZone == null)
            return false;

        _pathfinder.TransitionTarget = useTransition ? (_state.CurrentZoneId, x, y) : null;
        // Already there?
        if (_state.PlayerX == x && _state.PlayerY == y)
        {
            _actionState = BotActionState.Completed;
            return true;
        }

        // Find path (pass zoneId for blocklist checking)
        _currentPath = _pathfinder.FindPath(
            _state.CurrentZone,
            _state.PlayerX, _state.PlayerY,
            x, y,
            _state.ZoneNPCs,
            _state.CurrentZoneId);

        // Blocks are mostly monsters that were standing in the way; once they have moved on
        // they would wall the bot in for as long as it stays in this zone. Try again without.
        if ((_currentPath == null || _currentPath.Count == 0) && _pathfinder.ForgetTemporaryBlocks())
            _currentPath = _pathfinder.FindPath(_state.CurrentZone, _state.PlayerX, _state.PlayerY, x, y,
                _state.ZoneNPCs, _state.CurrentZoneId);

        // If enemies seal every walking route, plan up to one rather than give up.
        // Execution stops and requests combat before stepping onto the hostile cell.
        if (_currentPath == null)
            _currentPath = _pathfinder.FindPath(_state.CurrentZone, _state.PlayerX, _state.PlayerY,
                x, y, _state.ZoneNPCs.Where(n => !n.IsHostile &&
                    n.Behavior is NPCBehavior.Stationary or NPCBehavior.Animation).ToList(), _state.CurrentZoneId);

        if (_currentPath == null || _currentPath.Count == 0)
        {
            Trace?.Invoke("path-failed",new { TargetX=x,TargetY=y,Mode="walk" });
            Console.WriteLine($"[BOT] No path to ({x},{y})");
            _actionState = BotActionState.Completed; // Give up
            return false;
        }

        _currentPathIndex = 0;
        _targetX = _walkTargetX = x;
        _targetY = _walkTargetY = y;
        _actionState = BotActionState.Moving;
        CurrentActionDescription = $"Moving to ({x},{y})";
        Trace?.Invoke("path-planned",new { TargetX=x,TargetY=y,Mode="walk",Waypoints=_currentPath.ToArray() });
        Console.WriteLine($"[BOT] Found path with {_currentPath.Count} steps to ({x},{y})");
        return true;
    }

    /// <summary>
    /// Starts moving to be adjacent to a target (for interaction).
    /// </summary>
    public bool MoveToAdjacent(int x, int y, bool useTransition = false, bool allowDiagonalInteraction = false, bool extendedCardinalInteraction = false)
    {
        if (_state.CurrentZone == null)
            return false;

        _pathfinder.TransitionTarget = useTransition ? (_state.CurrentZoneId, x, y) : null;
        // Already adjacent?
        if (Pathfinder.InteractionCells(x,y,allowDiagonalInteraction,extendedCardinalInteraction).Contains((_state.PlayerX,_state.PlayerY)))
        {
            _actionState = BotActionState.Completed;
            return true;
        }

        var result = _pathfinder.FindPathToAdjacent(
            _state.CurrentZone,
            _state.PlayerX, _state.PlayerY,
            x, y,
            _state.ZoneNPCs,
            _state.CurrentZoneId,allowDiagonalInteraction,extendedCardinalInteraction);
        if ((result == null || result.Value.Path == null) && _pathfinder.ForgetTemporaryBlocks())
            result = _pathfinder.FindPathToAdjacent(_state.CurrentZone, _state.PlayerX, _state.PlayerY, x, y,
                _state.ZoneNPCs, _state.CurrentZoneId,allowDiagonalInteraction,extendedCardinalInteraction);

        if (result == null || result.Value.Path == null)
            result = _pathfinder.FindPathToAdjacent(_state.CurrentZone, _state.PlayerX, _state.PlayerY,
                x, y, _state.ZoneNPCs.Where(n => !n.IsHostile &&
                    n.Behavior is NPCBehavior.Stationary or NPCBehavior.Animation).ToList(), _state.CurrentZoneId,allowDiagonalInteraction,extendedCardinalInteraction);

        if (result == null || result.Value.Path == null)
        {
            Trace?.Invoke("path-failed",new { TargetX=x,TargetY=y,Mode="adjacent",
                Neighbors=new[] { (x,y-1),(x,y+1),(x-1,y),(x+1,y) }.Select(p=>new {
                    X=p.Item1,Y=p.Item2,Ground=_state.CurrentZone.GetTile(p.Item1,p.Item2,0),
                    Middle=_state.CurrentZone.GetTile(p.Item1,p.Item2,1),
                    Safe=!_pathfinder.IsUnrelatedTransition(_state.CurrentZone,p.Item1,p.Item2),
                    Blocklisted=_pathfinder.IsTemporarilyBlocked(_state.CurrentZoneId,p.Item1,p.Item2),
                    Walkable=_pathfinder.IsWalkable(_state.CurrentZone,p.Item1,p.Item2,_state.ZoneNPCs,_state.CurrentZoneId) }).ToArray() });
            Console.WriteLine($"[BOT] No path to adjacent of ({x},{y}) from ({_state.PlayerX},{_state.PlayerY}); around it: " +
                string.Join(" ", new[] { (0, -1), (0, 1), (-1, 0), (1, 0) }.Select(o =>
                    $"({x + o.Item1},{y + o.Item2})={_state.CurrentZone.GetTile(x + o.Item1, y + o.Item2, 1)}" +
                    $"[ground={_state.CurrentZone.GetTile(x + o.Item1, y + o.Item2, 0)},actors={string.Join(',', _state.ZoneNPCs.Where(n => n.IsEnabled && n.IsAlive && n.X == x + o.Item1 && n.Y == y + o.Item2).Select(n => n.CharacterId))}]" +
                    (_pathfinder.IsWalkable(_state.CurrentZone, x + o.Item1, y + o.Item2, _state.ZoneNPCs, _state.CurrentZoneId) ? "" : "!"))));
            _actionState = BotActionState.Completed;
            return false;
        }

        Trace?.Invoke("path-planned",new { TargetX=x,TargetY=y,Mode="adjacent",
            Destination=result.Value.FinalPos,Waypoints=result.Value.Path.ToArray() });
        _currentPath = result.Value.Path;
        _currentPathIndex = 0;
        _targetX = _walkTargetX = result.Value.FinalPos.X;
        _targetY = _walkTargetY = result.Value.FinalPos.Y;
        _actionState = BotActionState.Moving;
        CurrentActionDescription = $"Moving to adjacent of ({x},{y})";
        return true;
    }

    /// <summary>
    /// Attacks in a direction.
    /// </summary>
    public void Attack(Direction dir)
    {
        _actionState = BotActionState.Attacking;
        CurrentActionDescription = $"Attacking {dir}";
        OnAction?.Invoke(BotActionType.Attack, 0, 0, dir);
        _actionState = BotActionState.Completed;
    }

    /// <summary>
    /// Starts the sequence to talk to an NPC.
    /// </summary>
    public bool TalkToNpc(NPC npc)
    {
        _bumpTarget = false;
        _targetNpc = npc;

        // Move adjacent first if needed
        if (!IsAdjacent(_state.PlayerX, _state.PlayerY, npc.X, npc.Y))
        {
            CurrentActionDescription = $"Walking to NPC at ({npc.X},{npc.Y})";
            bool success = MoveToAdjacent(npc.X, npc.Y);
            // Restore proper state AFTER MoveToAdjacent (which overwrites it)
            if (success && _actionState != BotActionState.Completed)
            {
                _actionState = BotActionState.MovingToNpc;
            }
            return success;
        }

        // Already adjacent, just interact
        _actionState = BotActionState.Interacting;
        PerformNpcInteraction();
        return true;
    }

    /// <summary>
    /// Uses an item (selects it and triggers use).
    /// </summary>
    public void UseItem(int itemId)
    {
        _itemToUse = itemId;
        _state.SelectedItem = itemId;
        _actionState = BotActionState.UsingItem;
        CurrentActionDescription = $"Using item {itemId}";
        OnAction?.Invoke(BotActionType.UseItem, itemId, 0, Direction.Down);
        _actionState = BotActionState.Completed;
    }

    /// <summary>
    /// Uses an item on an NPC (move to NPC, select item, interact).
    /// </summary>
    public bool UseItemOnNpc(int itemId, NPC npc)
    {
        _targetNpc = npc;
        _itemToUse = itemId;

        // Select the item
        _state.SelectedItem = itemId;

        // Move adjacent first if needed
        if (!ItemUseReach.Contains(_gameData,itemId,_state.PlayerX,_state.PlayerY,npc.X,npc.Y))
        {
            CurrentActionDescription = $"Walking to NPC with item {itemId}";
            bool success = MoveToAdjacent(npc.X, npc.Y,allowDiagonalInteraction:true,extendedCardinalInteraction:ItemUseReach.Extended(_gameData,itemId));
            // Restore proper state AFTER MoveToAdjacent (which overwrites it)
            if (success && _actionState != BotActionState.Completed)
            {
                _actionState = BotActionState.MovingToNpc;
            }
            return success;
        }

        // Already adjacent, use item
        _actionState = BotActionState.UsingItem;
        PerformNpcInteraction();
        return true;
    }

    /// <summary>
    /// Picks up an item at a position.
    /// </summary>
    public bool PickupItem(int x, int y)
    {
        if (_state.CurrentZone is { } zone && _pickupNudge is { } nudge &&
            nudge.Zone == zone.Id && nudge.X == x && nudge.Y == y)
        {
            // A hotspot can place an item under Luke on zone entry. Walking away
            // and back makes the normal tile-entry pickup run in that case.
            if (_state.PlayerX == nudge.NextX && _state.PlayerY == nudge.NextY)
                _pickupNudge = null;
            else
            {
                _actionState = BotActionState.MovingToItem;
                CurrentActionDescription = $"Stepping off item at ({x},{y})";
                return MoveTo(nudge.NextX, nudge.NextY, useTransition: true);
            }
        }

        // Need to walk on the item to pick it up
        if (_state.PlayerX != x || _state.PlayerY != y)
        {
            _actionState = BotActionState.MovingToItem;
            CurrentActionDescription = $"Walking to item at ({x},{y})";
            return MoveTo(x, y,useTransition:true);
        }

        // Already on the item - it normally auto-picks up when entering the tile.
        // If the hotspot was laid down after arrival, leave and re-enter once.
        if (_state.CurrentZone is { } current && current.GetTile(x, y, 1) is var tile &&
            tile < _gameData.Tiles.Count && _gameData.Tiles[tile].IsItem)
        {
            foreach (var (dx,dy) in new[] { (0,-1), (1,0), (0,1), (-1,0) })
            {
                int nx=x+dx,ny=y+dy;
                if (!_pathfinder.IsWalkable(current,nx,ny,_state.ZoneNPCs) ||
                    _pathfinder.IsUnrelatedTransition(current,nx,ny)) continue;
                _pickupNudge=(current.Id,x,y,nx,ny);
                _actionState=BotActionState.MovingToItem;
                CurrentActionDescription=$"Stepping off item at ({x},{y})";
                return MoveTo(nx,ny,useTransition:true);
            }
        }

        _actionState = BotActionState.Completed;
        return true;
    }

    /// <summary>
    /// Walks to a specific position (e.g., to collect R2D2 NPC after talking).
    /// </summary>
    public bool WalkToPosition(int x, int y, bool useTransition = false)
    {
        _pathfinder.TransitionTarget = useTransition ? (_state.CurrentZoneId, x, y) : null;
        if (_state.PlayerX != x || _state.PlayerY != y)
        {
            _actionState = BotActionState.MovingToItem;
            CurrentActionDescription = $"Walking to ({x},{y})";
            return MoveTo(x, y, useTransition);
        }

        // Already at the position
        _actionState = BotActionState.Completed;
        return true;
    }

    /// <summary>
    /// Pushes an object in a direction.
    /// </summary>
    public bool PushObject(int x, int y, Direction dir)
    {
        _pulling = false;
        _pushDirection = dir;
        _targetX = x;
        _targetY = y;

        // Calculate position to push from (opposite side of push direction)
        int pushFromX = x;
        int pushFromY = y;
        switch (dir)
        {
            case Direction.Up: pushFromY++; break;
            case Direction.Down: pushFromY--; break;
            case Direction.Left: pushFromX++; break;
            case Direction.Right: pushFromX--; break;
        }

        // Move to push position
        if (_state.PlayerX != pushFromX || _state.PlayerY != pushFromY)
        {
            return MoveIntoObjectPosition(pushFromX, pushFromY, x, y);
        }

        // Already in position, push
        _actionState = BotActionState.Pushing;
        PerformPush();
        return true;
    }

    /// <summary>
    /// Enters a door at a position.
    /// </summary>
    public bool EnterDoor(int x, int y)
    {
        int tile = _state.CurrentZone?.GetTile(x,y,1) ?? ushort.MaxValue;
        if (tile < _gameData.Tiles.Count && _gameData.Tiles[tile].IsObject)
        {
            // Many native doors must first be bumped to remove their closed-door tile.
            // After the script completes, the planner issues entry through the open cell.
            return BumpAt(x,y,useTransition: true);
        }
        // Walk to the door
        if (_state.PlayerX != x || _state.PlayerY != y)
        {
            _actionState = BotActionState.MovingToDoor;
            CurrentActionDescription = $"Walking to door at ({x},{y})";
            return MoveTo(x, y, useTransition: true);
        }

        // Scripted arrivals can legitimately leave Luke on the exit itself.
        // Door travel is a tile-entry event; standing still cannot retrigger it.
        // Step aside so the next route instruction can enter it normally.
        if(_state.CurrentZone is { } zone)
            foreach(var (dx,dy) in new[] { (0,-1),(0,1),(-1,0),(1,0) })
                if(_pathfinder.IsWalkable(zone,x+dx,y+dy,_state.ZoneNPCs,_state.CurrentZoneId))
                    return MoveTo(x+dx,y+dy);
        _actionState = BotActionState.Completed;
        return false;
    }

    // Track X-Wing movement
    private int _xwingX, _xwingY;

    /// <summary>
    /// Uses the X-Wing - first walks to it, then travels.
    /// </summary>
    public bool UseXWing()
    {
        if (_state.CurrentZone == null)
        {
            _actionState = BotActionState.UsingXWing;
            OnAction?.Invoke(BotActionType.UseXWing, 0, 0, Direction.Down);
            _actionState = BotActionState.Completed;
            return true;
        }

        // Find X-Wing location in current zone
        ZoneObject? xwing = null;
        foreach (var obj in _state.CurrentZone.Objects)
        {
            if (obj.Type == ZoneObjectType.XWingFromDagobah ||
                obj.Type == ZoneObjectType.XWingToDagobah)
            {
                xwing = obj;
                break;
            }
        }

        if (xwing == null)
        {
            // No X-Wing in zone - just travel
            Console.WriteLine("[BOT] No X-Wing in zone, triggering travel anyway");
            _actionState = BotActionState.UsingXWing;
            OnAction?.Invoke(BotActionType.UseXWing, 0, 0, Direction.Down);
            _actionState = BotActionState.Completed;
            return true;
        }

        // Check if we're at or adjacent to X-Wing
        int dist = Math.Abs(_state.PlayerX - xwing.X) + Math.Abs(_state.PlayerY - xwing.Y);
        if (dist <= 1)
        {
            // At X-Wing - travel!
            Console.WriteLine($"[BOT] At X-Wing ({xwing.X},{xwing.Y}), taking off!");
            _actionState = BotActionState.UsingXWing;
            OnAction?.Invoke(BotActionType.UseXWing, 0, 0, Direction.Down);
            _actionState = BotActionState.Completed;
            return true;
        }

        // Need to walk to X-Wing first
        Console.WriteLine($"[BOT] Walking to X-Wing at ({xwing.X},{xwing.Y})");
        _xwingX = xwing.X;
        _xwingY = xwing.Y;
        CurrentActionDescription = $"Walking to X-Wing at ({xwing.X},{xwing.Y})";

        // Move adjacent to X-Wing - this sets _actionState to Moving
        bool success = MoveToAdjacent(xwing.X, xwing.Y, useTransition: true);

        // Restore proper state AFTER MoveToAdjacent (which overwrites it)
        if (success && _actionState != BotActionState.Completed)
        {
            _actionState = BotActionState.MovingToXWing;
        }

        return success;
    }

    /// <summary>
    /// Cancels the current action.
    /// </summary>
    public void Cancel()
    {
        _waitingForFriendly=null; _friendlyWaitTime=0; _yieldedForFriendly=false; _friendlyRefuge=null;
        _actionState = BotActionState.Idle;
        _pathfinder.TransitionTarget = null;
        _bumpTarget = false;
        _currentPath = null;
        _targetNpc = null;
        _itemToUse = null;
        CurrentActionDescription = "";
    }

    /// <summary>
    /// Resets state after action completion.
    /// </summary>
    public void Reset()
    {
        _waitingForFriendly=null; _friendlyWaitTime=0; _yieldedForFriendly=false; _friendlyRefuge=null;
        _actionState = BotActionState.Idle;
        _pathfinder.TransitionTarget = null;
        _bumpTarget = false;
        _currentPath = null;
        CurrentActionDescription = "";
    }

    /// <summary>
    /// Updates the action state (call each frame).
    /// </summary>
    public void Update(double deltaTime)
    {
        _moveTimer += deltaTime;
        if(_waitingForFriendly!=null) _friendlyWaitTime+=deltaTime;

        switch (_actionState)
        {
            case BotActionState.Moving:
            case BotActionState.MovingToNpc:
            case BotActionState.MovingToItem:
            case BotActionState.MovingToDoor:
            case BotActionState.MovingToPush:
            case BotActionState.MovingToXWing:
                UpdateMovement();
                break;

            case BotActionState.Completed:
                // Action finished, caller should check and reset
                break;
        }
    }

    private void UpdateMovement()
    {
        // Smooth movement commits tile coordinates only on arrival.
        if (MovementPending?.Invoke() == true) return;
        if (_currentPath == null || _currentPathIndex >= _currentPath.Count)
        {
            OnMovementComplete();
            return;
        }

        // Rate limit movement
        if (_moveTimer < MoveCooldown)
            return;

        _moveTimer = 0;

        if(_yieldedForFriendly && _state.CurrentZone is { } yieldingZone)
        {
            var resumed=_pathfinder.FindPathWithoutClearing(yieldingZone,_state.PlayerX,_state.PlayerY,
                _walkTargetX,_walkTargetY,_state.ZoneNPCs,_state.CurrentZoneId);
            if(resumed==null)
                resumed=_pathfinder.FindPathWithoutClearing(yieldingZone,_state.PlayerX,_state.PlayerY,
                    _walkTargetX,_walkTargetY,_state.ZoneNPCs.Where(n=>!n.IsHostile || n.IsInvulnerable).ToList(),_state.CurrentZoneId);
            if(resumed==null)
            {
                if(_friendlyRefuge is { } refuge && (_state.PlayerX,_state.PlayerY)!=refuge &&
                    _pathfinder.FindPathWithoutClearing(yieldingZone,_state.PlayerX,_state.PlayerY,
                        refuge.X,refuge.Y,_state.ZoneNPCs,_state.CurrentZoneId) is { Count:>0 } retreat)
                {
                    var next=retreat.First(p=>p!=(_state.PlayerX,_state.PlayerY));
                    OnAction?.Invoke(BotActionType.Move,next.X,next.Y,
                        _pathfinder.GetDirection(_state.PlayerX,_state.PlayerY,next.X,next.Y)!.Value);
                }
                return;
            }
            _currentPath=resumed; _currentPathIndex=0;
            _yieldedForFriendly=false; _friendlyRefuge=null; _waitingForFriendly=null; _friendlyWaitTime=0;
            return;
        }

        var (nextX, nextY) = _currentPath[_currentPathIndex];

        // Check if we're already at this position
        if (_state.PlayerX == nextX && _state.PlayerY == nextY)
        {
            _currentPathIndex++;
            if (_currentPathIndex >= _currentPath.Count)
            {
                OnMovementComplete();
            }
            return;
        }

        // Determine direction
        var dir = _pathfinder.GetDirection(_state.PlayerX, _state.PlayerY, nextX, nextY);
        if (dir == null)
        {
            _currentPathIndex++;
            return;
        }

        var blocker = _state.ZoneNPCs.FirstOrDefault(n => n.IsEnabled && n.IsAlive && n.IsHostile && !n.IsInvulnerable &&
            n.X == nextX && n.Y == nextY);
        if (blocker != null)
        {
            OnRouteBlockedByEnemy?.Invoke(blocker);
            return;
        }

        int stepX=nextX-_state.PlayerX,stepY=nextY-_state.PlayerY;
        var friendly=_state.ZoneNPCs.FirstOrDefault(n=>n.IsEnabled && n.IsAlive && (!n.IsHostile || n.IsInvulnerable) &&
            (n.X==nextX && n.Y==nextY || stepX!=0 && stepY!=0 &&
             (n.X==nextX && n.Y==_state.PlayerY || n.X==_state.PlayerX && n.Y==nextY)));
        if(friendly!=null && friendly.Behavior is not (NPCBehavior.Stationary or NPCBehavior.Animation))
        {
            if(_waitingForFriendly!=friendly)
            {
                Trace?.Invoke("wait-for-friendly",new { friendly.CharacterId,friendly.X,friendly.Y,TargetX=nextX,TargetY=nextY });
                _friendlyWaitTime=0;
            }
            _waitingForFriendly=friendly;
            if(_friendlyWaitTime>=1 && _state.CurrentZone is { } room)
            {
                var detour=_pathfinder.FindPathWithoutClearing(room,_state.PlayerX,_state.PlayerY,
                    _walkTargetX,_walkTargetY,_state.ZoneNPCs,_state.CurrentZoneId);
                if(detour is { Count:>0 })
                { _currentPath=detour; _currentPathIndex=0; _waitingForFriendly=null; _friendlyWaitTime=0; return; }
                if(FindFriendlyRefuge(room,friendly) is { } refuge)
                {
                    _friendlyRefuge=refuge; _yieldedForFriendly=true;
                    Trace?.Invoke("friendly-refuge",new { friendly.CharacterId,refuge.X,refuge.Y });
                    return;
                }
                // Luke can occupy the only square through which a wandering
                // companion can leave a narrow passage. Yield a clear square,
                // then resume the original searched route once it is available.
                foreach(var cell in _pathfinder.WalkingNeighbors(room,_state.PlayerX,_state.PlayerY,
                    _state.ZoneNPCs,_state.CurrentZoneId)
                    .OrderByDescending(p=>Math.Abs(p.X-friendly.X)+Math.Abs(p.Y-friendly.Y)))
                {
                    int ax=cell.X,ay=cell.Y;
                    Trace?.Invoke("yield-to-friendly",new { friendly.CharacterId,X=ax,Y=ay });
                    _yieldedForFriendly=true;
                    OnAction?.Invoke(BotActionType.Move,ax,ay,_pathfinder.GetDirection(_state.PlayerX,_state.PlayerY,ax,ay)!.Value);
                    return;
                }
            }
            return;
        }
        _waitingForFriendly=null; _friendlyWaitTime=0;

        if (_state.CurrentZone is { } currentZone && _pathfinder.IsUnrelatedTransition(currentZone, nextX, nextY))
        {
            _currentPath = _pathfinder.FindPathWithoutClearing(currentZone, _state.PlayerX, _state.PlayerY,
                _walkTargetX, _walkTargetY, _state.ZoneNPCs, _state.CurrentZoneId);
            _currentPathIndex = 0;
            if (_currentPath == null) _actionState = BotActionState.Completed;
            return;
        }

        // Request move
        OnAction?.Invoke(BotActionType.Move, nextX, nextY, dir.Value);
        if (MovementPending?.Invoke() == true) return;

        // Check if move was successful (player position changed)
        if (_state.PlayerX == nextX && _state.PlayerY == nextY)
        {
            _currentPathIndex++;
        }
        else
        {
            // Move was blocked - mark this position and recalculate path
            Trace?.Invoke("move-rejected",new { X=nextX,Y=nextY,Direction=dir,Action=_actionState });
            Console.WriteLine($"[BOT] Move blocked at ({nextX},{nextY}), marking as blocked...");
            _pathfinder.MarkBlocked(_state.CurrentZoneId, nextX, nextY);

            if (_state.CurrentZone != null)
            {
                // Don't call ClearBlockedPositions - we want to keep the blocklist
                // Use a special overload or pass zoneId=null to skip clearing
                _currentPath = _pathfinder.FindPathWithoutClearing(
                    _state.CurrentZone,
                    _state.PlayerX, _state.PlayerY,
                    _walkTargetX, _walkTargetY,
                    _state.ZoneNPCs,
                    _state.CurrentZoneId);

                if (_currentPath == null || _currentPath.Count == 0)
                {
                    Console.WriteLine("[BOT] No path found after recalculation, giving up");
                    _actionState = BotActionState.Completed;
                }
                else
                {
                    _currentPathIndex = 0;
                }
            }
        }
    }

    private void OnMovementComplete()
    {
        switch (_actionState)
        {
            case BotActionState.MovingToNpc:
                // Now interact with NPC
                _actionState = BotActionState.Interacting;
                PerformNpcInteraction();
                break;

            case BotActionState.MovingToPush:
                _actionState = BotActionState.Pushing;
                PerformPush();
                break;

            case BotActionState.MovingToXWing:
                // Arrived at X-Wing - take off!
                Console.WriteLine("[BOT] Reached X-Wing, taking off!");
                _actionState = BotActionState.UsingXWing;
                OnAction?.Invoke(BotActionType.UseXWing, 0, 0, Direction.Down);
                _actionState = BotActionState.Completed;
                break;

            default:
                _actionState = BotActionState.Completed;
                break;
        }
    }

    private (int X,int Y)? FindFriendlyRefuge(Zone zone,NPC companion)
    {
        // Prove a passing place: Luke can reach it, the companion can reach a
        // different square without crossing Luke, and Luke can then resume his
        // original route. Merely stepping backward in a one-cell corridor can
        // leave both actors blocking each other again.
        var others=_state.ZoneNPCs.Where(n=>n!=companion && (!n.IsHostile || n.IsInvulnerable)).ToList();
        var actorNav=new Pathfinder(_gameData,_state) { AllowDiagonal=_pathfinder.AllowDiagonal,PreferCardinal=_pathfinder.PreferCardinal,
            AdditionalWalkability=(area,x,y)=>_pathfinder.AdditionalWalkability?.Invoke(area,x,y)!=false &&
                (companion.Behavior!=NPCBehavior.Wandering || Math.Abs(x-companion.StartX)<=companion.WanderRadius &&
                 Math.Abs(y-companion.StartY)<=companion.WanderRadius) };
        var cells=(from y in Enumerable.Range(0,zone.Height) from x in Enumerable.Range(0,zone.Width)
                   select (X:x,Y:y)).ToArray();
        var futures=cells.Where(p=>actorNav.IsWalkable(zone,p.X,p.Y,others,zone.Id))
            .OrderBy(p=>Math.Abs(p.X-companion.X)+Math.Abs(p.Y-companion.Y)).Take(60).ToArray();
        foreach(var refuge in cells.Where(p=>p!=(_state.PlayerX,_state.PlayerY) &&
            _pathfinder.IsWalkable(zone,p.X,p.Y,_state.ZoneNPCs,zone.Id))
            .OrderBy(p=>Math.Abs(p.X-_state.PlayerX)+Math.Abs(p.Y-_state.PlayerY)).Take(48))
        {
            if(_pathfinder.FindPathWithoutClearing(zone,_state.PlayerX,_state.PlayerY,refuge.X,refuge.Y,
                _state.ZoneNPCs,zone.Id) is not { Count:>0 }) continue;
            foreach(var future in futures.Where(p=>p!=refuge))
            {
                var shifted=others.Concat(new[]{new NPC { X=future.X,Y=future.Y }}).ToList();
                if(_pathfinder.FindPathWithoutClearing(zone,refuge.X,refuge.Y,_walkTargetX,_walkTargetY,shifted,zone.Id)==null) continue;
                var actors=others.Concat(new[]{new NPC { X=refuge.X,Y=refuge.Y }}).ToList();
                if(actorNav.FindPath(zone,companion.X,companion.Y,future.X,future.Y,actors,zone.Id)==null) continue;
                return refuge;
            }
        }
        return null;
    }

    public bool BumpAt(int x, int y, bool useTransition = false, (int X,int Y)? stance = null)
    {
        _targetNpc = new NPC { X = x, Y = y };
        _itemToUse = null;
        _bumpTarget = true;
        if (stance is { } stand)
        {
            if (Math.Max(Math.Abs(stand.X-x),Math.Abs(stand.Y-y))!=1 || !MoveTo(stand.X,stand.Y,useTransition)) return false;
        }
        else if (!MoveToAdjacent(x, y, useTransition) && !MoveToAdjacent(x,y,useTransition,allowDiagonalInteraction:true)) return false;
        if (stance is { } required ? (_state.PlayerX,_state.PlayerY)==required : Math.Max(Math.Abs(_state.PlayerX-x),Math.Abs(_state.PlayerY-y))==1) PerformNpcInteraction();
        else _actionState = BotActionState.MovingToNpc;
        return true;
    }

    private void PerformNpcInteraction()
    {
        if (_targetNpc == null)
        {
            _actionState = BotActionState.Completed;
            return;
        }

        // Face the NPC
        var dir = _pathfinder.GetDirection(_state.PlayerX, _state.PlayerY, _targetNpc.X, _targetNpc.Y) ?? Direction.Down;

        if (_bumpTarget)
        {
            _bumpTarget = false;
            bool vehicle=_state.CurrentZone?.Objects.Any(obj=>obj.Enabled &&
                obj.X==_targetNpc.X && obj.Y==_targetNpc.Y &&
                obj.Type is ZoneObjectType.VehicleToPrimary or ZoneObjectType.VehicleToSecondary) == true;
            OnAction?.Invoke(vehicle ? BotActionType.Move : BotActionType.Bump,
                _targetNpc.X, _targetNpc.Y, dir);
        }
        else if (_itemToUse.HasValue)
        {
            // Using item on NPC
            OnAction?.Invoke(BotActionType.UseItemAt, _targetNpc.X, _targetNpc.Y, dir);
        }
        else
        {
            // Just talking
            OnAction?.Invoke(BotActionType.Talk, _targetNpc.X, _targetNpc.Y, dir);
        }

        _actionState = BotActionState.Completed;
        _itemToUse = null;
    }

    private void PerformPush()
    {
        if (!_pushDirection.HasValue)
        {
            _actionState = BotActionState.Completed;
            return;
        }

        var (dx, dy) = _pushDirection.Value switch
        { Direction.Up => (0, -1), Direction.Down => (0, 1), Direction.Left => (-1, 0), _ => (1, 0) };
        int standX = _targetX + (_pulling ? dx : -dx), standY = _targetY + (_pulling ? dy : -dy);
        int tile = _state.CurrentZone?.GetTile(_targetX, _targetY, 1) ?? ushort.MaxValue;
        if (_state.PlayerX != standX || _state.PlayerY != standY || tile >= _gameData.Tiles.Count ||
            !_gameData.Tiles[tile].IsDraggable)
        {
            Console.WriteLine("[BOT] Block move preconditions changed; replanning");
            _actionState = BotActionState.Completed;
            _pushDirection = null; _pulling = false;
            return;
        }
        if (_pulling)
            OnAction?.Invoke(BotActionType.Pull, 0, 0, _pushDirection.Value);
        else
            OnAction?.Invoke(BotActionType.Move, _targetX, _targetY, _pushDirection.Value);
        _actionState = BotActionState.Completed;
        _pushDirection = null;
        _pulling = false;
    }

    private bool MoveIntoObjectPosition(int standX, int standY, int blockX, int blockY)
    {
        if (!MoveTo(standX, standY)) return false;
        if (_currentPath is { Count: > 0 } && _currentPath[^1] != (standX, standY))
        { Cancel(); return false; }
        _targetX = blockX; _targetY = blockY;
        _actionState = BotActionState.MovingToPush;
        CurrentActionDescription = $"Positioning to {(_pulling ? "pull" : "push")} block ({blockX},{blockY})";
        return true;
    }

    private bool _pulling;

    /// <summary>
    /// Drags the block at (x, y) one square in <paramref name="dir"/>: stand on its
    /// <paramref name="dir"/> side and step away, pulling. For a block wedged where no
    /// push can move it (a crate in a corner with the treasure under it).
    /// </summary>
    public bool PullObject(int x, int y, Direction dir)
    {
        var (dx, dy) = dir switch { Direction.Up => (0, -1), Direction.Down => (0, 1), Direction.Left => (-1, 0), _ => (1, 0) };
        _targetX = x;
        _targetY = y;
        _pushDirection = dir;
        _pulling = true;

        if (_state.PlayerX != x + dx || _state.PlayerY != y + dy)
        {
            return MoveIntoObjectPosition(x + dx, y + dy, x, y);
        }

        _actionState = BotActionState.Pushing;
        PerformPush();
        return true;
    }

    private static bool IsAdjacent(int x1, int y1, int x2, int y2)
    {
        int dx = Math.Abs(x1 - x2);
        int dy = Math.Abs(y1 - y2);
        return (dx + dy) == 1;
    }
}

/// <summary>
/// Bot action states.
/// </summary>
public enum BotActionState
{
    Idle,
    Moving,
    MovingToNpc,
    MovingToItem,
    MovingToDoor,
    MovingToPush,
    MovingToXWing,
    Attacking,
    Interacting,
    UsingItem,
    Pushing,
    UsingXWing,
    Completed
}

/// <summary>
/// Types of actions the bot can request.
/// </summary>
public enum BotActionType
{
    Move,
    Bump,
    Attack,
    Talk,
    UseItem,
    UseItemAt,
    UseXWing,
    Pull,       // step in the direction given, dragging the block behind along
}
