using YodaStoriesNG.Engine.Data;
using YodaStoriesNG.Engine.Parsing;
using YodaStoriesNG.Engine.Game;

namespace YodaStoriesNG.Engine.Dev;

/// <summary>
/// One runnable check for the part of this codebase that is easiest to get wrong and
/// hardest to notice when you do: the binary layout of an IACT script item.
///
/// A wrong layout still parses. It yields plausible opcodes and arguments that are merely
/// shifted, so the game runs, mostly behaves, and quietly does the wrong thing - which is
/// what happened for a long time. This builds a data file by hand with known values in
/// known slots and asserts they come back where they went in.
///
/// Run with:  dotnet run --project src/YodaStoriesNG.Engine -- --self-test
/// </summary>
public static class SelfTest
{
    public static int Run()
    {
        int failures = 0;
        foreach (var game in new[] { GameType.YodaStories, GameType.IndianaJones })
        {
            var name = game == GameType.YodaStories ? "Yoda" : "Indy";
            failures += Check($"IACT round-trip ({name})", () => IactRoundTrip(game));
            failures += Check($"zone round-trip ({name})", () => ZoneRoundTrip(game));
        }
        failures += Check("character tables round-trip (Yoda)", () => CharacterRoundTrip(GameType.YodaStories));
        failures += Check("character tables round-trip (Indy)", () => CharacterRoundTrip(GameType.IndianaJones));
        failures += Check("data file lookup ignores case", DataFileLookupIgnoresCase);
        failures += Check("menu bar keeps off user32 away from Windows", MenuBarStaysOffUser32);
        failures += Check("file filters convert for portable dialogs", FilterConvertsToExtensions);
        failures += Check("SDL menu bar works from the keyboard", MenuBarKeyboard);
        failures += Check("bot solves a boulder row over a plate", Bot.BlockPuzzle.SelfTest);
        failures += Check("paused actions keep their owner and entry context", PausedActionKeepsContext);
        failures += Check("hotspot toggles preserve map tiles", HotspotTogglesPreserveTiles);
        failures += Check("scripted travel requests a complete zone load", ScriptedTravelRequestsLoad);
        failures += Check("mission identity and script variables agree with their readers", MissionScriptState);
        failures += Check("smooth movement, reservations, arrivals and settings", GameEngine.TestSmoothMovement);
        failures += Check("authored weapon poses and blade extensions", WeaponPoses);
        failures += Check("unsolved moved areas reset while solved areas retain progress", GameEngine.TestAreaReset);

        Console.WriteLine(failures == 0 ? "\nSelf-test PASSED" : $"\nSelf-test FAILED ({failures})");
        return failures == 0 ? 0 : 1;
    }

    private static int Check(string name, System.Action body)
    {
        try
        {
            body();
            Console.WriteLine($"  ok   {name}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL {name}: {ex.Message}");
            return 1;
        }
    }

    private static void PausedActionKeepsContext()
    {
        var zone = new Zone { Id = 1, Width = 2, Height = 2, TileGrid = new ushort[2, 2, 3] };
        zone.Actions.Add(new Data.Action
        {
            Conditions = { new Condition { Opcode = ConditionOpcode.ZoneEntered } },
            Instructions =
            {
                new Instruction { Opcode = InstructionOpcode.HideHero },
                new Instruction { Opcode = InstructionOpcode.Wait },
                new Instruction { Opcode = InstructionOpcode.MoveHeroTo, Arguments = { 1, 1 } },
                new Instruction { Opcode = InstructionOpcode.Wait },
                new Instruction { Opcode = InstructionOpcode.ShowHero },
                new Instruction { Opcode = InstructionOpcode.DisableAction }
            }
        });
        zone.Actions.Add(new Data.Action
        {
            Conditions = { new Condition { Opcode = ConditionOpcode.EnterByPlane } },
            Instructions = { new Instruction { Opcode = InstructionOpcode.SetCounter, Arguments = { 7 } } }
        });
        var state = new GameState { CurrentZone = zone, CurrentZoneId = zone.Id };
        var executor = new ActionExecutor(new GameData(), state);
        executor.ExecuteZoneActions(ActionTrigger.ZoneEnter, enteredByPlane: true);
        Assert(executor.IsBusy && !state.HeroVisible, "first Wait did not suspend the landing");
        Assert(state.GetCounter(zone.Id) == 0, "a later action ran during a paused action");

        // LoadZone marks initialization complete while its landing continues on later ticks.
        state.SetVariable(zone.Id + 1000, 1);
        executor.ExecuteZoneActions(ActionTrigger.Walk);
        Assert(executor.IsBusy && !state.HeroVisible && state.PlayerX == 1 && state.PlayerY == 1,
            "the resumed action skipped its second Wait");
        Assert(state.GetCounter(zone.Id) == 0, "the entry scan resumed before the animation completed");
        executor.ExecuteZoneActions(ActionTrigger.Walk);
        Assert(!executor.IsBusy && state.HeroVisible, "landing never restored the hero");
        Assert(state.DisabledActions.Contains((zone.Id, 0)) && !state.DisabledActions.Contains((zone.Id, 1)),
            "DisableAction disabled the wrong action after Wait");
        Assert(state.GetCounter(zone.Id) == 7, "plane-arrival context was lost after Wait");

        executor.ExecuteZoneActions(ActionTrigger.ZoneEnter, enteredByPlane: true);
        Assert(!executor.IsBusy, "a disabled animation started again");
    }

