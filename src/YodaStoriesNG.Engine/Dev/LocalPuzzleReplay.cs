using System.Diagnostics;
using System.Text.Json;
using YodaStoriesNG.Engine.Bot;
using YodaStoriesNG.Engine.Data;
using YodaStoriesNG.Engine.Parsing;

namespace YodaStoriesNG.Engine.Game;

public unsafe partial class GameEngine
{
    private sealed record PuzzleSearchNode(byte[] Snapshot, string Key, int Parent, LocalPuzzleStep? Step, int Depth);
    private string? _puzzleDataFingerprint;
    private Random? _puzzleReplayRandom;
    private Random NpcRandom => _puzzleReplayRandom ?? _random;
    private readonly Dictionary<string,LocalPuzzleResult> _localPuzzleCache=new();

    /// <summary>Compile a local solution in a private engine. No speculative action touches the live game.</summary>
    private LocalPuzzleResult PlanLocalPuzzle(LocalPuzzleGoal goal) => SearchLocalPuzzle(goal,192,TimeSpan.FromSeconds(1.5));

    internal LocalPuzzleResult SearchLocalPuzzle(LocalPuzzleGoal goal,int maxStates,TimeSpan budget)
    {
        var clock=Stopwatch.StartNew();
        if(_gameData==null || _worldGenerator?.CurrentWorld is not { } world || _state.CurrentZone==null)
            return new(LocalPuzzleStatus.Unsupported,"A generated world and an initialized entry are required.",0,clock.Elapsed.TotalSeconds);
        int[] scope=LocalPuzzleState.Scope(world,goal.Sector,_gameData);
        if(!scope.Contains(_state.CurrentZoneId))
            return new(LocalPuzzleStatus.Unsupported,"Route to a real entry of the puzzle family first.",0,clock.Elapsed.TotalSeconds);
        if(LocalPuzzleState.Satisfies(goal,_state,world))
            return new(LocalPuzzleStatus.AlreadySatisfied,"The requested postcondition already holds.",0,clock.Elapsed.TotalSeconds);
        _puzzleDataFingerprint??=PuzzleCatalogue.Compile(_gameData).DataFingerprint;
        string initial=LocalPuzzleState.Signature(_gameData,_state,world,scope,returns:_doorReturnPoints);
        string cacheKey=$"{_puzzleDataFingerprint}:{goal}:{initial}";
        if(_localPuzzleCache.TryGetValue(cacheKey,out var cached)) return cached;

        // Replays are synchronous. Keep their engine diagnostics out of the
        // user's action stream; only their verified result is logged below.
        var output=Console.Out;
        LocalPuzzleResult result;
        try
        {
            Console.SetOut(TextWriter.Null);
            using var sandbox=CreatePuzzleSandbox(scope);
            var loaded=ReadLocalPlan(initial,goal,scope,_puzzleDataFingerprint);
            result=loaded!=null && sandbox.VerifyPuzzlePlan(loaded,clock,budget)
                ? new(LocalPuzzleStatus.Verified,"Cached plan independently replayed against this checkpoint.",0,clock.Elapsed.TotalSeconds,loaded)
                : sandbox.SearchPuzzleFamily(goal,scope,_puzzleDataFingerprint,maxStates,budget,clock);
        }
        finally { Console.SetOut(output); }
        _localPuzzleCache[cacheKey]=result;
        // Bound memory across long sessions and regenerated worlds.
        if(_localPuzzleCache.Count>256) _localPuzzleCache.Remove(_localPuzzleCache.Keys.First());
        _bot?.TraceDebug("local-puzzle-plan",new { Goal=goal,result.Status,result.Detail,result.Expanded,result.Seconds,
            Steps=result.Plan?.Steps.Length,InitialState=initial,result.Deferred });
        if(result.Plan is { ReplayVerified:true } plan && _bot?.DebugLogDirectory is { } directory)
        {
            string folder=Path.Combine(directory,"local-plans"); Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder,$"{initial}.json"),JsonSerializer.Serialize(plan,LocalPuzzleState.Json));
        }
        return result;
    }

    private static LocalPuzzlePlan? ReadLocalPlan(string initial,LocalPuzzleGoal goal,int[] scope,string fingerprint)
    {
        string? directory=Environment.GetEnvironmentVariable("YSNG_PUZZLE_PLAN_DIR");
        if(string.IsNullOrWhiteSpace(directory)) return null;
        string path=Path.Combine(directory,$"{initial}.json");
        try
        {
            if(!File.Exists(path) || new FileInfo(path).Length>8*1024*1024) return null;
            var plan=JsonSerializer.Deserialize<LocalPuzzlePlan>(File.ReadAllText(path),LocalPuzzleState.Json);
            return plan is {SchemaVersion:1,ReplayVerified:true,Steps.Length:>0 and <=4096} &&
                plan.Goal==goal && plan.DataFingerprint==fingerprint && plan.InitialState==initial &&
                plan.Scope.SequenceEqual(scope) ? plan : null;
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or JsonException) {return null;}
    }

    private bool VerifyPuzzlePlan(LocalPuzzlePlan plan,Stopwatch? clock=null,TimeSpan? budget=null)
    {
        var world=_worldGenerator!.CurrentWorld!;
        var start=CapturePuzzleState(plan.Scope);int zone=_state.CurrentZoneId,x=_state.PlayerX,y=_state.PlayerY;
        try
        {
            foreach(var step in plan.Steps)
                if(clock!=null && budget.HasValue && clock.Elapsed>=budget.Value || step.Zone!=_state.CurrentZoneId ||
                    LocalPuzzleState.Signature(_gameData!,_state,world,plan.Scope,returns:_doorReturnPoints)!=step.Before ||
                    !ReplayPuzzleInstruction(step.Instruction,plan.Scope) ||
                    LocalPuzzleState.Signature(_gameData!,_state,world,plan.Scope,returns:_doorReturnPoints)!=step.After) return false;
            return LocalPuzzleState.Satisfies(plan.Goal,_state,world) &&
                (!plan.Goal.ReturnToEntry || plan.Goal.Kind is LocalPuzzleGoalKind.Win or LocalPuzzleGoalKind.ReachCell ||
                    _state.CurrentZoneId==zone && _state.PlayerX==x && _state.PlayerY==y);
        }
        finally {RestorePuzzleState(start);}
    }

    private GameEngine CreatePuzzleSandbox(int[] scope)
    {
        var data=new GameData();
        foreach(var p in typeof(GameData).GetProperties().Where(p=>p.CanRead && p.CanWrite)) p.SetValue(data,p.GetValue(_gameData));
        data.Zones=_gameData!.Zones.Select(CloneArea).ToList();
        var original=_worldGenerator!.CurrentWorld!;
        var world=new WorldMap();
        foreach(var p in typeof(WorldMap).GetProperties().Where(p=>p.CanRead && p.CanWrite)) p.SetValue(world,p.GetValue(original));
        world.Grid=original.Grid==null ? null : (int?[,])original.Grid.Clone();
        world.TypeMap=original.TypeMap==null ? null : (SectorType[,])original.TypeMap.Clone();
        world.OrderMap=original.OrderMap==null ? null : (int[,])original.OrderMap.Clone();
        world.Mission=JsonSerializer.Deserialize<Mission>(JsonSerializer.Serialize(original.Mission,CheckpointOptions),CheckpointOptions);
        world.DagobahZones=new(original.DagobahZones);world.RequiredItems=new(original.RequiredItems);
        world.Connections=original.Connections.ToDictionary(p=>p.Key,p=>new ZoneConnections {
            ZoneId=p.Value.ZoneId,North=p.Value.North,South=p.Value.South,East=p.Value.East,West=p.Value.West });
        world.RoomParents=new(original.RoomParents);
        world.RoomConnections=original.RoomConnections.ToDictionary(p=>p.Key,p=>new List<int>(p.Value));
        var sectors=original.Sectors.Values.Distinct().ToDictionary(s=>s,s=>JsonSerializer.Deserialize<Sector>(
            JsonSerializer.Serialize(s,CheckpointOptions),CheckpointOptions)!);
        foreach(var p in original.Sectors) world.Sectors[p.Key]=sectors[p.Value];
        var engine=new GameEngine("") { _gameData=data,_headless=true,_showingTitleScreen=false,
            _smoothMovement=_state.SmoothMovementEnabled,_smoothMovementRequested=_state.SmoothMovementEnabled,
            _worldGenerator=new WorldGenerator(data),_puzzleReplayRandom=new Random(0) };
        engine._worldGenerator.SetCurrentWorld(world);
        engine._sectorPuzzles=new SectorPuzzles(data,engine._state,world);
        engine._sectorPuzzles.OnSpeak+=(speaker,text)=>engine._messages.ShowDialogue(speaker,text);
        engine.InitializeActionExecutor();
        engine._actionExecutor!.CurrentWorld=world;engine._actionExecutor.CurrentMission=world.Mission;
        engine.RestorePuzzleState(CapturePuzzleState(scope));
        return engine;
    }

    private byte[] CapturePuzzleState(int[] scope)
    {
        var world=_worldGenerator!.CurrentWorld!;
        return JsonSerializer.SerializeToUtf8Bytes(new BotCheckpoint {
            State=_state,Seed=world.Seed ?? 0,Size=world.GenerationSize,
            LaidDown=_sectorPuzzles?.CheckpointLaidDown() ?? [],
            SolvedSectors=world.Sectors.Where(p=>p.Value.Solved).Select(p=>p.Key).ToArray(),
            MissionStep=world.Mission?.CurrentStep ?? 0,MissionCompleted=world.Mission?.IsCompleted ?? false,
            DoorReturns=new(_doorReturnPoints),Areas=scope.Select(id=>new CheckpointArea {
                Id=id,Tiles=_gameData!.Zones[id].TileGrid.Cast<ushort>().ToArray(),
                Objects=_gameData.Zones[id].Objects,Monsters=_gameData.Zones[id].Monsters }).ToList()
        },CheckpointOptions);
    }

    private void RestorePuzzleState(byte[] snapshot)
    {
        RestoreBotCheckpoint(JsonSerializer.Deserialize<BotCheckpoint>(snapshot,CheckpointOptions)!);
        _npcSteps.Clear();_pendingMovementAction=null;_playerWalkingTimer=0;
        _puzzleReplayRandom=new Random(0);
        _actionExecutor!.SuppressDialogue=false;
    }

    private bool LocalActorsAreDynamic() => _state.ZoneNPCs.Any(n=>n.IsEnabled && n.IsAlive &&
        (n.IsHostile || n.Behavior is not (NPCBehavior.Stationary or NPCBehavior.Animation)));

    private LocalPuzzleRequirement[] LocalActorRequirements(BotObjective? instruction=null) => _state.ZoneNPCs
        .Where(n=>n.IsEnabled && n.IsAlive && (n.IsHostile || n.Behavior is not (NPCBehavior.Stationary or NPCBehavior.Animation)))
        .Select(n=>new LocalPuzzleRequirement(n.IsHostile ? "LiveCombat" : "MovingActor",_state.CurrentZoneId,
            n.NativeMonsterIndex,n.CharacterId,n.X,n.Y,n.Health,
            instruction==null ? "Present at this entry; spatial routing/combat must be resolved live before local replay." :
                "Observed after a candidate interaction; this is a deferred branch, not proof that every route requires this encounter.",instruction)).ToArray();

    private LocalPuzzleResult SearchPuzzleFamily(LocalPuzzleGoal goal,int[] scope,string fingerprint,
        int maxStates,TimeSpan budget,Stopwatch clock)
    {
        var world=_worldGenerator!.CurrentWorld!;
        string Key()=>LocalPuzzleState.Signature(_gameData!,_state,world,scope,returns:_doorReturnPoints);
        var start=CapturePuzzleState(scope);string initial=Key();
        int entryZone=_state.CurrentZoneId,entryX=_state.PlayerX,entryY=_state.PlayerY;
        bool Complete()=>LocalPuzzleState.Satisfies(goal,_state,world) &&
            (!goal.ReturnToEntry || goal.Kind is LocalPuzzleGoalKind.Win or LocalPuzzleGoalKind.ReachCell ||
                _state.CurrentZoneId==entryZone && _state.PlayerX==entryX && _state.PlayerY==entryY);
        if(LocalActorsAreDynamic()) return new(LocalPuzzleStatus.NeedsLiveActors,
            "This entry contains live combat or moving actors; resolve that prerequisite in the live game first.",0,clock.Elapsed.TotalSeconds,
            Deferred:LocalActorRequirements());
        if(scope.SelectMany(id=>_gameData!.Zones[id].Actions).Any(a=>
            a.Conditions.Any(c=>!Enum.IsDefined(c.Opcode) || c.Opcode==ConditionOpcode.Unused) ||
            a.Instructions.Any(i=>!Enum.IsDefined(i.Opcode))))
            return new(LocalPuzzleStatus.Unsupported,"This puzzle family contains unsupported opcodes.",0,clock.Elapsed.TotalSeconds);
        var nodes=new List<PuzzleSearchNode> { new(start,initial,-1,null,0) };
        var seen=new HashSet<string> {initial};var queue=new PriorityQueue<int,double>();queue.Enqueue(0,0);
        int expanded=0;bool actorGate=false,cutoff=false;
        var deferred=new Dictionary<string,LocalPuzzleRequirement>();
        while(queue.TryDequeue(out int at,out _))
        {
            if(expanded>=maxStates || clock.Elapsed>=budget) {cutoff=true;break;}
            var node=nodes[at];RestorePuzzleState(node.Snapshot);expanded++;
            int departure=_state.CurrentZoneId;
            var candidates=PuzzleCandidates(scope,goal).ToList();
            if(goal.ReturnToEntry && LocalPuzzleState.Satisfies(goal,_state,world) && _state.CurrentZoneId==entryZone)
                candidates.Insert(0,new BotObjective {Type=ObjectiveType.WalkToPosition,TargetX=entryX,TargetY=entryY,
                    Description="Return to the verified puzzle entry"});
            foreach(var candidate in candidates)
            {
                if(clock.Elapsed>=budget) {cutoff=true;break;}
                RestorePuzzleState(node.Snapshot);
                if(!ReplayPuzzleInstruction(candidate,scope)) continue;
                if(_state.IsGameOver || !_state.HeroVisible && !_state.IsGameWon) continue;
                string after=Key();if(after==node.Key || !seen.Add(after)) continue;
                var step=new LocalPuzzleStep(departure,candidate,node.Key,after);
                var next=new PuzzleSearchNode(CapturePuzzleState(scope),after,at,step,node.Depth+1);
                int index=nodes.Count;nodes.Add(next);
                // Live actor outcomes depend on AI/combat timing. A script that
                // activates one is a prerequisite, not an invented offline win.
                if(LocalActorsAreDynamic())
                {
                    actorGate=true;
                    foreach(var requirement in LocalActorRequirements(candidate))
                        deferred[$"{requirement.Zone}:{requirement.Actor}:{requirement.X}:{requirement.Y}:{candidate.Type}:{candidate.TargetX}:{candidate.TargetY}"]=requirement;
                    continue;
                }
                // Once the reward is held, try the required retreat directly.
                // Do not expand unrelated switches and furniture first. A
                // failed retreat restores this branch and leaves search intact.
                if(goal.ReturnToEntry && goal.Kind is LocalPuzzleGoalKind.AcquireItem or LocalPuzzleGoalKind.SolveSector &&
                    LocalPuzzleState.Satisfies(goal,_state,world) && _state.CurrentZoneId==entryZone && !Complete())
                {
                    var retreat=new BotObjective {Type=ObjectiveType.WalkToPosition,TargetX=entryX,TargetY=entryY,
                        Description="Return to the verified puzzle entry"};
                    if(ReplayPuzzleInstruction(retreat,scope) && !LocalActorsAreDynamic() && Complete())
                    {
                        var returned=new PuzzleSearchNode(CapturePuzzleState(scope),Key(),index,
                            new(entryZone,retreat,after,Key()),next.Depth+1);
                        index=nodes.Count;nodes.Add(returned);
                    }
                    else RestorePuzzleState(next.Snapshot);
                }
                if(Complete())
                {
                    var steps=new List<LocalPuzzleStep>();
                    for(int i=index;nodes[i].Parent>=0;i=nodes[i].Parent) steps.Add(nodes[i].Step!);
                    steps.Reverse();RestorePuzzleState(start);
                    // A second, independent engine replay certifies the complete
                    // sequence, including scripts encountered while walking.
                    foreach(var replay in steps)
                        if(Key()!=replay.Before || !ReplayPuzzleInstruction(replay.Instruction,scope) || Key()!=replay.After)
                            return new(LocalPuzzleStatus.ReplayMismatch,"The proposed sequence did not reproduce its state contracts.",expanded,clock.Elapsed.TotalSeconds);
                    if(!Complete())
                        return new(LocalPuzzleStatus.ReplayMismatch,"The final postcondition did not reproduce.",expanded,clock.Elapsed.TotalSeconds);
                    return new(LocalPuzzleStatus.Verified,"Every step and the final postcondition replayed through the headless engine.",
                        expanded,clock.Elapsed.TotalSeconds,new(1,fingerprint,goal,scope,initial,steps.ToArray(),true));
                }
                // Prefer progress toward the requested pickup without turning
                // a heuristic into a proof. All accepted plans still replay.
                double heuristic=goal.Kind==LocalPuzzleGoalKind.ReachCell && _state.CurrentZoneId==goal.Zone
                    ? (Math.Abs(_state.PlayerX-goal.X)+Math.Abs(_state.PlayerY-goal.Y))*0.1 : 0;
                queue.Enqueue(index,next.Depth+heuristic);
            }
            if(cutoff) break;
        }
        return new(cutoff ? LocalPuzzleStatus.SearchLimit : actorGate ? LocalPuzzleStatus.NeedsLiveActors : LocalPuzzleStatus.NoPlan,
            cutoff ? "The bounded local search needs a larger offline budget or another puzzle-family solver." :
            actorGate ? "Reachable scripts activate live actors; that prerequisite is left to the live executor." :
            "No plan using the supported local interactions was found; this is not a proof of impossibility.",expanded,clock.Elapsed.TotalSeconds,
            Deferred:deferred.Values.ToArray());
    }

    private IEnumerable<BotObjective> PuzzleCandidates(int[] scope,LocalPuzzleGoal goal)
    {
        var zone=_state.CurrentZone!;var sector=_worldGenerator!.CurrentWorld!.Sectors.GetValueOrDefault(zone.Id);
        var candidates=new List<BotObjective>();
        void Add(ObjectiveType type,int x,int y,int? item=null,int? script=null,Direction? dir=null,int? destination=null) =>
            candidates.Add(new() {Type=type,TargetX=x,TargetY=y,RequiredItemId=item,ScriptIndex=script,
                Direction=dir,TargetZoneId=destination,Description=$"Local puzzle: {type} at ({x},{y})"});
        foreach(var (action,index) in zone.Actions.Select((a,i)=>(a,i)))
        {
            if(_state.DisabledActions.Contains((zone.Id,index))) continue;
            foreach(var condition in action.Conditions)
            {
                var a=condition.Arguments;
                if(a.Count<2 || a[0]<0 || a[1]<0 || a[0]>=zone.Width || a[1]>=zone.Height) continue;
                switch(condition.Opcode)
                {
                    case ConditionOpcode.Bump: Add(ObjectiveType.BumpTarget,a[0],a[1],script:index);break;
                    case ConditionOpcode.StandingOn:
                    case ConditionOpcode.HeroIsAt: Add(ObjectiveType.WalkToPosition,a[0],a[1],script:index);break;
                    case ConditionOpcode.PlacedItemIs when a.Count>=5:
                        int item=a[4]<0 ? sector?.RequiredItem ?? -1 : a[4];
                        if(item>=0 && _state.HasItem(item)) Add(ObjectiveType.UseItemOnNpc,a[0],a[1],item,index);
                        break;
                }
            }
        }
        foreach(var h in zone.Objects.Where(h=>h.Enabled))
        {
            if(h.Type is ZoneObjectType.DoorEntrance or ZoneObjectType.DoorExit)
            {
                int target=h.Argument==ushort.MaxValue ? _doorReturnPoints.GetValueOrDefault(zone.Id).Zone : h.Argument;
                if(scope.Contains(target)) Add(ObjectiveType.EnterDoor,h.X,h.Y,destination:target);
            }
            if(h.Type is ZoneObjectType.SpawnLocation or ZoneObjectType.PuzzleNPC or ZoneObjectType.Lock &&
                sector is {RequiredItem:>=0} && _state.HasItem(sector.RequiredItem))
                Add(ObjectiveType.UseItemOnNpc,h.X,h.Y,sector.RequiredItem);
        }
        for(int y=0;y<zone.Height;y++) for(int x=0;x<zone.Width;x++)
        {
            int t=zone.GetTile(x,y,1);if(t>=_gameData!.Tiles.Count) continue;
            if(_gameData.Tiles[t].IsItem) Add(ObjectiveType.PickupItem,x,y);
            if(_gameData.Tiles[t].IsDraggable)
                foreach(var direction in Enum.GetValues<Direction>())
                {Add(ObjectiveType.PushObject,x,y,dir:direction);Add(ObjectiveType.PullObject,x,y,dir:direction);}
        }
        if(goal.Kind==LocalPuzzleGoalKind.ReachCell && goal.Zone==zone.Id) Add(ObjectiveType.WalkToPosition,goal.X,goal.Y);
        var rewardCells=zone.Objects.Where(h=>h.Enabled && h.Argument==goal.Item &&
            h.Type is ZoneObjectType.CrateItem or ZoneObjectType.CrateWeapon or ZoneObjectType.Trigger)
            .Select(h=>(h.X,h.Y)).ToHashSet();
        return candidates.DistinctBy(c=>(c.Type,c.TargetX,c.TargetY,c.RequiredItemId,c.Direction,c.TargetZoneId))
            .OrderBy(c=>goal.Kind==LocalPuzzleGoalKind.AcquireItem &&
                (rewardCells.Contains((c.TargetX,c.TargetY)) ||
                 c.Type==ObjectiveType.PickupItem && zone.GetTile(c.TargetX,c.TargetY,1)==goal.Item) ? 0 : 1);
    }

    private bool ReplayPuzzleInstruction(BotObjective objective,int[] scope)
    {
        var nav=new Pathfinder(_gameData!,_state) {AllowDiagonal=true, PreferCardinal=true,
            AdditionalWalkability=(z,x,y)=>!IsGroundBlocked(z,x,y) && !IsScriptedActorTile(z,x,y)};
        var actions=new BotActions(_state,_gameData!,nav) {MovementPending=BotActionPending};
        bool relocated=false;int beforeZone=_state.CurrentZoneId;
        System.Action onRelocated=()=>relocated=true;
        _actionExecutor!.OnHeroRelocated+=onRelocated;
        actions.OnAction+=HandleBotAction;
        try
        {
            bool started=objective.Type switch {
                ObjectiveType.WalkToPosition=>actions.WalkToPosition(objective.TargetX,objective.TargetY),
                ObjectiveType.PickupItem=>actions.PickupItem(objective.TargetX,objective.TargetY),
                ObjectiveType.BumpTarget=>actions.BumpAt(objective.TargetX,objective.TargetY,
                    stance:objective.ApproachX is int sx && objective.ApproachY is int sy ? (sx,sy) : null),
                ObjectiveType.UseItemOnNpc when objective.RequiredItemId is int item=>actions.UseItemOnNpc(item,
                    new NPC {X=objective.TargetX,Y=objective.TargetY,Health=1,IsEnabled=true}),
                ObjectiveType.EnterDoor=>actions.EnterDoor(objective.TargetX,objective.TargetY),
                ObjectiveType.PushObject=>actions.PushObject(objective.TargetX,objective.TargetY,objective.Direction!.Value),
                ObjectiveType.PullObject=>actions.PullObject(objective.TargetX,objective.TargetY,objective.Direction!.Value),
                _=>false };
            if(!started) return false;
            for(int ticks=0;ticks<1200;ticks++)
            {
                if(_messages.HasDialogue) _messages.DismissDialogue();
                Update(1.0/60);
                if(!scope.Contains(_state.CurrentZoneId)) return false;
                if(relocated || _state.CurrentZoneId!=beforeZone) actions.Cancel();
                if(!relocated && _state.CurrentZoneId==beforeZone) actions.Update(1.0/60);
                if(!actions.IsBusy && !BotActionPending() && !_actionExecutor.HasDeferredDrops && !_state.IsAttacking)
                    return true;
                if(_state.IsGameWon) return true;
                if(_state.IsGameOver) return false;
            }
            return false;
        }
        finally { _actionExecutor.OnHeroRelocated-=onRelocated; }
    }

    public static int SolvePuzzleOffline(string dataFile,string checkpointFile,string output,LocalPuzzleGoal goal,
        int maxStates=3000,int seconds=30)
    {
        using var engine=new GameEngine("") {_headless=true,_showingTitleScreen=false,_gameData=new DtaParser().Parse(dataFile)};
        var checkpoint=ReadBotCheckpoint(checkpointFile,dataFile);
        Environment.SetEnvironmentVariable("YSNG_SEED",checkpoint.Seed.ToString(System.Globalization.CultureInfo.InvariantCulture));
        engine._selectedWorldSize=checkpoint.Size;engine.CaptureOriginalAreas();engine.InitializeActionExecutor();engine.StartNewGame();
        engine.RestoreBotCheckpoint(checkpoint);
        var result=engine.SearchLocalPuzzle(goal,maxStates,TimeSpan.FromSeconds(seconds));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output,JsonSerializer.Serialize(result,LocalPuzzleState.Json));
        if(result.Plan is {ReplayVerified:true} plan)
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!,$"{plan.InitialState}.json"),
                JsonSerializer.Serialize(plan,LocalPuzzleState.Json));
        Console.WriteLine($"Local puzzle: {result.Status}; {result.Expanded} states; {result.Seconds:F2}s. {result.Detail}");
        Console.WriteLine($"Plan/report: {Path.GetFullPath(output)}");
        return result.Status is LocalPuzzleStatus.Verified or LocalPuzzleStatus.AlreadySatisfied ? 0 : 1;
    }
}
