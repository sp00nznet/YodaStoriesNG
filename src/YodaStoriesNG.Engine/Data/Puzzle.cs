namespace YodaStoriesNG.Engine.Data;

/// <summary>
/// Represents a puzzle definition from the PUZ2 section.
/// </summary>
public class Puzzle
{
    public int Id { get; set; }
    public PuzzleType Type { get; set; }

    // Items involved in this puzzle
    public ushort Item1 { get; set; }
    public ushort Item2 { get; set; }

    // Text strings for the puzzle
    public List<string> Strings { get; set; } = new();

    // Raw data for unknown fields
    public byte[] RawData { get; set; } = Array.Empty<byte>();
}

public enum PuzzleType : short
{
    None = -1,
    Quest = 0,
    Transport = 1,
    Trade = 2,
    Use = 3,
    Goal = 4,
}
