using YodaStoriesNG.Engine.Data;
using YodaStoriesNG.Engine.Game;

namespace YodaStoriesNG.Engine.Bot;

/// <summary>
/// Plans mission dependencies and executable routes from the generated world
/// and its authored scripts. Live actors and object movement are resolved
/// before the bot resumes the pending mission action.
/// </summary>
public partial class MissionSolver
{
    private readonly GameState _state;
    private readonly GameData _gameData;
    private readonly WorldGenerator _worldGenerator;
    private readonly Func<Zone,int,int,bool>? _canWalk;

    // Track what we've done in each zone
    private readonly HashSet<int> _visitedZones = new();
    private readonly HashSet<(int zoneId, int x, int y)> _talkedToNpcs = new();
    private readonly HashSet<(int zoneId, int x, int y)> _collectedItems = new();
    private readonly HashSet<(int zoneId, int x, int y)> _enteredDoors = new();

    // Track unreachable positions to avoid infinite loops
    private readonly HashSet<(int zoneId, int x, int y)> _unreachablePositions = new();
    private string? _reachabilityRevision;

    private void RefreshReachability()
    {
        if (_state.CurrentZone is not { } zone) return;
        string revision = $"{zone.Id}:{BlockPuzzlePlanner.Layout(zone)}:" +
            string.Join(';', _state.ZoneNPCs.Where(n => n.IsEnabled && n.IsAlive).Select(n => $"{n.CharacterId},{n.X},{n.Y}")) +
            ":" + string.Join(',', _state.Inventory.OrderBy(i => i));
        if (revision == _reachabilityRevision) return;
        _reachabilityRevision = revision;
        _unreachablePositions.RemoveWhere(p => p.zoneId == zone.Id);
        _reportedMissingSources.Clear();
    }

    // Track blocked zone exits (from current zone -> target zone) with retry counter
    private sealed record BlockedExit(string Layout, string Actors, bool Structural, HashSet<(int X,int Y)> Component);
    private string? _staticAreaRevision;
    private HashSet<(int X,int Y)> _staticArea = new();
    private HashSet<(int X,int Y)> StaticWalkingArea()
    {
        if (_state.CurrentZone is not { } zone) return new();
        string revision = $"{zone.Id}:{_state.SmoothMovementEnabled}:{BlockPuzzlePlanner.Layout(zone)}:" +
            $"{_state.GetCounter(zone.Id)}:{_state.GetSectorCounter(zone.Id)}:{_state.ZoneRandom.GetValueOrDefault(zone.Id)}:{_state.DisabledActions.Count}";
        if (revision != _staticAreaRevision || !_staticArea.Contains((_state.PlayerX,_state.PlayerY)))
        {
            _staticAreaRevision = revision;
            _staticArea = WorldActionGraph.WalkableArea(_gameData,zone,_state.PlayerX,_state.PlayerY,
                new GameState { CurrentZoneId = -1, SmoothMovementEnabled = _state.SmoothMovementEnabled,
                    Counters=_state.Counters,Variables=_state.Variables,ZoneRandom=_state.ZoneRandom,
                    VisitedZones=_state.VisitedZones,DisabledActions=_state.DisabledActions },_canWalk);
        }
        return _staticArea;
    }
    private string NavigationActors() => string.Join(';', _state.ZoneNPCs.Where(n => n.IsEnabled && n.IsAlive)
        .Select(n => $"{n.CharacterId},{n.X},{n.Y}"));
    private readonly Dictionary<(int fromZone, Direction dir), BlockedExit> _blockedExits = new();



    public MissionSolver(GameState state, GameData gameData, WorldGenerator worldGenerator,
        Func<Zone,int,int,bool>? canWalk=null)
    {
        _state = state;
        _gameData = gameData;
        _worldGenerator = worldGenerator;
        _canWalk=canWalk;
    }

