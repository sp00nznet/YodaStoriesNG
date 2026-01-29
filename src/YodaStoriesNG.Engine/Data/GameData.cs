namespace YodaStoriesNG.Engine.Data;

/// <summary>
/// Contains all game data loaded from the DTA file.
/// </summary>
public class GameData
{
    /// <summary>
    /// Version information (typically 2.0).
    /// </summary>
    public Version Version { get; set; } = new(2, 0);

    /// <summary>
    /// Startup screen data (STUP section).
    /// </summary>
    public byte[] StartupScreen { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// Sound effect references.
    /// </summary>
    public List<Sound> Sounds { get; set; } = new();

    /// <summary>
    /// All tile/sprite definitions.
    /// </summary>
    public List<Tile> Tiles { get; set; } = new();

    /// <summary>
    /// All zone/map definitions.
    /// </summary>
    public List<Zone> Zones { get; set; } = new();

    /// <summary>
    /// All puzzle definitions.
    /// </summary>
    public List<Puzzle> Puzzles { get; set; } = new();

    /// <summary>
    /// All character definitions.
    /// </summary>
    public List<Character> Characters { get; set; } = new();

    /// <summary>
    /// Tile names from TNAM section.
    /// </summary>
    public Dictionary<int, string> TileNames { get; set; } = new();

    /// <summary>
    /// Gets a tile by ID, or null if not found.
    /// </summary>
    public Tile? GetTile(int id) =>
        id >= 0 && id < Tiles.Count ? Tiles[id] : null;

    /// <summary>
    /// Gets a zone by ID, or null if not found.
    /// </summary>
    public Zone? GetZone(int id) =>
        id >= 0 && id < Zones.Count ? Zones[id] : null;

    /// <summary>
    /// Gets a character by ID, or null if not found.
    /// </summary>
    public Character? GetCharacter(int id) =>
        id >= 0 && id < Characters.Count ? Characters[id] : null;

    /// <summary>
    /// Gets a sound by ID, or null if not found.
    /// </summary>
    public Sound? GetSound(int id) =>
        id >= 0 && id < Sounds.Count ? Sounds[id] : null;
}
