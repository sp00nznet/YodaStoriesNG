using YodaStoriesNG.Engine.Data;

namespace YodaStoriesNG.Engine.Game;

/// <summary>
/// Represents the current state of the game.
/// </summary>
public class GameState
{
    // Player state
    public int PlayerX { get; set; }
    public int PlayerY { get; set; }
    public Direction PlayerDirection { get; set; } = Direction.Down;
    public int Health { get; set; } = 100;
    public int MaxHealth { get; set; } = 100;

    // Current zone
    public int CurrentZoneId { get; set; }
    public Zone? CurrentZone { get; set; }
    public int PreviousZoneId { get; set; } = -1;  // For "return" doors (65535 destination)

    // Inventory
    public List<int> Inventory { get; set; } = new();
    public int? SelectedWeapon { get; set; }
    public int? SelectedItem { get; set; }

    // Game variables (used by scripts)
    public Dictionary<int, int> Variables { get; set; } = new();
    public Dictionary<int, int> Counters { get; set; } = new();

    // Quest state
    public HashSet<int> SolvedZones { get; set; } = new();
    public int GamesWon { get; set; }

    // Zone tracking
    public HashSet<int> VisitedZones { get; set; } = new();

    // Score tracking
    public DateTime GameStartTime { get; set; } = DateTime.Now;
    public WorldSize WorldSize { get; set; } = WorldSize.Medium;
    public int TotalSectors { get; set; } = 0;  // Total puzzle sectors in this world

    // Game flags
    public bool IsGameOver { get; set; }
    public bool IsGameWon { get; set; }
    public bool IsPaused { get; set; }
    public bool HasLocator { get; set; }  // Has picked up R2D2/locator droid

    // Animation state
    public int AnimationFrame { get; set; }
    public double AnimationTimer { get; set; }

    // Visual feedback
    public double DamageFlashTimer { get; set; }
    public double AttackFlashTimer { get; set; }

    // Attack state
    public bool IsAttacking { get; set; }
    public double AttackTimer { get; set; }

    // Weapons (can have multiple, toggle between them)
    public List<int> Weapons { get; set; } = new();
    public int CurrentWeaponIndex { get; set; }

    // Camera position (for large zones)
    public int CameraX { get; set; }
    public int CameraY { get; set; }

    // X-Wing position (for rendering in Dagobah zones)
    public (int X, int Y)? XWingPosition { get; set; }

    // NPCs in current zone
    public List<NPC> ZoneNPCs { get; set; } = new();

    // Active projectiles
    public List<Projectile> Projectiles { get; set; } = new();

    // Track collected objects by zone (key = "zoneId_x_y")
    public HashSet<string> CollectedObjects { get; set; } = new();

    /// <summary>
    /// Resets the game state for a new game.
    /// </summary>
    public void Reset()
    {
        PlayerX = 4;
        PlayerY = 4;
        PlayerDirection = Direction.Down;
        Health = MaxHealth;
        CurrentZoneId = 0;
        CurrentZone = null;
        Inventory.Clear();
        SelectedWeapon = null;
        SelectedItem = null;
        Variables.Clear();
        Counters.Clear();
        SolvedZones.Clear();
        VisitedZones.Clear();
        IsGameOver = false;
        IsGameWon = false;
        IsPaused = false;
        HasLocator = false;
        AnimationFrame = 0;
        AnimationTimer = 0;
        CameraX = 0;
        CameraY = 0;
        ZoneNPCs.Clear();
        CollectedObjects.Clear();
        IsAttacking = false;
        AttackTimer = 0;
        Weapons.Clear();
        CurrentWeaponIndex = 0;
        Projectiles.Clear();
        GameStartTime = DateTime.Now;
        TotalSectors = 0;
    }

