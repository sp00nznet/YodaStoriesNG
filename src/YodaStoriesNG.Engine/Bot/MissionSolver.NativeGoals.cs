using YodaStoriesNG.Engine.Data;
using YodaStoriesNG.Engine.Game;

namespace YodaStoriesNG.Engine.Bot;

public partial class MissionSolver
{
    internal static bool CanProduceRandom(Instruction instruction,Func<int,bool> matches) =>
        instruction.Arguments.Count>0 && (instruction.Opcode==InstructionOpcode.SetRandom
            ? matches(instruction.Arguments[0])
            : instruction.Opcode==InstructionOpcode.RollDice &&
                Enumerable.Range(1,Math.Max(1,(int)instruction.Arguments[0])).Any(matches));
    private (int Zone,int Action)? _nativeGoalFocus;
    private (int Zone,int Action)? _nativeGoalContinuation;
    private int _nativeContinuationRevision;
    internal void NoteScriptActionStarted(int zone,int action)
    {
        if(_nativeGoalFocus==(zone,action)) _nativeGoalFocus=null;
        if(_nativeGoalContinuation==(zone,action)) _nativeGoalContinuation=null;
    }
    private (int Zone,int X,int Y,int Tile)? _nativeOpeningFocus;
    private readonly HashSet<int> _nativeRewardRequests = new();
    private readonly HashSet<int> _neededNativeItems = new();
    public object NativePlanningState => new { Focus=_nativeGoalFocus,
        RequestedItems=_neededNativeItems.OrderBy(i=>i).ToArray(),ItemRouteFocus=_itemRouteFocus,Arrangement=_nativeArrangement,
        Opening=_nativeOpeningFocus,Continuation=_nativeGoalContinuation };
    private readonly HashSet<string> _failedNativeArrangements = new();
    private (int Zone,int Action)? _nativeArrangement;
    private int NativeInputEffort(WorldMap world,Zone zone,Data.Action action)
    {
        int cost=StateAllows(zone.Id,action) ? 0 : 10;
        foreach(var guard in action.Conditions.Where(c=>c.Opcode==ConditionOpcode.PlacedItemIs && c.Arguments.Count>=5))
        {
            int item=guard.Arguments[4]<0 ? world.Sectors.GetValueOrDefault(zone.Id)?.RequiredItem ?? -1 : guard.Arguments[4];
            cost+=item>=0 && _state.HasItem(item) ? 1 : 100;
        }
        return cost;
    }
    private static bool TemplateGuardsMatch(WorldMap world,Zone zone,Data.Action action)
    {
        foreach(var guard in action.Conditions)
        {
            var args=guard.Arguments;
            if(guard.Opcode==ConditionOpcode.RequiredItemIs &&
                (args.Count<1 || world.Sectors.GetValueOrDefault(zone.Id) is not { } sector ||
                 sector.RequiredItem!=args[0] && sector.AdditionalRequiredItem!=args[0])) return false;
            if(guard.Opcode==ConditionOpcode.FindItemIs &&
                (args.Count<1 || world.Sectors.GetValueOrDefault(zone.Id)?.FindItem!=args[0])) return false;
            if(guard.Opcode==ConditionOpcode.StartingItemIs &&
                (args.Count<1 || (world.StartingItemId ?? world.Mission?.PuzzleChain.FirstOrDefault()?.RewardItemId)!=args[0])) return false;
        }
        return true;
    }
    private bool RandomGuardsPossible(Zone zone,Data.Action action)
    {
        if(!_state.VisitedZones.Contains(zone.Id)) return true;
        foreach(var guard in action.Conditions.Where(c=>c.Arguments.Count>0 && c.Opcode is
            ConditionOpcode.RandomIs or ConditionOpcode.RandomIsNot or ConditionOpcode.RandomIsGreaterThan or ConditionOpcode.RandomIsLessThan))
        {
            bool Matches(int value)=>guard.Opcode switch {
                ConditionOpcode.RandomIsNot=>value!=guard.Arguments[0],
                ConditionOpcode.RandomIsGreaterThan=>value>guard.Arguments[0],
                ConditionOpcode.RandomIsLessThan=>value<guard.Arguments[0],_=>value==guard.Arguments[0] };
            if(!Matches(_state.ZoneRandom.GetValueOrDefault(zone.Id)) &&
                !zone.Actions.Where((p,i)=>!_state.DisabledActions.Contains((zone.Id,i)) &&
                    !p.Conditions.Any(c=>c.Opcode==ConditionOpcode.ZoneNotInitialized))
                    .Any(p=>p.Instructions.Any(i=>CanProduceRandom(i,Matches)))) return false;
        }
        return true;
    }

    private bool BumpAlwaysShadowed(Zone zone,Data.Action action,int index)
    {
        var bump=action.Conditions.FirstOrDefault(c=>c.Opcode==ConditionOpcode.Bump && c.Arguments.Count>=3);
        if(bump==null) return false;
        int x=bump.Arguments[0],y=bump.Arguments[1],tile=bump.Arguments[2];
        int Operands(Condition c)=>c.Opcode switch {
            ConditionOpcode.Bump or ConditionOpcode.StandingOn=>3,
            ConditionOpcode.HeroIsAt=>2,
            ConditionOpcode.TileAtIs or ConditionOpcode.IsVariable=>4,
            ConditionOpcode.CounterIs or ConditionOpcode.CounterIsNot or ConditionOpcode.RandomIs or
                ConditionOpcode.RandomIsNot or ConditionOpcode.RandomIsLessThan or ConditionOpcode.RandomIsGreaterThan or
                ConditionOpcode.SectorCounterIs or ConditionOpcode.SectorCounterIsNot or
                ConditionOpcode.SectorCounterIsLessThan or ConditionOpcode.SectorCounterIsGreaterThan or
                ConditionOpcode.RequiredItemIs or ConditionOpcode.FindItemIs or ConditionOpcode.HasItem=>1,
            _=>-1 };
        bool WritesTarget(Data.Action candidate)=>candidate.Instructions.Any(i=>i.Arguments.Count>=3 &&
            i.Arguments[2]==1 && (i.Opcode is InstructionOpcode.PlaceTile or InstructionOpcode.RemoveTile or InstructionOpcode.SetVariable &&
                i.Arguments[0]==x && i.Arguments[1]==y || i.Opcode==InstructionOpcode.MoveTile && i.Arguments.Count>=5 &&
                (i.Arguments[0]==x && i.Arguments[1]==y || i.Arguments[3]==x && i.Arguments[4]==y)));
        foreach(var (earlier,earlierIndex) in zone.Actions.Take(index).Select((a,i)=>(a,i)))
        {
            if(_state.DisabledActions.Contains((zone.Id,earlierIndex)) ||
                !earlier.Conditions.Any(c=>c.Opcode==ConditionOpcode.Bump) ||
                !earlier.Conditions.All(c=>Operands(c) is int count && count>0 && c.Arguments.Count>=count &&
                    action.Conditions.Any(g=>g.Opcode==c.Opcode && g.Arguments.Count>=count &&
                        g.Arguments.Take(count).SequenceEqual(c.Arguments.Take(count)))) ||
                !WritesTarget(earlier) || ActionWritesTile(zone,earlier,x,y,1,tile)) continue;
            if(!zone.Actions.Skip(earlierIndex+1).Take(index-earlierIndex-1)
                .Any(a=>WritesTarget(a) && ActionWritesTile(zone,a,x,y,1,tile))) return true;
        }
        return false;
    }

    private bool ActionWritesTile(Zone zone,Data.Action action,int x,int y,int layer,int tile)
    {
        var writes=new Dictionary<(int X,int Y,int Layer),ushort>();
        ushort Read(int cx,int cy,int l)
        {
            if(writes.TryGetValue((cx,cy,l),out var value)) return value;
            var bump=action.Conditions.FirstOrDefault(c=>c.Opcode==ConditionOpcode.Bump && l==1 &&
                c.Arguments.Count>=3 && c.Arguments[0]==cx && c.Arguments[1]==cy);
            return bump!=null ? unchecked((ushort)bump.Arguments[2]) : zone.GetTile(cx,cy,l);
        }
        // Automatic consumers of the same unchanged phase run before the
        // player can act again. Include their final writes, so a door opening
        // briefly during dialogue is not mistaken for a permanent passage.
        var phase=action.Conditions.FirstOrDefault(c=>c.Opcode==ConditionOpcode.CounterIs && c.Arguments.Count>0);
        var following=phase!=null && !action.Instructions.Any(i=>i.Opcode is InstructionOpcode.SetCounter or InstructionOpcode.AddToCounter)
            ? zone.Actions.Skip(zone.Actions.IndexOf(action)+1).Where(a=>
                !_state.DisabledActions.Contains((zone.Id,zone.Actions.IndexOf(a)))).TakeWhile(a=>!a.Instructions.Any(i=>
                i.Opcode is InstructionOpcode.SetCounter or InstructionOpcode.AddToCounter) ||
                a.Conditions.Count==1 && a.Conditions[0].Opcode==ConditionOpcode.CounterIs &&
                a.Conditions[0].Arguments[0]==phase.Arguments[0])
                .Where(a=>a.Conditions.Count==1 && a.Conditions[0].Opcode==ConditionOpcode.CounterIs &&
                    a.Conditions[0].Arguments.Count>0 && a.Conditions[0].Arguments[0]==phase.Arguments[0])
            : Enumerable.Empty<Data.Action>();
        foreach(var instruction in new[] { action }.Concat(following).SelectMany(a=>a.Instructions))
        {
            var a=instruction.Arguments;
            if(a.Count<3 || a[2] is <0 or >2) continue;
            if(instruction.Opcode is InstructionOpcode.PlaceTile or InstructionOpcode.SetVariable && a.Count>=4)
                writes[(a[0],a[1],a[2])]=unchecked((ushort)a[3]);
            else if(instruction.Opcode==InstructionOpcode.RemoveTile)
                writes[(a[0],a[1],a[2])]=ushort.MaxValue;
            else if(instruction.Opcode==InstructionOpcode.MoveTile && a.Count>=5)
            {
                ushort moved=Read(a[0],a[1],a[2]);
                writes[(a[0],a[1],a[2])]=ushort.MaxValue;
                writes[(a[3],a[4],a[2])]=moved;
            }
        }
        return writes.TryGetValue((x,y,layer),out ushort result) && result==unchecked((ushort)tile);
    }
    private static bool ReleasesStandingEffect(Zone zone,Data.Action producer,int x,int y,int layer,int tile)
    {
        var plate=producer.Conditions.FirstOrDefault(c=>c.Opcode==ConditionOpcode.StandingOn && c.Arguments.Count>=2);
        if(plate==null) return false;
        return zone.Actions.Any(reset=>!reset.Conditions.Any(c=>c.Opcode is ConditionOpcode.Bump or
                ConditionOpcode.StandingOn or ConditionOpcode.HeroIsAt or ConditionOpcode.PlacedItemIs) &&
            reset.Conditions.Any(c=>c.Opcode==ConditionOpcode.TileAtIs && c.Arguments.Count>=4 &&
                c.Arguments[0]==-1 && c.Arguments[1]==plate.Arguments[0] && c.Arguments[2]==plate.Arguments[1] && c.Arguments[3]==1) &&
            reset.Instructions.Any(i=>i.Arguments.Count>=3 && i.Arguments[0]==x && i.Arguments[1]==y && i.Arguments[2]==layer &&
                (i.Opcode==InstructionOpcode.RemoveTile && tile!=-1 ||
                 i.Opcode==InstructionOpcode.PlaceTile && i.Arguments.Count>=4 && i.Arguments[3]!=tile)));
    }

