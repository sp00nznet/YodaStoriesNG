using YodaStoriesNG.Engine.Data;

namespace YodaStoriesNG.Engine.Game;

/// <summary>
/// Sector types matching the original game's world generation.
/// Each cell in the 10x10 grid has a sector type.
/// IMPORTANT: The order matches the reference implementation for comparison logic.
/// </summary>
public enum SectorType
{
    BlockEast,      // Blockade requiring item (blocks east exit)
    BlockNorth,     // Blockade requiring item (blocks north exit)
    BlockSouth,     // Blockade requiring item (blocks south exit)
    BlockWest,      // Blockade requiring item (blocks west exit)
    Candidate,      // Potential puzzle location
    Empty,          // Regular walkable zone
    Island,         // Isolated area reached by travel
    KeptFree,       // Reserved space (no zone placed)
    Puzzle,         // Contains a puzzle
    Spaceport,      // Landing zone with X-Wing
    TravelEnd,      // Vehicle travel destination (island)
    TravelStart,    // Vehicle travel origin
    None            // Unassigned
}

/// <summary>
/// Generates a playable world by assembling zones into a connected map.
/// Based on the Yoda Stories procedural generation system.
/// </summary>
public class WorldGenerator
{
    private readonly GameData _gameData;
    private readonly Random _random = new();

    // World grid - dynamic based on world size (10 for Small/Medium/Large, 15 for XtraLarge)
    private int _gridSize = 10;

    /// <summary>Current grid size.</summary>
    public int GridSize => _gridSize;

    // Special item tile IDs (from original game)
    public const int TILE_THE_FORCE = 511;      // The Force - guaranteed weapon
    public const int TILE_LOCATOR = 512;        // Locator/Map item
    public const int TILE_LIGHTSABER = 510;     // Lightsaber

    // The generated world
    public WorldMap? CurrentWorld { get; private set; }

    // Current mission
    public Mission? CurrentMission { get; private set; }

    // Mission progression (1-15)
    private static int _currentMissionNumber = 1;
    private static List<int> _usedGoalPuzzles = new();

    /// <summary>
    /// Current mission number (1-15).
    /// </summary>
    public static int CurrentMissionNumber => _currentMissionNumber;

    public WorldGenerator(GameData gameData)
    {
        _gameData = gameData;
    }

    /// <summary>
    /// Sets the current world (used when loading a save game).
    /// </summary>
    public void SetCurrentWorld(WorldMap world)
    {
        CurrentWorld = world;
        CurrentMission = world.Mission;
        if (world.MissionNumber > 0)
            _currentMissionNumber = world.MissionNumber;
    }

    /// <summary>
    /// Diagnostic: Dump info about Dagobah zones and characters to help find the real Yoda.
    /// </summary>
    public void DumpDagobahInfo()
    {
        Console.WriteLine("\n=== DAGOBAH ZONE ANALYSIS ===");

        // Find Yoda in characters
        Console.WriteLine("\n-- Characters (looking for Yoda) --");
        for (int i = 0; i < _gameData.Characters.Count; i++)
        {
            var ch = _gameData.Characters[i];
            if (!string.IsNullOrEmpty(ch.Name))
            {
                Console.WriteLine($"  Character {i}: {ch.Name} (Type={ch.Type})");
            }
        }

        // Find Swamp zones (Dagobah)
        Console.WriteLine("\n-- Swamp/Dagobah Zones --");
        var swampZones = _gameData.Zones.Where(z => z.Planet == Planet.Swamp && z.Width > 0).ToList();
        Console.WriteLine($"Found {swampZones.Count} Swamp zones");

        foreach (var zone in swampZones)
        {
            Console.WriteLine($"\n  Zone {zone.Id}: {zone.Width}x{zone.Height}, Type={zone.Type}");

            // List all objects
            foreach (var obj in zone.Objects)
            {
                string details = obj.Type switch
                {
                    ZoneObjectType.PuzzleNPC => $"CharID={obj.Argument}" +
                        (obj.Argument < _gameData.Characters.Count ? $" ({_gameData.Characters[obj.Argument].Name})" : ""),
                    ZoneObjectType.XWingFromDagobah => "X-Wing departure point!",
                    ZoneObjectType.XWingToDagobah => $"X-Wing arrival -> zone {obj.Argument}",
                    ZoneObjectType.SpawnLocation => "Player spawn",
                    ZoneObjectType.DoorEntrance => $"Door to zone {obj.Argument}",
                    _ => $"Arg={obj.Argument}"
                };
                Console.WriteLine($"    {obj.Type} at ({obj.X},{obj.Y}): {details}");
            }

            // Check actions
            if (zone.Actions.Any())
            {
                int totalInstructions = zone.Actions.Sum(a => a.Instructions.Count);
                int totalConditions = zone.Actions.Sum(a => a.Conditions.Count);
                Console.WriteLine($"    Actions: {zone.Actions.Count} scripts, {totalConditions} conditions, {totalInstructions} instructions");
            }
        }

        // Dump character tile IDs to find Yoda
        Console.WriteLine("\n-- Character Tile IDs (looking for Yoda sprites) --");
        for (int i = 0; i < Math.Min(10, _gameData.Characters.Count); i++)
        {
            var ch = _gameData.Characters[i];
            Console.WriteLine($"  Char {i} ({ch.Name}): Down={ch.Frames.WalkDown[0]}, Up={ch.Frames.WalkUp[0]}");
        }

        // Search puzzles for "Yoda" references
        Console.WriteLine("\n-- Searching puzzles for Yoda --");
        foreach (var puzzle in _gameData.Puzzles.Where(p => p.Strings.Any(s => s.ToLower().Contains("yoda"))))
        {
            Console.WriteLine($"  Puzzle {puzzle.Id}: Item1={puzzle.Item1}, Item2={puzzle.Item2}");
            foreach (var str in puzzle.Strings)
            {
                Console.WriteLine($"    \"{str}\"");
            }
        }

        // Dump IZAX data for Dagobah zones to find entity spawns
        Console.WriteLine("\n-- IZAX Entity Data for Dagobah Zones --");
        foreach (var zone in swampZones)
        {
            if (zone.AuxData?.RawData != null && zone.AuxData.RawData.Length > 0)
            {
                Console.WriteLine($"\n  Zone {zone.Id} IZAX ({zone.AuxData.RawData.Length} bytes):");
                DumpIzaxEntities(zone.AuxData.RawData);
            }
        }

        // Find all Creature tiles that aren't mapped to any character
        Console.WriteLine("\n-- Searching for Yoda Tile (Creature tiles not in CHAR list) --");
        var charTileIds = new HashSet<ushort>();
        foreach (var ch in _gameData.Characters)
        {
            foreach (var t in ch.Frames.WalkDown) if (t != 0xFFFF) charTileIds.Add(t);
            foreach (var t in ch.Frames.WalkUp) if (t != 0xFFFF) charTileIds.Add(t);
            foreach (var t in ch.Frames.WalkLeft) if (t != 0xFFFF) charTileIds.Add(t);
            foreach (var t in ch.Frames.WalkRight) if (t != 0xFFFF) charTileIds.Add(t);
        }
        Console.WriteLine($"  Found {charTileIds.Count} tiles used by characters");

        // Check tiles with Character flag (bit 8 set)
        var creatureTiles = _gameData.Tiles.Where(t => t.IsCharacter && !charTileIds.Contains((ushort)t.Id)).ToList();
        Console.WriteLine($"  Found {creatureTiles.Count} Character tiles NOT mapped to characters:");
        foreach (var tile in creatureTiles.Take(30))
        {
            var flagType = ((int)tile.Flags & 0x70000) switch
            {
                0x40000 => "FRIENDLY",
                0x20000 => "ENEMY",
                0x10000 => "PLAYER",
                _ => "Unknown"
            };
            Console.WriteLine($"    Tile {tile.Id}: flags=0x{(int)tile.Flags:X5} ({flagType})");
        }

        // Specifically look for Friendly NPC tiles (flag 0x40000 = CharFriendly)
        var friendlyTiles = _gameData.Tiles.Where(t =>
            t.IsCharacter &&
            ((int)t.Flags & 0x40000) != 0 &&
            !charTileIds.Contains((ushort)t.Id)).ToList();
        Console.WriteLine($"\n  FRIENDLY character tiles not in CHAR list (likely Yoda!):");
        foreach (var tile in friendlyTiles)
        {
            Console.WriteLine($"    Tile {tile.Id}: flags=0x{(int)tile.Flags:X5}");
        }

        // Look at XWingFromDagobah objects to find tile IDs
        // Check area around X-Wing position (6,10) in zone 93 to see all tiles
        Console.WriteLine("\n-- Tiles around X-Wing position (6,10) in zone 93 --");
        var zone93 = _gameData.Zones.FirstOrDefault(z => z.Id == 93);
        if (zone93 != null)
        {
            Console.WriteLine("  Layer 1 tiles in area (4-10, 8-14):");
            for (int y = 8; y <= 14; y++)
            {
                Console.Write($"    y={y}: ");
                for (int x = 4; x <= 10; x++)
                {
                    var tile = zone93.GetTile(x, y, 1);
                    if (tile != 0xFFFF)
                        Console.Write($"[{x},{y}]={tile} ");
                }
                Console.WriteLine();
            }
        }

        Console.WriteLine("\n-- XWing Object Details --");
        foreach (var zone in _gameData.Zones.Where(z => z.Width > 0))
        {
            foreach (var obj in zone.Objects)
            {
                if (obj.Type == ZoneObjectType.XWingFromDagobah || obj.Type == ZoneObjectType.XWingToDagobah)
                {
                    Console.WriteLine($"  Zone {zone.Id}: {obj.Type} at ({obj.X},{obj.Y}), Argument={obj.Argument}");
                    // Check what tile is at this position in the zone
                    for (int layer = 0; layer < 3; layer++)
                    {
                        var tileId = zone.GetTile(obj.X, obj.Y, layer);
                        if (tileId != 0xFFFF && tileId < _gameData.Tiles.Count)
                        {
                            var tile = _gameData.Tiles[tileId];
                            // Count non-transparent pixels
                            int nonTransparentPixels = tile.PixelData.Count(p => p != 0);

                            // For X-Wing tile, show color distribution
                            if (tileId == 939)
                            {
                                var colorCounts = tile.PixelData.GroupBy(p => p).OrderByDescending(g => g.Count()).Take(5);
                                var colors = string.Join(", ", colorCounts.Select(g => $"idx{g.Key}={g.Count()}"));
                                Console.WriteLine($"    Layer {layer}: tile {tileId}, top colors: {colors}");
                            }
                            Console.WriteLine($"    Layer {layer}: tile {tileId}, flags=0x{(int)tile.Flags:X}, transparent={tile.IsTransparent}, pixel0={tile.PixelData[0]}, visible_pixels={nonTransparentPixels}/1024");
                        }
                    }
                }
            }
        }

        // Also find zones with X-Wing objects anywhere
        Console.WriteLine("\n-- Zones with X-Wing Objects (any planet) --");
        foreach (var zone in _gameData.Zones.Where(z => z.Width > 0))
        {
            var xwingObjs = zone.Objects.Where(o =>
                o.Type == ZoneObjectType.XWingFromDagobah ||
                o.Type == ZoneObjectType.XWingToDagobah).ToList();

            if (xwingObjs.Any())
            {
                Console.WriteLine($"  Zone {zone.Id} (Planet={zone.Planet}):");
                foreach (var obj in xwingObjs)
                {
                    Console.WriteLine($"    {obj.Type} at ({obj.X},{obj.Y})");
                }
            }
        }

        Console.WriteLine("\n=== END DAGOBAH ANALYSIS ===\n");
    }

