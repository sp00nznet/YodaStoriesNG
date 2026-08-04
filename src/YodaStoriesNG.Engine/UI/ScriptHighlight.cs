namespace YodaStoriesNG.Engine.UI;

/// <summary>
/// A position the script editor asks the game renderer to mark, so a script's
/// coordinates can be seen in the world instead of read as numbers.
/// </summary>
public struct ScriptHighlight
{
    public int X { get; set; }
    public int Y { get; set; }
    public HighlightType Type { get; set; }
    public string Label { get; set; }
}

public enum HighlightType
{
    Position,       // Generic position reference (cyan)
    Tile,           // Tile placement/check (yellow)
    Door,           // Door/teleporter (green)
    NPC,            // NPC/character (magenta)
    Item,           // Item location (orange)
    Trigger         // Trigger/hotspot (blue)
}
