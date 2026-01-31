using System.Text.Json;
using System.Text.Json.Serialization;
using YodaStoriesNG.Engine.Data;

namespace YodaStoriesNG.Engine.Game;

/// <summary>
/// Manages saving and loading game state.
/// </summary>
public class SaveGameManager
{
    private const string SaveFileExtension = ".ysng";
    private const string SaveFileHeader = "YODANG01";

    /// <summary>
    /// Saves the current game state to a file.
    /// </summary>
    public static bool SaveGame(string filePath, GameState state, WorldGenerator worldGen)
    {
        try
        {
            var saveData = new SaveGameData
            {
                Header = SaveFileHeader,
                Version = 1,
                SaveTime = DateTime.Now,

                // Player state
                PlayerX = state.PlayerX,
                PlayerY = state.PlayerY,
                PlayerDirection = state.PlayerDirection,
                Health = state.Health,
                MaxHealth = state.MaxHealth,

                // Current zone
                CurrentZoneId = state.CurrentZoneId,
                PreviousZoneId = state.PreviousZoneId,

                // Inventory and weapons
                Inventory = state.Inventory.ToList(),
                Weapons = state.Weapons.ToList(),
                CurrentWeaponIndex = state.CurrentWeaponIndex,
                SelectedWeapon = state.SelectedWeapon,
                SelectedItem = state.SelectedItem,

                // Game variables
                Variables = new Dictionary<int, int>(state.Variables),
                Counters = new Dictionary<int, int>(state.Counters),

                // Quest state
                SolvedZones = state.SolvedZones.ToList(),
                VisitedZones = state.VisitedZones.ToList(),
                CollectedObjects = state.CollectedObjects.ToList(),
                GamesWon = state.GamesWon,

                // Flags
                HasLocator = state.HasLocator,

                // Camera
                CameraX = state.CameraX,
                CameraY = state.CameraY,

                // X-Wing position
                XWingPositionX = state.XWingPosition?.X,
                XWingPositionY = state.XWingPosition?.Y,
            };

            // Save world map data
            if (worldGen.CurrentWorld != null)
            {
                var world = worldGen.CurrentWorld;
                saveData.WorldData = new SaveWorldData
                {
                    Planet = world.Planet,
                    MissionNumber = world.MissionNumber,
                    StartingZoneId = world.StartingZoneId,
                    LandingZoneId = world.LandingZoneId,
                    ObjectiveZoneId = world.ObjectiveZoneId,
                    YodaZoneId = world.YodaZoneId,
                    XWingZoneId = world.XWingZoneId,
                    TheForceZoneId = world.TheForceZoneId,
                    LandingPositionX = world.LandingPosition.x,
                    LandingPositionY = world.LandingPosition.y,
                    ObjectivePositionX = world.ObjectivePosition.x,
                    ObjectivePositionY = world.ObjectivePosition.y,
                    YodaPositionX = world.YodaPosition.x,
                    YodaPositionY = world.YodaPosition.y,
                    TheForcePositionX = world.TheForcePosition.x,
                    TheForcePositionY = world.TheForcePosition.y,
                    DagobahZones = world.DagobahZones.ToList(),
                    StartingItemId = world.StartingItemId,
                    RequiredItems = world.RequiredItems.ToList(),
                };

                // Save grid
                if (world.Grid != null)
                {
                    int width = world.Grid.GetLength(1);
                    int height = world.Grid.GetLength(0);
                    saveData.WorldData.GridWidth = width;
                    saveData.WorldData.GridHeight = height;
                    saveData.WorldData.GridData = new List<int?>();
                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            saveData.WorldData.GridData.Add(world.Grid[y, x]);
                        }
                    }
                }

                // Save connections
                saveData.WorldData.Connections = world.Connections.ToDictionary(
                    kv => kv.Key,
                    kv => new SaveZoneConnections
                    {
                        North = kv.Value.North,
                        South = kv.Value.South,
                        East = kv.Value.East,
                        West = kv.Value.West
                    }
                );

                // Save room connections
                saveData.WorldData.RoomConnections = new Dictionary<int, List<int>>(world.RoomConnections);
                saveData.WorldData.RoomParents = new Dictionary<int, int>(world.RoomParents);

                // Save mission data
                if (world.Mission != null)
                {
                    saveData.MissionData = new SaveMissionData
                    {
                        MissionNumber = world.Mission.MissionNumber,
                        Name = world.Mission.Name,
                        Description = world.Mission.Description,
                        CurrentStep = world.Mission.CurrentStep,
                        IsCompleted = world.Mission.IsCompleted,
                    };
                }
            }

