using System.Text;
using YodaStoriesNG.Engine.Data;
using YodaStoriesNG.Engine.Game;
using YodaStoriesNG.Engine.Parsing;

namespace YodaStoriesNG.Engine.Dev;

/// <summary>
/// Headless reachability audit for a generated mission. The DOT output is both a visual
/// dependency graph and a record of the closure calculation used to check the world.
/// </summary>
public static class MissionReachabilityGraph
{
    private sealed record PuzzleNode(int ZoneId, Sector Sector, Puzzle Puzzle);
    private sealed record Edge(int From, int To, string Label, bool IsTravel = false);

    public sealed record AuditResult(
        int PuzzleCount,
        int ReachablePuzzleCount,
        int SolvedPuzzleCount,
        int MissionStepCount,
        int ReachableMissionStepCount,
        IReadOnlyList<string> Problems,
        string Dot);

    /// <summary>Generate and audit a batch of worlds without constructing the game UI.</summary>
    public static int Run(string dataFile, int count, string? graphPath = null)
    {
        if (count < 1)
        {
            Console.Error.WriteLine("World count must be at least 1.");
            return 2;
        }

        var data = new DtaParser().Parse(dataFile);
        var previousSeed = Environment.GetEnvironmentVariable("YSNG_SEED");
        var output = Console.Out;
        string? graph = null;
        AuditResult? firstFailure = null;
        int generated = 0, puzzleCount = 0, reachablePuzzleCount = 0, solvedPuzzleCount = 0;
        var generationFailures = new Dictionary<string, int>();

        try
        {
            Console.SetOut(TextWriter.Null);
            for (int i = 0; i < count; i++)
            {
                Environment.SetEnvironmentVariable("YSNG_SEED", i.ToString());
                try
                {
                    var generator = new WorldGenerator(data);
                    var world = generator.GenerateWorld((WorldSize)(i % 3 + 1));
                    var audit = Analyze(data, world);
                    generated++;
                    puzzleCount += audit.PuzzleCount;
                    reachablePuzzleCount += audit.ReachablePuzzleCount;
                    solvedPuzzleCount += audit.SolvedPuzzleCount;
                    graph ??= audit.Dot;
                    if (audit.Problems.Count > 0 && firstFailure == null)
                    {
                        firstFailure = audit;
                        graph = audit.Dot;
                    }
                }
                catch (Exception ex) when (ex is WorldGenerationException or InvalidOperationException or ArgumentException)
                {
                    generationFailures[ex.Message] = generationFailures.GetValueOrDefault(ex.Message) + 1;
                }
            }
        }
        finally
        {
            Console.SetOut(output);
            Environment.SetEnvironmentVariable("YSNG_SEED", previousSeed);
        }

        graphPath ??= Path.Combine(Path.GetTempPath(), "yoda-mission-reachability.dot");
        if (graph != null)
        {
            File.WriteAllText(graphPath, graph);
            Console.WriteLine($"Graph written to: {Path.GetFullPath(graphPath)}");
        }

        Console.WriteLine($"Audited {generated}/{count} generated worlds without rendering.");
        if (generated > 0)
            Console.WriteLine($"Puzzle sectors reachable: {reachablePuzzleCount}/{puzzleCount}; solvable by item flow: {solvedPuzzleCount}/{puzzleCount}.");
        foreach (var (reason, failures) in generationFailures.OrderByDescending(pair => pair.Value))
            Console.WriteLine($"World generation failed {failures} time(s): {reason}");

        if (firstFailure != null)
        {
            Console.WriteLine($"First reachability failure ({firstFailure.Problems.Count}):");
            foreach (var problem in firstFailure.Problems.Take(30))
                Console.WriteLine($"  - {problem}");
        }

        return generated == count && firstFailure == null ? 0 : 1;
    }

