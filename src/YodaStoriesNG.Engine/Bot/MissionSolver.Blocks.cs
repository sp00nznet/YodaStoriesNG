using YodaStoriesNG.Engine.Data;
using YodaStoriesNG.Engine.Game;

namespace YodaStoriesNG.Engine.Bot;

public partial class MissionSolver
{
    private readonly Queue<BlockPuzzlePlanner.Step> _blockPlan = new();
    private int _blockPlanZone = -1;
    private string? _failedBlockMap;
    private string? _blockPlanSearchKey;
    private string? _failedCombatApproach;
    private (int Zone,int X,int Y,int FromX,int FromY)? _blockWalkStart;

    public BotObjective? PrepareEnemyApproach(NPC enemy)
    {
        if(_state.CurrentZone is not { } zone) return null;
        string key=$"{zone.Id}:{BlockPuzzlePlanner.Layout(zone)}:{_state.PlayerX},{_state.PlayerY}:{enemy.X},{enemy.Y}";
        if(_failedCombatApproach==key) return null;
        // Do not plan a push into the enemy we are trying to reach. Reserve its
        // square while solving the surrounding furniture, then fight normally.
        var reserved=new NPC { X=enemy.X,Y=enemy.Y,Health=enemy.Health,IsEnabled=true,
            Behavior=NPCBehavior.Stationary,IsHostile=false };
        var simulation=new GameState { CurrentZone=zone,CurrentZoneId=zone.Id,
                    Counters=_state.Counters,Variables=_state.Variables,ZoneRandom=_state.ZoneRandom,
                    VisitedZones=_state.VisitedZones,DisabledActions=_state.DisabledActions,
            PlayerX=_state.PlayerX,PlayerY=_state.PlayerY,SmoothMovementEnabled=_state.SmoothMovementEnabled,
            ZoneNPCs=_state.ZoneNPCs.Where(n=>!ReferenceEquals(n,enemy)).Append(reserved).ToList() };
        var targets=Pathfinder.InteractionCells(enemy.X,enemy.Y,diagonal:true);
        var plan=BlockPuzzlePlanner.Plan(_gameData,zone,simulation,targets,Array.Empty<BlockPuzzlePlanner.Placement>());
        if(plan is not { Count: >0 } && enemy.Behavior==NPCBehavior.Chasing)
        {
            // A chasing actor can trap a block in a one-cell pocket. Approach
            // from a reachable side that makes it leave the block's landing
            // square, rather than repeatedly attempting an impossible attack.
            var nav=new Pathfinder(_gameData,_state) { AllowDiagonal=true, PreferCardinal=true };
            var others=_state.ZoneNPCs.Where(n=>!ReferenceEquals(n,enemy)).ToList();
            var exits=new[] { (-1,0),(1,0),(0,-1),(0,1) }
                .Where(d=>nav.IsWalkable(zone,enemy.X+d.Item1,enemy.Y+d.Item2,others,zone.Id)).ToArray();
            var lure=new List<(int X,int Y)>();
            for(int y=0;y<zone.Height;y++) for(int x=0;x<zone.Width;x++)
                if(Math.Max(Math.Abs(x-enemy.X),Math.Abs(y-enemy.Y))>1 &&
                    exits.Any(d=>d.Item1!=0 ? Math.Sign(x-enemy.X)==d.Item1 : Math.Sign(y-enemy.Y)==d.Item2))
                    lure.Add((x,y));
            foreach(var point in lure.OrderBy(p=>Math.Abs(p.X-_state.PlayerX)+Math.Abs(p.Y-_state.PlayerY)))
                if(nav.FindPath(zone,_state.PlayerX,_state.PlayerY,point.X,point.Y,_state.ZoneNPCs) is { Count: >0 })
                    return new BotObjective { Type=ObjectiveType.WalkToPosition,TargetX=point.X,TargetY=point.Y,
                        Description=$"Draw the blocking enemy toward ({point.X},{point.Y}) to free the object route" };
            if(lure.Count>0) plan=BlockPuzzlePlanner.Plan(_gameData,zone,simulation,lure,Array.Empty<BlockPuzzlePlanner.Placement>());
        }
        if(plan is not { Count: >0 }) { _failedCombatApproach=key; return null; }
        _blockPlan.Clear(); _blockPlanZone=zone.Id;
        foreach(var move in plan) _blockPlan.Enqueue(move);
        Console.WriteLine($"[BOT] Preparing a {plan.Count}-step object route to the blocking enemy at ({enemy.X},{enemy.Y})");
        return PlannedBlockMove(new BotObjective { Type=ObjectiveType.KillEnemy,TargetNpc=enemy,
            TargetX=enemy.X,TargetY=enemy.Y,Description="Open an approach to the blocking enemy" });
    }