    private static void MissionScriptState()
    {
        var goal = new Puzzle { Item1 = 810, Type = PuzzleType.Mission };
        var mission = new Mission { GoalPuzzle = goal };
        mission.PuzzleChain.Add(new PuzzleStep { Puzzle = goal, RequiredItemId = 809 });
        var zone = new Zone { Id = 1, Width = 2, Height = 2, TileGrid = new ushort[2, 2, 3] };
        zone.Objects.Add(new ZoneObject { Type = ZoneObjectType.XWingFromDagobah, Enabled = false });
        zone.Actions.Add(new Data.Action
        {
            Conditions =
            {
                new Condition { Opcode = ConditionOpcode.ZoneEntered },
                new Condition { Opcode = ConditionOpcode.EndingIs, Arguments = { 810 } }
            },
            Instructions =
            {
                new Instruction { Opcode = InstructionOpcode.EnableHotspot, Arguments = { 0 } },
                new Instruction { Opcode = InstructionOpcode.SetVariable, Arguments = { 1, 1, 0, 7 } }
            }
        });
        zone.Actions.Add(new Data.Action
        {
            Conditions = { new Condition { Opcode = ConditionOpcode.IsVariable, Arguments = { 7, 1, 1, 0 } } },
            Instructions = { new Instruction { Opcode = InstructionOpcode.SetCounter, Arguments = { 7 } } }
        });
        zone.Actions.Add(new Data.Action
        {
            Conditions = { new Condition { Opcode = ConditionOpcode.HasGoalItem } },
            Instructions = { new Instruction { Opcode = InstructionOpcode.SetCounter, Arguments = { 8 } } }
        });
        var state = new GameState { CurrentZone = zone, CurrentZoneId = 1 };
        var executor = new ActionExecutor(new GameData(), state) { CurrentMission = mission };
        executor.ExecuteZoneActions(ActionTrigger.ZoneEnter);
        Assert(zone.Objects[0].Enabled, "mission hotspot was selected by a prerequisite instead of the mission item");
        executor.ExecuteZoneActions(ActionTrigger.Walk);
        Assert(state.GetCounter(1) == 7, "IsVariable did not see the value written by SetVariable");
        state.AddItem(809);
        executor.ExecuteZoneActions(ActionTrigger.Walk);
        Assert(state.GetCounter(1) == 7, "a prerequisite was treated as the goal item");
        state.AddItem(810);
        executor.ExecuteZoneActions(ActionTrigger.Walk);
        Assert(state.GetCounter(1) == 8, "the mission item was not recognized outside a puzzle sector");
    }

