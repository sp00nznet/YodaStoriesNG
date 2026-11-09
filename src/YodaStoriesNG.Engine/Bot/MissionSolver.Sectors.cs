using YodaStoriesNG.Engine.Data;
using YodaStoriesNG.Engine.Game;

namespace YodaStoriesNG.Engine.Bot;

/// <summary>
/// The bot for worlds from <see cref="OriginalWorldGenerator"/> (Indiana Jones). Those
/// worlds know their whole chain, so instead of exploring and trying things the bot reads
/// the mission's current step, works out where in which zone that step's item is used, and
/// goes there: across the grid, in or out through doors, then uses the item on the spot.
/// Enemies and items lying around come first, as for Yoda.
/// </summary>
public partial class MissionSolver
{
    private const ZoneObjectType SpawnLocationHotspot = (ZoneObjectType)1;
    private const ZoneObjectType LockHotspot = (ZoneObjectType)12;

    private string _lastInventory = "";
    private string _lastObjective = "";
    private int _sameObjectiveCount;
    private readonly HashSet<(int Zone,int Item)> _consumedInteractions = new();

    public void NoteConsumedItem(int item)
    {
        int zone=_worldGenerator.CurrentWorld?.Sectors.GetValueOrDefault(_state.CurrentZoneId)?.Zone ?? _state.CurrentZoneId;
        _consumedInteractions.Add((zone,item));
        if(_itemRouteFocus==item) _itemRouteFocus=null;
    }

    /// <summary>
    /// True once the bot has been handed the same objective from the same spot several times
    /// running - its path is blocked, and on these worlds that is usually a monster sitting in
    /// a corridor. The bot then fights at any range until it moves again.
    /// </summary>
    public bool HuntEnemies => _sameObjectiveCount > 5;

    private BotObjective WithStuckCheck(BotObjective objective)
    {
        if (objective.Type is ObjectiveType.UnresolvedReward or ObjectiveType.PlanningBlocked) return objective;
        var key = $"{objective.Type}|{objective.Description}|{_state.CurrentZoneId}|{_state.PlayerX},{_state.PlayerY}";
        _sameObjectiveCount = key == _lastObjective ? _sameObjectiveCount + 1 : 0;
        _lastObjective = key;

        // Only execute a searched object solution tied to this objective.
        if (PlannedBlockMove(objective) is { } planned) return planned;
        if (objective.Type == ObjectiveType.ClearObject)
            return new BotObjective { Type=ObjectiveType.PlanningBlocked,
                TargetX=objective.TargetX,TargetY=objective.TargetY,RequiredItemId=objective.RequiredItemId,
                Description=$"No bounded object solution to uncover ({objective.TargetX},{objective.TargetY}) for item {objective.RequiredItemId}" };

        if (objective.Type is ObjectiveType.UseItemOnNpc or ObjectiveType.BumpTarget &&
            _unreachablePositions.Contains((_state.CurrentZoneId, objective.TargetX, objective.TargetY)))
        {
            if (objective.Type == ObjectiveType.UseItemOnNpc && MoveDialogueBlockerOutOfMissionRoute(objective))
                return objective;
            if (objective.Type == ObjectiveType.UseItemOnNpc && BlockingEnemyOnApproach(objective) is { } enemy)
            {
                _unreachablePositions.Remove((_state.CurrentZoneId,objective.TargetX,objective.TargetY));
                return new BotObjective { Type=ObjectiveType.KillEnemy,TargetNpc=enemy,
                    TargetX=enemy.X,TargetY=enemy.Y,
                    Description=$"Defeat {enemy.CharacterId} blocking the item target" };
            }
            // An inaccessible item target can be behind authored weapon gates
            // or switches. Resolve that approach before abandoning its room.
            if(objective.Type==ObjectiveType.UseItemOnNpc && _worldGenerator.CurrentWorld is { } world &&
                world.Sectors.GetValueOrDefault(_state.CurrentZoneId) is { } sector &&
                NativeGoalPrerequisite(world,sector,interaction:objective) is { } preparation &&
                !(preparation.Type==objective.Type && preparation.TargetX==objective.TargetX &&
                  preparation.TargetY==objective.TargetY && preparation.RequiredItemId==objective.RequiredItemId))
                return preparation;
            return new BotObjective { Type = ObjectiveType.Explore,
                Description = $"Item target ({objective.TargetX},{objective.TargetY}) is inaccessible; looking for another approach" };
        }
        return objective;
    }

    /// <summary>
    /// Some authored puzzle rooms put a required trade actor behind another
    /// dialogue actor with no route around it. If a route exists when dialogue
    /// actor tiles are treated as open, move the blocking actor and its scripts
    /// to nearby reachable floor. This keeps the required mission actor in its
    /// authored location and preserves the moved character's dialogue.
    /// </summary>
    private bool MoveDialogueBlockerOutOfMissionRoute(BotObjective objective)
    {
        if (_state.CurrentZone is not { } zone || objective.TargetNpc is null) return false;
        var strict=new Pathfinder(_gameData,_state) { AllowDiagonal=true, PreferCardinal=true,
            AdditionalWalkability=_canWalk,TransitionTarget=(zone.Id,objective.TargetX,objective.TargetY) };
        var relaxed=new Pathfinder(_gameData,_state) { AllowDiagonal=true, PreferCardinal=true,
            TransitionTarget=(zone.Id,objective.TargetX,objective.TargetY) };
        var live=_state.ZoneNPCs.Where(n=>n.IsEnabled && n.IsAlive).ToList();
        var fixedActors=live.Where(n=>!n.IsHostile).ToList();
        var relaxedPath=relaxed.FindPathToAdjacent(zone,_state.PlayerX,_state.PlayerY,
            objective.TargetX,objective.TargetY,fixedActors,allowDiagonalInteraction:true,
            extendedCardinalInteraction:ItemUseReach.Extended(_gameData,objective.RequiredItemId ?? -1))?.Path;
        if(relaxedPath==null) return false;

        foreach(var point in relaxedPath)
        {
            int x=point.X,y=point.Y;
            ushort tile=zone.GetTile(x,y,1);
            if(tile==ushort.MaxValue || tile>=_gameData.Tiles.Count ||
                !_gameData.Tiles[tile].IsCharacter || strict.IsWalkable(zone,x,y,fixedActors)) continue;
            var bumpActions=zone.Actions.Where(a=>a.Conditions.Any(c=>c.Opcode==ConditionOpcode.Bump &&
                c.Arguments.Count>=3 && c.Arguments[0]==x && c.Arguments[1]==y && c.Arguments[2]==tile)).ToList();
            if(bumpActions.Count==0 || bumpActions.Any(a=>a.Instructions.Any(i=>i.Opcode is
                InstructionOpcode.ChangeZone or InstructionOpcode.HideHero or InstructionOpcode.DropItem or
                InstructionOpcode.AddItem or InstructionOpcode.RemoveItem or InstructionOpcode.MoveTile or
                InstructionOpcode.PlaceTile or InstructionOpcode.RemoveTile))) continue;

            var candidates=new List<(int X,int Y,int Distance)>();
            for(int cy=0;cy<zone.Height;cy++) for(int cx=0;cx<zone.Width;cx++)
            {
                if((cx,cy)==(x,y) || zone.GetTile(cx,cy,1)!=ushort.MaxValue ||
                    live.Any(n=>n.X==cx && n.Y==cy) || strict.IsUnrelatedTransition(zone,cx,cy) ||
                    !strict.IsWalkable(zone,cx,cy,fixedActors) || zone.Actions.Any(a=>a.Conditions.Any(c=>
                        c.Arguments.Count>=2 && c.Arguments[0]==cx && c.Arguments[1]==cy &&
                        c.Opcode is ConditionOpcode.Bump or ConditionOpcode.PlacedItemIs))) continue;
                if(strict.FindPathToAdjacent(zone,_state.PlayerX,_state.PlayerY,cx,cy,fixedActors,
                    allowDiagonalInteraction:true)!=null)
                    candidates.Add((cx,cy,Math.Abs(cx-x)+Math.Abs(cy-y)));
            }
            if(candidates.Count==0) continue;
            var destination=candidates.OrderBy(c=>c.Distance)
                .ThenBy(c=>Math.Abs(c.X-_state.PlayerX)+Math.Abs(c.Y-_state.PlayerY))
                .ThenBy(c=>c.Y).ThenBy(c=>c.X).First();
            foreach(var current in new[]{zone,_gameData.Zones[zone.Id]}.Distinct())
            {
                current.SetTile(x,y,1,ushort.MaxValue);
                current.SetTile(destination.X,destination.Y,1,tile);
                foreach(var action in current.Actions)
                {
                    foreach(var condition in action.Conditions)
                        if(condition.Opcode==ConditionOpcode.Bump && condition.Arguments.Count>=3 &&
                            condition.Arguments[0]==x && condition.Arguments[1]==y && condition.Arguments[2]==tile)
                        { condition.Arguments[0]=(short)destination.X; condition.Arguments[1]=(short)destination.Y; }
                    foreach(var instruction in action.Instructions)
                        if(instruction.Opcode==InstructionOpcode.SpeakNpc && instruction.Arguments.Count>=2 &&
                            instruction.Arguments[0]==x && instruction.Arguments[1]==y)
                        { instruction.Arguments[0]=(short)destination.X; instruction.Arguments[1]=(short)destination.Y; }
                }
            }
            _unreachablePositions.Remove((_state.CurrentZoneId,objective.TargetX,objective.TargetY));
            return true;
        }
        return false;
    }