    /// <summary>
    /// Parses and dumps IZAX entity data.
    /// IZAX format: 4 bytes header, 2 bytes count, then for each entity: charId(2), x(2), y(2), itemTile(2), itemQuantity(2), data(6)
    /// </summary>
    private void DumpIzaxEntities(byte[] izaxData)
    {
        if (izaxData.Length < 6) return;

        using var ms = new MemoryStream(izaxData);
        using var reader = new BinaryReader(ms);

        try
        {
            // Skip 4-byte header, then read entity count (2 bytes)
            reader.ReadUInt32();
            var entityCount = reader.ReadUInt16();
            Console.WriteLine($"    Entity count: {entityCount}");

            // Each entity is 16 bytes: charId(2) + x(2) + y(2) + itemTile(2) + itemQty(2) + data(6)
            for (int i = 0; i < entityCount && ms.Position + 16 <= ms.Length; i++)
            {
                var charId = reader.ReadUInt16();
                var x = reader.ReadUInt16();
                var y = reader.ReadUInt16();
                var itemTile = reader.ReadUInt16();
                var itemQty = reader.ReadUInt16();
                var data = reader.ReadBytes(6);

                string charName = "";
                if (charId < _gameData.Characters.Count)
                    charName = _gameData.Characters[charId].Name ?? "";
                else if (charId == 0xFFFF)
                    charName = "(none)";

                Console.WriteLine($"    Entity {i}: CharID={charId} ({charName}) at ({x},{y}), item={itemTile}, qty={itemQty}");
            }

            // Also dump raw hex of first 64 bytes for manual inspection
            ms.Position = 0;
            Console.Write("    Raw hex: ");
            for (int i = 0; i < Math.Min(64, izaxData.Length); i++)
            {
                Console.Write($"{izaxData[i]:X2} ");
            }
            Console.WriteLine();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    Parse error: {ex.Message}");
            Console.Write("    Raw hex: ");
            for (int i = 0; i < Math.Min(64, izaxData.Length); i++)
            {
                Console.Write($"{izaxData[i]:X2} ");
            }
            Console.WriteLine();
        }
    }

    /// <summary>
    /// Generates a new world with a random mission.
    /// </summary>
    public WorldMap GenerateWorld() => GenerateWorld(WorldSize.Medium);

