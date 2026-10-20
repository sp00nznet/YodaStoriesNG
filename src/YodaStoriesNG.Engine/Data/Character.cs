namespace YodaStoriesNG.Engine.Data;

/// <summary>
/// Represents a character definition from the CHAR section.
/// </summary>
public class Character
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public CharacterType Type { get; set; }

    // Animation frame tile IDs
    public CharacterFrames Frames { get; set; } = new();

    // Weapon info from CHWP section
    public CharacterWeapon? Weapon { get; set; }

    // Auxiliary data from CAUX section
    public CharacterAux? AuxData { get; set; }
}

/// <summary>
/// Character animation frames for different directions and states.
/// </summary>
public class CharacterFrames
{
    // The CHAR record provides three animation frames for each cardinal direction.
    public ushort[] WalkUp { get; set; } = new ushort[3];
    public ushort[] WalkDown { get; set; } = new ushort[3];
    public ushort[] WalkLeft { get; set; } = new ushort[3];
    public ushort[] WalkRight { get; set; } = new ushort[3];

    // Extension frames (if present)
    public ushort[] ExtensionUp { get; set; } = Array.Empty<ushort>();
    public ushort[] ExtensionDown { get; set; } = Array.Empty<ushort>();
    public ushort[] ExtensionLeft { get; set; } = Array.Empty<ushort>();
    public ushort[] ExtensionRight { get; set; } = Array.Empty<ushort>();
}

/// <summary>
/// The file stores 1 Hero, 2 Enemy, 4 Weapon. Friendly is the engine's own - Yoda Stories'
/// characters are still classified by name (see DtaParser.InferCharacterType).
/// </summary>
public enum CharacterType : ushort
{
    Hero = 0x01,
    Enemy = 0x02,
    Weapon = 0x04,
    Friendly = 0x100,
}

/// <summary>
/// Character weapon information from CHWP section.
/// </summary>
public class CharacterWeapon
{
    /// <summary>
    /// -1 for none. On an enemy, the index of the weapon character it fires; on a weapon
    /// character, the sound it makes.
    /// </summary>
    public short Reference { get; set; }
    public ushort Health { get; set; }
}

/// <summary>
/// Character auxiliary data from CAUX section.
/// </summary>
public class CharacterAux
{
    public short Damage { get; set; }  // -1 for none
}
