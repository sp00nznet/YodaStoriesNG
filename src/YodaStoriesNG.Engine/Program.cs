using YodaStoriesNG.Engine.Game;

namespace YodaStoriesNG.Engine;

class Program
{
    static int Main(string[] args)
    {
        // Options may appear before or after the data path; keep legacy positional modes.
        var positional = new List<string>();
        int? commandSeed = null;
        bool headless = args.Contains("--headless-bot");
        bool headlessSmooth = false;
        double botSpeed = 20, botSeconds = 1800;
        WorldSize botSize = WorldSize.Medium;
        string? botLogRoot = null, botResume = null;
        for (int i=0;i<args.Length;i++)
        {
            string option=args[i];
            string name=option.Split('=')[0];
            if (headless && name is "--headless-bot" or "--smooth" or "--no-smooth")
            {
                if (name == "--smooth") headlessSmooth = true;
                if (name == "--no-smooth") headlessSmooth = false;
            }
            else if (name is "--speed" or "--bot-seconds" or "--bot-log-dir" or "--world-size" or "--bot-resume")
            {
                string? value=option.Contains('=') ? option[(option.IndexOf('=')+1)..] : ++i<args.Length ? args[i] : null;
                if (!headless || string.IsNullOrWhiteSpace(value))
                { Console.Error.WriteLine($"{name} needs a value and --headless-bot."); return 2; }
                if (name == "--bot-resume") botResume=Path.GetFullPath(value);
                else if (name == "--bot-log-dir") botLogRoot=Path.GetFullPath(value);
                else if (name == "--world-size")
                {
                    if (!Enum.TryParse(value,true,out botSize) || !Enum.IsDefined(botSize))
                    { Console.Error.WriteLine("Use --world-size Small, Medium or Large."); return 2; }
                }
                else
                {
                    if (!double.TryParse(value,System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,out double number) || !double.IsFinite(number) ||
                        number < 0 || name == "--bot-seconds" && (number == 0 || number > 86400))
                    { Console.Error.WriteLine("Speed must be nonnegative (0 = unlimited); bot seconds must be in (0,86400]."); return 2; }
                    if (name == "--speed") botSpeed=number; else botSeconds=number;
                }
            }
            else if (option.Equals("--seed",StringComparison.OrdinalIgnoreCase) || option.StartsWith("--seed=",StringComparison.OrdinalIgnoreCase))
            {
                string? value=option.Contains('=') ? option[(option.IndexOf('=')+1)..] : ++i<args.Length ? args[i] : null;
                if(commandSeed.HasValue || !int.TryParse(value,System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,out int seed))
                {
                    Console.Error.WriteLine("Use --seed <32-bit integer> once. Example: YodaStoriesNG Yoda --seed 12345");
                    return 2;
                }
                commandSeed=seed;
            }
            else positional.Add(option);
        }
        if(commandSeed.HasValue) Environment.SetEnvironmentVariable("YSNG_SEED",commandSeed.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        args=positional.ToArray();
        if(args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine("Usage: YodaStoriesNG [data-directory|game.dta|game.daw] [--seed INTEGER]");
            Console.WriteLine("Headless: Yoda --headless-bot --seed 12345 --speed 20 --bot-seconds 1800 [--smooth]");
            Console.WriteLine("Headless options: --speed 0 runs without throttling; --world-size Medium; --bot-log-dir PATH. Movement defaults to tile mode; --smooth enables smooth movement.");
            Console.WriteLine("Checkpoints are saved automatically during headless runs. Resume: --bot-resume PATH/checkpoint-0001.json.gz (same game data; planner starts fresh).");
            Console.WriteLine("Puzzle catalogue: --puzzle-catalogue DATA OUTPUT_DIRECTORY");
            Console.WriteLine("Offline local plan: --puzzle-plan DATA CHECKPOINT SECTOR AcquireItem|SolveSector|Win ITEM OUTPUT_JSON [MAX_STATES=3000] [SECONDS=30]");
            Console.WriteLine("Use unpacked data. Optional YSNG_PUZZLE_PLAN_DIR loads matching local plans after independent engine replay.");
            Console.WriteLine("Exit codes: 0 mission complete, 1 bot stopped/game over, 2 invalid arguments, 3 simulation limit, 130 cancelled.");
            Console.WriteLine("--seed also accepts --seed=INTEGER and overrides YSNG_SEED. Match game data, world size, and build to reproduce a world.");
            return 0;
        }

        // Needs no game data and opens no window - keep it before everything else.
        if (args.Contains("--self-test"))
            return Dev.SelfTest.Run();

        if(args.Length>=3 && args[0]=="--puzzle-catalogue")
            return Dev.PuzzleCatalogueReport.Run(args[1],args[2]);

        // Bind a local proof to a real generated checkpoint, never an invented
        // inventory or a pristine template with unselected random variants.
        if(args.Length>=7 && args[0]=="--puzzle-plan" && int.TryParse(args[3],out int localSector) &&
            Enum.TryParse<Bot.LocalPuzzleGoalKind>(args[4],true,out var localKind) && Enum.IsDefined(localKind) &&
            localKind!=Bot.LocalPuzzleGoalKind.ReachCell &&
            int.TryParse(args[5],out int localItem))
            return GameEngine.SolvePuzzleOffline(args[1],args[2],args[6],new(localKind,localSector,localItem),
                args.Length>7 && int.TryParse(args[7],out int localStates) ? Math.Clamp(localStates,1,100000) : 3000,
                args.Length>8 && int.TryParse(args[8],out int localSeconds) ? Math.Clamp(localSeconds,1,300) : 30);

        if(args.FirstOrDefault() is "--puzzle-plan" or "--puzzle-catalogue")
        { Console.Error.WriteLine("Invalid puzzle command. Run --help for catalogue and offline-plan arguments.");return 2; }

        if (args.Length >= 2 && args[0] == "--pickup-audit")
            return GameEngine.AuditPickups(args[1]);

        if (args.Length >= 2 && args[0] == "--zone-script-audit")
            return GameEngine.AuditZoneScripts(args[1], args.Length >= 3 ? args[2] : Path.Combine(Path.GetTempPath(), "zone-script-audit.md"));

        // Replay native landing/takeoff scripts through the engine without creating a window.
        if (args.Length >= 2 && args[0] == "--xwing-audit")
            return GameEngine.AuditXWingTravel(args[1], args.Contains("--smooth"));

        // Headless mission graph audit: generate worlds, compute item and zone reachability,
        // then write a Graphviz DOT graph. No renderer or SDL window is initialized.
        if (args.Length >= 2 && args[0] == "--mission-graph")
            return Dev.MissionReachabilityGraph.Run(
                args[1],
                args.Length >= 3 && int.TryParse(args[2], out int auditCount) ? auditCount : 50,
                args.Length >= 4 ? args[3] : null);

        // Headless: generate many worlds from a data file and report how they went.
        if (args.Length >= 2 && args[0] == "--generate")
            return Dev.GenerateWorlds.Run(args[1], args.Length >= 3 ? int.Parse(args[2]) : 200);

        if (headless)
        {
            if (args.Length > 1 || args.Any(a=>a.StartsWith("--")))
            { Console.Error.WriteLine("Supply one data directory/file and supported headless options; see --help."); return 2; }
            string location=args.FirstOrDefault() ?? "Yoda";
            string? file=File.Exists(location) ? location : Data.GameDataFile.Find(location,"yodesk.dta") ??
                Data.GameDataFile.Find(location,"desktop.daw");
            if (file == null) { Console.Error.WriteLine($"No game data found at {location}"); return 2; }
            try { return GameEngine.RunHeadlessBot(file,botSpeed,botSeconds,botSize,headlessSmooth,botLogRoot,botResume); }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }

        Console.WriteLine("========================================");
        Console.WriteLine("     Yoda Stories NG");
        Console.WriteLine("     An open-source reimplementation");
        Console.WriteLine("========================================");
        Console.WriteLine();

        // Determine data path - search for both Yoda Stories and Indiana Jones
        string dataPath;
        string? dataFile = null;
        string gameName = "Yoda Stories";

        if (args.Length > 0)
        {
            // Check if arg is a direct file path
            if (File.Exists(args[0]) && (args[0].EndsWith(".dta", StringComparison.OrdinalIgnoreCase) ||
                                          args[0].EndsWith(".daw", StringComparison.OrdinalIgnoreCase)))
            {
                dataFile = args[0];
                dataPath = Path.GetDirectoryName(args[0]) ?? ".";
                gameName = args[0].EndsWith(".daw", StringComparison.OrdinalIgnoreCase) ? "Indiana Jones" : "Yoda Stories";
            }
            else if (Directory.Exists(args[0]))
            {
                dataPath = args[0];
            }
            else
            {
                dataPath = "Yoda";
            }
        }
        else
        {
            // Look for game data in common locations (Yoda Stories first, then Indiana Jones)
            var possiblePaths = new[]
            {
                // Yoda Stories locations
                (Path.Combine(AppContext.BaseDirectory, "Yoda"), "yodesk.dta", "Yoda Stories"),
                (Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Yoda"), "yodesk.dta", "Yoda Stories"),
                (@"C:\YodaStoriesNG\Yoda", "yodesk.dta", "Yoda Stories"),
                ("Yoda", "yodesk.dta", "Yoda Stories"),
                // Indiana Jones locations
                (Path.Combine(AppContext.BaseDirectory, "Indy"), "desktop.daw", "Indiana Jones"),
                (Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "ida"), "desktop.daw", "Indiana Jones"),
                (@"C:\YodaStoriesNG\ida", "desktop.daw", "Indiana Jones"),
                ("ida", "desktop.daw", "Indiana Jones"),
                ("INDYDESK", "desktop.daw", "Indiana Jones"),
            };

            dataPath = "Yoda"; // Default
            foreach (var (path, file, name) in possiblePaths)
            {
                var fullPath = Data.GameDataFile.Find(path, file);
                if (fullPath != null)
                {
                    dataPath = path;
                    dataFile = fullPath;
                    gameName = name;
                    break;
                }
            }
        }

        // Try to find data file in path if not already found
        if (dataFile == null)
        {
            var yodaFile = Data.GameDataFile.Find(dataPath, "yodesk.dta");
            var indyFile = Data.GameDataFile.Find(dataPath, "desktop.daw");

            if (yodaFile != null)
            {
                dataFile = yodaFile;
                gameName = "Yoda Stories";
            }
            else if (indyFile != null)
            {
                dataFile = indyFile;
                gameName = "Indiana Jones";
            }
        }

        if (dataFile == null || !File.Exists(dataFile))
        {
            Console.WriteLine($"Note: No game data file found.");
            Console.WriteLine("  Looking for: YODESK.DTA (Yoda Stories) or DESKTOP.DAW (Indiana Jones)");
            Console.WriteLine("The game will prompt you to select a data file.");
        }
        else
        {
            Console.WriteLine($"Found: {gameName}");
            Console.WriteLine($"Data path: {dataPath}");
        }
        Console.WriteLine();

        Console.WriteLine("Controls:");
        Console.WriteLine("  Arrow keys / WASD - Move (push blocks)");
        Console.WriteLine("  Shift + Move - Pull blocks");
        Console.WriteLine("  Space - Attack / Talk / Dismiss dialogue");
        Console.WriteLine("  Tab - Toggle weapon");
        Console.WriteLine("  1-8 - Select inventory item");
        Console.WriteLine("  B - Toggle Bot (auto-play)");
        Console.WriteLine("  F - Find zone with NPCs/items");
        Console.WriteLine("  I - Inspect (debug dump to console)");
        Console.WriteLine("  X - Travel (use X-Wing)");
        Console.WriteLine("  M - Toggle sound mute");
        Console.WriteLine("  N/P - Next/Previous zone (debug)");
        Console.WriteLine("  R - Restart game");
        Console.WriteLine("  ESC - Quit");
        Console.WriteLine();

        // Check for diagnostic mode
        bool diagnosticOnly = args.Contains("--diag") || args.Contains("-d");
        bool exportTiles = args.Contains("--export-tiles");

        try
        {
            using var engine = new GameEngine(dataPath);

            if (!engine.Initialize())
            {
                Console.WriteLine("Failed to initialize game engine.");
                return 1;
            }

            if (diagnosticOnly)
            {
                Console.WriteLine("\n=== Diagnostic mode - exiting without starting game ===");
                return 0;
            }

            Console.WriteLine("Starting game...");
            engine.Run();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            return 1;
        }

        Console.WriteLine("Thanks for playing!");
        return 0;
    }
}
