using YodaStoriesNG.Engine.Data;

namespace YodaStoriesNG.Engine.Game;

/// <summary>
/// One sector of a generated world: the zone placed there and what its puzzle needs and
/// gives. Items are tile IDs, -1 for none. (WebFun: engine/sector.ts)
/// </summary>
public class Sector
{
    public int Zone = -1;
    public ZoneType Type = ZoneType.None;
    public int PuzzleIndex = -1;
    public int RequiredItem = -1;
    public int FindItem = -1;
    public int Npc = -1;
    public bool Solved;
    public int AdditionalRequiredItem = -1;
    public int AdditionalGainItem = -1;
    public bool UsedAlternateStrain;
}

public class WorldGenerationException : Exception
{
    public WorldGenerationException(string message) : base(message) { }
}

/// <summary>
/// The original game's world generator: a port of WebFun's
/// engine/generation/world-generator.ts, which is itself a reconstruction of the
/// original's algorithm. <see cref="MapGenerator"/> lays out which sectors hold puzzles,
/// blockades and travel; this fills them. It picks the end puzzle, walks the puzzle chain
/// backwards from it choosing a zone whose item lists can host each step, then places
/// travel, blockade, town, find and filler zones, writing items and NPCs into zone
/// hotspots as it goes.
///
/// Yoda Stories builds two strains of puzzles that converge on a Mission needing two
/// items. Indiana Jones' data has one: its Mission puzzles carry one item, its zones have
/// no goal-item list, and each of its 15 Goal zones provides exactly one Mission item
/// while requiring the item of a Goal-type puzzle. So <see cref="_singleStrain"/> runs
/// the same algorithm with the second strain switched off - which is the whole of the
/// Indy-specific logic, decoded from the data rather than taken from WebFun, which gives
/// Indy puzzles no type and so cannot generate an Indy world at all.
///
/// The search order and its side effects are kept as the original has them: candidate
/// zones are tried in shuffled order and a rejected candidate can still leave quests
/// behind. Tidying that would produce different worlds.
/// </summary>
public class OriginalWorldGenerator
{
    // Hotspot types, by WebFun's names (engine/objects/hotspot-type.ts). ZoneObjectType has
    // the same values under older names.
    private const ZoneObjectType DropQuestItem = (ZoneObjectType)0;
    private const ZoneObjectType SpawnLocation = (ZoneObjectType)1;
    private const ZoneObjectType DropUniqueWeapon = (ZoneObjectType)2;
    private const ZoneObjectType VehicleTo = (ZoneObjectType)3;
    private const ZoneObjectType DropMap = (ZoneObjectType)5;
    private const ZoneObjectType DropItem = (ZoneObjectType)6;
    private const ZoneObjectType Npc = (ZoneObjectType)7;
    private const ZoneObjectType DropWeapon = (ZoneObjectType)8;
    private const ZoneObjectType DoorIn = (ZoneObjectType)9;
    private const ZoneObjectType Lock = (ZoneObjectType)12;
    private const ZoneObjectType Teleporter = (ZoneObjectType)13;

    /// <summary>Stands in for WebFun's ZoneType.PlaceholderForEndPuzzle when choosing the goal puzzle.</summary>
    private const ZoneType EndPuzzle = (ZoneType)9999;

    private record Quest(int Item, int Distance);

    private readonly GameData _data;
    private readonly bool _singleStrain;
    private readonly int _weaponTile;
    private readonly int _mapTile;
    private readonly Rng _rng;

    private MapGenerator _map = null!;
    private Sector[] _sectors = null!;
    private List<Zone> _zones = null!;
    private readonly Dictionary<ZoneType, List<Zone>> _zonesByType = new();
    private readonly List<Zone> _usedZones = new();
    private readonly List<Puzzle> _usedPuzzles = new();
    private readonly List<Quest> _providedItemQuests = new();
    private readonly List<Quest> _requiredItemQuests = new();
    private Puzzle?[] _strain1 = null!;
    private Puzzle?[] _strain2 = null!;
    private Planet _planet;
    private WorldSize _size;

    private int _requiredItem = -1, _findItem = -1, _npc = -1;
    private int _additionalRequiredItem = -1, _additionalFindItem = -1;
    private int _currentPuzzleIndex = -1;
    private int _somethingWithTeleporters = -1;
    private bool _usedAlternateStrain;

    /// <summary>Hotspot arguments for this world, per zone id, as the generator rewrote them.</summary>
    public Dictionary<int, int[]> HotspotArguments { get; } = new();

    public Sector[] Sectors => _sectors;
    public MapGenerator Map => _map;
    public Puzzle GoalPuzzle { get; private set; } = null!;
    public IReadOnlyList<Puzzle> Strain1 => _strain1.Where(p => p != null).Select(p => p!).ToList();

    /// <summary>The strains by sector puzzle index, gaps included - what a sector's PuzzleIndex points into.</summary>
    public IReadOnlyList<Puzzle?> Strain1ByIndex => _strain1;
    public IReadOnlyList<Puzzle?> Strain2ByIndex => _strain2;

