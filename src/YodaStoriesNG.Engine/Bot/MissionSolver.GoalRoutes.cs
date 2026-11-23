using YodaStoriesNG.Engine.Data;
using YodaStoriesNG.Engine.Game;

namespace YodaStoriesNG.Engine.Bot;

public partial class MissionSolver
{
    // Search a relaxed, private view to identify the next authored prerequisite.
    // This never changes the live map or executes a hypothetical opening.
    internal (Zone Zone,int X,int Y,int? Action)? GoalRouteOpening(
        IReadOnlyList<Zone> rooms,int destination,int x,int y,bool adjacent,
        Func<int,int,bool>? resolvingAction=null,bool diagonalItem=false,
        ISet<(int Zone,int X,int Y)>? blockedOpenings=null)
    {
        // A prerequisite may be on the outdoor route before the target room.
        // Limit the search to this generated world, including its nested rooms.
        var world=_worldGenerator.CurrentWorld;
        if(world!=null)
            rooms=rooms.Concat(world.Sectors.Values.Distinct().SelectMany(s=>ZoneAndRooms(s.Zone))
                .Select(id=>_gameData.Zones[id])).DistinctBy(z=>z.Id).ToList();
        var originals=rooms.ToDictionary(z=>z.Id);
        // The loaded area has already run its ZoneNotInitialized and entry
        // scripts. Route planning for the current room must see that live door,
        // object, and tile state instead of the pristine DTA template.
        if(_state.CurrentZone is { } liveZone && liveZone.Id==_state.CurrentZoneId)
            originals[liveZone.Id]=liveZone;
        var views=new Dictionary<int,Zone>();
        bool PossibleItemTarget(Zone zone,Data.Action action)
        {
            if(world!=null && !TemplateGuardsMatch(world,zone,action)) return false;
            if(!RandomGuardsPossible(zone,action)) return false;
            if(!_state.VisitedZones.Contains(zone.Id)) return true;
            foreach(var guard in action.Conditions.Where(c=>c.Opcode==ConditionOpcode.PlacedItemIs && c.Arguments.Count>=5))
            {
                var a=guard.Arguments;
                if(a[3]==-1 || zone.GetTile(a[0],a[1],a[2])==unchecked((ushort)a[3])) continue;
                // Randomized templates can offer several alternative hooks. An
                // initializer-only hook that was not selected cannot be restored
                // by ordinary play, so it must not open a hypothetical route.
                if(!zone.Actions.Where((producer,i)=>!_state.DisabledActions.Contains((zone.Id,i)) &&
                    !producer.Conditions.Any(c=>c.Opcode==ConditionOpcode.ZoneNotInitialized))
                    .SelectMany(p=>p.Instructions).Any(i=>i.Arguments.Count>=3 &&
                        (i.Opcode==InstructionOpcode.PlaceTile && i.Arguments.Count>=4 &&
                         i.Arguments[0]==a[0] && i.Arguments[1]==a[1] && i.Arguments[2]==a[2] && i.Arguments[3]==a[3] ||
                         i.Opcode==InstructionOpcode.RemoveTile && a[3]==-1 &&
                         i.Arguments[0]==a[0] && i.Arguments[1]==a[1] && i.Arguments[2]==a[2] ||
                         i.Opcode==InstructionOpcode.MoveTile && i.Arguments.Count>=5 && i.Arguments[2]==a[2] &&
                         i.Arguments[3]==a[0] && i.Arguments[4]==a[1] &&
                         zone.GetTile(i.Arguments[0],i.Arguments[1],a[2])==unchecked((ushort)a[3]))) &&
                    !(a[2]==1 && a[3]>=0 && a[3]<_gameData.Tiles.Count && _gameData.Tiles[a[3]].IsDraggable))
                    return false;
            }
            return true;
        }
        foreach(var zone in rooms)
        {
            var grid=(ushort[,,])zone.TileGrid.Clone();
            for(int row=0;row<zone.Height;row++) for(int column=0;column<zone.Width;column++)
                if(grid[row,column,1] is var tile && tile<_gameData.Tiles.Count && _gameData.Tiles[tile].IsDraggable &&
                    blockedOpenings?.Contains((zone.Id,column,row))!=true)
                    grid[row,column,1]=ushort.MaxValue;
            foreach(var action in zone.Actions.Where((a,i)=>!_state.DisabledActions.Contains((zone.Id,i)) &&
                resolvingAction?.Invoke(zone.Id,i)!=true &&
                PossibleItemTarget(zone,a) &&
                !a.Conditions.Any(c=>c.Opcode==ConditionOpcode.ZoneNotInitialized)))
            foreach(var instruction in action.Instructions)
            {
                var a=instruction.Arguments;
                if(a.Count<3 || a[0]<0 || a[1]<0 || a[0]>=zone.Width || a[1]>=zone.Height) continue;
                // Background values are also script variables. They do not
                // obstruct walking and must not become invented route unlocks.
                if(a[2]!=1) continue;
                if(instruction.Opcode==InstructionOpcode.MoveTile && a.Count>=5 &&
                    ActionWritesTile(zone,action,a[0],a[1],1,-1) ||
                    instruction.Opcode==InstructionOpcode.RemoveTile && ActionWritesTile(zone,action,a[0],a[1],1,-1) ||
                    instruction.Opcode==InstructionOpcode.PlaceTile && a.Count>=4 &&
                    ActionWritesTile(zone,action,a[0],a[1],1,a[3]) &&
                    (a[3]==-1 || a[3]>=0 && a[3]<_gameData.Tiles.Count && !_gameData.Tiles[a[3]].IsObject))
                    grid[a[1],a[0],1]=ushort.MaxValue;
            }
            views[zone.Id]=new Zone { Id=zone.Id,Width=zone.Width,Height=zone.Height,
                TileGrid=grid,Objects=zone.Objects,Actions=zone.Actions };
        }
        if(!views.ContainsKey(_state.CurrentZoneId)) return null;
        bool EnemyMarker(int id,int cx,int cy)=>originals[id].Monsters.Any(m=>m.X==cx && m.Y==cy &&
            m.CharacterId<_gameData.Characters.Count && _gameData.Characters[m.CharacterId].Type==CharacterType.Enemy);
        var nav=new Pathfinder(_gameData,_state) { AllowDiagonal=true, PreferCardinal=true,
            AvoidFloorSwitches=!adjacent,
            AdditionalWalkability=(z,cx,cy)=>z.GetTile(cx,cy,1) is var t &&
                (t>=_gameData.Tiles.Count || !_gameData.Tiles[t].IsCharacter ||
                 EnemyMarker(z.Id,cx,cy)) };
        var start=(_state.CurrentZoneId,_state.PlayerX,_state.PlayerY);
        var queue=new PriorityQueue<(int Zone,int X,int Y),int>(); queue.Enqueue(start,0);
        var previous=new Dictionary<(int Zone,int X,int Y),((int Zone,int X,int Y) From,int? Action)>();
        var costs=new Dictionary<(int Zone,int X,int Y),int> { [start]=0 };
        var simulatedDoorwayLandings=new HashSet<(int Zone,int X,int Y)>();
        var lockedDoorActions=new List<(Zone Zone,int X,int Y,int To,int Action)>();
        var travel=rooms.SelectMany(z=>ScriptedZoneTravel.ConditionalLinks(z,_state)
            .Where(link=>resolvingAction?.Invoke(z.Id,link.Action)!=true && !IsUnchangedPortalAction(z,z.Actions[link.Action]) &&
                PossibleItemTarget(z,z.Actions[link.Action]))
            .Select(link=>(Zone:z.Id,link.Action,link.Change,link.ByPlane))).ToLookup(l=>l.Zone);
        var structural=ActionGraph!.Edges.Select(e=>(e.From,e.To))
            .Concat(rooms.SelectMany(z=>z.Objects.Where(h=>h.Type==ZoneObjectType.DoorEntrance &&
                originals.ContainsKey(h.Argument)).Select(h=>(From:z.Id,To:(int)h.Argument))))
            .Where(e=>e.From!=e.To).Distinct().ToArray();
        var reverse=structural.ToLookup(e=>e.To);
        var distance=new Dictionary<int,int> { [destination]=0 };
        var pending=new Queue<int>(); pending.Enqueue(destination);
        while(pending.TryDequeue(out int next)) foreach(var edge in reverse[next])
            if(distance.TryAdd(edge.From,distance[next]+1)) pending.Enqueue(edge.From);
        var forward=structural.ToLookup(e=>e.From);
        var approach=new Dictionary<int,int> { [start.Item1]=0 };
        pending.Enqueue(start.Item1);
        while(pending.TryDequeue(out int next)) foreach(var edge in forward[next])
            if(approach.TryAdd(edge.To,approach[next]+1)) pending.Enqueue(edge.To);
        (Zone Zone,int Action)? initialize=null;
        (int Zone,int X,int Y)? goal=null;
        while(queue.TryDequeue(out var at,out int cost))
        {
            if(cost!=costs[at]) continue;
            if(at.Zone==destination && (x<0 || y<0 || (adjacent ? (diagonalItem ? Math.Max(Math.Abs(at.X-x),Math.Abs(at.Y-y))==1 : Math.Abs(at.X-x)+Math.Abs(at.Y-y)==1) : at.X==x && at.Y==y)))
            { goal=at; break; }
            // Do not require a path through untouched room templates. Enter the
            // next initializer on the destination's structural route, then plan
            // again from the doors/walls it actually selected.
            if(at.Zone!=start.Item1 && !_state.VisitedZones.Contains(at.Zone) &&
                distance.TryGetValue(at.Zone,out int remaining) &&
                distance.TryGetValue(start.Item1,out int originalDistance) && remaining<originalDistance &&
                approach.TryGetValue(at.Zone,out int steps) && steps+remaining==originalDistance)
            {
                int index=originals[at.Zone].Actions.FindIndex(a=>a.Conditions.Any(c=>c.Opcode==ConditionOpcode.ZoneNotInitialized));
                if(index>=0) { goal=at; initialize=(originals[at.Zone],index); break; }
            }
            var view=views[at.Zone];
            bool CanStepOn(int tx,int ty)
            {
                if(at.X==tx && at.Y==ty) return true;
                nav.TransitionTarget=(at.Zone,tx,ty);
                bool possible=nav.WalkingNeighbors(view,at.X,at.Y).Contains((tx,ty));
                nav.TransitionTarget=null;
                return possible;
            }
            void Add((int Zone,int X,int Y) next,int? action=null,int weight=1)
            {
                if(!views.TryGetValue(next.Zone,out var to) || next.X<0 || next.Y<0 || next.X>=to.Width || next.Y>=to.Height ||
                    costs.TryGetValue(next,out int old) && old<=cost+weight) return;
                // Entering a zone runs the same small sealed-room correction
                // as GameEngine.LoadZone. Simulate it from this exact landing
                // point so the route cannot assume an opening from another,
                // unreachable side of the map.
                if(next.Zone!=at.Zone && world?.RoomParents.Values.Contains(next.Zone)==true &&
                    simulatedDoorwayLandings.Add(next) && to.Objects.Any(h=>h.Enabled && h.Type==ZoneObjectType.DoorEntrance &&
                        world.RoomParents.TryGetValue(h.Argument,out int parent) && parent==next.Zone))
                    DoorwayAccess.OpenSealedRoomEntrances(_gameData,_state,world,to,
                        new[]{(next.X,next.Y)},writeLog:false);
                costs[next]=cost+weight; previous[next]=(at,action); queue.Enqueue(next,cost+weight);
            }
            foreach(var cell in nav.WalkingNeighbors(view,at.X,at.Y))
            {
                int tile=originals[at.Zone].GetTile(cell.X,cell.Y,1);
                Add((at.Zone,cell.X,cell.Y),weight:tile<_gameData.Tiles.Count &&
                    (_gameData.Tiles[tile].IsObject || _gameData.Tiles[tile].IsCharacter) ? 20 : 1);
            }
            foreach(var edge in ActionGraph!.Edges.Where(e=>e.From==at.Zone &&
                RouteEdgeAllowed(e) &&
                e.Instruction.Type==ObjectiveType.TraversePassage && !e.Instruction.ScriptedZoneChange))
            {
                var passage=edge.Instruction;
                if(passage.ArrivalX is not int ax || passage.ArrivalY is not int ay) continue;
                bool ready=passage.PassageRequiresBump
                    ? Math.Abs(at.X-passage.TargetX)+Math.Abs(at.Y-passage.TargetY)==1
                    : CanStepOn(passage.TargetX,passage.TargetY);
                if(!ready || passage.ApproachX is int sx && passage.ApproachY is int sy && (at.X!=sx || at.Y!=sy)) continue;
                if(!passage.PassageRequiresBump && (at.X!=passage.TargetX || at.Y!=passage.TargetY))
                {
                    // Retain the trigger cell in the route so a covering block
                    // is prepared before the passage is considered traversed.
                    Add((at.Zone,passage.TargetX,passage.TargetY));
                    continue;
                }
                // A same-room crossing is an authored interaction, not an
                // ordinary walking edge. Preserve its producer so prerequisite
                // resolution actually boards/uses the passage before walking on.
                Add((edge.To,ax,ay),edge.From==edge.To ? passage.ScriptIndex : null);
            }
            // Keep inactive local passages in the prerequisite view too. A switch
            // can replace their trigger floor; returning requires restoring it.
            foreach(var (action,index) in originals[at.Zone].Actions.Select((a,i)=>(a,i)))
            {
                if(_state.DisabledActions.Contains((at.Zone,index)) || resolvingAction?.Invoke(at.Zone,index)==true ||
                    !PossibleItemTarget(originals[at.Zone],action) ||
                    action.Conditions.Any(c=>c.Opcode==ConditionOpcode.ZoneNotInitialized) ||
                    action.Instructions.Any(i=>i.Opcode==InstructionOpcode.ChangeZone) ||
                    ScriptedPassage.IsTransportAnimation(originals[at.Zone],action,_gameData.GameType)) continue;
                var trigger=action.Conditions.FirstOrDefault(c=>c.Arguments.Count>=3 &&
                    c.Opcode is ConditionOpcode.StandingOn or ConditionOpcode.Bump or ConditionOpcode.PlacedItemIs);
                var movement=ScriptedPassage.ArrivalAction(originals[at.Zone],action,_state,_gameData.GameType,world?.Sectors.GetValueOrDefault(at.Zone));
                if(movement==null || ScriptedPassage.IsTransportAnimation(originals[at.Zone],movement,_gameData.GameType)) continue;
                var arrival=ScriptedPassage.Arrival(movement,_gameData.GameType);
                if(trigger==null || arrival==null) continue;
                int dx=Math.Abs(at.X-trigger.Arguments[0]),dy=Math.Abs(at.Y-trigger.Arguments[1]);
                bool canTrigger=trigger.Opcode switch {
                    ConditionOpcode.StandingOn=>CanStepOn(trigger.Arguments[0],trigger.Arguments[1]),
                    ConditionOpcode.PlacedItemIs=>Math.Max(dx,dy)==1,
                    _=>dx+dy==1 };
                if(!canTrigger) continue;
                if(trigger.Opcode==ConditionOpcode.StandingOn && (dx!=0 || dy!=0))
                {
                    Add((at.Zone,trigger.Arguments[0],trigger.Arguments[1]));
                    continue;
                }
                Add((at.Zone,arrival.Value.X,arrival.Value.Y),index,1000);
            }
            foreach(var edge in ActionGraph!.Edges.Where(e=>e.From==at.Zone &&
                e.Instruction.Type==ObjectiveType.ChangeZone))
            {
                if(edge.Instruction.Direction is not { } direction || !views.TryGetValue(edge.To,out var to) ||
                    to.Width<3 || to.Height<3) continue;
                bool vertical=direction is Direction.Up or Direction.Down;
                bool onEdge=direction switch { Direction.Up=>at.Y==0,Direction.Down=>at.Y==view.Height-1,
                    Direction.Left=>at.X==0,_=>at.X==view.Width-1 };
                if(!onEdge) continue;
                int along=Math.Clamp(vertical?at.X:at.Y,1,(vertical?to.Width:to.Height)-2);
                var entry=direction switch { Direction.Up=>(along,to.Height-1),Direction.Down=>(along,0),
                    Direction.Left=>(to.Width-1,along),_=>(0,along) };
                var landing=direction switch { Direction.Up=>(along,to.Height-2),Direction.Down=>(along,1),
                    Direction.Left=>(to.Width-2,along),_=>(1,along) };
                if(!nav.IsWalkable(to,landing.Item1,landing.Item2)) landing=entry;
                if(nav.IsWalkable(to,entry.Item1,entry.Item2) &&
                    !ZoneArrival.HasLivingActor(originals[to.Id],landing.Item1,landing.Item2,_state))
                    Add((to.Id,landing.Item1,landing.Item2));
            }
            foreach(var link in travel[at.Zone])
            {
                var trigger=originals[at.Zone].Actions[link.Action].Conditions.First(c=>c.Opcode is
                    ConditionOpcode.Bump or ConditionOpcode.StandingOn or ConditionOpcode.PlacedItemIs);
                var a=trigger.Arguments;
                if(a.Count<3 || (trigger.Opcode!=ConditionOpcode.StandingOn ? Math.Abs(at.X-a[0])+Math.Abs(at.Y-a[1])!=1 : at.X!=a[0] || at.Y!=a[1])) continue;
                var stance=originals[at.Zone].Actions[link.Action].Conditions.FirstOrDefault(c=>c.Opcode==ConditionOpcode.HeroIsAt && c.Arguments.Count>=2);
                if(stance!=null && (stance.Arguments[0]!=at.X || stance.Arguments[1]!=at.Y)) continue;
                // A currently unavailable scripted shortcut must not displace
                // an existing ordinary exit while fetching its own prerequisite.
                bool placed=trigger.Opcode==ConditionOpcode.PlacedItemIs && a.Count>=5;
                bool ready=originals[at.Zone].GetTile(a[0],a[1],placed ? a[2] : trigger.Opcode==ConditionOpcode.Bump ? 1 : 0)==
                    unchecked((ushort)(placed ? a[3] : a[2]));
                int target=link.Change.Arguments[0];
                if(!originals.TryGetValue(target,out var arrivalZone)) continue;
                var arrival=ZoneArrival.ResolveSpawn(_gameData,arrivalZone,link.Change.Arguments[1],link.Change.Arguments[2],link.ByPlane,_canWalk);
                Add((target,arrival.X,arrival.Y),link.Action,ready ? 1 : 1000);
            }
            // A locked destination can be on the other side of an already
            // usable transport. Preserve its real departure/arrival in the
            // prerequisite search rather than treating the island as isolated.
            foreach(var edge in ActionGraph!.Edges.Where(e=>e.From==at.Zone &&
                e.Instruction.Type==ObjectiveType.BumpTarget && originals[at.Zone].Objects.Any(h=>
                    h.Enabled && h.Argument==e.To && h.Type is
                        ZoneObjectType.VehicleToPrimary or ZoneObjectType.VehicleToSecondary)))
                foreach(var crossing in ActionGraph.Crossings(edge,new HashSet<(int X,int Y)> { (at.X,at.Y) }))
                    Add((edge.To,crossing.To.X,crossing.To.Y));
            var doors=ActionGraph!.Edges.Where(e=>e.From==at.Zone && RouteEdgeAllowed(e) && e.Instruction.Type==ObjectiveType.EnterDoor)
                .Select(e=>(To:e.To,X:e.Instruction.TargetX,Y:e.Instruction.TargetY))
                // Keep authored, enabled entrances in the relaxed search even
                // when the current overlay prevents the action graph from
                // emitting an EnterDoor edge. A script-controlled cover can
                // open the doorway after its own prerequisite is satisfied.
                .Concat(originals[at.Zone].Objects.Where(h=>h.Enabled && h.Type==ZoneObjectType.DoorEntrance &&
                    originals.ContainsKey(h.Argument)).Select(h=>(To:(int)h.Argument,X:(int)h.X,Y:(int)h.Y)))
                .Concat(originals[at.Zone].Objects.Select((h,i)=>(h,i)).Where(p=>!p.h.Enabled &&
                    p.h.Type==ZoneObjectType.DoorEntrance && originals[at.Zone].Actions.Where((a,i)=>
                        !_state.DisabledActions.Contains((at.Zone,i)) &&
                        resolvingAction?.Invoke(at.Zone,i)!=true && PossibleItemTarget(originals[at.Zone],a) &&
                        (!_state.VisitedZones.Contains(at.Zone) ||
                         !a.Conditions.Any(c=>c.Opcode==ConditionOpcode.ZoneNotInitialized))).Any(a=>a.Instructions.Any(i=>
                            i.Opcode==InstructionOpcode.EnableHotspot && i.Arguments.Count>0 && i.Arguments[0]==p.i)))
                    .Select(p=>(To:(int)p.h.Argument,X:(int)p.h.X,Y:(int)p.h.Y))).Distinct();
            foreach(var door in doors)
            {
                if(!views.TryGetValue(door.To,out var to) ||
                    blockedOpenings?.Contains((at.Zone,door.X,door.Y))==true) continue;
                int cover=originals[at.Zone].GetTile(door.X,door.Y,1);
                // A transparent roof normally does not block a route. Some native
                // doors share that tile with a Bump script, though, and only the
                // script's later state removes the cover and opens the passage.
                // Resolve that authored action chain instead of routing through a
                // door which the live engine will reject.
                if(cover<_gameData.Tiles.Count && _gameData.Tiles[cover].IsObject &&
                    originals[at.Zone].Actions.Select((action,index)=>(action,index)).FirstOrDefault(p=>
                        !_state.DisabledActions.Contains((at.Zone,p.index)) &&
                        !p.action.Conditions.Any(c=>c.Opcode==ConditionOpcode.ZoneNotInitialized) &&
                        world!=null && TemplateGuardsMatch(world,originals[at.Zone],p.action) &&
                        RandomGuardsPossible(originals[at.Zone],p.action) &&
                        !StateAllows(at.Zone,p.action) &&
                        p.action.Conditions.Any(c=>c.Opcode==ConditionOpcode.Bump && c.Arguments.Count>=3 &&
                            c.Arguments[0]==door.X && c.Arguments[1]==door.Y && c.Arguments[2]==cover) &&
                        p.action.Instructions.Any(i=>i.Opcode==InstructionOpcode.RemoveTile && i.Arguments.Count>=3 &&
                            i.Arguments[0]==door.X && i.Arguments[1]==door.Y && i.Arguments[2]==1)) is var locked &&
                    locked.action!=null)
                {
                    lockedDoorActions.Add((originals[at.Zone],door.X,door.Y,door.To,locked.index));
                    continue;
                }
                if(Math.Abs(at.X-door.X)+Math.Abs(at.Y-door.Y)!=1 && !CanStepOn(door.X,door.Y)) continue;
                bool directDoor=originals[at.Zone].Objects.Any(h=>h.Enabled && h.X==door.X && h.Y==door.Y &&
                    h.Type is ZoneObjectType.DoorEntrance or ZoneObjectType.DoorExit);
                bool hasDoorBump=originals[at.Zone].Actions.Any(a=>
                    a.Conditions.Any(c=>c.Opcode==ConditionOpcode.Bump && c.Arguments.Count>=2 &&
                        c.Arguments[0]==door.X && c.Arguments[1]==door.Y));
                if(cover<_gameData.Tiles.Count && _gameData.Tiles[cover].IsObject &&
                    !_gameData.Tiles[cover].IsDraggable &&
                    !(directDoor && !hasDoorBump) &&
                    !originals[at.Zone].Actions.Where((a,i)=>!_state.DisabledActions.Contains((at.Zone,i)) &&
                        resolvingAction?.Invoke(at.Zone,i)!=true && PossibleItemTarget(originals[at.Zone],a) &&
                        !a.Conditions.Any(c=>c.Opcode==ConditionOpcode.ZoneNotInitialized)).Any(a=>a.Instructions.Any(i=>i.Opcode==InstructionOpcode.RemoveTile &&
                        i.Arguments.Count>=3 && i.Arguments[0]==door.X && i.Arguments[1]==door.Y && i.Arguments[2]==1)))
                    continue;
                var back=to.Objects.FirstOrDefault(h=>h.Enabled && h.Argument==at.Zone &&
                    h.Type is ZoneObjectType.DoorEntrance or ZoneObjectType.DoorExit) ??
                    to.Objects.FirstOrDefault(h=>h.Enabled && h.Type==ZoneObjectType.DoorExit && h.Argument==ushort.MaxValue);
                if(back==null)
                {
                    // Match DoorTravel's center fallback when the destination
                    // is an authored room with no reciprocal doorway marker.
                    var spawn=(from sy in Enumerable.Range(0,to.Height)
                        from sx in Enumerable.Range(0,to.Width)
                        where nav.IsWalkable(to,sx,sy) && !ZoneArrival.HasLivingActor(to,sx,sy,_state)
                        orderby Math.Abs(sx-to.Width/2)+Math.Abs(sy-to.Height/2)
                        select (X:sx,Y:sy)).FirstOrDefault();
                    if(nav.IsWalkable(to,spawn.X,spawn.Y) && !ZoneArrival.HasLivingActor(to,spawn.X,spawn.Y,_state))
                        Add((to.Id,spawn.X,spawn.Y));
                    continue;
                }
                bool addedLanding=false;
                foreach(var (dx,dy) in DoorwayArrival.ExitOffsets(to,back.Y))
                    if(nav.IsWalkable(to,back.X+dx,back.Y+dy) &&
                        !ZoneArrival.HasLivingActor(to,back.X+dx,back.Y+dy,_state))
                    { Add((to.Id,back.X+dx,back.Y+dy)); addedLanding=true; }
                if(!addedLanding && nav.IsWalkable(to,back.X,back.Y) &&
                    !ZoneArrival.HasLivingActor(to,back.X,back.Y,_state))
                    Add((to.Id,back.X,back.Y));
            }
        }
        if(goal==null)
        {
            var reachableLockedDoor=lockedDoorActions
                .Where(d=>distance.TryGetValue(d.To,out int next) && distance.TryGetValue(d.Zone.Id,out int before) && next<before &&
                    costs.Keys.Any(p=>p.Zone==d.Zone.Id))
                .OrderBy(d=>distance[d.To]).ThenBy(d=>d.Zone.Id).ThenBy(d=>d.Action).FirstOrDefault();
            if(reachableLockedDoor.Zone!=null)
            {
                Console.WriteLine($"[BOT] Route needs locked doorway action {reachableLockedDoor.Action} in zone {reachableLockedDoor.Zone.Id} at ({reachableLockedDoor.X},{reachableLockedDoor.Y})");
                return (reachableLockedDoor.Zone,reachableLockedDoor.X,reachableLockedDoor.Y,reachableLockedDoor.Action);
            }
            foreach(var zone in rooms.Where(z=>distance.TryGetValue(z.Id,out int next) &&
                distance.TryGetValue(start.Item1,out int before) && next<before))
            foreach(var door in zone.Objects.Where(h=>h.Enabled && h.Type==ZoneObjectType.DoorEntrance &&
                originals.ContainsKey(h.Argument) && distance.TryGetValue(h.Argument,out int next) &&
                distance.TryGetValue(zone.Id,out int before) && next<before))
            {
                var reachable=costs.Keys.Where(p=>p.Zone==zone.Id &&
                    Math.Abs(p.X-door.X)+Math.Abs(p.Y-door.Y)<=1).Select(p=>$"({p.X},{p.Y})").ToArray();
                var reachedEdges=costs.Keys.Where(p=>p.Zone==zone.Id &&
                    (p.X==0 || p.Y==0 || p.X==zone.Width-1 || p.Y==zone.Height-1))
                    .Select(p=>$"({p.X},{p.Y})").ToArray();
                var destinationZone=originals[(int)door.Argument];
                var back=destinationZone.Objects.FirstOrDefault(h=>h.Enabled && h.Argument==zone.Id &&
                    h.Type is ZoneObjectType.DoorEntrance or ZoneObjectType.DoorExit) ??
                    destinationZone.Objects.FirstOrDefault(h=>h.Enabled && h.Type==ZoneObjectType.DoorExit && h.Argument==ushort.MaxValue);
                string Cell(int cx,int cy)
                {
                    if(cx<0 || cy<0 || cx>=zone.Width || cy>=zone.Height) return $"({cx},{cy})=edge";
                    int tile=zone.GetTile(cx,cy,1);
                    string flags=tile<_gameData.Tiles.Count
                        ? $"{(_gameData.Tiles[tile].IsObject?'O':'-')}{(_gameData.Tiles[tile].IsTransparent?'T':'-')}{(_gameData.Tiles[tile].IsDraggable?'D':'-')}"
                        : "---";
                    return $"({cx},{cy})={tile}:{flags}";
                }
                var around=new[]{(door.X,door.Y-1),(door.X+1,door.Y),(door.X,door.Y+1),(door.X-1,door.Y)};
                string Approach((int X,int Y) p)
                {
                    if(p.X<0 || p.Y<0 || p.X>=zone.Width || p.Y>=zone.Height) return Cell(p.X,p.Y);
                    var occupants=zone.Objects.Where(h=>h.Enabled && h.X==p.X && h.Y==p.Y)
                        .Select(h=>$"hotspot:{h.Type}->{h.Argument}").ToList();
                    occupants.AddRange(originals[zone.Id].Monsters.Where(m=>m.X==p.X && m.Y==p.Y)
                        .Select(m=>$"monster:{m.CharacterId}"));
                    bool walkable=nav.IsWalkable(views[zone.Id],p.X,p.Y);
                    return $"{Cell(p.X,p.Y)} walkable={walkable} [{string.Join(',',occupants)}]";
                }
                Console.WriteLine($"[BOT] Route doorway {zone.Id} ({door.X},{door.Y}) -> {door.Argument}: reachable approach=[{string.Join(',',reachable)}], reached edges=[{string.Join(',',reachedEdges)}], source {Cell(door.X,door.Y)} [{string.Join(' ',around.Select(Approach))}], return={(back==null?"none":$"{back.Type}({back.X},{back.Y})")}");
            }
            Console.WriteLine($"[BOT] No relaxed route from {_state.CurrentZoneId} to {destination} ({x},{y}); explored " +
                string.Join(',',costs.Keys.GroupBy(p=>p.Zone).Select(g=>$"{g.Key}:{g.Count()}")));
            return null;
        }
        var path=new List<((int Zone,int X,int Y) At,int? Action)>();
        for(var at=goal.Value;at!=start;)
        { var p=previous[at]; path.Add((at,p.Action)); at=p.From; }
        path.Reverse();
        foreach(var step in path)
        {
            var zone=originals[step.At.Zone];
            var from=previous[step.At].From;
            if(step.Action==null && from.Zone!=step.At.Zone)
            {
                var entrance=originals[from.Zone].Objects.FirstOrDefault(h=>h.Argument==step.At.Zone &&
                    h.Type==ZoneObjectType.DoorEntrance && Math.Max(Math.Abs(from.X-h.X),Math.Abs(from.Y-h.Y))<=1);
                if(entrance is { Enabled:false })
                {
                    int index=originals[from.Zone].Objects.IndexOf(entrance);
                    var enable=originals[from.Zone].Actions.Select((a,i)=>(a,i)).FirstOrDefault(p=>
                        !_state.DisabledActions.Contains((from.Zone,p.i)) &&
                        resolvingAction?.Invoke(from.Zone,p.i)!=true && PossibleItemTarget(originals[from.Zone],p.a) &&
                        (!_state.VisitedZones.Contains(from.Zone) ||
                         !p.a.Conditions.Any(c=>c.Opcode==ConditionOpcode.ZoneNotInitialized)) && p.a.Instructions.Any(i=>
                            i.Opcode==InstructionOpcode.EnableHotspot && i.Arguments.Count>0 && i.Arguments[0]==index));
                    if(enable.a!=null) return (originals[from.Zone],entrance.X,entrance.Y,enable.i);
                }
                if(entrance!=null && originals[from.Zone].GetTile(entrance.X,entrance.Y,1) is var tile &&
                    tile!=views[from.Zone].GetTile(entrance.X,entrance.Y,1) &&
                    tile<_gameData.Tiles.Count && _gameData.Tiles[tile].IsObject)
                    return (originals[from.Zone],entrance.X,entrance.Y,null);
            }
            if(step.Action is int action)
            {
                var source=previous[step.At].From;
                return (originals[source.Zone],source.X,source.Y,action);
            }
            int cover=zone.GetTile(step.At.X,step.At.Y,1);
            if(cover!=views[zone.Id].GetTile(step.At.X,step.At.Y,1) &&
                cover<_gameData.Tiles.Count && (_gameData.Tiles[cover].IsObject ||
                _gameData.Tiles[cover].IsCharacter && !EnemyMarker(zone.Id,step.At.X,step.At.Y)))
                return (zone,step.At.X,step.At.Y,null);
        }
        if(initialize is { } init) return (init.Zone,goal.Value.X,goal.Value.Y,init.Action);
        return null;
    }
}
