using YodaStoriesNG.Engine.Data;

namespace YodaStoriesNG.Engine.Parsing;

/// <summary>
/// Parser for YODESK.DTA (Yoda Stories) and DESKTOP.DAW (Indiana Jones) game data files.
/// Both games use the same Desktop Adventures engine with minor format differences.
/// </summary>
public class DtaParser
{
    /// <summary>Every IACT condition and instruction carries exactly five argument slots.</summary>
    private const int ActionItemArgumentCount = 5;

    private BinaryReader _reader = null!;
    private GameData _data = null!;

    /// <summary>
    /// Detects the game type from a file path.
    /// </summary>
    public static GameType DetectGameType(string filePath)
    {
        var fileName = Path.GetFileName(filePath).ToUpperInvariant();
        if (fileName.Contains("DESKTOP") || fileName.EndsWith(".DAW"))
            return GameType.IndianaJones;
        return GameType.YodaStories;
    }

    /// <summary>
    /// Parses a DTA/DAW file and returns the game data.
    /// </summary>
    public GameData Parse(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var gameType = DetectGameType(filePath);
        return Parse(stream, gameType);
    }

    /// <summary>
    /// Parses a game data file from a stream with specified game type.
    /// </summary>
    public GameData Parse(Stream stream, GameType gameType)
    {
        _reader = new BinaryReader(stream);
        _data = new GameData { GameType = gameType };

        Console.WriteLine($"Parsing {gameType} data file ({stream.Length} bytes)...");

        while (_reader.BaseStream.Position < _reader.BaseStream.Length)
        {
            if (_reader.BaseStream.Position + 4 > _reader.BaseStream.Length)
                break;

            var tagBytes = _reader.ReadBytes(4);
            var tag = System.Text.Encoding.ASCII.GetString(tagBytes);

            // VERS section has no length field - just 4 bytes of version data
            if (tag == "VERS")
            {
                ParseVersionSection();
                continue;
            }

            // ENDF section has no length or data
            if (tag == "ENDF")
            {
                Console.WriteLine("Reached end of file marker");
                break;
            }

            // Yoda Stories' ZONE has no length prefix; the section reads its own framing.
            if (tag == "ZONE")
            {
                ParseZonesSection();
                continue;
            }

            // All other sections have a 4-byte length prefix
            if (_reader.BaseStream.Position + 4 > _reader.BaseStream.Length)
                break;

            var length = _reader.ReadUInt32();
            ParseSection(tag, length);
        }

        var zones = _data.Zones;
        Console.WriteLine($"Zone contents: {zones.Sum(z => z.Objects.Count)} hotspots, " +
                          $"{zones.Sum(z => z.Monsters.Count)} monsters, {zones.Sum(z => z.Actions.Count)} actions, " +
                          $"{zones.Count(z => z.Name != null)} named; types " +
                          string.Join(" ", zones.GroupBy(z => z.Type).OrderBy(g => g.Key).Select(g => $"{g.Key}={g.Count()}")));

        return _data;
    }

    /// <summary>
    /// Parses a DTA file from a stream with auto-detection (defaults to Yoda Stories).
    /// </summary>
    public GameData Parse(Stream stream) => Parse(stream, GameType.YodaStories);

    private void ParseSection(string tag, uint length)
    {
        var startPos = _reader.BaseStream.Position;

        switch (tag)
        {
            case "VERS":
                // VERS is handled separately in the main loop
                break;
            case "STUP":
                ParseStartupSection(length);
                break;
            case "SNDS":
                ParseSoundsSection(length);
                break;
            case "TILE":
                ParseTilesSection(length);
                break;
            case "ZAUX":
            case "ZAX2":
            case "ZAX3":
            case "ZAX4":
            case "HTSP":
            case "ACTN":
            case "ZNAM":
                ParseIndyZoneSection(tag);
                _reader.BaseStream.Seek(startPos + length, SeekOrigin.Begin);
                break;
            case "PUZ2":
                ParsePuzzlesSection(length);
                break;
            case "CHAR":
                ParseCharactersSection(length);
                break;
            case "CHWP":
                ParseCharacterWeaponsSection(length);
                break;
            case "CAUX":
                ParseCharacterAuxSection(length);
                break;
            case "TNAM":
                ParseTileNamesSection(length);
                break;
            case "ENDF":
                // End of file marker
                break;
            default:
                // Skip unknown sections
                Console.WriteLine($"Unknown section: {tag}, length: {length}");
                _reader.BaseStream.Seek(startPos + length, SeekOrigin.Begin);
                break;
        }
    }

