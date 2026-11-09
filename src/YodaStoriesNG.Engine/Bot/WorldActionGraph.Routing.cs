using YodaStoriesNG.Engine.Data;
using YodaStoriesNG.Engine.Game;

namespace YodaStoriesNG.Engine.Bot;

public sealed partial class WorldActionGraph
{
    private GameData _data = null!;
    private GameState _state = null!;
    private WorldMap? _world;
    private Func<Zone,int,int,bool>? _canWalk;
    private IReadOnlyDictionary<int,(int Zone,int X,int Y)> _doorReturnPoints = new Dictionary<int,(int Zone,int X,int Y)>();
    private readonly Dictionary<string,bool> _objectApproaches = new();

    private bool CanMoveBlocksTo(Zone zone, HashSet<(int X,int Y)> reach,
        IReadOnlyCollection<(int X,int Y)> targets, (int X,int Y)? transition = null)
    {
        // Object preparation is evaluated from the actor's real component. Other
        // areas are revisited after entry, rather than searching every hypothetical
        // rock arrangement throughout the entire world in a gameplay update.
        if (zone.Id != _state.CurrentZoneId || !reach.Contains((_state.PlayerX,_state.PlayerY))) return false;
        if (targets.Count == 0) return false;
        if (!Enumerable.Range(0,zone.Height).Any(y => Enumerable.Range(0,zone.Width).Any(x =>
            zone.GetTile(x,y,1) is var t && t<_data.Tiles.Count && _data.Tiles[t].IsDraggable))) return false;
        var start=reach.OrderBy(p=>p.Y).ThenBy(p=>p.X).First();
        string key=$"{zone.Id}:{start}:{transition}:"+string.Join(';',targets.OrderBy(p=>p.Y).ThenBy(p=>p.X));
        if (_objectApproaches.TryGetValue(key,out bool possible)) return possible;
        var simulation=new GameState { CurrentZone=zone,CurrentZoneId=zone.Id,
                    Counters=_state.Counters,Variables=_state.Variables,ZoneRandom=_state.ZoneRandom,
                    VisitedZones=_state.VisitedZones,DisabledActions=_state.DisabledActions,
            PlayerX=start.X,PlayerY=start.Y,SmoothMovementEnabled=_state.SmoothMovementEnabled,
            ZoneNPCs=zone.Id==_state.CurrentZoneId ? _state.ZoneNPCs : new() };
        possible=BlockPuzzlePlanner.Plan(_data,zone,simulation,targets,
            Array.Empty<BlockPuzzlePlanner.Placement>(),transitionTarget:transition) is { Count: >0 };
        _objectApproaches[key]=possible;
        return possible;
    }

    // Some rooms choose doors or remove walls on first entry. Their untouched
    // template cannot prove that the final interaction cell is reachable yet.
    // Stage only toward an unvisited initializer on this target's dependency route.
    public IReadOnlyList<Edge> RouteToInitialization(int from, int to, Func<Edge, bool> allowed)
    {
        var reverse = Edges.Where(e => e.From != e.To && allowed(e)).ToLookup(e => e.To);
        var distance = new Dictionary<int, int> { [to] = 0 };
        var pending = new Queue<int>(); pending.Enqueue(to);
        while (pending.TryDequeue(out int at))
            foreach (var edge in reverse[at])
                if (distance.TryAdd(edge.From, distance[at] + 1)) pending.Enqueue(edge.From);
        if (!distance.TryGetValue(from, out int remaining)) return Array.Empty<Edge>();
        var forward=Edges.Where(e=>e.From!=e.To && allowed(e)).ToLookup(e=>e.From);
        var fromDistance=new Dictionary<int,int> { [from]=0 };
        pending.Enqueue(from);
        while(pending.TryDequeue(out int at)) foreach(var edge in forward[at])
            if(fromDistance.TryAdd(edge.To,fromDistance[at]+1)) pending.Enqueue(edge.To);
        foreach (var candidate in distance.OrderBy(p => p.Value).ThenBy(p=>p.Key))
        {
            var zone=_data.Zones[candidate.Key];
            bool preparesTarget=candidate.Key==to && Enumerable.Range(0,zone.Height).Any(y=>Enumerable.Range(0,zone.Width).Any(x=>
                zone.GetTile(x,y,1) is var tile && tile<_data.Tiles.Count && _data.Tiles[tile].IsDraggable));
            // An authored connector may return to a different component of the
            // same exterior. Zone-only distance counts that necessary trip as
            // a detour; initialize the known scripted entrance before rejecting it.
            bool connector=Edges.Any(e=>e.From==from && e.To==candidate.Key && e.Instruction.ScriptedZoneChange) &&
                Edges.Any(e=>e.From==candidate.Key && e.To==from);
            if(!connector && (!fromDistance.TryGetValue(candidate.Key,out int approach) || approach+candidate.Value!=remaining)) continue;
            if (candidate.Value >= remaining && !connector || _state.VisitedZones.Contains(candidate.Key) ||
                !preparesTarget && !zone.Actions.Any(a => a.Conditions.Any(c => c.Opcode == ConditionOpcode.ZoneNotInitialized))) continue;
            var route = RouteThroughAreas(from, candidate.Key, allowed, null);
            if (route.Count > 0) return route;
        }
        return Array.Empty<Edge>();
    }