    public OriginalWorldGenerator(GameData data, int seed)
    {
        _data = data;
        _singleStrain = data.GameType == GameType.IndianaJones;
        // WebFun variant weaponTileId / mapTileId
        (_weaponTile, _mapTile) = _singleStrain ? (450, 443) : (511, 421);
        _rng = new Rng(seed);
    }

    public void Generate(Planet planet, WorldSize size)
    {
        _planet = planet;
        _size = size == WorldSize.XtraLarge ? WorldSize.Large : size;
        _zones = _data.Zones.Where(z => z.Planet == planet && z.Width > 0).ToList();

        _map = new MapGenerator();
        _map.Generate(_rng.Next(), _size);

        _sectors = Enumerable.Range(0, 100).Select(i => new Sector { PuzzleIndex = _map.OrderMap[i] }).ToArray();

        int puzzleCount = _map.PuzzleCount;
        int puzzles1Count, puzzles2Count;
        if (_singleStrain)
        {
            // Every puzzle sector belongs to the one strain; strain 2 is just the goal.
            puzzles1Count = puzzleCount;
            puzzles2Count = 1;
        }
        else
        {
            puzzles1Count = puzzleCount % 2 == 1 ? (puzzleCount + 1) / 2 : puzzleCount / 2 + 1;
            puzzles2Count = puzzleCount % 2 == 1 ? (puzzleCount + 1) / 2 : puzzleCount / 2;
        }

        _strain1 = new Puzzle?[puzzles1Count + 1];
        _strain2 = new Puzzle?[puzzles2Count + 1];

        var goal = GetUnusedPuzzleRandomly(-1, EndPuzzle);
        ErrorWhen(goal == null, "Could not find a suitable goal puzzle");
        GoalPuzzle = goal!;
        _usedPuzzles.Add(goal!);
        _strain1[puzzles1Count] = goal;
        _strain2[puzzles2Count] = goal;

        DetermineTransportZones();
        DetermineGoalZone(puzzleCount, puzzles2Count);
        DetermineQuestZones(puzzleCount);
        PlacePuzzlesZones(puzzles2Count - 1);
        DetermineBlockadeAndTownZones();

        AddProvidedItemQuest(LookupTile(_weaponTile), 2);
        AddProvidedItemQuest(LookupTile(_mapTile), 1);
        DetermineFindZones();
        DetermineTeleporters();

        _rng.Next(); // the original wastes one here
    }

    // ---- placement phases ------------------------------------------------------------

    private void DetermineTransportZones()
    {
        var typeMap = _map.TypeMap;
        bool IsTravelTarget(int x, int y) =>
            typeMap[x + y * 10] == SectorType.TravelEnd && _sectors[x + y * 10].Zone == -1;

        for (int y = 0; y < 10; y++)
        for (int x = 0; x < 10; x++)
        {
            if (typeMap[x + y * 10] != SectorType.TravelStart) continue;

            ResetState();
            var zone = GetUnusedZoneRandomly(ZoneType.TravelStart, -1, -1, -1, -1, Distance(x, y), false);
            ErrorWhen(zone == null, "Could not determine zone for travel start");
            PlaceZone(x, y, zone!, requiredItem: _requiredItem);

            var vehicle = zone!.Objects.FirstOrDefault(o => o.Type == VehicleTo);
            var connected = LookupZone(vehicle != null ? Arg(vehicle) : -1);

            // Islands on the left, top, right, bottom - first free travel end wins.
            (int x, int y)? target = null;
            foreach (var (fx, fy, vertical) in new[] { (0, -1, true), (-1, 0, false), (9, -1, true), (-1, 9, false) })
            {
                for (int i = 0; i <= 9 && target == null; i++)
                {
                    var (tx, ty) = vertical ? (fx, i) : (i, fy);
                    if (IsTravelTarget(tx, ty)) target = (tx, ty);
                }
                if (target != null) break;
            }

            ErrorWhen(target == null, "Could not determine location for travel target");
            ErrorWhen(connected == null || _usedZones.Contains(connected), "Zone is already in use for a different travel sector");
            PlaceZone(target!.Value.x, target.Value.y, connected!, requiredItem: _requiredItem);
        }
    }

    private void DetermineGoalZone(int puzzleCount, int puzzles2Count)
    {
        ResetState();

        var puzzle = _strain1[^1]!;
        var pos = FindPositionOfPuzzle(puzzleCount - 1);
        ErrorWhen(pos == null, "Could not find the goal location");
        var zone = GetUnusedZoneRandomly(ZoneType.Goal, _strain1.Length - 2, puzzles2Count - 1,
            puzzle.Item1, puzzle.Item2 ?? -1, Distance(pos!.Value.x, pos.Value.y), true);
        ErrorWhen(zone == null, "Unable to find suitable goal zone.");

        PlaceZone(pos.Value.x, pos.Value.y, zone!, findItem: _findItem, puzzleIndex: _currentPuzzleIndex,
            requiredItem: _requiredItem,
            additionalRequiredItem: _usedPuzzles.Count > 2 ? _usedPuzzles[2].Item1 : -1,
            additionalGainItem: _additionalFindItem);
    }