            // Serialize to JSON
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Converters = { new JsonStringEnumConverter() }
            };

            var json = JsonSerializer.Serialize(saveData, options);
            File.WriteAllText(filePath, json);

            Console.WriteLine($"Game saved to: {filePath}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to save game: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Loads game state from a file.
    /// </summary>
    public static SaveGameData? LoadGame(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                Console.WriteLine($"Save file not found: {filePath}");
                return null;
            }

            var json = File.ReadAllText(filePath);
            var options = new JsonSerializerOptions
            {
                Converters = { new JsonStringEnumConverter() }
            };

            var saveData = JsonSerializer.Deserialize<SaveGameData>(json, options);

            if (saveData == null || saveData.Header != SaveFileHeader)
            {
                Console.WriteLine("Invalid save file format");
                return null;
            }

            Console.WriteLine($"Game loaded from: {filePath}");
            return saveData;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load game: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Applies loaded save data to the game state.
    /// </summary>
    public static void ApplyToGameState(SaveGameData saveData, GameState state)
    {
        // Player state
        state.PlayerX = saveData.PlayerX;
        state.PlayerY = saveData.PlayerY;
        state.PlayerDirection = saveData.PlayerDirection;
        state.Health = saveData.Health;
        state.MaxHealth = saveData.MaxHealth;

        // Current zone
        state.CurrentZoneId = saveData.CurrentZoneId;
        state.PreviousZoneId = saveData.PreviousZoneId;

        // Inventory and weapons
        state.Inventory = saveData.Inventory.ToList();
        state.Weapons = saveData.Weapons.ToList();
        state.CurrentWeaponIndex = saveData.CurrentWeaponIndex;
        state.SelectedWeapon = saveData.SelectedWeapon;
        state.SelectedItem = saveData.SelectedItem;

        // Game variables
        state.Variables = new Dictionary<int, int>(saveData.Variables);
        state.Counters = new Dictionary<int, int>(saveData.Counters);

        // Quest state
        state.SolvedZones = new HashSet<int>(saveData.SolvedZones);
        state.VisitedZones = new HashSet<int>(saveData.VisitedZones);
        state.CollectedObjects = new HashSet<string>(saveData.CollectedObjects);
        state.GamesWon = saveData.GamesWon;

        // Flags
        state.HasLocator = saveData.HasLocator;

        // Camera
        state.CameraX = saveData.CameraX;
        state.CameraY = saveData.CameraY;

        // X-Wing position
        if (saveData.XWingPositionX.HasValue && saveData.XWingPositionY.HasValue)
            state.XWingPosition = (saveData.XWingPositionX.Value, saveData.XWingPositionY.Value);
        else
            state.XWingPosition = null;
    }

    /// <summary>
    /// Gets the default save directory.
    /// </summary>
    public static string GetSaveDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var saveDir = Path.Combine(appData, "YodaStoriesNG", "saves");
        Directory.CreateDirectory(saveDir);
        return saveDir;
    }

    /// <summary>
    /// Gets the default save file path.
    /// </summary>
    public static string GetDefaultSavePath()
    {
        return Path.Combine(GetSaveDirectory(), $"quicksave{SaveFileExtension}");
    }

    /// <summary>
    /// Lists all save files in the save directory.
    /// </summary>
    public static List<SaveFileInfo> ListSaveFiles()
    {
        var saveDir = GetSaveDirectory();
        var files = new List<SaveFileInfo>();

        foreach (var file in Directory.GetFiles(saveDir, $"*{SaveFileExtension}"))
        {
            try
            {
                var info = new FileInfo(file);
                files.Add(new SaveFileInfo
                {
                    FilePath = file,
                    FileName = Path.GetFileNameWithoutExtension(file),
                    LastModified = info.LastWriteTime
                });
            }
            catch { }
        }

        return files.OrderByDescending(f => f.LastModified).ToList();
    }
}