    public static HashSet<(int X,int Y)> WalkableArea(GameData data, Zone zone, int x, int y, GameState state,
        Func<Zone,int,int,bool>? canWalk=null)
    {
        var nav = new Pathfinder(data,state) { AllowDiagonal=true, PreferCardinal=true,AdditionalWalkability=canWalk };
        var actors = zone.Id == state.CurrentZoneId ? state.ZoneNPCs.Where(n => !n.IsHostile &&
            n.Behavior is NPCBehavior.Stationary or NPCBehavior.Animation).ToList() : null;
        var seen = new HashSet<(int X,int Y)> { (x,y) };
        var queue = new Queue<(int X,int Y)>(); queue.Enqueue((x,y));
        while(queue.TryDequeue(out var at))
            foreach(var next in nav.WalkingNeighbors(zone,at.X,at.Y,actors,zone.Id))
                if(seen.Add(next)) queue.Enqueue(next);
        return seen;
    }

    // Build the directed component graph, then search backward from components
    // that can reach the requested action. No reverse traversal invents a return door.
    private IReadOnlyList<Edge> RouteThroughAreas(int from, int to, Func<Edge,bool> allowed,
        IReadOnlyCollection<(int X,int Y)>? targets)
    {
        var areas = new Dictionary<(int Zone,int X,int Y),HashSet<(int X,int Y)>>();
        var areaKeys = new Dictionary<(int Zone,int X,int Y),(int Zone,int X,int Y)>();
        var componentZones = new Dictionary<(int Zone,int X,int Y),Zone>();
        var arrivalViews = new Dictionary<(int Zone,int X,int Y),Zone>();
        var arrivalNav=new Pathfinder(_data,_state) { AdditionalWalkability=_canWalk };
        Zone ArrivalView(int zone,int x,int y)
        {
            var original=_data.Zones[zone];
            if(zone==_state.CurrentZoneId || _world==null || !_world.RoomParents.Values.Contains(zone) ||
                !original.Objects.Any(h=>h.Enabled && h.Type==ZoneObjectType.DoorEntrance &&
                    _world.RoomParents.TryGetValue(h.Argument,out int parent) && parent==zone)) return original;
            var key=(zone,x,y);
            if(arrivalViews.TryGetValue(key,out var existing)) return existing;
            var view=new Zone { Id=original.Id,Width=original.Width,Height=original.Height,Planet=original.Planet,
                Type=original.Type,Name=original.Name,TileGrid=(ushort[,,])original.TileGrid.Clone(),
                Objects=original.Objects,Actions=original.Actions,Monsters=original.Monsters,
                RequiredItems=original.RequiredItems,GoalItems=original.GoalItems,ProvidedItems=original.ProvidedItems,Npcs=original.Npcs };
            DoorwayAccess.OpenSealedRoomEntrances(_data,_state,_world,view,new[]{(x,y)},writeLog:false);
            arrivalViews[key]=view;
            return view;
        }
        (int Zone,int X,int Y) Component(int zone,int x,int y)
        {
            if(areaKeys.TryGetValue((zone,x,y),out var known)) return known;
            if (!areas.TryGetValue((zone,x,y),out var reach))
            {
                var view=ArrivalView(zone,x,y);
                reach=WalkableArea(_data,view,x,y,_state,_canWalk);
                // An authored arrival may stand on a blocked portal and walk
                // out on either side. Those sides cannot walk back through the
                // portal, so this is a directed landing, not a merged component.
                if(!arrivalNav.IsWalkable(view,x,y))
                {
                    var landing=(zone,x,y);
                    areas[landing]=reach;
                    areaKeys[landing]=landing;
                    return landing;
                }
            }
            var first=reach.OrderBy(p=>p.Y).ThenBy(p=>p.X).First();
            var key=(zone,first.X,first.Y);
            componentZones[key]=ArrivalView(zone,x,y);
            foreach(var cell in reach)
            {
                areas[(zone,cell.X,cell.Y)]=reach;
                areaKeys[(zone,cell.X,cell.Y)]=key;
            }
            return key;
        }
        var start=Component(from,_state.PlayerX,_state.PlayerY);
        var queue=new Queue<(int Zone,int X,int Y)>(); queue.Enqueue(start);
        var seen=new HashSet<(int Zone,int X,int Y)> { start };
        var incoming=new Dictionary<(int Zone,int X,int Y),List<((int Zone,int X,int Y) From,Edge Edge)>>();
        var goals=new List<(int Zone,int X,int Y)>();
        var outgoing=Edges.ToLookup(e=>e.From);
        var blockedCrossings=new List<((int Zone,int X,int Y) Area,Edge Edge)>();
        var arrivalPreparations=new List<((int Zone,int X,int Y) Area,Edge Edge)>();
        Edge? stagedArrival=null;
        while(queue.TryDequeue(out var at))
        {
            var reach=areas[at];
            if(at.Zone==to && (targets==null || targets.Any(reach.Contains) ||
                CanMoveBlocksTo(_data.Zones[at.Zone],reach,targets)))
            {
                if(targets!=null)
                    Console.WriteLine($"[BOT] Route proof {from} -> {to}: component ({at.X},{at.Y}), reachable targets=[{string.Join(';',targets.Where(reach.Contains))}], cells={reach.Count}");
                goals.Add(at);
            }
            foreach(var edge in outgoing[at.Zone])
            {
                if(!allowed(edge)) continue;
                var linked=new HashSet<(int Zone,int X,int Y)>();
                var sourceZone=componentZones.GetValueOrDefault(at,_data.Zones[edge.From]);
                var crossings=Crossings(edge,reach,sourceZone).ToList();
                var destinationZone=_data.Zones[edge.To];
                if(crossings.Count>0 && edge.Instruction.ScriptedZoneChange &&
                    edge.Instruction.ArrivalX is int arriveX && edge.Instruction.ArrivalY is int arriveY &&
                    ArrivalOpensPath(_data.Zones[edge.To],arriveX,arriveY))
                    arrivalPreparations.Add((at,edge));
                if(crossings.Count==0 && edge.Instruction.Type==ObjectiveType.ChangeZone)
                    blockedCrossings.Add((at,edge));
                foreach(var (departure,arrival) in crossings)
                {
                    var next=Component(edge.To,arrival.X,arrival.Y);
                    if(!linked.Add(next)) continue;
                    var instruction=edge.Instruction;
                    if(instruction.Direction is { } dir)
                        instruction=new BotObjective { Type=ObjectiveType.ChangeZone,TargetZoneId=edge.To,Direction=dir,
                            CrossingX=departure.X,CrossingY=departure.Y,
                            Description=$"Walk {dir} via ({departure.X},{departure.Y}) from zone {edge.From} to {edge.To}" };
                    if(!incoming.TryGetValue(next,out var links)) incoming[next]=links=new();
                    links.Add((at,new Edge(edge.From,edge.To,instruction)));
                    if(seen.Add(next)) queue.Enqueue(next);
                }
            }
        }
        if(goals.Count==0 && targets!=null)
        {
            // A covered reward in a previously visited room can require object
            // preparation too. Prove its approach from an actual reachable entry,
            // then let the live solver uncover it after entering that component.
            foreach(var candidate in seen.Where(a=>a.Zone==to && a!=start).OrderBy(a=>a.Y).ThenBy(a=>a.X))
            {
                var zone=_data.Zones[candidate.Zone];
                var simulation=new GameState { CurrentZone=zone,CurrentZoneId=zone.Id,
                    Counters=_state.Counters,Variables=_state.Variables,ZoneRandom=_state.ZoneRandom,
                    VisitedZones=_state.VisitedZones,DisabledActions=_state.DisabledActions,
                    PlayerX=candidate.X,PlayerY=candidate.Y,SmoothMovementEnabled=_state.SmoothMovementEnabled,
                    ZoneNPCs=zone.Id==_state.CurrentZoneId ? _state.ZoneNPCs : new() };
                if(BlockPuzzlePlanner.Plan(_data,zone,simulation,targets,
                    Array.Empty<BlockPuzzlePlanner.Placement>()) is not { Count: >0 }) continue;
                goals.Add(candidate);
                break;
            }
        }
        if(goals.Count==0)
        {
            // A known route may require moving a block after a passage in a
            // different, already visited area. Prove that preparation from its
            // reachable entrance, then stage along real edges to that entrance.
            var reverse=Edges.Where(e=>e.From!=e.To && allowed(e)).ToLookup(e=>e.To);
            var distance=new Dictionary<int,int> { [to]=0 };
            var pending=new Queue<int>(); pending.Enqueue(to);
            while(pending.TryDequeue(out int id)) foreach(var edge in reverse[id])
                if(distance.TryAdd(edge.From,distance[id]+1)) pending.Enqueue(edge.From);
            foreach(var candidate in arrivalPreparations.Where(c=>distance.TryGetValue(c.Edge.To,out int next) &&
                (c.Edge.To==to || distance.TryGetValue(c.Edge.From,out int before) && next<before))
                .OrderBy(c=>distance[c.Edge.To]).ThenBy(c=>c.Edge.From).ThenBy(c=>c.Edge.To)
                .ThenBy(c=>c.Area.Y).ThenBy(c=>c.Area.X))
            {
                goals.Add(candidate.Area);
                stagedArrival=candidate.Edge;
                break;
            }
            foreach(var candidate in blockedCrossings
                .Where(c=>goals.Count==0 && distance.ContainsKey(c.Edge.From) && distance.TryGetValue(c.Edge.To,out int next) &&
                    next<distance[c.Edge.From])
                .OrderBy(c=>distance[c.Edge.From]).ThenBy(c=>c.Edge.From).ThenBy(c=>c.Edge.To)
                .ThenBy(c=>c.Area.Y).ThenBy(c=>c.Area.X))
            {
                var zone=_data.Zones[candidate.Edge.From];
                if(!Enumerable.Range(0,zone.Height).Any(y=>Enumerable.Range(0,zone.Width).Any(x=>
                    zone.GetTile(x,y,1) is var tile && tile<_data.Tiles.Count && _data.Tiles[tile].IsDraggable))) continue;
                var crossingTargets=BoundaryCrossings(candidate.Edge).Select(c=>c.From).ToArray();
                if(crossingTargets.Length==0) continue;
                var simulation=new GameState { CurrentZone=zone,CurrentZoneId=zone.Id,
                    Counters=_state.Counters,Variables=_state.Variables,ZoneRandom=_state.ZoneRandom,
                    VisitedZones=_state.VisitedZones,DisabledActions=_state.DisabledActions,
                    PlayerX=candidate.Area.X,PlayerY=candidate.Area.Y,SmoothMovementEnabled=_state.SmoothMovementEnabled,
                    ZoneNPCs=zone.Id==_state.CurrentZoneId ? _state.ZoneNPCs : new() };
                var plan=BlockPuzzlePlanner.Plan(_data,zone,simulation,crossingTargets,Array.Empty<BlockPuzzlePlanner.Placement>());
                if(plan is not { Count: >0 } || !plan[^1].Walk) continue;
                Console.WriteLine($"[BOT] Preparing object-blocked {candidate.Edge.Instruction.Direction} crossing in zone {zone.Id} on route to {to}");
                if(candidate.Area==start)
                {
                    var target=plan[^1];
                    return new[] { new Edge(candidate.Edge.From,candidate.Edge.To,new BotObjective {
                        Type=ObjectiveType.ChangeZone,TargetZoneId=candidate.Edge.To,Direction=candidate.Edge.Instruction.Direction,
                        CrossingX=target.X,CrossingY=target.Y,TargetX=target.X,TargetY=target.Y,
                        Description=$"Clear the {candidate.Edge.Instruction.Direction} crossing in zone {zone.Id} toward zone {to}" }) };
                }
                goals.Add(candidate.Area);
                break;
            }
        }

        // Reverse BFS chooses entrances which lead all the way to the action's component.
        var nextStep=new Dictionary<(int Zone,int X,int Y),((int Zone,int X,int Y) To,Edge Edge)>();
        var connected=goals.ToHashSet();
        foreach(var goal in goals) queue.Enqueue(goal);
        while(queue.TryDequeue(out var at) && !connected.Contains(start))
            if(incoming.TryGetValue(at,out var links)) foreach(var link in links)
                if(connected.Add(link.From))
                { nextStep[link.From]=(at,link.Edge); queue.Enqueue(link.From); }
        if(!connected.Contains(start)) return Array.Empty<Edge>();
        var route=new List<Edge>();
        for(var at=start;nextStep.TryGetValue(at,out var step);at=step.To) route.Add(step.Edge);
        if(stagedArrival!=null) route.Add(stagedArrival);
        return route;
    }

