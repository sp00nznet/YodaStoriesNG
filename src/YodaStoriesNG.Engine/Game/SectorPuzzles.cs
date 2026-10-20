using YodaStoriesNG.Engine.Data;

namespace YodaStoriesNG.Engine.Game;

/// <summary>
/// The original game's hotspot puzzles, for worlds whose sectors came from
/// <see cref="OriginalWorldGenerator"/>. A port of WebFun's
/// script/hotspot-processing-unit.ts and script/hotspots/spawn-location.ts:
///
///  - entering a zone for the first time lays each enabled item hotspot's tile down on the
///    object layer - which is how the generator's items and trading NPCs appear;
///  - bumping the sector's NPC says what it wants (or thanks you, once solved);
///  - using the sector's required item on that NPC trades it for the find item
///    (Transaction), on the Lock hotspot holding it opens the find item's drop spot
///    (Offer), and on a Lock anywhere else solves blockades; anything else is NoGo.
///
/// Zone scripts still run first; this is what happens when no script claims the item.
/// </summary>
public class SectorPuzzles
{
    private const ZoneObjectType SpawnLocation = (ZoneObjectType)1;
    private const ZoneObjectType DropQuestItem = (ZoneObjectType)0;
    private const ZoneObjectType Lock = (ZoneObjectType)12;

    private readonly GameData _data;
    private readonly GameState _state;
    private readonly WorldMap _world;
    private readonly HashSet<int> _laidDown = new();

    public event Action<string, string>? OnSpeak;
    public event Action<int>? OnSound;

    public SectorPuzzles(GameData data, GameState state, WorldMap world)
    {
        _data = data;
        _state = state;
        _world = world;
    }

    private Sector? Sector => _world.Sectors.GetValueOrDefault(_state.CurrentZoneId);

    /// <summary>
    /// The mission's steps follow the sectors: the first step whose sector is unsolved is
    /// the current one. Returns true once every step is done.
    /// </summary>
    public bool SyncMission()
    {
        // Room and outdoor zones share a sector. Keep script checks and the saved
        // SolvedZones set in agreement with generated puzzle progress.
        foreach (var sector in _world.Sectors.Values.Distinct())
            if (sector.Solved || _world.Sectors.Any(p => ReferenceEquals(p.Value, sector) && _state.IsZoneSolved(p.Key)))
                SetSolved(sector);
        if (_world.Mission is not { } mission) return false;
        int step = mission.PuzzleChain.FindIndex(s =>
            !(s.ZoneId is { } z && _world.Sectors.TryGetValue(z, out var sector) && sector.Solved));
        mission.CurrentStep = step < 0 ? mission.PuzzleChain.Count : step;
        return step < 0;
    }

    /// <summary>The puzzle a sector's step uses, and the one after it (whose item it hands out).</summary>
    private (Puzzle? puzzle, Puzzle? next) PuzzlesFor(Sector sector)
    {
        var strain = sector.UsedAlternateStrain ? _world.Strain1 : _world.Strain2;
        Puzzle? At(int i) => i >= 0 && i < strain.Count ? strain[i] : null;
        return (At(sector.PuzzleIndex), At(sector.PuzzleIndex + 1));
    }

    /// <summary>Allow an unsolved area's generated contents to be laid down again after reset.</summary>
    public void ResetArea(int zoneId) => _laidDown.Remove(zoneId);

    /// <summary>First visit only: put generated items and NPCs where the hotspots say.</summary>
    public void LayDown(Zone zone)
    {
        if (_laidDown.Add(zone.Id))
            Place(zone, includeNpcs: true);
    }

    /// <summary>
    /// After a script has run: an item hotspot whose spot has just been cleared shows its item
    /// (a chest is a tile over the treasure; the script removes the chest). NPCs are left
    /// alone - a script that removes one means it.
    /// </summary>
    public void Refresh(Zone zone) => Place(zone, includeNpcs: false);

