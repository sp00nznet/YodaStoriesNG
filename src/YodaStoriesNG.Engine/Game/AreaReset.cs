using YodaStoriesNG.Engine.Data;

namespace YodaStoriesNG.Engine.Game;

public unsafe partial class GameEngine
{
    private readonly Dictionary<Zone, AreaInitialState> _initialAreas = new();
    private readonly HashSet<Zone> _areasWithMovedObjects = new();
    private sealed record AreaInitialState(ushort[,,] Tiles, ZoneType Type,
        (ZoneObject Object, bool Enabled, ushort Argument)[] Hotspots,
        int? Counter, int? Random, int? Initialized, int? SectorCounter, HashSet<(int Zone, int Action)> DisabledActions);

    private void CaptureInitialArea(Zone zone)
    {
        if (_initialAreas.ContainsKey(zone)) return;
        _initialAreas[zone] = new AreaInitialState((ushort[,,])zone.TileGrid.Clone(), zone.Type,
            zone.Objects.Select(o => (o, o.Enabled, o.Argument)).ToArray(),
            _state.Counters.TryGetValue(zone.Id, out var counter) ? counter : null,
            _state.ZoneRandom.TryGetValue(zone.Id, out var random) ? random : null,
            _state.Variables.TryGetValue(zone.Id + 1000, out var initialized) ? initialized : null,
            _state.Variables.TryGetValue(zone.Id + 3000, out var sectorCounter) ? sectorCounter : null,
            _state.DisabledActions.Where(a => a.Zone == zone.Id).ToHashSet());
    }

    private void MarkObjectsMoved(Zone zone)
    {
        CaptureInitialArea(zone);
        _areasWithMovedObjects.Add(zone);
    }

    private void ResetUnsolvedArea()
    {
        if (_state.CurrentZone is not { } zone || !_areasWithMovedObjects.Remove(zone)) return;
        bool solved = _state.IsZoneSolved(zone.Id) ||
            _worldGenerator?.CurrentWorld?.Sectors.GetValueOrDefault(zone.Id)?.Solved == true;
        if (solved || !_initialAreas.TryGetValue(zone, out var initial)) return;

        // Vehicle state belongs to travel, rather than the retryable puzzle layout.
        bool Vehicle(ZoneObject o) => o.Type is ZoneObjectType.XWingFromDagobah or ZoneObjectType.XWingToDagobah or
            ZoneObjectType.VehicleToPrimary or ZoneObjectType.VehicleToSecondary;
        bool transportArea = zone.Objects.Any(Vehicle);
        var liveTiles = zone.TileGrid;
        var flightActions = zone.Actions.Select((action, index) => (action, index)).Where(pair =>
            pair.action.Conditions.Any(c => c.Opcode == ConditionOpcode.EnterByPlane) ||
            _gameData!.GameType == GameType.YodaStories && pair.action.Instructions.Any(i =>
                i.Opcode == InstructionOpcode.PlaceTile && i.Arguments.Count >= 4 && i.Arguments[3] is >= 948 and <= 955)).ToArray();
        var flightVariables = flightActions.SelectMany(a => a.action.Conditions)
            .Where(c => c.Opcode == ConditionOpcode.IsVariable && c.Arguments.Count >= 4)
            .Select(c => (X: (int)c.Arguments[1], Y: (int)c.Arguments[2], Layer: (int)c.Arguments[3],
                Value: zone.GetTile(c.Arguments[1], c.Arguments[2], c.Arguments[3]))).ToArray();
        var disabledFlight = _state.DisabledActions.Where(a => a.Zone == zone.Id && flightActions.Any(f => f.index == a.Action)).ToArray();
        zone.TileGrid = (ushort[,,])initial.Tiles.Clone();
        if (transportArea && _gameData!.GameType == GameType.YodaStories)
            for (int y = 0; y < zone.Height; y++) for (int x = 0; x < zone.Width; x++) for (int layer = 0; layer < 3; layer++)
                if (liveTiles[y,x,layer] is >= 948 and <= 955 || initial.Tiles[y,x,layer] is >= 948 and <= 955)
                    zone.SetTile(x,y,layer,liveTiles[y,x,layer]);
        foreach (var variable in flightVariables)
            zone.SetTile(variable.X, variable.Y, variable.Layer, variable.Value);
        zone.Type = initial.Type;
        foreach (var (hotspot, enabled, argument) in initial.Hotspots)
        { if (!Vehicle(hotspot)) { hotspot.Enabled = enabled; hotspot.Argument = argument; } }
        if (!transportArea)
        {
            Restore(_state.Counters, zone.Id, initial.Counter);
            Restore(_state.ZoneRandom, zone.Id, initial.Random);
            Restore(_state.Variables, zone.Id + 1000, initial.Initialized);
            Restore(_state.Variables, zone.Id + 3000, initial.SectorCounter);
        }

        // Item handoffs remain one-time events even when the physical puzzle is retried.
        var rewardActions = _state.DisabledActions.Where(a => a.Zone == zone.Id && a.Action < zone.Actions.Count &&
            zone.Actions[a.Action].Instructions.Any(i => i.Opcode is InstructionOpcode.AddItem or InstructionOpcode.RemoveItem)).ToArray();
        _state.DisabledActions.RemoveWhere(a => a.Zone == zone.Id);
        _state.DisabledActions.UnionWith(initial.DisabledActions);
        _state.DisabledActions.UnionWith(rewardActions);
        _state.DisabledActions.UnionWith(disabledFlight);

        for (int y = 0; y < zone.Height; y++)
        for (int x = 0; x < zone.Width; x++)
        {
            bool collected = _state.IsObjectCollected(zone.Id, x, y) ||
                _state.CollectedObjects.Contains($"{zone.Id}_{x}_{y}_tile") ||
                _state.CollectedObjects.Contains($"{zone.Id}_{x}_{y}_r2d2");
            ushort tile = zone.GetTile(x, y, 1);
            if (collected && tile < _gameData!.Tiles.Count &&
                (_gameData.Tiles[tile].IsItem || _gameData.Tiles[tile].IsWeapon || tile == WorldGenerator.TILE_LOCATOR))
                zone.SetTile(x, y, 1, ushort.MaxValue);
        }
        _sectorPuzzles?.ResetArea(zone.Id);

        static void Restore(Dictionary<int, int> values, int key, int? value)
        { if (value.HasValue) values[key] = value.Value; else values.Remove(key); }
    }

    private void ResetAreaTracking()
    {
        ResetUnsolvedArea();
        _initialAreas.Clear();
        _doorReturnPoints.Clear();
        _areasWithMovedObjects.Clear();
    }
}
