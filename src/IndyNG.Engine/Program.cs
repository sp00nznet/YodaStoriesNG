using Hexa.NET.SDL2;
using IndyNG.Engine.Data;
using IndyNG.Engine.Parsing;
using IndyNG.Engine.Rendering;
using IndyNG.Engine.Game;

namespace IndyNG.Engine;

class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine("========================================");
        Console.WriteLine("     Indiana Jones Desktop Adventures NG");
        Console.WriteLine("     An open-source reimplementation");
        Console.WriteLine("========================================");
        Console.WriteLine();

        // Find data path
        var exePath = AppContext.BaseDirectory;
        var dataPath = Path.Combine(exePath, "..", "..", "..", "..", "..", "INDYDESK");

        if (!Directory.Exists(dataPath))
        {
            // Try alternative paths
            dataPath = Path.Combine(exePath, "INDYDESK");
            if (!Directory.Exists(dataPath))
            {
                dataPath = @"C:\YodaStoriesNG\INDYDESK";
            }
        }

        Console.WriteLine($"Data path: {dataPath}");

        var dawFile = Path.Combine(dataPath, "DESKTOP.DAW");
        if (!File.Exists(dawFile))
        {
            Console.WriteLine($"Error: Cannot find {dawFile}");
            return;
        }

        Console.WriteLine();
        Console.WriteLine("Controls:");
        Console.WriteLine("  Arrow keys / WASD - Move");
        Console.WriteLine("  Space - Attack / Talk / Interact");
        Console.WriteLine("  N/P - Next/Previous zone (debug)");
        Console.WriteLine("  R - Restart game");
        Console.WriteLine("  ESC - Quit");
        Console.WriteLine();

        Console.WriteLine("Loading game data...");

        GameData gameData;
        using (var parser = new DawParser(dawFile))
        {
            gameData = parser.Parse();
        }

        Console.WriteLine();
        Console.WriteLine($"Summary: {gameData.Tiles.Count} tiles, {gameData.Zones.Count} zones, {gameData.Characters.Count} characters, {gameData.Puzzles.Count} puzzles");
        Console.WriteLine();

        // Dump some puzzle info for analysis
        DumpPuzzleInfo(gameData);

        // Initialize SDL and run the game
        RunGame(gameData, dataPath);
    }

    static void DumpPuzzleInfo(GameData gameData)
    {
        Console.WriteLine("=== PUZZLE ANALYSIS ===");

        // Group by type
        var byType = gameData.Puzzles.GroupBy(p => p.Type);
        foreach (var group in byType)
        {
            Console.WriteLine($"\n{group.Key} puzzles ({group.Count()}):");
            foreach (var puzzle in group.Take(5)) // Show first 5 of each type
            {
                Console.WriteLine($"  #{puzzle.Id}: Item1={puzzle.Item1}, Item2={puzzle.Item2}");
                foreach (var str in puzzle.Strings.Where(s => !string.IsNullOrEmpty(s)))
                {
                    Console.WriteLine($"    \"{str}\"");
                }
            }
            if (group.Count() > 5)
                Console.WriteLine($"  ... and {group.Count() - 5} more");
        }

        Console.WriteLine("\n=== END PUZZLE ANALYSIS ===\n");
    }

    static void RunGame(GameData gameData, string dataPath)
    {
        // Initialize SDL
        const uint SDL_INIT_VIDEO = 0x00000020;
        const uint SDL_INIT_AUDIO = 0x00000010;
        const int SDL_WINDOWPOS_CENTERED = 0x2FFF0000;

        if (SDL.Init(SDL_INIT_VIDEO | SDL_INIT_AUDIO) < 0)
        {
            Console.WriteLine($"SDL_Init failed: {SDL.GetErrorS()}");
            return;
        }

        // Create window (32x32 tiles, 10x10 visible area = 320x320, scaled up)
        const int TILE_SIZE = 32;
        const int VISIBLE_TILES = 10;
        const int SCALE = 2;
        const int WINDOW_WIDTH = TILE_SIZE * VISIBLE_TILES * SCALE;
        const int WINDOW_HEIGHT = TILE_SIZE * VISIBLE_TILES * SCALE + 64; // Extra for HUD

        unsafe
        {
            SDLWindow* window = SDL.CreateWindow(
                "Indiana Jones Desktop Adventures NG",
                SDL_WINDOWPOS_CENTERED, SDL_WINDOWPOS_CENTERED,
                WINDOW_WIDTH, WINDOW_HEIGHT,
                (uint)SDLWindowFlags.Shown);

            if (window == null)
            {
                Console.WriteLine($"SDL_CreateWindow failed: {SDL.GetErrorS()}");
                SDL.Quit();
                return;
            }

            SDLRenderer* renderer = SDL.CreateRenderer(window, -1,
                (uint)(SDLRendererFlags.Accelerated | SDLRendererFlags.Presentvsync));

            if (renderer == null)
            {
                Console.WriteLine($"SDL_CreateRenderer failed: {SDL.GetErrorS()}");
                SDL.DestroyWindow(window);
                SDL.Quit();
                return;
            }

            // Create game components
            var gameRenderer = new GameRenderer(renderer, gameData, SCALE);
            var gameEngine = new GameEngine(gameData);

            // Start with a valid zone (prefer one with planet != 255 which indicates a real game zone)
            var startZone = gameData.Zones.FirstOrDefault(z => z.Width > 0 && z.Height > 0 && (int)z.Planet != 255)
                         ?? gameData.Zones.FirstOrDefault(z => z.Width > 0 && z.Height > 0);
            if (startZone != null)
            {
                gameEngine.LoadZone(startZone.Id);
                Console.WriteLine($"Starting in zone {startZone.Id} ({startZone.Width}x{startZone.Height}), Planet={startZone.Planet}");
            }

            // Main game loop
            bool running = true;
            SDLEvent ev;

            while (running)
            {
                // Process events
                while (SDL.PollEvent(&ev) != 0)
                {
                    switch ((SDLEventType)ev.Type)
                    {
                        case SDLEventType.Quit:
                            running = false;
                            break;

                        case SDLEventType.Keydown:
                            var key = ev.Key.Keysym.Scancode;
                            switch (key)
                            {
                                case SDLScancode.Escape:
                                    running = false;
                                    break;
                                case SDLScancode.Up:
                                case SDLScancode.W:
                                    gameEngine.MovePlayer(0, -1);
                                    break;
                                case SDLScancode.Down:
                                case SDLScancode.S:
                                    gameEngine.MovePlayer(0, 1);
                                    break;
                                case SDLScancode.Left:
                                case SDLScancode.A:
                                    gameEngine.MovePlayer(-1, 0);
                                    break;
                                case SDLScancode.Right:
                                case SDLScancode.D:
                                    gameEngine.MovePlayer(1, 0);
                                    break;
                                case SDLScancode.Space:
                                    gameEngine.Interact();
                                    break;
                                case SDLScancode.N:
                                    // Next zone
                                    var nextZone = gameData.Zones
                                        .Where(z => z.Id > gameEngine.CurrentZoneId && z.Width > 0)
                                        .FirstOrDefault();
                                    if (nextZone != null)
                                        gameEngine.LoadZone(nextZone.Id);
                                    break;
                                case SDLScancode.P:
                                    // Previous zone
                                    var prevZone = gameData.Zones
                                        .Where(z => z.Id < gameEngine.CurrentZoneId && z.Width > 0)
                                        .LastOrDefault();
                                    if (prevZone != null)
                                        gameEngine.LoadZone(prevZone.Id);
                                    break;
                                case SDLScancode.R:
                                    // Restart
                                    if (startZone != null)
                                        gameEngine.LoadZone(startZone.Id);
                                    break;
                            }
                            break;
                    }
                }

                // Update game state
                gameEngine.Update(1.0 / 60.0);

                // Render
                SDL.SetRenderDrawColor(renderer, 0, 0, 0, 255);
                SDL.RenderClear(renderer);

                gameRenderer.Render(gameEngine);

                SDL.RenderPresent(renderer);
            }

            // Cleanup
            gameRenderer.Dispose();
            SDL.DestroyRenderer(renderer);
            SDL.DestroyWindow(window);
        }

        SDL.Quit();
    }
}