    private void Place(Zone zone, bool includeNpcs)
    {
        foreach (var hotspot in zone.Objects)
        {
            int arg = hotspot.Argument == 0xFFFF ? -1 : hotspot.Argument;
            bool holdsTile = (int)hotspot.Type is 0 or 2 or 5 or 6 or 8 || includeNpcs && (int)hotspot.Type is 1 or 7;
            if (!hotspot.Enabled || arg < 0 || !holdsTile) continue;
            if (zone.GetTile(hotspot.X, hotspot.Y, 1) != 0xFFFF) continue;
            if (_state.IsObjectCollected(zone.Id, hotspot.X, hotspot.Y) ||
                _state.CollectedObjects.Contains($"{zone.Id}_{hotspot.X}_{hotspot.Y}_tile") ||
                _state.CollectedObjects.Contains($"{zone.Id}_{hotspot.X}_{hotspot.Y}_r2d2")) continue;
            zone.SetTile(hotspot.X, hotspot.Y, 1, (ushort)arg);
        }
    }

    /// <summary>Whether approaching this tile invokes a generated NPC interaction.</summary>
    public bool IsBumpTarget(int x, int y)
    {
        if (Sector is not { } sector || _state.CurrentZone is not { } zone) return false;
        if (sector.Type == ZoneType.Town)
            return _world.Mission?.GoalPuzzle != null &&
                zone.Objects.Any(h => h.X == x && h.Y == y && h.Type == SpawnLocation) &&
                !zone.Actions.Any(a => a.Conditions.Any(c => c.Opcode == ConditionOpcode.Bump &&
                    c.Arguments.Count > 1 && c.Arguments[0] == x && c.Arguments[1] == y));
        return sector.PuzzleIndex >= 0 && zone.GetTile(x, y, 1) == sector.Npc &&
            zone.Objects.Any(h => h.X == x && h.Y == y && h.Type == SpawnLocation && h.Enabled) &&
            PuzzlesFor(sector).Item1 != null;
    }

    /// <summary>Bumping (x, y). Returns true if it was the sector's NPC, which then spoke.</summary>
    public bool Bump(int x, int y)
    {
        if (Sector is { Type: ZoneType.Town } town && _state.CurrentZone is { } townZone)
            return Briefing(town, townZone, x, y);

        if (Sector is not { PuzzleIndex: >= 0 } sector || _state.CurrentZone is not { } zone) return false;
        var hotspot = zone.Objects.FirstOrDefault(h => h.X == x && h.Y == y && h.Type == SpawnLocation && h.Enabled);
        if (hotspot == null || zone.GetTile(x, y, 1) != sector.Npc) return false;

        var (puzzle, next) = PuzzlesFor(sector);
        if (puzzle == null) return false;
        var text = sector.Solved
            ? Line(puzzle, 1)
            : string.Join(" ", new[] { Line(puzzle, 0), next != null ? Line(next, 2) : "" }.Where(t => t.Length > 0));
        if (text.Length > 0) Speak(NameOf(sector.Npc), text);
        return true;
    }

    /// <summary>
    /// Indiana Jones' Marcus, and Yoda's seat before him (WebFun hotspots/spawn-location.ts):
    /// the town's spawn hotspot that no script answers is where the mission is handed out.
    /// First bump: the Mission puzzle's briefing (string 3) and the chain's first item.
    /// After that: its reminder, "Find the X, Indy!" (string 2).
    /// </summary>
    private bool Briefing(Sector town, Zone zone, int x, int y)
    {
        if (!zone.Objects.Any(h => h.X == x && h.Y == y && h.Type == SpawnLocation)) return false;
        bool scripted = zone.Actions.Any(a => a.Conditions.Any(c =>
            c.Opcode == ConditionOpcode.Bump && c.Arguments.Count > 1 && c.Arguments[0] == x && c.Arguments[1] == y));
        if (scripted || _world.Mission?.GoalPuzzle is not { } goal) return false;

        if (!town.Solved)
        {
            Speak("Marcus", Line(goal, 3));
            int first = _world.Mission.PuzzleChain.FirstOrDefault()?.RewardItemId ?? 0;
            if (first > 0) _state.AddItem(first);
            SetSolved(town);
        }
        else
        {
            Speak("Marcus", Line(goal, 2));
        }
        return true;
    }