    private static void HotspotTogglesPreserveTiles()
    {
        var zone = new Zone { Id = 1, Width = 1, Height = 1, TileGrid = new ushort[1, 1, 3] };
        zone.SetTile(0, 0, 1, 953);
        zone.Objects.Add(new ZoneObject { Type = ZoneObjectType.XWingToDagobah, Argument = 93 });
        zone.Actions.Add(new Data.Action
        {
            Instructions = { new Instruction { Opcode = InstructionOpcode.DisableHotspot, Arguments = { 0 } } }
        });
        var state = new GameState { CurrentZone = zone, CurrentZoneId = zone.Id };
        var executor = new ActionExecutor(new GameData(), state);
        executor.ExecuteZoneActions(ActionTrigger.Walk);
        Assert(!zone.Objects[0].Enabled && zone.GetTile(0, 0, 1) == 953, "disabling hotspot removed the ship");
        zone.Actions[0].Instructions[0].Opcode = InstructionOpcode.EnableHotspot;
        executor.ExecuteZoneActions(ActionTrigger.Walk);
        Assert(zone.Objects[0].Enabled && zone.GetTile(0, 0, 1) == 953, "enabling hotspot drew a zone ID as a tile");
    }

    private static void ScriptedTravelRequestsLoad()
    {
        var zone = new Zone { Id = 1 };
        zone.Actions.Add(new Data.Action
        {
            Instructions =
            {
                new Instruction { Opcode = InstructionOpcode.ChangeZone, Arguments = { 2, 3, 4 } },
                new Instruction { Opcode = InstructionOpcode.HideHero }
            }
        });
        var state = new GameState { CurrentZone = zone, CurrentZoneId = 1 };
        var executor = new ActionExecutor(new GameData(), state);
        executor.ExecuteZoneActions(ActionTrigger.Walk);
        Assert(executor.RequestedZoneChange == (2, 3, 4), "zone change was not requested");
        Assert(state.CurrentZoneId == 1 && ReferenceEquals(state.CurrentZone, zone) && state.HeroVisible,
            "travel left the zone ID and loaded map out of sync, or continued source instructions");
        executor.ResetForZone();
        Assert(!executor.RequestedZoneChange.HasValue && !executor.IsBusy, "zone entry retained an old script");
    }

    private static void WeaponPoses()
    {
        var data = new GameData { GameType = GameType.YodaStories };
        for (int i = 0; i < 32; i++) data.Tiles.Add(new Tile { Id = i });
        var frames = new CharacterFrames
        {
            WalkUp = new ushort[] { 20, 1, 2 }, WalkDown = new ushort[] { 20, 1, 2 },
            WalkLeft = new ushort[] { 20, 1, 2 }, WalkRight = new ushort[] { 20, 1, 2 },
            ExtensionUp = new ushort[] { 0, 3, 4 }, ExtensionDown = new ushort[] { 0, 5, 6 },
            ExtensionLeft = new ushort[] { 0, 7, 8 }, ExtensionRight = new ushort[] { 510, 9, 10 }
        };
        data.Characters.Add(new Character { Frames = frames, Weapon = new CharacterWeapon() });
        data.Characters.Add(new Character { Frames = new CharacterFrames { ExtensionRight = new ushort[] { 511 } }, Weapon = new CharacterWeapon() });
        data.Characters.Add(new Character { Frames = new CharacterFrames { ExtensionRight = new ushort[] { 18 } }, Weapon = new CharacterWeapon() });
        Assert(Rendering.WeaponAnimation.FindCharacter(data, 18) == 2 &&
            Rendering.WeaponAnimation.FindCharacter(data, 510) == 0, "blue and green sabers must use their own authored characters");
        Assert(Rendering.WeaponAnimation.FindCharacter(data, 511) == 1, "The Force used the lightsaber character");
        foreach (var (direction, dx, dy, early, late) in new[]
        {
            (Direction.Up, 0, -1, 3, 4), (Direction.Down, 0, 1, 5, 6),
            (Direction.Left, -1, 0, 7, 8), (Direction.Right, 1, 0, 9, 10)
        })
        {
            var first = Rendering.WeaponAnimation.Resolve(data, 0, direction, 0);
            var second = Rendering.WeaponAnimation.Resolve(data, 0, direction, 1);
            Assert(first?.Body == 1 && first?.Extension == early && first?.OffsetX == dx && first?.OffsetY == dy,
                $"incorrect first pose or blade placement for {direction}");
            Assert(second?.Body == 2 && second?.Extension == late, $"incorrect second pose for {direction}");
        }
        frames.WalkUp[1] = ushort.MaxValue;
        Assert(Rendering.WeaponAnimation.Resolve(data, 0, Direction.Up, 0)?.Body == 2,
            "an unused pose did not fall back to the other authored attack pose");
        frames.WalkUp[2] = ushort.MaxValue;
        Assert(Rendering.WeaponAnimation.Resolve(data, 0, Direction.Up, 0) == null,
            "a projectile frame was substituted for a missing attack pose");
    }

