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
/// </summary>
public enum ConditionOpcode : ushort
{
    ZoneNotInitialized = 0x00,
    ZoneEntered = 0x01,
    Bump = 0x02,
    PlacedItemIs = 0x03,
    Standing = 0x04,
    CounterIs = 0x05,
    RandomIs = 0x06,
    RandomIsGreaterThan = 0x07,
    RandomIsLessThan = 0x08,
    EnterByPlane = 0x09,
    TileAtIs = 0x0A,
    MonsterIsDead = 0x0B,
    HasNoActiveMonsters = 0x0C,
    HasItem = 0x0D,
    RequiredItemIs = 0x0E,
    EndingIs = 0x0F,
    ZoneIsSolved = 0x10,
    NoItemPlaced = 0x11,
    ItemIsPlaced = 0x12,
    HealthIsLessThan = 0x13,
    HealthIsGreaterThan = 0x14,
    Unused15 = 0x15,
    FindItemIs = 0x16,
    Unused17 = 0x17,
    Unused18 = 0x18,
    NpcIs = 0x19,
    HasNpc = 0x1A,
    RandomIsNot = 0x1B,
    RandomIsGreaterOrEqual = 0x1C,
    RandomIsLessOrEqual = 0x1D,
    GamesWonIs = 0x1E,
    DroppedItemIs = 0x1F,
    HasBothItemsPlaced = 0x20,
    HasAllQuestItems = 0x21,
    CounterIsNot = 0x22,
    CounterIsGreaterThan = 0x23,
    CounterIsLessThan = 0x24,
    VariableIsNot = 0x25,
}

/// <summary>
/// Instruction opcodes from the IACT script system.
/// </summary>
public enum InstructionOpcode : ushort
{
    PlaceTile = 0x00,
    RemoveTile = 0x01,
    MoveTile = 0x02,
    DrawTile = 0x03,
    SpeakHero = 0x04,
    SpeakNpc = 0x05,
    SetTileNeedsDisplay = 0x06,
    SetRectNeedsDisplay = 0x07,
    Wait = 0x08,
    Redraw = 0x09,
    PlaySound = 0x0A,
    StopSound = 0x0B,
    RollDice = 0x0C,
    SetCounter = 0x0D,
    AddToCounter = 0x0E,
    SetVariable = 0x0F,
    HideHero = 0x10,
    ShowHero = 0x11,
    MoveHeroTo = 0x12,
    MoveHeroBy = 0x13,
    DisableAction = 0x14,
    EnableHotspot = 0x15,
    DisableHotspot = 0x16,
    EnableMonster = 0x17,
    DisableMonster = 0x18,
    EnableAllMonsters = 0x19,
    DisableAllMonsters = 0x1A,
    DropItem = 0x1B,
    AddItem = 0x1C,
    RemoveItem = 0x1D,
    MarkAsSolved = 0x1E,
    WinGame = 0x1F,
    LoseGame = 0x20,
    ChangeZone = 0x21,
    SetZoneType = 0x22,
    Unknown23 = 0x23,
    Unknown24 = 0x24,
    SetNpc = 0x25,
    AddHealth = 0x26,
    SubtractHealth = 0x27,
    SetHealth = 0x28,
    Unknown29 = 0x29,
    Unknown2A = 0x2A,
    SpeakNpc2 = 0x2B,
}
