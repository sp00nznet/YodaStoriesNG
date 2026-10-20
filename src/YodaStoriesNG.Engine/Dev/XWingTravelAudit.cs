using YodaStoriesNG.Engine.Data;
using YodaStoriesNG.Engine.Parsing;

namespace YodaStoriesNG.Engine.Game;

// Like the capture harness, this partial exercises the real engine's private movement
// and script paths. Initialize/Run/Render are never called: no SDL window is created.
public unsafe partial class GameEngine
{
    public static int AuditXWingTravel(string dataFile, bool smooth = false)
    {
        var output = Console.Out;
        int failures = 0;
        foreach (bool shortcut in new[] { false, true })
        foreach (var route in new[]
                 {
                     (Planet.Desert, Zone: 2, Goal: 810),
                     (Planet.Snow, Zone: 151, Goal: 1419),
                     (Planet.Forest, Zone: 476, Goal: 448)
                 })
        {
            string stage = "initial Dagobah landing";
            var log = new StringWriter();
            try
            {
                Console.SetOut(log);
                var data = new DtaParser().Parse(dataFile);
                var goal = data.Puzzles.First(p => p.Type == PuzzleType.Mission && p.Item1 == route.Goal);
                var mission = new Mission { Planet = route.Item1, GoalPuzzle = goal };
                mission.PuzzleChain.Add(new PuzzleStep { Puzzle = goal, RequiredItemId = route.Goal });
                var world = new WorldMap
                {
                    Planet = route.Item1, Mission = mission,
                    StartingZoneId = 93, LandingZoneId = route.Zone, XWingZoneId = 93,
                    DagobahZones = new() { 93, 94, 95, 96 }, YodaZoneId = 93,
                    YodaPosition = (11, 4)
                };
                var engine = new GameEngine(Path.GetDirectoryName(dataFile)!)
                {
                    _gameData = data, _showingTitleScreen = false, _smoothMovement = smooth, _smoothMovementRequested = smooth,
                    _worldGenerator = new WorldGenerator(data)
                };
                engine._worldGenerator.SetCurrentWorld(world);
                engine._actionExecutor = new ActionExecutor(data, engine._state)
                {
                    CurrentWorld = world, CurrentMission = mission
                };
                engine.LoadZone(93, enteredByPlane: true);
                engine.AwaitXWingLanding(93);

                for (int trip = 0; trip < 3; trip++)
                {
                    stage = $"outbound trip {trip + 1}";
                    // Walk into the real cockpit, not the distant transit marker.
                    engine._state.PlayerX = 6;
                    engine._state.PlayerY = 12;
                    // Also exercise leaving an unsolved area after object manipulation.
                    engine.MarkObjectsMoved(engine._state.CurrentZone!);
                    if (shortcut) engine.TravelToPlanet();
                    else engine.AuditMovementStep(0, -1, Direction.Up);
                    engine.AwaitXWingLanding(route.Zone);
                    AssertXWingAudit(CountXWingTiles(data.Zones[93]) == 0, "departure left ship fragments on Dagobah");

                    // Returning on foot must keep the parked ship and visible hero,
                    // without restarting its landing or losing the boarding state.
                    var exitPosition = (engine._state.PlayerX, engine._state.PlayerY);
                    engine.LoadZone(route.Zone, exitPosition.PlayerX, exitPosition.PlayerY);
                    engine.AwaitXWingLanding(route.Zone);

                    stage = $"return trip {trip + 1}";
                    // The landing script leaves Luke one step below the cockpit.
                    // Also exercise leaving an unsolved area after object manipulation.
                    engine.MarkObjectsMoved(engine._state.CurrentZone!);
                    if (shortcut) engine.TravelToPlanet();
                    else
                    {
                        engine.AuditMovementStep(0, -1, Direction.Up);
                        if (route.Item1 != Planet.Desert)
                            engine.AuditMovementStep(0, -1, Direction.Up);
                    }
                    engine.AwaitXWingLanding(93);
                    AssertXWingAudit(CountXWingTiles(data.Zones[route.Zone]) == 0, "departure left ship fragments on the planet");
                }
                Console.SetOut(output);
                Console.WriteLine($"PASS {route.Item1} ({(shortcut ? "X shortcut" : "cockpit")}): 3 round trips and on-foot reentries; visible hero and one parked ship.");
            }
            catch (Exception ex)
            {
                Console.SetOut(output);
                Console.WriteLine($"FAIL {route.Item1} ({(shortcut ? "X shortcut" : "cockpit")}), {stage}: {ex.Message}");
                Console.WriteLine(string.Join(Environment.NewLine, log.ToString().Split('\n').TakeLast(30)));
                failures++;
            }
            finally
            {
                Console.SetOut(output);
            }
        }
        failures += AuditGeneratedXWingTravel(dataFile, smooth);
        return failures == 0 ? 0 : 1;
    }

