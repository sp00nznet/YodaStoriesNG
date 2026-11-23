using YodaStoriesNG.Engine.Data;
using YodaStoriesNG.Engine.UI;

namespace YodaStoriesNG.Engine.Game;

public unsafe partial class GameEngine
{
    // Keep each room's entrance independently: a nested visit must not replace its parent's route.
    private readonly Dictionary<int, (int Zone, int X, int Y)> _doorReturnPoints = new();

    private static bool IsDoor(ZoneObject obj) => obj.Type is ZoneObjectType.DoorEntrance or ZoneObjectType.DoorExit;

    private void TravelThroughDoor(ZoneObject door)
    {
        if (_gameData == null || _state.CurrentZone == null || !door.Enabled) return;
        int source = _state.CurrentZoneId, destination = door.Argument;
        bool returning = destination == ushort.MaxValue;
        (int X, int Y)? anchor = null;
        if (returning)
        {
            if (_doorReturnPoints.TryGetValue(source, out var entry) && entry.Zone != source)
            {
                destination = entry.Zone;
                anchor = (entry.X, entry.Y);
            }
            else if (_worldGenerator?.CurrentWorld?.RoomParents.TryGetValue(source, out var parent) == true && parent != source)
                destination = parent;
            else
            {
                // Recover old saves/current visits that predate per-room return points.
                var parents = _gameData.Zones.Where(z => z.Id != source && z.Objects.Any(h =>
                    h.Type == ZoneObjectType.DoorEntrance && h.Argument == source)).Select(z => z.Id).Distinct().ToList();
                destination = parents.Count == 1 ? parents[0] : _state.PreviousZoneId;
            }
        }
        if (destination < 0 || destination >= _gameData.Zones.Count || destination == source)
        {
            _messages.ShowMessage("Can't go back through this doorway.", MessageType.Info);
            return;
        }
        var zone = _gameData.Zones[destination];
        if (zone.Width == 0 || zone.Height == 0) return;
        if (anchor == null)
        {
            var reciprocal = zone.Objects.FirstOrDefault(h => h.Enabled && IsDoor(h) && h.Argument == source)
                ?? (!returning ? zone.Objects.FirstOrDefault(h => h.Enabled && h.Type == ZoneObjectType.DoorExit &&
                    h.Argument == ushort.MaxValue) : null);
            if (reciprocal != null) anchor = (reciprocal.X, reciprocal.Y);
        }

        bool CanStand(int x, int y, bool allowDoor = false)
        {
            if (x < 0 || y < 0 || x >= zone.Width || y >= zone.Height || IsGroundBlocked(zone, x, y) ||
                IsScriptedActorTile(zone, x, y) || !allowDoor && zone.Objects.Any(h => h.Enabled && IsDoor(h) && h.X == x && h.Y == y)) return false;
            int tile = zone.GetTile(x, y, 1);
            var spawns=zone.Monsters.Select((m,i)=>(Monster:m,Index:i))
                .Where(p=>p.Monster.X==x && p.Monster.Y==y).ToList();
            bool defeatedMarker=spawns.Count>0 && spawns.All(p=>_state.DefeatedMonsters.Contains($"{zone.Id}_{p.Index}"));
            bool tileOpen=tile >= _gameData.Tiles.Count ||
                (!_gameData.Tiles[tile].IsObject) &&
                (!_gameData.Tiles[tile].IsCharacter || defeatedMarker);
            return tileOpen && spawns.All(p=>_state.DefeatedMonsters.Contains($"{zone.Id}_{p.Index}") ||
                    _state.DisabledMonsters.Contains($"{zone.Id}_{p.Index}"));
        }
        (int X, int Y)? spawn = null;
        if (anchor is { } at)
        {
            // Some rooms seal every neighboring cell until Luke stands on the
            // entrance itself. Preserve that authored landing so its normal
            // StandingOn script can open the room on the next simulation tick.
            int entranceTile=zone.GetTile(at.X,at.Y,1);
            if(DoorwayArrival.HasEntryTrigger(zone,at.X,at.Y,_state) && !IsGroundBlocked(zone,at.X,at.Y) &&
                !ZoneArrival.HasLivingActor(zone,at.X,at.Y,_state) &&
                (entranceTile>=_gameData.Tiles.Count || !_gameData.Tiles[entranceTile].IsObject))
                spawn=at;
            // If the exit-side square now contains a movable prop, keep the
            // hero on the clear threshold so it can be pushed away. Choosing a
            // side square can strand the hero in a disconnected scenery pocket.
            if(spawn==null && DoorwayArrival.HasMovableExitCover(_gameData,zone,at.X,at.Y) &&
                CanStand(at.X,at.Y,allowDoor:true)) spawn=at;
            // Remain beside the actual entrance, never on another staircase or in a
            // disconnected pocket reached by a global nearest-walkable search.
            var offsets = DoorwayArrival.ExitOffsets(zone,at.Y);
            if(spawn==null)
                foreach (var (dx, dy) in offsets)
                    if (CanStand(at.X + dx, at.Y + dy)) { spawn = (at.X + dx, at.Y + dy); break; }
            // A surviving actor can occupy the sole square beside a narrow
            // entrance. Land on its clear doorway instead, so the player can
            // fight or wait without overlapping the actor or skipping a wall.
            if(spawn==null && CanStand(at.X,at.Y,allowDoor:true)) spawn=at;
        }
        else
        {
            // Malformed/unpaired doors have no reciprocal anchor. Prefer a clear center.
            var cells = from y in Enumerable.Range(0, zone.Height)
                        from x in Enumerable.Range(0, zone.Width)
                        where CanStand(x, y)
                        orderby Math.Abs(x - zone.Width / 2) + Math.Abs(y - zone.Height / 2)
                        select (X: x, Y: y);
            foreach (var cell in cells) { spawn = cell; break; }
        }
        if (spawn == null)
        {
            _messages.ShowMessage("The doorway is blocked.", MessageType.Info);
            return;
        }
        if (!returning && door.Type == ZoneObjectType.DoorEntrance)
        {
            _doorReturnPoints[destination] = (source, door.X, door.Y);
            _bot?.NoteDoorEntry(destination,source,door.X,door.Y);
        }
        _state.PreviousZoneId = source;
        _state.SetSectorCounter(destination,_state.GetSectorCounter(source));
        LoadZone(destination, spawn.Value.X, spawn.Value.Y, enteredThroughDoor:true);
    }
}