    private void ParseVersionSection()
    {
        // Version is stored as two 16-bit big-endian values
        // BinaryReader reads little-endian, so we need to swap bytes
        var majorBytes = _reader.ReadBytes(2);
        var minorBytes = _reader.ReadBytes(2);
        var major = (majorBytes[0] << 8) | majorBytes[1];
        var minor = (minorBytes[0] << 8) | minorBytes[1];
        _data.Version = new Version(major, minor);

        // The version field does NOT identify the game: retail DESKTOP.DAW also reports
        // 2.0, so trusting it here silently turned every Indiana Jones load into a Yoda
        // Stories one (wrong palette cycles, wrong title-screen vehicle). The file name
        // is authoritative - see DetectGameType.
        Console.WriteLine($"Game version: {major}.{minor} ({_data.GameType})");
    }

    private void ParseStartupSection(uint length)
    {
        _data.StartupScreen = _reader.ReadBytes((int)length);
    }

    private void ParseSoundsSection(uint length)
    {
        var endPos = _reader.BaseStream.Position + length;

        // Skip the first 2 bytes (header marker)
        var header = _reader.ReadInt16();

        int soundId = 0;
        while (_reader.BaseStream.Position < endPos - 1)
        {
            // Read filename length
            var nameLength = _reader.ReadUInt16();

            // 0xFFFF marks end of sounds
            if (nameLength == 0xFFFF || nameLength == 0)
                break;

            // Sanity check
            if (nameLength > 256)
                break;

            // Read filename (null-terminated)
            var nameBytes = _reader.ReadBytes(nameLength);
            var name = System.Text.Encoding.ASCII.GetString(nameBytes).TrimEnd('\0');

            _data.Sounds.Add(new Sound
            {
                Id = soundId++,
                FileName = name
            });
        }

        // Ensure we're at the end of the section
        _reader.BaseStream.Seek(endPos, SeekOrigin.Begin);
        Console.WriteLine($"Loaded {_data.Sounds.Count} sounds");
    }

    private void ParseTilesSection(uint length)
    {
        var endPos = _reader.BaseStream.Position + length;
        int tileId = 0;

        while (_reader.BaseStream.Position + 4 + Tile.PixelCount <= endPos)
        {
            var flags = (TileFlags)_reader.ReadUInt32();
            var pixels = _reader.ReadBytes(Tile.PixelCount);

            _data.Tiles.Add(new Tile
            {
                Id = tileId++,
                Flags = flags,
                PixelData = pixels
            });
        }

        Console.WriteLine($"Loaded {_data.Tiles.Count} tiles");
    }

    /// <summary>
    /// ZONE, per WebFun file-format/categories/zone.ts - verified against every zone of both
    /// retail files. Yoda Stories keeps everything about a zone in one record. Indiana Jones
    /// keeps only the tiles here and puts monsters, items, hotspots and scripts in the
    /// ZAUX, ZAX2-4, HTSP and ACTN sections that follow, one entry per zone in zone order.
    ///
    ///   Yoda:  uint16 count; per zone: uint16 planet, uint32 size, uint16 index, IZON,
    ///          hotspots, IZAX, IZX2, IZX3, IZX4, actions
    ///   Indy:  uint32 size, uint16 count; per zone: IZON
    ///
    /// The section has no length prefix in Yoda Stories, so the main loop hands over
    /// before reading one.
    /// </summary>
    private void ParseZonesSection()
    {
        bool indy = _data.GameType == GameType.IndianaJones;
        if (indy)
            _reader.ReadUInt32();

        int count = _reader.ReadUInt16();
        for (int id = 0; id < count; id++)
        {
            if (indy)
            {
                _data.Zones.Add(ParseIzon(id, Planet.None));
                continue;
            }

            var planet = (Planet)_reader.ReadUInt16();
            var size = _reader.ReadUInt32();
            var end = _reader.BaseStream.Position + size;
            _reader.ReadUInt16(); // zone index, always equal to id

            var zone = ParseIzon(id, planet);
            zone.Objects = ReadHotspots(_reader.ReadUInt16());
            ReadIzax(zone);
            ReadIzx2(zone);
            ReadIzx3(zone);
            ReadIzx4();
            zone.Actions = ReadActions(_reader.ReadUInt16());

            _reader.BaseStream.Seek(end, SeekOrigin.Begin);
            _data.Zones.Add(zone);
        }

        Console.WriteLine($"Loaded {count} zones");
    }