    /// <summary>
    /// Reset tracking when starting a new game.
    /// </summary>
    // Persist factual interaction history and the in-flight mission intention,
    // while discarding paths, block searches, and failure caches on resume.
    public sealed class ReplayIntent
    {
        public List<(int Zone,int Item)> Consumed { get; set; } = new();
        public Dictionary<int,int> RoomReturns { get; set; } = new();
        public Dictionary<int,(int Zone,int X,int Y)> DoorReturnPoints { get; set; } = new();
        public (int Zone,int Item,int X,int Y)? OpenedPassage { get; set; }
        public int? PendingReward { get; set; }
        public int? ItemRouteFocus { get; set; }
        public int[] NeededItems { get; set; } = [];
        public (int Zone,int Action)? NativeFocus { get; set; }
        public (int Zone,int Action)? NativeContinuation { get; set; }
    }
    public ReplayIntent CaptureReplayIntent() => new()
    {
        Consumed=_consumedInteractions.ToList(), PendingReward=_pendingRewardItem,
        RoomReturns=new(_roomReturns), DoorReturnPoints=new(_doorReturnPoints), OpenedPassage=_openedPassage, ItemRouteFocus=_itemRouteFocus,
        NeededItems=_neededNativeItems.ToArray(), NativeFocus=_nativeGoalFocus,NativeContinuation=_nativeGoalContinuation
    };
    public void RestoreReplayIntent(ReplayIntent intent)
    {
        foreach(var pair in intent.RoomReturns) _roomReturns[pair.Key]=pair.Value;
        foreach(var pair in intent.DoorReturnPoints) _doorReturnPoints[pair.Key]=pair.Value;
        _openedPassage=intent.OpenedPassage;
        _consumedInteractions.UnionWith(intent.Consumed);
        _pendingRewardItem=intent.PendingReward;
        _itemRouteFocus=intent.ItemRouteFocus;
        if(_itemRouteFocus is int used && !_state.HasItem(used) && intent.Consumed.Any(p=>p.Item==used))
            _itemRouteFocus=null;
        _neededNativeItems.UnionWith(intent.NeededItems);
        _nativeGoalFocus=intent.NativeFocus;
        _nativeGoalContinuation=intent.NativeContinuation;
    }

    public void Reset()
    {
        _localPuzzlePlan=null;_localPuzzleStep=0;_localPuzzleFailures.Clear();
        _nativeGoalFocus = null;
        _nativeGoalContinuation = null;
        _nativeOpeningFocus = null;
        _blockWalkStart = null;
        _nativeArrangement = null;
        _consumedInteractions.Clear();
        _failedNativeArrangements.Clear();
        _nativeRewardRequests.Clear();
        _neededNativeItems.Clear();
        _routePreparations.Clear();
        _departedAreas.Clear(); _unchangedPortalCycles.Clear(); _unchangedZoneCycles.Clear();
        _openingRoute = false;
        _openedPassage = null;
        _staticAreaRevision = null;
        _staticArea.Clear();
        _blockPlan.Clear();
        _blockPlanZone = -1;
        _failedBlockMap = null;
        _failedCombatApproach = null;
        _pendingRewardItem = null;
        _itemRouteFocus=null; _itemRouteRevision=0;
        _reportedMissingSources.Clear();
        _roomReturns.Clear();
        _doorReturnPoints.Clear();
        ActionGraph = null;
        PlannedRoute = Array.Empty<WorldActionGraph.Edge>();
        _visitedZones.Clear();
        _talkedToNpcs.Clear();
        _collectedItems.Clear();
        _enteredDoors.Clear();
        _failedDoorStates.Clear();
        _failedScriptStates.Clear();
        _completedScriptStates.Clear();
        _failedScriptSemanticStates.Clear();
        _unchangedScriptAttempts.Clear();
        _failedPassageStates.Clear();
        _failedPassageSemanticStates.Clear();
        _unchangedPassageAttempts.Clear();
        _unreachablePositions.Clear();
        _reachabilityRevision = null;
        _blockedExits.Clear();
        _unreachableRouteTiles.Clear();
        _unreachableRouteActions.Clear();
    }

    /// <summary>
    /// Mark a zone exit as blocked for the current area layout (can't reach the edge).
    /// Will be retried after visiting several other zones.
    /// </summary>
    public void MarkExitBlocked(Direction dir)
    {
        var key = (_state.CurrentZoneId, dir);
        if (_state.CurrentZone is not { } zone) return;
        var component = StaticWalkingArea();
        bool structural = !component.Any(p => dir switch
        { Direction.Up => p.Y == 0, Direction.Down => p.Y == zone.Height-1,
            Direction.Left => p.X == 0, _ => p.X == zone.Width-1 });
        _blockedExits[key] = new(BlockPuzzlePlanner.Layout(zone), NavigationActors(), structural, new(component));
        Console.WriteLine($"[BOT] Marked exit {dir} from zone {_state.CurrentZoneId} as {(structural ? "terrain-blocked" : "temporarily obstructed")}");
    }

    /// <summary>
    /// Check whether this entrance/component has an unchanged blocked exit.
    /// </summary>
    public bool IsExitBlocked(Direction dir) => IsExitBlocked(_state.CurrentZoneId, dir);

