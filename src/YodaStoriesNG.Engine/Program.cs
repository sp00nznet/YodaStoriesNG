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

        // Determine data path
        string dataPath;
        if (args.Length > 0 && Directory.Exists(args[0]))
        {
            dataPath = args[0];
        }
        else
        {
            // Look for Yoda folder in common locations
            var possiblePaths = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "Yoda"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Yoda"),
                @"C:\YodaStoriesNG\Yoda",
                "Yoda",
            };

            dataPath = possiblePaths.FirstOrDefault(Directory.Exists) ?? "Yoda";
        }

        var dtaFile = Path.Combine(dataPath, "yodesk.dta");
        if (!File.Exists(dtaFile))
        {
            Console.WriteLine($"Error: Could not find game data file.");
            Console.WriteLine($"Expected: {dtaFile}");
            Console.WriteLine();
            Console.WriteLine("Please ensure the Yoda Stories data files are in the 'Yoda' folder.");
            Console.WriteLine("Usage: YodaStoriesNG.Engine [path-to-yoda-folder]");
            return 1;
        }

        Console.WriteLine($"Data path: {dataPath}");
        Console.WriteLine();

        Console.WriteLine("Controls:");
        Console.WriteLine("  Arrow keys / WASD - Move");
        Console.WriteLine("  Space - Use item / Attack");
        Console.WriteLine("  1-8 - Select inventory item");
        Console.WriteLine("  N/P - Next/Previous zone (debug)");
        Console.WriteLine("  R - Restart game");
        Console.WriteLine("  ESC - Quit");
        Console.WriteLine();

        try
        {
            using var engine = new GameEngine(dataPath);

            if (!engine.Initialize())
            {
                Console.WriteLine("Failed to initialize game engine.");
                return 1;
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
