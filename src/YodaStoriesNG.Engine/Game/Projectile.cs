namespace YodaStoriesNG.Engine.Game;

/// <summary>
/// Represents a projectile (blaster bolt, etc.) in flight.
/// </summary>
public class Projectile
{
    public double X { get; set; }
    public double Y { get; set; }
    public double VelocityX { get; set; }
    public double VelocityY { get; set; }
    public int Damage { get; set; } = 25;
    public double LifeTime { get; set; } = 2.0;  // Seconds before despawn
    public bool IsActive { get; set; } = true;
    public int TileId { get; set; }  // Visual representation
    public ProjectileType Type { get; set; } = ProjectileType.Blaster;

    /// <summary>
    /// Updates the projectile position.
    /// </summary>
    public void Update(double deltaTime)
    {
        if (!IsActive) return;

        X += VelocityX * deltaTime;
        Y += VelocityY * deltaTime;
        LifeTime -= deltaTime;

        if (LifeTime <= 0)
            IsActive = false;
    }

    /// <summary>
    /// Gets the current tile position.
    /// </summary>
    public (int X, int Y) TilePosition => ((int)X, (int)Y);
}

public enum ProjectileType
{
    Blaster,
    HeavyBlaster,
    Force
}