    public static AuditResult Analyze(GameData data, WorldMap world)
    {
        var problems = new List<string>();
        var edges = BuildEdges(data, world);
        var puzzleNodes = GetPuzzleNodes(world, problems);
        var reachableZones = new HashSet<int>();
        var inventory = new HashSet<int>();
        var solvedZones = new HashSet<int>();

        if (world.StartingZoneId >= 0)
            reachableZones.Add(world.StartingZoneId);
        if (world.StartingItemId is int startingItem && startingItem > 0)
            inventory.Add(startingItem);
        // Indy hands over the first chain item during the reachable town
        // briefing rather than storing it in Yoda's StartingItemId field.
        if(data.GameType==GameType.IndianaJones && world.Mission?.PuzzleChain.FirstOrDefault() is
            { RequiredItemId:0,RewardItemId:>0 } briefing && world.Sectors.Values.Any(s=>
                s.Type==ZoneType.Town && s.Zone==briefing.ZoneId)) inventory.Add(briefing.RewardItemId);

        // Find zones, puzzle rewards and travel gates to a fixed point. This mirrors how a
        // player can expand the map as items become available, without loading a game window.
        bool changed;
        do
        {
            changed = ExpandReachableZones(world, edges, reachableZones, inventory);

            foreach (int zoneId in reachableZones.ToArray())
            {
                if (!world.Sectors.TryGetValue(zoneId, out var sector) || zoneId != sector.Zone)
                    continue;

                if (sector.PuzzleIndex < 0)
                {
                    if (sector.FindItem > 0 && inventory.Add(sector.FindItem))
                        changed = true;
                    continue;
                }

                if (solvedZones.Contains(zoneId))
                    continue;

                if (sector.RequiredItem > 0 && !inventory.Contains(sector.RequiredItem))
                    continue;
                if (sector.AdditionalRequiredItem > 0 && !inventory.Contains(sector.AdditionalRequiredItem))
                    continue;

                solvedZones.Add(zoneId);
                if (sector.FindItem > 0 && inventory.Add(sector.FindItem))
                    changed = true;
                if (sector.AdditionalGainItem > 0 && inventory.Add(sector.AdditionalGainItem))
                    changed = true;
                changed = true;
            }
        } while (changed);

        foreach (var node in puzzleNodes)
        {
            if (!reachableZones.Contains(node.ZoneId))
                problems.Add($"Puzzle {node.Puzzle.Id} ({node.Puzzle.Type}) at zone {node.ZoneId} is unreachable.");
            else if (!solvedZones.Contains(node.ZoneId))
            {
                var missing = new[] { node.Sector.RequiredItem, node.Sector.AdditionalRequiredItem }
                    .Where(item => item > 0 && !inventory.Contains(item)).Distinct().ToArray();
                problems.Add(missing.Length > 0
                    ? $"Puzzle {node.Puzzle.Id} ({node.Puzzle.Type}) at zone {node.ZoneId} is reachable but missing item(s) {string.Join(", ", missing)}."
                    : $"Puzzle {node.Puzzle.Id} ({node.Puzzle.Type}) at zone {node.ZoneId} was not solved by the reachability closure.");
            }

            var missionStep = world.Mission?.PuzzleChain.FirstOrDefault(step => step.ZoneId == node.ZoneId);
            if (missionStep == null)
                problems.Add($"Puzzle {node.Puzzle.Id} at zone {node.ZoneId} is missing from the mission chain.");
            else if (missionStep.Puzzle.Id != node.Puzzle.Id ||
                     missionStep.RequiredItemId != Math.Max(0, node.Sector.RequiredItem) ||
                     missionStep.RewardItemId != Math.Max(0, node.Sector.FindItem))
                problems.Add($"Mission chain entry at zone {node.ZoneId} does not match its generated puzzle/item links.");
        }

        int reachableSteps = 0;
        foreach (var step in world.Mission?.PuzzleChain ?? Enumerable.Empty<PuzzleStep>())
        {
            if (step.ZoneId is int zoneId && reachableZones.Contains(zoneId))
                reachableSteps++;
            else
                problems.Add($"Mission step [{step.Puzzle.Type}] at zone {step.ZoneId?.ToString() ?? "(none)"} is unreachable.");
        }

        var dot = WriteDot(data, world, edges, puzzleNodes, reachableZones, solvedZones, inventory, problems);
        return new AuditResult(
            puzzleNodes.Count,
            puzzleNodes.Count(p => reachableZones.Contains(p.ZoneId)),
            puzzleNodes.Count(p => solvedZones.Contains(p.ZoneId)),
            world.Mission?.PuzzleChain.Count ?? 0,
            reachableSteps,
            problems,
            dot);
    }

