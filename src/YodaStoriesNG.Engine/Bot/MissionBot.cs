using YodaStoriesNG.Engine.Data;
using YodaStoriesNG.Engine.Game;

namespace YodaStoriesNG.Engine.Bot;

/// <summary>
/// Main bot controller - orchestrates mission completion.
/// Uses exploration-based approach: systematically explores zones,
/// interacts with everything, and lets IACT scripts drive progression.
/// </summary>
public class MissionBot
{
    private readonly GameState _state;
    private readonly GameData _gameData;
    private readonly WorldGenerator _worldGenerator;
    private readonly Pathfinder _pathfinder;
    private readonly BotActions _actions;
    private readonly MissionSolver _solver;

    // Bot state
    private bool _isRunning;
    private BotState _currentState;
    private double _thinkTimer;
    private double _stuckTimer;
    private (int X, int Y, int ZoneId) _lastPosition;
    private int _stuckCount;

    // Current objective tracking
    private BotObjective? _currentObjective;
    private NPC? _lastTargetNpc;
    private int? _lastItemUsed;

    // Timing constants
    private const double ThinkInterval = 0.15;    // 150ms between decisions
    private const double StuckThreshold = 5.0;    // Consider stuck after 5 seconds (was 3)
    private const int MaxStuckRetries = 10;       // More retries before giving up (was 5)
    private const int MaxZoneExitAttempts = 10;   // More attempts before marking exit blocked (was 3)

    // Random for exploration
    private readonly Random _random = new();

    // Events
    public event Action<BotActionType, int, int, Direction>? OnActionRequested;

    public MissionBot(GameState state, GameData gameData, WorldGenerator worldGenerator)
    {
        _state = state;
        _gameData = gameData;
        _worldGenerator = worldGenerator;
        _pathfinder = new Pathfinder(gameData);
        _actions = new BotActions(state, gameData, _pathfinder);
        _solver = new MissionSolver(state, gameData, worldGenerator);

        // Wire up action events
        _actions.OnAction += (type, x, y, dir) => OnActionRequested?.Invoke(type, x, y, dir);

        _currentState = BotState.Idle;
    }

    /// <summary>
    /// Whether the bot is currently running.
    /// </summary>
    public bool IsRunning => _isRunning;

    /// <summary>
    /// Current bot state.
    /// </summary>
    public BotState CurrentState => _currentState;

    /// <summary>
    /// Gets current task description for display.
    /// </summary>
    public string CurrentTask
    {
        get
        {
            if (!_isRunning) return "Bot disabled";
            if (_actions.IsBusy) return _actions.CurrentActionDescription;
            return _currentState switch
            {
                BotState.Idle => "Idle",
                BotState.ThinkingAboutObjective => "Planning...",
                BotState.ExecutingObjective => _currentObjective?.Description ?? "Working...",
                BotState.Combat => "Fighting!",
                BotState.Exploring => "Exploring...",
                BotState.Stuck => "Trying alternatives...",
                _ => "Unknown"
            };
        }
    }

    /// <summary>
    /// Starts the bot.
    /// </summary>
    public void Start()
    {
        if (_isRunning) return;

        _isRunning = true;
        _currentState = BotState.ThinkingAboutObjective;
        _thinkTimer = 0;
        _stuckTimer = 0;
        _stuckCount = 0;
        _lastPosition = (_state.PlayerX, _state.PlayerY, _state.CurrentZoneId);
        _committedExplorationZone = null;
        _committedExplorationDirection = null;
        _zoneExitAttempts = 0;
        _solver.Reset();

        Console.WriteLine("[BOT] Started - Explorer mode");
        Console.WriteLine("[BOT] Will systematically explore zones and interact with everything");
    }

    /// <summary>
    /// Stops the bot.
    /// </summary>
    public void Stop()
    {
        if (!_isRunning) return;

        _isRunning = false;
        _currentState = BotState.Idle;
        _actions.Cancel();

        Console.WriteLine("[BOT] Stopped");
    }

