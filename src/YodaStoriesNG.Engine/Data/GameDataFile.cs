namespace YodaStoriesNG.Engine.Data;

/// <summary>
/// Locates a game data file inside a directory.
///
/// The retail discs write YODESK.DTA and DESKTOP.DAW in upper case, and Linux and macOS
/// take that literally - a plain File.Exists on "yodesk.dta" misses a perfectly good
/// copy of the game. Exact match first, case-insensitive scan of the directory second.
/// </summary>
public static class GameDataFile
{
    public static string? Find(string directory, string fileName)
    {
        var exact = Path.Combine(directory, fileName);
        if (File.Exists(exact))
            return exact;

        if (!Directory.Exists(directory))
            return null;

        foreach (var candidate in Directory.EnumerateFiles(directory))
        {
            if (string.Equals(Path.GetFileName(candidate), fileName, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }

        return null;
    }
}
