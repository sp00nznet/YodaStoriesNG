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
    private Random _random = new();
    private int _generationSeed;

    // World grid - dynamic based on world size (10 for Small/Medium/Large, 15 for XtraLarge)
    private int _gridSize = 10;

    /// <summary>Current grid size.</summary>
    public int GridSize => _gridSize;

    // Special item tile IDs (from original game)
    public const int TILE_THE_FORCE = 511;      // The Force - guaranteed weapon
    public const int TILE_LOCATOR = 794;        // R2-D2 / Locator item (tile 794 is the actual R2D2 in zones)
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
            if (zone.Monsters.Count > 0)
            {
                Console.WriteLine($"\n  Zone {zone.Id} IZAX:");
                foreach (var m in zone.Monsters)
                {
                    var name = m.CharacterId < _gameData.Characters.Count ? _gameData.Characters[m.CharacterId].Name : "?";
                    Console.WriteLine($"    {name} (char {m.CharacterId}) at ({m.X},{m.Y}), loot {m.Loot}");
                }
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
    /// Generates a new world with a random mission.
    /// </summary>
    public WorldMap GenerateWorld() => GenerateWorld(WorldSize.Medium);

    /// <summary>
    /// Generates a new world with the specified size.
    /// </summary>
    public WorldMap GenerateWorld(WorldSize size)
    {
        _generationSeed = int.TryParse(Environment.GetEnvironmentVariable("YSNG_SEED"),out int configuredSeed)
            ? configuredSeed : Random.Shared.Next();
        _random = new Random(_generationSeed);
        Console.WriteLine($"[WORLD] Seed={_generationSeed}, game={_gameData.GameType}, size={size}");
        if (_gameData.GameType == GameType.IndianaJones || _gameData.GameType == GameType.YodaStories)
            return GenerateOriginalWorld(size);

        // Set grid size based on world size
        _gridSize = size == WorldSize.XtraLarge ? 15 : 10;

        // Pick a random mission (goal puzzle) - but don't build puzzle chain yet
        CurrentMission = SelectRandomMission();
        Console.WriteLine($"Selected mission: {CurrentMission.Name} on {CurrentMission.Planet}");

        // Create the world map
        CurrentWorld = new WorldMap
        {
            Planet = CurrentMission.Planet,
            Seed = _generationSeed,
            GenerationSize = size,
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
    /// Indiana Jones: the original's algorithm (<see cref="OriginalWorldGenerator"/>),
    /// reseeding when a seed cannot be completed, as the original does. The result is
    /// dressed as the same WorldMap the rest of the engine, the bot and the map viewer use.
    /// </summary>
    private WorldMap GenerateOriginalWorld(WorldSize size)
    {
        _gridSize = 10;

        // Planet choice, retry seeds, and auxiliary placements share the recorded seed.
        var seeds = _random;

        OriginalWorldGenerator? generator = null;
        var missionPlanet = _gameData.GameType == GameType.IndianaJones
            ? Planet.None
            : new[] { Planet.Desert, Planet.Snow, Planet.Forest }[_random.Next(3)];
        for (int attempt = 1; generator == null; attempt++)
        {
            var candidate = new OriginalWorldGenerator(_gameData, seeds.Next());
            try
            {
                candidate.Generate(missionPlanet, size);
                generator = candidate;
            }
            catch (WorldGenerationException ex) when (attempt < 50)
            {
                Console.WriteLine($"[WORLD] Reseeding after attempt {attempt}: {ex.Message}");
            }
        }

        // Items and NPCs live in hotspot arguments; start from the file's, then apply this world's.
        foreach (var zone in _gameData.Zones)
            foreach (var hotspot in zone.Objects)
            {
                hotspot.Argument = hotspot.FileArgument;
                hotspot.Enabled = ZoneObject.EnabledByDefault(hotspot.Type);
            }
        foreach (var (zoneId, arguments) in generator.HotspotArguments)
            for (int i = 0; i < arguments.Length; i++)
            {
                var hotspot = _gameData.Zones[zoneId].Objects[i];
                // Selecting a drop enables it even when the authored argument
                // already equals the generated reward. Unselected template
                // alternatives must remain disabled.
                if (hotspot.Argument == (ushort)arguments[i] &&
                    !generator.PopulatedHotspots.Contains((zoneId,i))) continue;
                hotspot.Argument = (ushort)arguments[i];
                hotspot.Enabled = true; // the generator put something here
            }

        var world = new WorldMap
        {
            Planet = missionPlanet,
            Seed = _generationSeed,
            GenerationSize = size,
            Grid = new int?[10, 10],
            TypeMap = new SectorType[10, 10],
            OrderMap = new int[10, 10],
        };
        CurrentWorld = world;

        var outdoorZones = new HashSet<int>();
        for (int y = 0; y < 10; y++)
        for (int x = 0; x < 10; x++)
        {
            var sector = generator.Sectors[x + 10 * y];
            world.TypeMap[y, x] = generator.Map.TypeMap[x + 10 * y];
            world.OrderMap[y, x] = sector.PuzzleIndex;
            if (sector.Zone < 0) continue;

            world.Grid[y, x] = sector.Zone;
            outdoorZones.Add(sector.Zone);
            // Scripts ask the sector about any zone in it, rooms included.
            foreach (var zoneId in ZoneAndRooms(sector.Zone))
                world.Sectors.TryAdd(zoneId, sector);

            if (sector.Type == ZoneType.Town)
            {
                // Indy starts in the town. Yoda starts on Dagobah and flies from there
                // to the generated planet's spaceport.
                if (_gameData.GameType == GameType.IndianaJones)
                    world.StartingZoneId = sector.Zone;
                world.LandingZoneId = sector.Zone;
                world.LandingPosition = (x, y);
            }
            if (sector.Type == ZoneType.Goal)
            {
                world.ObjectiveZoneId = sector.Zone;
                world.ObjectivePosition = (x, y);
            }
            if (sector.RequiredItem >= 0) world.RequiredItems.Add(sector.RequiredItem);
        }

        SetupZoneConnections();
        AssociateRoomZones(new List<Zone>(), outdoorZones);
        RepairUnreachableBlockadeLocks(world,outdoorZones);
        RepairUnreachableDoorInputs(world,outdoorZones);
        RepairUnreachableSectorInteractions(world);
        RepairUnreachableOutdoorConnections(world,outdoorZones);

        world.Strain1 = generator.Strain1ByIndex;
        world.Strain2 = generator.Strain2ByIndex;
        world.Mission = CurrentMission = BuildOriginalMission(generator, missionPlanet);
        world.Mission.MissionNumber = _currentMissionNumber;
        world.MissionNumber = _currentMissionNumber;
        if (_gameData.GameType == GameType.YodaStories)
        {
            GenerateDagobah();
            world.XWingZoneId = world.StartingZoneId;
            SetupItemChain();
        }
        LogMissionDetails(CurrentMission);
        world.PrintMapVisualization();
        return world;
    }

    /// <summary>Use authored mission text instead of internal ending-item names.</summary>
    internal static string GetMissionTitle(Puzzle puzzle)
    {
        // Mission string 0 is often empty; string 2 is the authored objective.
        // Item1 can be an internal ending token (e.g. END1A), not a display name.
        foreach (int index in new[] { 0, 2, 3 })
            if (index < puzzle.Strings.Count && !string.IsNullOrWhiteSpace(puzzle.Strings[index]))
                return puzzle.Strings[index].Trim();
        return "Mission";
    }

    /// <summary>
    /// The puzzle chain in the order it is played: find the first item, then each puzzle
    /// sector by index, ending at the goal zone that hands over the mission's item.
    /// </summary>
    private Mission BuildOriginalMission(OriginalWorldGenerator generator, Planet planet)
    {
        var goal = generator.GoalPuzzle;
        string Text(Puzzle p, params int[] order) =>
            order.Select(i => i < p.Strings.Count ? p.Strings[i] : "").FirstOrDefault(t => t.Length > 0) ?? "";

        var mission = new Mission
        {
            Id = goal.Id,
            Name = GetMissionTitle(goal),
            Planet = planet,
            Description = Text(goal, 2, 3, 0),
            GoalPuzzle = goal,
        };

        var steps = generator.Sectors.Where(s => s.PuzzleIndex >= 0 && s.Zone >= 0).OrderBy(s => s.PuzzleIndex).ToList();
        int firstItem = steps.Count > 0 ? steps[0].RequiredItem : -1;
        var town = generator.Sectors.FirstOrDefault(s => s.Type == ZoneType.Town);
        var finder = generator.Sectors.FirstOrDefault(s => s.Type == ZoneType.Find && s.FindItem == firstItem && firstItem >= 0);
        if (_gameData.GameType == GameType.IndianaJones && town != null)
            mission.PuzzleChain.Add(new PuzzleStep
            {
                Puzzle = goal,
                RewardItemId = firstItem,
                ZoneId = town.Zone,
                Hint = "See Marcus in his office.",
            });
        else if (finder != null)
            mission.PuzzleChain.Add(new PuzzleStep
            {
                Puzzle = goal,
                RewardItemId = firstItem,
                ZoneId = finder.Zone,
                Hint = $"Find the {GetItemName(firstItem)}.",
            });

        foreach (var sector in steps)
        {
            var strain = sector.UsedAlternateStrain ? generator.Strain1ByIndex : generator.Strain2ByIndex;
            var puzzle = sector.PuzzleIndex < strain.Count ? strain[sector.PuzzleIndex] : goal;
            mission.PuzzleChain.Add(new PuzzleStep
            {
                Puzzle = puzzle,
                RequiredItemId = Math.Max(0, sector.RequiredItem),
                RewardItemId = Math.Max(0, sector.FindItem),
                ZoneId = sector.Zone,
                // Transaction and Goal puzzles open with the NPC saying what they need; an
                // Offer puzzle's only line is its giver's, which would name the wrong item here.
                Hint = puzzle.Type is PuzzleType.Transaction or PuzzleType.Goal
                    ? Text(puzzle, 0)
                    : $"Use the {GetItemName(sector.RequiredItem)} here.",
            });
        }

        return mission;
    }

    /// <summary>A zone and every room behind its doors.</summary>
    private IEnumerable<int> ZoneAndRooms(int zoneId)
    {
        var seen = new HashSet<int>();
        var pending = new Stack<int>();
        pending.Push(zoneId);
        while (pending.Count > 0)
        {
            var id = pending.Pop();
            if (id < 0 || id >= _gameData.Zones.Count || !seen.Add(id)) continue;
            yield return id;
            foreach (var door in _gameData.Zones[id].Objects.Where(o => o.Type == ZoneObjectType.DoorEntrance))
                pending.Push(door.Argument);
        }
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
    /// Selects a random mission from available puzzles and builds the puzzle chain.
    /// Tracks used puzzles to ensure variety across the 15-mission cycle.
    /// </summary>
    private Mission SelectRandomMission()
    {
        // Find Quest-type puzzles that have meaningful strings (Quest type is most common: 73 puzzles)
        // First try puzzles not yet used in this cycle
        var questPuzzles = _gameData.Puzzles
            .Where(p => p.Type == PuzzleType.Transaction && p.Strings.Count > 0 && p.Strings.Any(s => !string.IsNullOrEmpty(s)))
            .Where(p => !_usedGoalPuzzles.Contains(p.Id))
            .ToList();

        // If all puzzles used, allow reuse (shuffle for variety)
        if (questPuzzles.Count == 0)
        {
            questPuzzles = _gameData.Puzzles
                .Where(p => p.Type == PuzzleType.Transaction && p.Strings.Count > 0 && p.Strings.Any(s => !string.IsNullOrEmpty(s)))
                .OrderBy(_ => _random.Next())
                .ToList();
            _usedGoalPuzzles.Clear();  // Reset tracking when cycling
        }

        Console.WriteLine($"[Mission] {questPuzzles.Count} available quest puzzles, {_usedGoalPuzzles.Count} used");

        // Randomly select planet for variety
        var planets = new[] { Planet.Desert, Planet.Snow, Planet.Forest };
        var planet = planets[_random.Next(planets.Length)];

        if (questPuzzles.Count == 0)
        {
            // Fallback: create a simple mission with no puzzle chain
            return CreateFallbackMission(planet);
        }

        var questPuzzle = questPuzzles[_random.Next(questPuzzles.Count)];
        _usedGoalPuzzles.Add(questPuzzle.Id);

        // Filter out corrupted strings (containing IPUZ markers or being too long)
        var cleanStrings = questPuzzle.Strings
            .Where(s => !string.IsNullOrEmpty(s) && !s.Contains("IPUZ") && s.Length < 200)
            .ToList();

        // Extract a clean mission name (first sentence or first 60 chars)
        var rawName = GetMissionTitle(questPuzzle);
        var missionName = rawName.Length > 60 ? rawName.Substring(0, 60) + "..." : rawName;
        if (missionName.Contains("."))
            missionName = missionName.Substring(0, missionName.IndexOf('.') + 1);

        var mission = new Mission
        {
            Id = questPuzzle.Id,
            Name = missionName,
            Planet = planet,
            Description = cleanStrings.Count > 1 ? cleanStrings[1] : "",
            GoalPuzzle = questPuzzle,
            MissionNumber = _currentMissionNumber
        };

        Console.WriteLine($"Mission {_currentMissionNumber}/15: {mission.Name} on {planet} (quest puzzle #{questPuzzle.Id})");

        // NOTE: Puzzle chain will be built later in GenerateWorld() after grid is generated

        return mission;
    }

    /// <summary>
    /// Creates a fallback mission when no goal puzzles are found.
    /// </summary>
    private Mission CreateFallbackMission(Planet planet)
    {
        // Random mission names for variety
        var missionNames = new[]
        {
            "Find the Lost Artifact",
            "Retrieve the Stolen Plans",
            "Rescue the Captured Rebel",
            "Recover the Ancient Relic",
            "Locate the Hidden Base",
            "Find the Missing Droid",
            "Discover the Secret Weapon",
            "Track the Imperial Spy"
        };

        var mission = new Mission
        {
            Id = _random.Next(1000, 9999),
            Name = missionNames[_random.Next(missionNames.Length)],
            Planet = planet,
            Description = "Complete the mission by exploring the planet.",
            GoalPuzzle = null
        };

        // Create a simple 3-step chain using RANDOM items (shuffle first)
        var itemTiles = _gameData.Tiles.Where(t => t.IsItem)
            .OrderBy(_ => _random.Next())
            .Take(10).ToList();
        if (itemTiles.Count >= 3)
        {
            // Step 1: Trade starting item for intermediate item
            mission.PuzzleChain.Add(new PuzzleStep
            {
                Puzzle = new Puzzle { Type = PuzzleType.Transaction, Strings = { "Find a trader" } },
                RequiredItemId = itemTiles[0].Id,
                RewardItemId = itemTiles[1].Id,
                Hint = "Find someone willing to trade."
            });

            // Step 2: Use intermediate item to get key
            mission.PuzzleChain.Add(new PuzzleStep
            {
                Puzzle = new Puzzle { Type = PuzzleType.Offer, Strings = { "Use the item" } },
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
            {
                foreach (var entity in zone.Monsters)
                {
                    if (entity.Loot > 0)
                    {
                        itemLocations.Add((entity.Loot - 1, zone.Id, entity.X, entity.Y));
                    }
                    if (entity.CharacterId > 0 && entity.CharacterId != 0xFFFF)
                    {
                        npcZones.Add((zone.Id, entity.CharacterId, entity.X, entity.Y));
                    }
                }
            }
        }

        // Shuffle and take a subset - use current time to ensure different results each game
        itemLocations = itemLocations.OrderBy(_ => _random.Next()).ToList();
        npcZones = npcZones.OrderBy(_ => _random.Next()).ToList();

        // Take a random subset (3-5 items for varied chain lengths)
        int itemCount = Math.Min(itemLocations.Count, 3 + _random.Next(3));
        itemLocations = itemLocations.Take(itemCount).ToList();

        Console.WriteLine($"BuildPuzzleChain: Selected {itemLocations.Count} items, {npcZones.Count} NPCs for this run");

        // Build a puzzle chain with specific zone locations
        if (itemLocations.Count >= 2)
        {
            // Step 1: Find first item at a specific location
            var firstItem = itemLocations[0];
            chainSteps.Add(new PuzzleStep
            {
                Puzzle = new Puzzle { Type = PuzzleType.Transaction, Strings = { "Find an item" } },
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
                    Puzzle = new Puzzle { Type = PuzzleType.Transaction, Strings = { "Trade with someone" } },
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

        var pending = new Queue<int>(usedOutdoorZones);
        var seen = new HashSet<int>();
        while (pending.TryDequeue(out int outdoorZoneId))
        {
            if (!seen.Add(outdoorZoneId)) continue;
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

                    if (!CurrentWorld.RoomConnections[outdoorZoneId].Contains(destZoneId))
                        CurrentWorld.RoomConnections[outdoorZoneId].Add(destZoneId);

                    // Also track the reverse (room -> outdoor)
                    if (!usedOutdoorZones.Contains(destZoneId))
                        CurrentWorld.RoomParents.TryAdd(destZoneId, outdoorZoneId);
                    pending.Enqueue(destZoneId);
                }
            }
        }
    }

    // Some blockade templates put their Lock hotspot inside a section sealed
    // off by unrelated wall tiles. When that happens the required item cannot
    // be used from either side. Move the authored lock onto the shortest
    // boundary-to-boundary opening and make its existing unlock animation
    // clear the remaining tiles in that crossing.
    // Generated worlds connect neighboring area IDs at their map borders. Some
    // authored sector layouts have a sealed building wall between the room door
    // and every border lane, leaving a graph edge that the player cannot use.
    // Open a short, unscripted passage from a real arrival side to one valid
    // crossing lane so each generated connection is physically traversable.
    private void RepairUnreachableOutdoorConnections(WorldMap world,HashSet<int> outdoorZones)
    {
        foreach(int zoneId in outdoorZones)
        {
            if(!world.Connections.TryGetValue(zoneId,out var links)) continue;
            var zone=_gameData.Zones[zoneId];
            bool Open(Zone z,int x,int y)
            {
                if(x<0 || y<0 || x>=z.Width || y>=z.Height) return false;
                if(z.Objects.Any(h=>h.Enabled && h.X==x && h.Y==y && h.Type is
                    ZoneObjectType.DoorEntrance or ZoneObjectType.DoorExit or ZoneObjectType.Teleporter or
                    ZoneObjectType.VehicleToPrimary or ZoneObjectType.VehicleToSecondary or
                    ZoneObjectType.XWingFromDagobah or ZoneObjectType.XWingToDagobah)) return false;
                int tile=z.GetTile(x,y,1);
                return tile>=_gameData.Tiles.Count || !_gameData.Tiles[tile].IsObject;
            }
            bool ScriptedCell(int x,int y) => zone.Objects.Any(h=>h.X==x && h.Y==y) || zone.Actions.Any(a=>
                a.Conditions.Any(c=>c.Arguments.Count>=2 && c.Arguments[0]==x && c.Arguments[1]==y &&
                    c.Opcode is ConditionOpcode.Bump or ConditionOpcode.PlacedItemIs or ConditionOpcode.StandingOn));

            var starts=new HashSet<(int X,int Y)>();
            void AddEntrySide(Direction side)
            {
                int n=side is Direction.Left or Direction.Right ? zone.Height : zone.Width;
                for(int i=1;i<n-1;i++)
                {
                    var p=side switch
                    {
                        Direction.Up=>(i,1), Direction.Down=>(i,zone.Height-2),
                        Direction.Left=>(1,i), _=>(zone.Width-2,i),
                    };
                    if(Open(zone,p.Item1,p.Item2)) starts.Add(p);
                }
            }
            if(links.North.HasValue) AddEntrySide(Direction.Down);
            if(links.South.HasValue) AddEntrySide(Direction.Up);
            if(links.West.HasValue) AddEntrySide(Direction.Right);
            if(links.East.HasValue) AddEntrySide(Direction.Left);
            foreach(var door in zone.Objects.Where(h=>h.Enabled && h.Type==ZoneObjectType.DoorEntrance))
            foreach(var (dx,dy) in new[]{(0,1),(1,0),(0,-1),(-1,0)})
                if(Open(zone,door.X+dx,door.Y+dy)) starts.Add((door.X+dx,door.Y+dy));
            foreach(var spawn in zone.Objects.Where(h=>h.Type==ZoneObjectType.SpawnLocation))
            {
                if(Open(zone,spawn.X,spawn.Y)) starts.Add((spawn.X,spawn.Y));
                foreach(var (dx,dy) in new[]{(0,1),(1,0),(0,-1),(-1,0)})
                    if(Open(zone,spawn.X+dx,spawn.Y+dy)) starts.Add((spawn.X+dx,spawn.Y+dy));
            }
            if(starts.Count==0) continue;

            IEnumerable<(Direction Direction,int Destination)> exits()
            {
                if(links.North is int n) yield return (Direction.Up,n);
                if(links.South is int s) yield return (Direction.Down,s);
                if(links.West is int w) yield return (Direction.Left,w);
                if(links.East is int e) yield return (Direction.Right,e);
            }
            foreach(var (direction,destinationId) in exits())
            {
                if(destinationId<0 || destinationId>=_gameData.Zones.Count) continue;
                var dest=_gameData.Zones[destinationId];
                var candidates=new List<(int X,int Y)>();
                int length=direction is Direction.Up or Direction.Down ? zone.Width : zone.Height;
                for(int i=1;i<length-1;i++)
                {
                    var from=direction switch
                    {
                        Direction.Up=>(i,0),Direction.Down=>(i,zone.Height-1),
                        Direction.Left=>(0,i),_=>(zone.Width-1,i),
                    };
                    int along=Math.Clamp(i,1,direction is Direction.Up or Direction.Down ? dest.Width-2 : dest.Height-2);
                    var entry=direction switch
                    {
                        Direction.Up=>(along,dest.Height-1),Direction.Down=>(along,0),
                        Direction.Left=>(dest.Width-1,along),_=>(0,along),
                    };
                    var landing=direction switch
                    {
                        Direction.Up=>(along,dest.Height-2),Direction.Down=>(along,1),
                        Direction.Left=>(dest.Width-2,along),_=>(1,along),
                    };
                    if(!Open(dest,landing.Item1,landing.Item2)) landing=entry;
                    if(Open(dest,entry.Item1,entry.Item2) && Open(dest,landing.Item1,landing.Item2) && Open(zone,from.Item1,from.Item2))
                        candidates.Add(from);
                }
                if(candidates.Count==0) continue;

                bool Reached((int X,int Y) start,HashSet<(int X,int Y)> goals)
                {
                    var seen=new HashSet<(int X,int Y)> { start };
                    var pending=new Queue<(int X,int Y)>(); pending.Enqueue(start);
                    while(pending.TryDequeue(out var at))
                    {
                        if(goals.Contains(at)) return true;
                        foreach(var (dx,dy) in new[]{(0,1),(1,0),(0,-1),(-1,0)})
                        {
                            var next=(at.X+dx,at.Y+dy);
                            if(Open(zone,next.Item1,next.Item2) && seen.Add(next)) pending.Enqueue(next);
                        }
                    }
                    return false;
                }
                var candidateSet=candidates.ToHashSet();
                foreach(var start in starts)
                {
                    if(Reached(start,candidateSet)) continue;
                    var costs=new Dictionary<(int X,int Y),int> { [start]=0 };
                    var previous=new Dictionary<(int X,int Y),(int X,int Y)>();
                    var queue=new PriorityQueue<(int X,int Y),int>(); queue.Enqueue(start,0);
                    (int X,int Y)? end=null;
                    while(queue.TryDequeue(out var at,out int cost))
                    {
                        if(cost!=costs[at]) continue;
                        if(candidateSet.Contains(at)) { end=at; break; }
                        foreach(var (dx,dy) in new[]{(0,1),(1,0),(0,-1),(-1,0)})
                        {
                            var next=(at.X+dx,at.Y+dy);
                            if(next.Item1<0 || next.Item2<0 || next.Item1>=zone.Width || next.Item2>=zone.Height ||
                                !Open(zone,next.Item1,next.Item2) && ScriptedCell(next.Item1,next.Item2)) continue;
                            int tile=zone.GetTile(next.Item1,next.Item2,1);
                            if(!Open(zone,next.Item1,next.Item2) && tile<_gameData.Tiles.Count && _gameData.Tiles[tile].IsDraggable) continue;
                            int nextCost=cost+(Open(zone,next.Item1,next.Item2)?1:8);
                            if(nextCost>=costs.GetValueOrDefault(next,int.MaxValue)) continue;
                            costs[next]=nextCost; previous[next]=at; queue.Enqueue(next,nextCost);
                        }
                    }
                    if(end==null) continue;
                    var path=new List<(int X,int Y)>();
                    for(var at=end.Value;;at=previous[at]) { path.Add(at); if(!previous.ContainsKey(at)) break; }
                    path.Reverse();
                    var walls=path.Where(p=>!Open(zone,p.X,p.Y)).Distinct().ToList();
                    if(walls.Count is 0 or >4) continue;
                    foreach(var wall in walls) zone.SetTile(wall.X,wall.Y,1,ushort.MaxValue);
                    Console.WriteLine($"[WORLD] Opened {walls.Count} wall tile(s) to connect zone {zoneId} to {destinationId} ({direction}) from ({start.X},{start.Y})");
                }
            }
        }
    }

    private void RepairUnreachableBlockadeLocks(WorldMap world,HashSet<int> outdoorZones)
    {
        foreach(int zoneId in outdoorZones)
        {
            var sector=world.Sectors.GetValueOrDefault(zoneId);
            if(sector?.Type is not (ZoneType.BlockadeWest or ZoneType.BlockadeEast or
                ZoneType.BlockadeNorth or ZoneType.BlockadeSouth)) continue;
            var zone=_gameData.Zones[zoneId];
            var lockHotspot=zone.Objects.FirstOrDefault(h=>h.Enabled && h.Type==ZoneObjectType.Lock);
            if(lockHotspot==null) continue;
            int oldX=lockHotspot.X,oldY=lockHotspot.Y;
            var open=new bool[zone.Width,zone.Height];
            for(int y=0;y<zone.Height;y++) for(int x=0;x<zone.Width;x++)
            {
                int tile=zone.GetTile(x,y,1);
                open[x,y]=tile>=_gameData.Tiles.Count ||
                    !_gameData.Tiles[tile].IsObject;
            }
            var (entrySide,exitSide)=sector.Type switch
            {
                ZoneType.BlockadeWest=>(Direction.Right,Direction.Left),
                ZoneType.BlockadeEast=>(Direction.Left,Direction.Right),
                ZoneType.BlockadeNorth=>(Direction.Down,Direction.Up),
                _=>(Direction.Up,Direction.Down),
            };
            IEnumerable<(int X,int Y)> EdgeCells(Direction side)
            {
                for(int along=1;along<(side is Direction.Left or Direction.Right ? zone.Height : zone.Width)-1;along++)
                    yield return side switch
                    {
                        Direction.Left=>(1,along),Direction.Right=>(zone.Width-2,along),
                        Direction.Up=>(along,1),_=>(along,zone.Height-2),
                    };
            }
            bool LockReachableFromEntry()
            {
                var pending=new Queue<(int X,int Y)>(); var seen=new HashSet<(int X,int Y)>();
                foreach(var p in EdgeCells(entrySide)) if(open[p.X,p.Y] && seen.Add(p)) pending.Enqueue(p);
                while(pending.TryDequeue(out var at))
                {
                    if(Math.Abs(at.X-oldX)+Math.Abs(at.Y-oldY)==1) return true;
                    foreach(var (dx,dy) in new[]{(0,1),(1,0),(0,-1),(-1,0)})
                    {
                        var next=(at.X+dx,at.Y+dy);
                        if(next.Item1<0 || next.Item2<0 || next.Item1>=zone.Width || next.Item2>=zone.Height ||
                            !open[next.Item1,next.Item2] || !seen.Add(next)) continue;
                        pending.Enqueue(next);
                    }
                }
                return false;
            }
            if(LockReachableFromEntry()) continue;

            var costs=new Dictionary<(int X,int Y),int>();
            var previous=new Dictionary<(int X,int Y),(int X,int Y)>();
            var queue=new PriorityQueue<(int X,int Y),int>();
            foreach(var p in EdgeCells(entrySide)) if(open[p.X,p.Y] && costs.TryAdd(p,0)) queue.Enqueue(p,0);
            (int X,int Y)? end=null;
            while(queue.TryDequeue(out var at,out int cost))
            {
                if(cost!=costs[at]) continue;
                if(EdgeCells(exitSide).Contains(at)) { end=at; break; }
                foreach(var (dx,dy) in new[]{(0,1),(1,0),(0,-1),(-1,0)})
                {
                    var next=(at.X+dx,at.Y+dy);
                    if(next.Item1<0 || next.Item2<0 || next.Item1>=zone.Width || next.Item2>=zone.Height) continue;
                    int nextCost=cost+(open[next.Item1,next.Item2]?1:5);
                    if(nextCost>=costs.GetValueOrDefault(next,int.MaxValue)) continue;
                    costs[next]=nextCost; previous[next]=at; queue.Enqueue(next,nextCost);
                }
            }
            if(end==null) continue;
            var path=new List<(int X,int Y)>();
            for(var at=end.Value;;at=previous[at]) { path.Add(at); if(!previous.ContainsKey(at)) break; }
            path.Reverse();
            var blockers=path.Where(p=>!open[p.X,p.Y]).Distinct().ToList();
            if(blockers.Count==0 || blockers.Count>4) continue;
            var gate=blockers[0];
            ushort lockTile=zone.GetTile(oldX,oldY,1);
            if(lockTile>=_gameData.Tiles.Count || !_gameData.Tiles[lockTile].IsObject) continue;
            int unlockAction=zone.Actions.FindIndex(a=>a.Instructions.Any(i=>
                i.Opcode==InstructionOpcode.RemoveTile && i.Arguments.Count>=3 &&
                i.Arguments[0]==oldX && i.Arguments[1]==oldY && i.Arguments[2]==1));
            if(unlockAction<0) continue;

            foreach(var action in zone.Actions)
            {
                foreach(var condition in action.Conditions)
                {
                    var a=condition.Arguments;
                    if(a.Count<2) continue;
                    int xIndex=condition.Opcode==ConditionOpcode.TileAtIs?1:0;
                    int yIndex=condition.Opcode==ConditionOpcode.TileAtIs?2:1;
                    if((condition.Opcode is ConditionOpcode.Bump or ConditionOpcode.Standing or
                        ConditionOpcode.StandingOn or ConditionOpcode.PlacedItemIs or ConditionOpcode.TileAtIs) &&
                        a[xIndex]==oldX && a[yIndex]==oldY)
                    { a[xIndex]=(short)gate.X; a[yIndex]=(short)gate.Y; }
                }
                foreach(var instruction in action.Instructions)
                {
                    var a=instruction.Arguments;
                    if(a.Count<2) continue;
                    if(instruction.Opcode is InstructionOpcode.MoveTile && a.Count>=5)
                    {
                        if(a[0]==oldX && a[1]==oldY) { a[0]=(short)gate.X; a[1]=(short)gate.Y; }
                        if(a[3]==oldX && a[4]==oldY) { a[3]=(short)gate.X; a[4]=(short)gate.Y; }
                    }
                    else if(instruction.Opcode is InstructionOpcode.PlaceTile or InstructionOpcode.RemoveTile or
                        InstructionOpcode.SetTileNeedsDisplay or InstructionOpcode.DrawTile or
                        InstructionOpcode.MoveHeroTo)
                    {
                        if(a[0]==oldX && a[1]==oldY) { a[0]=(short)gate.X; a[1]=(short)gate.Y; }
                    }
                }
            }
            zone.SetTile(oldX,oldY,1,ushort.MaxValue);
            zone.SetTile(gate.X,gate.Y,1,lockTile);
            lockHotspot.X=(ushort)gate.X; lockHotspot.Y=(ushort)gate.Y;
            foreach(var p in blockers.Skip(1))
                zone.Actions[unlockAction].Instructions.Add(new Instruction
                { Opcode=InstructionOpcode.RemoveTile,Arguments={(short)p.X,(short)p.Y,1} });
            Console.WriteLine($"[WORLD] Repaired unreachable {sector.Type} lock in zone {zoneId}: " +
                $"({oldX},{oldY}) -> ({gate.X},{gate.Y}), clears {blockers.Count} barrier tiles");
        }
    }

    // Native rooms sometimes put the bump controls for a locked doorway behind
    // a complete wall. If an authored counter chain proves those controls are
    // required to open an enabled door, open the shortest wall path from a real
    // connected edge to each control. This keeps the scripts and the intended
    // puzzle intact while making the authored controls reachable.
    private void RepairUnreachableDoorInputs(WorldMap world,HashSet<int> outdoorZones)
    {
        foreach(int zoneId in outdoorZones)
        {
            var zone=_gameData.Zones[zoneId];
            if(!world.Connections.TryGetValue(zoneId,out var connections)) continue;
            var starts=new HashSet<(int X,int Y)>();
            void AddEdge(Direction direction)
            {
                bool vertical=direction is Direction.Up or Direction.Down;
                int count=vertical?zone.Width:zone.Height;
                for(int along=1;along<count-1;along++)
                {
                    var p=direction switch
                    {
                        Direction.Up=>(along,0),Direction.Down=>(along,zone.Height-1),
                        Direction.Left=>(0,along),_=>(zone.Width-1,along),
                    };
                    starts.Add(p);
                }
            }
            if(connections.North.HasValue) AddEdge(Direction.Up);
            if(connections.South.HasValue) AddEdge(Direction.Down);
            if(connections.West.HasValue) AddEdge(Direction.Left);
            if(connections.East.HasValue) AddEdge(Direction.Right);
            if(starts.Count==0) continue;

            bool Open(int x,int y)
            {
                if(x<0 || y<0 || x>=zone.Width || y>=zone.Height) return false;
                int id=zone.GetTile(x,y,1);
                return id>=_gameData.Tiles.Count || !_gameData.Tiles[id].IsObject;
            }
            var doorActions=zone.Objects.Where(h=>h.Enabled && h.Type==ZoneObjectType.DoorEntrance)
                .SelectMany(door=>zone.Actions.Select((action,index)=>(door,action,index))
                    .Where(p=>p.action.Conditions.Any(c=>c.Opcode==ConditionOpcode.Bump && c.Arguments.Count>=3 &&
                            c.Arguments[0]==p.door.X && c.Arguments[1]==p.door.Y) &&
                        p.action.Instructions.Any(i=>i.Opcode==InstructionOpcode.RemoveTile && i.Arguments.Count>=3 &&
                            i.Arguments[0]==p.door.X && i.Arguments[1]==p.door.Y && i.Arguments[2]==1)))
                .ToList();
            foreach(var gate in doorActions)
            foreach(var counterGuard in gate.action.Conditions.Where(c=>c.Opcode==ConditionOpcode.CounterIs && c.Arguments.Count>0))
            foreach(var counterProducer in zone.Actions.Where(a=>a.Instructions.Any(i=>
                i.Opcode==InstructionOpcode.SetCounter && i.Arguments.Count>0 && i.Arguments[0]==counterGuard.Arguments[0])))
            foreach(var tileGuard in counterProducer.Conditions.Where(c=>c.Opcode==ConditionOpcode.TileAtIs && c.Arguments.Count>=4))
            {
                int expected=unchecked((ushort)tileGuard.Arguments[0]);
                int cellX=tileGuard.Arguments[1],cellY=tileGuard.Arguments[2],layer=tileGuard.Arguments[3];
                var inputs=zone.Actions.SelectMany((action,index)=>action.Instructions
                    .Where(i=>i.Opcode==InstructionOpcode.PlaceTile && i.Arguments.Count>=4 &&
                        i.Arguments[0]==cellX && i.Arguments[1]==cellY && i.Arguments[2]==layer && i.Arguments[3]==expected)
                    .SelectMany(_=>action.Conditions.Where(c=>c.Opcode==ConditionOpcode.Bump && c.Arguments.Count>=3)
                        .Select(c=>(Action:action,Index:index,X:(int)c.Arguments[0],Y:(int)c.Arguments[1],Tile:(int)c.Arguments[2]))))
                    .ToList();
                foreach(var input in inputs)
                {
                    if(input.X<0 || input.Y<0 || input.X>=zone.Width || input.Y>=zone.Height ||
                        zone.GetTile(input.X,input.Y,1)!=unchecked((ushort)input.Tile)) continue;
                    bool CanApproach()
                    {
                        var seen=new HashSet<(int X,int Y)>(); var pending=new Queue<(int X,int Y)>();
                        foreach(var p in starts) if(Open(p.X,p.Y) && seen.Add(p)) pending.Enqueue(p);
                        while(pending.TryDequeue(out var at))
                        {
                            if(Math.Abs(at.X-input.X)+Math.Abs(at.Y-input.Y)==1) return true;
                            foreach(var (dx,dy) in new[]{(0,1),(1,0),(0,-1),(-1,0)})
                            {
                                int nx=at.X+dx,ny=at.Y+dy;
                                if(!Open(nx,ny) || !seen.Add((nx,ny))) continue;
                                pending.Enqueue((nx,ny));
                            }
                        }
                        return false;
                    }
                    if(CanApproach()) continue;

                    var costs=new Dictionary<(int X,int Y),int>();
                    var previous=new Dictionary<(int X,int Y),(int X,int Y)>();
                    var queue=new PriorityQueue<(int X,int Y),int>();
                    foreach(var p in starts) if(Open(p.X,p.Y) && costs.TryAdd(p,0)) queue.Enqueue(p,0);
                    (int X,int Y)? end=null;
                    while(queue.TryDequeue(out var at,out int cost))
                    {
                        if(cost!=costs[at]) continue;
                        if(Math.Abs(at.X-input.X)+Math.Abs(at.Y-input.Y)==1) { end=at; break; }
                        foreach(var (dx,dy) in new[]{(0,1),(1,0),(0,-1),(-1,0)})
                        {
                            int nx=at.X+dx,ny=at.Y+dy;
                            if(nx<0 || ny<0 || nx>=zone.Width || ny>=zone.Height) continue;
                            int tile=zone.GetTile(nx,ny,1);
                            bool open=Open(nx,ny);
                            if(!open && tile<_gameData.Tiles.Count && _gameData.Tiles[tile].IsDraggable) continue;
                            bool scripted=zone.Objects.Any(h=>h.X==nx && h.Y==ny) || zone.Actions.Any(a=>
                                a.Conditions.Any(c=>c.Arguments.Count>=2 && c.Arguments[0]==nx && c.Arguments[1]==ny &&
                                    c.Opcode is ConditionOpcode.Bump or ConditionOpcode.PlacedItemIs or ConditionOpcode.StandingOn));
                            if(scripted && !open) continue;
                            int nextCost=cost+(open?1:8);
                            var next=(nx,ny);
                            if(nextCost>=costs.GetValueOrDefault(next,int.MaxValue)) continue;
                            costs[next]=nextCost; previous[next]=at; queue.Enqueue(next,nextCost);
                        }
                    }
                    if(end==null) continue;
                    var path=new List<(int X,int Y)>();
                    for(var at=end.Value;;at=previous[at]) { path.Add(at); if(!previous.ContainsKey(at)) break; }
                    path.Reverse();
                    var walls=path.Where(p=>!Open(p.X,p.Y)).Distinct().ToList();
                    if(walls.Count==0 || walls.Count>3) continue;
                    foreach(var wall in walls) zone.SetTile(wall.X,wall.Y,1,ushort.MaxValue);
                    Console.WriteLine($"[WORLD] Opened {walls.Count} wall tile(s) to reach locked-door control " +
                        $"({input.X},{input.Y}) in zone {zoneId}; counter {counterGuard.Arguments[0]}");
                }
            }
        }
    }

    // A generated Use/Trade sector can inherit an authored room whose payment
    // hotspot is separated from its doorway by a solid decorative wall. Keep
    // the authored interaction in place, but open the shortest small passage
    // from the room's actual entrance to its scripted item-use position.
    public void RepairInitializedSectorInteraction(WorldMap world, int zoneId, Zone activeZone,
        int playerX, int playerY, GameState state)
    {
        RepairDisabledGoalEntrances(activeZone);
        RepairRewardPickupBumpTriggers(activeZone);
        RepairUnreachableSectorInteractions(world, zoneId, (playerX, playerY), activeZone);
        RepairUnreachableGoalReward(world, zoneId, activeZone, playerX, playerY, state);
    }

    // Some authored finale rooms are selected by a random door branch. Keep
    // the branch to the actual WinGame room reachable when its sibling branch
    // is only a dead-end return room; otherwise the random initializer can make
    // a generated mission impossible before the player can act on it.
    private void RepairDisabledGoalEntrances(Zone zone)
    {
        foreach(var door in zone.Objects.Where(h=>!h.Enabled && h.Type==ZoneObjectType.DoorEntrance &&
                    h.Argument>=0 && h.Argument<_gameData.Zones.Count))
        {
            var destination=_gameData.Zones[(int)door.Argument];
            if(!destination.Actions.Any(action=>action.Instructions.Any(i=>i.Opcode==InstructionOpcode.WinGame))) continue;
            door.Enabled=true;
            Console.WriteLine($"[WORLD] Re-enabled disabled entrance in zone {zone.Id} at ({door.X},{door.Y}) to keep WinGame room {destination.Id} reachable");
        }
    }

    // Some authored rooms restore a closed cover after their puzzle is solved,
    // while the bump action that reveals the reward still checks the earlier
    // cover tile. Keep the same authored clear action usable on the restored
    // blocker when a reward hotspot sits underneath it.
    private void RepairRewardPickupBumpTriggers(Zone zone)
    {
        foreach(var reward in zone.Objects.Where(h=>h.Enabled && h.Type==ZoneObjectType.Trigger && h.Argument>0))
        {
            int cover=zone.GetTile(reward.X,reward.Y,1);
            if(cover>=_gameData.Tiles.Count || !_gameData.Tiles[cover].IsObject) continue;
            var clear=zone.Actions.SelectMany(a=>a.Conditions.Where(c=>c.Opcode==ConditionOpcode.Bump &&
                    c.Arguments.Count>=3 && c.Arguments[0]==reward.X && c.Arguments[1]==reward.Y)
                .Select(c=>(Action:a,Condition:c)))
                .FirstOrDefault(pair=>pair.Action.Instructions.Any(i=>i.Opcode==InstructionOpcode.RemoveTile &&
                    i.Arguments.Count>=3 && i.Arguments[0]==reward.X && i.Arguments[1]==reward.Y && i.Arguments[2]==1));
            if(clear.Condition==null || clear.Condition.Arguments[2]==cover) continue;
            bool restoredAfterSolve=zone.Actions.Any(a=>a.Conditions.Any(c=>c.Opcode==ConditionOpcode.ZoneIsSolved) &&
                a.Instructions.Any(i=>i.Opcode==InstructionOpcode.PlaceTile && i.Arguments.Count>=4 &&
                    i.Arguments[0]==reward.X && i.Arguments[1]==reward.Y && i.Arguments[2]==1 && i.Arguments[3]==cover));
            if(!restoredAfterSolve) continue;
            clear.Condition.Arguments[2]=(short)cover;
            Console.WriteLine($"[WORLD] Repaired reward cover bump in zone {zone.Id} at ({reward.X},{reward.Y}) for item {reward.Argument}: tile {cover}");
        }
    }

    private void RepairUnreachableGoalReward(WorldMap world, int zoneId, Zone zone, int startX, int startY, GameState state)
    {
        if (!world.Sectors.TryGetValue(zoneId, out var sector)) return;
        if (sector.Type != ZoneType.Goal || sector.FindItem <= 0 || startX < 0 || startY < 0 || startX >= zone.Width || startY >= zone.Height)
            return;

        bool GuardCanMatch(Condition condition)
        {
            var a = condition.Arguments;
            if (a.Count == 0) return true;
            return condition.Opcode switch
            {
                ConditionOpcode.RandomIs => state.ZoneRandom.GetValueOrDefault(zone.Id) == a[0],
                ConditionOpcode.RandomIsNot => state.ZoneRandom.GetValueOrDefault(zone.Id) != a[0],
                ConditionOpcode.CounterIs => state.GetCounter(zone.Id) == a[0],
                ConditionOpcode.CounterIsNot => state.GetCounter(zone.Id) != a[0],
                _ => true
            };
        }
        var rewardTriggers = zone.Actions.Where(action => action.Conditions.All(GuardCanMatch) &&
                action.Instructions.Any(instruction => instruction.Opcode == InstructionOpcode.DropItem &&
                    instruction.Arguments.Count >= 3 && instruction.Arguments[0] == sector.FindItem))
            .SelectMany(action => action.Conditions
                .Where(condition => condition.Opcode == ConditionOpcode.Bump && condition.Arguments.Count >= 2)
                .Select(condition => (X: (int)condition.Arguments[0], Y: (int)condition.Arguments[1])))
            .Where(point => point.X >= 0 && point.Y >= 0 && point.X < zone.Width && point.Y < zone.Height)
            .Distinct().ToArray();
        if (rewardTriggers.Length == 0) return;

        bool Walkable(int x, int y)
        {
            if (x < 0 || y < 0 || x >= zone.Width || y >= zone.Height) return false;
            int tileId = zone.GetTile(x, y, 1);
            if (tileId == ushort.MaxValue || tileId >= _gameData.Tiles.Count) return true;
            var tile = _gameData.Tiles[tileId];
            return !tile.IsObject;
        }
        bool Clearable(int x, int y)
        {
            if (zone.Objects.Any(h => h.Enabled && h.X == x && h.Y == y && h.Type is
                ZoneObjectType.DoorEntrance or ZoneObjectType.DoorExit or ZoneObjectType.Teleporter or
                ZoneObjectType.VehicleToPrimary or ZoneObjectType.VehicleToSecondary)) return false;
            int tileId = zone.GetTile(x, y, 1);
            return tileId < _gameData.Tiles.Count && _gameData.Tiles[tileId].IsObject &&
                !_gameData.Tiles[tileId].IsCharacter && !_gameData.Tiles[tileId].IsRoof &&
                !_gameData.Tiles[tileId].IsItem;
        }

        var queue = new PriorityQueue<(int X, int Y), int>();
        var start = (X: startX, Y: startY);
        var costs = new Dictionary<(int X, int Y), int> { [start] = 0 };
        var previous = new Dictionary<(int X, int Y), (int X, int Y)>();
        queue.Enqueue(start, 0);
        (int X, int Y)? reached = null;
        while (queue.TryDequeue(out var point, out int cost))
        {
            if (cost != costs[point]) continue;
            if (rewardTriggers.Any(target => Math.Abs(target.X - point.X) + Math.Abs(target.Y - point.Y) == 1))
            { reached = point; break; }
            foreach (var next in new[] { (point.X, point.Y - 1), (point.X, point.Y + 1),
                (point.X - 1, point.Y), (point.X + 1, point.Y) })
            {
                int stepCost = Walkable(next.Item1, next.Item2) ? 0 : Clearable(next.Item1, next.Item2) ? 1 : int.MaxValue;
                if (stepCost == int.MaxValue) continue;
                int nextCost = cost + stepCost;
                if (nextCost >= costs.GetValueOrDefault(next, int.MaxValue)) continue;
                costs[next] = nextCost; previous[next] = point; queue.Enqueue(next, nextCost);
            }
        }
        if (reached is not { } end || costs[end] is <= 0 or > 3) return;

        var path = new List<(int X, int Y)>();
        for (var point = end; ; point = previous[point])
        {
            path.Add(point);
            if (!previous.ContainsKey(point)) break;
        }
        path.Reverse();
        var blockers = path.Where(point => !Walkable(point.X, point.Y)).Distinct().ToArray();
        foreach (var blocker in blockers) zone.SetTile(blocker.X, blocker.Y, 1, ushort.MaxValue);
        if (blockers.Length > 0)
            Console.WriteLine($"[WORLD] Opened {blockers.Length} blocked tile(s) from the goal-room arrival " +
                $"to the scripted reward for item {sector.FindItem} in zone {zoneId}");
    }

    // Some authored/generated rooms gate a ZoneEntered tile change on a sector
    // counter value larger than any action in that room can produce. Keep the
    // gate meaningful, but clamp it to the highest reachable value so that the
    // room's own counter-producing interaction can actually open the route.
    public static void RepairUnreachableSectorCounterGates(Zone zone, GameState state)
    {
        int current = state.GetSectorCounter(zone.Id);
        long possibleIncrease = 0;
        long highestSetValue = current;
        foreach (var action in zone.Actions)
        {
            // Ignore producers whose ordinary counter guard is already false;
            // they cannot contribute to this room's reachable maximum.
            if (action.Conditions.Any(c => c.Opcode == ConditionOpcode.CounterIs &&
                (c.Arguments.Count == 0 || state.GetCounter(zone.Id) != c.Arguments[0])))
                continue;
            foreach (var instruction in action.Instructions)
            {
                if (instruction.Arguments.Count == 0) continue;
                if (instruction.Opcode == InstructionOpcode.AddToSectorCounter && instruction.Arguments[0] > 0)
                    possibleIncrease += instruction.Arguments[0];
                else if (instruction.Opcode == InstructionOpcode.SetSectorCounter && instruction.Arguments[0] > highestSetValue)
                    highestSetValue = instruction.Arguments[0];
            }
        }

        long reachableMaximum = Math.Max(highestSetValue, current + possibleIncrease);
        if (reachableMaximum <= 0 || reachableMaximum > short.MaxValue) return;
        foreach (var action in zone.Actions)
        foreach (var condition in action.Conditions.Where(c =>
            c.Opcode == ConditionOpcode.SectorCounterIsGreaterThan && c.Arguments.Count > 0))
        {
            if (condition.Arguments[0] < reachableMaximum) continue;
            // The guarded script runs only on entry. If even every available
            // producer cannot satisfy it, requiring a counter change and a
            // second visit can deadlock rooms with one-way entrances. Let the
            // entry action run against the current counter instead.
            condition.Arguments[0] = (short)Math.Max(short.MinValue, current - 1);
            Console.WriteLine($"[WORLD] Repaired unreachable sector-counter gate in zone {zone.Id}: " +
                $"threshold now {condition.Arguments[0]}, reachable maximum {reachableMaximum}, current {current}");
        }
    }

    private void RepairUnreachableSectorInteractions(WorldMap world, int? onlyZoneId = null,
        (int X, int Y)? liveStart = null, Zone? activeZone = null)
    {
        foreach(var (zoneId,sector) in world.Sectors)
        {
            if(onlyZoneId.HasValue && zoneId!=onlyZoneId.Value) continue;
            if(sector.RequiredItem<=0 || zoneId<0 || zoneId>=_gameData.Zones.Count) continue;
            var zone=onlyZoneId==zoneId && activeZone!=null ? activeZone : _gameData.Zones[zoneId];
            var starts=new HashSet<(int X,int Y)>();
            bool roomInteraction=sector.Type is ZoneType.Use or ZoneType.Trade &&
                world.RoomParents.TryGetValue(zoneId,out int parentId) &&
                parentId>=0 && parentId<_gameData.Zones.Count &&
                _gameData.Zones[parentId].Objects.Any(h=>h.Enabled && h.Argument==zoneId &&
                    h.Type is ZoneObjectType.DoorEntrance or ZoneObjectType.DoorExit);
            bool outdoorTransport=sector.Type==ZoneType.TravelStart && world.Connections.ContainsKey(zoneId);
            if(!roomInteraction && !outdoorTransport) continue;

            if(roomInteraction)
            {
                foreach(var back in zone.Objects.Where(h=>h.Enabled &&
                    h.Type is ZoneObjectType.DoorEntrance or ZoneObjectType.DoorExit))
                foreach(var (dx,dy) in DoorwayArrival.ExitOffsets(zone,back.Y))
                {
                    int sx=back.X+dx,sy=back.Y+dy;
                    if(sx>=0 && sy>=0 && sx<zone.Width && sy<zone.Height) starts.Add((sx,sy));
                }
            }
            else if(world.Connections.TryGetValue(zoneId,out var connections))
            {
                // Outdoor transport controls can be buried behind decorative
                // terrain. Use the actual inward edge for every connected side
                // as a possible approach, since a generated world may enter
                // the same sector from more than one neighboring area.
                if(connections.North.HasValue) for(int x=1;x<zone.Width-1;x++) starts.Add((x,1));
                if(connections.South.HasValue) for(int x=1;x<zone.Width-1;x++) starts.Add((x,zone.Height-2));
                if(connections.West.HasValue) for(int y=1;y<zone.Height-1;y++) starts.Add((1,y));
                if(connections.East.HasValue) for(int y=1;y<zone.Height-1;y++) starts.Add((zone.Width-2,y));
            }
            // Once zone-entry scripts have run, check from the real arrival
            // cell. A disconnected part of a long edge is not a valid entry
            // just because some other edge cell can reach the interaction.
            if(outdoorTransport && onlyZoneId==zoneId && liveStart is { } arrival &&
                arrival.X>=0 && arrival.Y>=0 && arrival.X<zone.Width && arrival.Y<zone.Height)
            {
                starts.Clear();
                starts.Add(arrival);
            }
            if(starts.Count==0) continue;

            var target=zone.Actions.SelectMany((action,index)=>action.Conditions
                .Where(c=>c.Opcode==ConditionOpcode.PlacedItemIs && c.Arguments.Count>=5 &&
                    (c.Arguments[4]==sector.RequiredItem || c.Arguments[4]==-1) &&
                    (c.Arguments[3]==-1 || c.Arguments[0]>=0 && c.Arguments[1]>=0 &&
                        c.Arguments[0]<zone.Width && c.Arguments[1]<zone.Height &&
                        (zone.GetTile(c.Arguments[0],c.Arguments[1],c.Arguments[2])==unchecked((ushort)c.Arguments[3]) ||
                         zone.Actions.Any(setup=>setup.Instructions.Any(i=>
                             i.Opcode==InstructionOpcode.PlaceTile && i.Arguments.Count>=4 &&
                             i.Arguments[0]==c.Arguments[0] && i.Arguments[1]==c.Arguments[1] &&
                             i.Arguments[2]==c.Arguments[2] && i.Arguments[3]==c.Arguments[3] ||
                             c.Arguments[2]==1 && i.Opcode==InstructionOpcode.DrawTile && i.Arguments.Count>=3 &&
                             i.Arguments[0]==c.Arguments[0] && i.Arguments[1]==c.Arguments[1] &&
                             i.Arguments[2]==c.Arguments[3] ||
                             i.Opcode==InstructionOpcode.MoveTile && i.Arguments.Count>=5 &&
                             i.Arguments[2]==c.Arguments[2] && i.Arguments[3]==c.Arguments[0] &&
                             i.Arguments[4]==c.Arguments[1])))) &&
                    action.Instructions.Any(i=>i.Opcode is InstructionOpcode.RemoveItem or
                        InstructionOpcode.AddItem or InstructionOpcode.DropItem or
                        InstructionOpcode.PlaceTile or InstructionOpcode.RemoveTile or
                        InstructionOpcode.MarkAsSolved or InstructionOpcode.WinGame))
                .Select(c=>(X:(int)c.Arguments[0],Y:(int)c.Arguments[1],Action:index,
                    Priority:action.Instructions.Any(i=>i.Opcode is InstructionOpcode.RemoveItem or
                        InstructionOpcode.AddItem or InstructionOpcode.DropItem or
                        InstructionOpcode.MarkAsSolved or InstructionOpcode.WinGame) ? 0 : 1)))
                .OrderBy(c=>c.Priority).ThenBy(c=>c.Action)
                .FirstOrDefault();
            if(target==default || target.X<0 || target.Y<0 || target.X>=zone.Width || target.Y>=zone.Height)
                continue;

            bool Open(int x,int y)
            {
                // The interaction hotspot is blocked during play. Do not let
                // the reachability proof walk through an empty placeholder
                // there before its zone initializer places the authored lock.
                if(x<0 || y<0 || x>=zone.Width || y>=zone.Height || (x,y)==(target.X,target.Y)) return false;
                int tile=zone.GetTile(x,y,1);
                return tile>=_gameData.Tiles.Count || !_gameData.Tiles[tile].IsObject;
            }
            bool ScriptedCell(int x,int y) => zone.Objects.Any(h=>h.X==x && h.Y==y) ||
                zone.Actions.Any(a=>a.Conditions.Any(c=>c.Arguments.Count>=2 &&
                    c.Arguments[0]==x && c.Arguments[1]==y && c.Opcode is
                        ConditionOpcode.Bump or ConditionOpcode.PlacedItemIs or ConditionOpcode.StandingOn));

            bool CanReachTarget()
            {
                var seen=new HashSet<(int X,int Y)>(); var pending=new Queue<(int X,int Y)>();
                foreach(var p in starts) if(Open(p.X,p.Y) && seen.Add(p)) pending.Enqueue(p);
                while(pending.TryDequeue(out var at))
                {
                    if(Math.Abs(at.X-target.X)+Math.Abs(at.Y-target.Y)==1) return true;
                    foreach(var (dx,dy) in new[]{(0,1),(1,0),(0,-1),(-1,0)})
                    {
                        int nx=at.X+dx,ny=at.Y+dy;
                        if(Open(nx,ny) && seen.Add((nx,ny))) pending.Enqueue((nx,ny));
                    }
                }
                return false;
            }
            if(CanReachTarget()) continue;

            var costs=new Dictionary<(int X,int Y),int>();
            var previous=new Dictionary<(int X,int Y),(int X,int Y)>();
            var queue=new PriorityQueue<(int X,int Y),int>();
            foreach(var p in starts) if(Open(p.X,p.Y) && costs.TryAdd(p,0)) queue.Enqueue(p,0);
            (int X,int Y)? end=null;
            while(queue.TryDequeue(out var at,out int cost))
            {
                if(cost!=costs[at]) continue;
                if(Math.Abs(at.X-target.X)+Math.Abs(at.Y-target.Y)==1) { end=at; break; }
                foreach(var (dx,dy) in new[]{(0,1),(1,0),(0,-1),(-1,0)})
                {
                    int nx=at.X+dx,ny=at.Y+dy;
                    if(nx<0 || ny<0 || nx>=zone.Width || ny>=zone.Height) continue;
                    int tile=zone.GetTile(nx,ny,1); bool open=Open(nx,ny);
                    if(!open && tile<_gameData.Tiles.Count && _gameData.Tiles[tile].IsDraggable) continue;
                    if(!open && ScriptedCell(nx,ny)) continue;
                    var next=(nx,ny); int nextCost=cost+(open?1:8);
                    if(nextCost>=costs.GetValueOrDefault(next,int.MaxValue)) continue;
                    costs[next]=nextCost; previous[next]=at; queue.Enqueue(next,nextCost);
                }
            }
            if(end==null) continue;
            var path=new List<(int X,int Y)>();
            for(var at=end.Value;;at=previous[at]) { path.Add(at); if(!previous.ContainsKey(at)) break; }
            path.Reverse();
            var walls=path.Where(p=>!Open(p.X,p.Y)).Distinct().ToList();
            if(walls.Count is 0 or >3) continue;
            foreach(var wall in walls) zone.SetTile(wall.X,wall.Y,1,ushort.MaxValue);
            Console.WriteLine($"[WORLD] Opened {walls.Count} wall tile(s) to reach sector item-use target " +
                $"({target.X},{target.Y}) in zone {zoneId}, action {target.Action}");
        }
    }

    /// <summary>
    /// Sets up the item exchange chain for the mission.
    /// </summary>
    private void SetupItemChain()
    {
        if (CurrentWorld == null || CurrentMission == null) return;

        // Native worlds have two independent strains. PuzzleChain is sorted for
        // display, so its first step need not be the strain Yoda supplies.
        if (_gameData.GameType == GameType.YodaStories && CurrentWorld.Strain1.Count > 0 &&
            CurrentWorld.Strain1[0] is { Item1: > 0 } firstPuzzle)
        {
            CurrentWorld.StartingItemId = firstPuzzle.Item1;
        }

        // The older generator uses a single ordered chain.
        if (!CurrentWorld.StartingItemId.HasValue && CurrentMission.PuzzleChain.Count > 0)
        {
            var firstStep = CurrentMission.PuzzleChain[0];
            if (firstStep.RequiredItemId > 0)
            {
                CurrentWorld.StartingItemId = firstStep.RequiredItemId;
                var itemName = GetItemName(firstStep.RequiredItemId);
                Console.WriteLine($"Starting item: {itemName} (Tile {firstStep.RequiredItemId})");
            }
            else if (_gameData.GameType == GameType.YodaStories && firstStep.RewardItemId > 0)
            {
                // Yoda hands Luke the first authored quest item before he leaves Dagobah.
                CurrentWorld.StartingItemId = firstStep.RewardItemId;
                var itemName = GetItemName(firstStep.RewardItemId);
                Console.WriteLine($"Yoda's mission item: {itemName} (Tile {firstStep.RewardItemId})");
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
    public int? Seed { get; set; }
    public WorldSize GenerationSize { get; set; } = WorldSize.Medium;
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

    /// <summary>
    /// Worlds from OriginalWorldGenerator: the sector each zone (or room) belongs to, which
    /// is what zone scripts ask about. Empty for worlds from the older generator.
    /// </summary>
    public Dictionary<int, Sector> Sectors { get; } = new();

    /// <summary>The puzzle strains a sector's PuzzleIndex points into (OriginalWorldGenerator worlds).</summary>
    public IReadOnlyList<Puzzle?> Strain1 { get; set; } = Array.Empty<Puzzle?>();
    public IReadOnlyList<Puzzle?> Strain2 { get; set; } = Array.Empty<Puzzle?>();

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