    private int? ArrivalSectorCounter(WorldMap world,Zone zone,Data.Action producer,int initial)
    {
        if(!producer.Conditions.Any(c=>c.Opcode==ConditionOpcode.ZoneEntered)) return null;
        int value=initial; bool reset=false;
        foreach(var action in zone.Actions.Where((a,i)=>!_state.DisabledActions.Contains((zone.Id,i)) &&
            a.Conditions.Any(c=>c.Opcode==ConditionOpcode.ZoneEntered) &&
            a.Instructions.Any(i=>i.Opcode is InstructionOpcode.SetSectorCounter or InstructionOpcode.AddToSectorCounter)))
        {
            if(!TemplateGuardsMatch(world,zone,action)) continue;
            bool matches=true;
            foreach(var c in action.Conditions)
            {
                var a=c.Arguments;
                switch(c.Opcode)
                {
                    case ConditionOpcode.ZoneEntered: break;
                    case ConditionOpcode.RequiredItemIs:
                    case ConditionOpcode.FindItemIs: break; // TemplateGuardsMatch checked these.
                    case ConditionOpcode.TileAtIs:
                    case ConditionOpcode.IsVariable:
                        if(a.Count<4) return null;
                        matches &= zone.GetTile(a[1],a[2],a[3])==unchecked((ushort)a[0]); break;
                    case ConditionOpcode.RandomIs:
                        if(a.Count<1) return null;
                        matches &= _state.ZoneRandom.GetValueOrDefault(zone.Id)==a[0]; break;
                    default: return null; // Leave unsupported entry phases to ordinary planning.
                }
            }
            if(!matches) continue;
            foreach(var instruction in action.Instructions.Where(i=>i.Arguments.Count>0))
                if(instruction.Opcode==InstructionOpcode.SetSectorCounter) { value=instruction.Arguments[0]; reset=true; }
                else if(instruction.Opcode==InstructionOpcode.AddToSectorCounter) value+=instruction.Arguments[0];
        }
        return reset ? value : null;
    }

    internal bool SignalReachesConsumer(Zone zone,Data.Action producer,Data.Action consumer,ConditionOpcode guard,int value)
        => SignalBlocker(zone,producer,consumer,guard,value)==null;