    private void DetermineQuestZones(int puzzleCount)
    {
        int previousPuzzleIndex = puzzleCount - 1;
        for (int puzzleIdIndex = _strain1.Length - 2; puzzleIdIndex > 0; puzzleIdIndex--)
        {
            ResetState();

            var puzzle = _strain1[puzzleIdIndex];
            ErrorWhen(puzzle == null, $"Strain 1 has no puzzle at {puzzleIdIndex}");
            var pos = FindPositionOfPuzzle(previousPuzzleIndex - 1);
            ErrorWhen(pos == null, "Could not find previous puzzle location!");
            int distance = Distance(pos!.Value.x, pos.Value.y);

            var type = _rng.Next() % 2 != 0 ? ZoneType.Use : ZoneType.Trade;
            var zone = GetUnusedZoneRandomly(type, puzzleIdIndex - 1, -1, puzzle!.Item1, -1, distance, true);
            if (zone == null)
            {
                type = type == ZoneType.Trade ? ZoneType.Use : ZoneType.Trade;
                zone = GetUnusedZoneRandomly(type, puzzleIdIndex - 1, -1, puzzle.Item1, -1, distance, true);
            }
            ErrorWhen(zone == null, $"Unable to find suitable zone for puzzle at {pos.Value.x}x{pos.Value.y}");

            PlaceZone(pos.Value.x, pos.Value.y, zone!, findItem: _findItem, puzzleIndex: _currentPuzzleIndex,
                requiredItem: _requiredItem, npc: _npc, additionalRequiredItem: _additionalRequiredItem);

            // Yoda hands over strain 1's initial item on Dagobah. Only strain 2
            // needs a generated Find source; adding both duplicates Yoda's gift.

            previousPuzzleIndex--;
        }
    }

    private void PlacePuzzlesZones(int puzzleMapIdx)
    {
        for (int puzzleIndex = puzzleMapIdx; puzzleIndex > 0; puzzleIndex--)
        {
            ResetState();

            var pos = FindPositionOfPuzzle(puzzleIndex - 1);
            ErrorWhen(pos == null, "Could not find puzzle location");
            int distance = Distance(pos!.Value.x, pos.Value.y);
            var item1 = _strain2[puzzleIndex]!.Item1;

            var type = _rng.Next() % 2 != 0 ? ZoneType.Trade : ZoneType.Use;
            var zone = GetUnusedZoneRandomly(type, puzzleIndex - 1, -1, item1, -1, distance, false);
            if (zone == null)
            {
                type = type == ZoneType.Trade ? ZoneType.Use : ZoneType.Trade;
                zone = GetUnusedZoneRandomly(type, puzzleIndex - 1, -1, item1, -1, distance, false);
            }
            ErrorWhen(zone == null, "Unable to find suitable puzzle zone");

            PlaceZone(pos.Value.x, pos.Value.y, zone!, findItem: _findItem, puzzleIndex: _currentPuzzleIndex,
                requiredItem: _requiredItem, npc: _npc);
            _currentPuzzleIndex = -1;
        }

        // Yoda Stories has two independent item strains that meet at the final Mission
        // puzzle. Seed the second strain too; without this Find quest, its first puzzle
        // can require an item that no earlier puzzle or zone ever provides.
        if (!_singleStrain)
        {
            ErrorWhen(_strain2[0] == null, "Strain 2 has no first puzzle");
            var firstPosition = FindPositionOfPuzzle(0);
            ErrorWhen(firstPosition == null, "Could not locate the first puzzle in strain 2");
            AddProvidedItemQuest(_strain2[0]!.Item1, Distance(firstPosition!.Value.x, firstPosition.Value.y));
        }
    }

    private void DetermineBlockadeAndTownZones()
    {
        for (int y = 0; y < 10; y++)
        for (int x = 0; x < 10; x++)
        {
            var type = _map.TypeMap[x + 10 * y] switch
            {
                SectorType.Spaceport => ZoneType.Town,
                SectorType.BlockEast => ZoneType.BlockadeEast,
                SectorType.BlockWest => ZoneType.BlockadeWest,
                SectorType.BlockNorth => ZoneType.BlockadeNorth,
                SectorType.BlockSouth => ZoneType.BlockadeSouth,
                _ => ZoneType.Empty,
            };
            if (type == ZoneType.Empty) continue;

            ResetState();
            int distance = Distance(x, y);
            var zone = GetUnusedZoneRandomly(type, -1, -1, -1, -1, distance, false)
                       ?? GetUnusedZoneRandomly(ZoneType.Empty, -1, -1, -1, -1, distance, false);
            ErrorWhen(zone == null, "Could not find an unused empty zone");

            PlaceZone(x, y, zone!, requiredItem: type != ZoneType.Town ? _requiredItem : -1);
        }
    }

    private void DetermineFindZones()
    {
        foreach (var quest in _providedItemQuests.ToList())
        {
            var candidates = DeterminePuzzleLocationChoices(quest.Distance);
            ErrorWhen(candidates == null, $"No place to find item {quest.Item} found!");
            var (x, y) = candidates![_rng.Next() % candidates.Count];

            var zone = GetUnusedZoneRandomly(ZoneType.Find, -1, -1, quest.Item, -1, quest.Distance, false);
            ErrorWhen(zone == null, $"No zone to find item {quest.Item} in");
            PlaceZone(x, y, zone!, findItem: _findItem);
            _map.TypeMap[x + 10 * y] = SectorType.Puzzle;
        }
    }