    private static void IactRoundTrip(GameType game)
    {
        var data = Parse(BuildFile(game), game);
        var zone = Single(data.Zones, "zone");
        var action = Single(zone.Actions, "action");

        var condition = Single(action.Conditions, "condition");
        Assert(condition.Opcode == ConditionOpcode.TileAtIs, $"condition opcode was {condition.Opcode}");
        AssertArgs(condition.Arguments, new short[] { 511, 3, 4, 1, 0 }, "condition");
        Assert(condition.Text == null, $"condition text was {condition.Text ?? "null"}");

        var instruction = Single(action.Instructions, "instruction");
        Assert(instruction.Opcode == InstructionOpcode.SpeakNpc, $"instruction opcode was {instruction.Opcode}");
        // Five slots survive even though SpeakNpc only uses the first two. If the parser
        // ever goes back to reading a count from slot 0, this is what catches it.
        AssertArgs(instruction.Arguments, new short[] { 7, 9, 0, 0, 0 }, "instruction");
        Assert(instruction.Text == "May the Force be with you.",
            $"instruction text was \"{instruction.Text}\"");
    }

    /// <summary>
    /// Everything world generation reads from a zone: its type, tiles, hotspots, monsters
    /// and item lists. Two monsters, because a wrong monster size only shows from the
    /// second one on - which is how every Yoda zone with company used to parse.
    /// </summary>
    private static void ZoneRoundTrip(GameType game)
    {
        bool yoda = game == GameType.YodaStories;
        var zone = Single(Parse(BuildFile(game), game).Zones, "zone");

        Assert(zone.Width == 1 && zone.Height == 1, $"size was {zone.Width}x{zone.Height}");
        Assert(zone.Type == ZoneType.Town, $"type was {zone.Type}");
        Assert(zone.Planet == (yoda ? Planet.Swamp : Planet.None), $"planet was {zone.Planet}");
        Assert(zone.Name == (yoda ? null : "Cairo"), $"name was {zone.Name ?? "null"}");
        Assert(zone.GetTile(0, 0, 0) == 100, $"floor tile was {zone.GetTile(0, 0, 0)}");
        Assert(zone.GetTile(0, 0, 1) == 200, $"object tile was {zone.GetTile(0, 0, 1)}");
        Assert(zone.GetTile(0, 0, 2) == 300, $"roof tile was {zone.GetTile(0, 0, 2)}");

        var hotspot = Single(zone.Objects, "hotspot");
        Assert(hotspot.Type == ZoneObjectType.PuzzleNPC && hotspot.Argument == 77,
            $"hotspot was {hotspot.Type} {hotspot.Argument}");

        Assert(zone.Monsters.Count == 2, $"expected 2 monsters, got {zone.Monsters.Count}");
        var second = zone.Monsters[1];
        Assert(second.CharacterId == 6 && second.X == 5 && second.Y == 3,
            $"second monster was character {second.CharacterId} at ({second.X},{second.Y})");
        if (yoda)
            Assert(second.Loot == 484 && second.DropsLoot, $"second monster loot was {second.Loot}");

        AssertSequence(zone.RequiredItems.Select(x => x.ToString()).ToList(), new[] { "42" });
        AssertSequence(zone.ProvidedItems.Select(x => x.ToString()).ToList(), new[] { "43" });
        AssertSequence(zone.GoalItems.Select(x => x.ToString()).ToList(), yoda ? new[] { "44" } : Array.Empty<string>());
    }

    /// <summary>
    /// The discs ship YODESK.DTA in upper case; a case-sensitive filesystem does not care
    /// that we asked for "yodesk.dta". Uses a name that no filesystem folds by itself.
    /// </summary>
    private static void DataFileLookupIgnoresCase()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ysng-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllBytes(Path.Combine(directory, "YODESK.DTA"), Array.Empty<byte>());