    private Data.Action? SignalBlocker(Zone zone,Data.Action producer,Data.Action consumer,ConditionOpcode guard,int value)
    {
        if(guard is not (ConditionOpcode.RandomIs or ConditionOpcode.CounterIs)) return null;
        int start=zone.Actions.IndexOf(producer),end=zone.Actions.IndexOf(consumer);
        if(start<0 || end<=start) return null;
        var opcode=guard==ConditionOpcode.CounterIs ? InstructionOpcode.SetCounter : InstructionOpcode.SetRandom;
        int signal=value;
        Data.Action? blocker=null;
        bool MatchesOtherGuard(Condition c)
        {
            if(c.Opcode is not (ConditionOpcode.TileAtIs or ConditionOpcode.IsVariable) || c.Arguments.Count<4) return false;
            var a=c.Arguments;
            bool changed=producer.Instructions.Any(i=>i.Arguments.Count>=3 &&
                i.Opcode is InstructionOpcode.PlaceTile or InstructionOpcode.SetVariable or InstructionOpcode.RemoveTile &&
                i.Arguments[0]==a[1] && i.Arguments[1]==a[2] && i.Arguments[2]==a[3]);
            return changed ? ActionWritesTile(zone,producer,a[1],a[2],a[3],a[0]) :
                ScriptedActorTiles.Read(_gameData,_state,zone,a[1],a[2],a[3])==unchecked((ushort)a[0]);
        }
        foreach(var action in zone.Actions.Skip(start+1).Take(end-start-1))
        {
            if(_state.DisabledActions.Contains((zone.Id,zone.Actions.IndexOf(action))) ||
                !action.Conditions.Any(c=>c.Opcode==guard && c.Arguments.Count>0 && c.Arguments[0]==signal) ||
                !action.Conditions.All(c=>c.Opcode==guard && c.Arguments.Count>0 && c.Arguments[0]==signal ||
                    MatchesOtherGuard(c))) continue;
            foreach(var instruction in action.Instructions)
                if(instruction.Opcode==opcode && instruction.Arguments.Count>0)
                { signal=instruction.Arguments[0]; blocker=action; }
        }
        return signal==value ? null : blocker;
    }
    // Work backwards from the authored win condition. Every returned action has
    // an explicit producer/guard chain; this never tries arbitrary inventory items.
    internal BotObjective? NativeGoalPrerequisite(WorldMap world, Sector sector,
        (int Zone,int X,int Y)? openingTile=null,int? rewardItem=null,(int Zone,int Index)? authoredAction=null,
        BotObjective? interaction=null)
    {
        if (sector.Type != ZoneType.Goal && openingTile==null && rewardItem==null && authoredAction==null && interaction==null &&
            _nativeGoalFocus==null && _nativeGoalContinuation==null) return null;
        var rooms=ZoneAndRooms(sector.Zone).Select(id=>_gameData.Zones[id]).ToList();
        if(openingTile is { } entrance && !rooms.Any(z=>z.Id==entrance.Zone))
            rooms.Add(_gameData.Zones[entrance.Zone]);
        int Root(int id)=>world.Sectors.GetValueOrDefault(id)?.Zone ?? sector.Zone;
        var visiting=new HashSet<string>();
        var trace=new List<string>();
        int depthLimit=sector.Type!=ZoneType.Goal || sector.RequiredItem<=0 || _state.HasItem(sector.RequiredItem) ||
            _consumedInteractions.Contains((sector.Zone,sector.RequiredItem)) ? 64 : 24;
        BotObjective? PrepareRoute(int id,int x,int y,bool adjacent,bool diagonalItem)
        {
            var rejected=new HashSet<(int Zone,int Action)>();
            var blockedOpenings=new HashSet<(int Zone,int X,int Y)>();
            BotObjective? blocked=null;
            for(int attempt=0;attempt<8;attempt++)
            {
                var ignoredTiles=blockedOpenings.Concat(_unreachableRouteTiles
                    .Where(k=>k.TargetZone==id && k.TargetX==x && k.TargetY==y && k.State==RouteResolutionState(k.Zone))
                    .Select(k=>(k.Zone,k.X,k.Y))).ToHashSet();
                var opening=GoalRouteOpening(rooms,id,x,y,adjacent,
                    (z,i)=>visiting.Contains($"action:{z}:{i}") || rejected.Contains((z,i)) ||
                        _unreachableRouteActions.Contains((id,x,y,z,i,RouteResolutionState(z))),diagonalItem,ignoredTiles);
                if(opening is not { } needed) break;
                Console.WriteLine($"[BOT] Native route prerequisite for {id} ({x},{y}): zone {needed.Zone.Id} ({needed.X},{needed.Y}) action={needed.Action}");
                var prepare=needed.Action is int action ? Action(needed.Zone,needed.Zone.Actions[action],1) :
                    Tile(needed.Zone,needed.X,needed.Y,1,-1,1);
                if(prepare!=null && prepare.Type is not (ObjectiveType.Explore or ObjectiveType.PlanningBlocked or ObjectiveType.UnresolvedReward))
                    return prepare;
                // A null result means the prerequisite resolver proved it has
                // no executable step in the current state. Remember that proof
                // too: otherwise every nested route query rediscovers the same
                // initializer/opening, even though inventory, counters, and map
                // state have not changed.
                string failedState=RouteResolutionState(needed.Zone.Id);
                if(needed.Action is int failedAction)
                    _unreachableRouteActions.Add((id,x,y,needed.Zone.Id,failedAction,failedState));
                else
                    _unreachableRouteTiles.Add((id,x,y,needed.Zone.Id,needed.X,needed.Y,failedState));
                blocked ??= prepare;
                _nativeGoalFocus=null;
                if(needed.Action is int failed)
                { if(!rejected.Add((needed.Zone.Id,failed))) break; }
                else
                {
                    int count=rejected.Count;
                    foreach(var (producer,index) in needed.Zone.Actions.Select((a,i)=>(a,i)))
                        if(producer.Instructions.Any(i=>i.Arguments.Count>=3 && i.Arguments[0]==needed.X &&
                            i.Arguments[1]==needed.Y && i.Arguments[2]==1 && i.Opcode is
                                InstructionOpcode.RemoveTile or InstructionOpcode.PlaceTile or InstructionOpcode.MoveTile))
                            rejected.Add((needed.Zone.Id,index));
                    if(rejected.Count==count && !blockedOpenings.Add((needed.Zone.Id,needed.X,needed.Y))) break;
                }
            }
            return blocked;
        }
        BotObjective? At(int id,int x,int y,bool adjacent,Func<BotObjective> instruction,bool diagonalItem=false,bool extendedCardinal=false)
        {
            trace.Add($"approach {id} ({x},{y}) adjacent={adjacent}");
            if(_state.CurrentZoneId!=id)
            {
                var targets=adjacent ? Pathfinder.InteractionCells(x,y,diagonalItem,extendedCardinal) : new[] { (x,y) };
                if(GraphTravel(world,id,targets) is { } travel)
                {
                    Console.WriteLine($"[BOT] Native approach to {id} ({x},{y}) from {_state.CurrentZoneId} ({_state.PlayerX},{_state.PlayerY}): graph -> {travel.Description}");
                    return travel;
                }
                if(PrepareRoute(id,x,y,adjacent,diagonalItem) is { } preparation) return preparation;
                return NavigateToward(world,id,Root(id),x,y,adjacent);
            }
            var zone=_gameData.Zones[id];
            var nav=new Pathfinder(_gameData,_state) { AllowDiagonal=true, PreferCardinal=true,AdditionalWalkability=_canWalk,
                TransitionTarget=(id,x,y),AvoidFloorSwitches=!adjacent };
            var path=adjacent ? nav.FindPathToAdjacent(zone,_state.PlayerX,_state.PlayerY,x,y,_state.ZoneNPCs,allowDiagonalInteraction:diagonalItem,extendedCardinalInteraction:extendedCardinal)?.Path :
                nav.FindPath(zone,_state.PlayerX,_state.PlayerY,x,y,_state.ZoneNPCs);
            bool actorsPreventedPath=path==null;
            if(path==null)
            {
                // Match the world graph's clearable route proof: enemies are
                // fought by the executor and mobile friendlies are waited for.
                // Only fixed friendly actors justify searching for a detour.
                var fixedActors=_state.ZoneNPCs.Where(n=>!n.IsHostile &&
                    n.Behavior is NPCBehavior.Stationary or NPCBehavior.Animation).ToList();
                path=adjacent ? nav.FindPathToAdjacent(zone,_state.PlayerX,_state.PlayerY,x,y,fixedActors,allowDiagonalInteraction:diagonalItem,extendedCardinalInteraction:extendedCardinal)?.Path :
                    nav.FindPath(zone,_state.PlayerX,_state.PlayerY,x,y,fixedActors);
                if(path!=null && actorsPreventedPath)
                {
                    // A route that exists only after actor occupancy is
                    // removed must be cleared before issuing a scripted
                    // interaction. Otherwise At() reports the target as
                    // unreachable, and the planner may abandon the active
                    // mission dependency without ever fighting the blocker.
                    var probe=new BotObjective { Type=ObjectiveType.UseItemOnNpc,TargetX=x,TargetY=y,
                        Description=$"Approach authored interaction ({x},{y})" };
                    if(BlockingEnemyOnApproach(probe) is { } enemy)
                        return new BotObjective { Type=ObjectiveType.KillEnemy,TargetNpc=enemy,
                            TargetX=enemy.X,TargetY=enemy.Y,
                            Description=$"Defeat {enemy.CharacterId} blocking the authored interaction route" };
                }
            }
            if(path==null)
            {
                // A reachable room can still require rearranging furniture to
                // approach a fixed chest or switch. Resolve that local object
                // route before searching for another entrance to the same room.
                var localInstruction=instruction();
                if(localInstruction.Type is ObjectiveType.ClearObject or ObjectiveType.UseItemOnNpc or ObjectiveType.BumpTarget or
                    ObjectiveType.WalkToPosition or ObjectiveType.PickupItem &&
                    zone.TileGrid.Cast<ushort>().Any(t=>t<_gameData.Tiles.Count && _gameData.Tiles[t].IsDraggable))
                {
                    _unreachablePositions.Add((id,x,y));
                    if(PlannedBlockMove(localInstruction) is { } rearrange) return rearrange;
                    // The required stance can itself be covered by a block,
                    // with its approach sealed by a scripted lock. A walking
                    // proof cannot see past both obstacles at once. Prove the
                    // object route with one authored gate open, then execute
                    // that gate's real prerequisite before moving any blocks.
                    foreach(var gate in zone.Actions.Where((a,i)=>!_state.DisabledActions.Contains((id,i)) &&
                        !a.Conditions.Any(c=>c.Opcode==ConditionOpcode.ZoneNotInitialized) && TemplateGuardsMatch(world,zone,a))
                        .SelectMany(a=>a.Instructions.Where(i=>i.Opcode==InstructionOpcode.RemoveTile &&
                            i.Arguments.Count>=3 && i.Arguments[2]==1).Select(i=>(Action:a,X:(int)i.Arguments[0],Y:(int)i.Arguments[1])))
                        .DistinctBy(g=>(g.X,g.Y)))
                    {
                        int cover=zone.GetTile(gate.X,gate.Y,1);
                        if(cover>=_gameData.Tiles.Count || !_gameData.Tiles[cover].IsObject ||
                            _gameData.Tiles[cover].IsDraggable || !ActionWritesTile(zone,gate.Action,gate.X,gate.Y,1,-1)) continue;
                        var gateGrid=(ushort[,,])zone.TileGrid.Clone();gateGrid[gate.Y,gate.X,1]=ushort.MaxValue;
                        var opened=new Zone { Id=id,Width=zone.Width,Height=zone.Height,TileGrid=gateGrid,
                            Objects=zone.Objects,Actions=zone.Actions };
                        var destinations=adjacent ? Pathfinder.InteractionCells(x,y,diagonalItem,extendedCardinal) : new[]{(x,y)};
                        if(BlockPuzzlePlanner.Plan(_gameData,opened,_state,destinations,
                            Array.Empty<BlockPuzzlePlanner.Placement>(),maxStates:20000,transitionTarget:(x,y)) is not {Count:>0}) continue;
                        if(Tile(zone,gate.X,gate.Y,1,-1,1) is { } unlock && unlock.Type is not
                            (ObjectiveType.Explore or ObjectiveType.PlanningBlocked or ObjectiveType.UnresolvedReward)) return unlock;
                    }
                }
                if(zone.GetTile(x,y,1) is var block && block<_gameData.Tiles.Count && _gameData.Tiles[block].IsDraggable &&
                    nav.FindPathToAdjacent(zone,_state.PlayerX,_state.PlayerY,x,y,
                        _state.ZoneNPCs.Where(n=>!n.IsHostile &&
                            n.Behavior is NPCBehavior.Stationary or NPCBehavior.Animation).ToList())!=null)
                    return new BotObjective { Type=ObjectiveType.ClearObject,TargetX=x,TargetY=y,
                        Description=$"Uncover the authored mission interaction ({x},{y})" };
                // A room may have disconnected areas connected through other
                // rooms. Plan to the actual interaction area, not just its zone.
                var targets=adjacent ? Pathfinder.InteractionCells(x,y,diagonalItem,extendedCardinal) : new[] { (x,y) };
                if(GraphTravel(world,id,targets) is { } travel)
                {
                    Console.WriteLine($"[BOT] Native approach to {id} ({x},{y}) from {_state.CurrentZoneId} ({_state.PlayerX},{_state.PlayerY}): graph -> {travel.Description}");
                    return travel;
                }
                if(PrepareRoute(id,x,y,adjacent,diagonalItem) is { } preparation) return preparation;
                // Relax only authored, removable doors to locate the first
                // necessary opening. Resolve its real guards before walking it.
                var grid=(ushort[,,])zone.TileGrid.Clone();
                foreach(var action in zone.Actions.Where(a=>!a.Conditions.Any(c=>c.Opcode is
                    ConditionOpcode.ZoneNotInitialized or ConditionOpcode.EnterByPlane or ConditionOpcode.ZoneEntered)))
                foreach(var remove in action.Instructions.Where(i=>i.Opcode==InstructionOpcode.RemoveTile &&
                    i.Arguments.Count>=3 && i.Arguments[2]==1))
                {
                    int rx=remove.Arguments[0],ry=remove.Arguments[1];
                    if(rx>=0 && ry>=0 && rx<zone.Width && ry<zone.Height &&
                        ActionWritesTile(zone,action,rx,ry,1,-1)) grid[ry,rx,1]=ushort.MaxValue;
                }
                var relaxed=new Zone { Id=id,Width=zone.Width,Height=zone.Height,
                    TileGrid=grid,Objects=zone.Objects,Actions=zone.Actions };
                var approach=adjacent ? nav.FindPathToAdjacent(relaxed,_state.PlayerX,_state.PlayerY,x,y,allowDiagonalInteraction:diagonalItem,extendedCardinalInteraction:extendedCardinal)?.Path :
                    nav.FindPath(relaxed,_state.PlayerX,_state.PlayerY,x,y);
                if(approach!=null) foreach(var cell in approach)
                    if(zone.GetTile(cell.X,cell.Y,1) is var cover && cover<_gameData.Tiles.Count && _gameData.Tiles[cover].IsObject)
                        return Tile(zone,cell.X,cell.Y,1,-1,1);
                // An unproved approach must not become an executable objective.
                // Let the dependency search consider another authored route.
                return null;
            }
            var step=instruction();
            if(step.Type==ObjectiveType.WalkToPosition) step.ScriptedInteraction=true;
            return step;
        }

        BotObjective? Item(int item,int depth)
        {
            int revision=_itemRouteRevision;
            var step=ItemCore(item,depth);
            if(revision==_itemRouteRevision && !_state.HasItem(item) && step!=null && step.Type is not
                (ObjectiveType.Explore or ObjectiveType.PlanningBlocked or ObjectiveType.UnresolvedReward))
            { _itemRouteFocus=item; _itemRouteRevision++; }
            return step;
        }

        BotObjective? ItemCore(int item,int depth)
        {
            _neededNativeItems.Add(item);
            if(TryLocalItem(world,item) is { } local) return local;
            if(CurrentItemSupply(item) is { } supply) return supply;
            trace.Add($"item {item} depth={depth}");
            if (depth>depthLimit || !visiting.Add($"item:{item}")) return null;
            try
            {
                // Finale dependencies can lead to a separate authored encounter
                // (such as the source of a required ability). Include only this
                // item's generated providers, not arbitrary rooms from the data.
                foreach(var provider in world.Sectors.Values.Distinct().Where(s=>s.FindItem==item || s.AdditionalGainItem==item))
                foreach(int id in ZoneAndRooms(provider.Zone))
                    if(!rooms.Any(z=>z.Id==id)) rooms.Add(_gameData.Zones[id]);
                // Template alternatives may contain the same supply behind
                // mutually exclusive entrances. Prefer the room we can enter,
                // rather than preparing a permanently sealed alternative.
                var itemRooms=rooms.ToArray().OrderBy(z=>z.Id==_state.CurrentZoneId ? 0 :
                    ActionGraph!.Route(_state.CurrentZoneId,z.Id,e=>!e.Instruction.Direction.HasValue ||
                        !IsExitBlocked(e.From,e.Instruction.Direction.Value)).Count>0 ? 1 : 2).ToArray();
                foreach(var zone in itemRooms)
                for(int y=0;y<zone.Height;y++) for(int x=0;x<zone.Width;x++)
                    if(zone.GetTile(x,y,1)==item && !ScriptedActorTiles.IsTradeTarget(zone,x,y,item))
                    {
                        var bump=zone.Actions.Where((a,i)=>!_state.DisabledActions.Contains((zone.Id,i)))
                            .FirstOrDefault(a=>a.Conditions.Any(c=>c.Opcode==ConditionOpcode.Bump &&
                            c.Arguments.Count>=3 && c.Arguments[0]==x && c.Arguments[1]==y && c.Arguments[2]==item) &&
                            a.Instructions.Any(i=>i.Opcode==InstructionOpcode.DropItem && i.Arguments[0]==item ||
                                i.Opcode is InstructionOpcode.WinGame or InstructionOpcode.MarkAsSolved or InstructionOpcode.DisableAction ||
                                i.Opcode==InstructionOpcode.RemoveTile && i.Arguments.Count>=3 &&
                                    i.Arguments[0]==x && i.Arguments[1]==y && i.Arguments[2]==1));
                        if(bump!=null)
                        {
                            if(Action(zone,bump,depth+1) is { } interact) return interact;
                            continue;
                        }
                        // A stationary character can use the same picture as
                        // the item its later dialogue branch drops. Its bump
                        // sets a signal; the actual DropItem producer below
                        // resolves the remaining guards and handover.
                        if(zone.Actions.Where((a,i)=>!_state.DisabledActions.Contains((zone.Id,i)))
                            .Any(a=>a.Conditions.Any(c=>c.Opcode==ConditionOpcode.Bump &&
                                c.Arguments.Count>=3 && c.Arguments[0]==x && c.Arguments[1]==y &&
                                c.Arguments[2]==item))) continue;
                        if(!_state.VisitedZones.Contains(zone.Id) && GraphTravel(world,zone.Id) is { } entry)
                            return entry;
                        int px=x,py=y;
                        if(At(zone.Id,px,py,false,()=>new BotObjective { Type=ObjectiveType.PickupItem,
                            TargetX=px,TargetY=py,Description=$"Collect mission-goal item {item}" }) is { } pickup)
                            return pickup;
                    }
                BotObjective? blockedProvider=null;
                // Native death drops are item producers too. A zero loot field
                // selects the room's quest-drop hotspot, even when that hotspot
                // is disabled because the monster owns the reward.
                foreach(var zone in itemRooms)
                foreach(var (entity,index) in zone.Monsters.Select((m,i)=>(m,i)).Where(p=>p.m.DropsLoot &&
                    (p.m.Loot>0 ? p.m.Loot-1==item : zone.Objects.Any(h=>(int)h.Type==0 && h.Argument==item))))
                {
                    if(_state.DefeatedMonsters.Contains($"{zone.Id}_{index}")) continue;
                    if(zone.Id!=_state.CurrentZoneId)
                    {
                        var entry=NavigateToward(world,zone.Id,Root(zone.Id));
                        if(entry.Type is not (ObjectiveType.Explore or ObjectiveType.PlanningBlocked or ObjectiveType.UnresolvedReward))
                            return entry;
                        blockedProvider ??= entry;
                        continue;
                    }
                    var enemy=_state.ZoneNPCs.FirstOrDefault(n=>n.NativeMonsterIndex==index && n.IsAlive && n.IsHostile);
                    if(enemy==null) continue;
                    if(enemy.IsEnabled)
                        return At(zone.Id,enemy.X,enemy.Y,true,()=>new BotObjective {
                            Type=ObjectiveType.KillEnemy,TargetNpc=enemy,TargetX=enemy.X,TargetY=enemy.Y,
                            RequiredItemId=item,Description=$"Defeat the native carrier of item {item}" });
                    foreach(var producer in zone.Actions.Where(a=>a.Instructions.Any(i=>i.Opcode==InstructionOpcode.EnableMonster &&
                        i.Arguments.Count>0 && i.Arguments[0]==index)))
                        if(Action(zone,producer,depth+1) is { } enable)
                        {
                            if(enable.Type is not (ObjectiveType.Explore or ObjectiveType.PlanningBlocked or ObjectiveType.UnresolvedReward))
                                return enable;
                            blockedProvider ??= enable;
                            _nativeGoalFocus=null;
                        }
                }
                foreach(var zone in itemRooms) foreach(var action in zone.Actions.Where(a=>a.Instructions.Any(i=>
                    i.Arguments.Count>0 && i.Opcode is InstructionOpcode.AddItem or InstructionOpcode.DropItem &&
                    (i.Arguments[0]==item || i.Arguments[0]==-1 && world.Sectors.GetValueOrDefault(zone.Id)?.FindItem==item))))
                    if(Action(zone,action,depth+1) is { } step)
                    {
                        if(step.Type is not (ObjectiveType.Explore or ObjectiveType.PlanningBlocked or ObjectiveType.UnresolvedReward))
                            return step;
                        blockedProvider ??= step;
                        _nativeGoalFocus=null;
                    }
                foreach(var zone in itemRooms)
                foreach(var (marker,index) in zone.Objects.Select((h,i)=>(h,i)).Where(p=>!p.h.Enabled &&
                    p.h.Argument==item && p.h.Type is ZoneObjectType.CrateItem or ZoneObjectType.CrateWeapon or ZoneObjectType.Trigger))
                    foreach(var producer in zone.Actions.Where(a=>a.Instructions.Any(i=>i.Opcode==InstructionOpcode.EnableHotspot &&
                        i.Arguments.Count>0 && i.Arguments[0]==index)))
                        if(Action(zone,producer,depth+1) is { } activate)
                        {
                            if(activate.Type is not (ObjectiveType.Explore or ObjectiveType.PlanningBlocked or ObjectiveType.UnresolvedReward))
                                return activate;
                            blockedProvider ??= activate;
                            _nativeGoalFocus=null;
                        }
                foreach(var zone in itemRooms) foreach(var marker in zone.Objects.Where(h=>h.Enabled && h.Argument==item &&
                    h.Type is ZoneObjectType.CrateItem or ZoneObjectType.CrateWeapon or ZoneObjectType.Trigger))
                {
                    // Fixed supplies in an unvisited room are laid down on entry.
                    // Try every authored alternative: an initializer may seal the others.
                    if(!_state.VisitedZones.Contains(zone.Id) &&
                        marker.Type is ZoneObjectType.CrateItem or ZoneObjectType.CrateWeapon &&
                        GraphTravel(world,zone.Id) is { } supplyRoute)
                        return supplyRoute;
                    if(zone.GetTile(marker.X,marker.Y,1) is var cover && cover<_gameData.Tiles.Count && _gameData.Tiles[cover].IsDraggable)
                        return At(zone.Id,marker.X,marker.Y,true,()=>new BotObjective { Type=ObjectiveType.ClearObject,
                            TargetX=marker.X,TargetY=marker.Y,RequiredItemId=item,
                            Description=$"Uncover the item {item} required by the mission script" });
                    if(cover<_gameData.Tiles.Count &&
                        (_gameData.Tiles[cover].IsObject || _gameData.Tiles[cover].IsCharacter) &&
                        Tile(zone,marker.X,marker.Y,1,-1,depth+1) is { } uncover)
                        return uncover;
                }
                return FetchItem(world,item) ?? blockedProvider;
            }
            finally { visiting.Remove($"item:{item}"); }
        }

        int ResolvePlacedItemId(Zone zone,IReadOnlyList<short> arguments)
        {
            if(arguments.Count<5) return -1;
            if(arguments[4]>=0) return arguments[4];
            // In a PlacedItemIs condition, item -1 means the active sector's
            // required item (see ActionExecutor.EvaluateCondition), not any item.
            return world.Sectors.GetValueOrDefault(zone.Id)?.RequiredItem ??
                   (zone.Id==sector.Zone ? sector.RequiredItem : -1);
        }

        BotObjective? Tile(Zone zone,int x,int y,int layer,int tile,int depth)
        {
            trace.Add($"tile {zone.Id} ({x},{y},{layer})={tile} depth={depth}");
            string key=$"tile:{zone.Id}:{x},{y},{layer}:{tile}";
            if(depth>depthLimit || !visiting.Add(key)) return null;
            try
            {
                if(layer==1 && tile==-1 && zone.Id==_state.CurrentZoneId &&
                    _state.ZoneNPCs.FirstOrDefault(n=>n.IsAlive && n.IsEnabled && n.X==x && n.Y==y) is { } occupying)
                    return new BotObjective { Type=occupying.IsHostile && !occupying.IsInvulnerable
                        ? ObjectiveType.KillEnemy : ObjectiveType.WaitForActor,
                        TargetNpc=occupying,TargetX=x,TargetY=y,
                        Description=$"Clear actor {occupying.NativeMonsterIndex} from the scripted cell ({x},{y})" };
                if(layer==1 && tile>0 && tile<_gameData.Tiles.Count && zone.Id==_state.CurrentZoneId)
                foreach(var actor in _state.ZoneNPCs.Where(n=>n.IsAlive && n.IsInvulnerable &&
                    n.Behavior==NPCBehavior.Wandering && n.CharacterId>=0 && n.CharacterId<_gameData.Characters.Count &&
                    Enum.GetValues<Direction>().Any(d=>ScriptedActorTiles.Frames(_gameData.Characters[n.CharacterId],d).Contains((ushort)tile))))
                {
                    if(!WorldActionGraph.WalkableArea(_gameData,zone,actor.X,actor.Y,_state,_canWalk).Contains((x,y))) continue;
                    if(actor.IsEnabled)
                        return new BotObjective { Type=ObjectiveType.WaitForActor,TargetNpc=actor,TargetX=x,TargetY=y,
                            Description=$"Wait for authored actor {actor.NativeMonsterIndex} at ({x},{y})" };
                    foreach(var activate in zone.Actions.Where(a=>a.Instructions.Any(i=>i.Opcode==InstructionOpcode.EnableMonster &&
                        i.Arguments.Count>0 && i.Arguments[0]==actor.NativeMonsterIndex)))
                        if(Action(zone,activate,depth+1) is { } enable) return enable;
                }
                if(layer==1 && zone.Id==_state.CurrentZoneId)
                    foreach(var actor in _state.ZoneNPCs.Where(n=>n.IsAlive && n.IsEnabled && n.Behavior==NPCBehavior.Fleeing))
                        if(ActorGuidePlanner.Plan(_gameData,_state,actor,x,y,tile,_canWalk) is { } guide) return guide;
                if(layer==1 && tile==-1 && zone.GetTile(x,y,1) is var cover && cover<_gameData.Tiles.Count &&
                    _gameData.Tiles[cover].IsDraggable)
                {
                    var preparation=At(zone.Id,x,y,true,()=>new BotObjective { Type=ObjectiveType.ClearObject,TargetX=x,TargetY=y,
                        Description=$"Clear the scripted mission cell ({x},{y})" });
                    if(_state.CurrentZoneId!=zone.Id && preparation!=null &&
                        preparation.Type is not (ObjectiveType.Explore or ObjectiveType.PlanningBlocked or ObjectiveType.UnresolvedReward))
                        _nativeOpeningFocus ??= (zone.Id,x,y,cover);
                    if(zone.Id==_state.CurrentZoneId && preparation?.Type==ObjectiveType.ClearObject)
                        return PlannedBlockMove(preparation);
                    return preparation;
                }
                BotObjective? blockedOpening=null;
                foreach(var action in zone.Actions.Where(a=>ActionWritesTile(zone,a,x,y,layer,tile))
                    .OrderBy(a=>NativeInputEffort(world,zone,a))
                    // A plate held by Luke cannot keep a remote passage open
                    // after he leaves. Prefer a durable object placement first.
                    .ThenBy(a=>ReleasesStandingEffect(zone,a,x,y,layer,tile) ? 1 : 0)
                    .ThenBy(a=>StateAllows(zone.Id,a) ? 0 : 1)
                    .ThenBy(a=>a.Conditions.Count(c=>c.Opcode is ConditionOpcode.TileAtIs or ConditionOpcode.IsVariable &&
                        c.Arguments.Count>=4 && zone.GetTile(c.Arguments[1],c.Arguments[2],c.Arguments[3])!=unchecked((ushort)c.Arguments[0]))))
                    if(Action(zone,action,depth+1) is { } step)
                    {
                        Console.WriteLine($"[BOT] Tile prerequisite {zone.Id} ({x},{y},{layer})={tile}: action {zone.Actions.IndexOf(action)} -> {step.Type}: {step.Description}");
                        if(step.Type is not (ObjectiveType.Explore or ObjectiveType.PlanningBlocked or ObjectiveType.UnresolvedReward))
                            return step;
                        blockedOpening ??= step;
                        _nativeGoalFocus=null;
                    }
                return blockedOpening;
            }
            finally { visiting.Remove(key); }
        }

        BotObjective? Action(Zone zone,Data.Action action,int depth)
        {
            int revision=_nativeContinuationRevision;
            var step=ResolveAction(zone,action,depth);
            if(revision==_nativeContinuationRevision && step!=null && step.Type is not
                (ObjectiveType.Explore or ObjectiveType.PlanningBlocked or ObjectiveType.UnresolvedReward) &&
                !action.Conditions.Any(c=>c.Opcode is ConditionOpcode.ZoneNotInitialized or ConditionOpcode.ZoneEntered or ConditionOpcode.EnterByPlane) &&
                action.Instructions.Any(i=>i.Opcode is InstructionOpcode.ChangeZone or InstructionOpcode.WinGame))
            {
                _nativeGoalContinuation=(zone.Id,zone.Actions.IndexOf(action));
                _nativeContinuationRevision++;
            }
            return step;
        }

        BotObjective? ResolveAction(Zone zone,Data.Action action,int depth)
        {
            int index=zone.Actions.IndexOf(action);
            trace.Add($"action {zone.Id}:{index} depth={depth}");
            string key=$"action:{zone.Id}:{index}";
            if(depth>depthLimit || _state.DisabledActions.Contains((zone.Id,index)) ||
                IsScriptFailedWithoutProgress(zone.Id,index) || IsScriptCompletedInCurrentState(zone.Id,index) ||
                !visiting.Add(key)) return null;
            try
            {
                // Reject immutable template alternatives before entering their
                // rooms or resolving any mutable prerequisite. Authored guard
                // order does not imply that an item dependency is applicable.
                if(!TemplateGuardsMatch(world,zone,action) || IsUnchangedPortalAction(zone,action)) return null;
                if(!RandomGuardsPossible(zone,action)) return null;
                // Reject a bump branch that an earlier action necessarily
                // invalidates, before preparing its tile or hero stance. Doing
                // this afterward can alternate the two tile states forever.
                if(BumpAlwaysShadowed(zone,action,index)) return null;
                // A shared sector counter can select a reward room before that
                // room is visited. Do not enter an impossible alternative just
                // because its own initializer has not run yet.
                if(!_state.VisitedZones.Contains(zone.Id) && world.Sectors.GetValueOrDefault(zone.Id) is { } owner &&
                    _state.VisitedZones.Contains(owner.Zone))
                foreach(var guard in action.Conditions.Where(c=>c.Arguments.Count>0 && c.Opcode is
                    ConditionOpcode.SectorCounterIs or ConditionOpcode.SectorCounterIsNot or
                    ConditionOpcode.SectorCounterIsLessThan or ConditionOpcode.SectorCounterIsGreaterThan))
                {
                    int inherited=world.Sectors.GetValueOrDefault(_state.CurrentZoneId)?.Zone==owner.Zone
                        ? _state.CurrentZoneId : owner.Zone;
                    int value=_state.GetSectorCounter(inherited), wanted=guard.Arguments[0];
                    bool matches=guard.Opcode switch {
                        ConditionOpcode.SectorCounterIsNot=>value!=wanted,
                        ConditionOpcode.SectorCounterIsLessThan=>value<wanted,
                        ConditionOpcode.SectorCounterIsGreaterThan=>value>wanted,_=>value==wanted };
                    if(!matches && !rooms.Where(z=>world.Sectors.GetValueOrDefault(z.Id)?.Zone==owner.Zone)
                        .Any(z=>z.Actions.Select((a,i)=>(a,i)).Any(p=>
                            !_state.DisabledActions.Contains((z.Id,p.i)) &&
                            !p.a.Conditions.Any(c=>c.Opcode==ConditionOpcode.ZoneNotInitialized) &&
                            p.a.Instructions.Any(i=>i.Opcode is InstructionOpcode.SetSectorCounter or InstructionOpcode.AddToSectorCounter))))
                        return null;
                }
                // Initialization must run through genuine entry.
                if(!_state.VisitedZones.Contains(zone.Id))
                {
                    // Initialization may move props or reveal the actor. Enter
                    // through a reachable component before requiring a path to
                    // the not-yet-initialized interaction square.
                    if(_state.CurrentZoneId!=zone.Id) return NavigateToward(world,zone.Id,Root(zone.Id));
                    var point=action.Conditions.FirstOrDefault(c=>c.Arguments.Count>=2 &&
                        c.Opcode is ConditionOpcode.Bump or ConditionOpcode.StandingOn or ConditionOpcode.PlacedItemIs);
                    var tile=action.Conditions.FirstOrDefault(c=>c.Arguments.Count>=4 &&
                        c.Opcode is ConditionOpcode.TileAtIs or ConditionOpcode.IsVariable);
                    int px=point?.Arguments[0] ?? tile?.Arguments[1] ?? zone.Width/2;
                    int py=point?.Arguments[1] ?? tile?.Arguments[2] ?? zone.Height/2;
                    return At(zone.Id,px,py,true,()=>new BotObjective { Type=ObjectiveType.PlanningBlocked,
                        Description="Enter the authored mission room before resolving its initializer" });
                }
                if(action.Conditions.Any(c=>c.Opcode==ConditionOpcode.ZoneNotInitialized)) return null;
                var placements=action.Conditions.Where(c=>c.Opcode==ConditionOpcode.TileAtIs && c.Arguments.Count>=4 &&
                    c.Arguments[3]==1 && c.Arguments[0]>=0 && c.Arguments[0]<_gameData.Tiles.Count &&
                    _gameData.Tiles[c.Arguments[0]].IsDraggable)
                    .Select(c=>new BlockPuzzlePlanner.Placement(c.Arguments[1],c.Arguments[2],c.Arguments[0])).ToArray();
                if(zone.Id!=_state.CurrentZoneId && StateAllows(zone.Id,action) &&
                    placements.Any(p=>zone.GetTile(p.X,p.Y,1)!=p.Tile))
                {
                    _nativeGoalFocus ??= (zone.Id,index);
                    return NavigateToward(world,zone.Id,Root(zone.Id),placements[0].X,placements[0].Y,adjacent:true);
                }
                if(zone.Id==_state.CurrentZoneId && StateAllows(zone.Id,action) &&
                    placements.Any(p=>zone.GetTile(p.X,p.Y,1)!=p.Tile))
                {
                    // All block predicates belong to one arrangement. Solving them
                    // separately can undo a placement while preparing the next one.
                    var objective=new BotObjective { Type=ObjectiveType.ClearObject,
                        TargetX=placements[0].X,TargetY=placements[0].Y,
                        Description=$"Arrange all blocks for mission script {index} in zone {zone.Id}" };
                    if(_nativeArrangement==(zone.Id,index) && _blockPlan.Count>0 &&
                        PlannedBlockMove(objective) is { } continuing) return continuing;
                    string arrangementKey=$"{zone.Id}:{index}:{_state.PlayerX},{_state.PlayerY}:{_state.SmoothMovementEnabled}:"+
                        BlockPuzzlePlanner.Layout(zone)+":"+string.Join(';',_state.ZoneNPCs.Where(n=>n.IsEnabled && n.IsAlive &&
                            !n.IsHostile && n.Behavior is NPCBehavior.Stationary or NPCBehavior.Animation).Select(n=>$"{n.X},{n.Y}"));
                    var plan=_failedNativeArrangements.Contains(arrangementKey) ? null :
                        BlockPuzzlePlanner.Plan(_gameData,zone,_state,Array.Empty<(int X,int Y)>(),placements,maxStates:120000);
                    if(plan is { Count: >0 })
                    {
                        _nativeArrangement=(zone.Id,index);
                        _blockPlan.Clear(); _blockPlanZone=zone.Id;
                        foreach(var move in plan) _blockPlan.Enqueue(move);
                        return PlannedBlockMove(objective);
                    }
                    _failedNativeArrangements.Add(arrangementKey);
                    // A block route can be locked even when Luke can already
                    // approach the goal. Prove the arrangement with authored
                    // removable walls open, then execute the real unlock first.
                    string relaxedKey=arrangementKey+":native-openings";
                    if(!_failedNativeArrangements.Contains(relaxedKey))
                    {
                        var grid=(ushort[,,])zone.TileGrid.Clone();
                        var openings=new HashSet<(int X,int Y)>();
                        foreach(var candidate in zone.Actions.Where(p=>!p.Conditions.Any(c=>c.Opcode==ConditionOpcode.ZoneNotInitialized) &&
                            !_state.DisabledActions.Contains((zone.Id,zone.Actions.IndexOf(p))) && TemplateGuardsMatch(world,zone,p)))
                        foreach(var remove in candidate.Instructions.Where(i=>i.Opcode==InstructionOpcode.RemoveTile &&
                            i.Arguments.Count>=3 && i.Arguments[2]==1))
                        {
                            int ox=remove.Arguments[0],oy=remove.Arguments[1];
                            int cover=zone.GetTile(ox,oy,1);
                            if(ox<0 || oy<0 || ox>=zone.Width || oy>=zone.Height || cover>=_gameData.Tiles.Count ||
                                !_gameData.Tiles[cover].IsObject || _gameData.Tiles[cover].IsDraggable ||
                                !ActionWritesTile(zone,candidate,ox,oy,1,-1)) continue;
                            grid[oy,ox,1]=ushort.MaxValue; openings.Add((ox,oy));
                        }
                        var relaxed=new Zone { Id=zone.Id,Width=zone.Width,Height=zone.Height,
                            TileGrid=grid,Objects=zone.Objects,Actions=zone.Actions };
                        if(openings.Count>0 && BlockPuzzlePlanner.Plan(_gameData,relaxed,_state,
                            Array.Empty<(int X,int Y)>(),placements,maxStates:120000) is { Count: >0 })
                            foreach(var opening in openings.OrderBy(p=>Math.Abs(p.X-_state.PlayerX)+Math.Abs(p.Y-_state.PlayerY)))
                                if(Tile(zone,opening.X,opening.Y,1,-1,depth+1) is { } unlock && unlock.Type is not
                                    (ObjectiveType.Explore or ObjectiveType.PlanningBlocked or ObjectiveType.UnresolvedReward)) return unlock;
                        _failedNativeArrangements.Add(relaxedKey);
                    }
                    var approaches=placements.SelectMany(p=>new[] { (p.X,p.Y-1),(p.X,p.Y+1),(p.X-1,p.Y),(p.X+1,p.Y) }).ToArray();
                    if(GraphTravel(world,zone.Id,approaches) is { } passage) return passage;
                }
                bool arrival=action.Conditions.Any(c=>c.Opcode is ConditionOpcode.EnterByPlane or ConditionOpcode.ZoneEntered);
                // Keep the selected prerequisite while preparing its guards.
                // Rebuilding the outer goal each tick can otherwise alternate
                // between mutually exclusive switch configurations.
                if(arrival && _nativeGoalFocus==null && action.Instructions.Any(i=>i.Opcode==InstructionOpcode.DisableAction))
                    _nativeGoalFocus=(zone.Id,index);
                // Check persistent prerequisites before preparing transient
                // counters or moving Luke. Otherwise an impossible random-drop
                // branch can repeatedly bump a prop whose initializer chose a
                // different reward location.
                foreach(var c in action.Conditions.OrderBy(c=>c.Opcode switch
                {
                    ConditionOpcode.Bump=>0, // Validate the target before moving to its authored stance.
                    ConditionOpcode.StandingOn=>3,
                    ConditionOpcode.HeroIsAt=>2,
                    ConditionOpcode.CounterIs or ConditionOpcode.SectorCounterIs or ConditionOpcode.SectorCounterIsLessThan or
                        ConditionOpcode.SectorCounterIsGreaterThan when placements.Length>0=>-1,
                    ConditionOpcode.CounterIs or ConditionOpcode.RandomIs or ConditionOpcode.SectorCounterIs or
                        ConditionOpcode.SectorCounterIsLessThan or ConditionOpcode.SectorCounterIsGreaterThan=>1,
                    _=>0,
                }).ThenBy(c=>c.Opcode==ConditionOpcode.TileAtIs && c.Arguments.Count>=4 && c.Arguments[3]==0 &&
                    zone.Actions.Any(p=>p.Conditions.Any(g=>g.Opcode==ConditionOpcode.StandingOn && g.Arguments.Count>=2 &&
                        g.Arguments[0]==c.Arguments[1] && g.Arguments[1]==c.Arguments[2]))
                    ? zone.Actions.Count(p=>p.Instructions.Any(i=>i.Opcode==InstructionOpcode.SetCounter) &&
                        p.Conditions.Any(g=>g.Opcode==ConditionOpcode.TileAtIs && g.Arguments.Count>=4 &&
                            g.Arguments.Take(4).SequenceEqual(c.Arguments.Take(4)))) : 0))
                {
                    var a=c.Arguments;
                    switch(c.Opcode)
                    {
                        case ConditionOpcode.ZoneEntered:
                        case ConditionOpcode.EnterByPlane:
                            break;
                        case ConditionOpcode.Bump:
                        case ConditionOpcode.StandingOn:
                            if(a.Count<3) return null;
                            int layer=c.Opcode==ConditionOpcode.Bump ? 1 : 0;
                            if(zone.GetTile(a[0],a[1],layer)!=unchecked((ushort)a[2]))
                                return Tile(zone,a[0],a[1],layer,a[2],depth+1);
                            break;
                        case ConditionOpcode.TileAtIs:
                        case ConditionOpcode.IsVariable:
                            if(a.Count<4) return null;
                            if(ScriptedActorTiles.Read(_gameData,_state,zone,a[1],a[2],a[3])!=unchecked((ushort)a[0]))
                                return Tile(zone,a[1],a[2],a[3],a[0],depth+1);
                            break;
                        case ConditionOpcode.IndyGameWon:
                            if(!_state.IsGameWon) return null;
                            break;
                        case ConditionOpcode.IndyGameLost:
                            if(!_state.IsGameOver) return null;
                            break;
                        case ConditionOpcode.StartingItemIs:
                            if(a.Count<1 || (world.StartingItemId ?? world.Mission?.PuzzleChain.FirstOrDefault()?.RewardItemId)!=a[0]) return null;
                            break;
                        case ConditionOpcode.ZoneIsSolved:
                            if(!_state.IsZoneSolved(zone.Id) && world.Sectors.GetValueOrDefault(zone.Id)?.Solved!=true)
                            {
                                foreach(var producer in zone.Actions.Where(p=>p!=action &&
                                    !p.Conditions.Any(c=>c.Opcode==ConditionOpcode.ZoneIsSolved) &&
                                    p.Instructions.Any(i=>i.Opcode==InstructionOpcode.MarkAsSolved)))
                                    if(Action(zone,producer,depth+1) is { } solve) return solve;
                                // Collecting a generated quest drop also solves
                                // its sector through the normal pickup handler.
                                if(world.Sectors.GetValueOrDefault(zone.Id) is { FindItem: >0 } rewardSector &&
                                    !_state.HasItem(rewardSector.FindItem) &&
                                    Item(rewardSector.FindItem,depth+1) is { } collect) return collect;
                                return null;
                            }
                            break;
                        case ConditionOpcode.RequiredItemIs:
                            if(a.Count<1 || world.Sectors.GetValueOrDefault(zone.Id) is not { } requiredSector ||
                                requiredSector.RequiredItem!=a[0] && requiredSector.AdditionalRequiredItem!=a[0]) return null;
                            break;
                        case ConditionOpcode.FindItemIs:
                            if(a.Count<1 || world.Sectors.GetValueOrDefault(zone.Id)?.FindItem!=a[0]) return null;
                            break;
                        case ConditionOpcode.HasItem:
                            if(a.Count<1) return null;
                            int needed=a[0]==-1 ? world.Sectors.GetValueOrDefault(zone.Id)?.FindItem ?? -1 : a[0];
                            if(needed<0) return null;
                            if(!_state.HasItem(needed)) return Item(needed,depth+1);
                            break;
                        case ConditionOpcode.RandomIsGreaterThan:
                        case ConditionOpcode.RandomIsLessThan:
                        case ConditionOpcode.RandomIsNot:
                        case ConditionOpcode.CounterIsNot:
                            if(a.Count<1) return null;
                            int tested=c.Opcode==ConditionOpcode.CounterIsNot ? _state.GetCounter(zone.Id) :
                                _state.ZoneRandom.GetValueOrDefault(zone.Id);
                            bool accepted=c.Opcode switch {
                                ConditionOpcode.RandomIsGreaterThan=>tested>a[0],
                                ConditionOpcode.RandomIsLessThan=>tested<a[0], _=>tested!=a[0] };
                            if(!accepted) return null;
                            break;
                        case ConditionOpcode.HealthIsGreaterThan:
                            if(a.Count<1 || _state.Health<=a[0]) return null;
                            break;
                        case ConditionOpcode.HealthIsLessThan:
                            if(a.Count<1 || _state.Health>=a[0]) return null;
                            break;
                        case ConditionOpcode.PlacedItemIs:
                            if(a.Count<5) return null;
                            int item=ResolvePlacedItemId(zone,a);
                            if(item<0) return null;
                            if(!_state.HasItem(item)) return Item(item,depth+1);
                            if(a[3]!=-1 && zone.GetTile(a[0],a[1],a[2])!=unchecked((ushort)a[3]))
                                return Tile(zone,a[0],a[1],a[2],a[3],depth+1);
                            break;
                        case ConditionOpcode.HeroIsAt:
                            if(a.Count<2) return null;
                            if(arrival) break; // Resolve the actual arrival below.
                            if(_state.CurrentZoneId!=zone.Id || _state.PlayerX!=a[0] || _state.PlayerY!=a[1])
                            {
                                var approach=At(zone.Id,a[0],a[1],false,()=>Focus(new BotObjective { Type=ObjectiveType.WalkToPosition,
                                    TargetX=a[0],TargetY=a[1],Description="Stand at the authored mission interaction" }));
                                if(approach!=null && approach.Type is not
                                    (ObjectiveType.Explore or ObjectiveType.PlanningBlocked or ObjectiveType.UnresolvedReward))
                                    _nativeGoalFocus ??= (zone.Id,index);
                                return approach;
                            }
                            break;
                        case ConditionOpcode.MonsterIsDead:
                            if(a.Count<1 || a[0]<0) return null;
                            if(_state.CurrentZoneId!=zone.Id) return NavigateToward(world,zone.Id,Root(zone.Id));
                            var monster=_state.ZoneNPCs.ElementAtOrDefault(a[0]);
                            if(monster==null) return null;
                            if(!monster.IsAlive) break;
                            if(monster.IsEnabled && monster.IsHostile)
                                return At(zone.Id,monster.X,monster.Y,true,()=>new BotObjective {
                                    Type=ObjectiveType.KillEnemy,TargetNpc=monster,TargetX=monster.X,TargetY=monster.Y,
                                    Description=$"Defeat monster {a[0]} required by mission script {index}" });
                            foreach(var producer in zone.Actions.Where(p=>p.Instructions.Any(i=>i.Opcode==InstructionOpcode.EnableMonster &&
                                i.Arguments.Count>0 && i.Arguments[0]==a[0] || i.Opcode==InstructionOpcode.EnableAllMonsters)))
                                if(Action(zone,producer,depth+1) is { } enable) return enable;
                            return null;
                        case ConditionOpcode.HasNoActiveMonsters:
                            if(_state.CurrentZoneId!=zone.Id) return NavigateToward(world,zone.Id,Root(zone.Id));
                            if(_state.ZoneNPCs.Where(n=>n.IsEnabled && n.IsAlive && n.IsHostile)
                                .OrderBy(n=>n.DistanceTo(_state.PlayerX,_state.PlayerY)).FirstOrDefault() is { } enemy)
                                return At(zone.Id,enemy.X,enemy.Y,true,()=>new BotObjective {
                                    Type=ObjectiveType.KillEnemy,TargetNpc=enemy,TargetX=enemy.X,TargetY=enemy.Y,
                                    Description="Defeat the enemy required by the mission script" });
                            if(_state.ZoneNPCs.Any(n=>n.NativeMonsterIndex>=0 && n.IsAlive))
                            {
                                foreach(var producer in zone.Actions.Where(p=>p.Instructions.Any(i=>
                                    i.Opcode==InstructionOpcode.EnableAllMonsters ||
                                    i.Opcode==InstructionOpcode.EnableMonster && i.Arguments.Count>0 &&
                                    _state.ZoneNPCs.Any(n=>n.NativeMonsterIndex==i.Arguments[0] && n.IsAlive))))
                                    if(Action(zone,producer,depth+1) is { } activate) return activate;
                                // Disabled future waves are still alive. They
                                // cannot satisfy the native all-enemies-dead guard.
                                return null;
                            }
                            break;
                        case ConditionOpcode.CounterIs:
                        case ConditionOpcode.RandomIs:
                        case ConditionOpcode.SectorCounterIs:
                        case ConditionOpcode.SectorCounterIsNot:
                        case ConditionOpcode.SectorCounterIsLessThan:
                        case ConditionOpcode.SectorCounterIsGreaterThan:
                            if(a.Count<1) return null;
                            bool sectorCounter=c.Opcode is ConditionOpcode.SectorCounterIs or ConditionOpcode.SectorCounterIsNot or
                                ConditionOpcode.SectorCounterIsLessThan or ConditionOpcode.SectorCounterIsGreaterThan;
                            bool Matches(int value)=>c.Opcode switch {
                                ConditionOpcode.SectorCounterIsNot=>value!=a[0],
                                ConditionOpcode.SectorCounterIsLessThan=>value<a[0],
                                ConditionOpcode.SectorCounterIsGreaterThan=>value>a[0], _=>value==a[0] };
                            long Distance(int value)=>Matches(value) ? 0 : c.Opcode switch {
                                ConditionOpcode.SectorCounterIsGreaterThan=>(long)a[0]+1-value,
                                ConditionOpcode.SectorCounterIsLessThan=>(long)value-a[0]+1,
                                ConditionOpcode.SectorCounterIsNot=>1,
                                _=>Math.Abs((long)a[0]-value) };
                            int actual=c.Opcode==ConditionOpcode.CounterIs ? _state.GetCounter(zone.Id) :
                                sectorCounter ? _state.GetSectorCounter(zone.Id) : _state.ZoneRandom.GetValueOrDefault(zone.Id);
                            if(!Matches(actual))
                            {
                                if(sectorCounter && _state.CurrentZoneId!=zone.Id && Matches(_state.GetSectorCounter(_state.CurrentZoneId)))
                                {
                                    var effect=action.Instructions.FirstOrDefault(i=>i.Arguments.Count>=3 &&
                                        i.Opcode is InstructionOpcode.RemoveTile or InstructionOpcode.PlaceTile);
                                    if(effect!=null) return At(zone.Id,effect.Arguments[0],effect.Arguments[1],true,
                                        ()=>new BotObjective { Type=ObjectiveType.PlanningBlocked,
                                            Description="Wait for the native arrival prerequisite" });
                                    return NavigateToward(world,zone.Id,Root(zone.Id));
                                }
                                var opcode=c.Opcode==ConditionOpcode.CounterIs ? InstructionOpcode.SetCounter :
                                    sectorCounter ? InstructionOpcode.SetSectorCounter : InstructionOpcode.SetRandom;
                                bool AdvancesCounter(Instruction instruction)
                                {
                                    if(instruction.Arguments.Count==0) return false;
                                    int amount=instruction.Arguments[0];
                                    if(c.Opcode==ConditionOpcode.RandomIs) return CanProduceRandom(instruction,Matches);
                                    if(instruction.Opcode==opcode) return Matches(amount);
                                    if(c.Opcode==ConditionOpcode.CounterIs && instruction.Opcode==InstructionOpcode.AddToCounter)
                                        return Math.Abs((long)a[0]-actual-amount)<Math.Abs((long)a[0]-actual);
                                    return sectorCounter && instruction.Opcode==InstructionOpcode.AddToSectorCounter && amount!=0 &&
                                        Distance(_state.GetSectorCounter(_state.CurrentZoneId)+amount)<Distance(_state.GetSectorCounter(_state.CurrentZoneId));
                                }
                                foreach(var source in sectorCounter ? rooms.ToList() : new List<Zone> { zone })
                                foreach(var producer in source.Actions.Where(p=>p.Instructions.Any(AdvancesCounter) &&
                                    (!sectorCounter || ArrivalSectorCounter(world,source,p,actual) is not int afterEntry ||
                                     Distance(afterEntry)<Distance(actual)))
                                    .OrderBy(p=>NativeInputEffort(world,source,p))
                                    .ThenBy(p=>p.Conditions.Count(g=>g.Opcode is ConditionOpcode.TileAtIs or ConditionOpcode.IsVariable &&
                                        g.Arguments.Count>=4 && source.GetTile(g.Arguments[1],g.Arguments[2],g.Arguments[3])!=unchecked((ushort)g.Arguments[0]))))
                                {
                                    if(source.Id==zone.Id && SignalBlocker(zone,producer,action,c.Opcode,a[0]) is { } blocker)
                                    {
                                        // A one-shot introduction/battle may consume the
                                        // signal the first time. Run that real prerequisite
                                        // before trying the producer again after it disables.
                                        bool oneShot=blocker.Instructions.Any(i=>i.Opcode==InstructionOpcode.DisableAction) ||
                                            blocker.Conditions.Any(g=>g.Opcode is ConditionOpcode.TileAtIs or ConditionOpcode.IsVariable &&
                                                g.Arguments.Count>=4 && blocker.Instructions.Any(i=>i.Arguments.Count>=4 &&
                                                    i.Opcode is InstructionOpcode.PlaceTile or InstructionOpcode.SetVariable &&
                                                    i.Arguments[0]==g.Arguments[1] && i.Arguments[1]==g.Arguments[2] &&
                                                    i.Arguments[2]==g.Arguments[3] && i.Arguments[3]!=g.Arguments[0]));
                                        if(oneShot && Action(zone,blocker,depth+1) is { } first && first.Type is not
                                                (ObjectiveType.Explore or ObjectiveType.PlanningBlocked or ObjectiveType.UnresolvedReward)) return first;
                                        // A guarded reset can reject this signal until a
                                        // separate authored task changes its guard (for
                                        // example clearing the enemies before moving a prop).
                                        foreach(var gate in blocker.Conditions.Where(g=>g.Opcode is ConditionOpcode.TileAtIs or
                                            ConditionOpcode.IsVariable && g.Arguments.Count>=4))
                                        foreach(var unlock in zone.Actions.Where(p=>p!=blocker && p.Instructions.Any(i=>
                                            i.Arguments.Count>=4 && i.Opcode is InstructionOpcode.PlaceTile or InstructionOpcode.SetVariable &&
                                            i.Arguments[0]==gate.Arguments[1] && i.Arguments[1]==gate.Arguments[2] &&
                                            i.Arguments[2]==gate.Arguments[3] && i.Arguments[3]!=gate.Arguments[0])))
                                            if(Action(zone,unlock,depth+1) is { } open && open.Type is not
                                                (ObjectiveType.Explore or ObjectiveType.PlanningBlocked or ObjectiveType.UnresolvedReward)) return open;
                                        continue;
                                    }
                                    if(Action(source,producer,depth+1) is { } step && step.Type is not
                                        (ObjectiveType.Explore or ObjectiveType.PlanningBlocked or ObjectiveType.UnresolvedReward)) return step;
                                }
                                return null;
                            }
                            break;
                        default:
                            trace.Add($"unsupported guard {c.Opcode} in {zone.Id}:{index}");
                            return null; // Unsupported guards never become speculative actions.
                    }
                }
                if(arrival)
                {
                    var point=action.Conditions.FirstOrDefault(c=>c.Opcode==ConditionOpcode.HeroIsAt && c.Arguments.Count>=2);
                    if(point!=null)
                    {
                        foreach(var source in rooms.ToArray())
                        foreach(var producer in source.Actions.Where(p=>p.Instructions.Any(i=>
                            i.Opcode==InstructionOpcode.ChangeZone && i.Arguments.Count>=3 && i.Arguments[0]==zone.Id &&
                            i.Arguments[1]==point.Arguments[0] && i.Arguments[2]==point.Arguments[1])))
                            if(Action(source,producer,depth+1) is { } step) return step;
                        return null;
                    }
                    if(_state.CurrentZoneId!=zone.Id) return NavigateToward(world,zone.Id,Root(zone.Id));
                    // Re-enter through a known reciprocal connection to run the
                    // native entry guard. Do not execute arrival scripts directly.
                    bool needsDoor=action.Conditions.Any(c=>c.Opcode==ConditionOpcode.EnterByPlane) &&
                        !ZoneArrival.IsOutdoorIntroduction(_gameData.GameType,action);
                    foreach(var edge in ActionGraph!.Edges.Where(e=>e.From==zone.Id && e.To!=zone.Id &&
                        (!needsDoor || e.Instruction.Type==ObjectiveType.EnterDoor) &&
                        ActionGraph.Edges.Any(back=>back.From==e.To && back.To==zone.Id &&
                            (!needsDoor || back.Instruction.Type==ObjectiveType.EnterDoor))))
                    {
                        if(GraphTravel(world,edge.To) is { } leave) return leave;
                        // A reciprocal room can supply the required arrival
                        // phase after its authored entrance has been unlocked.
                        // Prepare that entrance instead of falling back to an
                        // outdoor crossing, which does not trigger this phase.
                        if(edge.Instruction.Type==ObjectiveType.EnterDoor &&
                            Tile(zone,edge.Instruction.TargetX,edge.Instruction.TargetY,1,-1,depth+1) is { } unlock &&
                            unlock.Type is not (ObjectiveType.Explore or ObjectiveType.PlanningBlocked or ObjectiveType.UnresolvedReward))
                            return unlock;
                    }
                    return null;
                }
                var placed=action.Conditions.FirstOrDefault(c=>c.Opcode==ConditionOpcode.PlacedItemIs);
                if(placed!=null)
                {
                    if(_state.CurrentZoneId!=zone.Id && _nativeGoalFocus==null) _nativeGoalFocus=(zone.Id,index);
                    var a=placed.Arguments; int item=ResolvePlacedItemId(zone,a);
                    if(item<0) return null;
                    // A -1 item operand resolves to this sector's required
                    // item. Acquire that exact dependency before attempting
                    // the placement; emitting UseAt for an absent item makes
                    // the bot retry a no-op interaction indefinitely.
                    if(!_state.HasItem(item) && Item(item,depth+1) is { } fetch) return fetch;
                    if(!_state.HasItem(item)) return null;
                    if(ScriptedWeaponUse.Range(_gameData,item)>0)
                    {
                        var stances=new List<(int X,int Y)>();
                        for(int distance=1;distance<=ScriptedWeaponUse.Range(_gameData,item);distance++)
                        foreach(var (dx,dy) in new[]{(0,-1),(0,1),(-1,0),(1,0)})
                        {
                            int sx=a[0]+dx*distance,sy=a[1]+dy*distance;
                            if(sx>=0 && sy>=0 && sx<zone.Width && sy<zone.Height &&
                                ScriptedWeaponUse.CanAim(_gameData,zone,item,sx,sy,a[0],a[1])) stances.Add((sx,sy));
                        }
                        var reachableAim=zone.Id==_state.CurrentZoneId ? StaticWalkingArea() : null;
                        foreach(var stance in stances.OrderBy(p=>reachableAim?.Contains(p)==true ? 0 : 1)
                            .ThenBy(p=>Math.Abs(p.X-_state.PlayerX)+Math.Abs(p.Y-_state.PlayerY)))
                            if(At(zone.Id,stance.X,stance.Y,false,()=>Focus(new BotObjective { Type=ObjectiveType.AttackTile,
                                TargetX=a[0],TargetY=a[1],RequiredItemId=item,ApproachX=stance.X,ApproachY=stance.Y,
                                Description=$"Aim weapon {item} at authored target ({a[0]},{a[1]})" })) is { } attack &&
                                attack.Type is not (ObjectiveType.Explore or ObjectiveType.PlanningBlocked or ObjectiveType.UnresolvedReward)) return attack;
                        return null;
                    }
                    return At(zone.Id,a[0],a[1],true,()=>
                    {
                        var use=UseAt(item,a[0],a[1],"Satisfy the authored mission condition");
                        var drop=action.Instructions.FirstOrDefault(i=>i.Opcode==InstructionOpcode.DropItem && i.Arguments.Count>=3);
                        int reward=drop==null ? -1 : drop.Arguments[0]==-1
                            ? world.Sectors.GetValueOrDefault(zone.Id)?.FindItem ?? -1 : drop.Arguments[0];
                        if(reward>0) use.ExpectedRewardItemId=reward;
                        return Focus(use);
                    },true,ItemUseReach.Extended(_gameData,item));
                }
                var trigger=action.Conditions.FirstOrDefault(c=>c.Opcode is ConditionOpcode.Bump or ConditionOpcode.StandingOn);
                if(trigger!=null)
                {
                    // Keep a selected authored dependency across zone changes.
                    // Choosing a different branch at each intermediate entrance
                    // can otherwise send the actor straight back along its route.
                    if(_state.CurrentZoneId!=zone.Id && _nativeGoalFocus==null) _nativeGoalFocus=(zone.Id,index);
                    var a=trigger.Arguments;
                    if(trigger.Opcode==ConditionOpcode.Bump && zone.Id==_state.CurrentZoneId)
                    {
                        (int X,int Y,int Layer,int Value)? Cell(Condition c) => c.Opcode==ConditionOpcode.Bump && c.Arguments.Count>=3
                            ? (c.Arguments[0],c.Arguments[1],1,c.Arguments[2])
                            : c.Opcode is ConditionOpcode.TileAtIs or ConditionOpcode.IsVariable && c.Arguments.Count>=4
                                ? (c.Arguments[1],c.Arguments[2],c.Arguments[3],c.Arguments[0]) : null;
                        bool Writes(Data.Action p,(int X,int Y,int Layer,int Value) cell) => p.Instructions.Any(i=>i.Arguments.Count>=3 &&
                            i.Arguments[2]==cell.Layer && (i.Opcode is InstructionOpcode.PlaceTile or InstructionOpcode.SetVariable or InstructionOpcode.RemoveTile &&
                                i.Arguments[0]==cell.X && i.Arguments[1]==cell.Y ||
                                i.Opcode==InstructionOpcode.MoveTile && i.Arguments.Count>=5 &&
                                (i.Arguments[0]==cell.X && i.Arguments[1]==cell.Y || i.Arguments[3]==cell.X && i.Arguments[4]==cell.Y)));
                        bool EarlierMatches(Data.Action p) => TemplateGuardsMatch(world,zone,p) && StateAllows(zone.Id,p) &&
                            p.Conditions.All(c=>Cell(c) is { } cell
                                ? zone.GetTile(cell.X,cell.Y,cell.Layer)==unchecked((ushort)cell.Value)
                                : c.Opcode switch {
                                    ConditionOpcode.CounterIs or ConditionOpcode.CounterIsNot or ConditionOpcode.RandomIs or
                                        ConditionOpcode.RandomIsNot or ConditionOpcode.RandomIsGreaterThan or ConditionOpcode.RandomIsLessThan or
                                        ConditionOpcode.RequiredItemIs or ConditionOpcode.FindItemIs or ConditionOpcode.StartingItemIs=>true,
                                    ConditionOpcode.HasItem when c.Arguments.Count>0=>_state.HasItem(c.Arguments[0]==-1 ? sector.RequiredItem : c.Arguments[0]),
                                    _=>false });
                        foreach(var (earlier,earlierIndex) in zone.Actions.Take(index).Select((p,i)=>(p,i)))
                        {
                            if(_state.DisabledActions.Contains((zone.Id,earlierIndex)) || !earlier.Conditions.Any(c=>c.Opcode==ConditionOpcode.Bump &&
                                c.Arguments.Count>=3 && c.Arguments[0]==a[0] && c.Arguments[1]==a[1]) || !EarlierMatches(earlier)) continue;
                            bool clobbers=action.Conditions.Select(Cell).Where(c=>c.HasValue).Select(c=>c!.Value).Any(cell=>
                                Writes(earlier,cell) && !ActionWritesTile(zone,earlier,cell.X,cell.Y,cell.Layer,cell.Value) &&
                                !zone.Actions.Skip(earlierIndex+1).Take(index-earlierIndex-1).Any(p=>
                                    Writes(p,cell) && ActionWritesTile(zone,p,cell.X,cell.Y,cell.Layer,cell.Value)));
                            if(!clobbers) continue;
                            // Native scripts run in order. An earlier matching bump
                            // can replace a guard before the desired action sees it.
                            foreach(var guard in earlier.Conditions.Where(c=>c.Opcode is ConditionOpcode.TileAtIs or ConditionOpcode.IsVariable))
                            {
                                if(Cell(guard) is not { } cell) continue;
                                if(cell.Layer==1 && cell.Value!=-1 && Tile(zone,cell.X,cell.Y,1,-1,depth+1) is { } clear &&
                                    clear.Type is not (ObjectiveType.Explore or ObjectiveType.PlanningBlocked or ObjectiveType.UnresolvedReward)) return clear;
                                foreach(var producer in zone.Actions.Where(p=>p!=earlier && Writes(p,cell) &&
                                    !ActionWritesTile(zone,p,cell.X,cell.Y,cell.Layer,cell.Value)))
                                    if(Action(zone,producer,depth+1) is { } avoid && avoid.Type is not
                                        (ObjectiveType.Explore or ObjectiveType.PlanningBlocked or ObjectiveType.UnresolvedReward)) return avoid;
                            }
                            return null;
                        }
                    }
                    var stance=action.Conditions.FirstOrDefault(c=>c.Opcode==ConditionOpcode.HeroIsAt && c.Arguments.Count>=2);
                    // A bump that begins an authored boarding/takeoff sequence
                    // is a transport edge, not a puzzle action to repeat. The
                    // graph already models its destination; treating the
                    // animation trigger as a mission goal makes the bot board
                    // and immediately return forever.
                    if(trigger.Opcode==ConditionOpcode.Bump && ScriptedTransport.StartsFlight(zone,action,_state))
                        return null;
                    var instruction=new BotObjective {
                        Type=trigger.Opcode==ConditionOpcode.Bump ? ObjectiveType.BumpTarget : ObjectiveType.WalkToPosition,
                        TargetX=a[0],TargetY=a[1],ApproachX=stance?.Arguments[0],ApproachY=stance?.Arguments[1],
                        ScriptIndex=index,
                        AvoidFloorSwitches=trigger.Opcode==ConditionOpcode.StandingOn,
                        Description=$"Trigger mission script {index} in zone {zone.Id}" };
                    // HeroIsAt was resolved above. Preserve that authored stance
                    // instead of substituting a different cardinal neighbor.
                    if(stance!=null && trigger.Opcode==ConditionOpcode.Bump) return Focus(instruction);
                    return At(zone.Id,a[0],a[1],trigger.Opcode==ConditionOpcode.Bump,()=>Focus(instruction),
                        diagonalItem:trigger.Opcode==ConditionOpcode.Bump);
                }
                // Automatic producers run when their area is simulated. A
                // ready action in another visited room still requires returning
                // there; treating it as impossible can hide an enabled doorway.
                if(action.Instructions.Any(i=>i.Opcode is InstructionOpcode.EnableHotspot or InstructionOpcode.DisableHotspot or
                    InstructionOpcode.PlaceTile or InstructionOpcode.RemoveTile or InstructionOpcode.MoveTile or
                    InstructionOpcode.SetVariable or InstructionOpcode.SetCounter or InstructionOpcode.AddToCounter or
                    InstructionOpcode.SetRandom or InstructionOpcode.RollDice or InstructionOpcode.SetSectorCounter or
                    InstructionOpcode.AddToSectorCounter or InstructionOpcode.AddItem or InstructionOpcode.DropItem or
                    InstructionOpcode.EnableMonster or InstructionOpcode.DisableMonster or InstructionOpcode.WinGame))
                {
                    if(_state.CurrentZoneId!=zone.Id) return NavigateToward(world,zone.Id,Root(zone.Id));
                    return new BotObjective { Type=ObjectiveType.WaitForActor,
                        Description=$"Wait for automatic mission script {index} in zone {zone.Id}" };
                }
                return null;
                BotObjective Focus(BotObjective step)
                {
                    _nativeGoalFocus=(zone.Id,index);
                    return step;
                }
            }
            finally { visiting.Remove(key); }
        }

        // Route preparation uses the same authored guard/producer search as
        // finales, without switching to an unrelated mission interaction.
        if(interaction is { } requestedInteraction)
        {
            int interactionZone=requestedInteraction.TargetZoneId ?? _state.CurrentZoneId;
            var targetZone=_gameData.Zones[interactionZone];
            var authored=targetZone.Actions.Select((a,i)=>(a,i)).Where(p=>
                !_state.DisabledActions.Contains((interactionZone,p.i)) && StateAllows(interactionZone,p.a) &&
                AdvancesInteraction(targetZone,p.a,new()) && p.a.Conditions.Any(c=>
                    c.Opcode==ConditionOpcode.PlacedItemIs && c.Arguments.Count>=5 &&
                    c.Arguments[0]==requestedInteraction.TargetX && c.Arguments[1]==requestedInteraction.TargetY &&
                    (c.Arguments[4]==requestedInteraction.RequiredItemId || c.Arguments[4]==-1 &&
                        requestedInteraction.RequiredItemId==sector.RequiredItem)))
                // Prefer the authored exchange branch over early flavor or
                // preparation branches that accept any placed item.
                .OrderByDescending(p=>p.a.Conditions.Any(c=>c.Opcode==ConditionOpcode.HasNoActiveMonsters))
                .ThenByDescending(p=>p.a.Instructions.Any(i=>i.Opcode==InstructionOpcode.RemoveItem))
                .ThenBy(p=>p.i).FirstOrDefault();
            if(authored.a!=null) return Action(targetZone,authored.a,0);
            return At(requestedInteraction.TargetZoneId ?? _state.CurrentZoneId,requestedInteraction.TargetX,
                requestedInteraction.TargetY,true,()=>requestedInteraction,true);
        }
        if(authoredAction is { } requested)
        {
            var result=Action(_gameData.Zones[requested.Zone],_gameData.Zones[requested.Zone].Actions[requested.Index],0);
            if(result==null)
                Console.WriteLine($"[BOT] Authored prerequisite unresolved: {string.Join(" -> ",trace.TakeLast(100))}");
            return result;
        }
        if(openingTile is { } door)
        {
            var prerequisite=Tile(_gameData.Zones[door.Zone],door.X,door.Y,1,-1,0);
            if(prerequisite==null)
            {
                int required=world.Sectors.GetValueOrDefault(door.Zone)?.RequiredItem ?? -1;
                Console.WriteLine($"[BOT] Native doorway prerequisite unresolved at zone {door.Zone} ({door.X},{door.Y}); required item {required}; trace: {string.Join(" -> ",trace.TakeLast(40))}");
            }
            return prerequisite;
        }
        if(rewardItem is int reward)
            return Item(reward,0);

        if(_nativeGoalFocus is { } focus)
        {
            if(!_state.DisabledActions.Contains((focus.Zone,focus.Action)) &&
                Action(_gameData.Zones[focus.Zone],_gameData.Zones[focus.Zone].Actions[focus.Action],0) is { } pending &&
                pending.Type is not (ObjectiveType.Explore or ObjectiveType.PlanningBlocked or ObjectiveType.UnresolvedReward))
                return pending;
            _nativeGoalFocus=null;
        }
        if(_nativeGoalContinuation is { } continuation)
        {
            if(!_state.DisabledActions.Contains((continuation.Zone,continuation.Action)) &&
                Action(_gameData.Zones[continuation.Zone],_gameData.Zones[continuation.Zone].Actions[continuation.Action],0) is { } next &&
                next.Type is not (ObjectiveType.Explore or ObjectiveType.PlanningBlocked or ObjectiveType.UnresolvedReward)) return next;
            _nativeGoalContinuation=null;
        }
        if(sector.Type!=ZoneType.Goal) return null;

        BotObjective? blockedHeldItem=null;
        foreach (int item in new[] { sector.AdditionalRequiredItem, sector.RequiredItem }.Where(i=>i>0 && _state.HasItem(i)))
        {
            bool interactionAlreadyAttempted=_consumedInteractions.Contains((sector.Zone,item));
            bool consumable=rooms.Any(r=>r.Actions.Any(p=>p.Instructions.Any(i=>
                i.Opcode==InstructionOpcode.RemoveItem && i.Arguments.Count>0 &&
                (i.Arguments[0]==item || i.Arguments[0]==-1 && sector.RequiredItem==item))));
            // Reusable tools are prerequisites of specific authored guards,
            // rather than exchanges to repeat merely because they are owned.
            if(!consumable) continue;
            if (!interactionAlreadyAttempted && ScriptedWeaponUse.Range(_gameData,item)==0 && FindUseSpot(world,sector.Zone,item) is { } use)
            {
                var preparation=At(use.zone,use.x,use.y,true,()=>UseAt(item,use.x,use.y,"Advance the authored mission goal"),true);
                if(preparation!=null && preparation.Type is not
                    (ObjectiveType.Explore or ObjectiveType.PlanningBlocked or ObjectiveType.UnresolvedReward)) return preparation;
                blockedHeldItem ??= preparation;
            }
            // The receiver may need a greeting, switch or relocation before its
            // use tile exists. Resolve that script, rather than routing straight
            // to the eventual prize while still carrying a required quest item.
            foreach(var zone in rooms.ToArray())
            foreach(var action in zone.Actions.Where(a=>AdvancesInteraction(zone,a,new()) &&
                (!interactionAlreadyAttempted || a.Conditions.Any(c=>c.Opcode==ConditionOpcode.HasNoActiveMonsters) ||
                    a.Instructions.Any(i=>i.Opcode==InstructionOpcode.RemoveItem && i.Arguments.Count>0 &&
                        (i.Arguments[0]==item || i.Arguments[0]==-1 && sector.RequiredItem==item))) &&
                a.Conditions.Any(c=>c.Opcode==ConditionOpcode.PlacedItemIs && c.Arguments.Count>=5 &&
                    (c.Arguments[4]==item || c.Arguments[4]==-1 && sector.RequiredItem==item))))
                if(Action(zone,action,0) is { } preparation && preparation.Type is not
                    (ObjectiveType.Explore or ObjectiveType.PlanningBlocked or ObjectiveType.UnresolvedReward))
                    return preparation;
        }

        BotObjective? blockedEnding=null;
        foreach(var zone in rooms.ToArray()) foreach(var action in zone.Actions.Where(a=>a.Instructions.Any(i=>i.Opcode==InstructionOpcode.WinGame)))
            if(Action(zone,action,0) is { } step)
            {
                if(step.Type is ObjectiveType.Explore or ObjectiveType.PlanningBlocked or ObjectiveType.UnresolvedReward)
                {
                    blockedEnding ??= step;
                    _nativeGoalFocus=null;
                    continue;
                }
                return step;
            }
        if(blockedEnding!=null || blockedHeldItem!=null) return blockedEnding ?? blockedHeldItem;
        Console.WriteLine($"[BOT] Goal prerequisite search unresolved: {string.Join(" -> ",trace.TakeLast(100))}");
        return null;
    }
}