    private void DetermineTeleporters()
    {
        (int x, int y) teleporterSource = (0, 0);
        Zone? lastZone = null;
        bool foundTeleporterTarget = false;
        int teleportersFound = 0;

        for (int y = 0; y < 10; y++)
        for (int x = 0; x < 10; x++)
        {
            var sectorType = _map.TypeMap[x + 10 * y];
            if (sectorType != SectorType.Empty && sectorType != SectorType.Candidate && sectorType != SectorType.Island)
                continue;

            int distance = Distance(x, y);
            _somethingWithTeleporters = sectorType == SectorType.Island || distance < 2 ? 1 : 0;

            var zone = _somethingWithTeleporters != 0 || !foundTeleporterTarget
                ? GetUnusedZoneRandomly(ZoneType.Empty, -1, -1, -1, -1, distance, false)
                : lastZone;
            ErrorWhen(zone == null, "No zone found");

            for (int limit = 5000; ; limit--)
            {
                ErrorWhen(limit == 0, "Maximum number of iterations exceeded");
                if (_somethingWithTeleporters != 0) break;
                if (!zone!.Objects.Any(o => o.Type == Teleporter)) break;

                if (teleportersFound == 0)
                {
                    teleportersFound++;
                    teleporterSource = (x, y);
                    break;
                }

                int reach = _size == WorldSize.Large ? 2 : 1;
                if (Math.Abs(teleporterSource.x - x) > reach || Math.Abs(teleporterSource.y - y) > reach)
                {
                    teleportersFound++;
                    teleporterSource = (x, y);
                    if (!foundTeleporterTarget) break;
                    foundTeleporterTarget = false;
                    lastZone = null;
                    break;
                }

                lastZone = zone;
                foundTeleporterTarget = true;
                zone = GetUnusedZoneRandomly(ZoneType.Empty, -1, -1, -1, -1, distance, false);
                ErrorWhen(zone == null, "No zone found");
            }

            PlaceZone(x, y, zone!);
        }

        if (teleportersFound == 1)
        {
            _somethingWithTeleporters = 1;
            var zone = GetUnusedZoneRandomly(ZoneType.Empty, -1, -1, -1, -1,
                Distance(teleporterSource.x, teleporterSource.y), false);
            if (zone != null)
                PlaceZone(teleporterSource.x, teleporterSource.y, zone);
        }
    }

    // ---- choosing zones and puzzles ----------------------------------------------------

    private Zone? GetUnusedZoneRandomly(ZoneType zoneType, int puzzleIndex, int puzzleIndex2,
        int providedItem, int providedItem2, int distance, bool useAlternateStrain)
    {
        _usedAlternateStrain = useAlternateStrain;
        var candidates = Shuffle(GetZonesForType(zoneType).ToList());
        // Evaluated lazily in order, like the original's find(): each rejected candidate's
        // side effects stay.
        foreach (var zone in candidates)
        {
            if (_usedZones.Contains(zone)) continue;
            if (TryZone(zone, puzzleIndex, puzzleIndex2, providedItem, providedItem2, distance, useAlternateStrain))
                return zone;
        }
        return null;
    }

    private List<Zone> GetZonesForType(ZoneType type)
    {
        if (_zonesByType.TryGetValue(type, out var cached)) return cached;

        bool isFind = type is ZoneType.Find or ZoneType.FindUniqueWeapon;
        var result = _zones.Where(z => isFind ? z.Type is ZoneType.Find or ZoneType.FindUniqueWeapon : z.Type == type).ToList();
        _zonesByType[type] = result;
        if (isFind)
        {
            _zonesByType[ZoneType.Find] = result;
            _zonesByType[ZoneType.FindUniqueWeapon] = result;
        }
        return result;
    }