    /// <summary>The same, for any zone - so route planning avoids exits already found blocked elsewhere.</summary>
    private bool IsExitBlocked(int zoneId, Direction dir)
    {
        var key = (zoneId, dir);
        if (!_blockedExits.TryGetValue(key, out var blocked)) return false;
        if (zoneId != _state.CurrentZoneId) return false;
        var zone = _gameData.Zones[zoneId];
        if (blocked.Layout != BlockPuzzlePlanner.Layout(zone) || !blocked.Structural && blocked.Actors != NavigationActors())
        { _blockedExits.Remove(key); return false; }
        // A different entrance can reach a different pocket in the same area.
        // Future-zone routes calculate their own component from their predicted landing.
        return zoneId == _state.CurrentZoneId && blocked.Component.Contains((_state.PlayerX,_state.PlayerY));
    }

    /// <summary>
    /// Called when entering a new zone. Clears position blocklists and tracks zone changes.
    /// </summary>
    public void OnZoneChanged()
    {
        _unreachablePositions.RemoveWhere(p => p.zoneId == _state.CurrentZoneId);
    }

    /// <summary>
    /// Mark current zone as visited.
    /// </summary>
    public void MarkZoneVisited()
    {
        _visitedZones.Add(_state.CurrentZoneId);
    }

    /// <summary>
    /// Mark a position as unreachable (for pathfinding failures).
    /// </summary>
    public void MarkUnreachable(int x, int y)
    {
        _unreachablePositions.Add((_state.CurrentZoneId, x, y));
    }

    /// <summary>
    /// Gets the current mission phase.
    /// </summary>
    public MissionPhase GetCurrentPhase()
    {
        var world = _worldGenerator.CurrentWorld;
        if (world == null)
            return MissionPhase.Unknown;

        bool onDagobah = world.DagobahZones.Contains(_state.CurrentZoneId);

        // Check if game is won
        if (_state.IsGameWon)
            return MissionPhase.Completed;

        if (_gameData.GameType == GameType.YodaStories && world.Mission?.IsCompleted == true)
            return onDagobah ? MissionPhase.TalkToYoda : MissionPhase.ReturnToDagobah;

        // Check if we have the starting item from Yoda
        if (world.StartingItemId.HasValue && !_state.HasItem(world.StartingItemId.Value))
        {
            // Need to talk to Yoda
            return onDagobah ? MissionPhase.TalkToYoda : MissionPhase.ReturnToDagobah;
        }

        // Have starting item - need to travel to planet
        if (onDagobah)
            return MissionPhase.TravelToPlanet;

        // On planet - explore and solve puzzles via IACT scripts
        return MissionPhase.SolvePuzzles;
    }