    /// <summary>
    /// Generates a new world with the specified size.
    /// </summary>
    public WorldMap GenerateWorld(WorldSize size)
    {
        // Set grid size based on world size
        _gridSize = size == WorldSize.XtraLarge ? 15 : 10;

        // Pick a random mission (goal puzzle) - but don't build puzzle chain yet
        CurrentMission = SelectRandomMission();
        Console.WriteLine($"Selected mission: {CurrentMission.Name} on {CurrentMission.Planet}");

        // Create the world map
        CurrentWorld = new WorldMap
        {
            Planet = CurrentMission.Planet,
            Mission = CurrentMission
        };

        // Generate Dagobah (starting area - fixed layout)
        GenerateDagobah();

        // Generate the main planet grid
        GeneratePlanetGrid();

        // NOW build the puzzle chain - after grid is generated so we know which zones exist
        if (CurrentMission.GoalPuzzle != null)
        {
            BuildPuzzleChain(CurrentMission, CurrentMission.GoalPuzzle);
            LogMissionDetails(CurrentMission);
        }

        // Set up item chain
        SetupItemChain();

        // Print map visualization for debugging
        CurrentWorld.PrintMapVisualization();

        return CurrentWorld;
    }

    /// <summary>
    /// Logs mission details to console.
    /// </summary>
    private void LogMissionDetails(Mission mission)
    {
        Console.WriteLine($"\n=== MISSION: {mission.Name} ===");
        Console.WriteLine($"Planet: {mission.Planet}");
        Console.WriteLine($"Goal: {mission.Description}");
        Console.WriteLine($"Puzzle chain ({mission.PuzzleChain.Count} steps):");
        for (int i = 0; i < mission.PuzzleChain.Count; i++)
        {
            var step = mission.PuzzleChain[i];
            var reqItem = GetItemName(step.RequiredItemId);
            var rewItem = GetItemName(step.RewardItemId);
            var zoneInfo = step.ZoneId.HasValue ? $" [Zone {step.ZoneId}]" : "";
            Console.WriteLine($"  {i + 1}. [{step.Puzzle.Type}] Need: {reqItem} -> Get: {rewItem}{zoneInfo}");
            if (!string.IsNullOrEmpty(step.Hint))
                Console.WriteLine($"     Hint: {step.Hint}");
        }
        Console.WriteLine();
    }

    /// <summary>
    /// Selects a random mission from available goal puzzles and builds the puzzle chain.
    /// Tracks used puzzles to ensure variety across the 15-mission cycle.
    /// </summary>
    private Mission SelectRandomMission()
    {
        // Find goal-type puzzles that define missions, excluding recently used ones
        var goalPuzzles = _gameData.Puzzles
            .Where(p => p.Type == PuzzleType.Goal && p.Strings.Count > 0)
            .Where(p => !_usedGoalPuzzles.Contains(p.Id))
            .ToList();

        // If all puzzles used, allow reuse
        if (goalPuzzles.Count == 0)
        {
            goalPuzzles = _gameData.Puzzles
                .Where(p => p.Type == PuzzleType.Goal && p.Strings.Count > 0)
                .ToList();
        }

        // Determine planet based on mission number for variety
        var planets = new[] { Planet.Desert, Planet.Snow, Planet.Forest };
        var planet = planets[(_currentMissionNumber - 1) % planets.Length];

        if (goalPuzzles.Count == 0)
        {
            // Fallback: create a simple mission with no puzzle chain
            return CreateFallbackMission(planet);
        }

        var goalPuzzle = goalPuzzles[_random.Next(goalPuzzles.Count)];
        _usedGoalPuzzles.Add(goalPuzzle.Id);

        var mission = new Mission
        {
            Id = goalPuzzle.Id,
            Name = goalPuzzle.Strings.FirstOrDefault() ?? "Unknown Mission",
            Planet = planet,
            Description = goalPuzzle.Strings.Count > 1 ? goalPuzzle.Strings[1] : "",
            GoalPuzzle = goalPuzzle,
            MissionNumber = _currentMissionNumber
        };

        Console.WriteLine($"Mission {_currentMissionNumber}/15: {mission.Name} on {planet}");

        // NOTE: Puzzle chain will be built later in GenerateWorld() after grid is generated

        return mission;
    }

    /// <summary>
    /// Creates a fallback mission when no goal puzzles are found.
    /// </summary>
    private Mission CreateFallbackMission(Planet planet)
    {
        var mission = new Mission
        {
            Id = 0,
            Name = "Find the Lost Artifact",
            Planet = planet,
            Description = "Locate and retrieve a valuable artifact.",
            GoalPuzzle = null
        };

        // Create a simple 3-step chain using available items
        var itemTiles = _gameData.Tiles.Where(t => t.IsItem).Take(10).ToList();
        if (itemTiles.Count >= 3)
        {
            // Step 1: Trade starting item for intermediate item
            mission.PuzzleChain.Add(new PuzzleStep
            {
                Puzzle = new Puzzle { Type = PuzzleType.Trade, Strings = { "Find a trader" } },
                RequiredItemId = itemTiles[0].Id,
                RewardItemId = itemTiles[1].Id,
                Hint = "Find someone willing to trade."
            });

            // Step 2: Use intermediate item to get key
            mission.PuzzleChain.Add(new PuzzleStep
            {
                Puzzle = new Puzzle { Type = PuzzleType.Use, Strings = { "Use the item" } },
                RequiredItemId = itemTiles[1].Id,
                RewardItemId = itemTiles[2].Id,
                Hint = "Use the item in the right place."
            });

            // Step 3: Goal - deliver the key
            mission.PuzzleChain.Add(new PuzzleStep
            {
                Puzzle = new Puzzle { Type = PuzzleType.Goal, Strings = { "Complete the mission" } },
                RequiredItemId = itemTiles[2].Id,
                RewardItemId = 0,
                Hint = "Deliver the artifact to complete your mission."
            });
        }

        return mission;
    }