    /// <summary>WebFun getUnusedZone: can this zone host this step? May set chain state as it goes.</summary>
    private bool TryZone(Zone zone, int puzzleIndex, int puzzleIndex2, int providedItem, int providedItem2,
        int distance, bool useAlternateStrain)
    {
        switch (zone.Type)
        {
            case ZoneType.Town:
                return true;

            case ZoneType.BlockadeNorth or ZoneType.BlockadeSouth or ZoneType.BlockadeEast or ZoneType.BlockadeWest
                or ZoneType.TravelStart or ZoneType.TravelEnd:
            {
                if (!RequiredItemForZoneWasNotPlaced(zone)) return false;
                var candidates = zone.RequiredItems.Select(i => (int)i).Where(i => !HasQuestRequiringItem(i)).ToList();
                if (candidates.Count == 0) return false;
                int item = candidates[_rng.Next() % candidates.Count];
                AddProvidedItemQuest(item, zone.Type == ZoneType.TravelStart ? 5 : distance);
                AddRequiredItemQuest(item, distance);
                _requiredItem = item;
                AddRequiredItemQuestsFromHotspots(zone);
                return true;
            }

            case ZoneType.Empty:
                if (_somethingWithTeleporters != 0)
                    return zone.Objects.Count == 0;
                return true;

            case ZoneType.Goal:
                return TryGoalZone(zone, puzzleIndex, puzzleIndex2, providedItem, providedItem2, distance);

            case ZoneType.Use:
            {
                if (!ZoneLeadsToProvidedItem(zone, providedItem)) return false;
                int requiredItem = GetUnusedRequiredItemForZoneRandomly(zone, false);
                if (requiredItem == -1) return false;
                var puzzle = GetUnusedPuzzleRandomly(requiredItem, ZoneType.Use);
                if (puzzle == null) return false;

                _usedAlternateStrain = useAlternateStrain;
                var strain = useAlternateStrain ? _strain1 : _strain2;
                strain[puzzleIndex] = puzzle;
                var p1 = strain[puzzleIndex]!;
                var p2 = strain[puzzleIndex + 1]!;
                _currentPuzzleIndex = puzzleIndex;

                AddRequiredItemQuest(p1.Item1, distance);
                AddRequiredItemQuest(p2.Item1, distance);

                if (!RequiredItemForZoneWasNotPlaced(zone))
                {
                    RemoveQuestRequiringItem(p1.Item1);
                    return false;
                }

                if (DropItemAtLockHotspot(zone, p1.Item1))
                {
                    AddRequiredItemQuest(p1.Item1, distance);
                    _requiredItem = p1.Item1;
                    if (PlaceQuestItem(zone, p2.Item1))
                    {
                        AddRequiredItemQuest(p2.Item1, distance);
                        _findItem = p2.Item1;
                    }
                }

                AddRequiredItemQuestsFromHotspots(zone);
                _usedPuzzles.Add(puzzle);
                AddRequiredItemQuest(requiredItem, distance);
                return true;
            }

            case ZoneType.Trade:
            {
                if (!ZoneLeadsToProvidedItem(zone, providedItem)) return false;
                int puzzleItem = GetUnusedRequiredItemForZoneRandomly(zone, false);
                if (puzzleItem == -1) return false;
                var puzzle = GetUnusedPuzzleRandomly(puzzleItem, ZoneType.Trade);
                if (puzzle == null) return false;

                _usedAlternateStrain = useAlternateStrain;
                var strain = useAlternateStrain ? _strain1 : _strain2;
                strain[puzzleIndex] = puzzle;

                if (!RequiredItemForZoneWasNotPlaced(zone)) return false;
                int npc = FindUnusedNpcForZoneRandomly(zone);
                if (npc == -1) return false;

                int requiredItem = strain[puzzleIndex]!.Item1;
                if (!ZoneLeadsToRequiredItem(zone, requiredItem)) return false;
                int findItem = strain[puzzleIndex + 1]!.Item1;
                if (!ZoneLeadsToProvidedItem(zone, findItem)) return false;
                if (!DropNpcAtHotspotRandomly(zone, npc)) return false;

                _npc = npc;
                _findItem = findItem;
                _requiredItem = requiredItem;
                _currentPuzzleIndex = puzzleIndex;

                AddRequiredItemQuest(npc, distance);
                AddRequiredItemQuestsFromHotspots(zone);
                _usedPuzzles.Add(puzzle);
                AddRequiredItemQuest(puzzleItem, distance);
                return true;
            }

            case ZoneType.Find or ZoneType.FindUniqueWeapon:
                if (!DropItemAtHotspotRandomly(zone, providedItem)) return false;
                AddRequiredItemQuest(providedItem, distance);
                _findItem = providedItem;
                AddRequiredItemQuestsFromHotspots(zone);
                return true;

            default:
                return false;
        }
    }

