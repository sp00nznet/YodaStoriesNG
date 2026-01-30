using YodaStoriesNG.Engine.Data;
using YodaStoriesNG.Engine.Game;

namespace YodaStoriesNG.Engine.Bot;

/// <summary>
/// Explorer-based mission solver. Instead of following a fake puzzle chain,
/// systematically explores zones and interacts with everything, letting
/// the game's IACT scripts drive actual progression.
/// </summary>
public class MissionSolver
{
    private readonly GameState _state;
    private readonly GameData _gameData;
    private readonly WorldGenerator _worldGenerator;

    // Track what we've done in each zone
    private readonly HashSet<int> _visitedZones = new();
    private readonly HashSet<(int zoneId, int x, int y)> _talkedToNpcs = new();
    private readonly HashSet<(int zoneId, int x, int y)> _usedItemsOnNpcs = new();
    private readonly HashSet<(int zoneId, int x, int y)> _collectedItems = new();
    private readonly HashSet<(int zoneId, int x, int y)> _enteredDoors = new();

    // Track unreachable positions to avoid infinite loops
    private readonly HashSet<(int zoneId, int x, int y)> _unreachablePositions = new();

    // Track blocked zone exits (from current zone -> target zone)
    private readonly HashSet<(int fromZone, Direction dir)> _blockedExits = new();

    // Current exploration target
    private int? _targetZoneId;

    public MissionSolver(GameState state, GameData gameData, WorldGenerator worldGenerator)
    {
        _state = state;
        _gameData = gameData;
        _worldGenerator = worldGenerator;
    }

    /// <summary>
    /// Reset tracking when starting a new game.
    /// </summary>
    public void Reset()
    {
        _visitedZones.Clear();
        _talkedToNpcs.Clear();
        _usedItemsOnNpcs.Clear();
        _collectedItems.Clear();
        _enteredDoors.Clear();
        _unreachablePositions.Clear();
        _blockedExits.Clear();
        _targetZoneId = null;
    }

    /// <summary>
    /// Mark a zone exit as blocked (can't reach the edge).
    /// </summary>
    public void MarkExitBlocked(Direction dir)
    {
        _blockedExits.Add((_state.CurrentZoneId, dir));
        Console.WriteLine($"[BOT] Marked exit {dir} from zone {_state.CurrentZoneId} as blocked");
    }

