using YodaStoriesNG.Engine.Data;

namespace YodaStoriesNG.Engine.Game;

public unsafe partial class GameEngine
{
    private List<Zone>? _originalAreas;

    // Capture before world generation or scripts modify the loaded area's data.
    private void CaptureOriginalAreas()
    {
        _originalAreas = _gameData!.Zones.Select(CloneArea).ToList();
    }

    private void RestoreOriginalAreas()
    {
        if (_originalAreas == null) CaptureOriginalAreas();
        _gameData!.Zones = _originalAreas!.Select(CloneArea).ToList();
    }

    private static Zone CloneArea(Zone zone) => new()
    {
        Id = zone.Id, Width = zone.Width, Height = zone.Height,
        Planet = zone.Planet, Type = zone.Type, Name = zone.Name,
        TileGrid = (ushort[,,])zone.TileGrid.Clone(),
        Objects = zone.Objects.Select(o => new ZoneObject
        {
            Type = o.Type, X = o.X, Y = o.Y, Argument = o.Argument,
            FileArgument = o.FileArgument, Enabled = o.Enabled,
        }).ToList(),
        Monsters = zone.Monsters.Select(m => new ZoneMonster
        {
            CharacterId = m.CharacterId, X = m.X, Y = m.Y,
            Loot = m.Loot, DropsLoot = m.DropsLoot, Waypoints = (int[])m.Waypoints.Clone(),
        }).ToList(),
        // Scripts are immutable; disabled actions and counters live in GameState.
        Actions = new(zone.Actions),
        RequiredItems = new(zone.RequiredItems), GoalItems = new(zone.GoalItems),
        ProvidedItems = new(zone.ProvidedItems), Npcs = new(zone.Npcs),
    };
}
