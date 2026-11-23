using YodaStoriesNG.Engine.Data;
using YodaStoriesNG.Engine.Parsing;
using YodaStoriesNG.Engine.Game;
using YodaStoriesNG.Engine.Bot;

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
        failures += Check("paused actions keep their owner and entry context", PausedActionKeepsContext);
        failures += Check("hotspot toggles preserve map tiles", HotspotTogglesPreserveTiles);
        failures += Check("scripted travel requests a complete zone load", ScriptedTravelRequestsLoad);
        failures += Check("mission identity and script variables agree with their readers", MissionScriptState);
        failures += Check("smooth movement, reservations, arrivals and settings", GameEngine.TestSmoothMovement);
        failures += Check("authored weapon poses and blade extensions", WeaponPoses);
        failures += Check("linked scripted passage arrivals", ScriptedPassageArrivals);
        failures += Check("scripted item consumption across waits", ScriptedItemConsumption);
        failures += Check("interaction stances remain walkable", InteractionStanceRouting);
        failures += Check("unchanged portal cycles and state changes", PortalCycleRouting);
        failures += Check("script-connected mission room families", ScriptedRoomFamilies);
        failures += Check("native signal consumption order", SignalConsumptionOrder);
        failures += Check("authored random producer ranges", RandomProducerRanges);
        failures += Check("visited rooms without random rolls retain their guards", VisitedRoomGuards);
        failures += Check("one-shot signal consumers are real prerequisites", OneShotSignalPrerequisite);
        failures += Check("native actor picture predicates and guidance", ActorPictureGuidance);
        failures += Check("completed exchanges do not restart reusable tools", CompletedExchangeTools);
        failures += Check("wildcard placement uses the sector item", WildcardPlacedItemResolvesSectorRequiredItem);
        failures += Check("companion yielding resumes the original route", CompanionYielding);
        failures += Check("rejected door unlocks do not mask alternate entrances", AlternateDoorUnlocks);
        failures += Check("briefings precede finale routes", BriefingBeforeFinale);
        failures += Check("reward routes skip absent template props", RewardTemplateRouting);
        failures += Check("trade actors require their scripted handoff", TradeActorReward);
        failures += Check("transport boarding follows authored floor triggers", TransportFloorBoarding);
        failures += Check("multi-room flights retain their landing state", GameEngine.TestMultiRoomFlight);
        failures += Check("pressure plates prefer durable block placements", DurablePressurePlate);
        failures += Check("entry counter resets are evaluated as one phase", EntryCounterPhase);
        failures += Check("wandering script actors are awaited in place", WanderingScriptActor);
        failures += Check("deep authored prerequisite chains remain executable", DeepNativeChain);
        failures += Check("bump props are prepared before walking to their stance", BumpBeforeStance);
        failures += Check("hidden dialogue actors are not transport", HiddenDialogueIsNotTransport);
        failures += Check("shadowed bumps do not toggle their prerequisites", ShadowedBumpPrerequisite);
        failures += Check("object routes unlock gates before clearing covered stances", LockedObjectStance);
        failures += Check("item pictures do not bypass character handoffs", ItemPictureHandoff);
        failures += Check("guarded signal resets require their authored unlock", GuardedSignalReset);
        failures += Check("remote automatic producers require visiting their area", RemoteAutomaticProducer);
        failures += Check("companion passing bays preserve the original goal", CompanionPassingBay);
        failures += Check("scripted arrival matches spawn validation", ScriptedSpawnRouting);
        failures += Check("nested room entrances get a short access opening", SealedNestedRoomDoorway);
        failures += Check("world routes apply doorway access after real arrivals", RouteGraphOpensSealedNestedRoom);
        failures += Check("transparent solid doors require their authored bump", TransparentDoorEntry);
        failures += Check("native weapon targets and wildcard placement", GameEngine.TestScriptedWeaponUse);
        failures += Check("local puzzle replay, variants, isolation, egress and block solutions", GameEngine.TestLocalPuzzlePlans);

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
        var started=new List<(int Zone,int Action)>();
        executor.OnActionStarted+=(id,index)=>started.Add((id,index));
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
        Assert(started.SequenceEqual(new[] { (zone.Id,0),(zone.Id,1) }),
            "resuming a suspended script restarted its objective notification");

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
        Assert(hero.MovementType==11,"authored flee movement type was not parsed");
        var invulnerable=new NPC { Health=256,IsInvulnerable=true };
        Assert(!invulnerable.TakeDamage(1000) && invulnerable.Health==256 && invulnerable.IsAlive,
            "an invulnerable puzzle actor could be killed");
        Assert(hero.Weapon == null && hero.AuxData == null, "character 0 picked up character 1's CHWP/CAUX");
        Assert(gun.Weapon is { Reference: 7, Health: 1 },
            $"weapon was ref {gun.Weapon?.Reference}, health {gun.Weapon?.Health}");
        Assert(gun.AuxData?.Damage == 4, $"damage was {gun.AuxData?.Damage}");
        Assert(gun.Frames.ExtensionRight.FirstOrDefault() == 512,
            $"weapon tile was {gun.Frames.ExtensionRight.FirstOrDefault()}");
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
            w.Write((short)(index==0 ? 11 : 0));    // movement
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

    private static void ScriptedSpawnRouting()
    {
        var data=new GameData();
        data.Tiles.Add(new Tile { Id=0,Flags=TileFlags.Floor });
        data.Tiles.Add(new Tile { Id=1,Flags=TileFlags.Object });
        var source=new Zone { Id=0,Width=6,Height=6,TileGrid=new ushort[6,6,3] };
        var destination=new Zone { Id=1,Width=6,Height=6,TileGrid=new ushort[6,6,3] };
        data.Zones.AddRange(new[]{source,destination});
        for(int row=0;row<6;row++) for(int column=0;column<6;column++) destination.SetTile(column,row,1,1);
        destination.SetTile(2,2,1,0); destination.SetTile(4,4,1,0);
        Assert(ZoneArrival.ResolveSpawn(data,destination,3,3,false)==(2,2),"arrival did not preserve native square-search order");
        Assert(ZoneArrival.ResolveSpawn(data,destination,3,3,true)==(3,3),"flight arrival was moved off its authored marker");
        var action=new Data.Action { Conditions={new Condition { Opcode=ConditionOpcode.Bump,Arguments={0,0,0} }},
            Instructions={new Instruction { Opcode=InstructionOpcode.ChangeZone,Arguments={1,3,3} }} };
        source.Actions.Add(action);
        var state=new GameState { CurrentZoneId=0,CurrentZone=source,HeroVisible=true };
        var arrival=ScriptedZoneTravel.Instructions(source,state,data).Single();
        Assert((arrival.ArrivalX,arrival.ArrivalY)==(2,2),"route graph retained a blocked warp destination");
        action.Instructions.Insert(0,new Instruction { Opcode=InstructionOpcode.HideHero });
        arrival=ScriptedZoneTravel.Instructions(source,state,data).Single();
        Assert((arrival.ArrivalX,arrival.ArrivalY)==(3,3),"route graph displaced a hidden flight arrival");
        source.Objects.Add(new ZoneObject { Type=ZoneObjectType.DoorEntrance,X=3,Y=3,Argument=1,Enabled=true });
        destination.Objects.Add(new ZoneObject { Type=ZoneObjectType.DoorExit,X=3,Y=5,Argument=ushort.MaxValue,Enabled=true });
        destination.SetTile(3,4,1,0); destination.SetTile(4,5,1,0); destination.SetTile(3,5,1,0);
        var world=new WorldMap(); world.Sectors[0]=new Sector { Zone=0 }; world.RoomParents[1]=0;
        var graph=WorldActionGraph.Build(data,world,state,new Dictionary<int,int>());
        var door=graph.Edges.Single(e=>e.From==0 && e.To==1 && e.Instruction.Type==ObjectiveType.EnterDoor);
        var crossings=graph.Crossings(door,new HashSet<(int,int)> { (3,2) }).ToArray();
        Assert(crossings.Any(c=>c.To==(3,4)),"bottom doorway omitted its inward landing while considering alternate entry components");
    }

    private static void SealedNestedRoomDoorway()
    {
        var data=new GameData();
        data.Tiles.Add(new Tile { Id=0,Flags=TileFlags.Floor });
        data.Tiles.Add(new Tile { Id=1,Flags=TileFlags.Object });
        var source=new Zone { Id=0,Width=5,Height=5,TileGrid=new ushort[5,5,3] };
        var destination=new Zone { Id=1,Width=5,Height=5,TileGrid=new ushort[5,5,3] };
        data.Zones.AddRange(new[]{source,destination});
        source.Objects.Add(new ZoneObject { Type=ZoneObjectType.DoorEntrance,X=0,Y=0,Argument=1,Enabled=true });
        source.SetTile(0,0,1,1); source.SetTile(1,0,1,1); source.SetTile(0,1,1,1); source.SetTile(1,1,1,1);
        var state=new GameState { CurrentZoneId=0,CurrentZone=source,PlayerX=4,PlayerY=4 };
        var world=new WorldMap(); world.RoomParents[1]=0;
        int cleared=DoorwayAccess.OpenSealedRoomEntrances(data,state,world,source);
        bool openedSide=source.GetTile(1,0,1)==ushort.MaxValue || source.GetTile(0,1,1)==ushort.MaxValue;
        Assert(cleared==1 && openedSide,
            "sealed doorway did not clear the single shortest wall tile from the entry area");
        Assert(source.GetTile(0,0,1)==1 &&
            (source.GetTile(1,0,1)==ushort.MaxValue) != (source.GetTile(0,1,1)==ushort.MaxValue),
            "doorway repair changed the authored door tile or an unrelated wall");

        var divided=new Zone { Id=2,Width=7,Height=7,TileGrid=new ushort[7,7,3] };
        divided.Objects.Add(new ZoneObject { Type=ZoneObjectType.DoorEntrance,X=3,Y=1,Argument=3,Enabled=true });
        for(int x=0;x<7;x++) divided.SetTile(x,3,1,1);
        divided.SetTile(3,1,1,0); divided.SetTile(3,2,1,0);
        var child=new Zone { Id=3,Width=7,Height=7,TileGrid=new ushort[7,7,3] };
        data.Zones.AddRange(new[]{divided,child});world.RoomParents[3]=2;
        var dividedState=new GameState { CurrentZoneId=2,CurrentZone=divided,PlayerX=3,PlayerY=6 };
        int opened=DoorwayAccess.OpenSealedRoomEntrances(data,dividedState,world,divided);
        Assert(opened==1 && divided.GetTile(3,3,1)==ushort.MaxValue,
            "doorway repair ignored a walkable but disconnected approach and did not open the intervening wall");
    }

    private static void RouteGraphOpensSealedNestedRoom()
    {
        var data=new GameData();
        data.Tiles.Add(new Tile { Id=0,Flags=TileFlags.Floor });
        data.Tiles.Add(new Tile { Id=1,Flags=TileFlags.Object });
        Zone Make(int id)=>new() { Id=id,Width=7,Height=7,TileGrid=new ushort[7,7,3] };
        var exterior=Make(0);var child=Make(1);var start=Make(2);
        data.Zones.AddRange(new[]{exterior,child,start});
        for(int x=0;x<7;x++) exterior.SetTile(x,3,1,1);
        exterior.Objects.Add(new ZoneObject { Type=ZoneObjectType.DoorEntrance,X=3,Y=1,Argument=1,Enabled=true });
        exterior.Objects.Add(new ZoneObject { Type=ZoneObjectType.DoorExit,X=3,Y=5,Argument=2,Enabled=true });
        child.Objects.Add(new ZoneObject { Type=ZoneObjectType.DoorExit,X=3,Y=5,Argument=ushort.MaxValue,Enabled=true });
        start.Objects.Add(new ZoneObject { Type=ZoneObjectType.DoorEntrance,X=3,Y=2,Argument=0,Enabled=true });
        var world=new WorldMap();world.RoomParents[1]=0;
        world.Sectors[0]=new Sector { Zone=0 };world.Sectors[1]=new Sector { Zone=1 };world.Sectors[2]=new Sector { Zone=2 };
        var state=new GameState { CurrentZoneId=2,CurrentZone=start,PlayerX=3,PlayerY=3 };
        var graph=WorldActionGraph.Build(data,world,state,new Dictionary<int,int>());
        var route=graph.Route(2,1,_=>true,new[]{(3,4)});
        Assert(route.Count==2 && route[0].From==2 && route[0].To==0 && route[1].From==0 && route[1].To==1,
            "offline world routing did not apply the same arrival-based wall opening as zone loading");
        Assert(exterior.GetTile(3,3,1)==1,"route simulation mutated the authored zone instead of its private view");
    }

    private static void TransparentDoorEntry()
    {
        var data=new GameData();
        data.Tiles.Add(new Tile { Id=0,Flags=TileFlags.Floor });
        data.Tiles.Add(new Tile { Id=1,Flags=TileFlags.Object|TileFlags.Transparency });
        var zone=new Zone { Id=0,Width=4,Height=4,TileGrid=new ushort[4,4,3] };
        zone.SetTile(2,1,1,1);
        zone.Objects.Add(new ZoneObject { Type=ZoneObjectType.DoorEntrance,X=2,Y=1,Argument=1,Enabled=true });
        data.Zones.Add(zone);data.Zones.Add(new Zone { Id=1,Width=4,Height=4,TileGrid=new ushort[4,4,3] });
        var state=new GameState { CurrentZoneId=0,CurrentZone=zone,PlayerX=1,PlayerY=1 };
        var actions=new BotActions(state,data,new Pathfinder(data,state));
        bool bumped=false;
        actions.OnAction+=(kind,x,y,direction)=>
        {
            if(kind==BotActionType.Bump) { bumped=true; zone.SetTile(x,y,1,ushort.MaxValue); }
        };
        Assert(actions.EnterDoor(2,1) && bumped && state.PlayerX==1 && state.PlayerY==1,
            "transparent solid doorway was entered without its opening interaction");
        Assert(actions.EnterDoor(2,1) && actions.CurrentActionDescription=="Moving to (2,1)",
            "opened doorway could not be entered");
    }

    private static void RandomProducerRanges()
    {
        var roll=new Instruction { Opcode=InstructionOpcode.RollDice,Arguments={4} };
        Assert(MissionSolver.CanProduceRandom(roll,n=>n==1) && MissionSolver.CanProduceRandom(roll,n=>n==4),
            "authored dice outcomes were rejected as immutable random branches");
        Assert(!MissionSolver.CanProduceRandom(roll,n=>n==0 || n==5),"dice producer admitted an impossible outcome");
        var fixedValue=new Instruction { Opcode=InstructionOpcode.SetRandom,Arguments={9} };
        Assert(MissionSolver.CanProduceRandom(fixedValue,n=>n==9) &&
            !MissionSolver.CanProduceRandom(fixedValue,n=>n==1),"fixed random state became a dice roll");
        Assert(!MissionSolver.CanProduceRandom(new Instruction { Opcode=InstructionOpcode.SetCounter,Arguments={4} },n=>true),
            "unrelated instructions became random producers");
    }

    private static void VisitedRoomGuards()
    {
        var data=new GameData(); data.Zones.Add(new Zone { Id=0 }); data.Zones.Add(new Zone { Id=1 });
        var state=new GameState { CurrentZoneId=0 }; state.VisitedZones.Add(1); state.SetSectorCounter(1,11);
        var solver=new MissionSolver(state,data,new WorldGenerator(data));
        var branch=new Data.Action { Conditions={new Condition { Opcode=ConditionOpcode.SectorCounterIs,Arguments={122} }} };
        Assert(!solver.StateAllows(1,branch),"visited room without a dice roll accepted a mismatched sector counter");
        branch.Conditions[0].Arguments[0]=11;
        Assert(solver.StateAllows(1,branch),"visited room rejected its actual sector counter");
    }

    private static void OneShotSignalPrerequisite()
    {
        var data=new GameData(); data.Tiles.Add(new Tile { Id=0,Flags=TileFlags.Floor });
        data.Tiles.Add(new Tile { Id=1,Flags=TileFlags.Object });
        var zone=new Zone { Id=0,Width=5,Height=5,TileGrid=new ushort[5,5,3] };
        for(int y=0;y<5;y++) for(int x=0;x<5;x++) zone.SetTile(x,y,1,ushort.MaxValue);
        zone.SetTile(2,0,1,1); data.Zones.Add(zone);
        zone.Actions.Add(new Data.Action { Conditions={new Condition { Opcode=ConditionOpcode.Bump,Arguments={2,0,1} }},
            Instructions={new Instruction { Opcode=InstructionOpcode.SetRandom,Arguments={7} }} });
        zone.Actions.Add(new Data.Action { Conditions={new Condition { Opcode=ConditionOpcode.RandomIs,Arguments={7} }},
            Instructions={new Instruction { Opcode=InstructionOpcode.SetRandom,Arguments={0} },
                new Instruction { Opcode=InstructionOpcode.DisableAction }} });
        zone.Actions.Add(new Data.Action { Conditions={new Condition { Opcode=ConditionOpcode.RandomIs,Arguments={7} }},
            Instructions={new Instruction { Opcode=InstructionOpcode.WinGame }} });
        var state=new GameState { CurrentZone=zone,CurrentZoneId=0,PlayerX=2,PlayerY=1 };
        state.VisitedZones.Add(0);
        var world=new WorldMap(); var sector=new Sector { Zone=0,Type=ZoneType.Goal }; world.Sectors[0]=sector;
        var generator=new WorldGenerator(data); generator.SetCurrentWorld(world);
        var solver=new MissionSolver(state,data,generator);
        var executor=new ActionExecutor(data,state) { BumpX=2,BumpY=0 };
        executor.OnActionStarted+=solver.NoteScriptActionStarted;
        Assert(solver.NativeGoalPrerequisite(world,sector,authoredAction:(0,2))?.Type==ObjectiveType.BumpTarget,
            "planner rejected the one-shot introduction instead of preparing it");
        executor.ExecuteZoneActions(ActionTrigger.Bump);
        Assert(!state.IsGameWon && state.DisabledActions.Contains((0,1)),"first activation skipped the one-shot prerequisite");
        Assert(solver.NativeGoalPrerequisite(world,sector,authoredAction:(0,2))?.Type==ObjectiveType.BumpTarget,
            "planner did not retry the switch after the introduction finished");
        executor.ExecuteZoneActions(ActionTrigger.Bump);
        Assert(state.IsGameWon,"second activation failed to reach the authored consumer");
    }

    private static void ActorPictureGuidance()
    {
        var data=new GameData(); data.Tiles.Add(new Tile { Id=0,Flags=TileFlags.Floor });
        data.Tiles.Add(new Tile { Id=1,Flags=TileFlags.Object });
        data.Tiles.Add(new Tile { Id=2,Flags=TileFlags.CharEnemy });
        data.Characters.Add(new Character { Frames=new CharacterFrames {
            WalkRight=[2],WalkLeft=[2],WalkUp=[2],WalkDown=[2] } });
        var zone=new Zone { Id=0,Width=6,Height=6,TileGrid=new ushort[6,6,3] }; data.Zones.Add(zone);
        for(int y=0;y<6;y++) for(int x=0;x<6;x++) zone.SetTile(x,y,1,ushort.MaxValue);
        var state=new GameState { CurrentZoneId=0,CurrentZone=zone,PlayerX=0,PlayerY=2,SmoothMovementEnabled=true };
        var actor=new NPC { CharacterId=0,X=1,Y=2,Behavior=NPCBehavior.Fleeing,IsInvulnerable=true,Direction=Direction.Right };
        state.ZoneNPCs.Add(actor); zone.SetTile(2,2,1,1);
        Assert(ScriptedActorTiles.Read(data,state,zone,1,2,1)==2,"script predicates could not see an active actor's picture");
        zone.Actions.Add(new Data.Action { Conditions={new Condition { Opcode=ConditionOpcode.TileAtIs,Arguments={2,1,2,1} }},
            Instructions={new Instruction { Opcode=InstructionOpcode.SetCounter,Arguments={7} }} });
        new ActionExecutor(data,state).ExecuteZoneActions(ActionTrigger.Walk);
        Assert(state.GetCounter(0)==7,"native tile predicate did not read the actor picture");
        var plan=ActorGuidePlanner.Plan(data,state,actor,3,4,2);
        Assert(plan?.Type==ObjectiveType.GuideActor && plan.ApproachX!=null && plan.ArrivalX!=null,
            "no guidance plan around a blocked preferred fleeing direction");
        Assert(ActorGuidePlanner.Plan(data,state,actor,3,4,-1)==null,"empty picture slots became actor goals");
        actor.X=1; actor.Y=1;
        Assert(ScriptedActorTiles.Read(data,state,zone,1,2,1)==ushort.MaxValue,"moving an actor left a stale script picture");
        actor.IsEnabled=false;
        Assert(ScriptedActorTiles.Read(data,state,zone,1,1,1)==ushort.MaxValue,"disabled actor remained in script tile predicates");
    }

    private static void CompletedExchangeTools()
    {
        var data=new GameData();
        for(int i=0;i<3;i++) data.Tiles.Add(new Tile { Id=i,Flags=i==0 ? TileFlags.Floor : TileFlags.Item });
        var zone=new Zone { Id=0,Width=6,Height=6,TileGrid=new ushort[6,6,3] }; data.Zones.Add(zone);
        for(int y=0;y<6;y++) for(int x=0;x<6;x++) zone.SetTile(x,y,1,ushort.MaxValue);
        zone.Actions.Add(new Data.Action { Conditions={new Condition { Opcode=ConditionOpcode.PlacedItemIs,Arguments={4,4,0,0,2} }},
            Instructions={new Instruction { Opcode=InstructionOpcode.AddToCounter,Arguments={1} }} });
        zone.Actions.Add(new Data.Action { Conditions={new Condition { Opcode=ConditionOpcode.HeroIsAt,Arguments={1,1} }},
            Instructions={new Instruction { Opcode=InstructionOpcode.WinGame }} });
        var state=new GameState { CurrentZoneId=0,CurrentZone=zone,PlayerX=3,PlayerY=3,Inventory=[2] };
        state.VisitedZones.Add(0);
        var world=new WorldMap(); var sector=new Sector { Zone=0,Type=ZoneType.Goal,RequiredItem=1,AdditionalRequiredItem=2 };
        world.Sectors[0]=sector; var generator=new WorldGenerator(data); generator.SetCurrentWorld(world);
        var solver=new MissionSolver(state,data,generator);
        solver.RestoreReplayIntent(new MissionSolver.ReplayIntent { Consumed=[(0,1)] });
        var ending=solver.NativeGoalPrerequisite(world,sector);
        Assert(ending?.Type==ObjectiveType.WalkToPosition && ending.TargetX==1 && ending.TargetY==1,
            "a reusable preparation tool displaced the remaining authored ending");
        state.Inventory=[1];
        solver=new MissionSolver(state,data,generator);
        solver.RestoreReplayIntent(new MissionSolver.ReplayIntent { Consumed=[(0,2)] });
        var graph=typeof(MissionSolver).GetMethod("GetGraphObjective",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
        ending=(BotObjective?)graph.Invoke(solver,new object[]{world});
        Assert(ending?.Type==ObjectiveType.WalkToPosition && ending.TargetX==1 && ending.TargetY==1,
            "a consumed secondary quest item was fetched again instead of continuing the native ending");
    }

    private static void WildcardPlacedItemResolvesSectorRequiredItem()
    {
        var data=new GameData();
        data.Tiles.Add(new Tile { Id=0,Flags=TileFlags.Floor });
        data.Tiles.Add(new Tile { Id=1,Flags=TileFlags.Item });
        data.Tiles.Add(new Tile { Id=2,Flags=TileFlags.Object });
        data.Tiles.Add(new Tile { Id=3,Flags=TileFlags.Item });
        var zone=new Zone { Id=0,Width=5,Height=5,TileGrid=new ushort[5,5,3] };
        var child=new Zone { Id=1,Width=5,Height=5,TileGrid=new ushort[5,5,3] };
        data.Zones.AddRange(new[]{zone,child});
        zone.SetTile(2,1,1,2);
        zone.SetTile(4,4,1,3);
        zone.Objects.Add(new ZoneObject { Type=ZoneObjectType.DoorEntrance,X=2,Y=1,Argument=1,Enabled=true });
        zone.Actions.Add(new Data.Action {
            Conditions={new Condition { Opcode=ConditionOpcode.PlacedItemIs,Arguments={2,1,1,2,-1} }},
            Instructions={new Instruction { Opcode=InstructionOpcode.RemoveTile,Arguments={2,1,1} }}
        });
        var state=new GameState { CurrentZoneId=0,CurrentZone=zone,PlayerX=1,PlayerY=1,Inventory=[1] };
        state.VisitedZones.Add(0);
        var world=new WorldMap();
        var sector=new Sector { Zone=0,Type=ZoneType.Goal,RequiredItem=3 };
        world.Sectors[0]=sector;world.RoomParents[1]=0;
        var generator=new WorldGenerator(data);generator.SetCurrentWorld(world);
        var solver=new MissionSolver(state,data,generator);
        typeof(MissionSolver).GetMethod("RefreshGraph",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!
            .Invoke(solver,new object[]{world});
        solver.NativeGoalPrerequisite(world,sector,openingTile:(0,2,1));
        var needed=(HashSet<int>)typeof(MissionSolver).GetField("_neededNativeItems",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!
            .GetValue(solver)!;
        Assert(needed.Contains(3) && !needed.Contains(1),
            "item -1 in PlacedItemIs should resolve to the sector-required item, even when another item is carried");
    }

    private static void CompanionYielding()
    {
        var data=new GameData(); data.Tiles.Add(new Tile { Id=0,Flags=TileFlags.Floor });
        data.Tiles.Add(new Tile { Id=1,Flags=TileFlags.Object });
        var zone=new Zone { Id=0,Width=6,Height=5,TileGrid=new ushort[5,6,3] }; data.Zones.Add(zone);
        for(int y=0;y<5;y++) for(int x=0;x<6;x++) zone.SetTile(x,y,1,y==2 ? ushort.MaxValue : (ushort)1);
        var state=new GameState { CurrentZoneId=0,CurrentZone=zone,PlayerX=1,PlayerY=2 };
        var companion=new NPC { X=2,Y=2,Behavior=NPCBehavior.Wandering }; state.ZoneNPCs.Add(companion);
        var actions=new BotActions(state,data,new Pathfinder(data,state));
        actions.OnAction+=(kind,x,y,direction)=> { if(kind==BotActionType.Move) { state.PlayerX=x; state.PlayerY=y; } };
        Assert(actions.MoveTo(3,2),"companion corridor could not be planned");
        actions.Update(0.1); actions.Update(1.1);
        Assert(state.PlayerX==0 && state.PlayerY==2,"Luke did not yield the companion's only exit square");
        companion.X=4;
        for(int i=0;i<12;i++) actions.Update(0.1);
        Assert(state.PlayerX==3 && state.PlayerY==2 && actions.IsCompleted,
            "yielding lost the original destination instead of resuming its route");
        // Some authored pockets have only diagonal exits. Yielding must use
        // the same walk rules as the route it is trying to resume.
        for(int y=0;y<5;y++) for(int x=0;x<6;x++) zone.SetTile(x,y,1,1);
        foreach(var p in new[]{(2,2),(1,3),(3,3),(4,4)}) zone.SetTile(p.Item1,p.Item2,1,ushort.MaxValue);
        state.PlayerX=2; state.PlayerY=2; state.SmoothMovementEnabled=false;
        companion.X=3; companion.Y=3;
        actions=new BotActions(state,data,new Pathfinder(data,state) { AllowDiagonal=true });
        actions.OnAction+=(kind,x,y,direction)=> { if(kind==BotActionType.Move) { state.PlayerX=x; state.PlayerY=y; } };
        Assert(actions.MoveTo(4,4),"diagonal companion corridor could not be planned");
        actions.Update(0.1); actions.Update(1.1);
        Assert(state.PlayerX==1 && state.PlayerY==3,"Luke did not yield through the diagonal exit");
        companion.X=0; companion.Y=0;
        for(int i=0;i<16;i++) actions.Update(0.1);
        Assert(state.PlayerX==4 && state.PlayerY==4 && actions.IsCompleted,"diagonal yielding lost the original route");
    }

    private static void RemoteAutomaticProducer()
    {
        var data=new GameData(); data.Tiles.Add(new Tile { Id=0,Flags=TileFlags.Floor });
        for(int id=0;id<2;id++)
        {
            var zone=new Zone { Id=id,Width=6,Height=6,TileGrid=new ushort[6,6,3] }; data.Zones.Add(zone);
            for(int y=0;y<6;y++) for(int x=0;x<6;x++) zone.SetTile(x,y,1,ushort.MaxValue);
        }
        data.Zones[1].Objects.Add(new ZoneObject { Type=ZoneObjectType.DoorEntrance,X=4,Y=4,Argument=0,Enabled=false });
        data.Zones[1].Actions.Add(new Data.Action { Conditions={new Condition { Opcode=ConditionOpcode.CounterIs,Arguments={0} }},
            Instructions={new Instruction { Opcode=InstructionOpcode.EnableHotspot,Arguments={0} }} });
        var state=new GameState { CurrentZoneId=0,CurrentZone=data.Zones[0],PlayerX=3,PlayerY=3 }; state.VisitedZones.UnionWith(new[]{0,1});
        var world=new WorldMap(); world.Sectors[0]=new Sector { Zone=0,Type=ZoneType.Empty }; world.Sectors[1]=new Sector { Zone=1,Type=ZoneType.Empty };
        world.Connections[0]=new ZoneConnections { ZoneId=0,East=1 }; world.Connections[1]=new ZoneConnections { ZoneId=1,West=0 };
        var generator=new WorldGenerator(data); generator.SetCurrentWorld(world);
        var next=new MissionSolver(state,data,generator).NativeGoalPrerequisite(world,world.Sectors[1],authoredAction:(1,0));
        Assert(next?.Type==ObjectiveType.ChangeZone && next.TargetZoneId==1,
            "a ready remote automatic action was rejected instead of revisited");
    }

    private static void CompanionPassingBay()
    {
        var data=new GameData(); data.Tiles.Add(new Tile { Id=0,Flags=TileFlags.Floor });
        data.Tiles.Add(new Tile { Id=1,Flags=TileFlags.Object|TileFlags.Transparency });
        var zone=new Zone { Id=0,Width=5,Height=5,TileGrid=new ushort[5,5,3] }; data.Zones.Add(zone);
        for(int y=0;y<5;y++) for(int x=0;x<5;x++) zone.SetTile(x,y,1,1);
        foreach(var p in new[]{(2,0),(2,1),(2,2),(2,3),(1,2)}) zone.SetTile(p.Item1,p.Item2,1,ushort.MaxValue);
        var companion=new NPC { X=2,Y=1,StartX=2,StartY=1,Behavior=NPCBehavior.Wandering };
        var state=new GameState { CurrentZoneId=0,CurrentZone=zone,PlayerX=2,PlayerY=2,SmoothMovementEnabled=true,ZoneNPCs=[companion] };
        var actions=new BotActions(state,data,new Pathfinder(data,state) { AllowDiagonal=true });
        actions.OnAction+=(kind,x,y,direction)=> { if(kind==BotActionType.Move) { state.PlayerX=x; state.PlayerY=y; } };
        Assert(actions.MoveTo(2,0),"passing-bay route could not be planned");
        actions.Update(0.1); actions.Update(1.1); actions.Update(0.1);
        Assert(state.PlayerX==1 && state.PlayerY==2,"Luke did not reach the proved passing bay");
        actions.Update(1.1); actions.Update(0.1);
        companion.Y=3;
        for(int i=0;i<16;i++) actions.Update(0.1);
        Assert(state.PlayerX==2 && state.PlayerY==0 && actions.IsCompleted,"passing bay lost the original goal");
    }

    private static void GuardedSignalReset()
    {
        var data=new GameData(); data.Tiles.Add(new Tile { Id=0,Flags=TileFlags.Floor });
        var zone=new Zone { Id=0,Width=6,Height=6,TileGrid=new ushort[6,6,3] }; data.Zones.Add(zone);
        for(int y=0;y<6;y++) for(int x=0;x<6;x++) zone.SetTile(x,y,1,ushort.MaxValue);
        zone.SetTile(2,2,1,0); zone.SetTile(4,4,1,0);
        zone.Actions.Add(new Data.Action { Conditions={new Condition { Opcode=ConditionOpcode.Bump,Arguments={2,2,0} }},
            Instructions={new Instruction { Opcode=InstructionOpcode.SetRandom,Arguments={33} }} });
        zone.Actions.Add(new Data.Action { Conditions={new Condition { Opcode=ConditionOpcode.RandomIs,Arguments={33} },
            new Condition { Opcode=ConditionOpcode.IsVariable,Arguments={0,0,0,0} }},
            Instructions={new Instruction { Opcode=InstructionOpcode.SetRandom,Arguments={0} }} });
        zone.Actions.Add(new Data.Action { Conditions={new Condition { Opcode=ConditionOpcode.Bump,Arguments={4,4,0} }},
            Instructions={new Instruction { Opcode=InstructionOpcode.SetVariable,Arguments={0,0,0,1} }} });
        zone.Actions.Add(new Data.Action { Conditions={new Condition { Opcode=ConditionOpcode.RandomIs,Arguments={33} }},
            Instructions={new Instruction { Opcode=InstructionOpcode.WinGame }} });
        var state=new GameState { CurrentZoneId=0,CurrentZone=zone,PlayerX=3,PlayerY=4 }; state.VisitedZones.Add(0);
        var sector=new Sector { Zone=0,Type=ZoneType.Goal }; var world=new WorldMap(); world.Sectors[0]=sector;
        var generator=new WorldGenerator(data); generator.SetCurrentWorld(world);
        var solver=new MissionSolver(state,data,generator);
        var next=solver.NativeGoalPrerequisite(world,sector);
        Assert(next?.Type==ObjectiveType.BumpTarget && next.TargetX==4 && next.TargetY==4,
            "a guarded reset hid the prerequisite needed before sending its signal");
        // A reset that changes its own guard is a real one-time introduction.
        zone.Actions[1].Instructions.Add(new Instruction { Opcode=InstructionOpcode.SetVariable,Arguments={0,0,0,1} });
        solver=new MissionSolver(state,data,generator);
        next=solver.NativeGoalPrerequisite(world,sector);
        Assert(next?.Type==ObjectiveType.BumpTarget && next.TargetX==2 && next.TargetY==2,
            "a self-invalidating introduction was rejected instead of executed once");
        var executor=new ActionExecutor(data,state) { BumpX=2,BumpY=2 };
        executor.ExecuteZoneActions(ActionTrigger.Bump);
        Assert(!state.IsGameWon && zone.GetTile(0,0,0)==1,"the introduction skipped its own reset");
        executor.ExecuteZoneActions(ActionTrigger.Bump);
        Assert(state.IsGameWon,"the signal never reached its consumer after the introduction");
        zone.SetTile(0,0,0,0);
        zone.Actions[0].Instructions.Add(new Instruction { Opcode=InstructionOpcode.SetVariable,Arguments={0,0,0,1} });
        Assert(solver.SignalReachesConsumer(zone,zone.Actions[0],zone.Actions[3],ConditionOpcode.RandomIs,33),
            "reset guards were evaluated before the producer changed their cell");
    }

    private static void ItemPictureHandoff()
    {
        var data=new GameData();
        data.Tiles.Add(new Tile { Id=0,Flags=TileFlags.Floor });
        data.Tiles.Add(new Tile { Id=1,Flags=TileFlags.Item });
        for(int id=0;id<2;id++)
        {
            var room=new Zone { Id=id,Width=6,Height=6,TileGrid=new ushort[6,6,3] }; data.Zones.Add(room);
            for(int y=0;y<6;y++) for(int x=0;x<6;x++) room.SetTile(x,y,1,ushort.MaxValue);
            room.SetTile(2,2,1,1);
            room.Actions.Add(new Data.Action { Conditions={new Condition { Opcode=ConditionOpcode.Bump,Arguments={2,2,1} }},
                Instructions={new Instruction { Opcode=InstructionOpcode.SetRandom,Arguments={10} }} });
        }
        var zone=data.Zones[0];
        zone.Actions.Add(new Data.Action { Conditions={new Condition { Opcode=ConditionOpcode.RandomIs,Arguments={10} }},
            Instructions={new Instruction { Opcode=InstructionOpcode.RemoveTile,Arguments={2,2,1} },
                new Instruction { Opcode=InstructionOpcode.DropItem,Arguments={1,2,2} }} });
        zone.Objects.Add(new ZoneObject { Type=ZoneObjectType.DoorEntrance,X=4,Y=4,Argument=1,Enabled=true });
        data.Zones[1].Objects.Add(new ZoneObject { Type=ZoneObjectType.DoorExit,X=4,Y=4,Argument=ushort.MaxValue,Enabled=true });
        var state=new GameState { CurrentZoneId=0,CurrentZone=zone,PlayerX=2,PlayerY=3 };state.VisitedZones.Add(0);
        var sector=new Sector { Zone=0,Type=ZoneType.Goal,FindItem=1 };var world=new WorldMap();world.Sectors[0]=sector;world.Sectors[1]=sector;world.RoomParents[1]=0;
        var generator=new WorldGenerator(data);generator.SetCurrentWorld(world);
        var solver=new MissionSolver(state,data,generator);solver.GetCurrentObjective();
        var next=solver.NativeGoalPrerequisite(world,sector,rewardItem:1);
        Assert(next?.Type==ObjectiveType.BumpTarget && next.ScriptIndex==0,
            "a character picture was collected or an unrelated unvisited room was entered before its scripted handoff");
        var fetch=typeof(MissionSolver).GetMethod("FetchItem",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
        next=(BotObjective?)fetch.Invoke(solver,new object[]{world,1});
        Assert(next?.Type==ObjectiveType.BumpTarget && next.ScriptIndex==0,
            "resuming an item dependency bypassed its character handoff");
        var executor=new ActionExecutor(data,state) { BumpX=2,BumpY=2 };
        executor.ExecuteZoneActions(ActionTrigger.Bump);
        Assert(state.HasItem(1) && zone.GetTile(2,2,1)==ushort.MaxValue,
            "a signal-driven handoff recreated its dialogue actor instead of awarding the item");
    }

    private static void LockedObjectStance()
    {
        var data=new GameData();
        data.Tiles.Add(new Tile {Id=0,Flags=TileFlags.Floor});
        data.Tiles.Add(new Tile {Id=1,Flags=TileFlags.Object|TileFlags.Transparency});
        data.Tiles.Add(new Tile {Id=2,Flags=TileFlags.Object|TileFlags.Draggable});
        data.Tiles.Add(new Tile {Id=3,Flags=TileFlags.Item});
        var zone=new Zone {Id=0,Width=9,Height=5,TileGrid=new ushort[5,9,3]};data.Zones.Add(zone);
        for(int y=0;y<5;y++) for(int x=0;x<9;x++) zone.SetTile(x,y,1,1);
        for(int x=1;x<8;x++) zone.SetTile(x,2,1,ushort.MaxValue);
        zone.SetTile(3,2,1,1);zone.SetTile(5,2,1,2);
        zone.Actions.Add(new Data.Action {Conditions={new Condition {Opcode=ConditionOpcode.PlacedItemIs,Arguments={3,2,1,1,3}}},
            Instructions={new Instruction {Opcode=InstructionOpcode.RemoveTile,Arguments={3,2,1}}}});
        zone.Actions.Add(new Data.Action {Conditions={new Condition {Opcode=ConditionOpcode.StandingOn,Arguments={5,2,0}}},
            Instructions={new Instruction {Opcode=InstructionOpcode.WinGame}}});
        var state=new GameState {CurrentZoneId=0,CurrentZone=zone,PlayerX=1,PlayerY=2,Inventory=[3]};state.VisitedZones.Add(0);
        var world=new WorldMap();var sector=new Sector {Zone=0,Type=ZoneType.Goal};world.Sectors[0]=sector;
        var generator=new WorldGenerator(data);generator.SetCurrentWorld(world);
        var solver=new MissionSolver(state,data,generator);solver.GetCurrentObjective();
        var next=solver.NativeGoalPrerequisite(world,sector);
        Assert(next?.Type==ObjectiveType.UseItemOnNpc && next.RequiredItemId==3 && next.TargetX==3,
            "a block covering the authored stance hid the reachable key-operated gate prerequisite");
    }

    private static void HiddenDialogueIsNotTransport()
    {
        var zone=new Zone { Id=0 };
        var dialogue=new Data.Action { Instructions={new Instruction { Opcode=InstructionOpcode.HideHero },
            new Instruction { Opcode=InstructionOpcode.SetCounter,Arguments={30} }} };
        var finish=new Data.Action { Conditions={new Condition { Opcode=ConditionOpcode.CounterIs,Arguments={30} }},
            Instructions={new Instruction { Opcode=InstructionOpcode.WinGame }} };
        zone.Actions.AddRange(new[]{dialogue,finish});
        var state=new GameState();
        Assert(!ScriptedTransport.StartsFlight(zone,dialogue,state),"a final conversation was treated as boarding");
        finish.Instructions.Clear();
        finish.Instructions.Add(new Instruction { Opcode=InstructionOpcode.ChangeZone,Arguments={1,2,3} });
        Assert(ScriptedTransport.StartsFlight(zone,dialogue,state),"a chained zone transfer was not treated as boarding");
        finish.Instructions.Clear();
        zone.Objects.Add(new ZoneObject { Type=ZoneObjectType.VehicleToPrimary,Argument=1 });
        Assert(ScriptedTransport.StartsFlight(zone,dialogue,state),"a vehicle destination was ignored");
    }

    private static void ShadowedBumpPrerequisite()
    {
        var data=new GameData(); data.Tiles.Add(new Tile { Id=0,Flags=TileFlags.Floor });
        data.Tiles.Add(new Tile { Id=1,Flags=TileFlags.Object });
        data.Tiles.Add(new Tile { Id=2,Flags=TileFlags.Object });
        var zone=new Zone { Id=0,Width=6,Height=6,TileGrid=new ushort[6,6,3] }; data.Zones.Add(zone);
        for(int y=0;y<6;y++) for(int x=0;x<6;x++) zone.SetTile(x,y,1,ushort.MaxValue);
        zone.SetTile(3,2,1,1);
        zone.Actions.Add(new Data.Action { Conditions={new Condition { Opcode=ConditionOpcode.Bump,Arguments={3,2,1} }},
            Instructions={new Instruction { Opcode=InstructionOpcode.PlaceTile,Arguments={3,2,1,2} }} });
        zone.Actions.Add(new Data.Action { Conditions={new Condition { Opcode=ConditionOpcode.Bump,Arguments={3,2,2} },
            new Condition { Opcode=ConditionOpcode.HeroIsAt,Arguments={2,2} }},
            Instructions={new Instruction { Opcode=InstructionOpcode.PlaceTile,Arguments={3,2,1,1} }} });
        zone.Actions.Add(new Data.Action { Conditions={new Condition { Opcode=ConditionOpcode.Bump,Arguments={3,2,2} },
            new Condition { Opcode=ConditionOpcode.HeroIsAt,Arguments={2,2} }},
            Instructions={new Instruction { Opcode=InstructionOpcode.WinGame }} });
        var state=new GameState { CurrentZoneId=0,CurrentZone=zone,PlayerX=2,PlayerY=2 }; state.VisitedZones.Add(0);
        var sector=new Sector { Zone=0,Type=ZoneType.Goal }; var world=new WorldMap(); world.Sectors[0]=sector;
        var generator=new WorldGenerator(data); generator.SetCurrentWorld(world);
        Assert(new MissionSolver(state,data,generator).NativeGoalPrerequisite(world,sector)==null,
            "an impossible later bump repeatedly prepared the tile that an earlier bump removes");
    }

    private static void BumpBeforeStance()
    {
        var data=new GameData(); data.Tiles.Add(new Tile { Id=0,Flags=TileFlags.Floor });
        data.Tiles.Add(new Tile { Id=1,Flags=TileFlags.Object });
        var zone=new Zone { Id=0,Width=6,Height=6,TileGrid=new ushort[6,6,3] }; data.Zones.Add(zone);
        for(int y=0;y<6;y++) for(int x=0;x<6;x++) zone.SetTile(x,y,1,ushort.MaxValue);
        zone.Actions.Add(new Data.Action { Conditions={new Condition { Opcode=ConditionOpcode.HeroIsAt,Arguments={1,1} }},
            Instructions={new Instruction { Opcode=InstructionOpcode.PlaceTile,Arguments={3,2,1,1} }} });
        zone.Actions.Add(new Data.Action { Conditions={new Condition { Opcode=ConditionOpcode.Bump,Arguments={3,2,1} },
            new Condition { Opcode=ConditionOpcode.HeroIsAt,Arguments={2,2} }},
            Instructions={new Instruction { Opcode=InstructionOpcode.WinGame }} });
        var state=new GameState { CurrentZoneId=0,CurrentZone=zone,PlayerX=4,PlayerY=4 }; state.VisitedZones.Add(0);
        var sector=new Sector { Zone=0,Type=ZoneType.Goal }; var world=new WorldMap(); world.Sectors[0]=sector;
        var generator=new WorldGenerator(data); generator.SetCurrentWorld(world);
        var next=new MissionSolver(state,data,generator).NativeGoalPrerequisite(world,sector);
        Assert(next?.Type==ObjectiveType.WalkToPosition && next.TargetX==1 && next.TargetY==1,
            "an unprepared bump target sent Luke away from its prerequisite");
    }

    private static void DeepNativeChain()
    {
        var data=new GameData(); data.Tiles.Add(new Tile { Id=0,Flags=TileFlags.Floor });
        var zone=new Zone { Id=0,Width=20,Height=4,TileGrid=new ushort[4,20,3] }; data.Zones.Add(zone);
        for(int y=0;y<4;y++) for(int x=0;x<20;x++) zone.SetTile(x,y,1,ushort.MaxValue);
        for(short i=0;i<16;i++) zone.Actions.Add(new Data.Action {
            Conditions={new Condition { Opcode=ConditionOpcode.TileAtIs,Arguments={1,(short)(i+1),0,0} }},
            Instructions={new Instruction { Opcode=InstructionOpcode.SetVariable,Arguments={i,0,0,1} }} });
        zone.SetTile(17,1,1,0);
        zone.Actions.Add(new Data.Action { Conditions={new Condition { Opcode=ConditionOpcode.Bump,Arguments={17,1,0} }},
            Instructions={new Instruction { Opcode=InstructionOpcode.SetVariable,Arguments={16,0,0,1} }} });
        zone.Actions.Add(new Data.Action { Conditions={new Condition { Opcode=ConditionOpcode.TileAtIs,Arguments={1,0,0,0} }},
            Instructions={new Instruction { Opcode=InstructionOpcode.WinGame }} });
        var state=new GameState { CurrentZoneId=0,CurrentZone=zone,PlayerX=17,PlayerY=2 }; state.VisitedZones.Add(0);
        var sector=new Sector { Zone=0,Type=ZoneType.Goal }; var world=new WorldMap(); world.Sectors[0]=sector;
        var generator=new WorldGenerator(data); generator.SetCurrentWorld(world);
        var next=new MissionSolver(state,data,generator).NativeGoalPrerequisite(world,sector);
        Assert(next?.Type==ObjectiveType.BumpTarget && next.TargetX==17,
            "a valid long prerequisite chain was cut off before its executable leaf");
        sector.RequiredItem=1;
        Assert(new MissionSolver(state,data,generator).NativeGoalPrerequisite(world,sector)==null,
            "deep speculative finale planning bypassed the missing main item");
        state.Inventory.Add(1);
        Assert(new MissionSolver(state,data,generator).NativeGoalPrerequisite(world,sector)?.Type==ObjectiveType.BumpTarget,
            "the proved deep chain stayed unavailable after receiving the main item");
    }

    private static void WanderingScriptActor()
    {
        var data=new GameData();
        for(int i=0;i<3;i++) data.Tiles.Add(new Tile { Id=i,Flags=TileFlags.Floor });
        var zone=new Zone { Id=0,Width=5,Height=5,TileGrid=new ushort[5,5,3] }; data.Zones.Add(zone);
        for(int y=0;y<5;y++) for(int x=0;x<5;x++) zone.SetTile(x,y,1,ushort.MaxValue);
        var character=new Character { Frames=new CharacterFrames { WalkDown=[2],WalkUp=[2],WalkLeft=[2],WalkRight=[2] } };
        data.Characters.Add(character);
        var actor=new NPC { CharacterId=0,NativeMonsterIndex=0,X=1,Y=1,IsInvulnerable=true,Behavior=NPCBehavior.Wandering };
        var state=new GameState { CurrentZoneId=0,CurrentZone=zone,PlayerX=3,PlayerY=3,ZoneNPCs=[actor] }; state.VisitedZones.Add(0);
        zone.Actions.Add(new Data.Action { Conditions={new Condition { Opcode=ConditionOpcode.TileAtIs,Arguments={2,2,1,1} }},
            Instructions={new Instruction { Opcode=InstructionOpcode.WinGame }} });
        var sector=new Sector { Zone=0,Type=ZoneType.Goal }; var world=new WorldMap(); world.Sectors[0]=sector;
        var generator=new WorldGenerator(data); generator.SetCurrentWorld(world);
        var next=new MissionSolver(state,data,generator).NativeGoalPrerequisite(world,sector);
        Assert(next?.Type==ObjectiveType.WaitForActor && next.TargetNpc==actor,
            "script timer actor was replaced by player movement or combat");
        var bot=new MissionBot(state,data,generator);
        var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
        typeof(MissionBot).GetField("_currentObjective",flags)!.SetValue(bot,next);
        typeof(MissionBot).GetMethod("UpdateStuckDetection",flags)!.Invoke(bot,new object[]{6.0});
        Assert((double)typeof(MissionBot).GetField("_stuckTimer",flags)!.GetValue(bot)! == 0,
            "an intentional actor wait was treated as failed player movement");
        typeof(MissionBot).GetField("_currentObjective",flags)!.SetValue(bot,new BotObjective { Type=ObjectiveType.WalkToPosition });
        var movement=(BotActions)typeof(MissionBot).GetField("_actions",flags)!.GetValue(bot)!;
        typeof(BotActions).GetField("_waitingForFriendly",flags)!.SetValue(movement,actor);
        typeof(MissionBot).GetMethod("UpdateStuckDetection",flags)!.Invoke(bot,new object[]{6.0});
        Assert((double)typeof(MissionBot).GetField("_stuckTimer",flags)!.GetValue(bot)! == 0,
            "yielding to a companion was interrupted by the movement watchdog");
    }

    private static void EntryCounterPhase()
    {
        var data=new GameData(); data.Tiles.Add(new Tile { Id=0,Flags=TileFlags.Floor });
        var zone=new Zone { Id=0,Width=6,Height=6,TileGrid=new ushort[6,6,3] }; data.Zones.Add(zone);
        for(int y=0;y<6;y++) for(int x=0;x<6;x++) zone.SetTile(x,y,1,ushort.MaxValue);
        zone.Actions.Add(new Data.Action { Conditions={new Condition { Opcode=ConditionOpcode.ZoneEntered }},
            Instructions={new Instruction { Opcode=InstructionOpcode.SetSectorCounter,Arguments={100} }} });
        zone.Actions.Add(new Data.Action { Conditions={new Condition { Opcode=ConditionOpcode.ZoneEntered }},
            Instructions={new Instruction { Opcode=InstructionOpcode.AddToSectorCounter,Arguments={41} }} });
        zone.Actions.Add(new Data.Action { Conditions={new Condition { Opcode=ConditionOpcode.Bump,Arguments={4,4,0} }},
            Instructions={new Instruction { Opcode=InstructionOpcode.SetSectorCounter,Arguments={251} }} });
        zone.SetTile(4,4,1,0);
        zone.Actions.Add(new Data.Action { Conditions={new Condition { Opcode=ConditionOpcode.SectorCounterIs,Arguments={251} }},
            Instructions={new Instruction { Opcode=InstructionOpcode.WinGame }} });
        var state=new GameState { CurrentZoneId=0,CurrentZone=zone,PlayerX=3,PlayerY=4 };
        state.VisitedZones.Add(0); state.SetSectorCounter(0,141);
        var sector=new Sector { Zone=0,Type=ZoneType.Goal }; var world=new WorldMap(); world.Sectors[0]=sector;
        var generator=new WorldGenerator(data); generator.SetCurrentWorld(world);
        var next=new MissionSolver(state,data,generator).NativeGoalPrerequisite(world,sector);
        Assert(next?.Type==ObjectiveType.BumpTarget && next.TargetX==4 && next.TargetY==4,
            "an entry reset was mistaken for repeated counter increments");
    }

    private static void DurablePressurePlate()
    {
        var data=new GameData();
        data.Tiles.Add(new Tile { Id=0,Flags=TileFlags.Floor });
        data.Tiles.Add(new Tile { Id=1,Flags=TileFlags.Object });
        data.Tiles.Add(new Tile { Id=2,Flags=TileFlags.Object|TileFlags.Draggable });
        var zone=new Zone { Id=0,Width=6,Height=6,TileGrid=new ushort[6,6,3] }; data.Zones.Add(zone);
        for(int y=0;y<6;y++) for(int x=0;x<6;x++) zone.SetTile(x,y,1,ushort.MaxValue);
        zone.SetTile(2,3,1,2); zone.SetTile(4,1,1,1);
        zone.Actions.Add(new Data.Action { Conditions={new Condition { Opcode=ConditionOpcode.StandingOn,Arguments={3,3,0} }},
            Instructions={new Instruction { Opcode=InstructionOpcode.RemoveTile,Arguments={4,1,1} }} });
        zone.Actions.Add(new Data.Action { Conditions={new Condition { Opcode=ConditionOpcode.TileAtIs,Arguments={2,3,3,1} }},
            Instructions={new Instruction { Opcode=InstructionOpcode.RemoveTile,Arguments={4,1,1} }} });
        zone.Actions.Add(new Data.Action { Conditions={new Condition { Opcode=ConditionOpcode.TileAtIs,Arguments={-1,3,3,1} }},
            Instructions={new Instruction { Opcode=InstructionOpcode.PlaceTile,Arguments={4,1,1,1} }} });
        var state=new GameState { CurrentZoneId=0,CurrentZone=zone,PlayerX=1,PlayerY=3 }; state.VisitedZones.Add(0);
        var sector=new Sector { Zone=0,Type=ZoneType.Goal }; var world=new WorldMap(); world.Sectors[0]=sector;
        var generator=new WorldGenerator(data); generator.SetCurrentWorld(world);
        var next=new MissionSolver(state,data,generator).NativeGoalPrerequisite(world,sector,(0,4,1));
        Assert(next?.Type is ObjectiveType.PushObject or ObjectiveType.PullObject,
            "Luke standing on a temporary plate displaced the durable block solution");
    }

    private static void BriefingBeforeFinale()
    {
        var data=new GameData { GameType=GameType.IndianaJones };
        data.Tiles.Add(new Tile { Id=0,Flags=TileFlags.Floor });
        var zone=new Zone { Id=0,Width=6,Height=6,TileGrid=new ushort[6,6,3] }; data.Zones.Add(zone);
        for(int y=0;y<6;y++) for(int x=0;x<6;x++) zone.SetTile(x,y,1,ushort.MaxValue);
        var world=new WorldMap { Mission=new Mission() };
        world.Mission.PuzzleChain.Add(new PuzzleStep { ZoneId=0,RequiredItemId=0,Hint="Receive briefing" });
        world.Sectors[0]=new Sector { Zone=0,Type=ZoneType.Goal };
        zone.Objects.Add(new ZoneObject { Type=ZoneObjectType.SpawnLocation,X=4,Y=3,Enabled=true });
        zone.Actions.Add(new Data.Action { Conditions={new Condition { Opcode=ConditionOpcode.HeroIsAt,Arguments={1,1} }},
            Instructions={new Instruction { Opcode=InstructionOpcode.WinGame }} });
        var state=new GameState { CurrentZoneId=0,CurrentZone=zone,PlayerX=3,PlayerY=3 };
        var generator=new WorldGenerator(data); generator.SetCurrentWorld(world);
        var next=new MissionSolver(state,data,generator).GetCurrentObjective();
        Assert(next.Type==ObjectiveType.TalkToNpc,"a reachable finale displaced the initial mission briefing");
    }

    private static void TransportFloorBoarding()
    {
        var data=new GameData();
        data.Tiles.Add(new Tile { Id=0,Flags=TileFlags.Floor });
        data.Tiles.Add(new Tile { Id=1,Flags=TileFlags.Floor });
        for(int id=0;id<3;id++)
        {
            var zone=new Zone { Id=id,Width=6,Height=6,TileGrid=new ushort[6,6,3] }; data.Zones.Add(zone);
            for(int y=0;y<6;y++) for(int x=0;x<6;x++) zone.SetTile(x,y,1,ushort.MaxValue);
        }
        var departure=data.Zones[0]; departure.SetTile(2,3,0,1);
        departure.Objects.Add(new ZoneObject { Type=ZoneObjectType.VehicleToSecondary,X=4,Y=1,Argument=1,Enabled=true });
        departure.Actions.Add(new Data.Action { Conditions={new Condition { Opcode=ConditionOpcode.CounterIs,Arguments={2} },
                new Condition { Opcode=ConditionOpcode.StandingOn,Arguments={2,3,1} }},
            Instructions={new Instruction { Opcode=InstructionOpcode.MarkAsSolved },new Instruction { Opcode=InstructionOpcode.HideHero },
                new Instruction { Opcode=InstructionOpcode.SetCounter,Arguments={3} }} });
        departure.Actions.Add(new Data.Action { Conditions={new Condition { Opcode=ConditionOpcode.CounterIs,Arguments={3} }},
            Instructions={new Instruction { Opcode=InstructionOpcode.ChangeZone,Arguments={2,0,0} }} });
        var state=new GameState { CurrentZoneId=0,CurrentZone=departure,PlayerX=1,PlayerY=4 }; state.VisitedZones.Add(0); state.SetCounter(0,2);
        var world=new WorldMap(); world.Sectors[0]=new Sector { Zone=0,Type=ZoneType.TravelStart,RequiredItem=-1 };
        world.Sectors[1]=new Sector { Zone=1,Type=ZoneType.TravelEnd }; var generator=new WorldGenerator(data); generator.SetCurrentWorld(world);
        var solver=new MissionSolver(state,data,generator);
        var method=typeof(MissionSolver).GetMethod("OpenTheWay",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
        var next=(BotObjective?)method.Invoke(solver,new object[]{world,1});
        Assert(next?.Type==ObjectiveType.WalkToPosition && next.TargetX==2 && next.TargetY==3,
            "transport ignored the authored boarding floor in favor of its unrelated hotspot marker");
    }

    private static void TradeActorReward()
    {
        var data=new GameData();
        for(int i=0;i<4;i++) data.Tiles.Add(new Tile { Id=i,Flags=i==0 ? TileFlags.Floor : TileFlags.Item });
        var zone=new Zone { Id=0,Width=6,Height=6,TileGrid=new ushort[6,6,3] }; data.Zones.Add(zone);
        for(int y=0;y<6;y++) for(int x=0;x<6;x++) { zone.SetTile(x,y,1,ushort.MaxValue); zone.SetTile(x,y,2,ushort.MaxValue); }
        zone.SetTile(2,1,1,2); zone.SetTile(1,1,1,3);
        zone.Actions.Add(new Data.Action { Conditions={new Condition { Opcode=ConditionOpcode.PlacedItemIs,Arguments={2,1,1,2,1} }},
            Instructions={new Instruction { Opcode=InstructionOpcode.SpeakNpc }} });
        zone.Actions.Add(new Data.Action { Conditions={new Condition { Opcode=ConditionOpcode.PlacedItemIs,Arguments={1,1,1,3,1} }},
            Instructions={new Instruction { Opcode=InstructionOpcode.RemoveTile,Arguments={2,1,1} },
                new Instruction { Opcode=InstructionOpcode.DropItem,Arguments={2,2,1} }} });
        var state=new GameState { CurrentZoneId=0,CurrentZone=zone,PlayerX=1,PlayerY=2,Inventory=[1] };
        state.VisitedZones.Add(0);
        var world=new WorldMap(); var sector=new Sector { Zone=0,Type=ZoneType.Trade,RequiredItem=1,FindItem=2 }; world.Sectors[0]=sector;
        var generator=new WorldGenerator(data); generator.SetCurrentWorld(world);
        var solver=new MissionSolver(state,data,generator);
        var fetch=typeof(MissionSolver).GetMethod("FetchItem",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
        var next=(BotObjective?)fetch.Invoke(solver,new object[]{world,2});
        Assert(next?.Type==ObjectiveType.UseItemOnNpc && next.TargetX==1 && next.TargetY==1,
            "an inventory-shaped trade actor was collected before its authored exchange");
        next=solver.NativeGoalPrerequisite(world,sector,rewardItem:2);
        Assert(next?.Type==ObjectiveType.UseItemOnNpc && next.TargetX==1 && next.TargetY==1,
            "native reward dependencies bypassed the trade actor's handoff");
        var executor=new ActionExecutor(data,state) { PlacedItemId=1,PlacedX=1,PlacedY=1 };
        executor.ExecuteZoneActions(ActionTrigger.UseItem);
        Assert(state.HasItem(2) && zone.GetTile(2,1,1)==ushort.MaxValue,
            "paying another actor recreated the released trade actor instead of awarding it");
        // Ordinary loose drops still belong on the map.
        zone.Actions.Clear();
        zone.Actions.Add(new Data.Action { Instructions={new Instruction { Opcode=InstructionOpcode.DropItem,Arguments={3,3,3} }} });
        executor.ExecuteZoneActions(ActionTrigger.Walk);
        Assert(!state.HasItem(3) && Enumerable.Range(0,6).Any(y=>Enumerable.Range(0,6).Any(x=>(x!=1 || y!=1) && zone.GetTile(x,y,1)==3)),"a loose drop was incorrectly awarded as a handoff");
    }

    private static void RewardTemplateRouting()
    {
        var data=new GameData();
        for(int i=0;i<4;i++) data.Tiles.Add(new Tile { Id=i,Flags=i==0 ? TileFlags.Floor : i==2 ? TileFlags.Object : TileFlags.Item });
        for(int id=0;id<3;id++)
        {
            var zone=new Zone { Id=id,Width=6,Height=6,TileGrid=new ushort[6,6,3] };
            for(int y=0;y<6;y++) for(int x=0;x<6;x++) zone.SetTile(x,y,1,ushort.MaxValue);
            data.Zones.Add(zone);
        }
        for(int id=1;id<3;id++)
        {
            data.Zones[0].Objects.Add(new ZoneObject { Type=ZoneObjectType.DoorEntrance,X=(ushort)(id*2),Y=1,Argument=(ushort)id,Enabled=true });
            data.Zones[id].Objects.Add(new ZoneObject { Type=ZoneObjectType.DoorExit,X=3,Y=5,Argument=ushort.MaxValue,Enabled=true });
            data.Zones[id].Actions.Add(new Data.Action { Conditions={new Condition { Opcode=ConditionOpcode.Bump,Arguments={2,2,2} }},
                Instructions={new Instruction { Opcode=InstructionOpcode.DropItem,Arguments={3,2,3} }} });
        }
        data.Zones[2].SetTile(2,2,1,2);
        var state=new GameState { CurrentZoneId=0,CurrentZone=data.Zones[0],PlayerX=3,PlayerY=3 };
        state.VisitedZones.UnionWith(new[]{0,1,2});
        var world=new WorldMap(); world.Sectors[0]=new Sector { Zone=0,Type=ZoneType.Find,FindItem=3 };
        world.RoomParents[1]=0; world.RoomParents[2]=0;
        var generator=new WorldGenerator(data); generator.SetCurrentWorld(world);
        var solver=new MissionSolver(state,data,generator);
        var fetch=typeof(MissionSolver).GetMethod("FetchItem",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
        var next=(BotObjective?)fetch.Invoke(solver,new object[]{world,3});
        Assert(next?.Type==ObjectiveType.EnterDoor && next.TargetZoneId==2,
            "reward route selected an absent prop in another template room");
    }

    private static void AlternateDoorUnlocks()
    {
        var data=new GameData(); data.Tiles.Add(new Tile { Id=0,Flags=TileFlags.Floor });
        for(int id=0;id<2;id++)
        {
            var zone=new Zone { Id=id,Width=6,Height=6,TileGrid=new ushort[6,6,3] };
            for(int y=0;y<6;y++) for(int x=0;x<6;x++) zone.SetTile(x,y,1,ushort.MaxValue);
            data.Zones.Add(zone);
        }
        var source=data.Zones[0]; var dest=data.Zones[1];
        source.Objects.Add(new ZoneObject { Type=ZoneObjectType.DoorEntrance,X=1,Y=1,Argument=1,Enabled=false });
        source.Objects.Add(new ZoneObject { Type=ZoneObjectType.DoorEntrance,X=4,Y=1,Argument=1,Enabled=false });
        dest.Objects.Add(new ZoneObject { Type=ZoneObjectType.DoorExit,X=3,Y=5,Argument=ushort.MaxValue,Enabled=true });
        for(short i=0;i<2;i++) source.Actions.Add(new Data.Action {
            Conditions={new Condition { Opcode=ConditionOpcode.Bump,Arguments={i==0 ? (short)1 : (short)4,2,0} }},
            Instructions={new Instruction { Opcode=InstructionOpcode.EnableHotspot,Arguments={i} }} });
        var state=new GameState { CurrentZoneId=0,CurrentZone=source,PlayerX=1,PlayerY=3 };
        state.VisitedZones.Add(0); state.VisitedZones.Add(1);
        var world=new WorldMap(); world.Sectors[0]=new Sector { Zone=0,Type=ZoneType.Empty }; world.RoomParents[1]=0;
        var generator=new WorldGenerator(data); generator.SetCurrentWorld(world);
        var solver=new MissionSolver(state,data,generator); solver.GetCurrentObjective();
        var opening=solver.GoalRouteOpening(new[]{source,dest},1,3,4,false,(zone,index)=>zone==0 && index==0);
        Assert(opening?.Action==1,"a rejected entrance was rediscovered instead of trying the other unlock");
        source.Actions[0].Conditions.Add(new Condition { Opcode=ConditionOpcode.RandomIs,Arguments={2} });
        source.Actions[1].Conditions.Add(new Condition { Opcode=ConditionOpcode.RandomIs,Arguments={1} });
        state.ZoneRandom[0]=1;
        opening=solver.GoalRouteOpening(new[]{source,dest},1,3,4,false);
        Assert(opening?.Action==1,"an immutable unselected entrance masked the selected template door");
    }

    private static void SignalConsumptionOrder()
    {
        var data=new GameData(); var zone=new Zone { Id=0 };
        data.Zones.Add(zone); var state=new GameState();
        var producer=new Data.Action { Instructions={new Instruction { Opcode=InstructionOpcode.SetRandom,Arguments={101} }} };
        var reset=new Data.Action { Conditions={new Condition { Opcode=ConditionOpcode.RandomIs,Arguments={101} }},
            Instructions={new Instruction { Opcode=InstructionOpcode.SetRandom,Arguments={0} }} };
        var later=new Data.Action { Instructions={new Instruction { Opcode=InstructionOpcode.SetRandom,Arguments={101} }} };
        var consumer=new Data.Action { Conditions={new Condition { Opcode=ConditionOpcode.RandomIs,Arguments={101} }} };
        zone.Actions.AddRange(new[]{producer,reset,later,consumer});
        var solver=new MissionSolver(state,data,new WorldGenerator(data));
        Assert(!solver.SignalReachesConsumer(zone,producer,consumer,ConditionOpcode.RandomIs,101),
            "planner chose a signal consumed by an earlier dialogue branch");
        Assert(solver.SignalReachesConsumer(zone,later,consumer,ConditionOpcode.RandomIs,101),
            "planner rejected a producer after the earlier reset");
        state.DisabledActions.Add((0,1));
        Assert(solver.SignalReachesConsumer(zone,producer,consumer,ConditionOpcode.RandomIs,101),
            "disabled signal consumers affected the plan");
        solver.RestoreReplayIntent(new MissionSolver.ReplayIntent { NativeFocus=(0,3) });
        solver.NoteScriptActionStarted(0,2);
        Assert(solver.CaptureReplayIntent().NativeFocus==(0,3),"unrelated scripts cancelled the pending objective");
        solver.NoteScriptActionStarted(0,3);
        Assert(solver.CaptureReplayIntent().NativeFocus==null,"executed script remained pending");
    }

    private static void ScriptedRoomFamilies()
    {
        var data=new GameData();
        for(int id=0;id<4;id++) data.Zones.Add(new Zone { Id=id,Width=1,Height=1,TileGrid=new ushort[1,1,3] });
        var world=new WorldMap(); var family=new Sector { Zone=0 };
        world.Sectors[0]=family; world.Sectors[1]=family;
        world.RoomParents[2]=1;
        world.Sectors[3]=new Sector { Zone=3 };
        var generator=new WorldGenerator(data); generator.SetCurrentWorld(world);
        var solver=new MissionSolver(new GameState(),data,generator);
        var rooms=solver.ZoneAndRooms(0).ToArray();
        Assert(rooms[0]==0 && rooms.ToHashSet().SetEquals(new[]{0,1,2}),
            "mission room search omitted script-connected rooms or included an unrelated sector");
    }

    private static void PortalCycleRouting()
    {
        var data=new GameData();
        data.Tiles.Add(new Tile { Id=0,Flags=TileFlags.Floor });
        data.Tiles.Add(new Tile { Id=1,Flags=TileFlags.Object });
        for(int id=0;id<2;id++) data.Zones.Add(new Zone { Id=id,Width=6,Height=6,TileGrid=new ushort[6,6,3] });
        var state=new GameState { CurrentZoneId=1,CurrentZone=data.Zones[1],PlayerX=1,PlayerY=1 };
        var solver=new MissionSolver(state,data,new WorldGenerator(data));
        var portal=new BotObjective { Type=ObjectiveType.BumpTarget,TargetX=2,TargetY=2 };
        var back=new WorldActionGraph.Edge(1,0,portal);
        solver.ObserveRouteTransition(0,1,1,portal);
        state.CurrentZoneId=0; state.CurrentZone=data.Zones[0];
        solver.ObserveRouteTransition(1,1,1,portal);
        Assert(solver.RouteEdgeAllowed(back),"one necessary return trip was mistaken for a repeated loop");
        state.CurrentZoneId=1; state.CurrentZone=data.Zones[1];
        solver.ObserveRouteTransition(0,1,1,portal);
        state.CurrentZoneId=0; state.CurrentZone=data.Zones[0];
        solver.ObserveRouteTransition(1,1,1,portal);
        Assert(!solver.RouteEdgeAllowed(back),"unchanged portal round trip was selected repeatedly");
        solver.NoteDoorEntry(1,0,4,5);
        var exit=new WorldActionGraph.Edge(1,0,new BotObjective { Type=ObjectiveType.EnterDoor,TargetX=2,TargetY=2 });
        Assert(solver.RouteEdgeAllowed(exit),"loop suppression trapped Luke inside a room by rejecting its actual return doorway");
        var wrongExit=new WorldActionGraph.Edge(1,0,new BotObjective { Type=ObjectiveType.EnterDoor,TargetX=2,TargetY=2 });
        solver.NoteDoorEntry(1,2,6,7);
        Assert(!solver.RouteEdgeAllowed(wrongExit),"a non-return portal bypassed loop suppression");
        solver.RestoreReplayIntent(new MissionSolver.ReplayIntent { NeededItems=[511] });
        Assert(solver.RouteEdgeAllowed(back),"discovering a missing dependency did not reopen the route to fetch it");
        state.SetCounter(1,1);
        Assert(solver.RouteEdgeAllowed(back),"a puzzle-state change did not reopen a previously cyclic portal");
        solver.Reset();
        for(int row=0;row<6;row++) data.Zones[0].SetTile(2,row,1,1);
        state.CurrentZoneId=1; state.CurrentZone=data.Zones[1];
        solver.ObserveRouteTransition(0,1,1,portal);
        state.CurrentZoneId=0; state.CurrentZone=data.Zones[0]; state.PlayerX=4;
        solver.ObserveRouteTransition(1,1,1,portal);
        Assert(solver.RouteEdgeAllowed(back),"a valid return to another area component was rejected as a cycle");
    }

    private static void InteractionStanceRouting()
    {
        var data=new GameData(); data.Tiles.Add(new Tile { Id=0,Flags=TileFlags.Floor });
        var zone=new Zone { Id=0,Width=6,Height=6,TileGrid=new ushort[6,6,3] };
        var state=new GameState { CurrentZone=zone,CurrentZoneId=0 };
        state.VisitedZones.Add(0);
        zone.Actions.Add(new Data.Action { Conditions={
            new Condition { Opcode=ConditionOpcode.Bump,Arguments={2,2,0} },
            new Condition { Opcode=ConditionOpcode.HeroIsAt,Arguments={2,3} } },Instructions={
            new Instruction { Opcode=InstructionOpcode.ChangeZone,Arguments={1,2,3} } } });
        zone.Actions.Add(new Data.Action { Conditions={
            new Condition { Opcode=ConditionOpcode.HeroIsAt,Arguments={4,3} } },Instructions={
            new Instruction { Opcode=InstructionOpcode.ChangeZone,Arguments={1,2,3} } } });
        var nav=new Pathfinder(data,state);
        Assert(!nav.IsUnrelatedTransition(zone,2,3),"door bump stance was mistaken for an automatic teleport");
        Assert(nav.IsUnrelatedTransition(zone,2,2),"actual doorway trigger lost its route protection");
        Assert(nav.IsUnrelatedTransition(zone,4,3),"automatic HeroIsAt travel lost its route protection");
    }

    private static void ScriptedItemConsumption()
    {
        var zone=new Zone { Id=0,Width=1,Height=1,TileGrid=new ushort[1,1,3] };
        var state=new GameState { CurrentZone=zone,CurrentZoneId=0 };
        state.Inventory.Add(123);
        zone.Actions.Add(new Data.Action { Conditions={
            new Condition { Opcode=ConditionOpcode.CounterIs,Arguments={0} } },Instructions={
            new Instruction { Opcode=InstructionOpcode.RemoveItem,Arguments={123} },
            new Instruction { Opcode=InstructionOpcode.Wait,Arguments={0} },
            new Instruction { Opcode=InstructionOpcode.RemoveItem,Arguments={123} },
            new Instruction { Opcode=InstructionOpcode.SetCounter,Arguments={1} } } });
        var executor=new ActionExecutor(new GameData(),state);
        var removed=new List<int>(); executor.OnItemRemoved+=removed.Add;
        executor.ExecuteZoneActions(ActionTrigger.Walk);
        Assert(removed.SequenceEqual(new[]{123}),"script consumption was not recorded before a cutscene wait");
        executor.ExecuteZoneActions(ActionTrigger.Walk);
        Assert(removed.Count==1 && state.GetCounter(0)==1,"missing items produced duplicate consumption events");
    }

    private static void ScriptedPassageArrivals()
    {
        var trigger=new Data.Action { Instructions={
            new Instruction { Opcode=InstructionOpcode.MoveHeroTo,Arguments={7,5} },
            new Instruction { Opcode=InstructionOpcode.SetRandom,Arguments={22} } } };
        var arrival=new Data.Action { Conditions={
            new Condition { Opcode=ConditionOpcode.RandomIs,Arguments={22} },
            new Condition { Opcode=ConditionOpcode.RequiredItemIs,Arguments={1246} } },Instructions={
            new Instruction { Opcode=InstructionOpcode.MoveHeroTo,Arguments={7,7} },
            new Instruction { Opcode=InstructionOpcode.MoveHeroBy,Arguments={-1,-1,4,1,1} } } };
        var cosmetic=new Data.Action { Conditions={
            new Condition { Opcode=ConditionOpcode.RandomIs,Arguments={22} },
            new Condition { Opcode=ConditionOpcode.RequiredItemIs,Arguments={1246} } },Instructions={
            new Instruction { Opcode=InstructionOpcode.Redraw } } };
        var zone=new Zone { Id=1,Actions={trigger,cosmetic,arrival} };
        var state=new GameState();
        var sector=new Sector { RequiredItem=1246 };
        Assert(ReferenceEquals(ScriptedPassage.ArrivalAction(zone,trigger,state,GameType.YodaStories,sector),arrival),
            "intermediate relocation hid the guarded final arrival");
        Assert(ScriptedPassage.Arrival(arrival,GameType.YodaStories)==(4,1),"Yoda animated destination was treated as an offset");
        Assert(ScriptedPassage.Arrival(arrival,GameType.IndianaJones)==(6,6),"Indy animated offset was treated as a destination");
        sector.RequiredItem=1292;
        Assert(ReferenceEquals(ScriptedPassage.ArrivalAction(zone,trigger,state,GameType.YodaStories,sector),trigger),
            "passage followed an incompatible quest branch");
        var boat=new Zone { Id=2 };
        boat.Actions.Add(new Data.Action { Conditions={
            new Condition { Opcode=ConditionOpcode.EnterByPlane,Arguments={0} },
            new Condition { Opcode=ConditionOpcode.CounterIs,Arguments={5} } },Instructions={
            new Instruction { Opcode=InstructionOpcode.MoveHeroTo,Arguments={8,9} } } });
        boat.Actions.Add(new Data.Action { Conditions={
            new Condition { Opcode=ConditionOpcode.EnterByPlane,Arguments={0} },
            new Condition { Opcode=ConditionOpcode.CounterIs,Arguments={6} } },Instructions={
            new Instruction { Opcode=InstructionOpcode.MoveHeroTo,Arguments={1,1} } } });
        state.SetCounter(2,5);
        Assert(ScriptedTransport.Arrival(boat,state,GameType.YodaStories)==(8,9),"transport ignored its current trip counter");
        state.SetCounter(2,7);
        Assert(ScriptedTransport.Arrival(boat,state,GameType.YodaStories)==null,"transport used an inactive arrival branch");
        var positionedBoat=new Zone { Id=3 };
        positionedBoat.Actions.Add(new Data.Action { Conditions={
            new Condition { Opcode=ConditionOpcode.EnterByPlane },
            new Condition { Opcode=ConditionOpcode.HeroIsAt,Arguments={8,9} } },Instructions={
            new Instruction { Opcode=InstructionOpcode.MoveHeroTo,Arguments={8,9} } } });
        Assert(ScriptedTransport.Arrival(positionedBoat,state,GameType.IndianaJones)==(8,9),
            "transport arrival rejected zero-argument EnterByPlane or its HeroIsAt landing guard");
        positionedBoat.Actions[0].Conditions[1].Arguments[0]=7;
        Assert(ScriptedTransport.Arrival(positionedBoat,state,GameType.IndianaJones)==null,
            "transport arrival ignored a mismatched HeroIsAt landing guard");
        positionedBoat.Actions[0].Conditions[1].Arguments[0]=8;
        positionedBoat.Actions[0].Conditions.Add(new Condition {Opcode=ConditionOpcode.SectorCounterIs,Arguments={5}});
        positionedBoat.Actions[0].Conditions.Add(new Condition {Opcode=ConditionOpcode.HasItem,Arguments={9}});
        state.SetSectorCounter(3,5);
        Assert(ScriptedTransport.Arrival(positionedBoat,state,GameType.IndianaJones)==null,
            "transport arrival ignored its required ticket");
        state.Inventory.Add(9);
        Assert(ScriptedTransport.Arrival(positionedBoat,state,GameType.IndianaJones)==(8,9),
            "transport rejected an arrival guarded by a valid sector phase and ticket");
        var boarding=new Data.Action { Conditions={
            new Condition { Opcode=ConditionOpcode.Bump,Arguments={2,3,1214} },
            new Condition { Opcode=ConditionOpcode.CounterIs,Arguments={1} } },Instructions={
            new Instruction { Opcode=InstructionOpcode.SetCounter,Arguments={2} } } };
        var preparing=new Data.Action { Conditions={
            new Condition { Opcode=ConditionOpcode.CounterIs,Arguments={2} } },Instructions={
            new Instruction { Opcode=InstructionOpcode.SetCounter,Arguments={3} } } };
        var flying=new Data.Action { Conditions={
            new Condition { Opcode=ConditionOpcode.CounterIs,Arguments={3} } },Instructions={
            new Instruction { Opcode=InstructionOpcode.HideHero },
            new Instruction { Opcode=InstructionOpcode.MoveHeroTo,Arguments={4,4} },
            new Instruction { Opcode=InstructionOpcode.SetCounter,Arguments={4} } } };
        var landing=new Data.Action { Conditions={
            new Condition { Opcode=ConditionOpcode.CounterIs,Arguments={4} } },Instructions={
            new Instruction { Opcode=InstructionOpcode.MoveHeroTo,Arguments={7,5} } } };
        var trip=new Zone { Id=3,Width=10,Height=10,TileGrid=new ushort[10,10,3],Actions={boarding,preparing,flying,landing} };
        trip.SetTile(2,3,1,1214);
        trip.Objects.Add(new ZoneObject {Type=ZoneObjectType.VehicleToPrimary,X=8,Y=1,Argument=9});
        state.SetCounter(3,1);
        Assert(ScriptedTransport.BoardingSpots(trip,state).Contains((2,3)),"boarding missed a multi-stage flight");
        Assert(ReferenceEquals(ScriptedPassage.ArrivalAction(trip,boarding,state,GameType.YodaStories),landing),
            "arrival stopped at an intermediate flight position");
        trip.SetTile(5,5,2,111);
        var itemFlight=new Data.Action {Conditions={new Condition {
            Opcode=ConditionOpcode.PlacedItemIs,Arguments={5,5,2,111,-1}}},Instructions={
            new Instruction {Opcode=InstructionOpcode.HideHero},
            new Instruction {Opcode=InstructionOpcode.MoveHeroTo,Arguments={8,1}}}};
        trip.Actions.Add(itemFlight);sector.RequiredItem=7;
        Assert(!ScriptedTransport.ItemBoardingSpots(trip,state,sector,GameType.IndianaJones,9).Any(),
            "item-based transport boarded without its required item");
        state.Inventory.Add(7);
        Assert(ScriptedTransport.ItemBoardingSpots(trip,state,sector,GameType.IndianaJones,9).Single().Item==7,
            "transport did not resolve the generated required item or top-layer boarding picture");
        state.DisabledActions.Add((trip.Id,trip.Actions.Count-1));
        Assert(!ScriptedTransport.ItemBoardingSpots(trip,state,sector,GameType.IndianaJones,9).Any(),
            "transport used a disabled boarding script");
        var transportData=new GameData();transportData.Tiles.Add(new Tile {Flags=TileFlags.Floor});
        transportData.Tiles.Add(new Tile {Flags=TileFlags.Object});
        for(int id=0;id<3;id++) transportData.Zones.Add(new Zone {Id=id,Width=5,Height=5,TileGrid=new ushort[5,5,3]});
        var start=transportData.Zones[0];start.SetTile(1,1,1,1);
        start.Actions.Add(new Data.Action {Conditions={new Condition {Opcode=ConditionOpcode.Bump,Arguments={1,1,1}}},
            Instructions={new Instruction {Opcode=InstructionOpcode.SetSectorCounter,Arguments={2}}}});
        start.Actions.Add(new Data.Action {Conditions={new Condition {Opcode=ConditionOpcode.SectorCounterIs,Arguments={2}}},
            Instructions={new Instruction {Opcode=InstructionOpcode.Redraw}}});
        start.Actions.Add(new Data.Action {Conditions={new Condition {Opcode=ConditionOpcode.SectorCounterIs,Arguments={2}}},
            Instructions={new Instruction {Opcode=InstructionOpcode.HideHero},
                new Instruction {Opcode=InstructionOpcode.ChangeZone,Arguments={1,0,0}}}});
        var transit=transportData.Zones[1];
        transit.Actions.Add(new Data.Action {Conditions={new Condition {Opcode=ConditionOpcode.EnterByPlane},
            new Condition {Opcode=ConditionOpcode.CounterIs,Arguments={0}}},Instructions={
            new Instruction {Opcode=InstructionOpcode.SetCounter,Arguments={4}}}});
        transit.Actions.Add(new Data.Action {Conditions={new Condition {Opcode=ConditionOpcode.EnterByPlane},
            new Condition {Opcode=ConditionOpcode.CounterIs,Arguments={4}}},Instructions={
            new Instruction {Opcode=InstructionOpcode.ChangeZone,Arguments={2,1,1}}}});
        transportData.Zones[2].Actions.Add(new Data.Action {Conditions={new Condition {Opcode=ConditionOpcode.EnterByPlane}},
            Instructions={new Instruction {Opcode=InstructionOpcode.MoveHeroTo,Arguments={3,3}},
                new Instruction {Opcode=InstructionOpcode.ShowHero}}});
        var travelState=new GameState {CurrentZone=start,CurrentZoneId=0};
        var route=ScriptedZoneTravel.Instructions(start,travelState,transportData).Single();
        Assert(route.TargetZoneId==2 && route.ArrivalX==3 && route.ArrivalY==3 && route.PassageRequiresBump,
            "sector-counter transport did not follow its animation-room redirect to the actual arrival");
        Assert(travelState.GetSectorCounter(0)==0 && travelState.GetCounter(1)==0,
            "transport prediction mutated live counters");
        sector.RequiredItem=1246; state.DisabledActions.Add((1,2));
        Assert(ReferenceEquals(ScriptedPassage.ArrivalAction(zone,trigger,state,GameType.YodaStories,sector),trigger),
            "passage followed a disabled arrival");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