            var found = Data.GameDataFile.Find(directory, "yodesk.dta");
            Assert(found != null, "did not find YODESK.DTA when asked for yodesk.dta");
            Assert(Data.GameDataFile.Find(directory, "desktop.daw") == null,
                "found a file that is not there");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Issue #1: the native menu bar P/Invoked user32.dll unconditionally, so starting the
    /// game on Linux or macOS died with "Unable to load shared library 'user32.dll'". The
    /// guard is a plain runtime check with nothing to stop it being deleted again, so call
    /// the thing that used to throw. Passing a null window is safe: the guard returns before
    /// SDL is touched, and on Windows there is no guard to test.
    /// </summary>
    private static unsafe void MenuBarStaysOffUser32()
    {
        if (OperatingSystem.IsWindows())
            return;

        new UI.NativeMenuBar().Initialize(null);
    }

    /// <summary>
    /// A Windows menu is reachable with F10 and the arrows for free; the SDL one has to do
    /// it by hand. Up from the top wraps to Exit, Up again has to step over the divider to
    /// Load Game, and Enter has to run it and close the menu.
    /// </summary>
    private static unsafe void MenuBarKeyboard()
    {
        var menu = new UI.MenuBar(new Rendering.BitmapFont());
        bool loaded = false;
        menu.OnLoadGame += () => loaded = true;

        foreach (var key in new[] { Hexa.NET.SDL2.SDLKeyCode.F10, Hexa.NET.SDL2.SDLKeyCode.Up,
                                    Hexa.NET.SDL2.SDLKeyCode.Up, Hexa.NET.SDL2.SDLKeyCode.Return })
        {
            var evt = new Hexa.NET.SDL2.SDLEvent { Type = (uint)Hexa.NET.SDL2.SDLEventType.Keydown };
            evt.Key.Keysym.Sym = (int)key;
            Assert(menu.HandleEvent(&evt), $"{key} was not consumed");
        }

        Assert(loaded, "Enter did not run Load Game");
        Assert(!menu.IsMenuOpen, "menu stayed open after Enter");
        menu.OnToggleSmoothMovement += () => menu.SmoothMovement = !menu.SmoothMovement;
        foreach (var key in new[] { Hexa.NET.SDL2.SDLKeyCode.F10,
            Hexa.NET.SDL2.SDLKeyCode.Right, Hexa.NET.SDL2.SDLKeyCode.Right,
            Hexa.NET.SDL2.SDLKeyCode.Down, Hexa.NET.SDL2.SDLKeyCode.Down,
            Hexa.NET.SDL2.SDLKeyCode.Down, Hexa.NET.SDL2.SDLKeyCode.Return })
        {
            var evt = new Hexa.NET.SDL2.SDLEvent { Type = (uint)Hexa.NET.SDL2.SDLEventType.Keydown };
            evt.Key.Keysym.Sym = (int)key;
            Assert(menu.HandleEvent(&evt), $"{key} was not consumed in Config");
        }
        Assert(menu.SmoothMovement, "Config did not toggle Smooth Movement");
    }

    /// <summary>
    /// zenity, kdialog and osascript all want a plain extension list, and the filter they
    /// have to get it from is a Win32 one whose display halves repeat every pattern in
    /// brackets - so the easy mistake is to take "dta" twice and "*" as an extension.
    /// </summary>
    private static void FilterConvertsToExtensions()
    {
        var extensions = UI.FileDialogHelper.ExtensionsIn(
            "Desktop Adventures Data (*.dta;*.daw)|*.dta;*.daw|Yoda Stories (*.dta)|*.dta|All Files (*.*)|*.*");

        AssertSequence(extensions, new[] { "dta", "daw" });
        AssertSequence(UI.FileDialogHelper.ExtensionsIn("All Files (*.*)|*.*"), Array.Empty<string>());
    }

