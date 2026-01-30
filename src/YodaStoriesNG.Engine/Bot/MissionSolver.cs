using YodaStoriesNG.Engine.Data;
using YodaStoriesNG.Engine.Game;

namespace YodaStoriesNG.Engine.Bot;

/// <summary>
/// Mission-specific logic and puzzle chain tracking.
/// Determines what the bot needs to do to complete the current mission.
/// </summary>
public class MissionSolver
{
    private readonly GameState _state;
    private readonly GameData _gameData;
    private readonly WorldGenerator _worldGenerator;

    public MissionSolver(GameState state, GameData gameData, WorldGenerator worldGenerator)
    {
        _state = state;
        _gameData = gameData;
        _worldGenerator = worldGenerator;
    }

    /// <summary>
    /// Gets the current mission phase.
    /// </summary>
    public MissionPhase GetCurrentPhase()
    {
        var world = _worldGenerator.CurrentWorld;
        if (world == null)
            return MissionPhase.Unknown;

        var mission = world.Mission;
        bool onDagobah = world.DagobahZones.Contains(_state.CurrentZoneId);

        // Check if game is won
        if (_state.IsGameWon)
            return MissionPhase.Completed;

        // Check if mission is complete and we need to return
        if (mission?.IsCompleted == true)
        {
            return onDagobah ? MissionPhase.ReturnToYoda : MissionPhase.ReturnToDagobah;
        }

        // Check if we have the starting item from Yoda
        if (world.StartingItemId.HasValue && !_state.HasItem(world.StartingItemId.Value))
        {
            // Need to talk to Yoda
            return onDagobah ? MissionPhase.TalkToYoda : MissionPhase.ReturnToDagobah;
        }

        // Have starting item - need to travel to planet
        if (onDagobah)
            return MissionPhase.TravelToPlanet;

        // On planet - solve puzzles
        return MissionPhase.SolvePuzzles;
    }