    /// <summary>
    /// Main update method - call each frame.
    /// </summary>
    public void Update(double deltaTime)
    {
        if (!_isRunning)
            return;

        // Update action executor
        _actions.Update(deltaTime);

        // Update timers
        _thinkTimer += deltaTime;
        UpdateStuckDetection(deltaTime);

        // State machine
        switch (_currentState)
        {
            case BotState.ThinkingAboutObjective:
                Think();
                break;

            case BotState.ExecutingObjective:
                ExecuteObjective();
                break;

            case BotState.Combat:
                HandleCombat();
                break;

            case BotState.Exploring:
                HandleExploration();
                break;

            case BotState.Stuck:
                HandleStuck();
                break;
        }
    }

    private void Think()
    {
        // Rate limit thinking
        if (_thinkTimer < ThinkInterval)
            return;
        _thinkTimer = 0;

        // Check for immediate threats (enemies nearby)
        var enemy = _solver.FindNearestEnemy();
        if (enemy != null && enemy.DistanceTo(_state.PlayerX, _state.PlayerY) <= 6)
        {
            Console.WriteLine($"[BOT] Enemy detected: {GetNpcName(enemy)} at ({enemy.X},{enemy.Y})!");
            _currentState = BotState.Combat;
            return;
        }

        // Check if game is won
        if (_state.IsGameWon)
        {
            Console.WriteLine("[BOT] MISSION COMPLETE! Game won!");
            Stop();
            return;
        }

        // Check if game over
        if (_state.IsGameOver)
        {
            Console.WriteLine("[BOT] Game over!");
            Stop();
            return;
        }

        // Get current objective from solver
        _currentObjective = _solver.GetCurrentObjective();
        Console.WriteLine($"[BOT] Objective: {_currentObjective.Type} - {_currentObjective.Description}");

        _currentState = BotState.ExecutingObjective;
        StartObjective(_currentObjective);
    }

    private void StartObjective(BotObjective objective)
    {
        switch (objective.Type)
        {
            case ObjectiveType.TalkToNpc:
                if (objective.TargetNpc != null)
                {
                    _lastTargetNpc = objective.TargetNpc;
                    Console.WriteLine($"[BOT] Going to talk to NPC at ({objective.TargetNpc.X},{objective.TargetNpc.Y})");
                    if (!_actions.TalkToNpc(objective.TargetNpc))
                    {
                        // Can't reach NPC
                        _solver.MarkUnreachable(objective.TargetNpc.X, objective.TargetNpc.Y);
                        _currentState = BotState.ThinkingAboutObjective;
                    }
                }
                else
                {
                    _currentState = BotState.Exploring;
                }
                break;

            case ObjectiveType.UseItemOnNpc:
                if (objective.TargetNpc != null && objective.RequiredItemId.HasValue)
                {
                    _lastTargetNpc = objective.TargetNpc;
                    _lastItemUsed = objective.RequiredItemId;
                    Console.WriteLine($"[BOT] Using item {objective.RequiredItemId} on NPC at ({objective.TargetNpc.X},{objective.TargetNpc.Y})");
                    if (!_actions.UseItemOnNpc(objective.RequiredItemId.Value, objective.TargetNpc))
                    {
                        // Can't reach NPC
                        _solver.MarkUnreachable(objective.TargetNpc.X, objective.TargetNpc.Y);
                        _currentState = BotState.ThinkingAboutObjective;
                    }
                }
                else
                {
                    _currentState = BotState.Exploring;
                }
                break;

            case ObjectiveType.PickupItem:
                Console.WriteLine($"[BOT] Picking up item at ({objective.TargetX},{objective.TargetY})");
                if (!_actions.PickupItem(objective.TargetX, objective.TargetY))
                {
                    _solver.MarkUnreachable(objective.TargetX, objective.TargetY);
                    _currentState = BotState.ThinkingAboutObjective;
                }
                break;

            case ObjectiveType.ChangeZone:
                if (objective.Direction.HasValue)
                {
                    Console.WriteLine($"[BOT] Moving to zone {objective.TargetZoneId} ({objective.Direction.Value})");
                    MoveToZoneEdge(objective.Direction.Value);
                }
                else if (objective.TargetZoneId.HasValue)
                {
                    var dir = _solver.GetDirectionToAdjacentZone(objective.TargetZoneId.Value);
                    if (dir.HasValue)
                    {
                        MoveToZoneEdge(dir.Value);
                    }
                    else
                    {
                        _currentState = BotState.Exploring;
                    }
                }
                break;

            case ObjectiveType.UseXWing:
                Console.WriteLine("[BOT] Using X-Wing to travel");
                _actions.UseXWing();
                break;

            case ObjectiveType.EnterDoor:
                Console.WriteLine($"[BOT] Entering door at ({objective.TargetX},{objective.TargetY})");
                _solver.MarkDoorEntered(objective.TargetX, objective.TargetY);
                if (!_actions.EnterDoor(objective.TargetX, objective.TargetY))
                {
                    _solver.MarkUnreachable(objective.TargetX, objective.TargetY);
                    _currentState = BotState.ThinkingAboutObjective;
                }
                break;

            case ObjectiveType.KillEnemy:
                _currentState = BotState.Combat;
                break;

            case ObjectiveType.Explore:
            default:
                _currentState = BotState.Exploring;
                break;
        }
    }