    private static List<PuzzleNode> GetPuzzleNodes(WorldMap world, List<string> problems)
    {
        var nodes = new List<PuzzleNode>();
        foreach (var (zoneId, sector) in world.Sectors)
        {
            // world.Sectors also maps every room back to its outdoor sector. Count and
            // check each physical sector once, using its outdoor zone ID.
            if (sector.PuzzleIndex < 0 || zoneId != sector.Zone)
                continue;

            var strain = sector.UsedAlternateStrain ? world.Strain1 : world.Strain2;
            if (sector.PuzzleIndex >= strain.Count || strain[sector.PuzzleIndex] is not { } puzzle)
            {
                problems.Add($"Puzzle sector zone {zoneId} references missing strain puzzle {sector.PuzzleIndex}.");
                continue;
            }
            nodes.Add(new PuzzleNode(zoneId, sector, puzzle));
        }
        return nodes;
    }

    private static List<Edge> BuildEdges(GameData data, WorldMap world)
    {
        var edges = new List<Edge>();
        var activeZones = world.Sectors.Keys.Concat(world.DagobahZones).ToHashSet();
        foreach (var connection in world.Connections.Values)
        {
            Add(connection.ZoneId, connection.North, "N");
            Add(connection.ZoneId, connection.South, "S");
            Add(connection.ZoneId, connection.East, "E");
            Add(connection.ZoneId, connection.West, "W");
        }

        foreach (var (outdoorZone, rooms) in world.RoomConnections)
            foreach (int room in rooms)
            {
                edges.Add(new Edge(outdoorZone, room, "door"));
                edges.Add(new Edge(room, outdoorZone, "door"));
            }

        // Zone hotspot arguments are rewritten by the generator to pair transport islands.
        foreach (var zone in data.Zones.Where(z => activeZones.Contains(z.Id)))
        foreach (var hotspot in zone.Objects.Where(o => o.Type is ZoneObjectType.VehicleToPrimary or ZoneObjectType.VehicleToSecondary))
        {
            int destination = hotspot.Argument;
            if (activeZones.Contains(destination) && destination != zone.Id)
            {
                edges.Add(new Edge(zone.Id, destination, "vehicle", true));
                var returnLink = data.Zones[destination].Objects.FirstOrDefault(o =>
                    o.Type is ZoneObjectType.VehicleToPrimary or ZoneObjectType.VehicleToSecondary && o.Argument == zone.Id);
                if (returnLink != null)
                    edges.Add(new Edge(destination, zone.Id, "vehicle", true));
            }
        }

        // Dagobah-to-planet travel is handled by the X-Wing and is separate from planet
        // transport hotspots. The return edge is included to reflect the actual trip back.
        if (world.LandingZoneId >= 0 && world.StartingZoneId >= 0 && world.LandingZoneId != world.StartingZoneId)
        {
            edges.Add(new Edge(world.StartingZoneId, world.LandingZoneId, "X-Wing", true));
            edges.Add(new Edge(world.LandingZoneId, world.StartingZoneId, "X-Wing", true));
        }
        return edges;

        void Add(int from, int? to, string label)
        {
            if (to.HasValue && to.Value != from)
                edges.Add(new Edge(from, to.Value, label));
        }
    }

    private static bool ExpandReachableZones(WorldMap world, List<Edge> edges,
        HashSet<int> reachable, HashSet<int> inventory)
    {
        bool changed = false;
        var queue = new Queue<int>(reachable);
        var visited = new HashSet<int>(reachable);
        while (queue.Count > 0)
        {
            int from = queue.Dequeue();
            foreach (var edge in edges.Where(e => e.From == from))
            {
                if (IsBlockedTransition(world, edge, inventory))
                    continue;
                if (!reachable.Add(edge.To))
                    continue;
                changed = true;
                if (visited.Add(edge.To))
                    queue.Enqueue(edge.To);
            }
        }
        return changed;
    }

