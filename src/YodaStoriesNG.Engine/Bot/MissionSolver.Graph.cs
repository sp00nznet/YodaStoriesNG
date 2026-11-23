using YodaStoriesNG.Engine.Data;
using YodaStoriesNG.Engine.Game;

namespace YodaStoriesNG.Engine.Bot;

public partial class MissionSolver
{
    public WorldActionGraph? ActionGraph { get; private set; }
    public IReadOnlyList<WorldActionGraph.Edge> PlannedRoute { get; private set; } = Array.Empty<WorldActionGraph.Edge>();
    private readonly Dictionary<int, int> _roomReturns = new();
    private readonly Dictionary<int,(int Zone,int X,int Y)> _doorReturnPoints = new();
    internal void NoteDoorEntry(int room,int parent,int x,int y)
    { _roomReturns[room]=parent; _doorReturnPoints[room]=(parent,x,y); }
    internal void SyncDoorReturns(IReadOnlyDictionary<int,(int Zone,int X,int Y)> entries)
    {
        _roomReturns.Clear(); _doorReturnPoints.Clear();
        foreach(var pair in entries) { _roomReturns[pair.Key]=pair.Value.Zone; _doorReturnPoints[pair.Key]=pair.Value; }
    }
    private int _planningDecision;
    private int _graphDecision = -1;
    private int? _pendingRewardItem;
    private (int Zone, int Item, int X, int Y)? _openedPassage;
    private readonly HashSet<(int Zone,int X,int Y)> _planningDoorOpenings = new();

    // Unlocking and entering are one planned operation. Keep its continuation
    // even if another, still-locked passage prevents a complete world route.
    public void FollowOpenedPassage(int item, int x, int y) =>
        _openedPassage = (_state.CurrentZoneId, item, x, y);

    private BotObjective? ContinueOpenedPassage()
    {
        // A bot resumed after stopping at an unlocked blockade has no in-memory
        // continuation. Recover it from the native unlock counter transition.
        if (_openedPassage == null && _state.CurrentZone is { } current &&
            _worldGenerator.CurrentWorld?.Sectors.GetValueOrDefault(current.Id) is { } gate &&
            gate.Type is ZoneType.BlockadeNorth or ZoneType.BlockadeSouth or ZoneType.BlockadeEast or ZoneType.BlockadeWest)
        {
            var unlock = current.Actions.FirstOrDefault(a =>
                a.Conditions.Any(c => c.Opcode == ConditionOpcode.CounterIs && c.Arguments.Count > 0 &&
                    c.Arguments[0] != _state.GetCounter(current.Id)) &&
                a.Instructions.Any(i => i.Opcode == InstructionOpcode.SetCounter && i.Arguments.Count > 0 &&
                    i.Arguments[0] == _state.GetCounter(current.Id)) &&
                a.Instructions.Any(i => i.Opcode == InstructionOpcode.RemoveTile) &&
                a.Conditions.Any(c => c.Opcode == ConditionOpcode.PlacedItemIs && c.Arguments.Count >= 5 &&
                    (c.Arguments[4] == gate.RequiredItem || c.Arguments[4] == -1)));
            var use = unlock?.Conditions.First(c => c.Opcode == ConditionOpcode.PlacedItemIs && c.Arguments.Count >= 5);
            if (use != null) _openedPassage = (current.Id, gate.RequiredItem, use.Arguments[0], use.Arguments[1]);
        }
        if (_openedPassage is not { } pending) return null;
        if (_state.CurrentZoneId != pending.Zone || _state.CurrentZone is not { } zone)
        { _openedPassage = null; return null; }
        var sector = _worldGenerator.CurrentWorld?.Sectors.GetValueOrDefault(zone.Id);
        foreach (var passage in ScriptedPassage.Instructions(zone, _state, sector,_gameData.GameType))
        {
            bool unlockedByUse = zone.Actions.Any(a => a.Conditions.Any(c =>
                c.Opcode == ConditionOpcode.PlacedItemIs && c.Arguments.Count >= 5 &&
                c.Arguments[0] == pending.X && c.Arguments[1] == pending.Y &&
                (c.Arguments[4] == pending.Item || c.Arguments[4] == -1 && sector?.RequiredItem == pending.Item)) &&
                a.Instructions.Any(i => i.Opcode == InstructionOpcode.RemoveTile && i.Arguments.Count >= 3 &&
                    i.Arguments[0] == passage.TargetX && i.Arguments[1] == passage.TargetY && i.Arguments[2] == 1));
            if (!unlockedByUse) continue;
            if (_state.PlayerX == passage.ArrivalX && _state.PlayerY == passage.ArrivalY)
            { _openedPassage = null; return null; }
            return passage;
        }
        _openedPassage = null;
        return null;
    }