    private void ExecuteObjective()
    {
        if (_actions.IsCompleted)
        {
            // Mark actions as completed
            if (_currentObjective != null)
            {
                switch (_currentObjective.Type)
                {
                    case ObjectiveType.TalkToNpc:
                        if (_lastTargetNpc != null)
                        {
                            _solver.MarkTalkedTo(_lastTargetNpc);
                            Console.WriteLine($"[BOT] Talked to NPC at ({_lastTargetNpc.X},{_lastTargetNpc.Y})");
                        }
                        break;

                    case ObjectiveType.UseItemOnNpc:
                        if (_lastTargetNpc != null && _lastItemUsed.HasValue)
                        {
                            _solver.MarkUsedItemOn(_lastTargetNpc, _lastItemUsed.Value);
                            Console.WriteLine($"[BOT] Used item {_lastItemUsed} on NPC");
                        }
                        break;

                    case ObjectiveType.PickupItem:
                        _solver.MarkItemCollected(_currentObjective.TargetX, _currentObjective.TargetY);
                        Console.WriteLine($"[BOT] Collected item at ({_currentObjective.TargetX},{_currentObjective.TargetY})");
                        break;
                }
            }

            _actions.Reset();
            _lastTargetNpc = null;
            _lastItemUsed = null;

            // Check if there's a pending zone exit
            if (_pendingZoneExitDirection.HasValue && _state.CurrentZone != null)
            {
                var dir = _pendingZoneExitDirection.Value;
                bool atEdge = dir switch
                {
                    Direction.Up => _state.PlayerY <= 2,
                    Direction.Down => _state.PlayerY >= _state.CurrentZone.Height - 3,
                    Direction.Left => _state.PlayerX <= 2,
                    Direction.Right => _state.PlayerX >= _state.CurrentZone.Width - 3,
                    _ => false
                };

                if (atEdge)
                {
                    var connected = _worldGenerator.GetConnectedZone(_state.CurrentZoneId, dir);
                    Console.WriteLine($"[BOT] At edge, walking {dir}. Target zone: {connected?.ToString() ?? "none"}");
                    OnActionRequested?.Invoke(BotActionType.Move, 0, 0, dir);
                    _pendingZoneExitDirection = null;
                    return;
                }
            }

            _pendingZoneExitDirection = null;
            _currentState = BotState.ThinkingAboutObjective;
            _stuckCount = 0;
        }
        else if (!_actions.IsBusy)
        {
            // Action finished without completing
            _pendingZoneExitDirection = null;
            _currentState = BotState.ThinkingAboutObjective;
        }
    }

    // Track combat approach attempts
    private int _combatApproachAttempts;
    private (int X, int Y) _lastCombatPos;