    private static int AuditGeneratedXWingTravel(string dataFile, bool smooth)
    {
        var output = Console.Out;
        var previousSeed = Environment.GetEnvironmentVariable("YSNG_SEED");
        int failures = 0, tested = 0;
        HashSet<ushort>? expectedMissions = null;
        var coveredMissions = new HashSet<ushort>();
        for (int seed = 0; seed < 180; seed++)
        {
            tested++;
            string context = $"seed {seed}";
            try
            {
                Console.SetOut(TextWriter.Null);
                Environment.SetEnvironmentVariable("YSNG_SEED", seed.ToString());
                var data = new DtaParser().Parse(dataFile);
                expectedMissions ??= data.Puzzles.Where(p => p.Type == PuzzleType.Mission).Select(p => p.Item1).ToHashSet();
                var generator = new WorldGenerator(data);
                var world = generator.GenerateWorld(WorldSize.Small);
                coveredMissions.Add(world.Mission!.GoalPuzzle!.Item1);
                context += $", {world.Planet}, mission item {world.Mission!.GoalPuzzle!.Item1}, final requirement {world.Mission.PuzzleChain[^1].RequiredItemId}";
                var engine = new GameEngine(Path.GetDirectoryName(dataFile)!)
                {
                    _gameData = data, _showingTitleScreen = false, _smoothMovement = smooth, _smoothMovementRequested = smooth, _worldGenerator = generator
                };
                engine._actionExecutor = new ActionExecutor(data, engine._state)
                {
                    CurrentWorld = world, CurrentMission = world.Mission
                };
                engine._sectorPuzzles = new SectorPuzzles(data, engine._state, world);
                engine.LoadZone(93, enteredByPlane: true);
                engine.AwaitXWingLanding(93);
                if (world.StartingItemId.HasValue) engine._state.AddItem(world.StartingItemId.Value);
                engine._state.PlayerX = 6;
                engine._state.PlayerY = 12;
                engine.MarkObjectsMoved(engine._state.CurrentZone!);
                engine.AuditMovementStep(0, -1, Direction.Up);
                if (world.Mission.GoalPuzzle.Item1 == 1788)
                {
                    // This authored mission intercepts the flight and takes Luke to a
                    // medical station. Its exit is a FromDagobah hotspot in another zone.
                    engine.AwaitXWingLanding(642, expectParkedShip: false);
                    engine._state.PlayerX = 3;
                    engine._state.PlayerY = 7;
                    engine.MarkObjectsMoved(engine._state.CurrentZone!);
                    engine.AuditMovementStep(0, 1, Direction.Down);
                    engine.AwaitXWingLanding(world.LandingZoneId, expectParkedShip: false);
                }
                else
                    engine.AwaitXWingLanding(world.LandingZoneId);
            }
            catch (Exception ex)
            {
                Console.SetOut(output);
                Console.WriteLine($"FAIL generated {context}: {ex.Message}");
                failures++;
            }
            finally
            {
                Console.SetOut(output);
                Environment.SetEnvironmentVariable("YSNG_SEED", previousSeed);
            }
            if (tested >= 24 && expectedMissions!.IsSubsetOf(coveredMissions))
                break;
        }
        Console.WriteLine($"Generated mission departures: {tested - failures}/{tested} passed; " +
            $"covered {coveredMissions.Count}/{expectedMissions?.Count ?? 0} mission types.");
        if (expectedMissions == null || !expectedMissions.IsSubsetOf(coveredMissions))
            failures++;
        return failures;
    }

    private void AuditMovementStep(int dx, int dy, Direction facing)
    {
        TryMovePlayer(dx, dy, facing);
        for (int tick = 0; tick < 180 && _playerStep != null; tick++) Update(TargetFrameTime);
        AssertXWingAudit(_playerStep == null, "tile step did not finish before boarding");
    }

    private void AwaitXWingLanding(int zoneId, bool expectParkedShip = true)
    {
        for (int tick = 0; tick < 400; tick++)
        {
            Update(TargetFrameTime);
            if (_state.CurrentZoneId != zoneId || _actionExecutor!.IsBusy || !_state.HeroVisible)
                continue;

            // Landing removes the roof and body pieces covering Luke's cockpit exit.
            AssertXWingAudit(!expectParkedShip || CountXWingTiles(_state.CurrentZone!) == 6,
                $"zone {zoneId} has {CountXWingTiles(_state.CurrentZone!)} ship pieces instead of 6");
            return;
        }
        throw new InvalidOperationException($"landing timed out: wanted zone {zoneId}, got {_state.CurrentZoneId}, " +
            $"heroVisible={_state.HeroVisible}, scriptPending={_actionExecutor!.IsBusy}, hero=({_state.PlayerX},{_state.PlayerY})");
    }

    private static int CountXWingTiles(Zone zone) => zone.TileGrid.Cast<ushort>().Count(t => t is >= 948 and <= 955);

    private static void AssertXWingAudit(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