    private NPC? BlockingEnemyOnApproach(BotObjective objective)
    {
        if (_state.CurrentZone is not { } zone) return null;
        var nav=new Pathfinder(_gameData,_state) { AllowDiagonal=true, PreferCardinal=true,
            // Native enemies are represented twice: as a live NPC and as a
            // character tile. The engine's scripted-actor filter can reject
            // the latter even though this route check deliberately ignores
            // hostile occupancy so it can identify enemies to fight.
            AdditionalWalkability=(z,x,y) =>
                _state.ZoneNPCs.Any(n=>n.IsEnabled && n.IsAlive && n.IsHostile && n.X==x && n.Y==y) ||
                _canWalk?.Invoke(z,x,y) != false,
            TransitionTarget=(zone.Id,objective.TargetX,objective.TargetY) };
        var stationary=_state.ZoneNPCs.Where(n=>!n.IsHostile &&
            n.Behavior is NPCBehavior.Stationary or NPCBehavior.Animation).ToList();
        var path=nav.FindPathToAdjacent(zone,_state.PlayerX,_state.PlayerY,objective.TargetX,objective.TargetY,
            stationary,allowDiagonalInteraction:true,
            extendedCardinalInteraction:ItemUseReach.Extended(_gameData,objective.RequiredItemId ?? -1))?.Path;
        if(path==null) return null;
        var route=path.Select(p=>(p.X,p.Y)).ToHashSet();
        return _state.ZoneNPCs.Where(n=>n.IsEnabled && n.IsAlive && n.IsHostile && route.Contains((n.X,n.Y)))
            .OrderBy(n=>n.DistanceTo(_state.PlayerX,_state.PlayerY)).FirstOrDefault();
    }

    private BotObjective? GetSectorObjective(WorldMap world)
    {
        // Using or gaining an item is what opens blockades and gates; exits found shut before
        // may be open now.
        var inventory = string.Join(",", _state.Inventory.OrderBy(i => i));
        if (inventory != _lastInventory)
        {
            _lastInventory = inventory;
        }

        // A script can mark a sector solved as soon as it consumes the input,
        // while the reward is still lying on the floor or in a child room.
        // Recover that promised reward before advancing to the next chain step.
        if (_pendingRewardItem is int pendingReward)
        {
            if (_state.HasItem(pendingReward)) _pendingRewardItem = null;
            else if (FetchItem(world,pendingReward) is { } pendingPickup) return pendingPickup;
        }

        if (world.Mission?.CurrentPuzzleStep is not { ZoneId: { } stepZone } step)
            return null;

        int item = step.RequiredItemId;
        // Not in hand yet: the last puzzle put it somewhere (an Offer's drop spot, maybe in
        // another room) - go and get it.
        if (item > 0 && !_state.HasItem(item))
        {
            // Using an authored mission item can consume it while its reward remains in
            // another room (often behind the passage that use just opened). The sector is
            // not marked solved until that reward is collected, so asking for the input
            // again sends the bot back to its old source and can create a zone-to-zone loop.
            if (_consumedInteractions.Contains((stepZone, item)) && step.RewardItemId > 0 &&
                !_state.HasItem(step.RewardItemId))
                return FetchItem(world, step.RewardItemId) ?? NativeGoalPrerequisite(world,
                    world.Sectors.GetValueOrDefault(stepZone) ?? new Sector { Zone = stepZone },
                    rewardItem: step.RewardItemId);
            return FetchItem(world, item);
        }

        if (FindUseSpot(world, stepZone, item) is not { } spot)
            return null;

        if (_state.CurrentZoneId != spot.zone)
        {
            // Resolve an unpaid blockade on the route even when the abstract
            // zone grid is connected through it. CanReach only describes the
            // grid component; a generated blockade remains a real traversal
            // prerequisite inside that component.
            if (!ZoneAndRooms(stepZone).Contains(_state.CurrentZoneId))
            {
                if (OpenTheWay(world, stepZone) is { } prerequisite)
                    return prerequisite;
            }
            return NavigateToward(world, spot.zone, stepZone, spot.x, spot.y, adjacent: true);
        }

        var target = new NPC { X = spot.x, Y = spot.y, IsEnabled = true, Health = 1 };
        if(item>0 && _consumedInteractions.Contains((stepZone,item)) &&
            world.Sectors.GetValueOrDefault(stepZone) is { } interactionSector)
        {
            // A retained item can hit an early flavor branch before the real
            // mission action is available (often until enemies are cleared).
            // Resolve the remaining authored prerequisites instead of repeating
            // the same no-op use on every planning pass.
            var attempted=new BotObjective { Type=ObjectiveType.UseItemOnNpc,
                TargetNpc=target,TargetX=spot.x,TargetY=spot.y,TargetZoneId=spot.zone,
                RequiredItemId=item,ExpectedRewardItemId=step.RewardItemId>0 ? step.RewardItemId : null,
                Description=$"Use item {item} ({step.Hint})" };
            if(NativeGoalPrerequisite(world,interactionSector,interaction:attempted) is { } followup &&
                !(followup.Type==ObjectiveType.UseItemOnNpc && followup.TargetZoneId==spot.zone &&
                  followup.TargetX==spot.x && followup.TargetY==spot.y && followup.RequiredItemId==item))
                return followup;
            return null;
        }
        return item > 0
            ? new BotObjective
            {
                Type = ObjectiveType.UseItemOnNpc, Description = $"Use item {item} ({step.Hint})",
                TargetNpc = target, TargetX = spot.x, TargetY = spot.y, RequiredItemId = item,
                ExpectedRewardItemId = step.RewardItemId > 0 && !_state.HasItem(step.RewardItemId)
                    ? step.RewardItemId : null,
            }
            : new BotObjective
            {
                Type = ObjectiveType.TalkToNpc, Description = step.Hint,
                TargetNpc = target, TargetX = spot.x, TargetY = spot.y,
            };
    }