    /// <summary>Choose the next mission dependency, its route, or the briefing/travel action.</summary>
    public BotObjective GetCurrentObjective()
    {
        _planningDecision++;
        RefreshReachability();
        // The board hashes validate each queued move. Finish an existing local
        // solution before repeating the expensive whole-mission dependency search.
        if (_blockPlan.Count > 0 && PlannedBlockMove(new BotObjective(), continueOnly: true) is { } blockMove)
            return WithStuckCheck(blockMove);
        if (ContinueOpenedPassage() is { } passage) return passage;
        var phase = GetCurrentPhase();
        var world = _worldGenerator.CurrentWorld;

        // Native worlds use dependency planning once the briefing/travel phase is over.
        if (world is { Sectors.Count: > 0 } &&
            (_gameData.GameType != GameType.YodaStories || !world.DagobahZones.Contains(_state.CurrentZoneId)))
        {
            MarkZoneVisited();
            if(ContinueLocalPuzzle(world) is { } localStep) return localStep;
            if(TryLocalPuzzle(world) is { } localPlan) return localPlan;
            // Local replay defers live actors. Keep the same deepest item
            // dependency with the live planner instead of abandoning its supply
            // room and restarting the outer reward route.
            if(_itemRouteFocus is int fetching)
            {
                if(_state.HasItem(fetching)) _itemRouteFocus=null;
                else
                {
                    RefreshGraph(world);
                    if(FetchItem(world,fetching) is { } dependency && dependency.Type is not
                        (ObjectiveType.Explore or ObjectiveType.PlanningBlocked or ObjectiveType.UnresolvedReward))
                        return WithStuckCheck(dependency);
                }
            }
            // Native-world missions still need their authored return to Yoda.
            // Once the final artifact is acquired, stop planning optional
            // sector rewards and take the X-Wing back to Dagobah.
            if (_gameData.GameType == GameType.YodaStories && world.Mission?.IsCompleted == true)
                return WithStuckCheck(new BotObjective { Type=ObjectiveType.UseXWing,
                    Description="Return to Dagobah with the mission reward" });
            // An explicit briefing is a mission prerequisite even when the
            // final room already has a visible route in the world graph.
            if (_gameData.GameType == GameType.IndianaJones &&
                world.Mission is { CurrentStep: 0, IsCompleted: false, CurrentPuzzleStep.RequiredItemId: 0 } &&
                GetSectorObjective(world) is { } briefing)
                return WithStuckCheck(briefing);
            // Follow the authored mission chain in order. The finale graph can
            // expose several later items at once; selecting among those while
            // an earlier mission puzzle is active can bounce the bot between
            // routes without completing either branch.
            if (world.Mission is { IsCompleted: false, CurrentPuzzleStep: not null })
            {
                RefreshGraph(world);
                // The authored chain is authoritative even when its next step
                // is temporarily blocked. Returning its recovery objective lets
                // the bot explore a reachable route or report the blocker; falling
                // through to the finale graph can select later, unrelated rewards
                // and repeatedly route around the same inaccessible dependency.
                if (GetSectorObjective(world) is { } missionStep)
                    return WithStuckCheck(missionStep);
            }
            return WithStuckCheck(GetGraphObjective(world) ?? GetSectorObjective(world) ?? CreatePlanningBlockedObjective());
        }

        // Mark current zone as visited
        MarkZoneVisited();

        switch (phase)
        {
            case MissionPhase.TalkToYoda:
                return CreateTalkToYodaObjective(world);

            case MissionPhase.TravelToPlanet:
                // Check for R2D2 before leaving Dagobah
                Console.WriteLine($"[BOT] TravelToPlanet phase. HasLocator={_state.HasLocator}");
                if (!_state.HasLocator)
                {
                    Console.WriteLine("[BOT] Don't have locator, searching for R2D2 NPC...");

                    // First check if R2D2 NPC is in current zone
                    var r2d2Npc = FindR2D2Npc();
                    if (r2d2Npc != null)
                    {
                        Console.WriteLine($"[BOT] R2D2 NPC found at ({r2d2Npc.X},{r2d2Npc.Y}), IsReadyToCollect={r2d2Npc.IsReadyToCollect}");
                        if (r2d2Npc.IsReadyToCollect)
                        {
                            // Walk over R2D2 to collect it
                            return new BotObjective
                            {
                                Type = ObjectiveType.WalkToPosition,
                                Description = "Walk over R2D2 to collect it",
                                TargetX = r2d2Npc.X,
                                TargetY = r2d2Npc.Y
                            };
                        }
                        else
                        {
                            // Talk to R2D2 to make it ready to collect
                            return new BotObjective
                            {
                                Type = ObjectiveType.TalkToNpc,
                                Description = "Talk to R2D2",
                                TargetX = r2d2Npc.X,
                                TargetY = r2d2Npc.Y
                            };
                        }
                    }

                    // Search other Dagobah zones for R2D2
                    var (r2d2ZoneId, foundR2D2) = FindR2D2InDagobah();
                    if (foundR2D2 != null && r2d2ZoneId.HasValue)
                    {
                        if (r2d2ZoneId.Value != _state.CurrentZoneId)
                        {
                            Console.WriteLine($"[BOT] Must get R2D2 before leaving! R2D2 is in zone {r2d2ZoneId.Value}, we're in {_state.CurrentZoneId}");
                            return new BotObjective
                            {
                                Type = ObjectiveType.ChangeZone,
                                Description = $"Go get R2D2 first (zone {r2d2ZoneId.Value})",
                                TargetZoneId = r2d2ZoneId.Value
                            };
                        }
                    }
                    else
                    {
                        Console.WriteLine("[BOT] WARNING: Could not find R2D2 anywhere on Dagobah!");
                    }
                }
                Console.WriteLine("[BOT] Proceeding to use X-Wing");
                return new BotObjective
                {
                    Type = ObjectiveType.UseXWing,
                    Description = "Travel to mission planet via X-Wing"
                };

            case MissionPhase.SolvePuzzles:
                return CreatePlanningBlockedObjective();

            case MissionPhase.Completed:
                return new BotObjective
                {
                    Type = ObjectiveType.None,
                    Description = "Mission complete!"
                };

            default:
                return new BotObjective
                {
                    Type = ObjectiveType.Explore,
                    Description = "Explore the area"
                };
        }
    }

    private BotObjective CreateTalkToYodaObjective(WorldMap? world)
    {
        if (world == null)
            return new BotObjective { Type = ObjectiveType.Explore };

        // The briefing objective must stay focused on Yoda. Optional R2 collection
        // is handled by the departure phase after the mission has been received.
        // Find Yoda NPC
        var yoda = FindYodaNpc();
        if (yoda != null)
        {
            return new BotObjective
            {
                Type = ObjectiveType.TalkToNpc,
                Description = "Talk to Yoda",
                TargetNpc = yoda,
                TargetX = yoda.X,
                TargetY = yoda.Y
            };
        }

        // Yoda not in current zone - need to navigate
        if (world.YodaZoneId.HasValue && _state.CurrentZoneId != world.YodaZoneId.Value)
        {
            if (GraphTravel(world, world.YodaZoneId.Value) is { } route) return route;
            return new BotObjective
            {
                Type = ObjectiveType.ChangeZone,
                Description = "Go to Yoda's zone",
                TargetZoneId = world.YodaZoneId.Value
            };
        }

        return new BotObjective
        {
            Type = ObjectiveType.Explore,
            Description = "Find Yoda"
        };
    }