    /// <summary>
    /// IZON: uint32 size, uint16 width, uint16 height, uint32 type, (Yoda: uint16 sector
    /// counter, uint16 planet), then width*height cells of three uint16 tiles each.
    /// </summary>
    private Zone ParseIzon(int id, Planet planet)
    {
        Expect("IZON");
        _reader.ReadUInt32();
        var zone = new Zone
        {
            Id = id,
            Planet = planet,
            Width = _reader.ReadUInt16(),
            Height = _reader.ReadUInt16(),
            Type = (ZoneType)_reader.ReadUInt32(),
        };

        if (_data.GameType != GameType.IndianaJones)
            _reader.ReadBytes(4); // sector counter, planet again

        zone.TileGrid = new ushort[zone.Height, zone.Width, 3];
        for (int y = 0; y < zone.Height; y++)
            for (int x = 0; x < zone.Width; x++)
                for (int layer = 0; layer < 3; layer++)
                    zone.TileGrid[y, x, layer] = _reader.ReadUInt16();

        return zone;
    }

    /// <summary>Hotspot: uint32 type, int16 x, int16 y, uint16 enabled, int16 argument.</summary>
    private List<ZoneObject> ReadHotspots(int count)
    {
        var hotspots = new List<ZoneObject>(count);
        for (int i = 0; i < count; i++)
        {
            var type = (ZoneObjectType)_reader.ReadUInt32();
            var x = _reader.ReadInt16();
            var y = _reader.ReadInt16();
            _reader.ReadUInt16(); // enabled
            var argument = _reader.ReadUInt16();
            hotspots.Add(new ZoneObject
            {
                Type = type, X = x, Y = y, Argument = argument, FileArgument = argument,
                Enabled = ZoneObject.EnabledByDefault(type),
            });
        }
        return hotspots;
    }

    /// <summary>
    /// IZAX: uint32 size, uint16 unknown, monsters, required items, and in Yoda goal items.
    /// A Yoda monster is 44 bytes (character, x, y, loot, drops-loot, eight waypoints); an
    /// Indiana Jones one is just character, x, y.
    /// </summary>
    private void ReadIzax(Zone zone)
    {
        bool yoda = _data.GameType != GameType.IndianaJones;
        Expect("IZAX");
        _reader.ReadUInt32();
        _reader.ReadUInt16();

        int monsters = _reader.ReadUInt16();
        for (int i = 0; i < monsters; i++)
        {
            var monster = new ZoneMonster
            {
                CharacterId = _reader.ReadUInt16(),
                X = _reader.ReadUInt16(),
                Y = _reader.ReadUInt16(),
            };
            if (yoda)
            {
                monster.Loot = _reader.ReadInt16();
                monster.DropsLoot = _reader.ReadUInt32() != 0;
                monster.Waypoints = Enumerable.Range(0, 8).Select(_ => _reader.ReadInt32()).ToArray();
            }
            zone.Monsters.Add(monster);
        }

        zone.RequiredItems = ReadIds();
        if (yoda)
            zone.GoalItems = ReadIds();
    }

    private void ReadIzx2(Zone zone)
    {
        Expect("IZX2");
        _reader.ReadUInt32();
        zone.ProvidedItems = ReadIds();
    }

    private void ReadIzx3(Zone zone)
    {
        Expect("IZX3");
        _reader.ReadUInt32();
        zone.Npcs = ReadIds();
    }

    private void ReadIzx4()
    {
        Expect("IZX4");
        _reader.ReadUInt32();
        _reader.ReadUInt16(); // unknown
    }

    private List<Data.Action> ReadActions(int count)
    {
        var actions = new List<Data.Action>(count);
        for (int i = 0; i < count; i++)
        {
            Expect("IACT");
            actions.Add(ParseAction(_reader));
        }
        return actions;
    }

