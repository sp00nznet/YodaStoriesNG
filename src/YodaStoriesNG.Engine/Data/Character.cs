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
    // Walking frames (3 frames per direction)
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

public enum CharacterType : ushort
{
    Hero = 0x01,
    Enemy = 0x02,
    Friendly = 0x04,
}

/// <summary>
/// Character weapon information from CHWP section.
/// </summary>
public class CharacterWeapon
{
    public ushort Reference { get; set; }
    public ushort Health { get; set; }
}

/// <summary>
/// Character auxiliary data from CAUX section.
/// </summary>
public class CharacterAux
{
    public ushort Damage { get; set; }
}