    internal IEnumerable<((int X,int Y) From,(int X,int Y) To)> Crossings(Edge edge,HashSet<(int X,int Y)> reach,Zone? sourceView=null)
    {
        var source=sourceView ?? _data.Zones[edge.From]; var dest=_data.Zones[edge.To];
        if(dest.Width<3 || dest.Height<3) yield break;
        var nav=new Pathfinder(_data,_state) { AllowDiagonal=true, PreferCardinal=true,AdditionalWalkability=_canWalk };
        if(edge.Instruction.Direction is { } dir)
        {
            foreach(var crossing in BoundaryCrossings(edge))
                if(reach.Contains(crossing.From)) yield return crossing;
            yield break;
        }
        var point=(edge.Instruction.TargetX,edge.Instruction.TargetY);
        if (edge.Instruction.Type == ObjectiveType.TraversePassage &&
            edge.Instruction.ArrivalX is int ax && edge.Instruction.ArrivalY is int ay)
        {
            nav.TransitionTarget = (source.Id, point.Item1, point.Item2);
            // A passage can land on the trigger for its return trip. That is
            // the intended arrival, not an unrelated transition to avoid.
            var landingNav=new Pathfinder(_data,_state) { AdditionalWalkability=_canWalk,
                TransitionTarget=(dest.Id,ax,ay) };
            bool reachableBump=edge.Instruction.PassageRequiresBump &&
                (edge.Instruction.ApproachX is int sx && edge.Instruction.ApproachY is int sy
                    ? reach.Contains((sx,sy)) : new[]{(0,1),(0,-1),(1,0),(-1,0)}
                        .Any(d=>reach.Contains((point.Item1+d.Item1,point.Item2+d.Item2))));
            if ((reachableBump || !edge.Instruction.PassageRequiresBump && nav.IsWalkable(source, point.Item1, point.Item2,
                    source.Id == _state.CurrentZoneId ? _state.ZoneNPCs.Where(n => !n.IsHostile &&
                        n.Behavior is NPCBehavior.Stationary or NPCBehavior.Animation).ToList() : null) &&
                (reach.Contains(point) || reach.Any(cell =>
                    Math.Max(Math.Abs(cell.X-point.Item1),Math.Abs(cell.Y-point.Item2)) == 1 &&
                    nav.WalkingNeighbors(source,cell.X,cell.Y).Contains(point)))) &&
                (landingNav.IsWalkable(dest, ax, ay) ||
                    ax>=0 && ay>=0 && ax<dest.Width && ay<dest.Height && nav.WalkingNeighbors(dest,ax,ay).Any()))
                yield return (point, (ax, ay));
            yield break;
        }
        // Transition tiles can be solid: reaching a cardinal neighbor is enough to bump/board.
        var adjacent=new[]{(point.Item1,point.Item2+1),(point.Item1,point.Item2-1),
            (point.Item1+1,point.Item2),(point.Item1-1,point.Item2)};
        nav.TransitionTarget=(source.Id,point.Item1,point.Item2);
        bool canStepOnDoor=edge.Instruction.Type==ObjectiveType.EnterDoor && reach.Any(cell=>
            Math.Max(Math.Abs(cell.X-point.Item1),Math.Abs(cell.Y-point.Item2))==1 &&
            nav.WalkingNeighbors(source,cell.X,cell.Y).Contains(point));
        int cover=source.GetTile(point.Item1,point.Item2,1);
        bool coveredByBlock=cover<_data.Tiles.Count && _data.Tiles[cover].IsDraggable;
        bool directDoor=source.Objects.Any(h=>h.Enabled && h.X==point.Item1 && h.Y==point.Item2 &&
            h.Type is ZoneObjectType.DoorEntrance or ZoneObjectType.DoorExit);
        if(edge.Instruction.Type==ObjectiveType.EnterDoor && !directDoor && !coveredByBlock &&
            cover<_data.Tiles.Count && _data.Tiles[cover].IsObject &&
            !source.Actions.Select((a,i)=>(a,i)).Any(p=>!_state.DisabledActions.Contains((source.Id,p.i)) &&
                p.a.Conditions.Any(c=>c.Opcode==ConditionOpcode.Bump && c.Arguments.Count>=3 &&
                    c.Arguments[0]==point.Item1 && c.Arguments[1]==point.Item2 && c.Arguments[2]==cover) &&
                p.a.Conditions.All(c=>c.Arguments.Count>0 && (c.Opcode switch {
                    ConditionOpcode.Bump=>c.Arguments.Count>=3 && c.Arguments[0]==point.Item1 &&
                        c.Arguments[1]==point.Item2 && c.Arguments[2]==cover,
                    ConditionOpcode.CounterIs=>_state.GetCounter(source.Id)==c.Arguments[0],
                    ConditionOpcode.CounterIsNot=>_state.GetCounter(source.Id)!=c.Arguments[0],
                    ConditionOpcode.RandomIs=>_state.ZoneRandom.GetValueOrDefault(source.Id)==c.Arguments[0],
                    ConditionOpcode.TileAtIs when c.Arguments.Count>=4=>
                        source.GetTile(c.Arguments[1],c.Arguments[2],c.Arguments[3])==unchecked((ushort)c.Arguments[0]),
                    _=>false })) &&
                p.a.Instructions.Any(i=>i.Opcode==InstructionOpcode.RemoveTile && i.Arguments.Count>=3 &&
                    i.Arguments[0]==point.Item1 && i.Arguments[1]==point.Item2 && i.Arguments[2]==1)))
            yield break;
        if (coveredByBlock || !reach.Contains(point) && !adjacent.Any(reach.Contains) && !canStepOnDoor)
        {
            var goals=coveredByBlock ? new[]{point} : adjacent;
            if (!CanMoveBlocksTo(source,reach,goals,point)) yield break;
        }
        // Vehicle art is not a doorway landing. The native arrival sequence
        // walks the hero off the vehicle, often several tiles from its hotspot.
        // Use that final position for the component reached by the trip.
        bool vehicle=source.Objects.Any(h=>h.Argument==dest.Id &&
            h.Type is ZoneObjectType.VehicleToPrimary or ZoneObjectType.VehicleToSecondary);
        if(vehicle)
        {
            var landing=ScriptedTransport.Arrival(dest,_state,_data.GameType,_world?.Sectors.GetValueOrDefault(dest.Id));
            if(landing is { } arrived)
            {
                bool walkable=nav.IsWalkable(dest,arrived.X,arrived.Y);
                // Authored flight scripts move the hero directly onto their
                // landing coordinate. A hostile already standing there does
                // not prevent boarding; the bot can resolve that encounter
                // after arrival.
                if(walkable) yield return(point,arrived);
                else Console.WriteLine($"[BOT] Vehicle edge {source.Id}->{dest.Id} arrival ({arrived.X},{arrived.Y}) rejected: tile={dest.GetTile(arrived.X,arrived.Y,1)}");
            }
            else Console.WriteLine($"[BOT] Vehicle edge {source.Id}->{dest.Id} has no active arrival script in zone {dest.Id}");
            yield break;
        }
        // DoorExit hotspots with no argument return to the specific doorway
        // that entered this room. The zone-only room parent is insufficient
        // when the parent has several entrances to the same room; choosing the
        // first one can land in a sealed component and make every real route
        // appear unreachable.
        if(edge.Instruction.Type==ObjectiveType.EnterDoor &&
            source.Objects.Any(h=>h.Type==ZoneObjectType.DoorExit && h.Argument==ushort.MaxValue) &&
            _doorReturnPoints.TryGetValue(source.Id,out var returnPoint) && returnPoint.Zone==dest.Id)
        {
            nav.TransitionTarget=(dest.Id,returnPoint.X,returnPoint.Y);
            int entranceTile=dest.GetTile(returnPoint.X,returnPoint.Y,1);
            if(DoorwayArrival.HasEntryTrigger(dest,returnPoint.X,returnPoint.Y,_state) &&
                (entranceTile>=_data.Tiles.Count || !_data.Tiles[entranceTile].IsObject) &&
                !ZoneArrival.HasLivingActor(dest,returnPoint.X,returnPoint.Y,_state))
                yield return(point,(returnPoint.X,returnPoint.Y));
            if(DoorwayArrival.HasMovableExitCover(_data,dest,returnPoint.X,returnPoint.Y) &&
                nav.IsWalkable(dest,returnPoint.X,returnPoint.Y) &&
                !ZoneArrival.HasLivingActor(dest,returnPoint.X,returnPoint.Y,_state))
            { yield return(point,(returnPoint.X,returnPoint.Y)); yield break; }
            bool yieldedLanding=false;
            foreach(var (dx,dy) in DoorwayArrival.ExitOffsets(dest,returnPoint.Y))
                if(nav.IsWalkable(dest,returnPoint.X+dx,returnPoint.Y+dy) &&
                    !ZoneArrival.HasLivingActor(dest,returnPoint.X+dx,returnPoint.Y+dy,_state))
                { yield return(point,(returnPoint.X+dx,returnPoint.Y+dy)); yieldedLanding=true; }
            if(!yieldedLanding && nav.IsWalkable(dest,returnPoint.X,returnPoint.Y) &&
                !ZoneArrival.HasLivingActor(dest,returnPoint.X,returnPoint.Y,_state))
                yield return(point,(returnPoint.X,returnPoint.Y));
            yield break;
        }
        var counterpart=dest.Objects.FirstOrDefault(h=>h.Enabled && h.Argument==source.Id &&
            h.Type is ZoneObjectType.DoorEntrance or ZoneObjectType.DoorExit or
                ZoneObjectType.VehicleToPrimary or ZoneObjectType.VehicleToSecondary)
            ?? dest.Objects.FirstOrDefault(h=>h.Enabled && h.Type==ZoneObjectType.DoorExit && h.Argument==ushort.MaxValue);
        if(counterpart != null)
        {
            nav.TransitionTarget=(dest.Id,counterpart.X,counterpart.Y);
            if(DoorwayArrival.HasEntryTrigger(dest,counterpart.X,counterpart.Y,_state) &&
                !ZoneArrival.HasLivingActor(dest,counterpart.X,counterpart.Y,_state))
                yield return(point,(counterpart.X,counterpart.Y));
            if(DoorwayArrival.HasMovableExitCover(_data,dest,counterpart.X,counterpart.Y) &&
                nav.IsWalkable(dest,counterpart.X,counterpart.Y) &&
                !ZoneArrival.HasLivingActor(dest,counterpart.X,counterpart.Y,_state))
            { yield return(point,(counterpart.X,counterpart.Y)); yield break; }
            bool yieldedLanding=false;
            foreach(var (dx,dy) in DoorwayArrival.ExitOffsets(dest,counterpart.Y))
                if(nav.IsWalkable(dest,counterpart.X+dx,counterpart.Y+dy) &&
                    !ZoneArrival.HasLivingActor(dest,counterpart.X+dx,counterpart.Y+dy,_state))
                { yield return(point,(counterpart.X+dx,counterpart.Y+dy)); yieldedLanding=true; }
            if(!yieldedLanding && nav.IsWalkable(dest,counterpart.X,counterpart.Y) &&
                !ZoneArrival.HasLivingActor(dest,counterpart.X,counterpart.Y,_state))
                yield return(point,(counterpart.X,counterpart.Y));
        }
        else
        {
            if(edge.Instruction.Type==ObjectiveType.EnterDoor)
            {
                // Match DoorTravel's fallback for an authored entrance whose
                // destination has no reciprocal door: it spawns at the first
                // safe cell nearest the room center. A flight arrival script
                // is unrelated to that door and can strand the route proof in
                // a disconnected component.
                var spawn=(from y in Enumerable.Range(0,dest.Height)
                    from x in Enumerable.Range(0,dest.Width)
                    where nav.IsWalkable(dest,x,y) && !ZoneArrival.HasLivingActor(dest,x,y,_state)
                    orderby Math.Abs(x-dest.Width/2)+Math.Abs(y-dest.Height/2)
                    select (X:x,Y:y)).FirstOrDefault();
                if(nav.IsWalkable(dest,spawn.X,spawn.Y) && !ZoneArrival.HasLivingActor(dest,spawn.X,spawn.Y,_state))
                    yield return(point,spawn);
            }
            else
            {
                // Flight scripts can place the hero away from their transit hotspot.
                var arrival=ScriptedTransport.Arrival(dest,_state,_data.GameType,_world?.Sectors.GetValueOrDefault(dest.Id));
                if(arrival is { } arrived && nav.IsWalkable(dest,arrived.X,arrived.Y))
                    yield return(point,arrived);
            }
        }
    }
    private IEnumerable<((int X,int Y) From,(int X,int Y) To)> BoundaryCrossings(Edge edge)
    {
        if(edge.Instruction.Direction is not { } dir) yield break;
        var source=_data.Zones[edge.From]; var dest=_data.Zones[edge.To];
        if(dest.Width<3 || dest.Height<3) yield break;
        var nav=new Pathfinder(_data,_state) { AllowDiagonal=true, PreferCardinal=true,AdditionalWalkability=_canWalk };
        bool vertical=dir is Direction.Up or Direction.Down;
        int length=vertical?source.Width:source.Height;
        for(int i=1;i<length-1;i++)
        {
            var from=dir switch { Direction.Up=>(i,0),Direction.Down=>(i,source.Height-1),
                Direction.Left=>(0,i),_=>(source.Width-1,i) };
            int along=Math.Clamp(i,1,(vertical?dest.Width:dest.Height)-2);
            var entry=dir switch { Direction.Up=>(along,dest.Height-1),Direction.Down=>(along,0),
                Direction.Left=>(dest.Width-1,along),_=>(0,along) };
            var landing=dir switch { Direction.Up=>(along,dest.Height-2),Direction.Down=>(along,1),
                Direction.Left=>(dest.Width-2,along),_=>(1,along) };
            // Match the engine's edge landing when the first interior tile is solid.
            if(!nav.IsWalkable(dest,landing.Item1,landing.Item2)) landing=entry;
            if(nav.IsWalkable(dest,entry.Item1,entry.Item2) &&
                !ZoneArrival.HasLivingActor(dest,landing.Item1,landing.Item2,_state))
                yield return(from,landing);
        }
    }

    private bool ArrivalOpensPath(Zone zone,int x,int y) => zone.Actions.Any(a=>
        a.Conditions.Any(c=>c.Opcode==ConditionOpcode.EnterByPlane) &&
        a.Conditions.Any(c=>c.Opcode==ConditionOpcode.HeroIsAt && c.Arguments.Count>=2 && c.Arguments[0]==x && c.Arguments[1]==y) &&
        a.Conditions.All(c=>c.Opcode is ConditionOpcode.EnterByPlane or ConditionOpcode.HeroIsAt) &&
        a.Instructions.Any(i=>i.Arguments.Count>=3 && i.Arguments[2]==1 &&
            zone.GetTile(i.Arguments[0],i.Arguments[1],1) is var tile && tile<_data.Tiles.Count && _data.Tiles[tile].IsObject &&
            (i.Opcode==InstructionOpcode.RemoveTile || i.Opcode==InstructionOpcode.PlaceTile && i.Arguments.Count>=4 &&
             (i.Arguments[3]==-1 || i.Arguments[3]>=0 && i.Arguments[3]<_data.Tiles.Count && !_data.Tiles[i.Arguments[3]].IsObject))));

}