    private static void AssertSequence(List<string> actual, string[] expected)
    {
        Assert(actual.Count == expected.Length,
            $"got [{string.Join(", ", actual)}], expected [{string.Join(", ", expected)}]");
        for (int i = 0; i < expected.Length; i++)
            Assert(actual[i] == expected[i], $"item {i} was \"{actual[i]}\", expected \"{expected[i]}\"");
    }

    /// <summary>
    /// CHWP and CAUX are lists keyed by character index, not arrays in character order, and
    /// CHAR records are a different size in each game. Reading any of them as fixed-size
    /// still "works" - it just hands every NPC someone else's health, damage and weapon,
    /// which is what happened. Only character 1 gets a CHWP/CAUX entry here, so a parser
    /// that reads them positionally puts the values on character 0 and fails.
    /// </summary>
    private static void CharacterRoundTrip(GameType game)
    {
        var data = new DtaParser().Parse(new MemoryStream(BuildCharacterFile(game)), game);

        Assert(data.Characters.Count == 2, $"expected 2 characters, got {data.Characters.Count}");
        var hero = data.Characters[0];
        var gun = data.Characters[1];

        Assert(gun.Name == "Blaster", $"second name was \"{gun.Name}\"");
        Assert(hero.Weapon == null && hero.AuxData == null, "character 0 picked up character 1's CHWP/CAUX");
        Assert(gun.Weapon is { Reference: 7, Health: 1 },
            $"weapon was ref {gun.Weapon?.Reference}, health {gun.Weapon?.Health}");
        Assert(gun.AuxData?.Damage == 4, $"damage was {gun.AuxData?.Damage}");
        Assert(gun.Frames.ExtensionRight.FirstOrDefault() == 512,
            $"weapon tile was {gun.Frames.ExtensionRight.FirstOrDefault()}");
        Assert(hero.Frames.WalkUp.SequenceEqual(new ushort[] { 100, 108, 116 }),
            "up animation did not read all three frames from slot 0");
        Assert(hero.Frames.WalkDown.SequenceEqual(new ushort[] { 101, 109, 117 }),
            "down animation did not read all three frames from slot 1");
        Assert(hero.Frames.WalkLeft.SequenceEqual(new ushort[] { 103, 111, 119 }),
            "left animation did not read all three frames from slot 3");
        Assert(hero.Frames.WalkRight.SequenceEqual(new ushort[] { 106, 114, 122 }),
            "right animation did not read all three frames from slot 6");
        Assert(hero.Frames.ExtensionUp.SequenceEqual(new ushort[] { 102, 110, 118 }), "up extension slots were not parsed");
        Assert(hero.Frames.ExtensionDown.SequenceEqual(new ushort[] { 104, 112, 120 }), "down extension slots were not parsed");
        Assert(hero.Frames.ExtensionLeft.SequenceEqual(new ushort[] { 105, 113, 121 }), "left extension slots were not parsed");
        Assert(hero.Frames.ExtensionRight.SequenceEqual(new ushort[] { 107, 115, 123 }), "right extension slots were not parsed");
    }

    private static byte[] BuildCharacterFile(GameType game)
    {
        var stream = new MemoryStream();
        var w = new BinaryWriter(stream);

        Tag(w, "VERS");
        w.Write(new byte[] { 0x00, 0x02, 0x00, 0x00 });

        Tag(w, "CHAR");
        var charLengthAt = Placeholder(w);
        var charStart = stream.Position;
        foreach (var (index, name, weaponTile) in new[] { (0, "Hero", 0), (1, "Blaster", 512) })
        {
            w.Write((short)index);
            Tag(w, "ICHA");
            var recordAt = Placeholder(w);
            var recordStart = stream.Position;
            var nameBytes = new byte[16];
            System.Text.Encoding.ASCII.GetBytes(name).CopyTo(nameBytes, 0);
            w.Write(nameBytes);
            w.Write((short)(index == 0 ? 1 : 4));  // type: hero, weapon
            w.Write((short)0);                     // movement
            if (game == GameType.YodaStories)
                w.Write(new byte[6]);              // the two fields Indiana Jones does not have
            for (int slot = 0; slot < 24; slot++)
                w.Write((short)(index == 1 && slot == 7 ? weaponTile : 100 + index * 100 + slot));
            Backfill(w, recordAt, stream.Position - recordStart);
        }
        w.Write((short)-1);
        Backfill(w, charLengthAt, stream.Position - charStart);

        Tag(w, "CHWP");
        var wpLengthAt = Placeholder(w);
        var wpStart = stream.Position;
        w.Write((short)1); w.Write((short)7); w.Write((ushort)1);  // character 1: sound 7, health 1
        w.Write((short)-1);
        Backfill(w, wpLengthAt, stream.Position - wpStart);

        Tag(w, "CAUX");
        var auxLengthAt = Placeholder(w);
        var auxStart = stream.Position;
        w.Write((short)1); w.Write((short)4);                      // character 1: damage 4
        w.Write((short)-1);
        Backfill(w, auxLengthAt, stream.Position - auxStart);

        Tag(w, "ENDF");
        w.Write((uint)0);
        return stream.ToArray();
    }

