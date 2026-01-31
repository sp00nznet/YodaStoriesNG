namespace YodaStoriesNG.Engine.Data;

/// <summary>
/// Represents an IACT action/script from a zone.
/// Actions define game logic triggers and responses.
/// </summary>
public class Action
{
    public List<Condition> Conditions { get; set; } = new();
    public List<Instruction> Instructions { get; set; } = new();
}

/// <summary>
/// A condition that must be met for an action to execute.
/// </summary>
public class Condition
{
    public ConditionOpcode Opcode { get; set; }
    public List<short> Arguments { get; set; } = new();
    public string? Text { get; set; }
}

/// <summary>
/// An instruction to execute when conditions are met.
/// </summary>
public class Instruction
{
    public InstructionOpcode Opcode { get; set; }
    public List<short> Arguments { get; set; } = new();
    public string? Text { get; set; }
}

/// <summary>
/// Condition opcodes from the IACT script system.
/// Based on WebFun reference implementation.
/// </summary>
public enum ConditionOpcode : ushort
{
    ZoneNotInitialized = 0x00,      // Evaluates to true exactly once (used for initialization)
    ZoneEntered = 0x01,             // Hero just entered the zone
    Bump = 0x02,                    // Player bumped tile at (x, y, tile)
    PlacedItemIs = 0x03,            // Placed item matches (x, y, layer, tileA, tileB)
    StandingOn = 0x04,              // Hero at (x, y) and floor tile is (tile)
    Standing = 0x04,                // Alias for StandingOn
    CounterIs = 0x05,               // Zone counter == value
    RandomIs = 0x06,                // Zone random == value
    RandomIsGreaterThan = 0x07,     // Zone random > value
    RandomIsLessThan = 0x08,        // Zone random < value
    EnterByPlane = 0x09,            // Entered zone via X-Wing
    TileAtIs = 0x0A,                // Tile at (x, y, layer) == tile
    MonsterIsDead = 0x0B,           // Monster index is dead
    HasNoActiveMonsters = 0x0C,     // All monsters are dead
    HasItem = 0x0D,                 // Inventory contains item (-1 = zone's puzzle item)
    RequiredItemIs = 0x0E,          // Zone's required item matches
    EndingIs = 0x0F,                // Current goal item id matches
    ZoneIsSolved = 0x10,            // Current zone is solved
    NoItemPlaced = 0x11,            // User did not place an item (x, y, layer, tileA, tileB)
    HasGoalItem = 0x12,             // Hero has the goal item
    ItemIsPlaced = 0x12,            // Alias - checks if an item has been placed
    HealthIsLessThan = 0x13,        // Hero health < value
    HealthIsGreaterThan = 0x14,     // Hero health > value
    Unused = 0x15,                  // Unused
    FindItemIs = 0x16,              // Zone's find item matches
    PlacedItemIsNot = 0x17,         // Placed item does NOT match (x, y, layer, tileA, tileB)
    HeroIsAt = 0x18,                // Hero at position (x, y)
    SectorCounterIs = 0x19,         // Zone sector-counter == value
    NpcIs = 0x19,                   // Alias - check current NPC (game-specific use)
    SectorCounterIsLessThan = 0x1A, // Zone sector-counter < value
    HasNpc = 0x1A,                  // Alias - check if interacting with NPC
    SectorCounterIsGreaterThan = 0x1B, // Zone sector-counter > value
    GamesWonIs = 0x1C,              // Total games won == value
    DropsQuestItemAt = 0x1D,        // Player drops quest item at (x, y)
    HasAnyRequiredItem = 0x1E,      // Inventory has any required item for zone
    CounterIsNot = 0x1F,            // Zone counter != value
    DroppedItemIs = 0x30,           // Check dropped item (game-specific extension)
    RandomIsNot = 0x20,             // Zone random != value
    SectorCounterIsNot = 0x21,      // Zone sector-counter != value
    IsVariable = 0x22,              // Variable at (x, y, layer) == value (same as TileAtIs internally)
    GamesWonIsGreaterThan = 0x23,   // Total games won > value
    CounterIsGreaterThan = 0x24,    // Counter > value (extended)
    CounterIsLessThan = 0x25,       // Counter < value (extended)
}

/// <summary>
/// Instruction opcodes from the IACT script system.
/// Based on WebFun reference implementation.
/// </summary>
public enum InstructionOpcode : ushort
{
    PlaceTile = 0x00,           // Place tile at (x, y, layer, tile)
    RemoveTile = 0x01,          // Remove tile at (x, y, layer)
    MoveTile = 0x02,            // Move tile from (x1, y1, layer) to (x2, y2)
    DrawTile = 0x03,            // Draw tile at (x, y, layer, tile, ?)
    SpeakHero = 0x04,           // Show hero speech bubble (uses text)
    SpeakNpc = 0x05,            // Show NPC speech at (x, y) (uses text)
    SetTileNeedsDisplay = 0x06, // Redraw tile at (x, y)
    SetRectNeedsDisplay = 0x07, // Redraw rect at (x, y, w, h)
    Wait = 0x08,                // Pause for one tick
    Redraw = 0x09,              // Redraw whole scene
    PlaySound = 0x0A,           // Play sound (id)
    StopSound = 0x0B,           // Stop sounds
    RollDice = 0x0C,            // Set zone random to 1..value
    SetCounter = 0x0D,          // Set zone counter = value
    AddToCounter = 0x0E,        // Add value to zone counter
    SetVariable = 0x0F,         // Set variable at (x, y, layer) = value (same as PlaceTile)
    HideHero = 0x10,            // Hide hero sprite
    ShowHero = 0x11,            // Show hero sprite
    MoveHeroTo = 0x12,          // Teleport hero to (x, y)
    MoveHeroBy = 0x13,          // Move hero by (dx, dy, ?, ?, ?)
    DisableAction = 0x14,       // Disable this action permanently
    EnableHotspot = 0x15,       // Enable hotspot (index)
    DisableHotspot = 0x16,      // Disable hotspot (index)
    EnableMonster = 0x17,       // Enable monster (index)
    DisableMonster = 0x18,      // Disable monster (index)
    EnableAllMonsters = 0x19,   // Enable all monsters
    DisableAllMonsters = 0x1A,  // Disable all monsters
    DropItem = 0x1B,            // Drop item at (item, x, y) - item -1 = zone find item
    AddItem = 0x1C,             // Add item to inventory
    RemoveItem = 0x1D,          // Remove item from inventory
    MarkAsSolved = 0x1E,        // Mark zone as solved
    WinGame = 0x1F,             // Win the game
    LoseGame = 0x20,            // Lose the game
    ChangeZone = 0x21,          // Go to zone (id) at (x, y)
    SetSectorCounter = 0x22,    // Set zone sector-counter = value
    SetZoneType = 0x22,         // Alias for SetSectorCounter
    AddToSectorCounter = 0x23,  // Add value to zone sector-counter
    SetRandom = 0x24,           // Set zone random = value
    AddHealth = 0x25,           // Increase health by value (capped at max)
    SubtractHealth = 0x26,      // Decrease health by value (game extension)
    SetHealth = 0x27,           // Set health to value (game extension)
    SpeakNpc2 = 0x28,           // Alternative NPC speech (game extension)
}