    /// <summary>
    /// Builds the puzzle chain for a mission based on zone objects.
    /// Since puzzle data parsing is unreliable, we scan zones for items and NPCs.
    /// Each step includes the specific zone where the item/NPC can be found.
    /// Only uses zones that are in the generated world grid.
    /// </summary>
    private void BuildPuzzleChain(Mission mission, Puzzle goalPuzzle)
    {
        var chainSteps = new List<PuzzleStep>();

        // Get the set of zone IDs that are actually in our generated world
        var worldZoneIds = new HashSet<int>();
        if (CurrentWorld?.Grid != null)
        {
            for (int y = 0; y < GridSize; y++)
            {
                for (int x = 0; x < GridSize; x++)
                {
                    if (CurrentWorld.Grid[y, x].HasValue)
                        worldZoneIds.Add(CurrentWorld.Grid[y, x]!.Value);
                }
            }
        }

        // Find item-bearing objects ONLY in zones that are in the world grid
        var planetZones = _gameData.Zones
            .Where(z => z.Planet == mission.Planet && z.Width > 0 && worldZoneIds.Contains(z.Id))
            .ToList();

        Console.WriteLine($"BuildPuzzleChain: {worldZoneIds.Count} zones in world, {planetZones.Count} planet zones to scan");

        // Collect items with their zone locations
        var itemLocations = new List<(int ItemId, int ZoneId, int X, int Y)>();
        var npcZones = new List<(int ZoneId, int CharacterId, int X, int Y)>();

        foreach (var zone in planetZones)
        {
            foreach (var obj in zone.Objects)
            {
                if (obj.Type == ZoneObjectType.CrateItem || obj.Type == ZoneObjectType.LocatorItem)
                {
                    if (obj.Argument > 0 && obj.Argument < _gameData.Tiles.Count)
                    {
                        itemLocations.Add((obj.Argument, zone.Id, obj.X, obj.Y));
                    }
                }
                else if (obj.Type == ZoneObjectType.PuzzleNPC)
                {
                    npcZones.Add((zone.Id, obj.Argument, obj.X, obj.Y));
                }
            }

            // Also check IZAX entity data for NPCs with items
            if (zone.AuxData?.Entities != null)
            {
                foreach (var entity in zone.AuxData.Entities)
                {
                    if (entity.ItemTileId > 0 && entity.ItemTileId != 0xFFFF)
                    {
                        itemLocations.Add((entity.ItemTileId, zone.Id, entity.X, entity.Y));
                    }
                    if (entity.CharacterId > 0 && entity.CharacterId != 0xFFFF)
                    {
                        npcZones.Add((zone.Id, entity.CharacterId, entity.X, entity.Y));
                    }
                }
            }
        }

        // Shuffle and take a subset
        itemLocations = itemLocations.OrderBy(_ => _random.Next()).Take(5).ToList();
        npcZones = npcZones.OrderBy(_ => _random.Next()).ToList();

        // Build a puzzle chain with specific zone locations
        if (itemLocations.Count >= 2)
        {
            // Step 1: Find first item at a specific location
            var firstItem = itemLocations[0];
            chainSteps.Add(new PuzzleStep
            {
                Puzzle = new Puzzle { Type = PuzzleType.Quest, Strings = { "Find an item" } },
                RequiredItemId = 0,  // No item required - just find it
                RewardItemId = firstItem.ItemId,
                ZoneId = firstItem.ZoneId,
                TargetX = firstItem.X,
                TargetY = firstItem.Y,
                Hint = $"Search zone {firstItem.ZoneId} for useful items."
            });

            // Steps 2-N: Trade items with NPCs at specific zones
            for (int i = 0; i < itemLocations.Count - 1; i++)
            {
                var npcZone = npcZones.Count > i ? npcZones[i] : (ZoneId: itemLocations[i + 1].ZoneId, CharacterId: 0, X: 0, Y: 0);
                var nextItem = itemLocations[i + 1];

                chainSteps.Add(new PuzzleStep
                {
                    Puzzle = new Puzzle { Type = PuzzleType.Trade, Strings = { "Trade with someone" } },
                    RequiredItemId = itemLocations[i].ItemId,
                    RewardItemId = nextItem.ItemId,
                    ZoneId = npcZone.ZoneId,
                    TargetX = npcZone.X,
                    TargetY = npcZone.Y,
                    Hint = $"Find someone in zone {npcZone.ZoneId} who needs this item."
                });
            }

            // Final step: Complete goal
            var goalZone = npcZones.Count > 0 ? npcZones[^1] : (ZoneId: itemLocations[^1].ZoneId, CharacterId: 0, X: 0, Y: 0);
            chainSteps.Add(new PuzzleStep
            {
                Puzzle = goalPuzzle,
                RequiredItemId = itemLocations[^1].ItemId,
                RewardItemId = 0,  // Mission complete!
                ZoneId = goalZone.ZoneId,
                TargetX = goalZone.X,
                TargetY = goalZone.Y,
                Hint = goalPuzzle.Strings.Count > 1 ? goalPuzzle.Strings[1] : "Complete your mission."
            });
        }
        else
        {
            // Fallback: simple exploration mission
            chainSteps.Add(new PuzzleStep
            {
                Puzzle = goalPuzzle,
                RequiredItemId = 0,
                RewardItemId = 0,
                ZoneId = CurrentWorld?.LandingZoneId,
                Hint = "Explore the planet and find what you need."
            });
        }

        mission.PuzzleChain = chainSteps;
    }

    /// <summary>
    /// Gets an item name from tile data.
    /// </summary>
    private string GetItemName(int tileId)
    {
        if (tileId <= 0 || tileId >= _gameData.Tiles.Count)
            return "(none)";

        if (_gameData.TileNames.TryGetValue(tileId, out var name))
            return name;

        return $"Item #{tileId}";
    }

    /// <summary>
    /// Generates the Dagobah starting area (fixed layout).
    /// Dagobah uses zones 93, 94, 95, 96 in a 2x2 grid.
    /// </summary>
    private void GenerateDagobah()
    {
        if (CurrentWorld == null) return;

        // Dagobah outdoor zones are specifically 93, 94, 95, 96
        // Zone 93 has the X-Wing departure point
        var dagobahOutdoorZoneIds = new[] { 93, 94, 95, 96 };

        // Verify zones exist
        var dagobahZones = dagobahOutdoorZoneIds
            .Where(id => id < _gameData.Zones.Count && _gameData.Zones[id].Width > 0)
            .ToList();

        if (dagobahZones.Count == 0)
        {
            // Fallback: use any swamp zones
            dagobahZones = _gameData.Zones
                .Where(z => z.Planet == Planet.Swamp && z.Width > 0 && z.Width == 18)
                .Select(z => z.Id)
                .Take(4)
                .ToList();
        }

        if (dagobahZones.Count == 0)
        {
            Console.WriteLine("Error: No Dagobah zones found!");
            return;
        }

        // Add Dagobah zones
        foreach (var zoneId in dagobahZones)
        {
            CurrentWorld.DagobahZones.Add(zoneId);
        }

        // Zone 93 is the starting zone (has X-Wing departure)
        CurrentWorld.StartingZoneId = dagobahZones[0];  // Should be 93
        CurrentWorld.XWingZoneId = CurrentWorld.StartingZoneId;

        // Yoda always appears in the starting zone for easy access
        CurrentWorld.YodaZoneId = CurrentWorld.StartingZoneId;

        // Yoda spawns near the player (player starts around 13,4, Yoda at 11,4)
        CurrentWorld.YodaPosition = (11, 4);

        Console.WriteLine($"Yoda will appear in zone {CurrentWorld.YodaZoneId} at position {CurrentWorld.YodaPosition}");

        // Debug: List all Dagobah zones
        Console.WriteLine($"Dagobah zones: {string.Join(", ", dagobahZones)}");

        // Connect Dagobah zones in a 2x2 grid pattern:
        //   [94] [93]    (93 has X-Wing - put it on the right side)
        //   [96] [95]
        // So: 94 is NW, 93 is NE, 96 is SW, 95 is SE
        if (dagobahZones.Count >= 4)
        {
            // Zone 93 (NE): connects west to 94, south to 95
            CurrentWorld.Connections[93] = new ZoneConnections
            {
                ZoneId = 93,
                West = 94,
                South = 95
            };

            // Zone 94 (NW): connects east to 93, south to 96
            CurrentWorld.Connections[94] = new ZoneConnections
            {
                ZoneId = 94,
                East = 93,
                South = 96
            };

            // Zone 95 (SE): connects west to 96, north to 93
            CurrentWorld.Connections[95] = new ZoneConnections
            {
                ZoneId = 95,
                West = 96,
                North = 93
            };

            // Zone 96 (SW): connects east to 95, north to 94
            CurrentWorld.Connections[96] = new ZoneConnections
            {
                ZoneId = 96,
                East = 95,
                North = 94
            };
        }
        else
        {
            // Fallback: connect linearly
            for (int i = 0; i < dagobahZones.Count; i++)
            {
                var zoneId = dagobahZones[i];
                var conn = new ZoneConnections { ZoneId = zoneId };
                if (i > 0)
                    conn.West = dagobahZones[i - 1];
                if (i < dagobahZones.Count - 1)
                    conn.East = dagobahZones[i + 1];
                CurrentWorld.Connections[zoneId] = conn;
            }
        }
    }