    /// <summary>A uint16 count followed by that many uint16 tile IDs.</summary>
    private List<ushort> ReadIds()
    {
        int count = _reader.ReadUInt16();
        var ids = new List<ushort>(count);
        for (int i = 0; i < count; i++)
            ids.Add(_reader.ReadUInt16());
        return ids;
    }

    /// <summary>
    /// Indiana Jones' per-zone sections. ZAUX..ZAX4 hold one record per zone in zone order;
    /// HTSP, ACTN and ZNAM are lists keyed by an int16 zone id, ended by -1.
    /// </summary>
    private void ParseIndyZoneSection(string tag)
    {
        switch (tag)
        {
            case "ZAUX": foreach (var zone in _data.Zones) ReadIzax(zone); break;
            case "ZAX2": foreach (var zone in _data.Zones) ReadIzx2(zone); break;
            case "ZAX3": foreach (var zone in _data.Zones) ReadIzx3(zone); break;
            case "ZAX4": foreach (var _ in _data.Zones) ReadIzx4(); break;
            default:
                for (int id = _reader.ReadInt16(); id != -1; id = _reader.ReadInt16())
                {
                    var zone = _data.Zones[id];
                    if (tag == "HTSP")
                        zone.Objects = ReadHotspots(_reader.ReadUInt16());
                    else if (tag == "ACTN")
                        zone.Actions = ReadActions(_reader.ReadUInt16());
                    else
                        zone.Name = System.Text.Encoding.Latin1.GetString(_reader.ReadBytes(16)).Split('\0')[0];
                }
                break;
        }
    }

    private void Expect(string marker)
    {
        var at = _reader.BaseStream.Position;
        var found = System.Text.Encoding.ASCII.GetString(_reader.ReadBytes(4));
        if (found != marker)
            throw new InvalidDataException($"Expected {marker} at 0x{at:X}, found \"{found}\"");
    }

    /// <summary>
    /// Parses one IACT (action script) block.
    ///
    /// Layout, verified against WebFun's file-format/categories/action.ts:
    ///
    ///   uint32  size of the block
    ///   uint16  condition count, followed by that many items
    ///   uint16  instruction count, followed by that many items
    ///
    /// and an item is always:
    ///
    ///   uint16  opcode
    ///   int16   argument x5   - ALWAYS five slots, used or not
    ///   uint16  text length
    ///   bytes   text
    ///
    /// The five argument slots are the part that is easy to get wrong: this parser used
    /// to read the first slot as an argument *count* and the second as the text length,
    /// which shifted every argument by one and turned dialogue into slices of the
    /// following section headers. Opcode argument indices in ActionExecutor and
    /// ScriptEditorWindow are written against the layout above.
    /// </summary>
    /// <summary>
    /// Indiana Jones numbers its script opcodes differently from Yoda Stories - same ideas,
    /// shuffled order, a few missing. WebFun's own docs leave this as a TODO. Decoded from the
    /// scripts themselves: each opcode's argument shapes, then what it does in context (the
    /// bartender's dialogue branches on RandomIs after RollDice; twenty "monster n is dead ->
    /// add 1" actions feed a CounterIs that makes Indy complain about snakes; delivering an
    /// artefact runs PlaySound 14, RemoveItem, SpeakNpc). Parsed Indy scripts are rewritten
    /// into Yoda's numbering, so everything downstream - the executor, the script editor -
    /// has one opcode table. See docs/SCRIPTING.md.
    /// </summary>
    private static readonly Dictionary<int, int> IndyConditions = new()
    {
        [0x01] = (int)ConditionOpcode.StandingOn,
        [0x02] = (int)ConditionOpcode.Bump,
        [0x03] = (int)ConditionOpcode.PlacedItemIs,
        [0x04] = (int)ConditionOpcode.ZoneNotInitialized,
        [0x05] = (int)ConditionOpcode.ZoneEntered,
        [0x06] = (int)ConditionOpcode.CounterIs,
        [0x09] = (int)ConditionOpcode.HasGoalItem,         // gate on handing Marcus the artefact
        [0x0a] = (int)ConditionOpcode.HasItem,
        [0x0c] = (int)ConditionOpcode.RandomIsGreaterThan,
        [0x0d] = (int)ConditionOpcode.RandomIs,
        [0x0e] = (int)ConditionOpcode.RandomIsLessThan,
        [0x0f] = IndyHasFindItem,                           // picked up this sector's item
        [0x10] = (int)ConditionOpcode.EnterByPlane,        // arrived by vehicle or tunnel
        [0x11] = (int)ConditionOpcode.MonsterIsDead,
        [0x12] = (int)ConditionOpcode.HasNoActiveMonsters,
        [0x13] = (int)ConditionOpcode.TileAtIs,
        [0x14] = (int)ConditionOpcode.RequiredItemIs,
        [0x15] = (int)ConditionOpcode.EndingIs,
        [0x16] = (int)ConditionOpcode.HealthIsLessThan,
    };