    /// <summary>
    /// One 1x1 zone laid out the way each game really stores it (docs/DATA-FORMAT.md):
    /// Yoda Stories as a single self-contained ZONE record, Indiana Jones as tiles in ZONE
    /// plus one entry in each of ZAUX, ZAX2-4, HTSP, ACTN and ZNAM.
    /// </summary>
    private static byte[] BuildFile(GameType game)
    {
        bool yoda = game == GameType.YodaStories;
        var stream = new MemoryStream();
        var w = new BinaryWriter(stream);

        Tag(w, "VERS");
        w.Write(new byte[] { 0x00, 0x02, 0x00, 0x00 });   // 2.0, big-endian halves

        Tag(w, "ZONE");
        if (yoda)
        {
            w.Write((ushort)1);                             // zone count
            w.Write((ushort)Planet.Swamp);
            var sizeAt = Placeholder(w);
            var start = stream.Position;
            w.Write((ushort)0);                             // zone index
            Izon(w, yoda);
            Hotspots(w);
            Izax(w, yoda);
            Ids(w, "IZX2", 43);
            Ids(w, "IZX3");
            Izx4(w);
            w.Write((ushort)1);
            Iact(w, yoda);
            Backfill(w, sizeAt, stream.Position - start);
        }
        else
        {
            Section(w, "ZONE", () => { w.Write((ushort)1); Izon(w, yoda); }, tagWritten: true);
            Section(w, "ZAUX", () => Izax(w, yoda));
            Section(w, "ZAX2", () => Ids(w, "IZX2", 43));
            Section(w, "ZAX3", () => Ids(w, "IZX3"));
            Section(w, "ZAX4", () => Izx4(w));
            Section(w, "HTSP", () => { w.Write((short)0); Hotspots(w); w.Write((short)-1); });
            Section(w, "ACTN", () => { w.Write((short)0); w.Write((ushort)1); Iact(w, yoda); w.Write((short)-1); });
            Section(w, "ZNAM", () =>
            {
                w.Write((short)0);
                var name = new byte[16];
                System.Text.Encoding.ASCII.GetBytes("Cairo").CopyTo(name, 0);
                w.Write(name);
                w.Write((short)-1);
            });
        }

        Tag(w, "ENDF");
        return stream.ToArray();
    }

    private static void Section(BinaryWriter w, string tag, System.Action body, bool tagWritten = false)
    {
        if (!tagWritten) Tag(w, tag);
        var lengthAt = Placeholder(w);
        var start = w.BaseStream.Position;
        body();
        Backfill(w, lengthAt, w.BaseStream.Position - start);
    }

    private static void Izon(BinaryWriter w, bool yoda)
    {
        Tag(w, "IZON");
        w.Write((uint)0);                // record size, unused by the parser
        w.Write((ushort)1);              // width
        w.Write((ushort)1);              // height
        w.Write((uint)ZoneType.Town);
        if (yoda)
        {
            w.Write((ushort)0);          // sector counter
            w.Write((ushort)Planet.Swamp);
        }
        w.Write((ushort)100);            // grid 0,0 floor
        w.Write((ushort)200);            // grid 0,0 object
        w.Write((ushort)300);            // grid 0,0 roof
    }

    private static void Hotspots(BinaryWriter w)
    {
        w.Write((ushort)1);
        w.Write((uint)ZoneObjectType.PuzzleNPC);
        w.Write((short)0);               // x
        w.Write((short)0);               // y
        w.Write((ushort)1);              // enabled
        w.Write((short)77);              // argument
    }