    private BotObjective CreatePlanningBlockedObjective() => new BotObjective
    {
        Type = ObjectiveType.PlanningBlocked,
        Description = "No executable mission instruction; saving planner state",
    };

    /// <summary>
    /// Finds Yoda NPC in current zone.
    /// </summary>
    public NPC? FindYodaNpc()
    {
        const int YODA_TILE_ID = 780;
        return _state.ZoneNPCs.FirstOrDefault(n =>
            n.CharacterId == YODA_TILE_ID && n.IsEnabled && n.IsAlive);
    }

    /// <summary>
    /// Finds the nearest hostile NPC.
    /// </summary>
    public NPC? FindNearestEnemy()
    {
        NPC? nearest = null;
        int nearestDist = int.MaxValue;
        var accessible = StaticWalkingArea();

        foreach (var npc in _state.ZoneNPCs)
        {
            if (!npc.IsEnabled || !npc.IsAlive || !npc.IsHostile || npc.IsInvulnerable)
                continue;

            // Skip NPCs at invalid positions (65535 = unset/placeholder)
            if (!IsValidPosition(npc.X, npc.Y))
                continue;

            // Combat gives up on an enemy it cannot reach by marking where it stands; honour
            // that, or the bot walks straight back into the same fight.
            if (_unreachablePositions.Contains((_state.CurrentZoneId, npc.X, npc.Y)))
                continue;

            if (!new[] { (npc.X,npc.Y-1), (npc.X,npc.Y+1), (npc.X-1,npc.Y), (npc.X+1,npc.Y) }.Any(accessible.Contains))
                continue; // Unrelated enemy movement cannot open a route through static walls.

            int dist = npc.DistanceTo(_state.PlayerX, _state.PlayerY);
            if (dist < nearestDist)
            {
                nearestDist = dist;
                nearest = npc;
            }
        }

        return nearest;
    }

    /// <summary>
    /// Checks if a position is valid within the current zone.
    /// </summary>
    private bool IsValidPosition(int x, int y)
    {
        if (_state.CurrentZone == null)
            return false;

        // 65535 (0xFFFF) is used as invalid/unset marker
        if (x == 65535 || y == 65535 || x == -1 || y == -1)
            return false;

        // Must be within zone bounds
        return x >= 0 && x < _state.CurrentZone.Width &&
               y >= 0 && y < _state.CurrentZone.Height;
    }

    /// <summary>
    /// Finds the nearest friendly NPC we haven't talked to yet.
    /// </summary>
    private NPC? FindUntalkedFriendlyNpc()
    {
        NPC? nearest = null;
        int nearestDist = int.MaxValue;

        foreach (var npc in _state.ZoneNPCs)
        {
            if (!npc.IsEnabled || !npc.IsAlive || npc.IsHostile)
                continue;

            // Skip NPCs at invalid positions
            if (!IsValidPosition(npc.X, npc.Y))
                continue;

            // Skip if we already talked to this NPC
            if (_talkedToNpcs.Contains((_state.CurrentZoneId, npc.X, npc.Y)))
                continue;

            // Skip unreachable positions
            if (_unreachablePositions.Contains((_state.CurrentZoneId, npc.X, npc.Y)))
                continue;

            if (!new[] { (npc.X,npc.Y-1),(npc.X,npc.Y+1),(npc.X-1,npc.Y),(npc.X+1,npc.Y) }.Any(StaticWalkingArea().Contains)) continue;

            int dist = npc.DistanceTo(_state.PlayerX, _state.PlayerY);
            if (dist < nearestDist)
            {
                nearestDist = dist;
                nearest = npc;
            }
        }

        return nearest;
    }

    /// <summary>
    /// Mark that we talked to an NPC.
    /// </summary>
    public void MarkTalkedTo(NPC npc)
    {
        _talkedToNpcs.Add((_state.CurrentZoneId, npc.X, npc.Y));
    }