    private static bool IsBlockedTransition(WorldMap world, Edge edge, HashSet<int> inventory)
    {
        if (edge.IsTravel && world.Sectors.TryGetValue(edge.From, out var transport) &&
            transport.RequiredItem > 0 && !inventory.Contains(transport.RequiredItem))
            return true;

        var positions = GridPositions(world);
        return Blocks(edge.From, edge.To) || Blocks(edge.To, edge.From);

        bool Blocks(int source, int destination)
        {
            if (!world.Sectors.TryGetValue(source, out var sector) || sector.RequiredItem <= 0 ||
                inventory.Contains(sector.RequiredItem) || !positions.TryGetValue(source, out var from) ||
                !positions.TryGetValue(destination, out var to))
                return false;

            return sector.Type switch
            {
                ZoneType.BlockadeNorth => from.x == to.x && from.y == to.y + 1,
                ZoneType.BlockadeSouth => from.x == to.x && from.y + 1 == to.y,
                ZoneType.BlockadeEast => from.y == to.y && from.x + 1 == to.x,
                ZoneType.BlockadeWest => from.y == to.y && from.x == to.x + 1,
                _ => false,
            };
        }
    }

    private static Dictionary<int, (int x, int y)> GridPositions(WorldMap world)
    {
        var positions = new Dictionary<int, (int x, int y)>();
        if (world.Grid == null) return positions;
        for (int y = 0; y < world.Grid.GetLength(0); y++)
        for (int x = 0; x < world.Grid.GetLength(1); x++)
            if (world.Grid[y, x] is int zoneId)
                positions.TryAdd(zoneId, (x, y));
        return positions;
    }