    public void AwaitReward(int item) => _pendingRewardItem = item;


    private void RefreshGraph(WorldMap world)
    {
        // One immutable world view per decision also shares bounded object-search
        // results between alternative mission dependencies and route attempts.
        if (ActionGraph != null && _graphDecision == _planningDecision) return;
        _graphDecision = _planningDecision;
        var inventory = string.Join(",", _state.Inventory.OrderBy(i => i));
        if (inventory != _lastInventory)
        {
            _lastInventory = inventory;
        }
        // Scripted warps can connect the same room pair as an ordinary door.
        // Only actual door travel may replace that room's return destination.
        ActionGraph = WorldActionGraph.Build(_gameData, world, _state, _roomReturns,_canWalk,_doorReturnPoints);
        PlannedRoute = Array.Empty<WorldActionGraph.Edge>();
        foreach (var task in ActionGraph.Tasks)
            if (FindUseSpot(world, task.Zone, task.RequiredItem) is { } at)
            {
                task.Instruction = task.RequiredItem > 0
                    ? UseAt(task.RequiredItem, at.x, at.y, $"Solve zone {task.Zone}")
                    : new BotObjective { Type = ObjectiveType.TalkToNpc, TargetZoneId = at.zone,
                        TargetX = at.x, TargetY = at.y,
                        TargetNpc = new NPC { X = at.x, Y = at.y, Health = 1, IsEnabled = true },
                        Description = $"Talk at ({at.x},{at.y}) in zone {at.zone}" };
                task.Instruction.TargetZoneId = at.zone;
            }
            else if (world.Sectors.GetValueOrDefault(task.Zone)?.Type is ZoneType.Find or ZoneType.FindUniqueWeapon)
            {
                foreach (int id in ZoneAndRooms(task.Zone))
                {
                    var drop = _gameData.Zones[id].Objects.FirstOrDefault(h =>
                        h.Argument == task.RewardItem && h.Type is ZoneObjectType.Trigger or
                            ZoneObjectType.CrateItem or ZoneObjectType.CrateWeapon);
                    if (drop == null) continue;
                    task.Instruction = new BotObjective { Type = ObjectiveType.PickupItem,
                        TargetZoneId = id, TargetX = drop.X, TargetY = drop.Y,
                        RequiredItemId = task.RewardItem,
                        Description = $"Reveal and collect item {task.RewardItem} at ({drop.X},{drop.Y}) in zone {id}" };
                    break;
                }
            }
    }

    private BotObjective? GraphTravel(WorldMap world, int destination, IReadOnlyCollection<(int X,int Y)>? targets = null)
    {
        RefreshGraph(world);
        PlannedRoute = ActionGraph!.Route(_state.CurrentZoneId, destination, RouteEdgeAllowed, targets);
        if (PlannedRoute.Count == 0)
            PlannedRoute = ActionGraph.RouteToInitialization(_state.CurrentZoneId, destination, RouteEdgeAllowed);
        var instruction=PlannedRoute.FirstOrDefault()?.Instruction;
        if(instruction is { Type:ObjectiveType.BumpTarget,TargetZoneId:int transportDestination } &&
            _state.CurrentZone is { } departure && departure.Objects.Any(h=>h.Argument==transportDestination &&
                h.Type is ZoneObjectType.VehicleToPrimary or ZoneObjectType.VehicleToSecondary))
        {
            var key=(departure.Id,instruction.TargetX,instruction.TargetY);
            if(_planningDoorOpenings.Add(key))
            {
                try
                {
                    var sector=world.Sectors.GetValueOrDefault(departure.Id);
                    if(sector!=null)
                    foreach(var (action,index) in departure.Actions.Select((a,i)=>(a,i)).Where(p=>
                        p.a.Conditions.Any(c=>c.Opcode==ConditionOpcode.Bump && c.Arguments.Count>=3 &&
                            c.Arguments[0]==key.Item2 && c.Arguments[1]==key.Item3) &&
                        ScriptedTransport.StartsFlight(departure,p.a,_state)))
                        if(NativeGoalPrerequisite(world,sector,authoredAction:(departure.Id,index)) is { } prepare &&
                            prepare.Type is not (ObjectiveType.Explore or ObjectiveType.PlanningBlocked or ObjectiveType.UnresolvedReward))
                            return prepare;
                }
                finally { _planningDoorOpenings.Remove(key); }
            }
        }
        if(instruction is { Type:ObjectiveType.EnterDoor } && _state.CurrentZone is { } zone)
        {
            var key=(zone.Id,instruction.TargetX,instruction.TargetY);
            int cover=zone.GetTile(key.Item2,key.Item3,1);
            if(cover<_gameData.Tiles.Count && _gameData.Tiles[cover].IsObject &&
                zone.Actions.Any(a=>a.Instructions.Any(i=>i.Opcode==InstructionOpcode.RemoveTile &&
                    i.Arguments.Count>=3 && i.Arguments[0]==key.Item2 && i.Arguments[1]==key.Item3 && i.Arguments[2]==1)) &&
                _planningDoorOpenings.Add(key))
            {
                try
                {
                    var sector=world.Sectors.GetValueOrDefault(zone.Id) ??
                        world.Sectors.Values.FirstOrDefault(s=>s.Type==ZoneType.Goal);
                    if(sector!=null && NativeGoalPrerequisite(world,sector,key) is { } prepare)
                        return prepare;
                }
                finally { _planningDoorOpenings.Remove(key); }
            }
        }
        if(instruction is { Type:ObjectiveType.ChangeZone,CrossingX:int x,CrossingY:int y } &&
            !StaticWalkingArea().Contains((x,y)))
        {
            instruction.TargetX=x; instruction.TargetY=y;
            _unreachablePositions.Add((_state.CurrentZoneId,x,y));
        }
        return instruction;
    }