    /// <summary>
    /// Checks if a character is R2D2 (by tile ID or character name).
    /// </summary>
    private bool IsR2D2Character(int characterId)
    {
        // Character 61 is "R2Unit" - the R2D2 character
        const int R2D2_CHARACTER_ID = 61;
        if (characterId == R2D2_CHARACTER_ID)
            return true;

        // Legacy: also check tile 512 (TILE_LOCATOR) for backwards compatibility
        if (characterId == WorldGenerator.TILE_LOCATOR)
            return true;

        // Also check by character name if it's a valid character
        if (characterId >= 0 && characterId < _gameData.Characters.Count)
        {
            var character = _gameData.Characters[characterId];
            var nameLower = character.Name.ToLowerInvariant();

            if (nameLower.Contains("r2") ||
                nameLower.Contains("r-2") ||
                nameLower.Contains("artoo") ||
                nameLower.Contains("locator"))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Finds the R2D2 NPC in the current zone.
    /// </summary>
    public NPC? FindR2D2Npc()
    {
        // Other planets reuse R2Unit art for ordinary NPCs. Companion collection
        // must not override native missions or chase a droid in another enclosure.
        if (_worldGenerator.CurrentWorld?.DagobahZones.Contains(_state.CurrentZoneId) != true) return null;
        var accessible = StaticWalkingArea();
        foreach (var npc in _state.ZoneNPCs)
        {
            if (npc.IsEnabled && npc.IsAlive && IsR2D2Character(npc.CharacterId) &&
                new[] { (npc.X,npc.Y-1),(npc.X,npc.Y+1),(npc.X-1,npc.Y),(npc.X+1,npc.Y) }.Any(accessible.Contains))
            {
                return npc;
            }
        }
        return null;
    }

    /// <summary>
    /// Finds R2D2 NPC across all Dagobah zones.
    /// Returns (zoneId, npc) or (null, null) if not found.
    /// </summary>
    private (int? zoneId, NPC? npc) FindR2D2InDagobah()
    {
        var world = _worldGenerator.CurrentWorld;
        if (world == null)
        {
            Console.WriteLine("[BOT] FindR2D2InDagobah: No world!");
            return (null, null);
        }

        Console.WriteLine($"[BOT] Searching for R2D2 NPC in {world.DagobahZones.Count} Dagobah zones: [{string.Join(", ", world.DagobahZones)}]");

        // First check current zone for R2D2 NPC
        var localR2D2 = FindR2D2Npc();
        if (localR2D2 != null)
        {
            Console.WriteLine($"[BOT] R2D2 NPC found in current zone {_state.CurrentZoneId} at ({localR2D2.X},{localR2D2.Y})");
            return (_state.CurrentZoneId, localR2D2);
        }

        // Search all Dagobah zones for R2D2 NPC (check zone objects for PuzzleNPC type)
        foreach (var dagobahZoneId in world.DagobahZones)
        {
            if (dagobahZoneId < 0 || dagobahZoneId >= _gameData.Zones.Count)
            {
                Console.WriteLine($"[BOT] Invalid zone ID {dagobahZoneId}");
                continue;
            }

            var zone = _gameData.Zones[dagobahZoneId];

            foreach (var obj in zone.Objects)
            {
                if (obj.Type == ZoneObjectType.PuzzleNPC)
                {
                    // Check if this NPC is R2D2
                    if (IsR2D2Character(obj.Argument))
                    {
                        Console.WriteLine($"[BOT] Found R2D2 NPC object in zone {dagobahZoneId} at ({obj.X},{obj.Y}), CharId={obj.Argument}");
                        // Create a temporary NPC object to return the position
                        var npc = new NPC
                        {
                            CharacterId = obj.Argument,
                            X = obj.X,
                            Y = obj.Y,
                            IsEnabled = true,
                            Health = 256 // IsAlive is computed from Health > 0
                        };
                        return (dagobahZoneId, npc);
                    }
                }
            }
        }

        Console.WriteLine("[BOT] No R2D2 NPC found in any Dagobah zone!");
        return (null, null);
    }

    /// <summary>
    /// Finds a tile item (like health kits, keys) that we haven't picked up yet.
    /// Checks both middle layer (1) and top layer (2) for items.
    /// </summary>
    public (int X, int Y)? FindTileItem()
    {
        if (_state.CurrentZone == null) return null;

        // Scan both middle and top layers for item tiles
        for (int layer = 1; layer <= 2; layer++)
        {
            for (int y = 0; y < _state.CurrentZone.Height; y++)
            {
                for (int x = 0; x < _state.CurrentZone.Width; x++)
                {
                    var tileId = _state.CurrentZone.GetTile(x, y, layer);
                    if (tileId == 0xFFFF || tileId >= _gameData.Tiles.Count)
                        continue;

                    var tile = _gameData.Tiles[tileId];
                    // Check if it's an item tile (including keys, keycards, etc.)
                    if (tile.IsItem || (tile.Flags & TileFlags.ItemKeycard) != 0)
                    {
                        // Skip if already collected
                        var key = $"{_state.CurrentZoneId}_{x}_{y}_tile";
                        if (_state.CollectedObjects.Contains(key))
                            continue;
                        if (_collectedItems.Contains((_state.CurrentZoneId, x, y)))
                            continue;
                        if (_unreachablePositions.Contains((_state.CurrentZoneId, x, y)))
                            continue;

                        if (!StaticWalkingArea().Contains((x,y))) continue;
                        return (x, y);
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Mark that we collected an item.
    /// </summary>
    public void MarkItemCollected(int x, int y)
    {
        _collectedItems.Add((_state.CurrentZoneId, x, y));
    }

    /// <summary>
    /// Finds a door we haven't entered yet.
    /// </summary>
    private ZoneObject? FindUnenteredDoor()
    {
        if (_state.CurrentZone == null) return null;

        foreach (var obj in _state.CurrentZone.Objects)
        {
            if (obj.Type == ZoneObjectType.DoorEntrance ||
                obj.Type == ZoneObjectType.DoorExit ||
                obj.Type == ZoneObjectType.Teleporter)
            {
                // Skip if already entered
                if (_enteredDoors.Contains((_state.CurrentZoneId, obj.X, obj.Y)))
                    continue;
                // Skip unreachable
                if (_unreachablePositions.Contains((_state.CurrentZoneId, obj.X, obj.Y)))
                    continue;
                // Skip if destination is visited (and we know the destination)
                if (obj.Argument < _gameData.Zones.Count && obj.Argument != 0xFFFF)
                {
                    if (_visitedZones.Contains(obj.Argument))
                        continue;
                }

                return obj;
            }
        }

        return null;
    }

    /// <summary>
    /// Mark that we entered a door.
    /// </summary>
    public void MarkDoorEntered(int x, int y)
    {
        _enteredDoors.Add((_state.CurrentZoneId, x, y));
    }

    /// <summary>
    /// Gets the direction to an adjacent zone.
    /// </summary>
    public Direction? GetDirectionToAdjacentZone(int targetZoneId)
    {
        var world = _worldGenerator.CurrentWorld;
        if (world == null) return null;

        if (!world.Connections.TryGetValue(_state.CurrentZoneId, out var connections))
            return null;

        if (connections.North == targetZoneId) return Direction.Up;
        if (connections.South == targetZoneId) return Direction.Down;
        if (connections.East == targetZoneId) return Direction.Right;
        if (connections.West == targetZoneId) return Direction.Left;

        return null;
    }

    /// <summary>
    /// Finds the direction to move to reach a non-adjacent target zone via BFS pathfinding.
    /// Returns the direction to the next zone on the path to the target.
    /// </summary>
    public Direction? GetDirectionTowardZone(int targetZoneId)
    {
        var world = _worldGenerator.CurrentWorld;
        if (world == null) return null;

        // First check if directly adjacent
        var direct = GetDirectionToAdjacentZone(targetZoneId);
        if (direct.HasValue) return direct;

        // BFS to find path from current zone to target zone
        var queue = new Queue<(int zoneId, Direction firstStep)>();
        var visited = new HashSet<int> { _state.CurrentZoneId };

        // Add initial adjacent zones with their directions
        if (world.Connections.TryGetValue(_state.CurrentZoneId, out var startConnections))
        {
            if (startConnections.North.HasValue && !IsExitBlocked(Direction.Up))
            {
                queue.Enqueue((startConnections.North.Value, Direction.Up));
                visited.Add(startConnections.North.Value);
            }
            if (startConnections.South.HasValue && !IsExitBlocked(Direction.Down))
            {
                queue.Enqueue((startConnections.South.Value, Direction.Down));
                visited.Add(startConnections.South.Value);
            }
            if (startConnections.East.HasValue && !IsExitBlocked(Direction.Right))
            {
                queue.Enqueue((startConnections.East.Value, Direction.Right));
                visited.Add(startConnections.East.Value);
            }
            if (startConnections.West.HasValue && !IsExitBlocked(Direction.Left))
            {
                queue.Enqueue((startConnections.West.Value, Direction.Left));
                visited.Add(startConnections.West.Value);
            }
        }

        while (queue.Count > 0)
        {
            var (currentZone, firstStep) = queue.Dequeue();

            // Found target!
            if (currentZone == targetZoneId)
            {
                Console.WriteLine($"[BOT] Path to zone {targetZoneId}: go {firstStep} first");
                return firstStep;
            }

            // Expand neighbors, skipping exits already found blocked from that zone
            if (world.Connections.TryGetValue(currentZone, out var connections))
            {
                foreach (var (neighbor, dir) in new[] { (connections.North, Direction.Up), (connections.South, Direction.Down),
                                                        (connections.East, Direction.Right), (connections.West, Direction.Left) })
                {
                    if (neighbor.HasValue && !visited.Contains(neighbor.Value) && !IsExitBlocked(currentZone, dir))
                    {
                        visited.Add(neighbor.Value);
                        queue.Enqueue((neighbor.Value, firstStep)); // Keep tracking the first step
                    }
                }
            }
        }

        Console.WriteLine($"[BOT] No path found to zone {targetZoneId}");
        return null;
    }

    /// <summary>
    /// Gets a list of unexplored connected zones (excluding blocked exits).
    /// </summary>
    public List<int> GetUnexploredConnectedZones()
    {
        var result = new List<int>();
        var world = _worldGenerator.CurrentWorld;
        if (world == null) return result;

        if (!world.Connections.TryGetValue(_state.CurrentZoneId, out var connections))
            return result;

        if (connections.North.HasValue && !_visitedZones.Contains(connections.North.Value) && !IsExitBlocked(Direction.Up))
            result.Add(connections.North.Value);
        if (connections.South.HasValue && !_visitedZones.Contains(connections.South.Value) && !IsExitBlocked(Direction.Down))
            result.Add(connections.South.Value);
        if (connections.East.HasValue && !_visitedZones.Contains(connections.East.Value) && !IsExitBlocked(Direction.Right))
            result.Add(connections.East.Value);
        if (connections.West.HasValue && !_visitedZones.Contains(connections.West.Value) && !IsExitBlocked(Direction.Left))
            result.Add(connections.West.Value);

        return result;
    }

    /// <summary>
    /// Finds a door to an unexplored zone (used by MissionBot exploration).
    /// </summary>
    public ZoneObject? FindUnexploredDoor()
    {
        return FindUnenteredDoor();
    }

    /// <summary>
    /// Finds the nearest friendly NPC (used by MissionBot exploration).
    /// </summary>
    public NPC? FindNearestFriendlyNpc()
    {
        return FindUntalkedFriendlyNpc();
    }

    /// <summary>
    /// Gets the NPC's name for display.
    /// </summary>
    private string GetNpcName(NPC npc)
    {
        if (npc.CharacterId < _gameData.Characters.Count)
        {
            var name = _gameData.Characters[npc.CharacterId].Name;
            if (!string.IsNullOrEmpty(name))
                return name;
        }
        return "NPC";
    }

    /// <summary>
    /// Checks if the mission is complete.
    /// </summary>
    public bool IsMissionComplete()
    {
        return _state.IsGameWon || GetCurrentPhase() == MissionPhase.Completed;
    }
}

/// <summary>
/// Mission phases for the bot.
/// </summary>
public enum MissionPhase
{
    Unknown,
    TalkToYoda,
    TravelToPlanet,
    SolvePuzzles,
    ReturnToDagobah,
    ReturnToYoda,
    Completed
}

/// <summary>
/// Types of objectives the bot can pursue.
/// </summary>
public enum ObjectiveType
{
    None,
    TalkToNpc,
    UseItemOnNpc,
    PickupItem,
    KillEnemy,
    ChangeZone,
    UseXWing,
    EnterDoor,
    PushObject,
    PullObject,
    Explore,
    FindNpc,
    BumpTarget,
    AttackTile,
    UnresolvedReward,
    PlanningBlocked,
    TraversePassage,
    WalkToPosition, // Walk to a specific position (e.g., to collect R2D2 after talking)
    ClearObject,   // Planning goal: uncover a scripted tile and reach a cardinal approach.
    WaitForActor,  // Retain the object plan while a friendly actor vacates a required cell.
    GuideActor    // Reach a guiding stance and let a native fleeing actor take a real step.
}

/// <summary>
/// Represents a bot objective with details.
/// </summary>
public class BotObjective
{
    public ObjectiveType Type { get; set; }
    public string Description { get; set; } = "";
    public int TargetX { get; set; }
    public int TargetY { get; set; }
    public int? TargetZoneId { get; set; }
    public NPC? TargetNpc { get; set; }
    public int? RequiredItemId { get; set; }
    public int? ExpectedRewardItemId { get; set; }
    public Direction? Direction { get; set; }
    public int? CrossingX { get; set; }
    public int? CrossingY { get; set; }
    public int? ArrivalX { get; set; }
    public int? ArrivalY { get; set; }
    public bool PassageRequiresBump { get; set; }
    public bool ScriptedZoneChange { get; set; }
    public bool AvoidFloorSwitches { get; set; }
    public bool ScriptedInteraction { get; set; }
    public int? ApproachX { get; set; }
    public int? ApproachY { get; set; }
    public int? ScriptIndex { get; set; }
}
