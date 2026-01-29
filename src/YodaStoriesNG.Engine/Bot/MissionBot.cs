using YodaStoriesNG.Engine.Data;
using YodaStoriesNG.Engine.Game;

namespace YodaStoriesNG.Engine.Bot;

/// <summary>
/// Main bot controller - orchestrates mission completion.
/// Can automatically play through all 15 missions from start to finish.
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
    private int _explorationAttempts;

    // Timing constants
    private const double ThinkInterval = 0.2;    // 200ms between decisions
    private const double StuckThreshold = 5.0;   // Consider stuck after 5 seconds
    private const int MaxExplorationAttempts = 20;

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
                BotState.ExecutingObjective => _solver.GetCurrentObjective().Description,
                BotState.Combat => "Fighting enemy!",
                BotState.Exploring => "Exploring...",
                BotState.Stuck => "Stuck - trying alternatives",
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
        _explorationAttempts = 0;
        _lastPosition = (_state.PlayerX, _state.PlayerY, _state.CurrentZoneId);

        Console.WriteLine("[BOT] Started");
        LogMissionState();
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
        if (enemy != null && enemy.DistanceTo(_state.PlayerX, _state.PlayerY) <= 5)
        {
            Console.WriteLine($"[BOT] Enemy detected at ({enemy.X},{enemy.Y})!");
            _currentState = BotState.Combat;
            return;
        }

        // Check if game is won
        if (_state.IsGameWon)
        {
            Console.WriteLine("[BOT] Mission complete! Game won!");
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
        var objective = _solver.GetCurrentObjective();
        Console.WriteLine($"[BOT] Current objective: {objective.Type} - {objective.Description}");

        _currentState = BotState.ExecutingObjective;
        StartObjective(objective);
    }

    private void StartObjective(BotObjective objective)
    {
        switch (objective.Type)
        {
            case ObjectiveType.TalkToNpc:
                if (objective.TargetNpc != null)
                {
                    Console.WriteLine($"[BOT] Talking to NPC at ({objective.TargetNpc.X},{objective.TargetNpc.Y})");
                    _actions.TalkToNpc(objective.TargetNpc);
                }
                else
                {
                    _currentState = BotState.Exploring;
                }
                break;

            case ObjectiveType.UseItemOnNpc:
                if (objective.TargetNpc != null && objective.RequiredItemId.HasValue)
                {
                    Console.WriteLine($"[BOT] Using item {objective.RequiredItemId.Value} on NPC at ({objective.TargetNpc.X},{objective.TargetNpc.Y})");
                    _actions.UseItemOnNpc(objective.RequiredItemId.Value, objective.TargetNpc);
                }
                else
                {
                    _currentState = BotState.Exploring;
                }
                break;

            case ObjectiveType.PickupItem:
                Console.WriteLine($"[BOT] Picking up item at ({objective.TargetX},{objective.TargetY})");
                _actions.PickupItem(objective.TargetX, objective.TargetY);
                break;

            case ObjectiveType.ChangeZone:
                if (objective.TargetZoneId.HasValue)
                {
                    var dir = _solver.GetDirectionToAdjacentZone(objective.TargetZoneId.Value);
                    if (dir.HasValue)
                    {
                        Console.WriteLine($"[BOT] Moving to zone {objective.TargetZoneId.Value} ({dir.Value})");
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
                _actions.EnterDoor(objective.TargetX, objective.TargetY);
                break;

            case ObjectiveType.KillEnemy:
                _currentState = BotState.Combat;
                break;

            case ObjectiveType.FindNpc:
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
            _actions.Reset();
            _currentState = BotState.ThinkingAboutObjective;
            _explorationAttempts = 0;
        }
        else if (!_actions.IsBusy)
        {
            // Action finished without completing - go back to thinking
            _currentState = BotState.ThinkingAboutObjective;
        }
    }

    private void HandleCombat()
    {
        // Rate limit
        if (_thinkTimer < ThinkInterval)
            return;
        _thinkTimer = 0;

        var enemy = _solver.FindNearestEnemy();
        if (enemy == null)
        {
            Console.WriteLine("[BOT] No enemies remaining");
            _currentState = BotState.ThinkingAboutObjective;
            return;
        }

        int dist = enemy.DistanceTo(_state.PlayerX, _state.PlayerY);

        if (dist <= 1)
        {
            // Adjacent - attack!
            var dir = GetDirectionTo(enemy.X, enemy.Y);
            Console.WriteLine($"[BOT] Attacking enemy at ({enemy.X},{enemy.Y})");
            _actions.Attack(dir);
        }
        else if (dist <= 5)
        {
            // Close - move toward enemy
            _actions.MoveToAdjacent(enemy.X, enemy.Y);
        }
        else
        {
            // Enemy too far, go back to normal objectives
            _currentState = BotState.ThinkingAboutObjective;
        }
    }

    private void HandleExploration()
    {
        if (_actions.IsBusy)
        {
            // Wait for current movement to complete
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

        _explorationAttempts++;
        if (_explorationAttempts > MaxExplorationAttempts)
        {
            Console.WriteLine("[BOT] Too many exploration attempts, changing zone");
            TryChangeZone();
            _explorationAttempts = 0;
            return;
        }

        // Check if there's anything useful in current zone first
        var friendlyNpc = _solver.FindNearestFriendlyNpc();
        if (friendlyNpc != null)
        {
            Console.WriteLine($"[BOT] Found friendly NPC at ({friendlyNpc.X},{friendlyNpc.Y})");
            _actions.TalkToNpc(friendlyNpc);
            _currentState = BotState.ExecutingObjective;
            return;
        }

        // Look for unexplored doors
        var door = _solver.FindUnexploredDoor();
        if (door != null)
        {
            Console.WriteLine($"[BOT] Found unexplored door at ({door.X},{door.Y})");
            _actions.EnterDoor(door.X, door.Y);
            _currentState = BotState.ExecutingObjective;
            return;
        }

        // Try moving to an unexplored connected zone
        var unexploredZones = _solver.GetUnexploredConnectedZones();
        if (unexploredZones.Count > 0)
        {
            var targetZone = unexploredZones[_random.Next(unexploredZones.Count)];
            var dir = _solver.GetDirectionToAdjacentZone(targetZone);
            if (dir.HasValue)
            {
                Console.WriteLine($"[BOT] Exploring toward zone {targetZone}");
                MoveToZoneEdge(dir.Value);
                _currentState = BotState.ExecutingObjective;
                return;
            }
        }

        // Random exploration within current zone
        ExploreRandomly();
    }

    private void HandleStuck()
    {
        // Rate limit
        if (_thinkTimer < ThinkInterval * 2)
            return;
        _thinkTimer = 0;

        Console.WriteLine("[BOT] Attempting to get unstuck");

        // Try random movement
        var directions = new[] { Direction.Up, Direction.Down, Direction.Left, Direction.Right };
        var dir = directions[_random.Next(directions.Length)];

        OnActionRequested?.Invoke(BotActionType.Move, 0, 0, dir);

        _stuckTimer = 0;
        _currentState = BotState.ThinkingAboutObjective;
    }

    private void TryChangeZone()
    {
        // Try to change zones - either through a door or edge transition
        var door = _solver.FindUnexploredDoor();
        if (door != null)
        {
            _actions.EnterDoor(door.X, door.Y);
            _currentState = BotState.ExecutingObjective;
            return;
        }

        // Try edge transition
        var unexploredZones = _solver.GetUnexploredConnectedZones();
        if (unexploredZones.Count > 0)
        {
            var targetZone = unexploredZones[_random.Next(unexploredZones.Count)];
            var dir = _solver.GetDirectionToAdjacentZone(targetZone);
            if (dir.HasValue)
            {
                MoveToZoneEdge(dir.Value);
                _currentState = BotState.ExecutingObjective;
                return;
            }
        }

        // Last resort - random direction
        var directions = new[] { Direction.Up, Direction.Down, Direction.Left, Direction.Right };
        MoveToZoneEdge(directions[_random.Next(directions.Length)]);
        _currentState = BotState.ExecutingObjective;
    }

    private void ExploreRandomly()
    {
        if (_state.CurrentZone == null) return;

        // Pick a random walkable position
        int attempts = 0;
        while (attempts++ < 20)
        {
            int x = _random.Next(1, _state.CurrentZone.Width - 1);
            int y = _random.Next(1, _state.CurrentZone.Height - 1);

            if (_pathfinder.IsWalkable(_state.CurrentZone, x, y, _state.ZoneNPCs))
            {
                Console.WriteLine($"[BOT] Random exploration to ({x},{y})");
                _actions.MoveTo(x, y);
                _currentState = BotState.ExecutingObjective;
                return;
            }
        }

        // Couldn't find walkable position, try changing zones
        TryChangeZone();
    }

    private void MoveToZoneEdge(Direction dir)
    {
        if (_state.CurrentZone == null) return;

        int targetX = _state.PlayerX;
        int targetY = _state.PlayerY;

        switch (dir)
        {
            case Direction.Up:
                targetY = 0;
                break;
            case Direction.Down:
                targetY = _state.CurrentZone.Height - 1;
                break;
            case Direction.Left:
                targetX = 0;
                break;
            case Direction.Right:
                targetX = _state.CurrentZone.Width - 1;
                break;
        }

        // Find nearest walkable position at edge
        var nearest = _pathfinder.FindNearestWalkable(_state.CurrentZone, targetX, targetY, _state.ZoneNPCs);
        if (nearest.HasValue)
        {
            _actions.MoveTo(nearest.Value.X, nearest.Value.Y);
        }
    }

    private void UpdateStuckDetection(double deltaTime)
    {
        var currentPos = (_state.PlayerX, _state.PlayerY, _state.CurrentZoneId);

        if (currentPos == _lastPosition)
        {
            _stuckTimer += deltaTime;
            if (_stuckTimer > StuckThreshold && _currentState != BotState.Stuck)
            {
                Console.WriteLine("[BOT] Detected stuck condition");
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

    private Direction GetDirectionTo(int targetX, int targetY)
    {
        int dx = targetX - _state.PlayerX;
        int dy = targetY - _state.PlayerY;

        if (Math.Abs(dx) > Math.Abs(dy))
            return dx > 0 ? Direction.Right : Direction.Left;
        else
            return dy > 0 ? Direction.Down : Direction.Up;
    }

    private void LogMissionState()
    {
        var world = _worldGenerator.CurrentWorld;
        if (world?.Mission != null)
        {
            var mission = world.Mission;
            Console.WriteLine($"[BOT] Mission: {mission.Name}");
            Console.WriteLine($"[BOT] Planet: {mission.Planet}");
            Console.WriteLine($"[BOT] Puzzle steps: {mission.PuzzleChain.Count}");
            Console.WriteLine($"[BOT] Current step: {mission.CurrentStep + 1}");
        }
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