/// <summary>
/// Information about a save file.
/// </summary>
public class SaveFileInfo
{
    public string FilePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public DateTime LastModified { get; set; }
}

/// <summary>
/// Complete save game data structure.
/// </summary>
public class SaveGameData
{
    public string Header { get; set; } = "";
    public int Version { get; set; }
    public DateTime SaveTime { get; set; }

    // Player state
    public int PlayerX { get; set; }
    public int PlayerY { get; set; }
    public Direction PlayerDirection { get; set; }
    public int Health { get; set; }
    public int MaxHealth { get; set; }

    // Current zone
    public int CurrentZoneId { get; set; }
    public int PreviousZoneId { get; set; }

    // Inventory
    public List<int> Inventory { get; set; } = new();
    public List<int> Weapons { get; set; } = new();
    public int CurrentWeaponIndex { get; set; }
    public int? SelectedWeapon { get; set; }
    public int? SelectedItem { get; set; }

    // Game variables
    public Dictionary<int, int> Variables { get; set; } = new();
    public Dictionary<int, int> Counters { get; set; } = new();

    // Quest state
    public List<int> SolvedZones { get; set; } = new();
    public List<int> VisitedZones { get; set; } = new();
    public List<string> CollectedObjects { get; set; } = new();
    public int GamesWon { get; set; }

    // Flags
    public bool HasLocator { get; set; }

    // Camera
    public int CameraX { get; set; }
    public int CameraY { get; set; }

    // X-Wing
    public int? XWingPositionX { get; set; }
    public int? XWingPositionY { get; set; }

    // World data
    public SaveWorldData? WorldData { get; set; }
    public SaveMissionData? MissionData { get; set; }
}

/// <summary>
/// World map save data.
/// </summary>
public class SaveWorldData
{
    public Planet Planet { get; set; }
    public int MissionNumber { get; set; }
    public int StartingZoneId { get; set; }
    public int LandingZoneId { get; set; }
    public int ObjectiveZoneId { get; set; }
    public int? YodaZoneId { get; set; }
    public int? XWingZoneId { get; set; }
    public int? TheForceZoneId { get; set; }

    public int LandingPositionX { get; set; }
    public int LandingPositionY { get; set; }
    public int ObjectivePositionX { get; set; }
    public int ObjectivePositionY { get; set; }
    public int YodaPositionX { get; set; }
    public int YodaPositionY { get; set; }
    public int TheForcePositionX { get; set; }
    public int TheForcePositionY { get; set; }

    public List<int> DagobahZones { get; set; } = new();
    public int? StartingItemId { get; set; }
    public List<int> RequiredItems { get; set; } = new();

    public int GridWidth { get; set; }
    public int GridHeight { get; set; }
    public List<int?> GridData { get; set; } = new();

    public Dictionary<int, SaveZoneConnections> Connections { get; set; } = new();
    public Dictionary<int, List<int>> RoomConnections { get; set; } = new();
    public Dictionary<int, int> RoomParents { get; set; } = new();
}

/// <summary>
/// Zone connections save data.
/// </summary>
public class SaveZoneConnections
{
    public int? North { get; set; }
    public int? South { get; set; }
    public int? East { get; set; }
    public int? West { get; set; }
}

/// <summary>
/// Mission save data.
/// </summary>
public class SaveMissionData
{
    public int MissionNumber { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public int CurrentStep { get; set; }
    public bool IsCompleted { get; set; }
}