    private void HandleCombat()
    {
        // Rate limit
        if (_thinkTimer < ThinkInterval)
            return;
        _thinkTimer = 0;

        var enemy = _solver.FindNearestEnemy();
        if (enemy == null)
        {
            Console.WriteLine("[BOT] All enemies defeated");
            _currentState = BotState.ThinkingAboutObjective;
            _combatApproachAttempts = 0;
            return;
        }

        int dist = enemy.DistanceTo(_state.PlayerX, _state.PlayerY);

        // Check if we're making progress
        if (_lastCombatPos == (_state.PlayerX, _state.PlayerY))
        {
            _combatApproachAttempts++;
            if (_combatApproachAttempts > 10)
            {
                Console.WriteLine("[BOT] Stuck in combat, marking enemy unreachable");
                _solver.MarkUnreachable(enemy.X, enemy.Y);
                _combatApproachAttempts = 0;
                _currentState = BotState.ThinkingAboutObjective;
                return;
            }
        }
        else
        {
            _combatApproachAttempts = 0;
            _lastCombatPos = (_state.PlayerX, _state.PlayerY);
        }

        Console.WriteLine($"[BOT] Combat: Player at ({_state.PlayerX},{_state.PlayerY}), Enemy {GetNpcName(enemy)} at ({enemy.X},{enemy.Y}), dist={dist}");

        if (dist <= 1)
        {
            // Adjacent - attack!
            var dir = GetDirectionTo(enemy.X, enemy.Y);
            Console.WriteLine($"[BOT] Attacking {GetNpcName(enemy)} in direction {dir}!");
            _actions.Attack(dir);
            _combatApproachAttempts = 0;
        }
        else if (dist <= 15)
        {
            // Close enough - try to path first
            if (_actions.IsBusy)
                return; // Wait for current action to complete

            Console.WriteLine($"[BOT] Approaching enemy at ({enemy.X},{enemy.Y})");
            if (!_actions.MoveToAdjacent(enemy.X, enemy.Y))
            {
                // Can't path to enemy - try direct movement
                Console.WriteLine("[BOT] Can't path to enemy, trying direct movement");

                // Calculate direct direction
                int dx = enemy.X - _state.PlayerX;
                int dy = enemy.Y - _state.PlayerY;

                // Try horizontal first if bigger, otherwise vertical
                Direction dir;
                if (Math.Abs(dx) > Math.Abs(dy))
                    dir = dx > 0 ? Direction.Right : Direction.Left;
                else
                    dir = dy > 0 ? Direction.Down : Direction.Up;

                OnActionRequested?.Invoke(BotActionType.Move, 0, 0, dir);

                // If that direction is blocked, try alternate direction
                if (_combatApproachAttempts > 3)
                {
                    // Try the other axis
                    if (Math.Abs(dy) > 0)
                        dir = dy > 0 ? Direction.Down : Direction.Up;
                    else if (Math.Abs(dx) > 0)
                        dir = dx > 0 ? Direction.Right : Direction.Left;
                    OnActionRequested?.Invoke(BotActionType.Move, 0, 0, dir);
                }
            }
        }
        else
        {
            // Enemy too far - go back to thinking
            Console.WriteLine("[BOT] Enemy too far, returning to think");
            _currentState = BotState.ThinkingAboutObjective;
            _combatApproachAttempts = 0;
        }
    }

    private void HandleExploration()
    {
        if (_actions.IsBusy)
        {
            if (_actions.IsCompleted)
            {
                _actions.Reset();
            }
            return;
        }

        // Rate limit
        if (_thinkTimer < ThinkInterval)
            return;
        _thinkTimer = 0;

        // If we have a committed target, stick with it
        if (_committedExplorationZone.HasValue && _committedExplorationDirection.HasValue)
        {
            var unexploredZones = _solver.GetUnexploredConnectedZones();
            bool stillValid = unexploredZones.Contains(_committedExplorationZone.Value) &&
                              !_solver.IsExitBlocked(_committedExplorationDirection.Value);

            if (stillValid)
            {
                Console.WriteLine($"[BOT] Continuing toward zone {_committedExplorationZone} ({_committedExplorationDirection})");
                MoveToZoneEdge(_committedExplorationDirection.Value);
                _currentState = BotState.ExecutingObjective;
                return;
            }
            else
            {
                // Target no longer valid, clear it
                Console.WriteLine($"[BOT] Committed target zone {_committedExplorationZone} no longer valid, picking new target");
                _committedExplorationZone = null;
                _committedExplorationDirection = null;
            }
        }

        // Pick a new exploration target and COMMIT to it
        var zones = _solver.GetUnexploredConnectedZones();
        if (zones.Count > 0)
        {
            // Pick first available (more predictable than random)
            var targetZone = zones[0];
            var dir = _solver.GetDirectionToAdjacentZone(targetZone);
            if (dir.HasValue)
            {
                _committedExplorationZone = targetZone;
                _committedExplorationDirection = dir.Value;
                _zoneExitAttempts = 0;
                Console.WriteLine($"[BOT] Committed to exploring zone {targetZone} via {dir}");
                MoveToZoneEdge(dir.Value);
                _currentState = BotState.ExecutingObjective;
                return;
            }
        }

        // No unexplored connected zones - go back to thinking for other objectives
        _currentState = BotState.ThinkingAboutObjective;
    }

