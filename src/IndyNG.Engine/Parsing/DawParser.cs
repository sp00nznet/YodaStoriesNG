using IndyNG.Engine.Data;

namespace IndyNG.Engine.Parsing;

/// <summary>
/// Parser for Indiana Jones Desktop Adventures .DAW files.
/// Similar format to Yoda Stories .DTA files.
/// </summary>
public class DawParser : IDisposable
{
    private BinaryReader _reader;
    private GameData _data = new();

    public DawParser(string filePath)
    {
        var stream = File.OpenRead(filePath);
        _reader = new BinaryReader(stream);
    }

    public void Dispose()
    {
        _reader.Dispose();
    }

    public GameData Parse()
    {
        Console.WriteLine($"Parsing DAW file ({_reader.BaseStream.Length} bytes)...");

        while (_reader.BaseStream.Position < _reader.BaseStream.Length - 4)
        {
            var sectionStart = _reader.BaseStream.Position;
            var marker = ReadMarker();

            if (string.IsNullOrEmpty(marker) || marker.Any(c => c < 32 || c > 126))
            {
                // Invalid marker - might be at end or corrupted
                break;
            }

            switch (marker)
            {
                case "VERS":
                    ParseVersion();
                    break;
                case "STUP":
                    ParseSetup();
                    break;
                case "SNDS":
                    ParseSounds();
                    break;
                case "TILE":
                    ParseTiles();
                    break;
                case "ZONE":
                    ParseZones();
                    break;
                case "PUZ2":
                    ParsePuzzles();
                    break;
                case "CHAR":
                    ParseCharacters();
                    break;
                case "CHWP":
                    ParseCharacterWeapons();
                    break;
                case "CAUX":
                    ParseCharacterAux();
                    break;
                case "TNAM":
                    ParseTileNames();
                    break;
                case "ENDF":
                    Console.WriteLine("Reached end of file marker");
                    return _data;
                default:
                    Console.WriteLine($"Unknown section '{marker}' at 0x{sectionStart:X}, skipping...");
                    // Try to skip unknown section
                    var size = _reader.ReadUInt32();
                    if (size > 0 && size < _reader.BaseStream.Length)
                        _reader.BaseStream.Seek(size, SeekOrigin.Current);
                    break;
            }
        }

        return _data;
    }

    private string ReadMarker()
    {
        var bytes = _reader.ReadBytes(4);
        return System.Text.Encoding.ASCII.GetString(bytes);
    }

    private void ParseVersion()
    {
        _data.Version = _reader.ReadInt32();
        Console.WriteLine($"Game version: {_data.Version}");
    }

    private void ParseSetup()
    {
        var size = _reader.ReadUInt32();
        Console.WriteLine($"STUP section: {size} bytes");

        // STUP contains the color palette (256 * 4 bytes = 1024 bytes for RGBA)
        // Plus additional setup data
        _data.Palette = _reader.ReadBytes((int)size);
    }

    private void ParseSounds()
    {
        var size = _reader.ReadUInt32();
        var endPos = _reader.BaseStream.Position + size;

        // Read sound file paths (null-terminated strings with size prefix)
        // Format: count(2), then for each: size(2), path(size)
        if (size >= 2)
        {
            var soundCount = _reader.ReadUInt16();
            Console.WriteLine($"SNDS section: {soundCount} sounds");

            for (int i = 0; i < soundCount && _reader.BaseStream.Position < endPos; i++)
            {
                try
                {
                    var strLen = _reader.ReadUInt16();
                    if (strLen > 0 && strLen < 1000)
                    {
                        var soundPath = System.Text.Encoding.ASCII.GetString(_reader.ReadBytes(strLen));
                        // Remove any null characters
                        soundPath = soundPath.TrimEnd('\0');
                        _data.Sounds.Add(soundPath);
                    }
                }
                catch
                {
                    break;
                }
            }
        }

        // Ensure we're at the end of the section
        _reader.BaseStream.Seek(endPos, SeekOrigin.Begin);
        Console.WriteLine($"Loaded {_data.Sounds.Count} sounds");
    }

    private void ParseTiles()
    {
        var size = _reader.ReadUInt32();
        var endPos = _reader.BaseStream.Position + size;
        Console.WriteLine($"TILE section: {size} bytes at 0x{_reader.BaseStream.Position:X}");

        // Tiles are stored sequentially: 4 bytes flags + 1024 bytes pixel data each
        // No tile count header - just read until end of section
        int tileId = 0;
        while (_reader.BaseStream.Position + 4 + 1024 <= endPos)
        {
            var tile = new Tile { Id = tileId++ };
            tile.Flags = (TileFlags)_reader.ReadUInt32();
            tile.PixelData = _reader.ReadBytes(1024); // 32x32 pixels
            _data.Tiles.Add(tile);
        }

        _reader.BaseStream.Seek(endPos, SeekOrigin.Begin);
        Console.WriteLine($"Loaded {_data.Tiles.Count} tiles");
    }