    private BotObjective? GetGraphObjective(WorldMap world)
    {
        RefreshGraph(world);
        // A requested dependency which is physically reachable here is immediate
        // mission progress. Collect it before choosing another multi-zone route.
        // This does not opportunistically gather unrelated items or supplies.
        _neededNativeItems.RemoveWhere(_state.HasItem);
        if(_nativeOpeningFocus is { } opening)
        {
            if(_gameData.Zones[opening.Zone].GetTile(opening.X,opening.Y,1)==opening.Tile &&
                world.Sectors.GetValueOrDefault(opening.Zone) is { } openingSector &&
                NativeGoalPrerequisite(world,openingSector,(opening.Zone,opening.X,opening.Y)) is { } prepare &&
                prepare.Type is not (ObjectiveType.Explore or ObjectiveType.PlanningBlocked or ObjectiveType.UnresolvedReward))
            {
                // Hold the prerequisite across travel. Once its room is
                // reached, the ordinary object executor owns the attempt and
                // may reject it or replace it after a scripted map change.
                if(_state.CurrentZoneId==opening.Zone) _nativeOpeningFocus=null;
                return prepare;
            }
            _nativeOpeningFocus=null;
        }
        if(_state.CurrentZone is { } here && _neededNativeItems.Count>0)
        {
            var nav=new Pathfinder(_gameData,_state) { AllowDiagonal=true, PreferCardinal=true,AdditionalWalkability=_canWalk };
            var fixedActors=_state.ZoneNPCs.Where(n=>!n.IsHostile &&
                n.Behavior is NPCBehavior.Stationary or NPCBehavior.Animation).ToList();
            for(int y=0;y<here.Height;y++) for(int x=0;x<here.Width;x++)
                if(_neededNativeItems.Contains(here.GetTile(x,y,1)) &&
                    nav.FindPath(here,_state.PlayerX,_state.PlayerY,x,y,fixedActors)!=null)
                    return new BotObjective { Type=ObjectiveType.PickupItem,TargetX=x,TargetY=y,
                        Description=$"Collect requested mission dependency {here.GetTile(x,y,1)}" };
        }
        // Keep multi-zone arrival prerequisites while temporarily leaving the
        // area. A consumed toll must not send us back to its reward provider.
        if((_pendingRewardItem is not int awaited || _state.HasItem(awaited)) &&
            (_itemRouteFocus is not int fetching || _state.HasItem(fetching)) &&
            (_nativeGoalFocus ?? _nativeGoalContinuation) is { } focus &&
            (world.Sectors.GetValueOrDefault(focus.Zone) ??
             world.Sectors.Values.FirstOrDefault(s=>s.Type==ZoneType.Goal)) is { } focusedSector &&
            NativeGoalPrerequisite(world,focusedSector) is { } continuation)
            return continuation;
        if(_itemRouteFocus is int focusedItem)
        {
            if(_state.HasItem(focusedItem)) _itemRouteFocus=null;
            else if(FetchItem(world,focusedItem) is { } fetch && fetch.Type is not
                (ObjectiveType.Explore or ObjectiveType.PlanningBlocked or ObjectiveType.UnresolvedReward)) return fetch;
            else _itemRouteFocus=null;
        }

        if (_pendingRewardItem is int reward)
        {
            if (_state.HasItem(reward)) _pendingRewardItem = null;
            else if(world.Sectors.Values.FirstOrDefault(s=>s.Type==ZoneType.Goal &&
                (s.FindItem==reward || s.AdditionalGainItem==reward) &&
                new[]{s.RequiredItem,s.AdditionalRequiredItem}.Any(item=>item>0 &&
                    (_state.HasItem(item) || _consumedInteractions.Contains((s.Zone,item))))) is { } unfinishedFinale &&
                NativeGoalPrerequisite(world,unfinishedFinale) is { } nextRequirement)
                return nextRequirement;
            else if (FetchItem(world, reward) is { } pickup)
            {
                if(pickup.Type is ObjectiveType.Explore or ObjectiveType.PlanningBlocked or ObjectiveType.UnresolvedReward &&
                    world.Sectors.Values.FirstOrDefault(s=>s.Type==ZoneType.Goal &&
                        (s.FindItem==reward || s.AdditionalGainItem==reward)) is { } finale &&
                    NativeGoalPrerequisite(world,finale) is { } prerequisite)
                    return prerequisite;
                return pickup;
            }
        }

        var active = new HashSet<int>();
        BotObjective? Solve(Sector sector)
        {
            if (!active.Add(sector.Zone)) return null; // Cyclic dependencies cannot make a plan.
            try
            {
                // A finale can consume its quest item before the actual win
                // condition. Continue its remaining native prerequisites instead
                // of searching for a second copy of that unique item.
                if(sector.Type==ZoneType.Goal && new[] { sector.RequiredItem,sector.AdditionalRequiredItem }.Any(item=>
                    item>0 && !_state.HasItem(item) &&
                    (_consumedInteractions.Contains((sector.Zone,item)) || ZoneAndRooms(sector.Zone).Any(id=>
                        _gameData.Zones[id].Actions.Select((a,i)=>(a,i)).Any(p=>
                            _state.DisabledActions.Contains((id,p.i)) && p.a.Instructions.Any(n=>
                                n.Opcode==InstructionOpcode.RemoveItem && n.Arguments.Count>0 &&
                                (n.Arguments[0]==item || n.Arguments[0]==-1 && item==sector.RequiredItem)))))))
                    return NativeGoalPrerequisite(world,sector);
                if(sector.RequiredItem>0 && !_state.HasItem(sector.RequiredItem) &&
                    _consumedInteractions.Contains((sector.Zone,sector.RequiredItem)))
                    return NativeGoalPrerequisite(world,sector,
                        rewardItem:sector.Type!=ZoneType.Goal && sector.FindItem>0 ? sector.FindItem : null);
                bool missingRequirement = false;
                BotObjective? blockedRequirement = null;
                foreach (int item in new[] { sector.RequiredItem, sector.AdditionalRequiredItem })
                {
                    if (item < 1 || _state.HasItem(item)) continue;
                    missingRequirement = true;
                    foreach (var source in world.Sectors.Values.Distinct().Where(s =>
                                 s.Zone != sector.Zone && (s.FindItem == item || s.AdditionalGainItem == item)))
                    {
                        bool solved = source.Solved || _state.IsZoneSolved(source.Zone);
                        // Trades/use puzzles must be solved before their reward can be collected.
                        var candidate = !solved && source.Type is ZoneType.Trade or ZoneType.Use or ZoneType.Goal
                            ? Solve(source) : FetchItem(world, item);
                        if (candidate == null) continue;
                        if (candidate.Type is ObjectiveType.Explore or ObjectiveType.PlanningBlocked or ObjectiveType.UnresolvedReward)
                        { blockedRequirement ??= candidate; continue; }
                        return candidate;
                    }
                }
                // Yoda's two dependency strands are independent. A blocked first
                // requirement must not suppress an executable step in the other.
                if (missingRequirement)
                {
                    // Generated item-flow requirements can describe an earlier
                    // stage or an optional route. A proved native ending branch
                    // remains authoritative when those abstract providers block.
                    if(sector.Type==ZoneType.Goal && NativeGoalPrerequisite(world,sector) is { } endingStep &&
                        endingStep.Type is not (ObjectiveType.Explore or ObjectiveType.PlanningBlocked or ObjectiveType.UnresolvedReward))
                        return endingStep;
                    return blockedRequirement;
                }
                if (sector.Type is ZoneType.Find or ZoneType.FindUniqueWeapon)
                    return sector.FindItem > 0 ? FetchItem(world, sector.FindItem) : null;
                if (FindUseSpot(world, sector.Zone, sector.RequiredItem) is not { } at)
                {
                    // A trading NPC can be hidden until its encounter is won.
                    // Prepare the authored activation instead of targeting an
                    // inactive marker or abandoning the trade dependency.
                    foreach(int room in ZoneAndRooms(sector.Zone))
                    {
                        var zone=_gameData.Zones[room];
                        foreach(var (marker,index) in zone.Objects.Select((h,i)=>(h,i)).Where(p=>
                            !p.h.Enabled && p.h.Type==SpawnLocationHotspot && p.h.Argument==sector.Npc && sector.Npc>=0))
                        foreach(var (action,actionIndex) in zone.Actions.Select((a,i)=>(a,i)).Where(p=>
                            p.a.Instructions.Any(i=>i.Opcode==InstructionOpcode.EnableHotspot &&
                                i.Arguments.Count>0 && i.Arguments[0]==index)))
                            if(NativeGoalPrerequisite(world,sector,authoredAction:(room,actionIndex)) is { } activate)
                                return activate;
                    }
                    return NativeGoalPrerequisite(world,sector,
                        rewardItem:sector.Type!=ZoneType.Goal && sector.FindItem>0 ? sector.FindItem : null);
                }
                if(sector.RequiredItem>0)
                {
                    var use=UseAt(sector.RequiredItem,at.x,at.y,$"Advance puzzle in zone {sector.Zone}");
                    use.TargetZoneId=at.zone;
                    var exchange=NativeGoalPrerequisite(world,sector,interaction:use);
                    if(sector.Type==ZoneType.Goal && (exchange==null || exchange.Type is
                        ObjectiveType.Explore or ObjectiveType.PlanningBlocked or ObjectiveType.UnresolvedReward) &&
                        NativeGoalPrerequisite(world,sector) is { } preparation && preparation.Type is not
                            (ObjectiveType.Explore or ObjectiveType.PlanningBlocked or ObjectiveType.UnresolvedReward)) return preparation;
                    return exchange;
                }
                var adjacent = new[] { (at.x,at.y-1), (at.x,at.y+1), (at.x-1,at.y), (at.x+1,at.y) };
                if (_state.CurrentZoneId != at.zone)
                    return GraphTravel(world, at.zone, adjacent) ??
                        (sector.Type==ZoneType.Goal ? NativeGoalPrerequisite(world,sector) : null) ??
                        OpenTheWay(world, sector.Zone);
                var nav = new Pathfinder(_gameData,_state) { AllowDiagonal=true, PreferCardinal=true,AdditionalWalkability=_canWalk };
                if (nav.FindPathToAdjacent(_state.CurrentZone!, _state.PlayerX, _state.PlayerY,
                    at.x, at.y, _state.ZoneNPCs) == null && GraphTravel(world, at.zone, adjacent) is { } approach)
                    return approach;
                return sector.RequiredItem > 0
                    ? UseAt(sector.RequiredItem, at.x, at.y, $"Advance puzzle in zone {sector.Zone}")
                    : new BotObjective { Type = ObjectiveType.TalkToNpc, TargetX = at.x, TargetY = at.y,
                        TargetNpc = new NPC { X = at.x, Y = at.y, Health = 1, IsEnabled = true },
                        Description = $"Talk to the mission giver in zone {at.zone}" };
            }
            finally { active.Remove(sector.Zone); }
        }

        var goal = world.Sectors.Values.FirstOrDefault(s => s.Type == ZoneType.Goal);
        if (goal != null && !_state.IsGameWon)
        {
            if(!goal.Solved && !_state.IsZoneSolved(goal.Zone)) return Solve(goal);
            // Acquiring the artifact can solve its sector before the authored
            // ending: some quests still require delivering it to the quest giver.
            if(NativeGoalPrerequisite(world,goal) is { } ending) return ending;
        }
        if (_gameData.GameType == GameType.YodaStories && world.Mission?.IsCompleted == true)
            return new BotObjective { Type = ObjectiveType.UseXWing, Description = "Return to Dagobah with the mission reward" };
        return null;
    }
}
