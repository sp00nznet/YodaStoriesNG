namespace YodaStoriesNG.Engine.Game;

/// <summary>
/// Represents an NPC instance in a zone.
/// </summary>
public class NPC
{
    public int CharacterId { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public Direction Direction { get; set; } = Direction.Down;
    public int Health { get; set; } = 100;
    public bool IsAlive => Health > 0;
    public bool IsEnabled { get; set; } = true;

    // Animation
    public int AnimationFrame { get; set; }

    // AI state
    public double MoveTimer { get; set; }
    public double ActionTimer { get; set; }

    /// <summary>
    /// Creates an NPC from a zone object.
    /// </summary>
    public static NPC FromZoneObject(Data.ZoneObject obj)
    {
        return new NPC
        {
            CharacterId = obj.Argument,
            X = obj.X,
            Y = obj.Y,
            Direction = Direction.Down,
            Health = 100,
            IsEnabled = true
        };
    }
}