    /// <summary>Using <paramref name="item"/> on (x, y). Returns true if a sector puzzle took it.</summary>
    public bool PlaceItem(int item, int x, int y)
    {
        if (Sector is not { } sector || _state.CurrentZone is not { } zone) return false;
        // Reward markers frequently share a cell with the actual lock or NPC.
        // Select by interaction role, not whichever hotspot was stored first.
        var hotspot = zone.Objects.FirstOrDefault(h => h.X == x && h.Y == y && h.Enabled && h.Type == Lock);

        if (sector.PuzzleIndex < 0 || sector.Solved)
        {
            // Blockades and solved sectors: the required item opens a Lock.
            if (hotspot?.Type != Lock || sector.RequiredItem != item) return false;
            SetSolved(sector);
            _state.RemoveItem(item);
            return true;
        }

        var (puzzle, _) = PuzzlesFor(sector);
        if (puzzle == null) return false;

        if (puzzle.Type == PuzzleType.Transaction)
        {
            hotspot = zone.Objects.FirstOrDefault(h => h.X == x && h.Y == y && h.Enabled && h.Type == SpawnLocation);
            if (sector.RequiredItem != item) return NoGo();
            if (zone.GetTile(x, y, 1) != sector.Npc || hotspot == null) return NoGo();

            _state.RemoveItem(item);
            Speak(NameOf(sector.Npc), Line(puzzle, 1));
            // Handed straight over - WebFun's "pick up items automatically" behaviour.
            if (sector.FindItem >= 0) _state.AddItem(sector.FindItem);
            SetSolved(sector);
            return true;
        }

        if (puzzle.Type == PuzzleType.Offer)
        {
            if (hotspot?.Type != Lock || hotspot.Argument != item) return NoGo();
            SetSolved(sector);
            _state.RemoveItem(item);
            // The find item's drop spot opens; show it now rather than on the next visit.
            foreach (var drop in zone.Objects.Where(h => h.Type == DropQuestItem && h.Argument == sector.FindItem))
            {
                drop.Enabled = true;
                zone.SetTile(drop.X, drop.Y, 1, (ushort)sector.FindItem);
                _state.CollectedObjects.Remove($"{zone.Id}_{drop.X}_{drop.Y}_tile");
            }
            return true;
        }

        return false;
    }

    private bool NoGo()
    {
        OnSound?.Invoke(Audio.SoundManager.NoGo);
        return true;
    }

    /// <summary>Picking up a sector's find item solves it (WebFun engine.dropItem).</summary>
    public void OnPickedUp(int item)
    {
        if (Sector is { } sector && sector.FindItem == item) SetSolved(sector);
    }

    private void SetSolved(Sector sector)
    {
        sector.Solved = true;
        foreach (var pair in _world.Sectors.Where(p => ReferenceEquals(p.Value, sector)))
            _state.MarkZoneSolved(pair.Key);
    }

    private static string Line(Puzzle p, int i) => i < p.Strings.Count ? p.Strings[i].Trim() : "";
    private void Speak(string speaker, string text)
    {
        int provided = Sector?.FindItem ?? -1, required = Sector?.RequiredItem ?? -1;
        if (required < 0) required = provided;
        if (provided < 0) provided = required;
        OnSpeak?.Invoke(speaker, DialogueText.Resolve(_data, text, provided, required));
    }
    private string NameOf(int tile) => _data.TileNames.GetValueOrDefault(tile) ?? "";
}
