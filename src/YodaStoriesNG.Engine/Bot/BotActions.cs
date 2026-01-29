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
    private double _moveTimer;
    private const double MoveCooldown = 0.1; // 100ms between moves

    // Action state
    private BotActionState _actionState = BotActionState.Idle;
    private int _targetX, _targetY;
    private NPC? _targetNpc;
    private int? _itemToUse;
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
    public bool IsBusy => _actionState != BotActionState.Idle && _actionState != BotActionState.Completed;

    /// <summary>
    /// Whether the current action has completed.
    /// </summary>
    public bool IsCompleted => _actionState == BotActionState.Completed;

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
    public bool MoveTo(int x, int y)
    {
        if (_state.CurrentZone == null)
            return false;

        // Already there?
        if (_state.PlayerX == x && _state.PlayerY == y)
        {
            _actionState = BotActionState.Completed;
            return true;
        }

        // Find path
        _currentPath = _pathfinder.FindPath(
            _state.CurrentZone,
            _state.PlayerX, _state.PlayerY,
            x, y,
            _state.ZoneNPCs);

        if (_currentPath == null || _currentPath.Count == 0)
        {
            Console.WriteLine($"[BOT] No path to ({x},{y})");
            _actionState = BotActionState.Completed; // Give up
            return false;
        }

        _currentPathIndex = 0;
        _targetX = x;
        _targetY = y;
        _actionState = BotActionState.Moving;
        CurrentActionDescription = $"Moving to ({x},{y})";
        Console.WriteLine($"[BOT] Found path with {_currentPath.Count} steps to ({x},{y})");
        return true;
    }

    /// <summary>
    /// Starts moving to be adjacent to a target (for interaction).
    /// </summary>
    public bool MoveToAdjacent(int x, int y)
    {
        if (_state.CurrentZone == null)
            return false;

        // Already adjacent?
        if (IsAdjacent(_state.PlayerX, _state.PlayerY, x, y))
        {
            _actionState = BotActionState.Completed;
            return true;
        }

        var result = _pathfinder.FindPathToAdjacent(
            _state.CurrentZone,
            _state.PlayerX, _state.PlayerY,
            x, y,
            _state.ZoneNPCs);

        if (result == null || result.Value.Path == null)
        {
            Console.WriteLine($"[BOT] No path to adjacent of ({x},{y})");
            _actionState = BotActionState.Completed;
            return false;
        }

        _currentPath = result.Value.Path;
        _currentPathIndex = 0;
        _targetX = result.Value.FinalPos.X;
        _targetY = result.Value.FinalPos.Y;
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
        _targetNpc = npc;

        // Move adjacent first if needed
        if (!IsAdjacent(_state.PlayerX, _state.PlayerY, npc.X, npc.Y))
        {
            _actionState = BotActionState.MovingToNpc;
            CurrentActionDescription = $"Walking to NPC at ({npc.X},{npc.Y})";
            return MoveToAdjacent(npc.X, npc.Y);
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
        if (!IsAdjacent(_state.PlayerX, _state.PlayerY, npc.X, npc.Y))
        {
            _actionState = BotActionState.MovingToNpc;
            CurrentActionDescription = $"Walking to NPC with item {itemId}";
            return MoveToAdjacent(npc.X, npc.Y);
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
        // Need to walk on the item to pick it up
        if (_state.PlayerX != x || _state.PlayerY != y)
        {
            _actionState = BotActionState.MovingToItem;
            CurrentActionDescription = $"Walking to item at ({x},{y})";
            return MoveTo(x, y);
        }

        // Already on the item - it should auto-pickup
        _actionState = BotActionState.Completed;
        return true;
    }

    /// <summary>
    /// Pushes an object in a direction.
    /// </summary>
    public bool PushObject(int x, int y, Direction dir)
    {
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
            _actionState = BotActionState.MovingToPush;
            CurrentActionDescription = $"Moving to push object at ({x},{y})";
            return MoveTo(pushFromX, pushFromY);
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
        // Walk to the door
        if (_state.PlayerX != x || _state.PlayerY != y)
        {
            _actionState = BotActionState.MovingToDoor;
            CurrentActionDescription = $"Walking to door at ({x},{y})";
            return MoveTo(x, y);
        }

        // On the door - should trigger automatically
        _actionState = BotActionState.Completed;
        return true;
    }

    /// <summary>
    /// Triggers X-Wing travel.
    /// </summary>
    public void UseXWing()
    {
        _actionState = BotActionState.UsingXWing;
        CurrentActionDescription = "Using X-Wing";
        OnAction?.Invoke(BotActionType.UseXWing, 0, 0, Direction.Down);
        _actionState = BotActionState.Completed;
    }

    /// <summary>
    /// Cancels the current action.
    /// </summary>
    public void Cancel()
    {
        _actionState = BotActionState.Idle;
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
        _actionState = BotActionState.Idle;
        _currentPath = null;
        CurrentActionDescription = "";
    }

    /// <summary>
    /// Updates the action state (call each frame).
    /// </summary>
    public void Update(double deltaTime)
    {
        _moveTimer += deltaTime;

        switch (_actionState)
        {
            case BotActionState.Moving:
            case BotActionState.MovingToNpc:
            case BotActionState.MovingToItem:
            case BotActionState.MovingToDoor:
            case BotActionState.MovingToPush:
                UpdateMovement();
                break;

            case BotActionState.Completed:
                // Action finished, caller should check and reset
                break;
        }
    }

    private void UpdateMovement()
    {
        if (_currentPath == null || _currentPathIndex >= _currentPath.Count)
        {
            OnMovementComplete();
            return;
        }

        // Rate limit movement
        if (_moveTimer < MoveCooldown)
            return;

        _moveTimer = 0;

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

        // Request move
        OnAction?.Invoke(BotActionType.Move, nextX, nextY, dir.Value);

        // Check if move was successful (player position changed)
        if (_state.PlayerX == nextX && _state.PlayerY == nextY)
        {
            _currentPathIndex++;
        }
        else
        {
            // Move was blocked - recalculate path
            Console.WriteLine($"[BOT] Move blocked at ({nextX},{nextY}), recalculating...");
            if (_state.CurrentZone != null)
            {
                _currentPath = _pathfinder.FindPath(
                    _state.CurrentZone,
                    _state.PlayerX, _state.PlayerY,
                    _targetX, _targetY,
                    _state.ZoneNPCs);

                if (_currentPath == null)
                {
                    Console.WriteLine("[BOT] No path found after recalculation");
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

            default:
                _actionState = BotActionState.Completed;
                break;
        }
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

        if (_itemToUse.HasValue)
        {
            // Using item on NPC
            OnAction?.Invoke(BotActionType.UseItem, _itemToUse.Value, 0, dir);
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

        OnAction?.Invoke(BotActionType.Move, _targetX, _targetY, _pushDirection.Value);
        _actionState = BotActionState.Completed;
        _pushDirection = null;
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
    Attack,
    Talk,
    UseItem,
    UseXWing
}