    private void ParseZones()
    {
        var size = _reader.ReadUInt32();
        var endPos = _reader.BaseStream.Position + size;
        Console.WriteLine($"ZONE section: {size} bytes");

        // Skip zone count header (2 bytes count + 2 bytes padding)
        _reader.ReadUInt16();
        _reader.ReadUInt16();

        int zoneId = 0;
        int validZones = 0;

        // Scan for IZON markers within the section
        while (_reader.BaseStream.Position + 4 < endPos)
        {
            var markerPos = _reader.BaseStream.Position;
            var marker = ReadMarker();

            if (marker == "IZON")
            {
                try
                {
                    var zone = ParseSingleZone(zoneId);
                    _data.Zones.Add(zone);
                    if (zone.Width > 0)
                        validZones++;
                    zoneId++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error parsing zone {zoneId}: {ex.Message}");
                    _data.Zones.Add(new Zone { Id = zoneId });
                    zoneId++;
                }
            }
            else if (marker == "PUZ2" || marker == "CHAR" || marker == "CHWP" ||
                     marker == "CAUX" || marker == "TNAM" || marker == "ENDF")
            {
                // Hit another section - go back and exit
                _reader.BaseStream.Seek(markerPos, SeekOrigin.Begin);
                break;
            }
            else
            {
                // Unknown data, try next byte
                _reader.BaseStream.Seek(markerPos + 1, SeekOrigin.Begin);
            }
        }

        _reader.BaseStream.Seek(endPos, SeekOrigin.Begin);
        Console.WriteLine($"Loaded {_data.Zones.Count} zones ({validZones} valid)");
    }

    private Zone ParseSingleZone(int zoneId)
    {
        var zone = new Zone { Id = zoneId };

        // IZON marker already consumed by caller
        // Yoda Stories format: size(4), width(2), height(2), flags(1), pad(5), planet(1), pad(1) = 16 bytes
        var sizeInfo = _reader.ReadUInt32();
        zone.Width = _reader.ReadUInt16();
        zone.Height = _reader.ReadUInt16();
        zone.Type = (ZoneType)_reader.ReadByte();
        _reader.ReadBytes(5); // padding
        zone.Planet = (Planet)_reader.ReadByte();
        _reader.ReadByte(); // unused

        // Sanity check dimensions
        if (zone.Width == 0 || zone.Height == 0 || zone.Width > 18 || zone.Height > 18)
        {
            return zone;
        }

        // Read tile grid - exact Yoda Stories format (row-major, interleaved)
        zone.TileGrid = new ushort[zone.Height, zone.Width, 3];
        for (int y = 0; y < zone.Height; y++)
        {
            for (int x = 0; x < zone.Width; x++)
            {
                zone.TileGrid[y, x, 0] = _reader.ReadUInt16();
                zone.TileGrid[y, x, 1] = _reader.ReadUInt16();
                zone.TileGrid[y, x, 2] = _reader.ReadUInt16();
            }
        }

        // Read object count and objects
        var objectCount = _reader.ReadUInt16();
        for (int i = 0; i < objectCount && i < 1000; i++)
        {
            // Object format: Type(2), pad(2), X(2), Y(2), pad(2), Argument(2) = 12 bytes
            var objType = (ZoneObjectType)_reader.ReadUInt16();
            _reader.ReadUInt16(); // padding
            var objX = _reader.ReadUInt16();
            var objY = _reader.ReadUInt16();
            _reader.ReadUInt16(); // padding
            var objArg = _reader.ReadUInt16();

            zone.Objects.Add(new ZoneObject
            {
                Type = objType,
                X = objX,
                Y = objY,
                Argument = objArg
            });
        }

        // Parse auxiliary sections
        while (_reader.BaseStream.Position + 4 < _reader.BaseStream.Length)
        {
            var auxPos = _reader.BaseStream.Position;
            var auxTag = ReadMarker();

            switch (auxTag)
            {
                case "IZAX":
                    var izaxLen = _reader.ReadUInt16();
                    var izaxData = _reader.ReadBytes(Math.Max(0, izaxLen - 6));
                    zone.AuxData = ParseZoneAuxFromBytes(izaxData);
                    break;
                case "IZX2":
                    var izx2Len = _reader.ReadUInt16();
                    _reader.ReadBytes(Math.Max(0, izx2Len - 6));
                    break;
                case "IZX3":
                    var izx3Len = _reader.ReadUInt16();
                    _reader.ReadBytes(Math.Max(0, izx3Len - 6));
                    break;
                case "IZX4":
                    _reader.ReadBytes(8);
                    break;
                case "IACT":
                    var action = ParseZoneActionInline();
                    if (action.Instructions.Count > 0 || action.Conditions.Count > 0)
                        zone.Actions.Add(action);
                    break;
                default:
                    // Not a zone subsection - go back and return
                    _reader.BaseStream.Seek(auxPos, SeekOrigin.Begin);
                    return zone;
            }
        }

        return zone;
    }