    private BotObjective? PlannedBlockMove(BotObjective objective, bool continueOnly = false)
    {
        if (_state.CurrentZone is not { } zone) return null;
        string map = BlockPuzzlePlanner.Layout(zone);
        if (_blockPlanZone != zone.Id) { _blockPlan.Clear(); _failedBlockMap = null; _blockWalkStart=null; }
        _blockPlanZone = zone.Id;
        while (_blockPlan.TryPeek(out var step))
        {
            if (step.Walk && map == step.Before && (_state.PlayerX != step.X || _state.PlayerY != step.Y))
            {
                if(_blockWalkStart is { } issued && issued.Zone==zone.Id && issued.X==step.X && issued.Y==step.Y &&
                    (_state.PlayerX!=issued.FromX || _state.PlayerY!=issued.FromY) &&
                    zone.Actions.Any(a=>a.Conditions.Any(c=>c.Opcode==ConditionOpcode.StandingOn &&
                        c.Arguments.Count>=2 && c.Arguments[0]==step.X && c.Arguments[1]==step.Y) &&
                        a.Instructions.LastOrDefault(i=>i.Opcode==InstructionOpcode.MoveHeroTo && i.Arguments.Count>=2) is { } landing &&
                        landing.Arguments[0]==_state.PlayerX && landing.Arguments[1]==_state.PlayerY))
                {
                    _blockPlan.Dequeue(); _blockWalkStart=null; continue;
                }
                if(_blockWalkStart is not { } previous || previous.Zone!=zone.Id || previous.X!=step.X || previous.Y!=step.Y)
                    _blockWalkStart=(zone.Id,step.X,step.Y,_state.PlayerX,_state.PlayerY);
                return new BotObjective { Type = zone.Objects.Any(h=>h.Enabled && h.X==step.X && h.Y==step.Y &&
                    h.Type is ZoneObjectType.DoorEntrance or ZoneObjectType.DoorExit)
                        ? ObjectiveType.EnterDoor : ObjectiveType.WalkToPosition, TargetX = step.X, TargetY = step.Y,
                    // This is the solver's explicit destination, which may be
                    // an authored pressure plate or passage trigger. Preserve
                    // that intent when the executor rebuilds the walking path.
                    ScriptedInteraction = true,
                    Description = $"Finish block solution at ({step.X},{step.Y})" };
            }
            if (map == step.After)
            {
                if(!step.Walk)
                {
                    var (dx,dy)=step.Direction switch { Direction.Up=>(0,-1),Direction.Down=>(0,1),
                        Direction.Left=>(-1,0),_=>(1,0) };
                    int expectedX=step.X+(step.Pull?2*dx:0),expectedY=step.Y+(step.Pull?2*dy:0);
                    if(_state.PlayerX!=expectedX || _state.PlayerY!=expectedY)
                    {
                        // A push can trigger a bridge, trap or cutscene that
                        // relocates the hero without changing the final board.
                        // The remaining solution belongs to the old component.
                        _blockPlan.Clear();
                        break;
                    }
                }
                _blockPlan.Dequeue(); _blockWalkStart=null; continue;
            }
            if (map == step.Before)
            {
                var (dx,dy) = step.Direction switch { Direction.Up => (0,-1), Direction.Down => (0,1),
                    Direction.Left => (-1,0), _ => (1,0) };
                int blockTile=zone.GetTile(step.X,step.Y,1);
                int standX=step.X+(step.Pull?dx:-dx), standY=step.Y+(step.Pull?dy:-dy);
                int landingX=step.X+(step.Pull?2*dx:dx), landingY=step.Y+(step.Pull?2*dy:dy);
                var liveNav=new Pathfinder(_gameData,_state) { AllowDiagonal=true, PreferCardinal=true,
                    AdditionalWalkability=_canWalk };
                var stationaryActors=_state.ZoneNPCs.Where(n=>!n.IsHostile &&
                    n.Behavior is NPCBehavior.Stationary or NPCBehavior.Animation).ToList();
                bool canReachStance=liveNav.FindPath(zone,_state.PlayerX,_state.PlayerY,standX,standY,
                    _state.ZoneNPCs,_state.CurrentZoneId) is not null ||
                    liveNav.FindPath(zone,_state.PlayerX,_state.PlayerY,standX,standY,
                        stationaryActors,_state.CurrentZoneId) is not null;
                bool liveStepValid=blockTile<_gameData.Tiles.Count && _gameData.Tiles[blockTile].IsDraggable &&
                    canReachStance &&
                    landingX>=0 && landingY>=0 && landingX<zone.Width && landingY<zone.Height &&
                    (zone.GetTile(landingX,landingY,1)==ushort.MaxValue ||
                        zone.GetTile(landingX,landingY,1)<_gameData.Tiles.Count &&
                        !_gameData.Tiles[zone.GetTile(landingX,landingY,1)].IsObject);
                if(!liveStepValid)
                {
                    // Scripts can place an item, open a passage, or change a
                    // floor trigger after an earlier push. Do not keep following
                    // a plan whose next stance is no longer reachable.
                    _failedBlockMap=_blockPlanSearchKey;
                    _blockPlan.Clear(); _blockWalkStart=null;
                    return null;
                }
                var needed = new[] { (step.X+dx,step.Y+dy),
                    (step.X+(step.Pull?dx:-dx),step.Y+(step.Pull?dy:-dy)),
                    (step.X+(step.Pull?2*dx:0),step.Y+(step.Pull?2*dy:0)) };
                if (_state.ZoneNPCs.FirstOrDefault(n => n.IsEnabled && n.IsAlive && n.IsHostile &&
                    needed.Contains((n.X,n.Y))) is { } blocker)
                    return new BotObjective { Type=ObjectiveType.KillEnemy,TargetNpc=blocker,
                        TargetX=blocker.X,TargetY=blocker.Y,Description="Clear the enemy obstructing the object solution" };
                if (_state.ZoneNPCs.FirstOrDefault(n => n.IsEnabled && n.IsAlive && !n.IsHostile &&
                    needed.Contains((n.X,n.Y))) is { } friendly)
                    return new BotObjective { Type=ObjectiveType.WaitForActor,
                        TargetX=friendly.X,TargetY=friendly.Y,
                        Description="Wait for the friendly actor obstructing the object solution" };
                return new BotObjective { Type = step.Pull ? ObjectiveType.PullObject : ObjectiveType.PushObject,
                    TargetX = step.X, TargetY = step.Y, Direction = step.Direction,
                    Description = $"Execute block plan: {(step.Pull ? "pull" : "push")} ({step.X},{step.Y}) {step.Direction} ({_blockPlan.Count} moves left)" };
            }
            // A script, enemy or player changed the map: search again from reality.
            _blockPlan.Clear();
            _blockWalkStart=null;
        }
        if (continueOnly) return null;
        var searchKey = $"{map}:{_state.SmoothMovementEnabled}:{objective.Type}:{objective.TargetX}:{objective.TargetY}:{objective.TargetZoneId}:" +
            $"{_state.GetCounter(zone.Id)}:{_state.GetSectorCounter(zone.Id)}:{_state.ZoneRandom.GetValueOrDefault(zone.Id)}:"+
            $"disabled={string.Join(',',_state.DisabledActions.Where(a=>a.Zone==zone.Id).OrderBy(a=>a.Action).Select(a=>a.Action))}:"+
            string.Join(';', _state.ZoneNPCs.Where(n => n.IsEnabled && n.IsAlive).Select(n => $"{n.X},{n.Y}"));
        if ((objective.Type != ObjectiveType.ClearObject && !HuntEnemies && !_unreachablePositions.Contains((_state.CurrentZoneId, objective.TargetX, objective.TargetY))) ||
            _failedBlockMap == searchKey) return null;
        var targets = new List<(int X,int Y)>();
        int targetTile=zone.GetTile(objective.TargetX,objective.TargetY,1);
        bool closedDoor=targetTile<_gameData.Tiles.Count && _gameData.Tiles[targetTile].IsObject &&
            !_gameData.Tiles[targetTile].IsDraggable;
        bool clearingPassage=objective.Type==ObjectiveType.ClearObject &&
            ScriptedPassage.Instructions(zone,_state,gameType:_gameData.GameType).Any(p=>!p.PassageRequiresBump &&
                p.TargetX==objective.TargetX && p.TargetY==objective.TargetY);
        if (objective.Type == ObjectiveType.EnterDoor && !closedDoor || clearingPassage)
            targets.Add((objective.TargetX,objective.TargetY));
        else if(objective.Type==ObjectiveType.UseItemOnNpc)
            targets.AddRange(Pathfinder.InteractionCells(objective.TargetX,objective.TargetY,true,
                ItemUseReach.Extended(_gameData,objective.RequiredItemId ?? -1)));
        else if (objective.Type is ObjectiveType.TalkToNpc or ObjectiveType.UseItemOnNpc or ObjectiveType.BumpTarget or ObjectiveType.EnterDoor or ObjectiveType.ClearObject)
            targets.AddRange(new[]{(0,1),(0,-1),(1,0),(-1,0)}.Select(d=>(objective.TargetX+d.Item1,objective.TargetY+d.Item2)));
        else if (objective.Type is ObjectiveType.WalkToPosition or ObjectiveType.PickupItem)
            targets.Add((objective.TargetX,objective.TargetY));
        else if(objective.Type == ObjectiveType.ChangeZone)
        {
            if(objective.CrossingX is int crossingX && objective.CrossingY is int crossingY)
                targets.Add((crossingX,crossingY));
            var dir=objective.Direction ?? (objective.TargetZoneId is int target ? GetDirectionTowardZone(target) : null);
            for(int i=0;!objective.CrossingX.HasValue && i<Math.Max(zone.Width,zone.Height);i++)
            {
                if(dir==Direction.Up && i<zone.Width) targets.Add((i,0));
                if(dir==Direction.Down && i<zone.Width) targets.Add((i,zone.Height-1));
                if(dir==Direction.Left && i<zone.Height) targets.Add((0,i));
                if(dir==Direction.Right && i<zone.Height) targets.Add((zone.Width-1,i));
            }
        }
        if (targets.Count == 0) return null;
        var plan=BlockPuzzlePlanner.Plan(_gameData,zone,_state,targets,Array.Empty<BlockPuzzlePlanner.Placement>(),
            transitionTarget:objective.Type==ObjectiveType.EnterDoor || clearingPassage ? (objective.TargetX,objective.TargetY) : null,
            clearTiles:objective.Type==ObjectiveType.ClearObject ? new[] { (objective.TargetX,objective.TargetY) } : null);
        if(plan is not { Count: >0 })
        {
            // Authored pressure-plate puzzles describe their desired block arrangement
            // through TileAtIs conditions. Search all placements of one action together.
            foreach(var (action,index) in zone.Actions.Select((a,i)=>(a,i)))
            {
                if(_state.DisabledActions.Contains((zone.Id,index)) || !StateAllows(zone.Id,action)) continue;
                // A plate arrangement is useful only if its script supplies this
                // objective's reward or opens the objective's target cell.
                var sector = _worldGenerator.CurrentWorld?.Sectors.GetValueOrDefault(zone.Id);
                bool suppliesReward = objective.RequiredItemId is int reward && sector != null &&
                    ProvidesReward(action, reward, sector);
                bool opensTarget = action.Instructions.Any(i => i.Opcode == InstructionOpcode.PlaceTile &&
                    i.Arguments.Count >= 4 && targets.Contains((i.Arguments[0], i.Arguments[1])));
                bool rearrangesPassage=action.Instructions.Any(i=>i.Opcode is InstructionOpcode.MoveTile or InstructionOpcode.RemoveTile);
                if (!suppliesReward && !opensTarget && !rearrangesPassage) continue;
                var placements=action.Conditions.Where(c=>c.Opcode==ConditionOpcode.TileAtIs && c.Arguments.Count>=4 &&
                    c.Arguments[3]==1 && c.Arguments[0]>=0 && c.Arguments[0]<_gameData.Tiles.Count &&
                    _gameData.Tiles[c.Arguments[0]].IsDraggable)
                    .Select(c=>new BlockPuzzlePlanner.Placement(c.Arguments[1],c.Arguments[2],c.Arguments[0])).ToList();
                if(placements.Count==0) continue;
                var switches=action.Conditions.Where(c=>c.Opcode==ConditionOpcode.StandingOn && c.Arguments.Count>=2)
                    .Select(c=>((int)c.Arguments[0],(int)c.Arguments[1])).ToList();
                plan=BlockPuzzlePlanner.Plan(_gameData,zone,_state,switches,placements);
                if(plan is { Count: >0 } && !suppliesReward && !opensTarget &&
                    !ScriptedArrangementOpensApproach(zone,action,plan,targets,objective)) plan=null;
                if(plan is { Count: >0 }) break;
            }
        }
        if(plan is not { Count: >0 }) { _failedBlockMap=searchKey; return null; }
        _blockPlanSearchKey=searchKey;
        foreach(var move in plan) _blockPlan.Enqueue(move);
        Console.WriteLine($"[BOT] Calculated {plan.Count}-step object solution offline for zone {zone.Id}: {objective.Description}");
        return PlannedBlockMove(objective);
    }

