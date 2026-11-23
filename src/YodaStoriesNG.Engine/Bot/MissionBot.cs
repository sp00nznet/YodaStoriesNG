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
    private BotDebugLog? _debug;
    private string? _debugState;
    public string? DebugLogDirectory => _debug?.DirectoryPath;
    public string? DebugLogRoot { get; set; }
    public string? ObjectiveDescription => _currentObjective?.Description;
    public void TraceDebug(string kind,object? detail) => _debug?.Record(kind,detail);
    private void DebugSnapshot(string reason,bool force=false) => _debug?.Snapshot(reason,force,false,
        new { State=_currentState,Objective=_currentObjective,Route=_solver.PlannedRoute,
            Graph=_solver.ActionGraph,NativePlanning=_solver.NativePlanningState,
            StuckRetries=_stuckCount,NoProgressSeconds=_noMissionProgressTime });

    // Bot state
    private bool _isRunning;
    private BotState _currentState;
    private double _thinkTimer;
    private double _stuckTimer;
    private (int X, int Y, int ZoneId) _lastPosition;
    private int _stuckCount;
    private double _noMissionProgressTime;
    private readonly HashSet<string> _seenProgressStates = new();
    private string? _progressObjectiveKey;
    private int _bestObjectiveDistance = int.MaxValue;
    private double _planningSeconds;

    // Planning runs synchronously between frames. The engine excludes that CPU
    // time from the next gameplay update, including NPCs and inactivity timers.
    public double ConsumePlanningSeconds()
    {
        double elapsed = _planningSeconds;
        _planningSeconds = 0;
        return elapsed;
    }

    // Current objective tracking
    private BotObjective? _currentObjective;
    private NPC? _lastTargetNpc;
    private NPC? _routeBlocker;
    private int _routeBlockerZone = -1;
    private int? _lastItemUsed;
    private string? _itemUseWorldState;
    private (int Zone,int X,int Y,string State)? _doorAttempt;
    private (int Zone,int Action,int X,int Y,string State,string SemanticState)? _scriptAttempt;
    private (int Zone,int X,int Y,int ArrivalX,int ArrivalY,string State,string SemanticState)? _passageAttempt;

    private string ItemUseWorldState()
    {
        var zone=_state.CurrentZone;
        string tiles=zone==null ? "" : string.Join(',',Enumerable.Range(0,3).SelectMany(layer=>
            Enumerable.Range(0,zone.Height).SelectMany(y=>Enumerable.Range(0,zone.Width)
                .Select(x=>zone.GetTile(x,y,layer)))));
        return $"{_state.CurrentZoneId}:{_state.GetCounter(_state.CurrentZoneId)}:{_state.GetSectorCounter(_state.CurrentZoneId)}:"+
            $"{tiles}:disabled={string.Join(',',_state.DisabledActions.Where(a=>a.Zone==_state.CurrentZoneId).OrderBy(a=>a.Action))}:"+
            $"vars={string.Join(',',_state.Variables.OrderBy(p=>p.Key).Select(p=>$"{p.Key}={p.Value}"))}:"+
            $"inventory={string.Join(',',_state.Inventory.OrderBy(i=>i))}";
    }

    // Timing constants
    private const double ThinkInterval = 0.15;    // 150ms between decisions
    private const double StuckThreshold = 5.0;    // Consider stuck after 5 seconds (was 3)
    private const int MaxStuckRetries = 10;       // More retries before giving up (was 5)
    private const int MaxZoneExitAttempts = 10;   // Actual boundary requests, never frames spent approaching.


    // Events
    public event Action<BotActionType, int, int, Direction>? OnActionRequested;

    public MissionBot(GameState state, GameData gameData, WorldGenerator worldGenerator, Func<bool>? movementPending = null,
        Func<Zone, int, int, bool>? canWalk = null, Func<LocalPuzzleGoal,LocalPuzzleResult>? localPlanner=null)
    {
        _state = state;
        _gameData = gameData;
        _worldGenerator = worldGenerator;
        _pathfinder = new Pathfinder(gameData,state) { AdditionalWalkability = canWalk };
        _actions = new BotActions(state, gameData, _pathfinder) { MovementPending = movementPending };
        _solver = new MissionSolver(state, gameData, worldGenerator,canWalk);
        _solver.LocalPlanner=localPlanner;

        _actions.OnRouteBlockedByEnemy += enemy =>
        {
            _routeBlocker = enemy;
            _routeBlockerZone = _state.CurrentZoneId;
            _actions.Cancel();
            ClearPendingExit();
            _currentState = BotState.Combat;
            Console.WriteLine($"[BOT] Enemy blocks route at ({enemy.X},{enemy.Y}); clearing it first");
        };

        // Wire up action events
        _actions.OnAction += (type, x, y, dir) =>
        {
            _debug?.Record("action-request",new { Type=type,X=x,Y=y,Direction=dir });
            OnActionRequested?.Invoke(type, x, y, dir);
            _debug?.Record("action-result",new { Type=type,X=x,Y=y });
        };
        _actions.Trace = (kind,detail) =>
        {
            _debug?.Record(kind,detail);
            if(kind is "path-failed" or "move-rejected") DebugSnapshot(kind);
        };

        _currentState = BotState.Idle;
    }

    /// <summary>
    /// Whether the bot is currently running.
    /// </summary>
    public bool IsRunning => _isRunning;

    public WorldActionGraph? ActionGraph => _solver.ActionGraph;
    public IReadOnlyList<WorldActionGraph.Edge> PlannedRoute => _solver.PlannedRoute;

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

        _debug?.Dispose();
        _debug = new BotDebugLog(_gameData,_state,_worldGenerator,DebugLogRoot);
        _debugState = null;
        _isRunning = true;
        _currentState = BotState.ThinkingAboutObjective;
        _thinkTimer = 0;
        _stuckTimer = 0;
        _stuckCount = 0;
        _lastPosition = (_state.PlayerX, _state.PlayerY, _state.CurrentZoneId);
        ClearPendingExit();
        _noMissionProgressTime = 0;
        _seenProgressStates.Clear();
        _progressObjectiveKey=null;
        _bestObjectiveDistance=int.MaxValue;
        _solver.Reset();
        _scriptedRelocation=false;
        _routeBlocker = null;


        Console.WriteLine("[BOT] Started - Mission planner");
        Console.WriteLine("[BOT] Will follow mission dependencies and planned routes");
    }

    /// <summary>
    /// Stops the bot.
    /// </summary>
    public void Stop()
    {
        if (!_isRunning) return;

        DebugSnapshot("bot-stop",true);
        _debug?.Record("session-stop",new { State=_currentState,NoProgressSeconds=_noMissionProgressTime });
        _debug?.Dispose();
        _isRunning = false;
        _currentState = BotState.Idle;
        _actions.Cancel();
        ClearPendingExit();

        Console.WriteLine("[BOT] Stopped");
    }

    /// <summary>
    /// Main update method - call each frame.
    /// </summary>
    private bool _scriptedRelocation;
    internal void NotifyScriptedRelocation() => _scriptedRelocation=true;

    public void Update(double deltaTime)
    {
        if (!_isRunning)
            return;
        if(_scriptedRelocation)
        {
            _scriptedRelocation=false;
            _actions.Cancel();
            ClearPendingExit();
            _debug?.Record("scripted-path-invalidated",new { _state.CurrentZoneId,_state.PlayerX,_state.PlayerY });
        }
        if(_state.IsGameWon)
        {
            Console.WriteLine("[BOT] Authored victory reached");
            Stop();
            return;
        }
        // Tile-step execution can navigate authored diagonal corridors without
        // enabling interpolation. Prefer cardinal routes, but do not erase
        // diagonal-only connections from the world graph.
        _pathfinder.AllowDiagonal=true;
        _pathfinder.PreferCardinal=true;

        string debugState = $"{_currentState}:{_state.CurrentZoneId}:{_state.PlayerX},{_state.PlayerY}:" +
            string.Join(',',_state.Inventory) + ":" + _state.Health;
        if(debugState != _debugState)
        {
            _debugState = debugState;
            _debug?.Record("state",new { State=_currentState,Task=CurrentTask,NoProgressSeconds=_noMissionProgressTime });
        }

        string progress = string.Join(',',_state.Inventory.OrderBy(i => i)) + "|" +
            string.Join(',',_state.SolvedZones.OrderBy(i => i)) + "|" +
            string.Join(',',_state.VisitedZones.OrderBy(i => i)) + "|" + _state.DisabledActions.Count + "|" +
            string.Join(';',_state.Counters.OrderBy(p=>p.Key).Select(p=>$"{p.Key}={p.Value}"))+"|"+
            string.Join(';',_state.Variables.OrderBy(p=>p.Key).Select(p=>$"{p.Key}={p.Value}"))+"|"+
            string.Join(';',_state.ZoneRandom.OrderBy(p=>p.Key).Select(p=>$"{p.Key}={p.Value}"))+"|"+
            (_state.CurrentZone is { } progressZone ? $"{progressZone.Id}:{_state.PlayerX},{_state.PlayerY}:{BlockPuzzlePlanner.Layout(progressZone)}:" +
                $"{_state.GetCounter(progressZone.Id)}:{_state.GetSectorCounter(progressZone.Id)}:" +
                string.Join(',',progressZone.Actions.SelectMany(a=>a.Conditions).Select(c=>
                    c.Opcode==ConditionOpcode.StandingOn && c.Arguments.Count>=3 ? (X:(int)c.Arguments[0],Y:(int)c.Arguments[1],Layer:0) :
                    c.Opcode is ConditionOpcode.TileAtIs or ConditionOpcode.IsVariable && c.Arguments.Count>=4 ?
                        (X:(int)c.Arguments[1],Y:(int)c.Arguments[2],Layer:(int)c.Arguments[3]) : (X:-1,Y:-1,Layer:-1))
                    .Where(p=>p.Item1>=0 && p.Item2>=0 && p.Item3 is >=0 and <3).Distinct()
                    .Select(p=>progressZone.GetTile(p.Item1,p.Item2,p.Item3))) : "") + "|" +
            string.Join(';',_state.ZoneNPCs.Where(n => n.IsHostile).Select(n => $"{n.CharacterId},{n.StartX},{n.StartY},{n.Health}"));
        bool progressStateChanged=_seenProgressStates.Add(progress);
        bool objectiveCloser=TrackObjectiveProgress();
        if (progressStateChanged || objectiveCloser) { _noMissionProgressTime = 0; _stuckCount = 0; }
        else _noMissionProgressTime += deltaTime;
        if (_noMissionProgressTime >= 45)
        {
            Console.WriteLine($"[BOT] Stopped: 45 seconds without new route or mission progress in zone {_state.CurrentZoneId}. Last objective: {_currentObjective?.Description}");
            Stop();
            return;
        }

        // Discard the old route before a queued step can run in a different zone.
        _thinkTimer += deltaTime;
        // Record the transition while its source instruction is still available.
        // Clearing a completed scripted passage first hides round trips from
        // the route planner's cycle detector.
        UpdateStuckDetection(deltaTime);
        if(_currentObjective is { Type:ObjectiveType.GuideActor,TargetNpc:{ } guided } guiding &&
            (guided.X!=guiding.TargetX || guided.Y!=guiding.TargetY || !guided.IsEnabled || !guided.IsAlive))
        {
            _actions.Cancel(); _currentObjective=null;
            _currentState=BotState.ThinkingAboutObjective;
        }
        if (_currentObjective is { Type: ObjectiveType.TraversePassage } passage &&
            _state.CurrentZoneId == passage.TargetZoneId &&
            _state.PlayerX == passage.ArrivalX && _state.PlayerY == passage.ArrivalY)
        {
            _debug?.Record("passage-arrived", new { Objective = passage });
            if(_passageAttempt is { } attempted)
            {
                bool sameZone=_state.CurrentZoneId==attempted.Zone;
                bool exactNoProgress=sameZone &&
                    _solver.CapturePassageProgressState(attempted.Zone)==attempted.State;
                bool repeatedNoSemanticProgress=sameZone &&
                    _solver.CapturePassageSemanticState(attempted.Zone)==attempted.SemanticState &&
                    _solver.RecordUnchangedPassageAttempt(attempted.Zone,attempted.X,attempted.Y,attempted.SemanticState);
                if(exactNoProgress || repeatedNoSemanticProgress)
                {
                    _solver.MarkPassageFailed(attempted.Zone,attempted.X,attempted.Y,
                        _solver.CapturePassageProgressState(attempted.Zone));
                    Console.WriteLine($"[BOT] Passage at ({attempted.X},{attempted.Y}) did not advance the mission; deferring it until the area state changes");
                }
                _passageAttempt=null;
            }
            _actions.Reset();
            _currentObjective = null;
            _currentState = BotState.ThinkingAboutObjective;
        }
        _actions.Update(deltaTime);

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

    private bool TrackObjectiveProgress()
    {
        var objective=_currentObjective;
        if(objective==null)
        {
            _progressObjectiveKey=null;
            _bestObjectiveDistance=int.MaxValue;
            return false;
        }
        bool edgeObjective=objective.Type is ObjectiveType.ChangeZone or ObjectiveType.TraversePassage &&
            !objective.CrossingX.HasValue && !objective.ApproachX.HasValue;
        int targetX=objective.ApproachX ?? objective.CrossingX ?? objective.TargetX;
        int targetY=objective.ApproachY ?? objective.CrossingY ?? objective.TargetY;
        string key=$"{_state.CurrentZoneId}:{objective.Type}:{objective.TargetZoneId}:"+
            (edgeObjective ? $"edge:{objective.Direction}" : $"{targetX},{targetY}")+ $":{objective.Direction}:{objective.Description}";
        if(key!=_progressObjectiveKey)
        {
            _progressObjectiveKey=key;
            _bestObjectiveDistance=int.MaxValue;
        }
        int distance=edgeObjective ? objective.Direction switch {
            Direction.Up=>_state.PlayerY,Direction.Down=>(_state.CurrentZone?.Height ?? 1)-1-_state.PlayerY,
            Direction.Left=>_state.PlayerX,Direction.Right=>(_state.CurrentZone?.Width ?? 1)-1-_state.PlayerX,
            _=>int.MaxValue } : Math.Abs(_state.PlayerX-targetX)+Math.Abs(_state.PlayerY-targetY);
        if(distance>=_bestObjectiveDistance) return false;
        _bestObjectiveDistance=distance;
        return true;
    }

    private void Think()
    {
        // Rate limit thinking
        if (_thinkTimer < ThinkInterval)
            return;
        _thinkTimer = 0;

        // Defend against an adjacent hostile; all other combat comes from the
        // mission prerequisite or the actual enemy obstructing a planned route.
        const int threat = 1;
        var enemy = _solver.FindNearestEnemy();
        if (enemy != null && Math.Max(Math.Abs(enemy.X - _state.PlayerX), Math.Abs(enemy.Y - _state.PlayerY)) <= threat)
        {
            Console.WriteLine($"[BOT] Enemy detected: {GetNpcName(enemy)} at ({enemy.X},{enemy.Y})!");
            _routeBlocker = enemy;
            _routeBlockerZone = _state.CurrentZoneId;
            _actions.Cancel();
            ClearPendingExit();
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
        var planning = System.Diagnostics.Stopwatch.StartNew();
        try { _currentObjective = _solver.GetCurrentObjective(); }
        finally
        {
            planning.Stop();
            _planningSeconds += planning.Elapsed.TotalSeconds;
        }
        _debug?.Record("planning-finished", new { Seconds=planning.Elapsed.TotalSeconds,
            Objective=_currentObjective,NativePlanning=_solver.NativePlanningState });
        if (_solver.ActionGraph is { } graph) graph.NextInstruction = _currentObjective;
        Console.WriteLine($"[BOT] Objective: {_currentObjective.Type} - {_currentObjective.Description}");

        _currentState = BotState.ExecutingObjective;
        StartObjective(_currentObjective);
    }

    private void StartObjective(BotObjective objective)
    {
        // Normal route planning should not enter unrelated floor triggers.
        // Explicit trigger objectives remain reachable because BotActions marks
        // their requested cell as TransitionTarget while planning the approach.
        _pathfinder.AvoidFloorSwitches=true;
        // An approach belongs only to the objective that created it.
        _pendingZoneExitDirection = null;
        _debug?.Record("objective",new { Objective=objective,Route=_solver.PlannedRoute });
        switch (objective.Type)
        {
            case ObjectiveType.WaitForActor:
                _thinkTimer = 0;
                _actions.Reset();
                break;
            case ObjectiveType.TraversePassage:
                _passageAttempt=(_state.CurrentZoneId,objective.TargetX,objective.TargetY,
                    objective.ArrivalX ?? objective.TargetX,objective.ArrivalY ?? objective.TargetY,
                    _solver.CapturePassageProgressState(_state.CurrentZoneId),
                    _solver.CapturePassageSemanticState(_state.CurrentZoneId));
                if (!(objective.RequiredItemId is int passageItem
                    ? _actions.UseItemOnNpc(passageItem,new NPC { X=objective.TargetX,Y=objective.TargetY,IsEnabled=true,Health=1 })
                    : objective.PassageRequiresBump
                    ? _actions.BumpAt(objective.TargetX, objective.TargetY, useTransition: true,
                        stance:objective.ApproachX is int sx && objective.ApproachY is int sy ? (sx,sy) : null)
                    : _actions.WalkToPosition(objective.TargetX, objective.TargetY, useTransition: true)))
                {
                    _passageAttempt=null;
                    _solver.MarkUnreachable(objective.TargetX, objective.TargetY);
                    _currentState = BotState.ThinkingAboutObjective;
                }
                break;
            case ObjectiveType.PlanningBlocked:
                Console.WriteLine($"[BOT] No executable plan: {objective.Description}");
                _debug?.Snapshot("planning-blocked",true,true,new { Objective=objective,Route=_solver.PlannedRoute });
                Stop();
                break;

            case ObjectiveType.UnresolvedReward:
                Console.WriteLine($"[BOT] Reward state needs investigation: {objective.Description}");
                _debug?.Snapshot("unresolved-reward",true,true,new { Objective=objective,Route=_solver.PlannedRoute });
                Stop();
                break;

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
                if (objective.RequiredItemId.HasValue)
                {
                    // Authored item targets can be map objects rather than NPCs.
                    // Coordinate-only replay instructions use the same live handler.
                    var target=objective.TargetNpc ?? new NPC {
                        X=objective.TargetX,Y=objective.TargetY,IsEnabled=true,Health=1 };
                    _lastTargetNpc = target;
                    _lastItemUsed = objective.RequiredItemId;
                    _itemUseWorldState = ItemUseWorldState();
                    Console.WriteLine($"[BOT] Using item {objective.RequiredItemId} at ({target.X},{target.Y})");
                    if (!_actions.UseItemOnNpc(objective.RequiredItemId.Value, target))
                    {
                        // Can't reach NPC
                        _solver.MarkUnreachable(target.X, target.Y);
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
                    MoveToZoneEdge(objective.Direction.Value, objective.CrossingX, objective.CrossingY);
                }
                else if (objective.TargetZoneId.HasValue)
                {
                    // Try direct adjacency first, then pathfind if needed
                    var dir = _solver.GetDirectionToAdjacentZone(objective.TargetZoneId.Value);
                    if (!dir.HasValue)
                    {
                        // Not directly adjacent - use pathfinding to find next step
                        dir = _solver.GetDirectionTowardZone(objective.TargetZoneId.Value);
                    }

                    if (dir.HasValue)
                    {
                        Console.WriteLine($"[BOT] Navigating toward zone {objective.TargetZoneId} via {dir.Value}");
                        MoveToZoneEdge(dir.Value);
                    }
                    else
                    {
                        Console.WriteLine($"[BOT] Cannot find path to zone {objective.TargetZoneId}");
                        _currentState = BotState.Exploring;
                    }
                }
                break;

            case ObjectiveType.UseXWing:
                Console.WriteLine("[BOT] Using X-Wing to travel");
                _actions.UseXWing();
                break;

            case ObjectiveType.AttackTile:
                if(objective.ApproachX is int aimX && objective.ApproachY is int aimY &&
                    (_state.PlayerX!=aimX || _state.PlayerY!=aimY))
                {
                    if(!_actions.MoveTo(aimX,aimY))
                    { _solver.MarkUnreachable(aimX,aimY); _currentState=BotState.ThinkingAboutObjective; }
                    break;
                }
                if(objective.RequiredItemId is int weapon && _state.HasItem(weapon) && _state.CurrentZone is { } attackZone &&
                    ScriptedWeaponUse.CanAim(_gameData,attackZone,weapon,_state.PlayerX,_state.PlayerY,objective.TargetX,objective.TargetY))
                {
                    _state.SelectedWeapon=weapon;
                    var direction=objective.TargetX<_state.PlayerX ? Direction.Left : objective.TargetX>_state.PlayerX ? Direction.Right :
                        objective.TargetY<_state.PlayerY ? Direction.Up : Direction.Down;
                    _actions.Attack(direction);
                }
                else _currentState=BotState.ThinkingAboutObjective;
                break;

            case ObjectiveType.BumpTarget:
                if(objective.ScriptIndex is int scriptIndex)
                    _scriptAttempt=(_state.CurrentZoneId,scriptIndex,objective.TargetX,objective.TargetY,
                    _solver.CaptureRouteState(_state.CurrentZoneId),
                    _solver.CapturePassageSemanticState(_state.CurrentZoneId));
                if (!_actions.BumpAt(objective.TargetX, objective.TargetY, useTransition: objective.TargetZoneId.HasValue,
                    stance:objective.ApproachX is int bumpX && objective.ApproachY is int bumpY ? (bumpX,bumpY) : null))
                {
                    _scriptAttempt=null;
                    _solver.MarkUnreachable(objective.TargetX, objective.TargetY);
                    _currentState = BotState.ThinkingAboutObjective;
                }
                break;

            case ObjectiveType.EnterDoor:
                int entranceCover=_state.CurrentZone?.GetTile(objective.TargetX,objective.TargetY,1) ?? ushort.MaxValue;
                if (entranceCover<_gameData.Tiles.Count && _gameData.Tiles[entranceCover].IsDraggable)
                {
                    _solver.MarkUnreachable(objective.TargetX,objective.TargetY);
                    _currentState=BotState.ThinkingAboutObjective;
                    break;
                }
                Console.WriteLine($"[BOT] Entering door at ({objective.TargetX},{objective.TargetY})");
                _solver.MarkDoorEntered(objective.TargetX, objective.TargetY);
                int doorZone=_state.CurrentZoneId;
                string doorState=_solver.CaptureRouteState(doorZone);
                if (!_actions.EnterDoor(objective.TargetX, objective.TargetY))
                {
                    _solver.MarkUnreachable(objective.TargetX, objective.TargetY);
                    _currentState = BotState.ThinkingAboutObjective;
                }
                else _doorAttempt=(doorZone,objective.TargetX,objective.TargetY,doorState);
                break;

            case ObjectiveType.KillEnemy:
                _routeBlocker = objective.TargetNpc;
                _routeBlockerZone = _state.CurrentZoneId;
                _currentState = BotState.Combat;
                break;

            case ObjectiveType.GuideActor:
                if(objective.ApproachX is int gx && objective.ApproachY is int gy)
                    _actions.MoveTo(gx,gy);
                else _currentState=BotState.ThinkingAboutObjective;
                break;

            case ObjectiveType.PushObject:
            case ObjectiveType.PullObject:
                bool pull = objective.Type == ObjectiveType.PullObject;
                Console.WriteLine($"[BOT] {(pull ? "Pulling" : "Pushing")} block at ({objective.TargetX},{objective.TargetY}) {objective.Direction}");
                if (objective.Direction is not { } pushDir ||
                    !(pull ? _actions.PullObject(objective.TargetX, objective.TargetY, pushDir)
                           : _actions.PushObject(objective.TargetX, objective.TargetY, pushDir)))
                {
                    _solver.AbandonBlockPlan();
                    _solver.MarkUnreachable(objective.TargetX, objective.TargetY);
                    _currentState = BotState.ThinkingAboutObjective;
                }
                break;

            case ObjectiveType.WalkToPosition:
                // Walk directly to a position (e.g., to collect R2D2 after talking)
                Console.WriteLine($"[BOT] Walking to position ({objective.TargetX},{objective.TargetY})");
                // Native/local puzzle plans retain the index of the script
                // that makes this exact cell meaningful. Mark it as the
                // intended transition so route safety does not reject its
                // StandingOn trigger while rebuilding the path at execution.
                if(objective.ScriptIndex is not null) objective.ScriptedInteraction=true;
                if(objective.ScriptIndex is int walkScriptIndex)
                    _scriptAttempt=(_state.CurrentZoneId,walkScriptIndex,objective.TargetX,objective.TargetY,
                        _solver.CaptureRouteState(_state.CurrentZoneId),
                        _solver.CapturePassageSemanticState(_state.CurrentZoneId));
                if (!_actions.WalkToPosition(objective.TargetX, objective.TargetY, useTransition:objective.ScriptedInteraction))
                {
                    _scriptAttempt=null;
                    _solver.MarkUnreachable(objective.TargetX, objective.TargetY);
                    _currentState = BotState.ThinkingAboutObjective;
                }
                break;

            case ObjectiveType.Explore:
            default:
                _currentState = BotState.Exploring;
                break;
        }
    }

    private void ExecuteObjective()
    {
        if(_currentObjective?.Type==ObjectiveType.GuideActor) return;
        if (_currentObjective?.Type == ObjectiveType.WaitForActor)
        {
            if (_thinkTimer >= 0.5) _currentState = BotState.ThinkingAboutObjective;
            return;
        }
        // Wait for the engine to resolve the boundary request before replanning.
        if (_zoneExitRequest.HasValue) return;
        if (_actions.IsCompleted)
        {
            if(_passageAttempt is { } passage)
            {
                bool sameArrival=_currentObjective is { Type:ObjectiveType.TraversePassage } &&
                    _state.CurrentZoneId==passage.Zone &&
                    _state.PlayerX==passage.ArrivalX && _state.PlayerY==passage.ArrivalY;
                bool exactNoProgress=sameArrival &&
                    _solver.CapturePassageProgressState(passage.Zone)==passage.State;
                bool repeatedNoSemanticProgress=sameArrival &&
                    _solver.CapturePassageSemanticState(passage.Zone)==passage.SemanticState &&
                    _solver.RecordUnchangedPassageAttempt(passage.Zone,passage.X,passage.Y,passage.SemanticState);
                if(exactNoProgress || repeatedNoSemanticProgress)
                {
                    _solver.MarkPassageFailed(passage.Zone,passage.X,passage.Y,
                        _solver.CapturePassageProgressState(passage.Zone));
                    Console.WriteLine($"[BOT] Passage at ({passage.X},{passage.Y}) returned to an unchanged area; deferring it until the puzzle state changes");
                }
                _passageAttempt=null;
            }
            if(_scriptAttempt is { } scripted)
            {
                if(_currentObjective is { Type:ObjectiveType.BumpTarget or ObjectiveType.WalkToPosition } completedScript &&
                    completedScript.ScriptIndex==scripted.Action &&
                    _state.CurrentZoneId==scripted.Zone &&
                    Math.Max(Math.Abs(_state.PlayerX-scripted.X),Math.Abs(_state.PlayerY-scripted.Y))<=1)
                {
                    bool exactNoProgress=_solver.CaptureRouteState(scripted.Zone)==scripted.State;
                    bool repeatedNoSemanticProgress=_solver.CapturePassageSemanticState(scripted.Zone)==scripted.SemanticState &&
                        _solver.RecordUnchangedScriptAttempt(scripted.Zone,scripted.Action,scripted.SemanticState);
                    if(exactNoProgress || repeatedNoSemanticProgress)
                    {
                        _solver.MarkScriptFailed(scripted.Zone,scripted.Action,
                            _solver.CaptureRouteState(scripted.Zone));
                        Console.WriteLine($"[BOT] Script {scripted.Action} in zone {scripted.Zone} caused no durable state change; deferring it until the area changes");
                        _noMissionProgressTime=0;
                        _lastPosition=(_state.PlayerX,_state.PlayerY,_state.CurrentZoneId);
                    }
                    else _solver.MarkScriptCompleted(scripted.Zone,scripted.Action);
                }
                _scriptAttempt=null;
            }
            if(_doorAttempt is { } attempted)
            {
                if(_currentObjective is { Type:ObjectiveType.EnterDoor } &&
                    _state.CurrentZoneId==attempted.Zone &&
                    Math.Max(Math.Abs(_state.PlayerX-attempted.X),Math.Abs(_state.PlayerY-attempted.Y))<=1 &&
                    _solver.CaptureRouteState(attempted.Zone)==attempted.State)
                {
                    _solver.MarkDoorFailed(attempted.X,attempted.Y,attempted.State);
                    Console.WriteLine($"[BOT] Door at ({attempted.X},{attempted.Y}) did not change the area; excluding it until the puzzle state changes");
                    _noMissionProgressTime=0;
                    _lastPosition=(_state.PlayerX,_state.PlayerY,_state.CurrentZoneId);
                }
                _doorAttempt=null;
            }
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
                            if (!_state.HasItem(_lastItemUsed.Value))
                            {
                                _solver.NoteConsumedItem(_lastItemUsed.Value);
                                _solver.FollowOpenedPassage(_lastItemUsed.Value, _currentObjective.TargetX, _currentObjective.TargetY);
                                if (_currentObjective.ExpectedRewardItemId is int reward && !_state.HasItem(reward))
                                {
                                    _solver.AwaitReward(reward);
                                    Console.WriteLine($"[BOT] Item {_lastItemUsed} consumed; reward {reward} not in inventory yet, looking for its drop");
                                }
                                else Console.WriteLine($"[BOT] Item {_lastItemUsed} consumed; replanning from actual inventory");
                            }
                            else if (_itemUseWorldState != ItemUseWorldState())
                            {
                                // A retained input can still complete a scripted
                                // offer. Remember the exact sector interaction so
                                // the planner follows its reward instead of
                                // triggering a repeatable placement script forever.
                                _solver.NoteConsumedItem(_lastItemUsed.Value);
                                _solver.FollowOpenedPassage(_lastItemUsed.Value, _currentObjective.TargetX, _currentObjective.TargetY);
                                if (_currentObjective.ExpectedRewardItemId is int reward && !_state.HasItem(reward))
                                {
                                    _solver.AwaitReward(reward);
                                    Console.WriteLine($"[BOT] Item {_lastItemUsed} retained; interaction changed the puzzle state, seeking reward {reward}");
                                }
                                else Console.WriteLine($"[BOT] Item {_lastItemUsed} retained; interaction changed the puzzle state, replanning");
                            }
                            else
                            {
                                // Repeating a retained-item interaction with no
                                // world-state change cannot make progress. Defer
                                // that sector/item pair and let the bot search
                                // for another route or prerequisite.
                                _solver.NoteConsumedItem(_lastItemUsed.Value);
                                _solver.FollowOpenedPassage(_lastItemUsed.Value,_currentObjective.TargetX,_currentObjective.TargetY);
                                _noMissionProgressTime=0;
                                Console.WriteLine($"[BOT] Item {_lastItemUsed} remained held and changed nothing; deferring this interaction while looking for another way forward");
                            }
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
                if (IsAtBoundary(_state.CurrentZone, dir))
                {
                    RequestZoneExit(dir);
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


    internal MissionSolver.ReplayIntent CaptureReplayIntent() => _solver.CaptureReplayIntent();
    internal void NoteScriptConsumedItem(int item) => _solver.NoteConsumedItem(item);
    internal void NoteDoorEntry(int room,int parent,int x,int y) => _solver.NoteDoorEntry(room,parent,x,y);
    internal void SyncDoorReturns(IReadOnlyDictionary<int,(int Zone,int X,int Y)> entries) => _solver.SyncDoorReturns(entries);
    internal void NoteScriptActionStarted(int zone,int action) => _solver.NoteScriptActionStarted(zone,action);

    internal void RestoreReplayIntent(MissionSolver.ReplayIntent intent) => _solver.RestoreReplayIntent(intent);

    private void HandleCombat()
    {
        // Rate limit
        if (_thinkTimer < ThinkInterval)
            return;
        _thinkTimer = 0;

        if (_routeBlocker != null && (_routeBlockerZone != _state.CurrentZoneId ||
            !_routeBlocker.IsEnabled || !_routeBlocker.IsAlive || !_routeBlocker.IsHostile || _routeBlocker.IsInvulnerable))
        {
            _routeBlocker = null;
            _actions.Cancel();
            _currentState = BotState.ThinkingAboutObjective;
            return;
        }
        var enemy = _routeBlocker;
        if (enemy == null)
        {
            Console.WriteLine("[BOT] All enemies defeated");
            _currentState = BotState.ThinkingAboutObjective;
            return;
        }

        // A scripted Force target can leave the stun weapon equipped. A kill
        // objective requires damage, so return to an owned damaging weapon.
        if(_state.SelectedWeapon is int selected && selected>=0 && selected<_gameData.Tiles.Count &&
            (_gameData.Tiles[selected].Flags & TileFlags.WeaponTheForce)!=0)
        {
            var damaging=_state.Inventory.Where(id=>id>=0 && id<_gameData.Tiles.Count &&
                _gameData.Tiles[id].IsWeapon && (_gameData.Tiles[id].Flags & TileFlags.WeaponTheForce)==0)
                .OrderByDescending(id=>(_gameData.Tiles[id].Flags & TileFlags.WeaponLightsaber)!=0).FirstOrDefault(-1);
            if(damaging>=0) _state.SelectedWeapon=damaging;
        }
        int dist = enemy.DistanceTo(_state.PlayerX, _state.PlayerY);

        // Scripted waves can replace a defeated body at Luke's exact cell.
        // A directional attack cannot hit the cell the attacker occupies.
        // First take a real path to an adjacent stance, then attack normally.
        if(dist==0)
        {
            if(!_actions.IsBusy) _actions.MoveToAdjacent(enemy.X,enemy.Y,allowDiagonalInteraction:true);
            return;
        }

        if (Math.Max(Math.Abs(enemy.X - _state.PlayerX), Math.Abs(enemy.Y - _state.PlayerY)) <= 1)
        {
            _actions.Cancel();
            _actions.Attack(GetDirectionTo(enemy.X, enemy.Y));
            return;
        }
        // Distance is not a failure: A* can approach any reachable target in this area.
        if (_actions.IsBusy) return;
        if (_actions.MoveToAdjacent(enemy.X, enemy.Y, allowDiagonalInteraction: true))
        {
            return;
        }
        if(_solver.PrepareEnemyApproach(enemy) is { } preparation)
        {
            _actions.Cancel();
            _currentObjective=preparation;
            _currentState=BotState.ExecutingObjective;
            StartObjective(preparation);
            return;
        }
        Console.WriteLine($"[BOT] No safe route to enemy at ({enemy.X},{enemy.Y}); deferring it until the map changes");
        _solver.MarkUnreachable(enemy.X, enemy.Y);
        _routeBlocker = null;
        _actions.Cancel();
        _currentState = BotState.ThinkingAboutObjective;
    }

    private void HandleExploration()
    {
        if (_actions.IsBusy || _thinkTimer < ThinkInterval) return;
        if(_solver.GetExplorationRecoveryObjective() is { } recovery)
        {
            _currentObjective=recovery;
            _currentState=BotState.ExecutingObjective;
            StartObjective(recovery);
            return;
        }
        Console.WriteLine($"[BOT] Stopped: no executable route for {_currentObjective?.Description}");
        _debug?.Snapshot("planning-blocked",true,true,new { Objective=_currentObjective,Route=_solver.PlannedRoute });
        Stop();
    }

    private void HandleStuck()
    {
        if (_thinkTimer < ThinkInterval * 2) return;
        _thinkTimer = 0;
        _stuckCount = Math.Min(MaxStuckRetries, _stuckCount + 1);
        DebugSnapshot("stuck");
        if (_stuckCount >= MaxStuckRetries)
        {
            Console.WriteLine($"[BOT] Stopped: no executable mission route after {MaxStuckRetries} replans in zone {_state.CurrentZoneId}. Last objective: {_currentObjective?.Description}");
            Stop();
            return;
        }
        // Recalculate from actual state; never wander or leave through an unrelated exit.
        _stuckTimer = 0;
        _currentState = BotState.ThinkingAboutObjective;
    }

    // Track the direction we're trying to exit
    private Direction? _pendingZoneExitDirection;

    private int _zoneExitAttempts;
    private (int Zone, int X, int Y, Direction Direction)? _zoneExitRequest;
    private double _zoneExitWait;
    private (int Zone, int X, int Y, Direction Direction)? _lastZoneExitAttempt;

    private void ClearPendingExit()
    {
        _pendingZoneExitDirection = null;
        _zoneExitRequest = null;
        _zoneExitWait = 0;
        _zoneExitAttempts = 0;
        _lastZoneExitAttempt = null;
    }

    private bool IsAtBoundary(Zone zone, Direction dir) => dir switch
    {
        Direction.Up => _state.PlayerY == 0,
        Direction.Down => _state.PlayerY == zone.Height - 1,
        Direction.Left => _state.PlayerX == 0,
        Direction.Right => _state.PlayerX == zone.Width - 1,
        _ => false
    };

    private void RequestZoneExit(Direction dir)
    {
        _pendingZoneExitDirection = null;
        _zoneExitRequest = (_state.CurrentZoneId, _state.PlayerX, _state.PlayerY, dir);
        if (_lastZoneExitAttempt != _zoneExitRequest) _zoneExitAttempts = 0;
        _lastZoneExitAttempt = _zoneExitRequest;
        _lastPosition = (_state.PlayerX, _state.PlayerY, _state.CurrentZoneId);
        _stuckTimer = 0;
        _zoneExitWait = 0;
        _zoneExitAttempts++;
        _debug?.Record("exit-request", new { Direction=dir,Attempt=_zoneExitAttempts,
            Destination=_worldGenerator.GetConnectedZone(_state.CurrentZoneId, dir) });
        Console.WriteLine($"[BOT] At boundary, walking {dir} (attempt {_zoneExitAttempts})");
        OnActionRequested?.Invoke(BotActionType.Move, 0, 0, dir);
    }

    /// <summary>
    /// Walks off the zone in <paramref name="dir"/>. The engine only lets you cross where the
    /// tile you leave from and the tile you would land on are both open, so pick the nearest
    /// such crossing on that edge, walk onto it, then step over.
    /// </summary>
    private void MoveToZoneEdge(Direction dir, int? crossingX = null, int? crossingY = null)
    {
        var zone = _state.CurrentZone;
        if (zone == null) return;
        _pendingZoneExitDirection = dir;

        // Nearest open crossing we can actually walk to; an edge is only "blocked" when none
        // is - and only after forgetting blocks left by monsters that have since moved.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            foreach (var (x, y) in FindCrossings(zone, dir))
            {
                if (crossingX.HasValue && crossingY.HasValue && (x != crossingX || y != crossingY)) continue;
                if (_state.PlayerX == x && _state.PlayerY == y)
                {
                    RequestZoneExit(dir);
                    return;
                }
                if (_actions.MoveTo(x, y))
                    return;
            }
            if (!_pathfinder.ForgetTemporaryBlocks())
                break;
        }

        Console.WriteLine($"[BOT] No reachable crossing {dir} out of zone {_state.CurrentZoneId}, marking exit blocked");
        _solver.MarkExitBlocked(dir);
        _pendingZoneExitDirection = null;
    }

    private IEnumerable<(int X, int Y)> FindCrossings(Zone zone, Direction dir)
    {
        var destId = _worldGenerator.GetConnectedZone(_state.CurrentZoneId, dir);
        if (destId is not { } id || id < 0 || id >= _gameData.Zones.Count) yield break;
        var dest = _gameData.Zones[id];

        bool vertical = dir is Direction.Up or Direction.Down;
        int length = vertical ? zone.Width : zone.Height;
        int here = vertical ? _state.PlayerX : _state.PlayerY;

        foreach (var i in Enumerable.Range(1, Math.Max(0, length - 2)).OrderBy(i => Math.Abs(i - here)))
        {
            var (from, to) = dir switch
            {
                Direction.Up => ((i, 0), (i, dest.Height - 1)),
                Direction.Down => ((i, zone.Height - 1), (i, 0)),
                Direction.Left => ((0, i), (dest.Width - 1, i)),
                _ => ((zone.Width - 1, i), (0, i)),
            };
            if (_pathfinder.IsWalkable(zone, from.Item1, from.Item2, _state.ZoneNPCs, _state.CurrentZoneId) &&
                _pathfinder.IsWalkable(dest, to.Item1, to.Item2))
                yield return from;
        }
    }

    private void UpdateStuckDetection(double deltaTime)
    {
        var currentPos = (_state.PlayerX, _state.PlayerY, _state.CurrentZoneId);

        // Zone changed - reset tracking
        if (currentPos.CurrentZoneId != _lastPosition.ZoneId)
        {
            Console.WriteLine($"[BOT] Zone changed: {_lastPosition.ZoneId} -> {currentPos.CurrentZoneId}");
                _debug?.Record("zone-change",new { From=_lastPosition.ZoneId,To=_state.CurrentZoneId });
            DebugSnapshot("zone-change",true);
            if(_currentObjective is { ArrivalX:int ax,ArrivalY:int ay } arrival &&
                arrival.TargetZoneId==_state.CurrentZoneId && (ax!=_state.PlayerX || ay!=_state.PlayerY))
                _debug?.Record("arrival-mismatch",new { ExpectedX=ax,ExpectedY=ay,
                    ActualX=_state.PlayerX,ActualY=_state.PlayerY,Objective=arrival });
            _solver.ObserveRouteTransition(_lastPosition.ZoneId,_lastPosition.X,_lastPosition.Y,_currentObjective);
            _solver.OnZoneChanged();
            _actions.Reset();
            if (_currentState == BotState.ExecutingObjective) _currentState = BotState.ThinkingAboutObjective;
            _stuckCount = 0;
            _stuckTimer = 0;
            ClearPendingExit();

        }

        // A scripted wait deliberately keeps Luke still. The mission-progress
        // watchdog still stops waits whose actor or script never advances.
        if (_currentObjective?.Type == ObjectiveType.WaitForActor || _actions.IsWaitingForFriendly)
        {
            _stuckTimer=0; _stuckCount=0; _lastPosition=currentPos;
            return;
        }

        if (currentPos == _lastPosition)
        {
            _stuckTimer += deltaTime;

            // Only a request issued at the boundary can fail. Approaching the
            // crossing, fighting, and in-flight movement are not exit attempts.
            if (_zoneExitRequest is { } request)
            {
                if (_actions.MovementPending?.Invoke() != true)
                    _zoneExitWait += deltaTime;
                if (_zoneExitWait < 2.0) return;
                _debug?.Record("exit-rejected", new { request.Direction,Attempt=_zoneExitAttempts,
                    WaitSeconds=_zoneExitWait });
                _zoneExitRequest = null;
                _zoneExitWait = 0;
                if (_zoneExitAttempts >= MaxZoneExitAttempts)
                {
                    Console.WriteLine($"[BOT] Failed to cross {request.Direction} after {_zoneExitAttempts} boundary requests, marking temporarily blocked");
                    _solver.MarkExitBlocked(request.Direction);
                    _zoneExitAttempts = 0;
                }
                _actions.Reset();
                _stuckTimer = 0;
                _currentState = BotState.ThinkingAboutObjective;
                return;
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
            _zoneExitRequest = null;
            _zoneExitWait = 0;
            _zoneExitAttempts = 0;
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
