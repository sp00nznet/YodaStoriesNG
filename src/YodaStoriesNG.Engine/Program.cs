using YodaStoriesNG.Engine.Game;

namespace YodaStoriesNG.Engine;

class Program
{
    static int Main(string[] args)
    {
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
                var fullPath = Path.Combine(path, file);
                if (File.Exists(fullPath))
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
            var yodaFile = Path.Combine(dataPath, "yodesk.dta");
            var indyFile = Path.Combine(dataPath, "desktop.daw");

            if (File.Exists(yodaFile))
            {
                dataFile = yodaFile;
                gameName = "Yoda Stories";
            }
            else if (File.Exists(indyFile))
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