    private static void Izax(BinaryWriter w, bool yoda)
    {
        Tag(w, "IZAX");
        var sizeAt = Placeholder(w);
        var start = w.BaseStream.Position - 8;
        w.Write((ushort)0);              // unknown
        w.Write((ushort)2);
        foreach (var (character, x, y) in new[] { (5, 1, 2), (6, 5, 3) })
        {
            w.Write((ushort)character);
            w.Write((ushort)x);
            w.Write((ushort)y);
            if (yoda)
            {
                w.Write((short)(character == 6 ? 484 : -1));   // loot
                w.Write((uint)(character == 6 ? 1 : 0));       // drops loot
                w.Write(new byte[32]);                         // waypoints
            }
        }
        w.Write((ushort)1); w.Write((ushort)42);               // required items
        if (yoda) { w.Write((ushort)1); w.Write((ushort)44); } // goal items
        Backfill(w, sizeAt, w.BaseStream.Position - start);
    }

    private static void Ids(BinaryWriter w, string tag, params ushort[] ids)
    {
        Tag(w, tag);
        w.Write((uint)(10 + ids.Length * 2));
        w.Write((ushort)ids.Length);
        foreach (var id in ids) w.Write(id);
    }

    private static void Izx4(BinaryWriter w)
    {
        Tag(w, "IZX4");
        w.Write((uint)10);
        w.Write((ushort)0);
    }

    /// <summary>Indiana Jones writes 0 for every IACT size; only the counts frame it.</summary>
    private static void Iact(BinaryWriter w, bool yoda)
    {
        Tag(w, "IACT");
        var lengthAt = Placeholder(w);
        var start = w.BaseStream.Position;

        // Indiana Jones numbers its opcodes differently; the parser must hand back Yoda's.
        w.Write((ushort)1);          // one condition
        ActionItem(w, yoda ? (ushort)ConditionOpcode.TileAtIs : (ushort)0x13, new short[] { 511, 3, 4, 1, 0 }, null);
        w.Write((ushort)1);          // one instruction
        ActionItem(w, yoda ? (ushort)InstructionOpcode.SpeakNpc : (ushort)0x1c, new short[] { 7, 9, 0, 0, 0 },
            "May the Force be with you.");

        if (yoda)
            Backfill(w, lengthAt, w.BaseStream.Position - start);
    }

    private static void ActionItem(BinaryWriter w, ushort opcode, short[] arguments, string? text)
    {
        Assert(arguments.Length == 5, "an action item always has five argument slots");
        w.Write(opcode);
        foreach (var argument in arguments) w.Write(argument);

        var bytes = text == null ? Array.Empty<byte>() : System.Text.Encoding.Latin1.GetBytes(text);
        w.Write((ushort)bytes.Length);
        w.Write(bytes);
    }

    private static GameData Parse(byte[] bytes, GameType game) =>
        new DtaParser().Parse(new MemoryStream(bytes), game);

    private static void Tag(BinaryWriter w, string tag) =>
        w.Write(System.Text.Encoding.ASCII.GetBytes(tag));

    private static long Placeholder(BinaryWriter w)
    {
        var at = w.BaseStream.Position;
        w.Write((uint)0);
        return at;
    }

    private static void Backfill(BinaryWriter w, long at, long value)
    {
        var resume = w.BaseStream.Position;
        w.BaseStream.Seek(at, SeekOrigin.Begin);
        w.Write((uint)value);
        w.BaseStream.Seek(resume, SeekOrigin.Begin);
    }

    private static T Single<T>(List<T> items, string what)
    {
        Assert(items.Count == 1, $"expected exactly one {what}, got {items.Count}");
        return items[0];
    }

    private static void AssertArgs(List<short> actual, short[] expected, string what)
    {
        Assert(actual.Count == expected.Length,
            $"{what} had {actual.Count} arguments, expected {expected.Length}");
        for (int i = 0; i < expected.Length; i++)
            Assert(actual[i] == expected[i],
                $"{what} argument {i} was {actual[i]}, expected {expected[i]}");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