    private ZoneAuxData ParseZoneAuxFromBytes(byte[] data)
    {
        var aux = new ZoneAuxData { RawData = data };

        if (data.Length >= 6)
        {
            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms);

            try
            {
                var entityCount = reader.ReadUInt16();

                for (int i = 0; i < entityCount && ms.Position + 12 <= ms.Length; i++)
                {
                    var entity = new ZoneEntity
                    {
                        CharacterId = reader.ReadUInt16(),
                        X = reader.ReadUInt16(),
                        Y = reader.ReadUInt16()
                    };

                    if (ms.Position + 6 <= ms.Length)
                    {
                        entity.ItemTileId = reader.ReadUInt16();
                        entity.ItemQuantity = reader.ReadUInt16();
                        reader.ReadUInt16(); // Skip padding
                    }

                    if (entity.CharacterId != 0xFFFF)
                        aux.Entities.Add(entity);
                }
            }
            catch { }
        }

        return aux;
    }

    private ZoneAction ParseZoneActionInline()
    {
        var action = new ZoneAction();

        // Action length (2 bytes)
        var actionLen = _reader.ReadUInt16();
        var endPos = _reader.BaseStream.Position + actionLen - 6;

        // Conditions count
        var conditionCount = _reader.ReadUInt16();
        for (int i = 0; i < conditionCount && _reader.BaseStream.Position < endPos; i++)
        {
            var condition = new ActionCondition
            {
                Opcode = _reader.ReadUInt16()
            };

            for (int j = 0; j < 5; j++)
                condition.Arguments.Add(_reader.ReadUInt16());

            _reader.ReadUInt16(); // text length (0 for conditions)
            action.Conditions.Add(condition);
        }

        // Instructions count
        if (_reader.BaseStream.Position >= endPos) return action;

        var instructionCount = _reader.ReadUInt16();
        for (int i = 0; i < instructionCount && _reader.BaseStream.Position < endPos; i++)
        {
            var instruction = new ActionInstruction
            {
                Opcode = _reader.ReadUInt16()
            };

            for (int j = 0; j < 5; j++)
                instruction.Arguments.Add(_reader.ReadUInt16());

            var textLen = _reader.ReadUInt16();
            if (textLen > 0 && textLen < 1000)
            {
                instruction.Text = System.Text.Encoding.ASCII.GetString(_reader.ReadBytes(textLen));
            }

            action.Instructions.Add(instruction);
        }

        return action;
    }

    private void ParsePuzzles()
    {
        var size = _reader.ReadUInt32();
        var endPos = _reader.BaseStream.Position + size;
        Console.WriteLine($"PUZ2 section: {size} bytes");

        // Look for IPUZ markers within the section
        int puzzleId = 0;
        while (_reader.BaseStream.Position < endPos - 8)
        {
            var pos = _reader.BaseStream.Position;
            var marker = ReadMarker();

            if (marker == "IPUZ")
            {
                var puzzleSize = _reader.ReadUInt32();
                var puzzle = ParseSinglePuzzle(puzzleId++, puzzleSize);
                _data.Puzzles.Add(puzzle);
            }
            else
            {
                // Not a puzzle marker, go back and skip a byte
                _reader.BaseStream.Seek(pos + 1, SeekOrigin.Begin);
            }
        }

        _reader.BaseStream.Seek(endPos, SeekOrigin.Begin);

        // Count by type
        var quest = _data.Puzzles.Count(p => p.Type == PuzzleType.Quest);
        var transport = _data.Puzzles.Count(p => p.Type == PuzzleType.Transport);
        var trade = _data.Puzzles.Count(p => p.Type == PuzzleType.Trade);
        var use = _data.Puzzles.Count(p => p.Type == PuzzleType.Use);
        var goal = _data.Puzzles.Count(p => p.Type == PuzzleType.Goal);

        Console.WriteLine($"Loaded {_data.Puzzles.Count} puzzles: Quest={quest}, Transport={transport}, Trade={trade}, Use={use}, Goal={goal}");
    }

    private Puzzle ParseSinglePuzzle(int id, uint size)
    {
        var puzzle = new Puzzle { Id = id };
        var endPos = _reader.BaseStream.Position + size;

        // Puzzle header
        puzzle.Type = (PuzzleType)_reader.ReadUInt32();
        puzzle.Item1 = _reader.ReadUInt16();
        puzzle.Item2 = _reader.ReadUInt16();

        // Skip some header bytes
        _reader.ReadBytes(4);

        // Read strings (5 strings max)
        for (int i = 0; i < 5 && _reader.BaseStream.Position < endPos - 2; i++)
        {
            var strLen = _reader.ReadUInt16();
            if (strLen > 0 && strLen < 500)
            {
                var str = System.Text.Encoding.ASCII.GetString(_reader.ReadBytes(strLen));
                puzzle.Strings.Add(str.TrimEnd('\0'));
            }
            else if (strLen == 0)
            {
                puzzle.Strings.Add("");
            }
            else
            {
                break;
            }
        }

        _reader.BaseStream.Seek(endPos, SeekOrigin.Begin);
        return puzzle;
    }

    private void ParseCharacters()
    {
        var size = _reader.ReadUInt32();
        var endPos = _reader.BaseStream.Position + size;
        Console.WriteLine($"CHAR section: {size} bytes");

        // Skip ICHA marker if present
        var marker = ReadMarker();
        if (marker != "ICHA")
        {
            _reader.BaseStream.Seek(-4, SeekOrigin.Current);
        }

        // Character count
        int charId = 0;
        while (_reader.BaseStream.Position < endPos - 30)
        {
            var character = new Character { Id = charId++ };

            // Read character name (null-terminated, padded to 16 bytes)
            var nameBytes = _reader.ReadBytes(16);
            var nameEnd = Array.IndexOf(nameBytes, (byte)0);
            character.Name = nameEnd >= 0
                ? System.Text.Encoding.ASCII.GetString(nameBytes, 0, nameEnd)
                : System.Text.Encoding.ASCII.GetString(nameBytes).TrimEnd('\0');

            character.Type = _reader.ReadUInt16();

            // Read animation frames (4 directions, 3 frames each)
            character.Frames.WalkUp = new ushort[3];
            character.Frames.WalkDown = new ushort[3];
            character.Frames.WalkLeft = new ushort[3];
            character.Frames.WalkRight = new ushort[3];

            for (int i = 0; i < 3; i++) character.Frames.WalkUp[i] = _reader.ReadUInt16();
            for (int i = 0; i < 3; i++) character.Frames.WalkDown[i] = _reader.ReadUInt16();
            for (int i = 0; i < 3; i++) character.Frames.WalkLeft[i] = _reader.ReadUInt16();
            for (int i = 0; i < 3; i++) character.Frames.WalkRight[i] = _reader.ReadUInt16();

            _data.Characters.Add(character);
        }

        _reader.BaseStream.Seek(endPos, SeekOrigin.Begin);
        Console.WriteLine($"Loaded {_data.Characters.Count} characters");
    }

    private void ParseCharacterWeapons()
    {
        var size = _reader.ReadUInt32();
        // Skip weapon data for now
        _reader.BaseStream.Seek(size, SeekOrigin.Current);
        Console.WriteLine($"CHWP section: {size} bytes (skipped)");
    }

    private void ParseCharacterAux()
    {
        var size = _reader.ReadUInt32();
        // Skip aux data for now
        _reader.BaseStream.Seek(size, SeekOrigin.Current);
        Console.WriteLine($"CAUX section: {size} bytes (skipped)");
    }

    private void ParseTileNames()
    {
        var size = _reader.ReadUInt32();
        var endPos = _reader.BaseStream.Position + size;
        Console.WriteLine($"TNAM section: {size} bytes");

        while (_reader.BaseStream.Position < endPos - 4)
        {
            var tileId = _reader.ReadUInt16();
            var nameLen = _reader.ReadUInt16();

            if (nameLen > 0 && nameLen < 100)
            {
                var name = System.Text.Encoding.ASCII.GetString(_reader.ReadBytes(nameLen));
                _data.TileNames[tileId] = name.TrimEnd('\0');
            }
            else if (nameLen == 0)
            {
                continue;
            }
            else
            {
                break;
            }
        }

        _reader.BaseStream.Seek(endPos, SeekOrigin.Begin);
        Console.WriteLine($"Loaded {_data.TileNames.Count} tile names");
    }
}