    /// <summary>
    /// Gets the current objective based on mission state.
    /// </summary>
    public BotObjective GetCurrentObjective()
    {
        var phase = GetCurrentPhase();
        var world = _worldGenerator.CurrentWorld;

        // First priority: If we're in a room (small indoor zone), find the exit door
        var doorExit = CheckForRoomExit();
        if (doorExit != null)
        {
            return doorExit;
        }

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
                return CreatePuzzleObjective(world);

            case MissionPhase.ReturnToDagobah:
                return new BotObjective
                {
                    Type = ObjectiveType.UseXWing,
                    Description = "Return to Dagobah via X-Wing"
                };

            case MissionPhase.ReturnToYoda:
                return CreateTalkToYodaObjective(world);

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
            return new BotObjective { Type = ObjectiveType.None };

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

    private BotObjective CreatePuzzleObjective(WorldMap? world)
    {
        if (world?.Mission == null)
            return new BotObjective { Type = ObjectiveType.Explore };

        var mission = world.Mission;
        var currentStep = mission.CurrentPuzzleStep;

        if (currentStep == null)
        {
            // No more steps - mission complete
            return new BotObjective
            {
                Type = ObjectiveType.UseXWing,
                Description = "Return to Dagobah"
            };
        }

        // Check if we need to change zones to reach the target
        if (currentStep.ZoneId.HasValue && currentStep.ZoneId.Value != _state.CurrentZoneId)
        {
            // Need to navigate to target zone
            var dir = GetDirectionToAdjacentZone(currentStep.ZoneId.Value);
            if (dir.HasValue)
            {
                return new BotObjective
                {
                    Type = ObjectiveType.ChangeZone,
                    Description = $"Go to zone {currentStep.ZoneId.Value}",
                    TargetZoneId = currentStep.ZoneId.Value,
                    Direction = dir.Value
                };
            }
            else
            {
                // Target zone not adjacent - explore toward it
                return new BotObjective
                {
                    Type = ObjectiveType.Explore,
                    Description = $"Navigate toward zone {currentStep.ZoneId.Value}"
                };
            }
        }

        // We're in the target zone (or no specific zone required)

        // Check if we have the required item for this step
        if (currentStep.RequiredItemId > 0)
        {
            if (_state.HasItem(currentStep.RequiredItemId))
            {
                // Have the item - find NPC to give it to
                return CreateFindNpcToTradeObjective(currentStep);
            }
            else
            {
                // Need to find the item - check at specific location first
                if (currentStep.TargetX > 0 || currentStep.TargetY > 0)
                {
                    return new BotObjective
                    {
                        Type = ObjectiveType.PickupItem,
                        Description = $"Pick up item at ({currentStep.TargetX},{currentStep.TargetY})",
                        TargetX = currentStep.TargetX,
                        TargetY = currentStep.TargetY,
                        RequiredItemId = currentStep.RequiredItemId
                    };
                }
                return CreateFindItemObjective(currentStep.RequiredItemId);
            }
        }

        // No specific required item - go to target location if specified
        if (currentStep.TargetX > 0 || currentStep.TargetY > 0)
        {
            // Check for items at target location
            var itemAtTarget = FindAnyItemInCurrentZone();
            if (itemAtTarget != null)
            {
                return new BotObjective
                {
                    Type = ObjectiveType.PickupItem,
                    Description = $"Pick up item at ({itemAtTarget.X},{itemAtTarget.Y})",
                    TargetX = itemAtTarget.X,
                    TargetY = itemAtTarget.Y
                };
            }
        }

        // Priority 1: If we have ANY items, try to use them on friendly NPCs
        if (_state.Inventory.Count > 0)
        {
            var friendlyNpc = FindNearestFriendlyNpc();
            if (friendlyNpc != null)
            {
                var itemToUse = _state.Inventory.FirstOrDefault();
                if (itemToUse > 0)
                {
                    return new BotObjective
                    {
                        Type = ObjectiveType.UseItemOnNpc,
                        Description = $"Try using item on NPC",
                        TargetNpc = friendlyNpc,
                        TargetX = friendlyNpc.X,
                        TargetY = friendlyNpc.Y,
                        RequiredItemId = itemToUse
                    };
                }
            }
        }

        // Priority 2: Talk to any friendly NPC (might get items or advance quest)
        var talkableNpc = FindNearestFriendlyNpc();
        if (talkableNpc != null)
        {
            return new BotObjective
            {
                Type = ObjectiveType.TalkToNpc,
                Description = "Talk to NPC",
                TargetNpc = talkableNpc,
                TargetX = talkableNpc.X,
                TargetY = talkableNpc.Y
            };
        }

        // Priority 3: Pick up any items in the zone
        var anyItem = FindAnyItemInCurrentZone();
        if (anyItem != null)
        {
            return new BotObjective
            {
                Type = ObjectiveType.PickupItem,
                Description = $"Pick up item at ({anyItem.X},{anyItem.Y})",
                TargetX = anyItem.X,
                TargetY = anyItem.Y
            };
        }

        // Priority 4: Kill any enemies blocking progress
        var enemy = FindNearestEnemy();
        if (enemy != null)
        {
            return new BotObjective
            {
                Type = ObjectiveType.KillEnemy,
                Description = "Defeat enemy",
                TargetNpc = enemy,
                TargetX = enemy.X,
                TargetY = enemy.Y
            };
        }

        // Priority 5: Explore to find more stuff
        return new BotObjective
        {
            Type = ObjectiveType.Explore,
            Description = "Explore to find objectives"
        };
    }

    private BotObjective CreateFindNpcToTradeObjective(PuzzleStep step)
    {
        // Look for an NPC that might accept this item
        // First, check current zone for friendly NPCs
        var friendlyNpc = _state.ZoneNPCs.FirstOrDefault(n =>
            n.IsEnabled && n.IsAlive && !n.IsHostile);

        if (friendlyNpc != null)
        {
            return new BotObjective
            {
                Type = ObjectiveType.UseItemOnNpc,
                Description = $"Use item on NPC",
                TargetNpc = friendlyNpc,
                TargetX = friendlyNpc.X,
                TargetY = friendlyNpc.Y,
                RequiredItemId = step.RequiredItemId
            };
        }

        // No NPC in current zone - explore to find one
        return new BotObjective
        {
            Type = ObjectiveType.FindNpc,
            Description = "Find someone to trade with"
        };
    }

    private BotObjective CreateFindItemObjective(int itemId)
    {
        // First, check current zone for the item
        var itemObj = FindItemInCurrentZone(itemId);
        if (itemObj != null)
        {
            return new BotObjective
            {
                Type = ObjectiveType.PickupItem,
                Description = $"Pick up item at ({itemObj.X},{itemObj.Y})",
                TargetX = itemObj.X,
                TargetY = itemObj.Y,
                RequiredItemId = itemId
            };
        }

        // Check for any collectable items in current zone
        var anyItem = FindAnyItemInCurrentZone();
        if (anyItem != null)
        {
            return new BotObjective
            {
                Type = ObjectiveType.PickupItem,
                Description = $"Pick up item at ({anyItem.X},{anyItem.Y})",
                TargetX = anyItem.X,
                TargetY = anyItem.Y
            };
        }

        // Check if any NPC might give us items
        var npcWithItem = _state.ZoneNPCs.FirstOrDefault(n =>
            n.IsEnabled && n.IsAlive && !n.IsHostile &&
            n.CarriedItemId.HasValue && !n.HasGivenItem);

        if (npcWithItem != null)
        {
            return new BotObjective
            {
                Type = ObjectiveType.TalkToNpc,
                Description = "Talk to NPC for item",
                TargetNpc = npcWithItem,
                TargetX = npcWithItem.X,
                TargetY = npcWithItem.Y
            };
        }

        // No items in current zone - explore
        return new BotObjective
        {
            Type = ObjectiveType.Explore,
            Description = "Explore to find items"
        };
    }

    /// <summary>
    /// Checks if we're in a small room zone that needs an exit door.
    /// Rooms are typically 9x9 zones with DoorExit objects.
    /// </summary>
    private BotObjective? CheckForRoomExit()
    {
        var zone = _state.CurrentZone;
        if (zone == null) return null;

        // Rooms are typically 9x9 (or smaller than 18x18 outdoor zones)
        bool isRoom = zone.Width < 18 || zone.Height < 18;
        if (!isRoom) return null;

        // Find exit door in this room
        var exitDoor = zone.Objects.FirstOrDefault(o =>
            o.Type == ZoneObjectType.DoorExit ||
            o.Type == ZoneObjectType.Teleporter);

        if (exitDoor != null)
        {
            Console.WriteLine($"[BOT] In room zone {_state.CurrentZoneId} ({zone.Width}x{zone.Height}), found exit at ({exitDoor.X},{exitDoor.Y})");
            return new BotObjective
            {
                Type = ObjectiveType.EnterDoor,
                Description = $"Exit room via door at ({exitDoor.X},{exitDoor.Y})",
                TargetX = exitDoor.X,
                TargetY = exitDoor.Y,
                TargetZoneId = exitDoor.Argument != 0xFFFF ? exitDoor.Argument : null
            };
        }

        // No exit door found in room - this shouldn't happen
        Console.WriteLine($"[BOT] WARNING: In room zone {_state.CurrentZoneId} but no exit door found!");
        return null;
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
    /// Finds a specific item in the current zone.
    /// </summary>
    private ZoneObject? FindItemInCurrentZone(int itemId)
    {
        if (_state.CurrentZone == null) return null;

        foreach (var obj in _state.CurrentZone.Objects)
        {
            if ((obj.Type == ZoneObjectType.CrateItem || obj.Type == ZoneObjectType.CrateWeapon) &&
                obj.Argument == itemId &&
                !_state.IsObjectCollected(_state.CurrentZoneId, obj.X, obj.Y))
            {
                return obj;
            }
        }
        return null;
    }

    /// <summary>
    /// Finds any collectable item in the current zone.
    /// Checks both zone objects (crates) and items placed in the tile grid.
    /// </summary>
    private ZoneObject? FindAnyItemInCurrentZone()
    {
        if (_state.CurrentZone == null) return null;

        // First check zone objects (crates, locators)
        foreach (var obj in _state.CurrentZone.Objects)
        {
            if ((obj.Type == ZoneObjectType.CrateItem ||
                 obj.Type == ZoneObjectType.CrateWeapon ||
                 obj.Type == ZoneObjectType.LocatorItem) &&
                !_state.IsObjectCollected(_state.CurrentZoneId, obj.X, obj.Y))
            {
                return obj;
            }
        }

        // Also scan the tile grid for items (e.g., mushrooms on rocks)
        var zone = _state.CurrentZone;
        if (zone.TileGrid != null)
        {
            for (int y = 0; y < zone.Height; y++)
            {
                for (int x = 0; x < zone.Width; x++)
                {
                    // Check all layers for item tiles
                    for (int layer = 0; layer < 3; layer++)
                    {
                        var tileId = zone.TileGrid[y, x, layer];
                        if (tileId != 0xFFFF && tileId < _gameData.Tiles.Count)
                        {
                            var tile = _gameData.Tiles[tileId];
                            if (tile.IsItem && !_state.IsObjectCollected(_state.CurrentZoneId, x, y))
                            {
                                // Found an item tile - return as a synthetic object
                                return new ZoneObject
                                {
                                    Type = ZoneObjectType.LocatorItem,
                                    X = x,
                                    Y = y,
                                    Argument = tileId
                                };
                            }
                        }
                    }
                }
            }
        }

        return null;
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
    /// Finds the nearest friendly NPC.
    /// </summary>
    public NPC? FindNearestFriendlyNpc()
    {
        NPC? nearest = null;
        int nearestDist = int.MaxValue;

        foreach (var npc in _state.ZoneNPCs)
        {
            if (!npc.IsEnabled || !npc.IsAlive || npc.IsHostile)
                continue;

            // Additional check: skip NPCs that look hostile by name
            if (IsLikelyHostileByName(npc.CharacterId))
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
    /// Checks if a character is likely hostile based on their name.
    /// </summary>
    private bool IsLikelyHostileByName(int characterId)
    {
        if (characterId < 0 || characterId >= _gameData.Characters.Count)
            return false;

        var character = _gameData.Characters[characterId];
        if (string.IsNullOrEmpty(character.Name))
            return false;

        var nameLower = character.Name.ToLowerInvariant();

        // Common hostile character name patterns
        string[] hostilePatterns = {
            "trooper", "attack", "hard", "enemy", "patrol",
            "wampa", "tuscan", "probot", "droid", "vader",
            "fett", "greedo", "ig88", "scorpion", "bug",
            "sarlacc", "rancor", "tank", "gurk", "jawa"
        };

        foreach (var pattern in hostilePatterns)
        {
            if (nameLower.Contains(pattern))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Finds a door to an unexplored zone.
    /// </summary>
    public ZoneObject? FindUnexploredDoor()
    {
        if (_state.CurrentZone == null) return null;

        foreach (var obj in _state.CurrentZone.Objects)
        {
            if (obj.Type == ZoneObjectType.DoorEntrance ||
                obj.Type == ZoneObjectType.DoorExit)
            {
                // Check if destination zone is unexplored (not solved)
                if (obj.Argument < _gameData.Zones.Count && !_state.IsZoneSolved(obj.Argument))
                {
                    return obj;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the direction to an adjacent zone (for edge transitions).
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
    /// Gets a list of connected zone IDs that haven't been visited.
    /// </summary>
    public List<int> GetUnexploredConnectedZones()
    {
        var result = new List<int>();
        var world = _worldGenerator.CurrentWorld;
        if (world == null) return result;

        if (!world.Connections.TryGetValue(_state.CurrentZoneId, out var connections))
            return result;

        if (connections.North.HasValue && !_state.IsZoneSolved(connections.North.Value))
            result.Add(connections.North.Value);
        if (connections.South.HasValue && !_state.IsZoneSolved(connections.South.Value))
            result.Add(connections.South.Value);
        if (connections.East.HasValue && !_state.IsZoneSolved(connections.East.Value))
            result.Add(connections.East.Value);
        if (connections.West.HasValue && !_state.IsZoneSolved(connections.West.Value))
            result.Add(connections.West.Value);

        return result;
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