    private bool TryGoalZone(Zone zone, int puzzleIndex, int puzzleIndex2, int providedItem, int providedItem2, int distance)
    {
        if (!ZoneLeadsToProvidedItem(zone, providedItem)) return false;
        if (!_singleStrain && !ZoneLeadsToProvidedItem(zone, providedItem2)) return false;

        int newItem1 = GetUnusedRequiredItemForZoneRandomly(zone, false);
        // Both branches are chosen before either is committed to the quest list.
        // Exclude the first choice explicitly so independent branches need distinct items.
        int newItem2 = _singleStrain ? -1 : GetUnusedRequiredItemForZoneRandomly(zone, true, newItem1);
        if (newItem1 == -1 || !_singleStrain && newItem2 == -1) return false;

        var newPuzzle = GetUnusedPuzzleRandomly(newItem1, ZoneType.Goal);
        if (newPuzzle != null) _usedPuzzles.Add(newPuzzle);
        Puzzle? altPuzzle = null;
        if (!_singleStrain)
        {
            altPuzzle = GetUnusedPuzzleRandomly(newItem2, ZoneType.Goal);
            if (altPuzzle != null) _usedPuzzles.Add(altPuzzle);
            if (altPuzzle == null) return false;
        }
        if (newPuzzle == null) return false;

        _strain1[puzzleIndex] = newPuzzle;
        if (!_singleStrain) _strain2[puzzleIndex2] = altPuzzle;
        if (!RequiredItemForZoneWasNotPlaced(zone)) return false;

        var puzzle1 = _strain1[puzzleIndex]!;
        var puzzle2 = _singleStrain ? null : _strain2[puzzleIndex2];
        var puzzle3 = _strain1[puzzleIndex + 1]!;

        int npc = FindUnusedNpcForZoneRandomly(zone);
        bool hasNpc = npc != -1 && ZoneLeadsToNpc(zone, npc);

        bool hasItem = ZoneLeadsToRequiredItem(zone, puzzle1.Item1);
        hasItem &= ZoneLeadsToProvidedItem(zone, puzzle3.Item1);
        if (!_singleStrain) ZoneLeadsToProvidedItem(zone, puzzle3.Item2 ?? -1);
        if (!hasItem) return false;

        if (hasNpc)
        {
            AddRequiredItemQuestsFromHotspots(zone);
            _npc = npc;
            _findItem = -1;
            _requiredItem = puzzle1.Item1;
            _currentPuzzleIndex = puzzleIndex;
            _additionalRequiredItem = -1;
            AddRequiredItemQuestsFromHotspots(zone);
        }
        else
        {
            bool added = ChooseItemFromZone(zone, puzzle1.Item1, false);
            added &= PlaceQuestItem(zone, puzzle3.Item1);
            if (!_singleStrain)
            {
                added &= ChooseItemFromZone(zone, puzzle2!.Item1, true);
                added &= PlaceQuestItem(zone, puzzle3.Item2 ?? -1);
            }
            if (!added) return false;

            AddRequiredItemQuest(puzzle1.Item1, distance);
            if (!_singleStrain) AddRequiredItemQuest(puzzle2!.Item1, distance);
            AddRequiredItemQuest(puzzle3.Item1, distance);
            if (!_singleStrain) AddRequiredItemQuest(puzzle3.Item2 ?? -1, distance);

            _npc = -1;
            _findItem = puzzle3.Item1;
            _requiredItem = puzzle1.Item1;
            _currentPuzzleIndex = puzzleIndex;
            _additionalRequiredItem = -1;
            _additionalFindItem = puzzle3.Item2 ?? -1;
            AddRequiredItemQuestsFromHotspots(zone);
        }

        AddRequiredItemQuest(newItem1, distance);
        if (!_singleStrain) AddRequiredItemQuest(newItem2, distance);
        return true;
    }

    private Puzzle? GetUnusedPuzzleRandomly(int item, ZoneType zoneType)
    {
        var wanted = zoneType switch
        {
            ZoneType.Trade => PuzzleType.Transaction,
            ZoneType.Use => PuzzleType.Offer,
            ZoneType.Goal => PuzzleType.Goal,
            EndPuzzle => PuzzleType.Mission,
            _ => throw new ArgumentException($"Zone type {zoneType} has no puzzle type"),
        };

        var candidates = Shuffle(_data.Puzzles.Where(p => zoneType == EndPuzzle
            ? p.Type == PuzzleType.Mission && IsGoalOnPlanet(p)
            : !_usedPuzzles.Contains(p) && p.Type == wanted).ToList());

        return candidates.FirstOrDefault(p =>
            p.Type == wanted && !_usedPuzzles.Contains(p) && (zoneType == EndPuzzle || p.Item1 == item));
    }

    private bool IsGoalOnPlanet(Puzzle puzzle) =>
        _data.Zones.Any(z => z.Type == ZoneType.Goal && z.Planet == _planet &&
                             ZoneLeadsToProvidedItem(z, puzzle.Item1) &&
                             (_singleStrain || ZoneLeadsToProvidedItem(z, puzzle.Item2 ?? -1)));

    // ---- zone and room queries -----------------------------------------------------------

    /// <summary>A zone and every room reachable through its doors, each once (WebFun room-iterator.ts).</summary>
    private IEnumerable<Zone> Rooms(Zone zone)
    {
        var visited = new HashSet<Zone>();
        // Depth-first, zone before its rooms, rooms in hotspot order - as the original iterates.
        var order = new List<Zone>();
        void Visit(Zone z)
        {
            if (!visited.Add(z)) return;
            order.Add(z);
            foreach (var door in z.Objects.Where(o => o.Type == DoorIn && Arg(o) >= 0))
                if (LookupZone(Arg(door)) is { } room) Visit(room);
        }
        Visit(zone);
        return order;
    }

    private bool ZoneLeadsToRequiredItem(Zone zone, int item) => Rooms(zone).Any(r => r.RequiredItems.Contains((ushort)item) && item >= 0);
    private bool ZoneLeadsToProvidedItem(Zone zone, int item) => Rooms(zone).Any(r => r.ProvidedItems.Contains((ushort)item) && item >= 0);
    private bool ZoneLeadsToNpc(Zone zone, int npc) => Rooms(zone).Any(r => r.Npcs.Contains((ushort)npc) && npc >= 0);

    private bool ChooseItemFromZone(Zone zone, int item, bool fromGoalItems) =>
        item >= 0 && Rooms(zone).Any(r => (fromGoalItems ? r.GoalItems : r.RequiredItems).Contains((ushort)item));