    /// <summary>
    /// Calculates the end-game score (Force Factor for Yoda, Indy Quotient for Indy).
    /// Score is based on: Time, Puzzle Completion, Difficulty, and Exploration.
    /// </summary>
    public (int total, int time, int puzzles, int difficulty, int exploration) CalculateScore()
    {
        // World size as number: Small=1, Medium=2, Large=3, XtraLarge=4
        int worldSizeValue = WorldSize switch
        {
            WorldSize.Small => 1,
            WorldSize.Medium => 2,
            WorldSize.Large => 3,
            WorldSize.XtraLarge => 4,
            _ => 2
        };

        // Time component (200 points max)
        double elapsedSeconds = (DateTime.Now - GameStartTime).TotalSeconds;
        double timeValue = (elapsedSeconds / 60.0) - (5 * worldSizeValue);
        int timeScore;
        if (timeValue <= 0)
            timeScore = 200;
        else
            timeScore = Math.Max(0, 200 - (int)(20 * timeValue));

        // Puzzle completion (100 points max - percentage of sectors solved)
        int puzzleScore = TotalSectors > 0
            ? (int)((SolvedZones.Count * 100.0) / TotalSectors)
            : 100;
        puzzleScore = Math.Min(100, puzzleScore);

        // Difficulty (100 points max - same as puzzle completion for now)
        int difficultyScore = puzzleScore;

        // Exploration (100 points max - percentage of zones visited vs world size)
        int expectedZones = worldSizeValue * 10; // Rough estimate
        int explorationScore = Math.Min(100, (int)((VisitedZones.Count * 100.0) / expectedZones));

        int totalScore = timeScore + puzzleScore + difficultyScore + explorationScore;
        return (totalScore, timeScore, puzzleScore, difficultyScore, explorationScore);
    }

    /// <summary>
    /// Gets a game variable, returning 0 if not set.
    /// </summary>
    public int GetVariable(int id) =>
        Variables.TryGetValue(id, out var value) ? value : 0;

    /// <summary>
    /// Sets a game variable.
    /// </summary>
    public void SetVariable(int id, int value) =>
        Variables[id] = value;

    /// <summary>
    /// Gets a counter, returning 0 if not set.
    /// </summary>
    public int GetCounter(int id) =>
        Counters.TryGetValue(id, out var value) ? value : 0;

    /// <summary>
    /// Sets a counter.
    /// </summary>
    public void SetCounter(int id, int value) =>
        Counters[id] = value;

    /// <summary>
    /// Adds to a counter.
    /// </summary>
    public void AddToCounter(int id, int amount)
    {
        var current = GetCounter(id);
        Counters[id] = current + amount;
    }

    /// <summary>
    /// Checks if the player has an item.
    /// </summary>
    public bool HasItem(int itemId) =>
        Inventory.Contains(itemId);

    /// <summary>
    /// Adds an item to inventory (allows duplicates for stacking).
    /// </summary>
    public void AddItem(int itemId)
    {
        Inventory.Add(itemId);
    }

    /// <summary>
    /// Removes an item from inventory.
    /// </summary>
    public void RemoveItem(int itemId) =>
        Inventory.Remove(itemId);

    /// <summary>
    /// Marks a zone as solved.
    /// </summary>
    public void MarkZoneSolved(int zoneId) =>
        SolvedZones.Add(zoneId);

    /// <summary>
    /// Checks if a zone is solved.
    /// </summary>
    public bool IsZoneSolved(int zoneId) =>
        SolvedZones.Contains(zoneId);

    /// <summary>
    /// Marks a zone as visited.
    /// </summary>
    public void MarkZoneVisited(int zoneId) =>
        VisitedZones.Add(zoneId);

    /// <summary>
    /// Checks if a zone has been visited.
    /// </summary>
    public bool IsZoneVisited(int zoneId) =>
        VisitedZones.Contains(zoneId);

    /// <summary>
    /// Marks an object at a position as collected.
    /// </summary>
    public void MarkObjectCollected(int zoneId, int x, int y) =>
        CollectedObjects.Add($"{zoneId}_{x}_{y}");

    /// <summary>
    /// Checks if an object at a position has been collected.
    /// </summary>
    public bool IsObjectCollected(int zoneId, int x, int y) =>
        CollectedObjects.Contains($"{zoneId}_{x}_{y}");
}

public enum Direction
{
    Up,
    Down,
    Left,
    Right
}
