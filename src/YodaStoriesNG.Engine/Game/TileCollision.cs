using YodaStoriesNG.Engine.Data;

namespace YodaStoriesNG.Engine.Game;

/// <summary>Terrain rules shared by movement and route planning.</summary>
public static class TileCollision
{
    public static bool IsGroundBlocked(GameData data, Zone zone, int x, int y)
    {
        int ground = zone.GetTile(x, y, 0);
        if (ground >= data.Tiles.Count || !data.Tiles[ground].IsObject) return false;
        // Bridges and other walkable surfaces may cover solid ground.
        int surface = zone.GetTile(x, y, 1);
        return surface >= data.Tiles.Count || !data.Tiles[surface].IsFloor || data.Tiles[surface].IsObject;
    }
}
