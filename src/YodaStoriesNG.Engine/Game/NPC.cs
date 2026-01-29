namespace YodaStoriesNG.Engine.Game;

/// <summary>
/// NPC behavior type for AI.
/// </summary>
public enum NPCBehavior
{
    Stationary,  // Doesn't move
    Wandering,   // Moves randomly
    Chasing,     // Chases the player (enemy)
    Fleeing      // Runs away from player
}

/// <summary>
/// Represents an NPC instance in a zone.
/// </summary>
public class NPC
{
    public int CharacterId { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int StartX { get; set; }  // Original spawn position
    public int StartY { get; set; }
    public Direction Direction { get; set; } = Direction.Down;
    public int Health { get; set; } = 100;
    public int MaxHealth { get; set; } = 100;
    public bool IsAlive => Health > 0;
    public bool IsEnabled { get; set; } = true;
    public bool IsHostile { get; set; } = false;

    // AI behavior
    public NPCBehavior Behavior { get; set; } = NPCBehavior.Wandering;
    public int WanderRadius { get; set; } = 3;  // How far from start position to wander

    // Animation
    public int AnimationFrame { get; set; }

    // AI state/timers
    public double MoveTimer { get; set; }
    public double ActionTimer { get; set; }
    public double MoveCooldown { get; set; } = 0.5;  // Time between moves
    public double AttackCooldown { get; set; } = 1.0;  // Time between attacks

    // Combat
    public int Damage { get; set; } = 10;
    public int AttackRange { get; set; } = 1;

    // Item handoff (from IZAX data)
    public int? CarriedItemId { get; set; }  // Item this NPC will give when interacted with
    public int CarriedItemQuantity { get; set; } = 1;
    public bool HasGivenItem { get; set; } = false;  // Track if item was already given

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
            StartX = obj.X,
            StartY = obj.Y,
            Direction = Direction.Down,
            Health = 100,
            MaxHealth = 100,
            IsEnabled = true,
            Behavior = NPCBehavior.Wandering
        };
    }

    /// <summary>
    /// Gets the distance to another position.
    /// </summary>
    public int DistanceTo(int x, int y)
    {
        return Math.Abs(X - x) + Math.Abs(Y - y);  // Manhattan distance
    }

    /// <summary>
    /// Takes damage and returns true if killed.
    /// </summary>
    public bool TakeDamage(int damage)
    {
        Health -= damage;
        if (Health <= 0)
        {
            Health = 0;
            IsEnabled = false;
            return true;
        }
        return false;
    }
}
