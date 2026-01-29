using YodaStoriesNG.Engine.Data;

namespace YodaStoriesNG.Engine.Parsing;

/// <summary>
/// Parser for YODESK.DTA game data files.
/// </summary>
public class DtaParser
{
    private BinaryReader _reader = null!;
    private GameData _data = null!;

    /// <summary>
    /// Parses a DTA file and returns the game data.
    /// </summary>
    public GameData Parse(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Parse(stream);
    }

    /// <summary>
    /// Parses a DTA file from a stream and returns the game data.
    /// </summary>
    public GameData Parse(Stream stream)
    {
        _reader = new BinaryReader(stream);
        _data = new GameData();

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

            // All other sections have a 4-byte length prefix
            if (_reader.BaseStream.Position + 4 > _reader.BaseStream.Length)
                break;

            var length = _reader.ReadUInt32();
            // Console.WriteLine($"Section '{tag}' at position {_reader.BaseStream.Position - 8}, length: {length}");
            ParseSection(tag, length);
        }

        return _data;
    }

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
            case "ZONE":
                ParseZonesSection();
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

    private void ParseZonesSection()
    {
        // Zone count is 2 bytes (not accurate, just skip it)
        _reader.ReadUInt16();
        _reader.ReadUInt16(); // padding

        int zonesLoaded = 0;
        int zoneId = 0;

        // Parse zones by scanning for IZON markers
        while (_reader.BaseStream.Position + 4 < _reader.BaseStream.Length)
        {
            var markerPos = _reader.BaseStream.Position;

            // Check for IZON marker
            var marker = System.Text.Encoding.ASCII.GetString(_reader.ReadBytes(4));

            if (marker == "IZON")
            {
                try
                {
                    var zone = ParseIZONZone(zoneId, markerPos);
                    _data.Zones.Add(zone);
                    if (zone.Width > 0)
                        zonesLoaded++;
                    zoneId++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error parsing zone {zoneId}: {ex.Message}");
                    _data.Zones.Add(new Zone { Id = zoneId, Width = 0, Height = 0 });
                    zoneId++;
                }
            }
            else if (marker == "PUZ2" || marker == "CHAR" || marker == "CHWP" ||
                     marker == "CAUX" || marker == "TNAM" || marker == "ENDF")
            {
                // Hit another section - we're done with zones
                _reader.BaseStream.Seek(markerPos, SeekOrigin.Begin);
                break;
            }
            else
            {
                // Unknown data, try to find next IZON or section marker
                _reader.BaseStream.Seek(markerPos + 1, SeekOrigin.Begin);
            }
        }

        Console.WriteLine($"Loaded {zonesLoaded} valid zones");
    }

    private Zone ParseIZONZone(int zoneId, long izonPos)
    {
        var zone = new Zone { Id = zoneId };

        // IZON format (marker already read):
        // 4 bytes: size info
        // 2 bytes: width
        // 2 bytes: height
        // 1 byte: type/flags
        // 5 bytes: padding
        // 1 byte: planet
        // 1 byte: unused
        // Then tile data...

        var sizeInfo = _reader.ReadUInt32();
        zone.Width = _reader.ReadUInt16();
        zone.Height = _reader.ReadUInt16();
        zone.Flags = (ZoneFlags)_reader.ReadByte();
        _reader.ReadBytes(5); // padding
        zone.Planet = (Planet)_reader.ReadByte();
        _reader.ReadByte(); // unused

        // Sanity check dimensions
        if (zone.Width == 0 || zone.Height == 0 || zone.Width > 18 || zone.Height > 18)
        {
            return zone;
        }

        // Read tile grid (3 layers per cell, 2 bytes per tile ID)
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

        for (int j = 0; j < objectCount; j++)
        {
            // Object format: Type(2), pad(2), X(2), Y(2), pad(2), Argument(2) = 12 bytes
            var objType = (ZoneObjectType)_reader.ReadUInt16();
            _reader.ReadUInt16(); // padding
            var objX = _reader.ReadUInt16();
            var objY = _reader.ReadUInt16();
            _reader.ReadUInt16(); // padding
            var objArg = _reader.ReadUInt16();

            var obj = new ZoneObject
            {
                Type = objType,
                X = objX,
                Y = objY,
                Argument = objArg
            };
            zone.Objects.Add(obj);
        }

        // Parse auxiliary sections
        while (_reader.BaseStream.Position + 4 < _reader.BaseStream.Length)
        {
            var auxPos = _reader.BaseStream.Position;
            var auxTag = System.Text.Encoding.ASCII.GetString(_reader.ReadBytes(4));

            switch (auxTag)
            {
                case "IZAX":
                    var izaxLen = _reader.ReadUInt16();
                    zone.AuxData = new ZoneAuxData { RawData = _reader.ReadBytes(Math.Max(0, izaxLen - 6)) };
                    break;
                case "IZX2":
                    var izx2Len = _reader.ReadUInt16();
                    var izx2Data = _reader.ReadBytes(Math.Max(0, izx2Len - 6));
                    zone.Aux2Data = new ZoneAux2Data { RawData = izx2Data };
                    break;
                case "IZX3":
                    var izx3Len = _reader.ReadUInt16();
                    zone.Aux3Data = new ZoneAux3Data { RawData = _reader.ReadBytes(Math.Max(0, izx3Len - 6)) };
                    break;
                case "IZX4":
                    zone.Aux4Data = new ZoneAux4Data { RawData = _reader.ReadBytes(8) };
                    break;
                case "IACT":
                    var actLen = _reader.ReadUInt32();
                    _reader.BaseStream.Seek(_reader.BaseStream.Position + actLen, SeekOrigin.Begin);
                    zone.Actions.Add(new Data.Action());
                    break;
                default:
                    // Not a zone subsection - go back and return
                    _reader.BaseStream.Seek(auxPos, SeekOrigin.Begin);
                    return zone;
            }
        }

        return zone;
    }

    /// <summary>
    private Zone ParseZoneData(int zoneId, byte[] zoneData)
    {
        var zone = new Zone { Id = zoneId };

        if (zoneData.Length < 22)
            return zone;

        using var ms = new MemoryStream(zoneData);
        using var reader = new BinaryReader(ms);

        // Zone data format:
        // Bytes 0-1: Zone ID (from file)
        // Bytes 2-5: "IZON" marker
        // Bytes 6-9: Size/unknown
        // Bytes 10-11: Width
        // Bytes 12-13: Height
        // Byte 14: Zone type/flags
        // Bytes 15-19: Padding
        // Byte 20: Planet
        // Byte 21: Unused
        // Bytes 22+: Tile data

        var fileZoneId = reader.ReadUInt16();
        var izonTag = System.Text.Encoding.ASCII.GetString(reader.ReadBytes(4));

        if (izonTag != "IZON")
            return zone;

        var sizeInfo = reader.ReadUInt32();
        zone.Width = reader.ReadUInt16();
        zone.Height = reader.ReadUInt16();
        zone.Flags = (ZoneFlags)reader.ReadByte();
        reader.ReadBytes(5); // padding
        zone.Planet = (Planet)reader.ReadByte();
        reader.ReadByte(); // unused

        // Sanity check dimensions
        if (zone.Width == 0 || zone.Height == 0 || zone.Width > 18 || zone.Height > 18)
        {
            return zone;
        }

        // Read tile grid (3 layers per cell, 2 bytes per tile ID)
        zone.TileGrid = new ushort[zone.Height, zone.Width, 3];
        for (int y = 0; y < zone.Height; y++)
        {
            for (int x = 0; x < zone.Width; x++)
            {
                zone.TileGrid[y, x, 0] = reader.ReadUInt16(); // Background
                zone.TileGrid[y, x, 1] = reader.ReadUInt16(); // Middle
                zone.TileGrid[y, x, 2] = reader.ReadUInt16(); // Foreground
            }
        }

        // Read object count and objects (hotspots)
        if (ms.Position + 2 <= ms.Length)
        {
            var objectCount = reader.ReadUInt16();
            for (int j = 0; j < objectCount && ms.Position + 12 <= ms.Length; j++)
            {
                zone.Objects.Add(ParseZoneObjectFromReader(reader));
            }
        }

        // Parse auxiliary sections by looking for known tags
        while (ms.Position + 4 <= ms.Length)
        {
            var auxTag = System.Text.Encoding.ASCII.GetString(reader.ReadBytes(4));

            switch (auxTag)
            {
                case "IZAX":
                    zone.AuxData = ParseIZAXFromReader(reader);
                    break;
                case "IZX2":
                    zone.Aux2Data = ParseIZX2FromReader(reader);
                    break;
                case "IZX3":
                    zone.Aux3Data = ParseIZX3FromReader(reader);
                    break;
                case "IZX4":
                    zone.Aux4Data = ParseIZX4FromReader(reader);
                    break;
                case "IACT":
                    zone.Actions.Add(ParseActionFromReader(reader, ms));
                    break;
                default:
                    // Unknown tag or end of zone - stop parsing
                    return zone;
            }
        }

        return zone;
    }

    private ZoneObject ParseZoneObjectFromReader(BinaryReader reader)
    {
        var type = (ZoneObjectType)reader.ReadUInt16();
        reader.ReadUInt16(); // padding
        var x = reader.ReadUInt16();
        var y = reader.ReadUInt16();
        reader.ReadUInt16(); // padding
        var argument = reader.ReadUInt16();

        return new ZoneObject
        {
            Type = type,
            X = x,
            Y = y,
            Argument = argument
        };
    }

    private ZoneAuxData ParseIZAXFromReader(BinaryReader reader)
    {
        var length = reader.ReadUInt16();
        var dataLength = Math.Max(0, length - 6);
        var data = reader.ReadBytes(dataLength);
        return new ZoneAuxData { RawData = data };
    }

    private ZoneAux2Data ParseIZX2FromReader(BinaryReader reader)
    {
        var length = reader.ReadUInt16();
        var dataLength = Math.Max(0, length - 6);
        var data = reader.ReadBytes(dataLength);
        return new ZoneAux2Data { RawData = data };
    }

    private ZoneAux3Data ParseIZX3FromReader(BinaryReader reader)
    {
        var length = reader.ReadUInt16();
        var dataLength = Math.Max(0, length - 6);
        var data = reader.ReadBytes(dataLength);
        return new ZoneAux3Data { RawData = data };
    }

    private ZoneAux4Data ParseIZX4FromReader(BinaryReader reader)
    {
        // IZX4 has fixed 8-byte data
        var data = reader.ReadBytes(8);
        return new ZoneAux4Data { RawData = data };
    }

    private Data.Action ParseActionFromReader(BinaryReader reader, MemoryStream ms)
    {
        var action = new Data.Action();
        var length = reader.ReadUInt32();
        var endPos = ms.Position + length;

        // Skip IACT data for now
        if (endPos <= ms.Length)
            ms.Seek(endPos, SeekOrigin.Begin);
        else
            ms.Seek(0, SeekOrigin.End);

        return action;
    }


    private Condition ParseCondition()
    {
        var condition = new Condition();
        condition.Opcode = (ConditionOpcode)_reader.ReadUInt16();
        var argCount = _reader.ReadUInt16();
        var textLength = _reader.ReadUInt16();

        for (int i = 0; i < argCount; i++)
        {
            condition.Arguments.Add(_reader.ReadInt16());
        }

        if (textLength > 0)
        {
            var textBytes = _reader.ReadBytes(textLength);
            condition.Text = System.Text.Encoding.ASCII.GetString(textBytes).TrimEnd('\0');
        }

        return condition;
    }

    private Instruction ParseInstruction()
    {
        var instruction = new Instruction();
        instruction.Opcode = (InstructionOpcode)_reader.ReadUInt16();
        var argCount = _reader.ReadUInt16();
        var textLength = _reader.ReadUInt16();

        for (int i = 0; i < argCount; i++)
        {
            instruction.Arguments.Add(_reader.ReadInt16());
        }

        if (textLength > 0)
        {
            var textBytes = _reader.ReadBytes(textLength);
            instruction.Text = System.Text.Encoding.ASCII.GetString(textBytes).TrimEnd('\0');
        }

        return instruction;
    }

    private void ParsePuzzlesSection(uint length)
    {
        var endPos = _reader.BaseStream.Position + length;
        int puzzleId = 0;

        while (_reader.BaseStream.Position < endPos - 4)
        {
            var puzzle = new Puzzle { Id = puzzleId++ };

            // Read puzzle header (variable format, read cautiously)
            var marker = _reader.ReadUInt32();
            if (marker == 0xFFFFFFFF || _reader.BaseStream.Position >= endPos)
                break;

            _reader.BaseStream.Seek(-4, SeekOrigin.Current);

            // Read unknown header bytes
            var unknown1 = _reader.ReadUInt16();
            puzzle.Item1 = _reader.ReadUInt16();
            puzzle.Item2 = _reader.ReadUInt16();
            var unknown2 = _reader.ReadUInt16();
            var unknown3 = _reader.ReadUInt16();

            // Read puzzle strings (5 strings typically)
            for (int i = 0; i < 5; i++)
            {
                var strLen = _reader.ReadUInt16();
                if (strLen > 0 && strLen < 1000) // Sanity check
                {
                    var strBytes = _reader.ReadBytes(strLen);
                    puzzle.Strings.Add(System.Text.Encoding.ASCII.GetString(strBytes).TrimEnd('\0'));
                }
                else if (strLen >= 1000)
                {
                    // Invalid length, likely parsing error
                    _reader.BaseStream.Seek(-2, SeekOrigin.Current);
                    break;
                }
            }

            _data.Puzzles.Add(puzzle);
        }

        // Ensure we're at the end
        _reader.BaseStream.Seek(endPos, SeekOrigin.Begin);
        Console.WriteLine($"Loaded {_data.Puzzles.Count} puzzles");
    }

    private void ParseCharactersSection(uint length)
    {
        var startPos = _reader.BaseStream.Position;
        var endPos = startPos + length;

        // Characters are stored in fixed 84-byte chunks
        // The number of characters is determined by section length, not a count field
        const int CharacterSize = 84;

        // Calculate number of characters from section length
        var charCount = (int)(length / CharacterSize);

        for (int i = 0; i < charCount; i++)
        {
            if (_reader.BaseStream.Position + CharacterSize > endPos)
                break;

            var charData = _reader.ReadBytes(CharacterSize);
            var character = ParseCharacterData(i, charData);
            _data.Characters.Add(character);
        }

        // Ensure we're at the end of the section
        _reader.BaseStream.Seek(endPos, SeekOrigin.Begin);
    }

    private Character ParseCharacterData(int index, byte[] data)
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

        // Character type at byte 4
        character.Type = (CharacterType)data[4];

        // Directional tile IDs at bytes 36-47 (6 directions x 2 bytes)
        var frames = new CharacterFrames();

        // The tile IDs for different directions
        var tileUp = BitConverter.ToUInt16(data, 36);
        var tileUpRight = BitConverter.ToUInt16(data, 38);
        var tileRight = BitConverter.ToUInt16(data, 40);
        var tileDownRight = BitConverter.ToUInt16(data, 42);
        var tileDown = BitConverter.ToUInt16(data, 44);
        var tileDownLeft = BitConverter.ToUInt16(data, 46);

        // Map to our 4-direction system
        frames.WalkUp = new ushort[] { tileUp, tileUp, tileUp };
        frames.WalkDown = new ushort[] { tileDown, tileDown, tileDown };
        frames.WalkLeft = new ushort[] { tileDownLeft, tileDownLeft, tileDownLeft };
        frames.WalkRight = new ushort[] { tileRight, tileRight, tileRight };

        character.Frames = frames;

        return character;
    }

    private void ParseCharacterWeaponsSection(uint length)
    {
        var endPos = _reader.BaseStream.Position + length;

        // Each entry is 4 bytes (2 for reference, 2 for health)
        var count = (int)(length / 4);

        for (int i = 0; i < count && _reader.BaseStream.Position + 4 <= endPos; i++)
        {
            var reference = _reader.ReadUInt16();
            var health = _reader.ReadUInt16();

            if (i < _data.Characters.Count)
            {
                _data.Characters[i].Weapon = new CharacterWeapon
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

        // Each entry is 2 bytes (damage)
        var count = (int)(length / 2);

        for (int i = 0; i < count && _reader.BaseStream.Position + 2 <= endPos; i++)
        {
            var damage = _reader.ReadUInt16();

            if (i < _data.Characters.Count)
            {
                _data.Characters[i].AuxData = new CharacterAux
                {
                    Damage = damage
                };
            }
        }

        _reader.BaseStream.Seek(endPos, SeekOrigin.Begin);
    }

    private void ParseTileNamesSection(uint length)
    {
        var startPos = _reader.BaseStream.Position;
        var endPos = startPos + length;

        while (_reader.BaseStream.Position + 4 <= endPos)
        {
            var tileId = _reader.ReadUInt16();
            var nameLength = _reader.ReadUInt16();

            if (nameLength > 0 && nameLength < 256 && _reader.BaseStream.Position + nameLength <= endPos)
            {
                var nameBytes = _reader.ReadBytes(nameLength);
                var name = System.Text.Encoding.ASCII.GetString(nameBytes).TrimEnd('\0');
                _data.TileNames[tileId] = name;
            }
            else if (nameLength >= 256)
            {
                // Invalid length, stop parsing
                break;
            }
        }

        _reader.BaseStream.Seek(endPos, SeekOrigin.Begin);
        Console.WriteLine($"Loaded {_data.TileNames.Count} tile names");
    }
}