    private int FindUnusedNpcForZoneRandomly(Zone zone)
    {
        foreach (var room in Rooms(zone))
        {
            var candidates = room.Npcs.Select(n => (int)n).Where(n => !HasQuestRequiringItem(n)).ToList();
            if (candidates.Count > 0) return candidates[_rng.Next() % candidates.Count];
        }
        return -1;
    }

    private int GetUnusedRequiredItemForZoneRandomly(Zone zone, bool isGoal, int excludedItem = -1)
    {
        foreach (var room in Rooms(zone))
        {
            var items = (isGoal ? room.GoalItems : room.RequiredItems).Select(i => (int)i)
                .Where(i => i != excludedItem && !HasQuestRequiringItem(i)).ToList();
            if (items.Count > 0) return items[_rng.Next() % items.Count];
        }
        return -1;
    }

    private bool RequiredItemForZoneWasNotPlaced(Zone zone) => RequiredItemForZoneWasNotPlaced(zone, new HashSet<Zone>());

    private bool RequiredItemForZoneWasNotPlaced(Zone zone, HashSet<Zone> seen)
    {
        if (!seen.Add(zone)) return true; // ponytail: cycle guard the original does not need; its doors never loop
        foreach (var hotspot in zone.Objects)
        {
            int arg = Arg(hotspot);
            if (arg == -1) continue;
            if (CanHoldItem(hotspot.Type) && HasQuestRequiringItem(LookupTile(arg))) return false;
            if (hotspot.Type == DoorIn && LookupZone(arg) is { } room && !RequiredItemForZoneWasNotPlaced(room, seen))
                return false;
        }
        return true;
    }

    private void AddRequiredItemQuestsFromHotspots(Zone zone) => AddRequiredItemQuestsFromHotspots(zone, new HashSet<Zone>());

    private void AddRequiredItemQuestsFromHotspots(Zone zone, HashSet<Zone> seen)
    {
        if (!seen.Add(zone)) return;
        foreach (var hotspot in zone.Objects)
        {
            int arg = Arg(hotspot);
            if (arg == -1) continue;
            if (CanHoldItem(hotspot.Type)) AddRequiredItemQuest(LookupTile(arg), -1);
            if (hotspot.Type == DoorIn && LookupZone(arg) is { } room) AddRequiredItemQuestsFromHotspots(room, seen);
        }
    }

    private static bool CanHoldItem(ZoneObjectType type) => type == DropItem || type == Npc || type == DropWeapon;

    // ---- writing items and NPCs into hotspots --------------------------------------------

    private bool PlaceQuestItem(Zone zone, int item) =>
        Rooms(zone).Any(room => room.ProvidedItems.Contains((ushort)item) && item >= 0 &&
                                PlaceItemAtHotspotRandomly(room, room.Objects.Where(o => o.Type == DropQuestItem).ToList(), item));

    private bool PlaceItemAtHotspotRandomly(Zone zone, List<ZoneObject> candidates, int item)
    {
        if (candidates.Count == 0) return false;
        var hotspot = candidates[_rng.Next() % candidates.Count];
        return DropItemAtHotspot(item, hotspot, zone);
    }

    private bool DropItemAtLockHotspot(Zone zone, int item) =>
        Rooms(zone).Any(room => item >= 0 && room.RequiredItems.Contains((ushort)item) &&
                                room.Objects.FirstOrDefault(o => o.Type == Lock) is { } lockHotspot &&
                                DropItemAtHotspot(item, lockHotspot, room));

    private bool DropItemAtHotspot(int item, ZoneObject hotspot, Zone zone)
    {
        int index = zone.Objects.IndexOf(hotspot);
        if (index == -1) return false;
        ArgumentsFor(zone)[index] = item;
        return true;
    }

    private bool DropNpcAtHotspotRandomly(Zone zone, int npc) =>
        Rooms(zone).Any(room => npc >= 0 && room.Npcs.Contains((ushort)npc) &&
                                PlaceItemAtHotspotRandomly(room,
                                    room.Objects.Where(o => o.Type == SpawnLocation && Arg(o) == -1).ToList(), npc));

    private bool DropItemAtHotspotRandomly(Zone zone, int item)
    {
        if (!RequiredItemForZoneWasNotPlaced(zone)) return false;
        var type = HotspotTypeForTile(item);
        return Rooms(zone).Any(room => item >= 0 && room.ProvidedItems.Contains((ushort)item) && type != null &&
                                       PlaceItemAtHotspotRandomly(room, room.Objects.Where(o => o.Type == type).ToList(), item));
    }

    private ZoneObjectType? HotspotTypeForTile(int tileId)
    {
        if (tileId < 0 || tileId >= _data.Tiles.Count) return null;
        var flags = _data.Tiles[tileId].Flags;
        if ((flags & TileFlags.Weapon) != 0) return DropUniqueWeapon;
        if ((flags & (TileFlags.Item | TileFlags.ItemLocator)) == (TileFlags.Item | TileFlags.ItemLocator)) return DropMap;
        if ((flags & TileFlags.Item) != 0) return DropQuestItem;
        return null;
    }

    private int[] ArgumentsFor(Zone zone)
    {
        if (!HotspotArguments.TryGetValue(zone.Id, out var args))
            HotspotArguments[zone.Id] = args = zone.Objects.Select(Arg).ToArray();
        return args;
    }