    private static readonly Dictionary<int, int> IndyInstructions = new()
    {
        [0x00] = (int)InstructionOpcode.PlaceTile,
        [0x01] = (int)InstructionOpcode.MoveTile,
        [0x02] = (int)InstructionOpcode.RemoveTile,
        [0x03] = (int)InstructionOpcode.EnableMonster,
        [0x04] = (int)InstructionOpcode.DisableMonster,
        [0x05] = (int)InstructionOpcode.SpeakHero,
        [0x06] = (int)InstructionOpcode.Redraw,
        [0x09] = (int)InstructionOpcode.SetCounter,
        [0x0a] = (int)InstructionOpcode.AddToCounter,
        [0x0b] = (int)InstructionOpcode.PlaySound,
        [0x0c] = (int)InstructionOpcode.Wait,
        [0x0d] = (int)InstructionOpcode.RollDice,
        [0x0e] = (int)InstructionOpcode.HideHero,
        [0x0f] = (int)InstructionOpcode.ShowHero,
        [0x10] = (int)InstructionOpcode.DrawTile,
        [0x11] = (int)InstructionOpcode.SetTileNeedsDisplay,
        [0x12] = (int)InstructionOpcode.MoveHeroTo,
        [0x13] = (int)InstructionOpcode.SetRectNeedsDisplay,
        [0x15] = (int)InstructionOpcode.DisableAction,
        [0x16] = (int)InstructionOpcode.EnableHotspot,
        [0x17] = (int)InstructionOpcode.DisableHotspot,
        [0x18] = (int)InstructionOpcode.EnableAllMonsters,
        [0x19] = (int)InstructionOpcode.DisableAllMonsters,
        [0x1a] = (int)InstructionOpcode.MoveHeroBy,
        [0x1b] = (int)InstructionOpcode.WinGame,              // right after taking the artefact
        [0x1c] = (int)InstructionOpcode.SpeakNpc,
        [0x1d] = (int)InstructionOpcode.AddItem,
        [0x1e] = (int)InstructionOpcode.DropItem,
        [0x1f] = (int)InstructionOpcode.LoseGame,             // the alien abduction
        [0x20] = (int)InstructionOpcode.RemoveItem,
        [0x21] = (int)InstructionOpcode.MarkAsSolved,
        [0x22] = (int)InstructionOpcode.ChangeZone,
    };

    /// <summary>Indy 0x0f has no Yoda twin; it becomes HasItem with -1, "this sector's find item".</summary>
    private const int IndyHasFindItem = -2;

    /// <summary>Opcodes not yet decoded (conditions 0x07/0x08, instructions 0x14/0x23) - kept visible, not guessed.</summary>
    private const int IndyUnknown = 0xFF;

    private Dictionary<int, int>? _indyConditions => _data.GameType == GameType.IndianaJones ? IndyConditions : null;
    private Dictionary<int, int>? _indyInstructions => _data.GameType == GameType.IndianaJones ? IndyInstructions : null;

    private static int MapIndy(Dictionary<int, int> table, int opcode) =>
        table.TryGetValue(opcode, out var mapped) ? (mapped == IndyHasFindItem ? (int)ConditionOpcode.HasItem : mapped) : IndyUnknown;