    /// <summary>
    /// Generates the main planet grid with connected zones using the proper MapGenerator algorithm.
    /// This creates a complex world with blockades, travel zones, islands, and proper puzzle ordering.
    /// </summary>
    private void GeneratePlanetGrid()
    {
        if (CurrentWorld == null || CurrentMission == null) return;

        // Initialize grids with dynamic size
        CurrentWorld.Grid = new int?[_gridSize, _gridSize];
        CurrentWorld.TypeMap = new SectorType[_gridSize, _gridSize];
        CurrentWorld.OrderMap = new int[_gridSize, _gridSize];

        // Store grid size in world
        CurrentWorld.GridWidth = _gridSize;
        CurrentWorld.GridHeight = _gridSize;

        // Determine world size for MapGenerator
        WorldSize worldSize;
        if (_gridSize == 15)
        {
            worldSize = WorldSize.XtraLarge;
        }
        else
        {
            worldSize = _currentMissionNumber switch
            {
                <= 5 => WorldSize.Small,
                <= 10 => WorldSize.Medium,
                _ => WorldSize.Large
            };
        }

        // Use the proper MapGenerator to create the world layout
        var mapGenerator = new MapGenerator();
        mapGenerator.Generate(_random.Next(), worldSize);

        // Copy the generated type and order maps
        for (int y = 0; y < _gridSize; y++)
        {
            for (int x = 0; x < _gridSize; x++)
            {
                CurrentWorld.TypeMap[y, x] = mapGenerator.TypeMap[x + y * mapGenerator.MapWidth];
                CurrentWorld.OrderMap[y, x] = mapGenerator.OrderMap[x + y * mapGenerator.MapWidth];
            }
        }

        // Find outdoor zones matching the mission's planet type (18x18 only, not 9x9 rooms)
        var planetZones = _gameData.Zones
            .Where(z => z.Planet == CurrentMission.Planet && z.Width == 18 && z.Height == 18)
            .ToList();

        // Also find indoor rooms for this planet (for door connections later)
        var roomZones = _gameData.Zones
            .Where(z => z.Planet == CurrentMission.Planet && z.Width > 0 && z.Width < 18)
            .ToList();

        Console.WriteLine($"Planet {CurrentMission.Planet}: found {planetZones.Count} outdoor zones, {roomZones.Count} room zones");

        if (planetZones.Count == 0)
        {
            Console.WriteLine($"Warning: No outdoor zones found for planet {CurrentMission.Planet}");
            return;
        }

        // Categorize zones by type to match sector types
        var emptyZones = planetZones.Where(z => z.Type == ZoneType.Empty || z.Type == ZoneType.None).ToList();
        var townZones = planetZones.Where(z => z.Type == ZoneType.Town).ToList();
        var goalZones = planetZones.Where(z => z.Type == ZoneType.Goal).ToList();
        var tradeZones = planetZones.Where(z => z.Type == ZoneType.Trade).ToList();
        var useZones = planetZones.Where(z => z.Type == ZoneType.Use).ToList();
        var findZones = planetZones.Where(z => z.Type == ZoneType.Find).ToList();
        var blockadeZonesN = planetZones.Where(z => z.Type == ZoneType.BlockadeNorth).ToList();
        var blockadeZonesS = planetZones.Where(z => z.Type == ZoneType.BlockadeSouth).ToList();
        var blockadeZonesE = planetZones.Where(z => z.Type == ZoneType.BlockadeEast).ToList();
        var blockadeZonesW = planetZones.Where(z => z.Type == ZoneType.BlockadeWest).ToList();
        var travelStartZones = planetZones.Where(z => z.Type == ZoneType.TravelStart).ToList();
        var travelEndZones = planetZones.Where(z => z.Type == ZoneType.TravelEnd).ToList();

        // Fallback lists
        if (emptyZones.Count == 0) emptyZones = planetZones.Where(z => z.Type != ZoneType.Goal).ToList();
        var puzzleZones = tradeZones.Concat(useZones).Concat(findZones).ToList();
        if (puzzleZones.Count == 0) puzzleZones = emptyZones;

        // Blockade fallbacks
        var allBlockadeZones = blockadeZonesN.Concat(blockadeZonesS).Concat(blockadeZonesE).Concat(blockadeZonesW).ToList();
        if (allBlockadeZones.Count == 0) allBlockadeZones = emptyZones;
        if (blockadeZonesN.Count == 0) blockadeZonesN = allBlockadeZones;
        if (blockadeZonesS.Count == 0) blockadeZonesS = allBlockadeZones;
        if (blockadeZonesE.Count == 0) blockadeZonesE = allBlockadeZones;
        if (blockadeZonesW.Count == 0) blockadeZonesW = allBlockadeZones;

        // Travel fallbacks
        if (travelStartZones.Count == 0) travelStartZones = emptyZones;
        if (travelEndZones.Count == 0) travelEndZones = emptyZones;

        // Track used zones
        var usedZones = new HashSet<int>();

        // Find spaceport position and assign landing zone
        int spaceportX = -1, spaceportY = -1;
        for (int y = 0; y < GridSize; y++)
        {
            for (int x = 0; x < GridSize; x++)
            {
                if (CurrentWorld.TypeMap[y, x] == SectorType.Spaceport)
                {
                    spaceportX = x;
                    spaceportY = y;
                    break;
                }
            }
            if (spaceportX >= 0) break;
        }

        // Assign zones based on sector types
        for (int y = 0; y < GridSize; y++)
        {
            for (int x = 0; x < GridSize; x++)
            {
                var sectorType = CurrentWorld.TypeMap[y, x];
                if (sectorType == SectorType.None || sectorType == SectorType.KeptFree)
                    continue;

                Zone? zone = null;
                List<Zone> candidates;

                switch (sectorType)
                {
                    case SectorType.Spaceport:
                        // Landing zone - must have X-Wing landing spot
                        candidates = planetZones
                            .Where(z => z.Objects.Any(o => o.Type == ZoneObjectType.XWingToDagobah) && !usedZones.Contains(z.Id))
                            .ToList();
                        if (candidates.Count == 0)
                            candidates = townZones.Where(z => !usedZones.Contains(z.Id)).ToList();
                        if (candidates.Count == 0)
                            candidates = emptyZones.Where(z => !usedZones.Contains(z.Id)).ToList();
                        break;

                    case SectorType.Puzzle:
                        // Use goal zone for the highest-order puzzle (mission objective)
                        int order = CurrentWorld.OrderMap[y, x];
                        if (order == mapGenerator.PuzzleCount - 1 && goalZones.Count > 0)
                        {
                            candidates = goalZones.Where(z => !usedZones.Contains(z.Id)).ToList();
                        }
                        else
                        {
                            candidates = puzzleZones.Where(z => !usedZones.Contains(z.Id)).ToList();
                        }
                        if (candidates.Count == 0)
                            candidates = emptyZones.Where(z => !usedZones.Contains(z.Id)).ToList();
                        break;

                    case SectorType.Empty:
                    case SectorType.Candidate:
                        candidates = emptyZones.Where(z => !usedZones.Contains(z.Id)).ToList();
                        break;

                    case SectorType.BlockNorth:
                        candidates = blockadeZonesN.Where(z => !usedZones.Contains(z.Id)).ToList();
                        break;
                    case SectorType.BlockSouth:
                        candidates = blockadeZonesS.Where(z => !usedZones.Contains(z.Id)).ToList();
                        break;
                    case SectorType.BlockEast:
                        candidates = blockadeZonesE.Where(z => !usedZones.Contains(z.Id)).ToList();
                        break;
                    case SectorType.BlockWest:
                        candidates = blockadeZonesW.Where(z => !usedZones.Contains(z.Id)).ToList();
                        break;

                    case SectorType.TravelStart:
                        candidates = travelStartZones.Where(z => !usedZones.Contains(z.Id)).ToList();
                        break;

                    case SectorType.TravelEnd:
                    case SectorType.Island:
                        candidates = travelEndZones.Where(z => !usedZones.Contains(z.Id)).ToList();
                        if (candidates.Count == 0)
                            candidates = emptyZones.Where(z => !usedZones.Contains(z.Id)).ToList();
                        break;

                    default:
                        candidates = emptyZones.Where(z => !usedZones.Contains(z.Id)).ToList();
                        break;
                }

                if (candidates.Count > 0)
                {
                    zone = candidates[_random.Next(candidates.Count)];
                    CurrentWorld.Grid[y, x] = zone.Id;
                    usedZones.Add(zone.Id);

                    // Track special zones
                    if (sectorType == SectorType.Spaceport)
                    {
                        CurrentWorld.LandingZoneId = zone.Id;
                        CurrentWorld.LandingPosition = (x, y);
                    }
                    else if (sectorType == SectorType.Puzzle)
                    {
                        int order = CurrentWorld.OrderMap[y, x];
                        if (order == mapGenerator.PuzzleCount - 1)
                        {
                            CurrentWorld.ObjectiveZoneId = zone.Id;
                            CurrentWorld.ObjectivePosition = (x, y);
                        }
                    }
                }
            }
        }

        // Set up zone connections (adjacent cells are connected)
        SetupZoneConnections();

        // Associate room zones with their parent outdoor zones (for doors)
        AssociateRoomZones(roomZones, usedZones);

        // Store generation stats
        CurrentWorld.MissionNumber = _currentMissionNumber;

        var stats = CurrentWorld.GetMapStatistics();
        Console.WriteLine($"Generated planet grid: {stats.TotalZones} zones, {stats.PuzzleCount} puzzles, " +
                          $"{stats.BlockadeCount} blockades, {stats.TravelCount} travels");
    }