    /// <summary>
    /// Check if a zone exit is blocked.
    /// </summary>
    public bool IsExitBlocked(Direction dir)
    {
        return _blockedExits.Contains((_state.CurrentZoneId, dir));
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
    /// Clear unreachable positions when zone changes.
    /// </summary>
    public void OnZoneChanged()
    {
        // Clear unreachable markers for old zone - they might be reachable now
        _unreachablePositions.RemoveWhere(p => p.zoneId != _state.CurrentZoneId);
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

    /// <summary>
    /// Gets the current objective based on exploration state.
    /// Priority order:
    /// 1. Kill nearby enemies (safety)
    /// 2. Pick up items in current zone
    /// 3. Talk to friendly NPCs we haven't talked to
    /// 4. Try using inventory items on NPCs
    /// 5. Enter unexplored doors
    /// 6. Move to unexplored connected zones
    /// 7. If all explored, return to Dagobah
    /// </summary>
    public BotObjective GetCurrentObjective()
    {
        var phase = GetCurrentPhase();
        var world = _worldGenerator.CurrentWorld;

        // Mark current zone as visited
        MarkZoneVisited();

        switch (phase)
        {
            case MissionPhase.TalkToYoda:
                return CreateTalkToYodaObjective(world);

            case MissionPhase.TravelToPlanet:
                return new BotObjective
                {
                    Type = ObjectiveType.UseXWing,
                    Description = "Travel to mission planet via X-Wing"
                };

            case MissionPhase.SolvePuzzles:
                return CreateExplorationObjective();

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

    /// <summary>
    /// Creates exploration-based objective. Systematically explores and interacts
    /// with everything in the zone before moving on.
    /// </summary>
    private BotObjective CreateExplorationObjective()
    {
        // Priority 1: Kill nearby enemies (safety first!)
        var enemy = FindNearestEnemy();
        if (enemy != null)
        {
            return new BotObjective
            {
                Type = ObjectiveType.KillEnemy,
                Description = $"Defeat {GetNpcName(enemy)}",
                TargetNpc = enemy,
                TargetX = enemy.X,
                TargetY = enemy.Y
            };
        }

        // Priority 2: Pick up any zone object items in the zone
        var item = FindUnpickedItem();
        if (item != null)
        {
            return new BotObjective
            {
                Type = ObjectiveType.PickupItem,
                Description = $"Pick up item at ({item.X},{item.Y})",
                TargetX = item.X,
                TargetY = item.Y
            };
        }

        // Priority 2b: Pick up any tile items (like health kits, weapons on ground)
        var tileItem = FindTileItem();
        if (tileItem != null)
        {
            return new BotObjective
            {
                Type = ObjectiveType.PickupItem,
                Description = $"Pick up tile item at ({tileItem.Value.X},{tileItem.Value.Y})",
                TargetX = tileItem.Value.X,
                TargetY = tileItem.Value.Y
            };
        }

        // Priority 3: Talk to friendly NPCs we haven't talked to yet
        var untalkedNpc = FindUntalkedFriendlyNpc();
        if (untalkedNpc != null)
        {
            return new BotObjective
            {
                Type = ObjectiveType.TalkToNpc,
                Description = $"Talk to {GetNpcName(untalkedNpc)}",
                TargetNpc = untalkedNpc,
                TargetX = untalkedNpc.X,
                TargetY = untalkedNpc.Y
            };
        }

        // Priority 4: Try using inventory items on friendly NPCs
        var (npcForItem, itemToUse) = FindNpcToUseItemOn();
        if (npcForItem != null && itemToUse.HasValue)
        {
            return new BotObjective
            {
                Type = ObjectiveType.UseItemOnNpc,
                Description = $"Use item on {GetNpcName(npcForItem)}",
                TargetNpc = npcForItem,
                TargetX = npcForItem.X,
                TargetY = npcForItem.Y,
                RequiredItemId = itemToUse.Value
            };
        }

        // Priority 5: Enter unexplored doors in current zone
        var door = FindUnenteredDoor();
        if (door != null)
        {
            return new BotObjective
            {
                Type = ObjectiveType.EnterDoor,
                Description = $"Enter door at ({door.X},{door.Y})",
                TargetX = door.X,
                TargetY = door.Y,
                TargetZoneId = door.Argument != 0xFFFF ? door.Argument : null
            };
        }

        // Priority 6: Move to unexplored connected zones
        var unexploredZone = FindUnexploredConnectedZone();
        if (unexploredZone.HasValue)
        {
            var dir = GetDirectionToAdjacentZone(unexploredZone.Value);
            if (dir.HasValue)
            {
                return new BotObjective
                {
                    Type = ObjectiveType.ChangeZone,
                    Description = $"Explore zone {unexploredZone.Value}",
                    TargetZoneId = unexploredZone.Value,
                    Direction = dir.Value
                };
            }
        }

        // Priority 7: All explored - check if we should return to Dagobah
        // (This might mean we've completed everything or are stuck)
        if (_state.IsGameWon)
        {
            return new BotObjective
            {
                Type = ObjectiveType.UseXWing,
                Description = "Return to Dagobah - Mission Complete!"
            };
        }

        // Still exploring - try to find any unexplored zone in the world
        var anyUnexplored = FindAnyUnexploredZone();
        if (anyUnexplored.HasValue)
        {
            // Navigate toward it
            _targetZoneId = anyUnexplored.Value;
            return new BotObjective
            {
                Type = ObjectiveType.Explore,
                Description = $"Navigate toward zone {anyUnexplored.Value}"
            };
        }

        // Truly stuck or complete - try returning to Dagobah
        return new BotObjective
        {
            Type = ObjectiveType.UseXWing,
            Description = "Return to Dagobah"
        };
    }

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

        foreach (var npc in _state.ZoneNPCs)
        {
            if (!npc.IsEnabled || !npc.IsAlive || !npc.IsHostile)
                continue;

            // Skip NPCs at invalid positions (65535 = unset/placeholder)
            if (!IsValidPosition(npc.X, npc.Y))
                continue;

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
    /// Finds an NPC we haven't tried using items on, and an item to use.
    /// </summary>
    private (NPC? npc, int? itemId) FindNpcToUseItemOn()
    {
        if (_state.Inventory.Count == 0)
            return (null, null);

        foreach (var npc in _state.ZoneNPCs)
        {
            if (!npc.IsEnabled || !npc.IsAlive || npc.IsHostile)
                continue;

            // Skip NPCs at invalid positions
            if (!IsValidPosition(npc.X, npc.Y))
                continue;

            // Skip unreachable positions
            if (_unreachablePositions.Contains((_state.CurrentZoneId, npc.X, npc.Y)))
                continue;

            // Find an item we haven't tried on this NPC
            foreach (var itemId in _state.Inventory)
            {
                var key = (_state.CurrentZoneId, npc.X, npc.Y);
                // We use a compound key including item ID
                if (!_usedItemsOnNpcs.Contains((key.CurrentZoneId, key.X * 10000 + itemId, key.Y)))
                {
                    return (npc, itemId);
                }
            }
        }

        return (null, null);
    }

    /// <summary>
    /// Mark that we talked to an NPC.
    /// </summary>
    public void MarkTalkedTo(NPC npc)
    {
        _talkedToNpcs.Add((_state.CurrentZoneId, npc.X, npc.Y));
    }

    /// <summary>
    /// Mark that we used an item on an NPC.
    /// </summary>
    public void MarkUsedItemOn(NPC npc, int itemId)
    {
        _usedItemsOnNpcs.Add((_state.CurrentZoneId, npc.X * 10000 + itemId, npc.Y));
    }

    /// <summary>
    /// Finds an item we haven't picked up yet (either zone objects or tile items).
    /// </summary>
    private ZoneObject? FindUnpickedItem()
    {
        if (_state.CurrentZone == null) return null;

        // Check zone objects for items
        foreach (var obj in _state.CurrentZone.Objects)
        {
            if (obj.Type == ZoneObjectType.CrateItem ||
                obj.Type == ZoneObjectType.CrateWeapon ||
                obj.Type == ZoneObjectType.LocatorItem)
            {
                // Skip if already collected
                if (_collectedItems.Contains((_state.CurrentZoneId, obj.X, obj.Y)))
                    continue;
                if (_state.IsObjectCollected(_state.CurrentZoneId, obj.X, obj.Y))
                    continue;
                // Skip unreachable
                if (_unreachablePositions.Contains((_state.CurrentZoneId, obj.X, obj.Y)))
                    continue;

                return obj;
            }
        }

        return null;
    }

    /// <summary>
    /// Finds a tile item (like health kits) that we haven't picked up yet.
    /// These are tiles placed directly in the zone's middle layer with IsItem flag.
    /// </summary>
    public (int X, int Y)? FindTileItem()
    {
        if (_state.CurrentZone == null) return null;

        // Scan the middle layer for item tiles
        for (int y = 0; y < _state.CurrentZone.Height; y++)
        {
            for (int x = 0; x < _state.CurrentZone.Width; x++)
            {
                var tileId = _state.CurrentZone.GetTile(x, y, 1); // Middle layer
                if (tileId == 0xFFFF || tileId >= _gameData.Tiles.Count)
                    continue;

                var tile = _gameData.Tiles[tileId];
                if (tile.IsItem && !tile.IsObject) // Item tile, not blocking object
                {
                    // Skip if already collected
                    var key = $"{_state.CurrentZoneId}_{x}_{y}_tile";
                    if (_state.CollectedObjects.Contains(key))
                        continue;
                    if (_collectedItems.Contains((_state.CurrentZoneId, x, y)))
                        continue;
                    if (_unreachablePositions.Contains((_state.CurrentZoneId, x, y)))
                        continue;

                    return (x, y);
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
    /// Finds an unexplored zone connected to current zone.
    /// </summary>
    private int? FindUnexploredConnectedZone()
    {
        var world = _worldGenerator.CurrentWorld;
        if (world == null) return null;

        if (!world.Connections.TryGetValue(_state.CurrentZoneId, out var connections))
            return null;

        // Check each direction for unexplored zones, skipping blocked exits
        (int? zoneId, Direction dir)[] candidates = {
            (connections.North, Direction.Up),
            (connections.South, Direction.Down),
            (connections.East, Direction.Right),
            (connections.West, Direction.Left)
        };

        foreach (var (zoneId, dir) in candidates)
        {
            if (zoneId.HasValue && !_visitedZones.Contains(zoneId.Value) && !IsExitBlocked(dir))
            {
                return zoneId.Value;
            }
        }

        return null;
    }

    /// <summary>
    /// Finds any unexplored zone in the world (for when we need to navigate further).
    /// </summary>
    private int? FindAnyUnexploredZone()
    {
        var world = _worldGenerator.CurrentWorld;
        if (world?.Grid == null) return null;

        // Scan the grid for unexplored zones
        for (int y = 0; y < WorldGenerator.GridSize; y++)
        {
            for (int x = 0; x < WorldGenerator.GridSize; x++)
            {
                var zoneId = world.Grid[y, x];
                if (zoneId.HasValue && !_visitedZones.Contains(zoneId.Value))
                {
                    return zoneId.Value;
                }
            }
        }

        return null;
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
    Explore,
    FindNpc
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
    public Direction? Direction { get; set; }
}
