using System.Diagnostics;

namespace YodaStoriesNG.Engine.UI;

/// <summary>
/// A file picker for the platforms comdlg32 does not exist on.
///
/// SDL2 has no file dialog, so the choices were to draw a file browser inside the engine or
/// to ask the desktop for the one it already has. This asks: macOS answers with osascript,
/// Linux with zenity or kdialog, both of which ship with GNOME and KDE respectively. If a
/// machine has neither, the caller gets null and the menu item reports that rather than
/// pretending - which still beats what this replaced, a Console.ReadLine that froze the
/// render loop on stdin while the window sat there looking hung.
/// </summary>
public static class PortableFileDialog
{
    /// <summary>Seconds to wait for the helper before giving up on it.</summary>
    private const int TimeoutSeconds = 300;

    public static string? Open(string title, string? initialDir, IReadOnlyList<string> extensions)
        => Show(save: false, title, initialDir, defaultFileName: null, extensions);

    public static string? Save(string title, string? initialDir, string? defaultFileName, IReadOnlyList<string> extensions)
        => Show(save: true, title, initialDir, defaultFileName, extensions);

    private static string? Show(bool save, string title, string? initialDir, string? defaultFileName,
        IReadOnlyList<string> extensions)
    {
        if (OperatingSystem.IsMacOS())
            return Run("osascript", AppleScript(save, title, initialDir, defaultFileName));

        if (Which("zenity") != null)
            return Run("zenity", Zenity(save, title, initialDir, defaultFileName, extensions));

        if (Which("kdialog") != null)
            return Run("kdialog", KDialog(save, title, initialDir, defaultFileName, extensions));

        Console.WriteLine("No file dialog available - install zenity or kdialog, " +
                          "or pass the file on the command line.");
        return null;
    }

    private static List<string> AppleScript(bool save, string title, string? initialDir, string? defaultFileName)
    {
        // "choose file"/"choose file name" return an alias; POSIX path turns it into /a/path.
        var chooser = save
            ? $"choose file name with prompt {Literal(title)}"
            : $"choose file with prompt {Literal(title)}";

        if (save && !string.IsNullOrEmpty(defaultFileName))
            chooser += $" default name {Literal(defaultFileName)}";

        if (!string.IsNullOrEmpty(initialDir) && Directory.Exists(initialDir))
            chooser += $" default location POSIX file {Literal(initialDir)}";

        return new List<string> { "-e", $"POSIX path of ({chooser})" };
    }

    private static List<string> Zenity(bool save, string title, string? initialDir, string? defaultFileName,
        IReadOnlyList<string> extensions)
    {
        var args = new List<string> { "--file-selection", $"--title={title}" };

        if (save)
        {
            args.Add("--save");
            args.Add("--confirm-overwrite");
        }

        var start = StartPath(initialDir, save ? defaultFileName : null);
        if (start != null)
            args.Add($"--filename={start}");

        if (extensions.Count > 0)
            args.Add("--file-filter=" + string.Join(" ", extensions.Select(e => "*." + e)));

        return args;
    }

    private static List<string> KDialog(bool save, string title, string? initialDir, string? defaultFileName,
        IReadOnlyList<string> extensions)
    {
        // kdialog takes the starting path as a positional argument, and "" means "wherever".
        var filter = extensions.Count > 0
            ? string.Join(" ", extensions.Select(e => "*." + e))
            : "*";

        return new List<string>
        {
            save ? "--getsavefilename" : "--getopenfilename",
            StartPath(initialDir, save ? defaultFileName : null) ?? ".",
            filter,
            "--title", title,
        };
    }

    private static string? StartPath(string? initialDir, string? defaultFileName)
    {
        if (string.IsNullOrEmpty(initialDir) || !Directory.Exists(initialDir))
            return null;

        // A trailing separator is what tells both helpers "this is a directory, not a name".
        return string.IsNullOrEmpty(defaultFileName)
            ? initialDir + Path.DirectorySeparatorChar
            : Path.Combine(initialDir, defaultFileName);
    }

    /// <summary>AppleScript string literal - the only escapes it needs are backslash and quote.</summary>
    private static string Literal(string value)
        => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string? Run(string program, List<string> arguments)
    {
        try
        {
            var info = new ProcessStartInfo(program)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var argument in arguments)
                info.ArgumentList.Add(argument);

            using var process = Process.Start(info);
            if (process == null)
                return null;

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(TimeoutSeconds * 1000))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            // Every one of these exits non-zero when the user cancels.
            if (process.ExitCode != 0)
                return null;

            var path = output.Trim();
            return path.Length == 0 ? null : path;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"File dialog ({program}) failed: {ex.Message}");
            return null;
        }
    }

    private static string? Which(string program)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (directory.Length == 0)
                continue;

            var candidate = Path.Combine(directory, program);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }
}