    /// <summary>
    /// Where in <paramref name="zoneId"/> or the rooms behind its doors <paramref name="item"/>
    /// is used: the trader standing on the sector's spawn hotspot, a Lock holding the item,
    /// a script that waits for it to be placed - or, for item 0, Marcus's desk.
    /// </summary>
    private (int zone, int x, int y)? FindUseSpot(WorldMap world, int zoneId, int item)
    {
        world.Sectors.TryGetValue(zoneId, out var sector);
        var rooms = ZoneAndRooms(zoneId).ToList();
        Zone CurrentOrTemplate(int id) => id == _state.CurrentZoneId
            ? _state.CurrentZone ?? _gameData.Zones[id]
            : _gameData.Zones[id];

        // A script that takes the item here and now comes first. Puzzle rooms roll a layout
        // on entry (which ledge the ladder goes on); only the matching script opens the way,
        // while the generator's Lock hotspot would accept the item anywhere and waste it.
        if (item > 0)
            foreach (var id in rooms)
            {
                var zone = CurrentOrTemplate(id);
                var locks=zone.Objects.Where(h=>h.Enabled && h.Type==LockHotspot && h.Argument==item).ToList();
                bool authoredLock=locks.Any(h=>zone.Actions.Any(a=>a.Conditions.Any(c=>
                    c.Opcode==ConditionOpcode.PlacedItemIs && c.Arguments.Count>=5 &&
                    c.Arguments[0]==h.X && c.Arguments[1]==h.Y &&
                    (c.Arguments[4]==item || c.Arguments[4]==-1)) &&
                    a.Instructions.Any(i=>i.Opcode is InstructionOpcode.PlaceTile or InstructionOpcode.RemoveTile &&
                        i.Arguments.Count>=3 && i.Arguments[0]==h.X && i.Arguments[1]==h.Y)));
                foreach (var action in zone.Actions)
                {
                    var placed = action.Conditions.FirstOrDefault(c => c.Opcode == ConditionOpcode.PlacedItemIs && c.Arguments.Count >= 5);
                    if (placed == null || _state.DisabledActions.Contains((id, zone.Actions.IndexOf(action))) ||
                        !AdvancesInteraction(zone,action,new())) continue;
                    if(sector?.Type==ZoneType.Goal && action.Instructions.Any(i=>i.Arguments.Count>0 &&
                        i.Opcode is InstructionOpcode.AddToCounter or InstructionOpcode.AddToSectorCounter && i.Arguments[0]<0)) continue;
                    var a = placed.Arguments;
                    if(authoredLock && !locks.Any(h=>h.X==a[0] && h.Y==a[1])) continue;
                    if (!(a[4] == item || a[4] == -1 && sector?.RequiredItem == item)) continue;
                    if (a[3]!=-1 && zone.GetTile(a[0], a[1], a[2]) != a[3]) continue;
                    if (!StateAllows(id, action)) continue;
                    return (id, a[0], a[1]);
                }
            }

        foreach (var id in rooms)
        {
            var zone = CurrentOrTemplate(id);
            foreach (var h in zone.Objects)
            {
                // Town briefing markers are authored as disabled, but the briefing
                // handler deliberately accepts them independently of hotspot activation.
                bool briefing = item == 0 && sector?.Type == ZoneType.Town && h.Type == SpawnLocationHotspot;
                if(!h.Enabled && _state.VisitedZones.Contains(id) && !briefing) continue;
                if (item == 0 && h.Type == SpawnLocationHotspot && !HasBumpScript(zone, h.X, h.Y))
                    return (id, h.X, h.Y);
                // The generator wrote the trader into the hotspot; the tile only appears on a visit.
                if (item > 0 && h.Type == SpawnLocationHotspot && sector != null && sector.Npc >= 0 &&
                    h.Argument == sector.Npc)
                    return (id, h.X, h.Y);
                if (item > 0 && h.Type == LockHotspot && h.Argument == item)
                {
                    if(SectorPuzzles.UsesHeldItem(zone,item,sector?.RequiredItem ?? -1)) continue;
                    bool alreadyApplied=zone.Actions.Any(a=>a.Conditions.Any(c=>
                        c.Opcode==ConditionOpcode.PlacedItemIs && c.Arguments.Count>=5 &&
                        c.Arguments[0]==h.X && c.Arguments[1]==h.Y &&
                        (c.Arguments[4]==item || c.Arguments[4]==-1) &&
                        zone.GetTile(c.Arguments[0],c.Arguments[1],c.Arguments[2])==unchecked((ushort)c.Arguments[3])) &&
                        StateAllows(id,a) && !AdvancesInteraction(zone,a,new()));
                    if(alreadyApplied) continue;
                    var nav=new Pathfinder(_gameData,_state);
                    if(Pathfinder.InteractionCells(h.X,h.Y,true)
                        .Any(p=>nav.IsWalkable(zone,p.Item1,p.Item2) && !nav.IsUnrelatedTransition(zone,p.Item1,p.Item2)))
                        return (id,h.X,h.Y);
                }
            }
        }
        return null;
    }

    private static bool AdvancesInteraction(Zone zone,Data.Action action,HashSet<Data.Action> seen)
    {
        if(!seen.Add(action)) return false;
        if(action.Instructions.Any(i=>i.Opcode is InstructionOpcode.RemoveItem or InstructionOpcode.AddItem or
            InstructionOpcode.DropItem or InstructionOpcode.MoveTile or InstructionOpcode.EnableHotspot or
            InstructionOpcode.EnableMonster or InstructionOpcode.ChangeZone or InstructionOpcode.MoveHeroTo or
            InstructionOpcode.MarkAsSolved or InstructionOpcode.WinGame ||
            i.Opcode==InstructionOpcode.PlaceTile && i.Arguments.Count>=4 &&
                zone.GetTile(i.Arguments[0],i.Arguments[1],i.Arguments[2])!=unchecked((ushort)i.Arguments[3]) ||
            i.Opcode==InstructionOpcode.RemoveTile && i.Arguments.Count>=3 &&
                zone.GetTile(i.Arguments[0],i.Arguments[1],i.Arguments[2])!=ushort.MaxValue)) return true;
        if(action.Instructions.Where(i=>i.Opcode==InstructionOpcode.SetVariable && i.Arguments.Count>=4 &&
            !(i.Arguments[3]==0 && action.Instructions.Any(n=>n.Opcode is InstructionOpcode.SpeakHero or InstructionOpcode.SpeakNpc)))
            .Any(set=>zone.Actions.Any(next=>next.Conditions.Any(c=>c.Opcode==ConditionOpcode.IsVariable &&
                c.Arguments.Count>=4 && c.Arguments[0]==set.Arguments[3] && c.Arguments[1]==set.Arguments[0] &&
                c.Arguments[2]==set.Arguments[1] && c.Arguments[3]==set.Arguments[2]) &&
                AdvancesInteraction(zone,next,seen)))) return true;
        return action.Instructions.Where(i=>i.Arguments.Count>0 &&
            i.Opcode is InstructionOpcode.SetCounter or InstructionOpcode.SetRandom).Any(set=>
                zone.Actions.Any(next=>!(set.Opcode==InstructionOpcode.SetCounter && set.Arguments[0]==0 &&
                    action.Instructions.Any(i=>i.Opcode is InstructionOpcode.SpeakHero or InstructionOpcode.SpeakNpc)) &&
                    next.Conditions.Any(c=>c.Arguments.Count>0 &&
                    c.Opcode==(set.Opcode==InstructionOpcode.SetCounter ? ConditionOpcode.CounterIs : ConditionOpcode.RandomIs) &&
                    c.Arguments[0]==set.Arguments[0]) && AdvancesInteraction(zone,next,seen)));
    }