    private void HandleStuck()
    {
        // Rate limit
        if (_thinkTimer < ThinkInterval * 2)
            return;
        _thinkTimer = 0;

        _stuckCount++;
        Console.WriteLine($"[BOT] Stuck (attempt {_stuckCount}/{MaxStuckRetries})");

        if (_stuckCount >= MaxStuckRetries)
        {
            // Really stuck - try to change zones
            Console.WriteLine("[BOT] Max retries reached, trying zone change");
            var unexploredZones = _solver.GetUnexploredConnectedZones();
            if (unexploredZones.Count > 0)
            {
                var dir = _solver.GetDirectionToAdjacentZone(unexploredZones[0]);
                if (dir.HasValue)
                {
                    MoveToZoneEdge(dir.Value);
                    _currentState = BotState.ExecutingObjective;
                    _stuckCount = 0;
                    return;
                }
            }
        }

        // Try random movement
        var directions = new[] { Direction.Up, Direction.Down, Direction.Left, Direction.Right };
        var dir2 = directions[_random.Next(directions.Length)];
        OnActionRequested?.Invoke(BotActionType.Move, 0, 0, dir2);

        _stuckTimer = 0;
        _currentState = BotState.ThinkingAboutObjective;
    }

    // Track the direction we're trying to exit
    private Direction? _pendingZoneExitDirection;

    // Track committed exploration target to prevent switching
    private int? _committedExplorationZone;
    private Direction? _committedExplorationDirection;
    private int _zoneExitAttempts;

    private void MoveToZoneEdge(Direction dir)
    {
        if (_state.CurrentZone == null) return;

        int targetX = _state.PlayerX;
        int targetY = _state.PlayerY;

        switch (dir)
        {
            case Direction.Up:
                targetY = 1;
                break;
            case Direction.Down:
                targetY = _state.CurrentZone.Height - 2;
                break;
            case Direction.Left:
                targetX = 1;
                break;
            case Direction.Right:
                targetX = _state.CurrentZone.Width - 2;
                break;
        }

        _pendingZoneExitDirection = dir;

        bool nearEdge = dir switch
        {
            Direction.Up => _state.PlayerY <= 2,
            Direction.Down => _state.PlayerY >= _state.CurrentZone.Height - 3,
            Direction.Left => _state.PlayerX <= 2,
            Direction.Right => _state.PlayerX >= _state.CurrentZone.Width - 3,
            _ => false
        };

        if (nearEdge)
        {
            var connected = _worldGenerator.GetConnectedZone(_state.CurrentZoneId, dir);
            Console.WriteLine($"[BOT] At edge, walking {dir}. Connected zone: {connected?.ToString() ?? "none"}");
            OnActionRequested?.Invoke(BotActionType.Move, 0, 0, dir);
        }
        else
        {
            var nearest = _pathfinder.FindNearestWalkable(_state.CurrentZone, targetX, targetY, _state.ZoneNPCs, _state.CurrentZoneId);
            if (nearest.HasValue)
            {
                if (!_actions.MoveTo(nearest.Value.X, nearest.Value.Y))
                {
                    // Failed to find path - mark this zone exit as blocked
                    Console.WriteLine($"[BOT] No path to ({nearest.Value.X},{nearest.Value.Y}), marking exit {dir} blocked");
                    _solver.MarkExitBlocked(dir);
                    _pendingZoneExitDirection = null;
                }
            }
            else
            {
                Console.WriteLine($"[BOT] No walkable position near edge for {dir}, marking exit blocked");
                _solver.MarkExitBlocked(dir);
                _pendingZoneExitDirection = null;
            }
        }
    }

