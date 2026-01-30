namespace IndyNG.Engine.Data;

/// <summary>
/// Container for all game data loaded from DESKTOP.DAW
/// </summary>
public class GameData
{
    public int Version { get; set; }
    public byte[] Palette { get; set; } = Array.Empty<byte>();
    public List<Tile> Tiles { get; set; } = new();
    public List<Zone> Zones { get; set; } = new();
    public List<Character> Characters { get; set; } = new();
    public List<Puzzle> Puzzles { get; set; } = new();
    public List<string> Sounds { get; set; } = new();
    public Dictionary<int, string> TileNames { get; set; } = new();
}

/// <summary>
/// A 32x32 pixel tile with flags
/// </summary>
public class Tile
{
    public int Id { get; set; }
    public TileFlags Flags { get; set; }
    public byte[] PixelData { get; set; } = new byte[1024]; // 32x32

    public bool IsFloor => (Flags & TileFlags.Floor) != 0;
    public bool IsObject => (Flags & TileFlags.Object) != 0;
    public bool IsDraggable => (Flags & TileFlags.Draggable) != 0;
    public bool IsItem => (Flags & TileFlags.Item) != 0;
    public bool IsWeapon => (Flags & TileFlags.Weapon) != 0;
    public bool IsCharacter => (Flags & TileFlags.Character) != 0;
    public bool IsTransparent => (Flags & TileFlags.Transparent) != 0;
}

[Flags]
public enum TileFlags : uint
{
    None = 0,
    Floor = 0x0001,
    Object = 0x0002,
    Draggable = 0x0004,
    Transparent = 0x0008,
    Item = 0x0010,
    Weapon = 0x0020,
    Character = 0x0100,
    CharEnemy = 0x20000,
    CharFriendly = 0x40000,
}

/// <summary>
/// A game zone (map/room)
/// </summary>
public class Zone
{
    public int Id { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public ZoneType Type { get; set; }
    public Planet Planet { get; set; }

    // 3-layer tile grid [y, x, layer]
    public ushort[,,]? TileGrid { get; set; }

    // Objects in this zone
    public List<ZoneObject> Objects { get; set; } = new();

    // Actions/scripts
    public List<ZoneAction> Actions { get; set; } = new();

    // NPC auxiliary data
    public ZoneAuxData? AuxData { get; set; }

    public ushort GetTile(int x, int y, int layer)
    {
        if (TileGrid == null || x < 0 || x >= Width || y < 0 || y >= Height || layer < 0 || layer >= 3)
            return 0xFFFF;
        return TileGrid[y, x, layer];
    }

    public void SetTile(int x, int y, int layer, ushort tileId)
    {
        if (TileGrid != null && x >= 0 && x < Width && y >= 0 && y < Height && layer >= 0 && layer < 3)
            TileGrid[y, x, layer] = tileId;
    }
}

public enum ZoneType
{
    None = 0,
    Empty = 1,
    Town = 2,
    Goal = 3,
    Trade = 4,
    Use = 5,
    Find = 6,
    BlockNorth = 7,
    BlockSouth = 8,
    BlockEast = 9,
    BlockWest = 10,
}

public enum Planet
{
    None = 0,
    Desert = 1,   // Egypt/Desert areas
    Jungle = 2,   // South American jungle
    Urban = 3,    // City/Urban areas
    Arctic = 4,   // Snow/Ice areas
    Temple = 5,   // Indoor temple areas
}

/// <summary>
/// An object in a zone (NPC, door, item spawn, etc.)
/// </summary>
public class ZoneObject
{
    public ZoneObjectType Type { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Argument { get; set; }
}

public enum ZoneObjectType
{
    Unknown = 0,
    Teleporter = 1,
    SpawnLocation = 2,
    DoorEntrance = 3,
    DoorExit = 4,
    Lock = 5,
    CrateItem = 6,
    PuzzleNPC = 7,
    CrateWeapon = 8,
    TravelStart = 9,   // Vehicle/travel point from home
    TravelEnd = 10,    // Vehicle/travel point to home
    LocatorItem = 11,
}

/// <summary>
/// Auxiliary NPC data for a zone
/// </summary>
public class ZoneAuxData
{
    public byte[] RawData { get; set; } = Array.Empty<byte>();
    public List<ZoneEntity> Entities { get; set; } = new();
}

public class ZoneEntity
{
    public int CharacterId { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int ItemTileId { get; set; }
    public int ItemQuantity { get; set; }
}

/// <summary>
/// A zone action/script
/// </summary>
public class ZoneAction
{
    public List<ActionCondition> Conditions { get; set; } = new();
    public List<ActionInstruction> Instructions { get; set; } = new();
}

public class ActionCondition
{
    public int Opcode { get; set; }
    public List<int> Arguments { get; set; } = new();
}

public class ActionInstruction
{
    public int Opcode { get; set; }
    public List<int> Arguments { get; set; } = new();
    public string? Text { get; set; }
}

/// <summary>
/// Character definition
/// </summary>
public class Character
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int Type { get; set; }
    public CharacterFrames Frames { get; set; } = new();
}

public class CharacterFrames
{
    public ushort[] WalkUp { get; set; } = new ushort[3];
    public ushort[] WalkDown { get; set; } = new ushort[3];
    public ushort[] WalkLeft { get; set; } = new ushort[3];
    public ushort[] WalkRight { get; set; } = new ushort[3];
}

/// <summary>
/// Puzzle definition
/// </summary>
public class Puzzle
{
    public int Id { get; set; }
    public PuzzleType Type { get; set; }
    public int Item1 { get; set; }  // Required item or NPC
    public int Item2 { get; set; }  // Reward item or zone
    public List<string> Strings { get; set; } = new();
}

public enum PuzzleType
{
    Unknown = 0,
    Quest = 1,      // Main quest puzzle
    Transport = 2,  // Travel/transport puzzle
    Trade = 3,      // Item trade puzzle
    Use = 4,        // Use item puzzle
    Goal = 5,       // Mission goal
}