    // ---- quests ----------------------------------------------------------------------------

    private bool HasQuestRequiringItem(int item) => _requiredItemQuests.Any(q => q.Item == item);
    private void AddProvidedItemQuest(int item, int distance) => _providedItemQuests.Insert(0, new Quest(item, distance));
    private void AddRequiredItemQuest(int item, int distance) => _requiredItemQuests.Add(new Quest(item, distance));

    private void RemoveQuestRequiringItem(int item)
    {
        int index = _requiredItemQuests.FindIndex(q => q.Item == item);
        if (index != -1) _requiredItemQuests.RemoveAt(index);
    }

    // ---- sectors and the map ---------------------------------------------------------------

    private void PlaceZone(int x, int y, Zone zone, int findItem = -1, int puzzleIndex = -1, int requiredItem = -1,
        int npc = -1, int additionalRequiredItem = -1, int additionalGainItem = -1)
    {
        _sectors[x + 10 * y] = new Sector
        {
            Zone = zone.Id,
            PuzzleIndex = puzzleIndex,
            RequiredItem = requiredItem,
            Npc = npc,
            FindItem = findItem,
            AdditionalRequiredItem = additionalRequiredItem,
            AdditionalGainItem = additionalGainItem,
            UsedAlternateStrain = _usedAlternateStrain,
            Type = zone.Type == ZoneType.FindUniqueWeapon ? ZoneType.Find : zone.Type,
        };
        _usedAlternateStrain = false;
        _usedZones.Insert(0, zone);
    }

    private (int x, int y)? FindPositionOfPuzzle(int orderIndex)
    {
        for (int y = 0; y < 10; y++)
        for (int x = 0; x < 10; x++)
            if (_map.OrderMap[x + 10 * y] == orderIndex) return (x, y);
        return null;
    }

    private List<(int x, int y)>? DeterminePuzzleLocationChoices(int maxDistance)
    {
        var far = new List<(int, int)>();
        var best = new List<(int, int)>();
        var nearPuzzles = new List<(int, int)>();
        var world = _map.TypeMap;

        for (int y = 0; y < 10; y++)
        for (int x = 0; x < 10; x++)
        {
            int idx = x + 10 * y;
            var item = world[idx];
            if (Distance(x, y) > maxDistance)
            {
                if (item == SectorType.Empty) far.Add((x, y));
            }
            else if (item is SectorType.Empty or SectorType.Candidate)
            {
                bool clear = (x < 1 || world[idx - 1] != SectorType.Puzzle) &&
                             (x > 8 || world[idx + 1] != SectorType.Puzzle) &&
                             (y < 1 || world[idx - 10] != SectorType.Puzzle) &&
                             (y > 8 || world[idx + 10] != SectorType.Puzzle);
                (clear ? best : nearPuzzles).Add((x, y));
            }
        }

        if (best.Count > 0) return best;
        if (nearPuzzles.Count > 0) return nearPuzzles;
        if (far.Count > 0) return far;
        return null;
    }

    // ---- small helpers ---------------------------------------------------------------------

    private void ResetState()
    {
        _requiredItem = _findItem = _npc = -1;
        _currentPuzzleIndex = -1;
        _additionalRequiredItem = _additionalFindItem = -1;
    }

    private int Distance(int x, int y) => _map.GetDistanceToCenter(x, y);
    // The file's value, not the live one: the live one still holds the previous world's placements.
    private static int Arg(ZoneObject o) => o.FileArgument == 0xFFFF ? -1 : o.FileArgument;
    private int LookupTile(int id) => id >= 0 && id < _data.Tiles.Count ? id : -1;
    private Zone? LookupZone(int id) => id >= 0 && id < _data.Zones.Count ? _data.Zones[id] : null;

    private static void ErrorWhen(bool condition, string message)
    {
        if (condition) throw new WorldGenerationException(message);
    }

    /// <summary>WebFun's Array.prototype.shuffle, which is the original's - rand()-driven and not uniform.</summary>
    private List<T> Shuffle<T>(List<T> items) where T : class
    {
        int count = items.Count;
        if (count == 0) return items;
        if (count == 1)
        {
            _rng.Next();
            return items;
        }

        var source = items.Cast<T?>().ToArray();
        var result = new T?[count];
        for (int i = 0; i < count; i++)
        {
            int idx = _rng.Next() % count;
            if (result[idx] == null)
            {
                result[idx] = source[i];
                source[i] = null;
            }
        }

        for (int i = count - 1; i != 0; i--)
        {
            while (result.Any(r => r == null))
            {
                int idx = _rng.Next() % count;
                if (result[idx] == null)
                {
                    result[idx] = source[i];
                    source[i] = null;
                    break;
                }
            }
        }

        return result.Select(r => r!).ToList();
    }

    /// <summary>The MSVC rand() the original links: an LCG returning 15 bits.</summary>
    private sealed class Rng
    {
        private uint _state;
        public Rng(int seed) => _state = (uint)seed;

        public int Next()
        {
            _state = unchecked(_state * 214013 + 2531011);
            return (int)((_state >> 16) & 0x7FFF);
        }
    }
}