    private Data.Action ParseAction(BinaryReader reader)
    {
        var action = new Data.Action();
        var length = reader.ReadUInt32();
        // Indiana Jones writes 0 here for every block; the counts are the real framing there.
        var endPos = length == 0 ? reader.BaseStream.Length : reader.BaseStream.Position + length;

        try
        {
            var conditionCount = reader.ReadUInt16();
            for (int i = 0; i < conditionCount && reader.BaseStream.Position < endPos; i++)
            {
                var (opcode, args, text) = ReadActionItem(reader);
                if (_indyConditions != null && opcode == 0x0f && args.Count > 0)
                    args[0] = -1; // IndyHasFindItem: HasItem(-1), this sector's find item
                action.Conditions.Add(new Condition
                {
                    Opcode = (ConditionOpcode)(_indyConditions != null ? MapIndy(_indyConditions, opcode) : opcode),
                    Arguments = args,
                    Text = text
                });
            }

            if (reader.BaseStream.Position < endPos)
            {
                var instructionCount = reader.ReadUInt16();
                for (int i = 0; i < instructionCount && reader.BaseStream.Position < endPos; i++)
                {
                    var (opcode, args, text) = ReadActionItem(reader);
                    action.Instructions.Add(new Instruction
                    {
                        Opcode = (InstructionOpcode)(_indyInstructions != null ? MapIndy(_indyInstructions, opcode) : opcode),
                        Arguments = args,
                        Text = text
                    });
                }
            }
        }
        catch (EndOfStreamException)
        {
            // Truncated action - keep whatever parsed and resync below.
        }

        // Resync to the block boundary whether or not the body parsed cleanly.
        if (length != 0 && reader.BaseStream.Position != endPos && endPos <= reader.BaseStream.Length)
            reader.BaseStream.Seek(endPos, SeekOrigin.Begin);

        return action;
    }

    private static (ushort opcode, List<short> arguments, string? text) ReadActionItem(BinaryReader reader)
    {
        var opcode = reader.ReadUInt16();

        var arguments = new List<short>(ActionItemArgumentCount);
        for (int i = 0; i < ActionItemArgumentCount; i++)
            arguments.Add(reader.ReadInt16());

        var textLength = reader.ReadUInt16();
        string? text = null;
        if (textLength > 0)
        {
            // The originals are ISO-8859-2; Latin1 is byte-identical over the ASCII range
            // the games actually use and needs no encoding provider registration.
            text = System.Text.Encoding.Latin1.GetString(reader.ReadBytes(textLength)).TrimEnd('\0');
        }

        return (opcode, arguments, text);
    }

    /// <summary>
    /// PUZ2: a list of int16 index (-1 ends it), "IPUZ", uint32 size, then
    ///
    ///   Yoda:  uint32 type, uint32 item1 class, uint32 item2 class, uint16 unknown,
    ///          5 x (uint16 length, text), uint16 item1, uint16 item2
    ///   Indy:  uint32 type, uint32 item1 class,                     uint16 unknown,
    ///          5 x (uint16 length, text), uint16 item1
    ///
    /// WebFun and its Kaitai spec read Indiana Jones' first field as an item class and
    /// give every Indy puzzle no type. It is the type: it matches the suffix of every
    /// puzzle's PNAM name without exception (TX 0, KL 1, GW 2, GO 3 - Transaction,
    /// Offer, Goal, Mission), and the 15 GO puzzles pair with Indy's 15 Goal zones.
    /// </summary>
    private void ParsePuzzlesSection(uint length)
    {
        bool yoda = _data.GameType != GameType.IndianaJones;
        var endPos = _reader.BaseStream.Position + length;

        while (_reader.BaseStream.Position + 2 <= endPos)
        {
            if (_reader.ReadInt16() == -1)
                break;

            Expect("IPUZ");
            var size = _reader.ReadUInt32();
            var next = _reader.BaseStream.Position + size;

            var puzzle = new Puzzle { Id = _data.Puzzles.Count, Type = (PuzzleType)_reader.ReadInt32() };
            _reader.ReadUInt32();                    // item1 class
            if (yoda) _reader.ReadUInt32();          // item2 class
            _reader.ReadUInt16();                    // unknown

            for (int i = 0; i < 5; i++)
                puzzle.Strings.Add(System.Text.Encoding.Latin1.GetString(_reader.ReadBytes(_reader.ReadUInt16())).TrimEnd('\0'));

            puzzle.Item1 = _reader.ReadUInt16();
            if (yoda)
            {
                var item2 = _reader.ReadUInt16();
                puzzle.Item2 = item2 == 0 ? null : item2;   // WebFun: no second item when 0
            }

            _reader.BaseStream.Seek(next, SeekOrigin.Begin);
            _data.Puzzles.Add(puzzle);
        }

        _reader.BaseStream.Seek(endPos, SeekOrigin.Begin);

        var typeStats = _data.Puzzles.GroupBy(p => p.Type).OrderBy(g => g.Key).Select(g => $"{g.Key}={g.Count()}");
        Console.WriteLine($"Loaded {_data.Puzzles.Count} puzzles: {string.Join(", ", typeStats)}");
    }

