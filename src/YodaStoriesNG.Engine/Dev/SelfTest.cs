using YodaStoriesNG.Engine.Data;
using YodaStoriesNG.Engine.Parsing;

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
        failures += Check("IACT round-trip", IactRoundTrip);
        failures += Check("zone header round-trip", ZoneHeaderRoundTrip);
        failures += Check("data file lookup ignores case", DataFileLookupIgnoresCase);
        failures += Check("menu bar keeps off user32 away from Windows", MenuBarStaysOffUser32);
        failures += Check("file filters convert for portable dialogs", FilterConvertsToExtensions);

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

    private static void IactRoundTrip()
    {
        var data = Parse(BuildFile());
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

    private static void ZoneHeaderRoundTrip()
    {
        var zone = Single(Parse(BuildFile()).Zones, "zone");
        Assert(zone.Width == 1 && zone.Height == 1, $"size was {zone.Width}x{zone.Height}");
        Assert(zone.Planet == Planet.Swamp, $"planet was {zone.Planet}");
        Assert(zone.GetTile(0, 0, 0) == 100, $"floor tile was {zone.GetTile(0, 0, 0)}");
        Assert(zone.GetTile(0, 0, 1) == 200, $"object tile was {zone.GetTile(0, 0, 1)}");
        Assert(zone.GetTile(0, 0, 2) == 300, $"roof tile was {zone.GetTile(0, 0, 2)}");
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
    /// The smallest file the parser accepts: a version, one 1x1 zone holding one action,
    /// and an end marker. See docs/DATA-FORMAT.md for what each field is.
    /// </summary>
    private static byte[] BuildFile()
    {
        var stream = new MemoryStream();
        var w = new BinaryWriter(stream);

        Tag(w, "VERS");
        w.Write(new byte[] { 0x00, 0x02, 0x00, 0x00 });   // 2.0, big-endian halves

        Tag(w, "ZONE");
        var zoneLengthAt = Placeholder(w);
        var zoneStart = stream.Position;

        w.Write((ushort)1);          // zone count - deliberately wrong; the parser scans instead
        w.Write((ushort)0);          // padding

        Tag(w, "IZON");
        w.Write((uint)0);            // record size, unused by the parser
        w.Write((ushort)1);          // width
        w.Write((ushort)1);          // height
        w.Write((byte)0);            // flags
        w.Write(new byte[5]);        // padding
        w.Write((byte)Planet.Swamp);
        w.Write((byte)0);            // unused
        w.Write((ushort)100);        // grid 0,0 floor
        w.Write((ushort)200);        // grid 0,0 object
        w.Write((ushort)300);        // grid 0,0 roof
        w.Write((ushort)0);          // object count

        Tag(w, "IACT");
        var actionLengthAt = Placeholder(w);
        var actionStart = stream.Position;

        w.Write((ushort)1);          // one condition
        ActionItem(w, (ushort)ConditionOpcode.TileAtIs, new short[] { 511, 3, 4, 1, 0 }, null);
        w.Write((ushort)1);          // one instruction
        ActionItem(w, (ushort)InstructionOpcode.SpeakNpc, new short[] { 7, 9, 0, 0, 0 },
            "May the Force be with you.");

        Backfill(w, actionLengthAt, stream.Position - actionStart);

        Tag(w, "ENDF");
        Backfill(w, zoneLengthAt, stream.Position - zoneStart);

        return stream.ToArray();
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

    private static GameData Parse(byte[] bytes) =>
        new DtaParser().Parse(new MemoryStream(bytes), GameType.YodaStories);

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
