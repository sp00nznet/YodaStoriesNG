namespace YodaStoriesNG.Engine.Data;

/// <summary>
/// Represents a game zone/map area.
/// </summary>
public class Zone
{
    public int Id { get; set; }
    public int Width { get; set; }  // 9 or 18
    public int Height { get; set; } // 9 or 18
    public ZoneFlags Flags { get; set; }
    public Planet Planet { get; set; }
    public ZoneType Type { get; set; }

    // Tile layers: [y, x, layer] - 3 layers per cell
    public ushort[,,] TileGrid { get; set; } = null!;

    // Objects placed in this zone
    public List<ZoneObject> Objects { get; set; } = new();

    // Action scripts for this zone
    public List<Action> Actions { get; set; } = new();

    // Zone auxiliary data
    public ZoneAuxData? AuxData { get; set; }
    public ZoneAux2Data? Aux2Data { get; set; }
    public ZoneAux3Data? Aux3Data { get; set; }
    public ZoneAux4Data? Aux4Data { get; set; }

    /// <summary>
    /// Gets the tile ID at the specified position and layer.
    /// </summary>
    public ushort GetTile(int x, int y, int layer)
    {
        if (x < 0 || x >= Width || y < 0 || y >= Height || layer < 0 || layer >= 3)
            return 0xFFFF;
        return TileGrid[y, x, layer];
    }

    /// <summary>
    /// Sets the tile ID at the specified position and layer.
    /// </summary>
    public void SetTile(int x, int y, int layer, ushort tileId)
    {
        if (x >= 0 && x < Width && y >= 0 && y < Height && layer >= 0 && layer < 3)
            TileGrid[y, x, layer] = tileId;
    }
}

[Flags]
public enum ZoneFlags : byte
{
    None = 0,
    Unknown1 = 1 << 0,
    Unknown2 = 1 << 1,
    Unknown3 = 1 << 2,
    Unknown4 = 1 << 3,
}

public enum Planet : byte
{
    None = 0,
    Desert = 1,     // Tatooine
    Snow = 2,       // Hoth
    Forest = 3,     // Endor
    Swamp = 5,      // Dagobah
}

public enum ZoneType
{
    None,
    Empty,
    BlockadeNorth,
    BlockadeSouth,
    BlockadeEast,
    BlockadeWest,
    TravelStart,
    TravelEnd,
    Room,
    Load,
    Goal,
    Town,
    Win,
    Lose,
    Trade,
    Use,
    Find,
    FindTheForce,
}

/// <summary>
/// An object placed within a zone.
/// </summary>
public class ZoneObject
{
    public ZoneObjectType Type { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public ushort Argument { get; set; }  // Context-dependent (item ID, destination zone, etc.)
}

public enum ZoneObjectType : ushort
{
    Trigger = 0x00,
    SpawnLocation = 0x01,
    ForceLocation = 0x02,
    VehicleToSecondary = 0x03,
    VehicleToPrimary = 0x04,
    LocatorItem = 0x05,
    CrateItem = 0x06,
    PuzzleNPC = 0x07,
    CrateWeapon = 0x08,
    DoorEntrance = 0x09,
    DoorExit = 0x0A,
    Unused0B = 0x0B,
    Lock = 0x0C,
    Teleporter = 0x0D,
    XWingFromDagobah = 0x0E,
    XWingToDagobah = 0x0F,
}

/// <summary>
/// Represents an entity defined in IZAX data (NPC spawn with associated item).
/// </summary>
public class IZAXEntity
{
    public ushort CharacterId { get; set; }
    public ushort X { get; set; }
    public ushort Y { get; set; }
    public ushort ItemTileId { get; set; }
    public ushort ItemQuantity { get; set; }
    public byte[] Data { get; set; } = new byte[6];
}

/// <summary>
/// IZAX auxiliary data structure.
/// </summary>
public class ZoneAuxData
{
    public byte[] RawData { get; set; } = Array.Empty<byte>();
    public List<IZAXEntity> Entities { get; set; } = new();
}

/// <summary>
/// IZX2 auxiliary data structure.
/// </summary>
public class ZoneAux2Data
{
    public byte[] RawData { get; set; } = Array.Empty<byte>();
}

/// <summary>
/// IZX3 auxiliary data structure.
/// </summary>
public class ZoneAux3Data
{
    public byte[] RawData { get; set; } = Array.Empty<byte>();
}

/// <summary>
/// IZX4 auxiliary data structure.
/// </summary>
public class ZoneAux4Data
{
    public byte[] RawData { get; set; } = Array.Empty<byte>();
}