    private bool HasAdjacentCell(int?[,] grid, int x, int y)
    {
        if (x > 0 && grid[y, x - 1] != null) return true;
        if (x < GridSize - 1 && grid[y, x + 1] != null) return true;
        if (y > 0 && grid[y - 1, x] != null) return true;
        if (y < GridSize - 1 && grid[y + 1, x] != null) return true;
        return false;
    }

    /// <summary>
    /// Sets up connections between adjacent zones in the grid.
    /// </summary>
    private void SetupZoneConnections()
    {
        if (CurrentWorld == null) return;

        Console.WriteLine($"[WORLD] Setting up zone connections for {GridSize}x{GridSize} grid...");
        int connectionCount = 0;

        for (int y = 0; y < GridSize; y++)
        {
            for (int x = 0; x < GridSize; x++)
            {
                var zoneId = CurrentWorld.Grid[y, x];
                if (zoneId == null) continue;

                var connections = new ZoneConnections { ZoneId = zoneId.Value };

                // Check each direction
                if (x > 0 && CurrentWorld.Grid[y, x - 1] != null)
                    connections.West = CurrentWorld.Grid[y, x - 1];
                if (x < GridSize - 1 && CurrentWorld.Grid[y, x + 1] != null)
                    connections.East = CurrentWorld.Grid[y, x + 1];
                if (y > 0 && CurrentWorld.Grid[y - 1, x] != null)
                    connections.North = CurrentWorld.Grid[y - 1, x];
                if (y < GridSize - 1 && CurrentWorld.Grid[y + 1, x] != null)
                    connections.South = CurrentWorld.Grid[y + 1, x];

                CurrentWorld.Connections[zoneId.Value] = connections;

                // Count actual connections
                if (connections.North.HasValue) connectionCount++;
                if (connections.South.HasValue) connectionCount++;
                if (connections.East.HasValue) connectionCount++;
                if (connections.West.HasValue) connectionCount++;
            }
        }

        Console.WriteLine($"[WORLD] Created {CurrentWorld.Connections.Count} zone entries with {connectionCount} total connections");

        // Print grid visualization
        Console.WriteLine("[WORLD] Zone grid (10x10):");
        for (int y = 0; y < GridSize; y++)
        {
            var row = "";
            for (int x = 0; x < GridSize; x++)
            {
                var zoneId = CurrentWorld.Grid[y, x];
                row += zoneId.HasValue ? $"{zoneId.Value,4}" : "   .";
            }
            Console.WriteLine($"  {row}");
        }
    }

    /// <summary>
    /// Associates indoor room zones with outdoor zones that have door entrances.
    /// </summary>
    private void AssociateRoomZones(List<Zone> roomZones, HashSet<int> usedOutdoorZones)
    {
        if (CurrentWorld == null) return;

        foreach (var outdoorZoneId in usedOutdoorZones)
        {
            var zone = _gameData.Zones.FirstOrDefault(z => z.Id == outdoorZoneId);
            if (zone == null) continue;

            // Find door entrances in this zone
            var doorEntrances = zone.Objects
                .Where(o => o.Type == ZoneObjectType.DoorEntrance)
                .ToList();

            foreach (var door in doorEntrances)
            {
                // The door's argument is the destination zone ID
                var destZoneId = door.Argument;
                if (destZoneId < _gameData.Zones.Count)
                {
                    // Register this room as associated with the outdoor zone
                    if (!CurrentWorld.RoomConnections.ContainsKey(outdoorZoneId))
                        CurrentWorld.RoomConnections[outdoorZoneId] = new List<int>();

                    CurrentWorld.RoomConnections[outdoorZoneId].Add(destZoneId);

                    // Also track the reverse (room -> outdoor)
                    CurrentWorld.RoomParents[destZoneId] = outdoorZoneId;
                }
            }
        }
    }