    private void ParseCharactersSection(uint length)
    {
        var startPos = _reader.BaseStream.Position;
        var endPos = startPos + length;

        // Each entry: int16 index (-1 ends the list), "ICHA", uint32 size, then size bytes.
        // The size differs by game - Yoda Stories has 6 bytes before the frames that
        // Indiana Jones does not - so read what each entry says rather than assume one size.
        int frameBase = _data.GameType == GameType.IndianaJones ? 30 : 36;

        while (_reader.BaseStream.Position + 10 <= endPos)
        {
            var header = _reader.ReadBytes(10);
            if (BitConverter.ToInt16(header, 0) == -1)
                break;

            var size = BitConverter.ToInt32(header, 6);
            var charData = header.Concat(_reader.ReadBytes(size)).ToArray();
            _data.Characters.Add(ParseCharacterData(_data.Characters.Count, charData, frameBase));
        }

        // Ensure we're at the end of the section
        _reader.BaseStream.Seek(endPos, SeekOrigin.Begin);
    }

    private Character ParseCharacterData(int index, byte[] data, int frameBase)
    {
        var character = new Character { Id = index };

        // Character ID at bytes 0-1
        var charId = BitConverter.ToUInt16(data, 0);

        // Name starts at byte 10, null-terminated
        var nameEnd = 10;
        while (nameEnd < 36 && data[nameEnd] != 0)
            nameEnd++;
        if (nameEnd > 10)
            character.Name = System.Text.Encoding.ASCII.GetString(data, 10, nameEnd - 10);

        // Both games store the character type after the fixed-size name field.
        var storedType = (CharacterType)BitConverter.ToUInt16(data, 26);
        character.Type = storedType is CharacterType.Hero or CharacterType.Enemy or CharacterType.Weapon
            ? storedType : InferCharacterType(character.Name, index);
        // Yoda uses an animated CHAR record for the collectible companion as well.
        // Townsfolk and puzzle NPCs otherwise live in the scripted tile layer.
        if (_data.GameType == GameType.YodaStories &&
            character.Name.StartsWith("R2", StringComparison.OrdinalIgnoreCase))
            character.Type = CharacterType.Friendly;

        // CHAR stores three animation frames, each with eight tile slots. The game uses
        // slots 0, 1, 3, and 6 for up, down, left, and right respectively. Slot 7 of the
        // first frame is the inventory icon for weapon characters.
        var frames = new CharacterFrames();

        ushort ReadFrameTile(int frame, int slot) =>
            BitConverter.ToUInt16(data, frameBase + frame * 16 + slot * 2);

        frames.WalkUp = new[]
        {
            ReadFrameTile(0, 0), ReadFrameTile(1, 0), ReadFrameTile(2, 0)
        };
        frames.WalkDown = new[]
        {
            ReadFrameTile(0, 1), ReadFrameTile(1, 1), ReadFrameTile(2, 1)
        };
        frames.WalkLeft = new[]
        {
            ReadFrameTile(0, 3), ReadFrameTile(1, 3), ReadFrameTile(2, 3)
        };
        frames.WalkRight = new[]
        {
            ReadFrameTile(0, 6), ReadFrameTile(1, 6), ReadFrameTile(2, 6)
        };

        // Last slot of the first frame is the inventory tile for weapon characters.
        frames.ExtensionUp = Enumerable.Range(0, 3).Select(frame => ReadFrameTile(frame, 2)).ToArray();
        frames.ExtensionDown = Enumerable.Range(0, 3).Select(frame => ReadFrameTile(frame, 4)).ToArray();
        frames.ExtensionLeft = Enumerable.Range(0, 3).Select(frame => ReadFrameTile(frame, 5)).ToArray();
        frames.ExtensionRight = Enumerable.Range(0, 3).Select(frame => ReadFrameTile(frame, 7)).ToArray();

        character.Frames = frames;

        return character;
    }