    public void AbandonBlockPlan()
    {
        if(_blockPlan.Count>0) _failedBlockMap=_blockPlanSearchKey;
        _blockPlan.Clear();
        _blockWalkStart=null;
    }

    private bool ScriptedArrangementOpensApproach(Zone zone,Data.Action action,
        IReadOnlyList<BlockPuzzlePlanner.Step> plan,IReadOnlyCollection<(int X,int Y)> targets,BotObjective objective)
    {
        var grid=(ushort[,,])zone.TileGrid.Clone();
        int hx=_state.PlayerX,hy=_state.PlayerY;
        foreach(var step in plan)
        {
            if(step.Walk) { hx=step.X;hy=step.Y;continue; }
            var (dx,dy)=step.Direction switch { Direction.Up=>(0,-1),Direction.Down=>(0,1),Direction.Left=>(-1,0),_=>(1,0) };
            grid[step.Y+dy,step.X+dx,1]=grid[step.Y,step.X,1];
            grid[step.Y,step.X,1]=ushort.MaxValue;
            hx=step.X+(step.Pull?2*dx:0);hy=step.Y+(step.Pull?2*dy:0);
        }
        var view=new Zone { Id=zone.Id,Width=zone.Width,Height=zone.Height,
            TileGrid=grid,Objects=zone.Objects,Actions=zone.Actions };
        foreach(var condition in action.Conditions)
        {
            var a=condition.Arguments;
            bool valid=condition.Opcode switch {
                ConditionOpcode.TileAtIs or ConditionOpcode.IsVariable when a.Count>=4 => view.GetTile(a[1],a[2],a[3])==unchecked((ushort)a[0]),
                ConditionOpcode.StandingOn when a.Count>=3 => hx==a[0] && hy==a[1] && view.GetTile(hx,hy,0)==unchecked((ushort)a[2]),
                ConditionOpcode.HeroIsAt when a.Count>=2 => hx==a[0] && hy==a[1],
                ConditionOpcode.CounterIs or ConditionOpcode.CounterIsNot or ConditionOpcode.RandomIs or
                    ConditionOpcode.RandomIsGreaterThan or ConditionOpcode.RandomIsLessThan or
                    ConditionOpcode.SectorCounterIs or ConditionOpcode.SectorCounterIsNot or
                    ConditionOpcode.SectorCounterIsLessThan or ConditionOpcode.SectorCounterIsGreaterThan => StateAllows(zone.Id,action),
                _=>false };
            if(!valid) return false;
        }
        bool In(int x,int y,int layer)=>x>=0 && y>=0 && x<zone.Width && y<zone.Height && layer is >=0 and <3;
        foreach(var instruction in action.Instructions)
        {
            var a=instruction.Arguments;
            switch(instruction.Opcode)
            {
                case InstructionOpcode.MoveTile when a.Count>=5 && In(a[0],a[1],a[2]) && In(a[3],a[4],a[2]):
                    grid[a[4],a[3],a[2]]=grid[a[1],a[0],a[2]];grid[a[1],a[0],a[2]]=ushort.MaxValue;break;
                case InstructionOpcode.PlaceTile or InstructionOpcode.SetVariable when a.Count>=4 && In(a[0],a[1],a[2]):
                    grid[a[1],a[0],a[2]]=unchecked((ushort)a[3]);break;
                case InstructionOpcode.RemoveTile when a.Count>=3 && In(a[0],a[1],a[2]):
                    grid[a[1],a[0],a[2]]=ushort.MaxValue;break;
                case InstructionOpcode.SetCounter or InstructionOpcode.SetRandom or InstructionOpcode.PlaySound or
                    InstructionOpcode.Wait or InstructionOpcode.Redraw or InstructionOpcode.SetRectNeedsDisplay or
                    InstructionOpcode.SetTileNeedsDisplay or InstructionOpcode.SpeakHero or InstructionOpcode.SpeakNpc or
                    InstructionOpcode.DisableAction: break;
                default:return false;
            }
        }
        var nav=new Pathfinder(_gameData,_state) { AllowDiagonal=true, PreferCardinal=true,AdditionalWalkability=_canWalk };
        if(objective.Type==ObjectiveType.EnterDoor) nav.TransitionTarget=(zone.Id,objective.TargetX,objective.TargetY);
        return targets.Any(target=>nav.FindPath(view,hx,hy,target.X,target.Y,
            _state.ZoneNPCs.Where(n=>!n.IsHostile).ToList())!=null);
    }
}