    /// <summary>
    /// Sets up the item exchange chain for the mission.
    /// </summary>
    private void SetupItemChain()
    {
        if (CurrentWorld == null || CurrentMission == null) return;

        // The starting item is the first item needed in the puzzle chain
        if (CurrentMission.PuzzleChain.Count > 0)
        {
            var firstStep = CurrentMission.PuzzleChain[0];
            if (firstStep.RequiredItemId > 0)
            {
                CurrentWorld.StartingItemId = firstStep.RequiredItemId;
                var itemName = GetItemName(firstStep.RequiredItemId);
                Console.WriteLine($"Starting item: {itemName} (Tile {firstStep.RequiredItemId})");
            }
        }

        // If no puzzle chain, pick a random item
        if (!CurrentWorld.StartingItemId.HasValue)
        {
            var startingItems = _gameData.Tiles
                .Where(t => t.IsItem)
                .Take(20)
                .ToList();

            if (startingItems.Count > 0)
            {
                var startingItem = startingItems[_random.Next(startingItems.Count)];
                CurrentWorld.StartingItemId = startingItem.Id;
                Console.WriteLine($"Starting item (random): Tile {startingItem.Id}");
            }
        }

        // Place The Force at distance 2 from spaceport (guaranteed weapon pickup)
        PlaceTheForce();

        // Build the list of all required items from the puzzle chain
        foreach (var step in CurrentMission.PuzzleChain)
        {
            if (step.RequiredItemId > 0 && !CurrentWorld.RequiredItems.Contains(step.RequiredItemId))
                CurrentWorld.RequiredItems.Add(step.RequiredItemId);
            if (step.RewardItemId > 0 && !CurrentWorld.RequiredItems.Contains(step.RewardItemId))
                CurrentWorld.RequiredItems.Add(step.RewardItemId);
        }

        Console.WriteLine($"Mission requires {CurrentWorld.RequiredItems.Count} unique items");
    }

    /// <summary>
    /// Places The Force weapon at distance 2 from the spaceport.
    /// This is the guaranteed weapon pickup in the original game.
    /// </summary>
    private void PlaceTheForce()
    {
        if (CurrentWorld?.Grid == null) return;

        // Find a zone at distance 2 from the landing zone
        var landingPos = CurrentWorld.LandingPosition;
        var candidateZones = new List<(int zoneId, int x, int y, int distance)>();

        for (int y = 0; y < GridSize; y++)
        {
            for (int x = 0; x < GridSize; x++)
            {
                var zoneId = CurrentWorld.Grid[y, x];
                if (zoneId == null) continue;

                int distance = Math.Abs(x - landingPos.x) + Math.Abs(y - landingPos.y);
                if (distance == 2)
                {
                    candidateZones.Add((zoneId.Value, x, y, distance));
                }
            }
        }

        if (candidateZones.Count == 0)
        {
            // Fallback to distance 1 or 3
            for (int y = 0; y < GridSize; y++)
            {
                for (int x = 0; x < GridSize; x++)
                {
                    var zoneId = CurrentWorld.Grid[y, x];
                    if (zoneId == null) continue;

                    int distance = Math.Abs(x - landingPos.x) + Math.Abs(y - landingPos.y);
                    if (distance >= 1 && distance <= 3 && zoneId != CurrentWorld.LandingZoneId)
                    {
                        candidateZones.Add((zoneId.Value, x, y, distance));
                    }
                }
            }
        }

        if (candidateZones.Count > 0)
        {
            var chosen = candidateZones[_random.Next(candidateZones.Count)];
            CurrentWorld.TheForceZoneId = chosen.zoneId;
            CurrentWorld.TheForcePosition = (chosen.x, chosen.y);
            Console.WriteLine($"The Force placed in zone {chosen.zoneId} at grid ({chosen.x},{chosen.y}), distance {chosen.distance} from landing");
        }
        else
        {
            Console.WriteLine("Warning: Could not place The Force - no suitable zone found");
        }
    }

    /// <summary>
    /// Advances to the next mission (1-15 cycle).
    /// </summary>
    public static void AdvanceMission()
    {
        _currentMissionNumber++;
        if (_currentMissionNumber > 15)
        {
            _currentMissionNumber = 1;
            _usedGoalPuzzles.Clear();  // Reset for new cycle
            Console.WriteLine("=== COMPLETED ALL 15 MISSIONS! Starting new cycle ===");
        }
        Console.WriteLine($"Mission {_currentMissionNumber}/15");
    }

    /// <summary>
    /// Resets mission progression to mission 1.
    /// </summary>
    public static void ResetMissionProgression()
    {
        _currentMissionNumber = 1;
        _usedGoalPuzzles.Clear();
        Console.WriteLine("Mission progression reset to 1/15");
    }

    /// <summary>
    /// Gets the zone ID at a grid position.
    /// </summary>
    public int? GetZoneAt(int gridX, int gridY)
    {
        if (CurrentWorld?.Grid == null) return null;
        if (gridX < 0 || gridX >= GridSize || gridY < 0 || gridY >= GridSize) return null;
        return CurrentWorld.Grid[gridY, gridX];
    }

    /// <summary>
    /// Gets the grid position of a zone.
    /// </summary>
    public (int x, int y)? GetZonePosition(int zoneId)
    {
        if (CurrentWorld?.Grid == null) return null;

        for (int y = 0; y < GridSize; y++)
        {
            for (int x = 0; x < GridSize; x++)
            {
                if (CurrentWorld.Grid[y, x] == zoneId)
                    return (x, y);
            }
        }
        return null;
    }

    /// <summary>
    /// Gets the connected zone in a given direction.
    /// </summary>
    public int? GetConnectedZone(int zoneId, Direction direction)
    {
        if (CurrentWorld == null) return null;

        // Check if it's a room - return to parent
        if (CurrentWorld.RoomParents.TryGetValue(zoneId, out var parentZoneId))
        {
            // For rooms, any exit goes back to parent
            return parentZoneId;
        }

        // Check grid connections
        if (CurrentWorld.Connections.TryGetValue(zoneId, out var connections))
        {
            return direction switch
            {
                Direction.Up => connections.North,
                Direction.Down => connections.South,
                Direction.Left => connections.West,
                Direction.Right => connections.East,
                _ => null
            };
        }

        return null;
    }
}

/// <summary>
/// Represents a generated world map.
/// </summary>
public class WorldMap
{
    public Planet Planet { get; set; }
    public Mission? Mission { get; set; }

    // Grid of zone IDs (null = empty cell)
    public int?[,]? Grid { get; set; }

    // Type map - sector type for each grid cell
    public SectorType[,]? TypeMap { get; set; }

    // Order map - puzzle order index for each cell (-1 = no puzzle)
    public int[,]? OrderMap { get; set; }

    // Grid dimensions (10 for Small/Medium/Large, 15 for XtraLarge)
    public int GridWidth { get; set; } = 10;
    public int GridHeight { get; set; } = 10;

    // Special zone IDs
    public int StartingZoneId { get; set; }
    public int LandingZoneId { get; set; }
    public int ObjectiveZoneId { get; set; }
    public int? YodaZoneId { get; set; }

    // Grid positions
    public (int x, int y) LandingPosition { get; set; }
    public (int x, int y) ObjectivePosition { get; set; }

    // Dagobah zones (fixed starting area)
    public List<int> DagobahZones { get; set; } = new();

    // Zone connections (for edge traversal)
    public Dictionary<int, ZoneConnections> Connections { get; set; } = new();

    // Room connections (outdoor zone -> list of indoor room zones)
    public Dictionary<int, List<int>> RoomConnections { get; set; } = new();

    // Room parents (indoor room -> outdoor parent)
    public Dictionary<int, int> RoomParents { get; set; } = new();

    // Items
    public int? StartingItemId { get; set; }
    public List<int> RequiredItems { get; set; } = new();

    // X-Wing location (for travel from Dagobah to planet)
    public int? XWingZoneId { get; set; }

    // Yoda's position within the zone
    public (int x, int y) YodaPosition { get; set; } = (5, 5);

