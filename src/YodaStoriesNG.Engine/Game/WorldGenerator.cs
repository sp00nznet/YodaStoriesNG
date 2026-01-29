using YodaStoriesNG.Engine.Data;

namespace YodaStoriesNG.Engine.Game;

/// <summary>
/// Generates a playable world by assembling zones into a connected map.
/// Based on the Yoda Stories procedural generation system.
/// </summary>
public class WorldGenerator
{
    private readonly GameData _gameData;
    private readonly Random _random = new();

    // World grid (10x10 for planets, smaller for Dagobah)
    public const int GridSize = 10;

    // The generated world
    public WorldMap? CurrentWorld { get; private set; }

    // Current mission
    public Mission? CurrentMission { get; private set; }

    public WorldGenerator(GameData gameData)
    {
        _gameData = gameData;
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
    public WorldMap GenerateWorld()
    {
        // Pick a random mission (goal puzzle)
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

        // Set up item chain
        SetupItemChain();

        return CurrentWorld;
    }

    /// <summary>
    /// Selects a random mission from available goal puzzles and builds the puzzle chain.
    /// </summary>
    private Mission SelectRandomMission()
    {
        // Find goal-type puzzles that define missions
        var goalPuzzles = _gameData.Puzzles
            .Where(p => p.Type == PuzzleType.Goal && p.Strings.Count > 0)
            .ToList();

        // Determine planet (random selection)
        var planets = new[] { Planet.Desert, Planet.Snow, Planet.Forest };
        var planet = planets[_random.Next(planets.Length)];

        if (goalPuzzles.Count == 0)
        {
            // Fallback: create a simple mission with no puzzle chain
            return CreateFallbackMission(planet);
        }

        var goalPuzzle = goalPuzzles[_random.Next(goalPuzzles.Count)];

        var mission = new Mission
        {
            Id = goalPuzzle.Id,
            Name = goalPuzzle.Strings.FirstOrDefault() ?? "Unknown Mission",
            Planet = planet,
            Description = goalPuzzle.Strings.Count > 1 ? goalPuzzle.Strings[1] : "",
            GoalPuzzle = goalPuzzle
        };

        // Build the puzzle chain leading to the goal
        BuildPuzzleChain(mission, goalPuzzle);

        // Log mission details
        Console.WriteLine($"\n=== MISSION: {mission.Name} ===");
        Console.WriteLine($"Planet: {mission.Planet}");
        Console.WriteLine($"Goal: {mission.Description}");
        Console.WriteLine($"Puzzle chain ({mission.PuzzleChain.Count} steps):");
        for (int i = 0; i < mission.PuzzleChain.Count; i++)
        {
            var step = mission.PuzzleChain[i];
            var reqItem = GetItemName(step.RequiredItemId);
            var rewItem = GetItemName(step.RewardItemId);
            Console.WriteLine($"  {i + 1}. [{step.Puzzle.Type}] Need: {reqItem} -> Get: {rewItem}");
            if (!string.IsNullOrEmpty(step.Hint))
                Console.WriteLine($"     Hint: {step.Hint}");
        }
        Console.WriteLine();

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
    /// Builds the puzzle chain for a mission based on the goal puzzle.
    /// </summary>
    private void BuildPuzzleChain(Mission mission, Puzzle goalPuzzle)
    {
        // Get all non-goal puzzles that can be used in the chain
        var tradePuzzles = _gameData.Puzzles.Where(p => p.Type == PuzzleType.Trade).ToList();
        var usePuzzles = _gameData.Puzzles.Where(p => p.Type == PuzzleType.Use).ToList();
        var questPuzzles = _gameData.Puzzles.Where(p => p.Type == PuzzleType.Quest).ToList();

        // The goal puzzle defines what item is needed to complete the mission
        var goalItemId = goalPuzzle.Item1;

        // Work backwards from the goal to build the chain
        var currentNeededItem = goalItemId;
        var usedPuzzles = new HashSet<int>();
        var chainSteps = new List<PuzzleStep>();

        // Add the final goal step
        chainSteps.Add(new PuzzleStep
        {
            Puzzle = goalPuzzle,
            RequiredItemId = goalItemId,
            RewardItemId = 0,  // Mission complete!
            Hint = goalPuzzle.Strings.Count > 1 ? goalPuzzle.Strings[1] : "Complete the mission."
        });

        // Try to find puzzles that give us what we need (working backwards)
        int maxSteps = 5;  // Limit chain length
        for (int i = 0; i < maxSteps && currentNeededItem > 0; i++)
        {
            // Look for a puzzle that rewards the item we need
            Puzzle? sourcePuzzle = null;

            // First try trade puzzles (item2 is reward)
            sourcePuzzle = tradePuzzles
                .Where(p => p.Item2 == currentNeededItem && !usedPuzzles.Contains(p.Id))
                .OrderBy(_ => _random.Next())
                .FirstOrDefault();

            // Then try use puzzles
            if (sourcePuzzle == null)
            {
                sourcePuzzle = usePuzzles
                    .Where(p => p.Item2 == currentNeededItem && !usedPuzzles.Contains(p.Id))
                    .OrderBy(_ => _random.Next())
                    .FirstOrDefault();
            }

            // Then try quest puzzles
            if (sourcePuzzle == null)
            {
                sourcePuzzle = questPuzzles
                    .Where(p => p.Item2 == currentNeededItem && !usedPuzzles.Contains(p.Id))
                    .OrderBy(_ => _random.Next())
                    .FirstOrDefault();
            }

            if (sourcePuzzle != null)
            {
                usedPuzzles.Add(sourcePuzzle.Id);
                chainSteps.Insert(0, new PuzzleStep
                {
                    Puzzle = sourcePuzzle,
                    RequiredItemId = sourcePuzzle.Item1,
                    RewardItemId = sourcePuzzle.Item2,
                    Hint = sourcePuzzle.Strings.FirstOrDefault() ?? ""
                });
                currentNeededItem = sourcePuzzle.Item1;
            }
            else
            {
                // No more puzzles in the chain, this is where Yoda gives the starting item
                break;
            }
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
    /// Generates the main planet grid with connected zones.
    /// </summary>
    private void GeneratePlanetGrid()
    {
        if (CurrentWorld == null || CurrentMission == null) return;

        // Initialize grid
        CurrentWorld.Grid = new int?[GridSize, GridSize];

        // Find zones matching the mission's planet type
        var planetZones = _gameData.Zones
            .Where(z => z.Planet == CurrentMission.Planet && z.Width > 0)
            .ToList();

        if (planetZones.Count == 0)
        {
            Console.WriteLine($"Warning: No zones found for planet {CurrentMission.Planet}");
            return;
        }

        // Categorize zones by type
        var emptyZones = planetZones.Where(z => z.Type == ZoneType.Empty || z.Type == ZoneType.None).ToList();
        var townZones = planetZones.Where(z => z.Type == ZoneType.Town).ToList();
        var goalZones = planetZones.Where(z => z.Type == ZoneType.Goal).ToList();
        var puzzleZones = planetZones.Where(z => z.Type == ZoneType.Trade || z.Type == ZoneType.Use || z.Type == ZoneType.Find).ToList();
        var roomZones = planetZones.Where(z => z.Type == ZoneType.Room).ToList();

        // If no categorization, use all zones
        if (emptyZones.Count == 0) emptyZones = planetZones;

        // 1. Place Landing Cell near center (one of 4 central squares)
        int centerX = GridSize / 2 + _random.Next(-1, 1);
        int centerY = GridSize / 2 + _random.Next(-1, 1);

        var landingZone = (townZones.Count > 0 ? townZones : emptyZones)[_random.Next(Math.Min(townZones.Count, emptyZones.Count) > 0 ? (townZones.Count > 0 ? townZones.Count : emptyZones.Count) : 1)];
        if (townZones.Count > 0)
            landingZone = townZones[_random.Next(townZones.Count)];
        else
            landingZone = emptyZones[_random.Next(emptyZones.Count)];

        CurrentWorld.Grid[centerY, centerX] = landingZone.Id;
        CurrentWorld.LandingZoneId = landingZone.Id;
        CurrentWorld.LandingPosition = (centerX, centerY);

        // Track used zones
        var usedZones = new HashSet<int> { landingZone.Id };

        // 2. Place Objective Cell (min 2 squares away from landing)
        if (goalZones.Count > 0)
        {
            var goalZone = goalZones[_random.Next(goalZones.Count)];
            int goalX, goalY;
            do
            {
                goalX = _random.Next(GridSize);
                goalY = _random.Next(GridSize);
            } while (Math.Abs(goalX - centerX) + Math.Abs(goalY - centerY) < 2 || CurrentWorld.Grid[goalY, goalX] != null);

            CurrentWorld.Grid[goalY, goalX] = goalZone.Id;
            CurrentWorld.ObjectiveZoneId = goalZone.Id;
            CurrentWorld.ObjectivePosition = (goalX, goalY);
            usedZones.Add(goalZone.Id);
        }

        // 3. Fill in connected cells between landing and objective
        // Create a path and fill surrounding area
        int fillCount = _random.Next(15, 30); // 15-30 cells populated
        var availableZones = emptyZones.Concat(puzzleZones).Where(z => !usedZones.Contains(z.Id)).ToList();

        for (int i = 0; i < fillCount && availableZones.Count > 0; i++)
        {
            // Find an empty cell adjacent to an existing cell
            var candidates = new List<(int x, int y)>();
            for (int y = 0; y < GridSize; y++)
            {
                for (int x = 0; x < GridSize; x++)
                {
                    if (CurrentWorld.Grid[y, x] == null && HasAdjacentCell(CurrentWorld.Grid, x, y))
                    {
                        candidates.Add((x, y));
                    }
                }
            }

            if (candidates.Count == 0) break;

            var (px, py) = candidates[_random.Next(candidates.Count)];
            var zone = availableZones[_random.Next(availableZones.Count)];
            CurrentWorld.Grid[py, px] = zone.Id;
            usedZones.Add(zone.Id);
            availableZones.Remove(zone);
        }

        // 4. Set up zone connections (adjacent cells are connected)
        SetupZoneConnections();

        // 5. Associate room zones with their parent outdoor zones (for doors)
        AssociateRoomZones(roomZones, usedZones);

        Console.WriteLine($"Generated planet grid with {usedZones.Count} zones");
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
            }
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
    public string Hint { get; set; } = "";   // Hint text for the player
    public bool IsCompleted { get; set; } = false;
}