    /// <summary>
    /// Whether an action's zone-state conditions hold: the zone's rolled random and counter.
    /// (A zone not yet visited has rolled nothing - accept, the visit will tell.)
    /// </summary>
    internal bool StateAllows(int zoneId, Data.Action action)
    {
        int actionIndex=_gameData.Zones[zoneId].Actions.IndexOf(action);
        if(actionIndex>=0 && IsScriptFailedWithoutProgress(zoneId,actionIndex)) return false;
        bool visited = _state.CurrentZoneId==zoneId || _state.VisitedZones.Contains(zoneId) ||
            _state.ZoneRandom.ContainsKey(zoneId);
        int random = _state.ZoneRandom.GetValueOrDefault(zoneId);
        int counter = _state.GetCounter(zoneId);
        foreach (var c in action.Conditions)
        {
            if (c.Arguments.Count < 1) continue;
            int v = c.Arguments[0];
            bool ok = c.Opcode switch
            {
                ConditionOpcode.IndyGameWon => _state.IsGameWon,
                ConditionOpcode.IndyGameLost => _state.IsGameOver,
                ConditionOpcode.StartingItemIs => (_worldGenerator.CurrentWorld?.StartingItemId ??
                    _worldGenerator.CurrentWorld?.Mission?.PuzzleChain.FirstOrDefault()?.RewardItemId)==v,
                ConditionOpcode.RequiredItemIs => _worldGenerator.CurrentWorld?.Sectors.GetValueOrDefault(zoneId) is { } requiredSector &&
                    (requiredSector.RequiredItem==v || requiredSector.AdditionalRequiredItem==v),
                ConditionOpcode.FindItemIs => _worldGenerator.CurrentWorld?.Sectors.GetValueOrDefault(zoneId)?.FindItem==v,
                ConditionOpcode.RandomIs => !visited || random == v,
                ConditionOpcode.RandomIsGreaterThan => !visited || random > v,
                ConditionOpcode.RandomIsLessThan => !visited || random < v,
                ConditionOpcode.CounterIs => counter == v,
                ConditionOpcode.CounterIsNot => counter != v,
                ConditionOpcode.SectorCounterIs => !visited || _state.GetSectorCounter(zoneId)==v,
                ConditionOpcode.SectorCounterIsNot => !visited || _state.GetSectorCounter(zoneId)!=v,
                ConditionOpcode.SectorCounterIsLessThan => !visited || _state.GetSectorCounter(zoneId)<v,
                ConditionOpcode.SectorCounterIsGreaterThan => !visited || _state.GetSectorCounter(zoneId)>v,
                ConditionOpcode.IsVariable when c.Arguments.Count >= 4 =>
                    _gameData.Zones[zoneId].GetTile(c.Arguments[1],c.Arguments[2],c.Arguments[3]) == unchecked((ushort)v),
                _ => true,
            };
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>
    /// The way to <paramref name="target"/> is shut: by water (an island only a vehicle reaches)
    /// or by a blockade. Either way a sector wants an item first, which a Find sector holds.
    /// Returns the next thing to do about it, or null if nothing obvious is left.
    /// </summary>
    private BotObjective? OpenTheWay(WorldMap world, int target)
    {
        var island = Component(world, target);
        var here = world.Sectors.TryGetValue(_state.CurrentZoneId, out var at) ? at.Zone : _state.CurrentZoneId;
        bool acrossWater = !island.Contains(here);
        bool ChangedUseTarget(Sector candidate) => _state.VisitedZones.Contains(candidate.Zone) &&
            _gameData.Zones[candidate.Zone].Actions.Any(a=>a.Conditions.Any(c=>c.Opcode==ConditionOpcode.PlacedItemIs &&
                c.Arguments.Count>=5 && (c.Arguments[4]==candidate.RequiredItem || c.Arguments[4]==-1) &&
                _gameData.Zones[candidate.Zone].GetTile(c.Arguments[0],c.Arguments[1],c.Arguments[2])!=unchecked((ushort)c.Arguments[3])));

        // A nearby gate is not necessarily on the mission's route. Removing the
        // candidate from the topology identifies which side contains the target.
        bool SeparatesTarget(Sector candidate)
        {
            if (!world.Connections.TryGetValue(candidate.Zone,out var links)) return false;
            int? beyond=candidate.Type switch { ZoneType.BlockadeNorth=>links.North,ZoneType.BlockadeSouth=>links.South,
                ZoneType.BlockadeEast=>links.East,ZoneType.BlockadeWest=>links.West,_=>null };
            if (beyond==null || target==candidate.Zone) return false;
            var reachable=new HashSet<int>{target}; var pending=new Queue<int>();pending.Enqueue(target);
            while(pending.TryDequeue(out int id))
                if(world.Connections.TryGetValue(id,out var c))
                    foreach(var next in new[]{c.North,c.South,c.East,c.West})
                        if(next is int n && n!=candidate.Zone && reachable.Add(n))pending.Enqueue(n);
            return reachable.Contains(beyond.Value) && (here==candidate.Zone || !reachable.Contains(here));
        }
        var gate = acrossWater
            ? world.Sectors.Values.Distinct().FirstOrDefault(s =>
                  s.Type == ZoneType.TravelStart && VehicleOf(s.Zone) is { } v && island.Contains(v.Argument))
            : world.Sectors.Values.Distinct()
                .Where(s => s.Type is ZoneType.BlockadeNorth or ZoneType.BlockadeSouth or ZoneType.BlockadeEast or ZoneType.BlockadeWest &&
                            !s.Solved && s.RequiredItem >= 0 && SeparatesTarget(s) &&
                            (FindUseSpot(world,s.Zone,s.RequiredItem)!=null || ChangedUseTarget(s)))
                .OrderBy(s => GridDistance(world, s.Zone)) // the one we are standing in first
                .FirstOrDefault();
        if (gate == null) return null;
        if(gate.Type==ZoneType.TravelStart && VehicleOf(gate.Zone) is { } cyclicalTransport &&
            IsUnchangedZoneCycle(gate.Zone,cyclicalTransport.Argument))
        {
            Console.WriteLine($"[BOT] Ignoring unchanged transport loop {gate.Zone}->{cyclicalTransport.Argument}");
            return null;
        }

        // A trader may move onto its use tile only after a greeting. An empty
        // target does not prove payment: resolve the authored exchange first.
        if(!gate.Solved && gate.RequiredItem>=0 &&
            !_consumedInteractions.Contains((gate.Zone,gate.RequiredItem)))
        {
            foreach(int id in ZoneAndRooms(gate.Zone))
            foreach(var (action,index) in _gameData.Zones[id].Actions.Select((a,i)=>(a,i)))
                if(!_state.DisabledActions.Contains((id,index)) &&
                    AdvancesInteraction(_gameData.Zones[id],action,new()) &&
                    TemplateGuardsMatch(world,_gameData.Zones[id],action) &&
                    action.Conditions.Any(c=>c.Opcode==ConditionOpcode.PlacedItemIs && c.Arguments.Count>=5 &&
                        (c.Arguments[4]==gate.RequiredItem || c.Arguments[4]==-1)))
                {
                    if(!_state.HasItem(gate.RequiredItem)) return FetchItem(world,gate.RequiredItem);
                    if(NativeGoalPrerequisite(world,gate,authoredAction:(id,index)) is { } exchange)
                        return exchange;
                }
        }

        // A paid guard can move aside in stages: a follow-up bump and a real
        // re-entry may remove its new blocking position. Follow those authored
        // prerequisites instead of fetching another copy of the consumed fee.
        if(gate.Zone==_state.CurrentZoneId && !_state.HasItem(gate.RequiredItem) &&
            gate.Type is ZoneType.BlockadeNorth or ZoneType.BlockadeSouth or ZoneType.BlockadeEast or ZoneType.BlockadeWest)
        {
            var zone=_gameData.Zones[gate.Zone];
            bool used=zone.Actions.Any(a=>a.Conditions.Any(c=>c.Opcode==ConditionOpcode.PlacedItemIs &&
                c.Arguments.Count>=5 && (c.Arguments[4]==gate.RequiredItem || c.Arguments[4]==-1) &&
                zone.GetTile(c.Arguments[0],c.Arguments[1],c.Arguments[2])!=unchecked((ushort)c.Arguments[3])));
            if(used)
            {
                var rooms=ZoneAndRooms(gate.Zone).Select(id=>_gameData.Zones[id]).ToList();
                int length=gate.Type is ZoneType.BlockadeNorth or ZoneType.BlockadeSouth ? zone.Width : zone.Height;
                for(int i=1;i<length-1;i++)
                {
                    int x=gate.Type==ZoneType.BlockadeWest ? 0 : gate.Type==ZoneType.BlockadeEast ? zone.Width-1 : i;
                    int y=gate.Type==ZoneType.BlockadeNorth ? 0 : gate.Type==ZoneType.BlockadeSouth ? zone.Height-1 : i;
                    if(GoalRouteOpening(rooms,zone.Id,x,y,false) is { Action:null } opening &&
                        NativeGoalPrerequisite(world,gate,(opening.Zone.Id,opening.X,opening.Y)) is { } prepare)
                        return prepare;
                }
            }
        }

        if (!gate.Solved && gate.RequiredItem >= 0 && !_consumedInteractions.Contains((gate.Zone,gate.RequiredItem)) &&
            FindUseSpot(world,gate.Zone,gate.RequiredItem) is { } use)
        {
            if (!_state.HasItem(gate.RequiredItem)) return FetchItem(world,gate.RequiredItem);
            var instruction=UseAt(gate.RequiredItem,use.x,use.y,"Open the way");
            instruction.TargetZoneId=use.zone;
            return NativeGoalPrerequisite(world,gate,interaction:instruction);
        }

        // Some transports require a key, switches, or combat after their first
        // unlock. Their native departure script owns those prerequisites; the
        // vehicle hotspot alone does not prove boarding is ready.
        if(!gate.Solved && gate.Type==ZoneType.TravelStart && VehicleOf(gate.Zone) is { } transport)
        {
            // The cockpit can be in a child room while the exterior hotspot
            // is only a transit marker. Prefer its proved authored departure
            // over trying to walk onto that inaccessible exterior marker.
            foreach(int room in ZoneAndRooms(gate.Zone))
            foreach(var departure in ScriptedZoneTravel.Instructions(_gameData.Zones[room],_state,_gameData,_canWalk)
                .Where(p=>p.TargetZoneId==transport.Argument))
            {
                if(_state.CurrentZoneId==room) return departure;
                if(GraphTravel(world,room,departure.PassageRequiresBump
                    ? Pathfinder.InteractionCells(departure.TargetX,departure.TargetY,false)
                    : new[]{(departure.TargetX,departure.TargetY)}) is { } approach) return approach;
            }
            var departureZone=_gameData.Zones[gate.Zone];
            foreach(var (action,index) in departureZone.Actions.Select((a,i)=>(a,i)).Where(p=>
                !p.a.Conditions.Any(c=>c.Opcode==ConditionOpcode.ZoneNotInitialized) &&
                (p.a.Instructions.Any(i=>i.Opcode==InstructionOpcode.ChangeZone && i.Arguments.Count>=3 &&
                    i.Arguments[0]==transport.Argument) ||
                p.a.Instructions.Any(i=>i.Opcode==InstructionOpcode.MarkAsSolved) &&
                (p.a.Instructions.Any(i=>i.Opcode==InstructionOpcode.HideHero) ||
                 p.a.Instructions.Any(i=>i.Opcode==InstructionOpcode.MoveHeroTo && i.Arguments.Count>=2 &&
                    i.Arguments[0]==transport.X && i.Arguments[1]==transport.Y)))))
                if(NativeGoalPrerequisite(world,gate,authoredAction:(gate.Zone,index)) is { } departure)
                    return departure;
        }

        var boarding = gate.Type==ZoneType.TravelStart
            ? ScriptedTransport.BoardingSpots(_gameData.Zones[gate.Zone],_state,destination:VehicleOf(gate.Zone)?.Argument).ToList() : new();
        if(boarding.Count>0 && VehicleOf(gate.Zone) is { } readyVehicle)
        {
            var point=boarding[0];
            return _state.CurrentZoneId==gate.Zone
                ? new BotObjective { Type=ObjectiveType.BumpTarget,TargetX=point.X,TargetY=point.Y,
                    TargetZoneId=readyVehicle.Argument,Description="Board the unlocked transport" }
                : NavigateToward(world,gate.Zone,gate.Zone,point.X,point.Y,adjacent:true);
        }

        if (gate.Type == ZoneType.TravelStart && VehicleOf(gate.Zone) is { } vehicle)
            return _state.CurrentZoneId == gate.Zone
                ? new BotObjective { Type = ObjectiveType.BumpTarget, Description = "Take the vehicle",
                    TargetX = vehicle.X, TargetY = vehicle.Y, TargetZoneId = vehicle.Argument }
                : NavigateToward(world, gate.Zone, gate.Zone, vehicle.X, vehicle.Y, adjacent: true);
        return null;
    }

    /// <summary>Go to where <paramref name="item"/> lies - a Find sector's hotspot, maybe in a room.</summary>
    private readonly HashSet<string> _reportedMissingSources = new();

    private int? _itemRouteFocus;
    private int _itemRouteRevision;

    private BotObjective? CurrentItemSupply(int item)
    {
        if(_state.CurrentZone is not { } zone) return null;
        foreach(var marker in zone.Objects.Where(h=>h.Enabled && h.Argument==item &&
            h.Type is ZoneObjectType.CrateItem or ZoneObjectType.CrateWeapon or ZoneObjectType.Trigger))
        {
            if(_state.IsObjectCollected(zone.Id,marker.X,marker.Y)) continue;
            int cover=zone.GetTile(marker.X,marker.Y,1);
            if(cover<_gameData.Tiles.Count && _gameData.Tiles[cover].IsDraggable)
            {
                var clear=new BotObjective { Type=ObjectiveType.ClearObject,TargetX=marker.X,TargetY=marker.Y,
                    RequiredItemId=item,Description=$"Uncover the active supply of item {item}" };
                if(PlannedBlockMove(clear) is { } move) return move;
            }
        }
        return null;
    }

    private BotObjective? FetchItem(WorldMap world,int item)
    {
        int revision=_itemRouteRevision;
        var step=FetchItemCore(world,item);
        // Preserve the deepest active dependency across zone transitions. A
        // newly entered room must not restart the outer mission and reverse it.
        if(revision==_itemRouteRevision && !_state.HasItem(item) && step!=null && step.Type is not
            (ObjectiveType.Explore or ObjectiveType.PlanningBlocked or ObjectiveType.UnresolvedReward))
        { _itemRouteFocus=item; _itemRouteRevision++; }
        return step;
    }

    // A sprite can represent both an inventory item and a scripted trade actor.
    // Its presence is not a physical reward until the authored handoff removes it.
    private BotObjective? FetchItemCore(WorldMap world, int item)
    {
        _neededNativeItems.Add(item);
        if(TryLocalItem(world,item) is { } local) return local;
        // Prefer the selected, enabled supply in the room we just entered.
        // Remote alternative producers must not send us back out before we
        // move the furniture hiding this room's actual item.
        if(CurrentItemSupply(item) is { } supply) return supply;
        // Script drops and monster rewards need not have a matching hotspot argument.
        var sources = world.Sectors.Values.Distinct().Where(s => s.FindItem == item || s.AdditionalGainItem == item ||
            ZoneAndRooms(s.Zone).Any(id=>_gameData.Zones[id].Monsters.Any(m=>m.DropsLoot && m.Loot>0 && m.Loot-1==item) ||
                _gameData.Zones[id].Objects.Any(h=>h.Argument==item && h.Type is
                    ZoneObjectType.CrateItem or ZoneObjectType.CrateWeapon or ZoneObjectType.Trigger) ||
                _gameData.Zones[id].Actions.Any(a=>TemplateGuardsMatch(world,_gameData.Zones[id],a) && ProvidesReward(a,item,s))))
            .OrderBy(s=>ZoneAndRooms(s.Zone).Contains(_state.CurrentZoneId) ? 0 : 1).ToList();
        foreach (var source in sources)
        foreach (int id in ZoneAndRooms(source.Zone))
        {
            var zone = _gameData.Zones[id];
            for (int y = 0; y < zone.Height; y++)
            for (int x = 0; x < zone.Width; x++)
                if (zone.GetTile(x, y, 1) == item && !ScriptedActorTiles.IsTradeTarget(zone,x,y,item) &&
                    !zone.Actions.Select((a,i)=>(a,i)).Any(p=>!_state.DisabledActions.Contains((id,p.i)) &&
                        p.a.Conditions.Any(c=>c.Opcode==ConditionOpcode.Bump && c.Arguments.Count>=3 &&
                            c.Arguments[0]==x && c.Arguments[1]==y && c.Arguments[2]==item)) &&
                    !(source.Npc == item && zone.Objects.Any(h => h.X == x && h.Y == y &&
                        h.Type is ZoneObjectType.SpawnLocation or ZoneObjectType.PuzzleNPC)))
                {
                    if(id!=_state.CurrentZoneId) return NavigateToReward(world,id,source.Zone,x,y);
                    var pickupNav=new Pathfinder(_gameData,_state) { AllowDiagonal=true, PreferCardinal=true,AdditionalWalkability=_canWalk,TransitionTarget=(id,x,y) };
                    var fixedActors=_state.ZoneNPCs.Where(n=>!n.IsHostile &&
                        n.Behavior is NPCBehavior.Stationary or NPCBehavior.Animation).ToList();
                    if(pickupNav.FindPath(zone,_state.PlayerX,_state.PlayerY,x,y,fixedActors)==null)
                    {
                        if(GraphTravel(world,id,new[] { (x,y) }) is { } passage) return passage;
                        // A visible reward can still be behind a scripted gate.
                        // Resolve its opening guards before asking the executor to walk there.
                        if(_nativeRewardRequests.Add(item))
                        {
                            try
                            {
                                if(NativeGoalPrerequisite(world,source,rewardItem:item) is { } prepare)
                                    return prepare;
                            }
                            finally { _nativeRewardRequests.Remove(item); }
                        }
                    }
                    return new BotObjective { Type = ObjectiveType.PickupItem, TargetX = x, TargetY = y,
                        Description = $"Collect script reward {item} at ({x},{y})" };
                }

            foreach (var (action, index) in zone.Actions.Select((a, i) => (a, i)))
            {
                if (_state.DisabledActions.Contains((id, index)) || !StateAllows(id, action)) continue;
                bool provides = ProvidesReward(action,item,source);
                if (!provides) continue;
                // A pure bump/standing reward can be executed directly. Item-dependent
                // scripts must go through dependency planning and item targeting instead.
                if (action.Conditions.Any(c => c.Opcode is ConditionOpcode.PlacedItemIs or
                    ConditionOpcode.PlacedItemIsNot or ConditionOpcode.HasItem)) continue;
                var trigger = action.Conditions.FirstOrDefault(c =>
                    c.Opcode is ConditionOpcode.Bump or ConditionOpcode.StandingOn && c.Arguments.Count >= 2);
                if (trigger == null) continue;
                int tx = trigger.Arguments[0], ty = trigger.Arguments[1];
                if (tx < 0 || ty < 0 || tx >= zone.Width || ty >= zone.Height) continue;
                var stance=action.Conditions.FirstOrDefault(c=>c.Opcode==ConditionOpcode.HeroIsAt && c.Arguments.Count>=2);
                int approachX=stance?.Arguments[0] ?? tx,approachY=stance?.Arguments[1] ?? ty;
                bool adjacent=stance==null && trigger.Opcode==ConditionOpcode.Bump;
                // Validate the selected reward prop before traveling to it.
                // Other randomized props can belong to the same room family.
                if (trigger.Arguments.Count >= 3)
                {
                    int tile = trigger.Arguments[2];
                    int layer = trigger.Opcode == ConditionOpcode.StandingOn ? 0 : 1;
                    if (tile != -1 && zone.GetTile(tx, ty, layer) != unchecked((ushort)tile))
                    {
                        int covering = zone.GetTile(tx,ty,layer);
                        bool movable = layer==1 && covering<_gameData.Tiles.Count && _gameData.Tiles[covering].IsDraggable;
                        bool chestSetup = zone.Actions.Any(setup => setup.Conditions.Any(c=>c.Opcode==ConditionOpcode.TileAtIs &&
                            c.Arguments.Count>=4 && c.Arguments[0]==-1 && c.Arguments[1]==tx && c.Arguments[2]==ty && c.Arguments[3]==1) &&
                            setup.Instructions.Any(i=>i.Opcode==InstructionOpcode.PlaceTile && i.Arguments.Count>=4 &&
                                i.Arguments[0]==tx && i.Arguments[1]==ty && i.Arguments[2]==1 && i.Arguments[3]==tile));
                        if (movable && chestSetup)
                        {
                            // The native script creates the chest after this square is cleared.
                            // Clear it and retain an approach; the chest itself will be solid.
                            if(id!=_state.CurrentZoneId)
                                return NavigateToward(world,id,source.Zone,tx,ty,adjacent:true);
                            return new BotObjective { Type=ObjectiveType.ClearObject,TargetX=tx,TargetY=ty,
                                RequiredItemId=item,Description=$"Clear the block at ({tx},{ty}) to reveal scripted reward {item}" };
                        }
                        continue;
                    }
                }
                if (id != _state.CurrentZoneId) return NavigateToward(world,id,source.Zone,approachX,approachY,adjacent);
                var nav=new Pathfinder(_gameData,_state) { AllowDiagonal=true, PreferCardinal=true,AdditionalWalkability=_canWalk };
                var actors=_state.ZoneNPCs.Where(n=>!n.IsHostile).ToList();
                bool reachable=adjacent ? nav.FindPathToAdjacent(zone,_state.PlayerX,_state.PlayerY,tx,ty,actors,allowDiagonalInteraction:true)!=null :
                    nav.FindPath(zone,_state.PlayerX,_state.PlayerY,approachX,approachY,actors)!=null;
                var approaches=adjacent ? Pathfinder.InteractionCells(tx,ty,true) : new[] { (approachX,approachY) };
                if(!reachable && GraphTravel(world,id,approaches) is { } route) return route;
                if(!reachable && _nativeRewardRequests.Add(item))
                {
                    try
                    {
                        if(NativeGoalPrerequisite(world,source,rewardItem:item) is { } prerequisite && prerequisite.Type is not
                            (ObjectiveType.Explore or ObjectiveType.PlanningBlocked or ObjectiveType.UnresolvedReward))
                            return prerequisite;
                    }
                    finally { _nativeRewardRequests.Remove(item); }
                }
                // A template can contain alternative reward props. An intact
                // but unreachable prop must not hide a reachable provider.
                if(!reachable) continue;
                return new BotObjective { Type = trigger.Opcode == ConditionOpcode.Bump
                    ? ObjectiveType.BumpTarget : ObjectiveType.WalkToPosition,
                    ApproachX=stance?.Arguments[0],ApproachY=stance?.Arguments[1],
                    TargetX = tx, TargetY = ty, Description = $"Trigger scripted reward {item} at ({tx},{ty})" };
            }
        }
        // A trade/use sector is itself the producer of its FindItem. If the
        // reward has not been laid down yet, resolve the sector's input and
        // perform its authored interaction before treating the reward as a
        // missing physical pickup. This lets native dependencies recurse:
        // e.g. item A is traded for B, which is traded for C.
        foreach (var source in sources.Where(s => s.FindItem == item &&
                     (s.Type is ZoneType.Trade or ZoneType.Use) && !s.Solved && !_state.IsZoneSolved(s.Zone)))
        {
            if (source.RequiredItem > 0 && !_state.HasItem(source.RequiredItem))
            {
                if (FetchItem(world, source.RequiredItem) is { } prerequisite &&
                    prerequisite.Type is not (ObjectiveType.Explore or ObjectiveType.PlanningBlocked or ObjectiveType.UnresolvedReward))
                    return prerequisite;
                continue;
            }

            var use = FindUseSpot(world, source.Zone, source.RequiredItem);
            if (use is not { } at)
            {
                // Some transactions reveal their spawn hotspot only after the
                // encounter script runs. Activate that authored hotspot before
                // deciding that the generated reward has no source.
                foreach(int room in ZoneAndRooms(source.Zone))
                {
                    var zone=_gameData.Zones[room];
                    foreach(var (marker,index) in zone.Objects.Select((h,i)=>(h,i)).Where(p=>
                        !p.h.Enabled && p.h.Type==SpawnLocationHotspot && p.h.Argument==source.Npc && source.Npc>=0))
                    foreach(var (_,actionIndex) in zone.Actions.Select((a,i)=>(a,i)).Where(p=>
                        p.a.Instructions.Any(i=>i.Opcode==InstructionOpcode.EnableHotspot &&
                            i.Arguments.Count>0 && i.Arguments[0]==index)))
                        if(NativeGoalPrerequisite(world,source,authoredAction:(room,actionIndex)) is { } activate &&
                            activate.Type is not (ObjectiveType.Explore or ObjectiveType.PlanningBlocked or ObjectiveType.UnresolvedReward))
                        { return activate; }
                }
                continue;
            }
            if (source.RequiredItem <= 0)
                return new BotObjective { Type=ObjectiveType.TalkToNpc,TargetX=at.x,TargetY=at.y,
                    TargetNpc=new NPC { X=at.x,Y=at.y,Health=1,IsEnabled=true },
                    Description=$"Talk to the reward source in zone {source.Zone}" };

            var exchange=UseAt(source.RequiredItem,at.x,at.y,$"Trade for item {item}");
            exchange.TargetZoneId=at.zone;
            if (NativeGoalPrerequisite(world,source,interaction:exchange) is { } action &&
                action.Type is not (ObjectiveType.Explore or ObjectiveType.PlanningBlocked or ObjectiveType.UnresolvedReward)) return action;
            // Transaction NPCs may accept an inventory item through their
            // standard interaction even when the data has no PlacedItemIs
            // script. The authored sector hotspot is the target in that case.
            if (_state.CurrentZoneId != at.zone)
                return GraphTravel(world,at.zone,Pathfinder.InteractionCells(at.x,at.y,true)) ??
                    NavigateToward(world,at.zone,source.Zone,at.x,at.y,adjacent:true);
            return exchange;
        }
        // Some rewards are produced by counter/variable actions rather than a
        // direct bump. Resolve their full authored producer chain as well.
        if(_nativeRewardRequests.Add(item))
        {
            try
            {
                foreach(var finder in sources)
                    if(NativeGoalPrerequisite(world,finder,rewardItem:item) is { } prerequisite)
                        return prerequisite;
            }
            finally { _nativeRewardRequests.Remove(item); }
        }
        foreach (var finder in sources)
            foreach (var id in ZoneAndRooms(finder.Zone))
                foreach(var spot in _gameData.Zones[id].Objects.Where(o=>o.Enabled && o.Argument==item))
                {
                    var sourceZone=_state.CurrentZoneId==id ? _state.CurrentZone! : _gameData.Zones[id];
                    int covering = sourceZone.GetTile(spot.X,spot.Y,1);
                    var bumpClear=spot.Type==ZoneObjectType.Trigger ? sourceZone.Actions
                        .Where(a=>a.Conditions.Any(c=>c.Opcode==ConditionOpcode.Bump && c.Arguments.Count>=3 &&
                            c.Arguments[0]==spot.X && c.Arguments[1]==spot.Y && c.Arguments[2]==covering))
                        .FirstOrDefault(a=>a.Instructions.Any(i=>i.Opcode==InstructionOpcode.RemoveTile &&
                            i.Arguments.Count>=3 && i.Arguments[0]==spot.X && i.Arguments[1]==spot.Y && i.Arguments[2]==1)) : null;
                    if(covering<_gameData.Tiles.Count && _gameData.Tiles[covering].IsObject && bumpClear!=null)
                    {
                        if(id!=_state.CurrentZoneId)
                            return NavigateToward(world,id,finder.Zone,spot.X,spot.Y,adjacent:true);
                        return new BotObjective { Type=ObjectiveType.BumpTarget,TargetX=spot.X,TargetY=spot.Y,
                            RequiredItemId=item,ScriptIndex=sourceZone.Actions.IndexOf(bumpClear),
                            Description=$"Bump the cover to reveal item {item} at ({spot.X},{spot.Y})" };
                    }
                    bool staticMarker = covering < _gameData.Tiles.Count && _gameData.Tiles[covering].IsObject &&
                        !_gameData.Tiles[covering].IsDraggable;
                    bool scriptedSource = _gameData.Zones[id].Actions.Any(a => ProvidesReward(a,item,finder));
                    if (staticMarker && scriptedSource && _state.VisitedZones.Contains(id))
                    {
                        if (RewardEnemyPrerequisite(world,finder,item,id) is { } prerequisite) return prerequisite;
                        bool completedSource = finder.Solved || _state.IsZoneSolved(id) ||
                            _gameData.Zones[id].Actions.Select((a,i)=>(a,i)).Where(p=>ProvidesReward(p.a,item,finder))
                                .All(p=>_state.DisabledActions.Contains((id,p.i)));
                        if (!completedSource)
                            return new BotObjective { Type=ObjectiveType.Explore,TargetZoneId=id,
                                Description=$"Reward {item} in zone {id} still has pending native prerequisites" };
                        // Some native marker coordinates lie in a wall; the reward comes
                        // from a different scripted NPC. Never revisit the wall as a fallback.
                        return new BotObjective { Type = ObjectiveType.UnresolvedReward,
                            TargetZoneId=id,RequiredItemId=item,TargetX=spot.X,TargetY=spot.Y,
                            Description=$"Reward {item} in zone {id} has no active script or physical drop; marker ({spot.X},{spot.Y}) is static tile {covering}, counter={_state.GetCounter(id)}" };
                    }
                    if (_state.CurrentZoneId != id)
                        return NavigateToReward(world, id, finder.Zone, spot.X, spot.Y);
                    if(_state.ZoneNPCs.FirstOrDefault(n=>n.IsEnabled && n.IsAlive && n.IsHostile &&
                        n.X==spot.X && n.Y==spot.Y) is { } blocker)
                        return new BotObjective { Type=ObjectiveType.KillEnemy,TargetNpc=blocker,
                            TargetX=blocker.X,TargetY=blocker.Y,RequiredItemId=item,
                            Description=$"Defeat enemy blocking reward {item} at ({spot.X},{spot.Y})" };
                    // Only if it is actually lying there: Find zones hide items behind their own
                    // puzzles (a locked chest), which this bot does not solve yet.
                    if (_state.CurrentZone!.GetTile(spot.X, spot.Y, 1) == item &&
                        !ScriptedActorTiles.IsTradeTarget(zone: _state.CurrentZone,x: spot.X,y: spot.Y,tile: item))
                        return new BotObjective { Type = ObjectiveType.PickupItem, Description = $"Fetch item {item}", TargetX = spot.X, TargetY = spot.Y };
                    // Hidden under movable furniture: solve the arrangement as
                    // a whole, including the approach and retreat cells.
                    if (covering < _gameData.Tiles.Count && _gameData.Tiles[covering].IsDraggable)
                        return new BotObjective { Type=ObjectiveType.ClearObject,TargetX=spot.X,TargetY=spot.Y,
                            RequiredItemId=item,Description=$"Uncover item {item} at ({spot.X},{spot.Y}) in zone {id}" };
                    Console.WriteLine($"[BOT] Item {item} is at ({spot.X},{spot.Y}) in zone {id} but not reachable yet (tile {_state.CurrentZone.GetTile(spot.X, spot.Y, 1)})");
                    continue;
                }
        var missingKey = $"{_state.CurrentZoneId}:{item}:{string.Join(',', _state.Inventory)}:{_state.DisabledActions.Count}:{_state.SolvedZones.Count}";
        if (_reportedMissingSources.Add(missingKey))
            Console.WriteLine($"[BOT] No executable source for item {item} yet (hotspots, physical rewards, and script triggers checked)");
        return null;
    }

    private BotObjective? RewardEnemyPrerequisite(WorldMap world,Sector sector,int item,int zoneId)
    {
        var zone=_gameData.Zones[zoneId];
        // Native rewards can wait on kills directly, or on a counter/variable/NPC
        // produced by another action in the same authored encounter.
        var required=new HashSet<int>();
        foreach(var (action,index) in zone.Actions.Select((a,i)=>(a,i)))
        {
            if(_state.DisabledActions.Contains((zoneId,index)) || !StateAllows(zoneId,action)) continue;
            foreach(var c in action.Conditions)
            {
                if(c.Opcode==ConditionOpcode.MonsterIsDead && c.Arguments.Count>0) required.Add(c.Arguments[0]);
                if(c.Opcode==ConditionOpcode.HasNoActiveMonsters)
                    foreach(int i in Enumerable.Range(0,zone.Monsters.Count)) required.Add(i);
            }
        }
        if(required.Count==0) return null;
        if(zoneId!=_state.CurrentZoneId)
            return NavigateToward(world,zoneId,sector.Zone);
        var enemy=required.Where(i=>i>=0 && i<_state.ZoneNPCs.Count).Select(i=>_state.ZoneNPCs[i])
            .Where(n=>n.IsAlive && n.IsEnabled && n.IsHostile)
            .OrderBy(n=>n.DistanceTo(_state.PlayerX,_state.PlayerY)).FirstOrDefault();
        return enemy==null ? null : new BotObjective { Type=ObjectiveType.KillEnemy,TargetNpc=enemy,
            TargetX=enemy.X,TargetY=enemy.Y,RequiredItemId=item,
            Description=$"Defeat scripted prerequisite for reward {item} in zone {zoneId}" };
    }

    private static bool ProvidesReward(Data.Action action,int item,Sector sector) => action.Instructions.Any(i =>
        i.Arguments.Count > 0 && (i.Opcode == InstructionOpcode.AddItem && i.Arguments[0] == item ||
            i.Opcode == InstructionOpcode.DropItem && (i.Arguments[0] == item || i.Arguments[0] == -1 && sector.FindItem == item)));

    private BotObjective UseAt(int item, int x, int y, string why) => new()
    {
        Type = ObjectiveType.UseItemOnNpc, Description = $"{why}: use item {item}",
        TargetNpc = new NPC { X = x, Y = y, IsEnabled = true, Health = 1 }, TargetX = x, TargetY = y, RequiredItemId = item,
        ExpectedRewardItemId = _worldGenerator.CurrentWorld?.Sectors.GetValueOrDefault(_state.CurrentZoneId) is { } sector &&
            sector.RequiredItem == item && sector.FindItem > 0 ? sector.FindItem : null,
    };

    private int GridDistance(WorldMap world, int zoneId)
    {
        var here = world.Sectors.TryGetValue(_state.CurrentZoneId, out var s) ? s.Zone : _state.CurrentZoneId;
        if (world.Grid == null) return 0;
        var (hx, hy) = FindCell(world, here);
        var (tx, ty) = FindCell(world, zoneId);
        return Math.Abs(hx - tx) + Math.Abs(hy - ty);
    }

    private static (int x, int y) FindCell(WorldMap world, int zoneId)
    {
        for (int y = 0; y < world.Grid!.GetLength(0); y++)
            for (int x = 0; x < world.Grid.GetLength(1); x++)
                if (world.Grid[y, x] == zoneId) return (x, y);
        return (100, 100);
    }

    private ZoneObject? VehicleOf(int zoneId) =>
        _gameData.Zones[zoneId].Objects.FirstOrDefault(o => o.Type == ZoneObjectType.VehicleToSecondary);

    /// <summary>Grid zones joined to <paramref name="zoneId"/> by walking.</summary>
    private static HashSet<int> Component(WorldMap world, int zoneId)
    {
        var seen = new HashSet<int> { zoneId };
        var pending = new Queue<int>(seen);
        while (pending.Count > 0 && world.Connections.TryGetValue(pending.Dequeue(), out var c))
            foreach (var next in new[] { c.North, c.South, c.East, c.West })
                if (next is { } n && seen.Add(n)) pending.Enqueue(n);
        return seen;
    }

    private bool CanReach(WorldMap world, int zoneId)
    {
        var here = world.Sectors.TryGetValue(_state.CurrentZoneId, out var s) ? s.Zone : _state.CurrentZoneId;
        if (!Component(world, here).Contains(zoneId)) return false; // across water
        return here == zoneId || GetDirectionTowardZone(zoneId) != null;
    }

    private static bool HasBumpScript(Zone zone, int x, int y) =>
        zone.Actions.Any(a => a.Conditions.Any(c =>
            c.Opcode == ConditionOpcode.Bump && c.Arguments.Count > 1 && c.Arguments[0] == x && c.Arguments[1] == y));

    /// <summary>One step toward <paramref name="targetZone"/>: out of this room, into the right door, or across the grid.</summary>
    private BotObjective NavigateToReward(WorldMap world, int zoneId, int outdoor, int x, int y)
    {
        // Reward markers may be off-map placeholders until the room's native
        // initializer chooses their location. Prove entry before using those
        // coordinates as a navigation goal.
        if (!_state.VisitedZones.Contains(zoneId))
            return NavigateToward(world,zoneId,outdoor);
        var targets = new List<(int X,int Y)> { (x,y) };
        int covering = _gameData.Zones[zoneId].GetTile(x,y,1);
        if (covering < _gameData.Tiles.Count &&
            (_gameData.Tiles[covering].IsDraggable || _gameData.Tiles[covering].IsObject))
            targets.AddRange(new[] { (x,y-1), (x,y+1), (x-1,y), (x+1,y) });
        // A covered reward needs an approach for manipulation, not an already-empty tile.
        if (GraphTravel(world, zoneId, targets) is { } route) return route;
        return NavigateToward(world,zoneId,outdoor,x,y,adjacent:targets.Count>1);
    }

    private readonly HashSet<(int Zone,int X,int Y,bool Adjacent)> _routePreparations = new();
    private bool _openingRoute;

    private BotObjective NavigateToward(WorldMap world, int targetZone, int outdoor,
        int? targetX = null, int? targetY = null, bool adjacent = false)
    {
        IReadOnlyCollection<(int X,int Y)>? targets = null;
        if (targetX is int x && targetY is int y)
            targets = adjacent ? new[] { (x,y-1), (x,y+1), (x-1,y), (x+1,y) } : new[] { (x,y) };
        if (GraphTravel(world, targetZone, targets) is { } instruction) return instruction;
        // At a known unpaid blockade, resolve its exchange before searching
        // hypothetical routes through every wall in the generated world.
        if(!_openingRoute && world.Sectors.GetValueOrDefault(_state.CurrentZoneId) is { Solved:false } currentGate &&
            currentGate.Type is ZoneType.BlockadeNorth or ZoneType.BlockadeSouth or ZoneType.BlockadeEast or ZoneType.BlockadeWest &&
            !_consumedInteractions.Contains((currentGate.Zone,currentGate.RequiredItem)))
        {
            _openingRoute=true;
            try
            {
                if(OpenTheWay(world,outdoor) is { } payment && payment.Type is not
                    (ObjectiveType.Explore or ObjectiveType.PlanningBlocked or ObjectiveType.UnresolvedReward)) return payment;
            }
            finally { _openingRoute=false; }
        }
        int tx=targetX ?? -1, ty=targetY ?? -1;
        if(world.Sectors.GetValueOrDefault(outdoor) is { } sector &&
            _routePreparations.Add((targetZone,tx,ty,adjacent)))
        {
            try
            {
                var rooms=ZoneAndRooms(outdoor).Select(id=>_gameData.Zones[id]).ToList();
                var rejected=new HashSet<(int Zone,int Action)>();
                var blockedOpenings=new HashSet<(int Zone,int X,int Y)>();
                for(int attempt=0;attempt<8;attempt++)
                {
                    if(GoalRouteOpening(rooms,targetZone,tx,ty,adjacent,(z,i)=>rejected.Contains((z,i)) ||
                        _unreachableRouteActions.Contains((targetZone,tx,ty,z,i,RouteResolutionState(z))),
                        blockedOpenings:blockedOpenings) is not { } opening) break;
                    Console.WriteLine($"[BOT] Route prerequisite to zone {targetZone}: open ({opening.X},{opening.Y}) in zone {opening.Zone.Id}, action={opening.Action}");
                    var prepare=opening.Action is int action
                        ? NativeGoalPrerequisite(world,sector,authoredAction:(opening.Zone.Id,action))
                        : NativeGoalPrerequisite(world,sector,(opening.Zone.Id,opening.X,opening.Y));
                    if(prepare!=null && prepare.Type is not
                        (ObjectiveType.Explore or ObjectiveType.PlanningBlocked or ObjectiveType.UnresolvedReward))
                        return prepare;
                    if(opening.Action is int failed)
                    {
                        _unreachableRouteActions.Add((targetZone,tx,ty,opening.Zone.Id,failed,
                            RouteResolutionState(opening.Zone.Id)));
                        if(!rejected.Add((opening.Zone.Id,failed))) break;
                    }
                    else
                    {
                        int count=rejected.Count;
                        foreach(var (producer,index) in opening.Zone.Actions.Select((a,i)=>(a,i)))
                            if(ActionWritesTile(opening.Zone,producer,opening.X,opening.Y,1,-1))
                                rejected.Add((opening.Zone.Id,index));
                        if(rejected.Count==count && !blockedOpenings.Add((opening.Zone.Id,opening.X,opening.Y))) break;
                    }
                }
            }
            finally { _routePreparations.Remove((targetZone,tx,ty,adjacent)); }
        }
        // A full route can be unavailable because an earlier generated gate
        // needs its own quest item or transport. Resolve that dependency before
        // declaring the destination unreachable, without recursing into itself.
        if(!_openingRoute)
        {
            _openingRoute=true;
            try
            {
                if(OpenTheWay(world,outdoor) is { } prerequisite) return prerequisite;
            }
            finally { _openingRoute=false; }
        }
        // Do not fall back to a zone-only route: it can recreate the very crossing
        // that component routing rejected. Explore/open prerequisites and replan.
        return new BotObjective { Type = ObjectiveType.Explore, TargetZoneId = targetZone,
            Description = $"No currently walkable entrance/exit route to zone {targetZone}" };
    }

    internal IEnumerable<int> ZoneAndRooms(int zoneId)
    {
        var seen = new HashSet<int>();
        var pending = new Stack<int>();
        // Scripted passages can link mission rooms without a DoorEntrance
        // hotspot. The generated ownership map is authoritative for that family.
        if(_worldGenerator.CurrentWorld is { } generated)
            foreach(var pair in generated.Sectors.Where(p=>p.Value.Zone==zoneId && p.Key!=zoneId)) pending.Push(pair.Key);
        pending.Push(zoneId);
        while (pending.Count > 0)
        {
            var id = pending.Pop();
            if (id < 0 || id >= _gameData.Zones.Count || !seen.Add(id)) continue;
            yield return id;
            foreach (var door in _gameData.Zones[id].Objects.Where(o => o.Type == ZoneObjectType.DoorEntrance))
                pending.Push(door.Argument);
            if(_worldGenerator.CurrentWorld is { } world)
                foreach(var room in world.RoomParents.Where(p=>p.Value==id)) pending.Push(room.Key);
        }
    }
}