    // The Force location (guaranteed weapon at distance 2)
    public int? TheForceZoneId { get; set; }
    public (int x, int y) TheForcePosition { get; set; }

    // Mission number (1-15)
    public int MissionNumber { get; set; } = 1;

    /// <summary>
    /// Advances the mission to the next step.
    /// </summary>
    public bool AdvanceMission()
    {
        if (Mission == null) return false;

        var currentStep = Mission.CurrentPuzzleStep;
        if (currentStep != null)
        {
            currentStep.IsCompleted = true;
            Mission.CurrentStep++;

            if (Mission.CurrentStep >= Mission.PuzzleChain.Count)
            {
                Mission.IsCompleted = true;
                Console.WriteLine($"=== MISSION {Mission.MissionNumber}/15 COMPLETE: {Mission.Name} ===");
                WorldGenerator.AdvanceMission();
                return true; // Mission complete!
            }
        }
        return false;
    }

    /// <summary>
    /// Gets the current mission objective as a string.
    /// </summary>
    public string GetCurrentObjective()
    {
        if (Mission == null)
            return "Explore the area.";

        if (Mission.IsCompleted)
            return "Mission Complete! Return to Yoda.";

        var step = Mission.CurrentPuzzleStep;
        if (step == null)
            return Mission.Description;

        return !string.IsNullOrEmpty(step.Hint) ? step.Hint : $"Find a way to progress.";
    }

    /// <summary>
    /// Prints a visual representation of the world map to the console.
    /// </summary>
    public void PrintMapVisualization()
    {
        Console.WriteLine($"\n=== WORLD MAP ({GridWidth}x{GridHeight}) ===");
        Console.WriteLine("Legend: · Empty  P Puzzle  S Spaceport  T Travel  I Island  B Blockade  L Landing  *G* Goal");

        if (TypeMap == null || Grid == null)
        {
            Console.WriteLine("No map data available");
            return;
        }

        // Print header row
        Console.Write("   ");
        for (int x = 0; x < GridWidth; x++)
            Console.Write($" {x,2} ");
        Console.WriteLine();

        for (int y = 0; y < GridHeight; y++)
        {
            Console.Write($"{y,2} ");
            for (int x = 0; x < GridWidth; x++)
            {
                var sectorType = TypeMap[y, x];
                var zoneId = Grid[y, x];
                var orderIdx = OrderMap?[y, x] ?? -1;

                // Determine display character
                string cell = GetSectorDisplayString(sectorType, zoneId, orderIdx, x, y);
                Console.Write($"{cell,4}");
            }
            Console.WriteLine();
        }

        // Print statistics
        var stats = GetMapStatistics();
        Console.WriteLine($"\nMission: {Mission?.MissionNumber ?? 0}/15 | Zones: {stats.TotalZones} | Puzzles: {stats.PuzzleCount}");
        Console.WriteLine($"Landing Zone: {LandingZoneId} at ({LandingPosition.x},{LandingPosition.y})");
        if (ObjectiveZoneId > 0)
            Console.WriteLine($"Objective Zone: {ObjectiveZoneId} at ({ObjectivePosition.x},{ObjectivePosition.y})");
        if (TheForceZoneId.HasValue)
            Console.WriteLine($"The Force: Zone {TheForceZoneId} at ({TheForcePosition.x},{TheForcePosition.y})");
        Console.WriteLine();
    }

    private string GetSectorDisplayString(SectorType type, int? zoneId, int orderIdx, int x, int y)
    {
        // Check for special positions
        bool isLanding = (x == LandingPosition.x && y == LandingPosition.y);
        bool isObjective = (x == ObjectivePosition.x && y == ObjectivePosition.y);
        bool isTheForce = (x == TheForcePosition.x && y == TheForcePosition.y && TheForceZoneId.HasValue);

        if (isLanding)
            return "  L  ";
        if (isObjective)
            return " *G* ";
        if (isTheForce)
            return " [F] ";

        return type switch
        {
            SectorType.None => "     ",
            SectorType.Empty => zoneId.HasValue ? $" {zoneId.Value,3} " : "  ·  ",
            SectorType.Candidate => "  ?  ",
            SectorType.Puzzle => orderIdx >= 0 ? $" P{orderIdx,2} " : " P?? ",
            SectorType.Spaceport => "  S  ",
            SectorType.BlockNorth => " B↑  ",
            SectorType.BlockSouth => " B↓  ",
            SectorType.BlockEast => " B→  ",
            SectorType.BlockWest => " B←  ",
            SectorType.TravelStart => " T→  ",
            SectorType.TravelEnd => " ←T  ",
            SectorType.Island => "  I  ",
            SectorType.KeptFree => "  #  ",
            _ => "  ?  "
        };
    }

    /// <summary>
    /// Gets statistics about the generated map.
    /// </summary>
    public (int TotalZones, int PuzzleCount, int BlockadeCount, int TravelCount) GetMapStatistics()
    {
        if (TypeMap == null) return (0, 0, 0, 0);

        int totalZones = 0, puzzles = 0, blockades = 0, travels = 0;

        for (int y = 0; y < GridHeight; y++)
        {
            for (int x = 0; x < GridWidth; x++)
            {
                var type = TypeMap[y, x];
                if (Grid?[y, x] != null) totalZones++;

                switch (type)
                {
                    case SectorType.Puzzle:
                        puzzles++;
                        break;
                    case SectorType.BlockNorth:
                    case SectorType.BlockSouth:
                    case SectorType.BlockEast:
                    case SectorType.BlockWest:
                        blockades++;
                        break;
                    case SectorType.TravelStart:
                        travels++;
                        break;
                }
            }
        }

        return (totalZones, puzzles, blockades, travels);
    }
}

/// <summary>
/// Connections from a zone to adjacent zones.
/// </summary>
public class ZoneConnections
{
    public int ZoneId { get; set; }
    public int? North { get; set; }
    public int? South { get; set; }
    public int? East { get; set; }
    public int? West { get; set; }
}

/// <summary>
/// Represents a mission/quest with its puzzle chain.
/// </summary>
public class Mission
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public Planet Planet { get; set; }
    public Puzzle? GoalPuzzle { get; set; }

    // Mission number in the 15-mission cycle
    public int MissionNumber { get; set; } = 1;

    // The chain of puzzles to complete the mission
    public List<PuzzleStep> PuzzleChain { get; set; } = new();

    // Current progress in the mission
    public int CurrentStep { get; set; } = 0;
    public bool IsCompleted { get; set; } = false;

    /// <summary>
    /// Gets the current puzzle step, or null if mission is complete.
    /// </summary>
    public PuzzleStep? CurrentPuzzleStep =>
        CurrentStep < PuzzleChain.Count ? PuzzleChain[CurrentStep] : null;
}

/// <summary>
/// A single step in a mission's puzzle chain.
/// </summary>
public class PuzzleStep
{
    public Puzzle Puzzle { get; set; } = null!;
    public int RequiredItemId { get; set; }  // Item needed to complete this step
    public int RewardItemId { get; set; }    // Item received upon completion
    public int? ZoneId { get; set; }         // Zone where this puzzle is solved
    public int TargetX { get; set; }         // X position in the zone
    public int TargetY { get; set; }         // Y position in the zone
    public string Hint { get; set; } = "";   // Hint text for the player
    public bool IsCompleted { get; set; } = false;
}
