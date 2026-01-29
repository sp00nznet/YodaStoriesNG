namespace YodaStoriesNG.Engine.Data;

/// <summary>
/// Represents a 32x32 pixel tile/sprite from the game.
/// </summary>
public class Tile
{
    public const int Width = 32;
    public const int Height = 32;
    public const int PixelCount = Width * Height; // 1024 bytes

    public int Id { get; set; }
    public TileFlags Flags { get; set; }
    public byte[] PixelData { get; set; } = new byte[PixelCount];

    // Derived properties from flags
    public bool IsTransparent => (Flags & TileFlags.Transparency) != 0;
    public bool IsFloor => (Flags & TileFlags.Floor) != 0;
    public bool IsObject => (Flags & TileFlags.Object) != 0;
    public bool IsDraggable => (Flags & TileFlags.Draggable) != 0;
    public bool IsRoof => (Flags & TileFlags.Roof) != 0;
    public bool IsMap => (Flags & TileFlags.Map) != 0;
    public bool IsWeapon => (Flags & TileFlags.Weapon) != 0;
    public bool IsItem => (Flags & TileFlags.Item) != 0;
    public bool IsCharacter => (Flags & TileFlags.Character) != 0;
}

/// <summary>
/// Tile attribute flags from the DTA file.
/// </summary>
[Flags]
public enum TileFlags : uint
{
    None = 0,

    // Object classification (bits 0-8)
    Transparency = 1 << 0,      // Has transparent pixels
    Floor = 1 << 1,             // Non-colliding, draws behind player
    Object = 1 << 2,            // Colliding, middle layer
    Draggable = 1 << 3,         // Push/pull block
    Roof = 1 << 4,              // Non-colliding, draws above player
    Map = 1 << 5,               // Mini-map tile
    Weapon = 1 << 6,            // Weapon item
    Item = 1 << 7,              // Inventory item
    Character = 1 << 8,         // Character/NPC sprite

    // Weapon type flags (bits 16-19, when Weapon is set)
    WeaponLightBlaster = 1 << 16,
    WeaponHeavyBlaster = 1 << 17,
    WeaponLightsaber = 1 << 18,
    WeaponTheForce = 1 << 19,

    // Item type flags (bits 16-22, when Item is set)
    ItemKeycard = 1 << 16,
    ItemPuzzle1 = 1 << 17,
    ItemPuzzle2 = 1 << 18,
    ItemPuzzle3 = 1 << 19,
    ItemLocator = 1 << 20,
    ItemHealthPack = 1 << 22,

    // Character type flags (bits 16-18, when Character is set)
    CharPlayer = 1 << 16,
    CharEnemy = 1 << 17,
    CharFriendly = 1 << 18,

    // Mini-map flags (bits 17-30, when Map is set)
    MapHome = 1 << 17,
    MapPuzzleSolved = 1 << 18,
    MapPuzzleUnsolved = 1 << 19,
    MapGateway = 1 << 20,
    MapWall = 1 << 21,
    MapObjective = 1 << 22,
}