    /// <summary>
    /// Fallback for character records whose stored type is unrecognized.
    /// </summary>
    private CharacterType InferCharacterType(string name, int index)
    {
        // Luke Skywalker is the hero (usually character 0)
        if (index == 0 || name.Contains("Luke", StringComparison.OrdinalIgnoreCase))
            return CharacterType.Hero;

        var nameLower = name.ToLowerInvariant();

        // Enemy patterns - Stormtroopers, monsters, droids, bounty hunters
        string[] enemyPatterns = {
            "trooper", "st-", "wampa", "probot", "fett", "greedo", "tuscan", "tusken",
            "sarlacc", "rancor", "scorpion", "bug", "beetle", "droid", "mine", "tank",
            "gurk", "jawa", "enemy", "attack", "hard", "patrol", "generator",
            "ree yees", "invisi", "boushh", "snowstar", "icebug", "sandworm", "timer"
        };

        foreach (var pattern in enemyPatterns)
        {
            if (nameLower.Contains(pattern))
                return CharacterType.Enemy;
        }

        // Friendly patterns
        string[] friendlyPatterns = {
            "ewok", "snowman", "blank"
        };

        foreach (var pattern in friendlyPatterns)
        {
            if (nameLower.Contains(pattern))
                return CharacterType.Friendly;
        }

        // Weapons/items are not NPCs - treat as friendly (won't be spawned anyway)
        string[] weaponPatterns = {
            "blaster", "rifle", "saber", "force", "thermal", "det"
        };

        foreach (var pattern in weaponPatterns)
        {
            if (nameLower.Contains(pattern))
                return CharacterType.Friendly;
        }

        // Default to friendly for unknown characters
        return CharacterType.Friendly;
    }

    private void ParseCharacterWeaponsSection(uint length)
    {
        var endPos = _reader.BaseStream.Position + length;

        // Each entry: int16 character index (-1 ends the list), int16 reference, uint16 health.
        while (_reader.BaseStream.Position + 2 <= endPos)
        {
            var index = _reader.ReadInt16();
            if (index == -1)
                break;

            var reference = _reader.ReadInt16();
            var health = _reader.ReadUInt16();

            if (index >= 0 && index < _data.Characters.Count)
            {
                _data.Characters[index].Weapon = new CharacterWeapon
                {
                    Reference = reference,
                    Health = health
                };
            }
        }

        _reader.BaseStream.Seek(endPos, SeekOrigin.Begin);
    }

    private void ParseCharacterAuxSection(uint length)
    {
        var endPos = _reader.BaseStream.Position + length;

        // Each entry: int16 character index (-1 ends the list), int16 damage.
        while (_reader.BaseStream.Position + 2 <= endPos)
        {
            var index = _reader.ReadInt16();
            if (index == -1)
                break;

            var damage = _reader.ReadInt16();

            if (index >= 0 && index < _data.Characters.Count)
                _data.Characters[index].AuxData = new CharacterAux { Damage = damage };
        }

        _reader.BaseStream.Seek(endPos, SeekOrigin.Begin);
    }

    /// <summary>
    /// TNAM: int16 tile id (-1 ends the list), then the name in a fixed-width, NUL-padded
    /// field - 24 bytes in Yoda Stories, 16 in Indiana Jones (WebFun categories/tile.ts).
    /// </summary>
    private void ParseTileNamesSection(uint length)
    {
        var endPos = _reader.BaseStream.Position + length;
        int width = _data.GameType == GameType.IndianaJones ? 0x10 : 0x18;

        while (_reader.BaseStream.Position + 2 <= endPos)
        {
            var tileId = _reader.ReadInt16();
            if (tileId == -1)
                break;

            var name = System.Text.Encoding.Latin1.GetString(_reader.ReadBytes(width)).Split('\0')[0];
            if (name.Length > 0)
                _data.TileNames[tileId] = name;
        }

        _reader.BaseStream.Seek(endPos, SeekOrigin.Begin);
        Console.WriteLine($"Loaded {_data.TileNames.Count} tile names");
    }
}