    private void UpdateStuckDetection(double deltaTime)
    {
        var currentPos = (_state.PlayerX, _state.PlayerY, _state.CurrentZoneId);

        // Zone changed - reset tracking
        if (currentPos.CurrentZoneId != _lastPosition.ZoneId)
        {
            Console.WriteLine($"[BOT] Zone changed: {_lastPosition.ZoneId} -> {currentPos.CurrentZoneId}");
            _solver.OnZoneChanged();
            _stuckCount = 0;
            _stuckTimer = 0;
            // Clear committed exploration target since we changed zones
            _committedExplorationZone = null;
            _committedExplorationDirection = null;
            _zoneExitAttempts = 0;

            // Block X-Wing positions in the new zone to prevent accidentally traveling back
            BlockXWingPositions();
        }

        if (currentPos == _lastPosition)
        {
            _stuckTimer += deltaTime;

            // If we're trying to exit a zone and are stuck, increment exit attempts
            if (_pendingZoneExitDirection.HasValue && _stuckTimer > 2.0)
            {
                _zoneExitAttempts++;
                if (_zoneExitAttempts >= MaxZoneExitAttempts)
                {
                    // Failed to exit zone multiple times - mark exit as temporarily blocked
                    Console.WriteLine($"[BOT] Failed to exit via {_pendingZoneExitDirection} after {_zoneExitAttempts} attempts, marking temporarily blocked");
                    _solver.MarkExitBlocked(_pendingZoneExitDirection.Value);
                    _pendingZoneExitDirection = null;
                    _committedExplorationZone = null;
                    _committedExplorationDirection = null;
                    _zoneExitAttempts = 0;
                    _stuckTimer = 0;
                    _currentState = BotState.ThinkingAboutObjective;
                    return;
                }
            }

            if (_stuckTimer > StuckThreshold && _currentState != BotState.Stuck)
            {
                Console.WriteLine("[BOT] Stuck detected");
                _currentState = BotState.Stuck;
                _actions.Cancel();
            }
        }
        else
        {
            _stuckTimer = 0;
            _lastPosition = currentPos;
        }
    }

    /// <summary>
    /// Blocks X-Wing positions to prevent accidentally triggering travel when walking.
    /// Uses permanent blocklist that survives zone changes.
    /// </summary>
    private void BlockXWingPositions()
    {
        if (_state.CurrentZone == null) return;

        foreach (var obj in _state.CurrentZone.Objects)
        {
            if (obj.Type == ZoneObjectType.XWingFromDagobah ||
                obj.Type == ZoneObjectType.XWingToDagobah)
            {
                Console.WriteLine($"[BOT] Permanently blocking X-Wing position at ({obj.X},{obj.Y})");
                _pathfinder.MarkPermanentlyBlocked(_state.CurrentZoneId, obj.X, obj.Y);
            }
        }
    }

    private Direction GetDirectionTo(int targetX, int targetY)
    {
        int dx = targetX - _state.PlayerX;
        int dy = targetY - _state.PlayerY;

        if (Math.Abs(dx) > Math.Abs(dy))
            return dx > 0 ? Direction.Right : Direction.Left;
        else
            return dy > 0 ? Direction.Down : Direction.Up;
    }

    private string GetNpcName(NPC npc)
    {
        if (npc.CharacterId < _gameData.Characters.Count)
        {
            var name = _gameData.Characters[npc.CharacterId].Name;
            if (!string.IsNullOrEmpty(name))
                return name;
        }
        return "Enemy";
    }
}

/// <summary>
/// Bot state machine states.
/// </summary>
public enum BotState
{
    Idle,
    ThinkingAboutObjective,
    ExecutingObjective,
    Combat,
    Exploring,
    Stuck
}