    private static string WriteDot(GameData data, WorldMap world, List<Edge> edges,
        List<PuzzleNode> puzzles, HashSet<int> reachable, HashSet<int> solved,
        HashSet<int> inventory, List<string> problems)
    {
        var sb = new StringBuilder();
        var items = new Dictionary<int, string>();
        var positions = GridPositions(world);
        sb.AppendLine("digraph MissionReachability {");
        sb.AppendLine("  rankdir=LR;");
        int solvedPuzzleCount = puzzles.Count(p => solved.Contains(p.ZoneId));
        sb.AppendLine("  graph [label=\"Mission reachability: " + Escape(world.Mission?.Name ?? "(unnamed)") + "\\n" +
                      $"{puzzles.Count} puzzle sectors; {solvedPuzzleCount} solved; {problems.Count} issue(s)\", labelloc=t, fontsize=18];");
        sb.AppendLine("  node [fontname=Helvetica, fontsize=9];");

        var zoneIds = world.Connections.Keys
            .Concat(world.Sectors.Keys)
            .Concat(edges.SelectMany(e => new[] { e.From, e.To }))
            .Distinct().OrderBy(id => id);
        foreach (int zoneId in zoneIds)
        {
            string zoneName = zoneId >= 0 && zoneId < data.Zones.Count ? data.Zones[zoneId].Name ?? "" : "";
            string sectorType = world.Sectors.TryGetValue(zoneId, out var sector) ? sector.Type.ToString() : "zone";
            bool isPuzzle = world.Sectors.TryGetValue(zoneId, out var puzzleSector) &&
                zoneId == puzzleSector.Zone && puzzleSector.PuzzleIndex >= 0;
            bool puzzleSolved = isPuzzle && solved.Contains(zoneId);
            string status = !reachable.Contains(zoneId) ? "unreachable" : puzzleSolved ? "puzzle solved" : "reachable";
            var fill = !reachable.Contains(zoneId) ? "#ffd8d8" : puzzleSolved ? "#c9f2cf" : "#dbeafe";
            sb.AppendLine($"  z{zoneId} [shape=box, style=filled, fillcolor=\"{fill}\", label=\"Z{zoneId} {Escape(zoneName)}\\n{Escape(sectorType)} — {status}\"];");
        }

        foreach (var node in puzzles)
        {
            string pn = $"p{node.ZoneId}";
            string label = $"{node.Puzzle.Type} #{node.Puzzle.Id} / sector {node.Sector.PuzzleIndex}" +
                $"\\n{(node.Sector.UsedAlternateStrain ? "strain 1" : "strain 2")}; requires {ItemName(data, node.Sector.RequiredItem)}";
            sb.AppendLine($"  {pn} [shape=hexagon, style=filled, fillcolor=\"{(solved.Contains(node.ZoneId) ? "#b7e4c7" : "#ffe7a3")}\", label=\"{Escape(label)}\"];");
            sb.AppendLine($"  z{node.ZoneId} -> {pn} [label=\"contains\"];");
            AddItemRequirement(node.Sector.RequiredItem, pn);
            AddItemRequirement(node.Sector.AdditionalRequiredItem, pn);
            AddItemReward(node.Sector.FindItem, pn);
            AddItemReward(node.Sector.AdditionalGainItem, pn);
        }

        foreach (var (zoneId, sector) in world.Sectors.Where(kv => kv.Value.PuzzleIndex < 0 && kv.Value.FindItem > 0))
        {
            string item = ItemNode(sector.FindItem);
            sb.AppendLine($"  z{zoneId} -> {item} [label=\"find\"];");
        }

        foreach (var edge in edges.Distinct())
        {
            int gateItem = TransitionRequirement(world, edge, positions);
            string style = edge.IsTravel ? "bold" : gateItem > 0 ? "dashed" : "solid";
            string label = gateItem > 0 ? $"{edge.Label} (needs {ItemName(data, gateItem)})" : edge.Label;
            sb.AppendLine($"  z{edge.From} -> z{edge.To} [label=\"{Escape(label)}\", style={style}];");
        }

        if (world.StartingItemId is int startItem && startItem > 0)
        {
            sb.AppendLine("  yoda [shape=ellipse, style=filled, fillcolor=\"#d8c7ff\", label=\"Yoda\"];");
            sb.AppendLine($"  yoda -> {ItemNode(startItem)} [label=\"briefing item\"];");
        }

        foreach (var (itemId, itemName) in items.OrderBy(pair => pair.Key))
            sb.AppendLine($"  i{itemId} [shape=ellipse, style=filled, fillcolor=\"{(inventory.Contains(itemId) ? "#c9f2cf" : "#fff4ce")}\", label=\"{Escape($"{itemName} ({itemId})")}\"];");

        sb.AppendLine("}");
        return sb.ToString();

        void AddItemRequirement(int itemId, string puzzleNode)
        {
            if (itemId <= 0) return;
            sb.AppendLine($"  {ItemNode(itemId)} -> {puzzleNode} [label=\"requires\"];");
        }

        void AddItemReward(int itemId, string puzzleNode)
        {
            if (itemId <= 0) return;
            sb.AppendLine($"  {puzzleNode} -> {ItemNode(itemId)} [label=\"gives\"];");
        }

        string ItemNode(int itemId)
        {
            items[itemId] = ItemName(data, itemId);
            return $"i{itemId}";
        }

    }

    private static int TransitionRequirement(WorldMap world, Edge edge,
        Dictionary<int, (int x, int y)> positions)
    {
        if (edge.IsTravel && world.Sectors.TryGetValue(edge.From, out var transport))
            return Math.Max(0, transport.RequiredItem);

        foreach (var (source, destination) in new[] { (edge.From, edge.To), (edge.To, edge.From) })
        {
            if (!world.Sectors.TryGetValue(source, out var sector) || sector.RequiredItem <= 0 ||
                !positions.TryGetValue(source, out var from) || !positions.TryGetValue(destination, out var to))
                continue;
            bool pointsAtTarget = sector.Type switch
            {
                ZoneType.BlockadeNorth => from.x == to.x && from.y == to.y + 1,
                ZoneType.BlockadeSouth => from.x == to.x && from.y + 1 == to.y,
                ZoneType.BlockadeEast => from.y == to.y && from.x + 1 == to.x,
                ZoneType.BlockadeWest => from.y == to.y && from.x == to.x + 1,
                _ => false,
            };
            if (pointsAtTarget) return sector.RequiredItem;
        }
        return 0;
    }

    private static string ItemName(GameData data, int id) => id <= 0
        ? "none"
        : data.TileNames.GetValueOrDefault(id, $"item {id}");

    private static string Escape(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\r", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal);
}
