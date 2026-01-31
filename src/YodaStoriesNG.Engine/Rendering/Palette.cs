using YodaStoriesNG.Engine.Data;

namespace YodaStoriesNG.Engine.Rendering;

/// <summary>
/// Default 256-color palette for Yoda Stories with animated color cycling.
/// This palette was extracted from the original game executable.
/// Format: RGBA (Red, Green, Blue, Alpha)
/// </summary>
public static class Palette
{
    /// <summary>
    /// Animation cycle definition: start index, length, is fast (true) or slow (false)
    /// </summary>
    private static readonly (int start, int length, bool fast)[] YodaCycles = new[]
    {
        (0x0A, 6, true),   // Water/blue effects
        (0xC6, 2, false),
        (0xC8, 2, false),
        (0xCA, 2, true),
        (0xCC, 2, true),
        (0xCE, 2, true),
        (0xD7, 9, false),  // Forest colors
        (0xE0, 5, true),   // Ice/snow effects
        (0xE5, 9, false),  // Water shimmer
        (0xEE, 6, true),   // Lava/fire effects
        (0xF4, 2, false),
    };

    private static readonly (int start, int length, bool fast)[] IndyCycles = new[]
    {
        (0xA0, 8, true),   // Fire/lava
        (0xE0, 5, true),   // Water
        (0xE5, 9, true),   // More water
        (0xEE, 6, false),  // Lava
        (0xF4, 2, false),
    };

    // Animation state
    private static double _fastTimer = 0;
    private static double _slowTimer = 0;
    private const double FastCycleTime = 0.15;  // 150ms
    private const double SlowCycleTime = 0.30;  // 300ms
    private static bool _animationDirty = false;
    private static GameType _gameType = GameType.YodaStories;

    /// <summary>
    /// The working color palette (may be modified by animation).
    /// </summary>
    public static readonly uint[] Colors = new uint[256];

    /// <summary>
    /// The original color palette (never modified).
    /// </summary>
    private static readonly uint[] OriginalColors = new uint[256]
    {
        // Palette from goda-stories project
        // Row 0 (0x00-0x0F)
        0x00000000, 0xFF000000, 0xFF000000, 0xFF000000, 0xFF000000, 0xFF000000, 0xFF000000, 0xFF000000,
        0xFF000000, 0xFF000000, 0xFF8BFFFF, 0xFF4BCFC3, 0xFF1BA38B, 0xFF007757, 0xFF1BA38B, 0xFF4BCFC3,
        // Row 1 (0x10-0x1F)
        0xFFFBFBFB, 0xFFE7E7EB, 0xFFD3D3DB, 0xFFC3C3CB, 0xFFB3B3BB, 0xFFA3A3AB, 0xFF8F8F9B, 0xFF7F7F8B,
        0xFF6F6F7B, 0xFF5B5B67, 0xFF4B4B57, 0xFF3B3B47, 0xFF2B2B33, 0xFF1B1B23, 0xFF0F0F13, 0xFF000000,
        // Row 2 (0x20-0x2F)
        0xFF43C700, 0xFF43B700, 0xFF3FAB00, 0xFF3F9F00, 0xFF3F9300, 0xFF3B8700, 0xFF377B00, 0xFF336F00,
        0xFF336300, 0xFF2B5300, 0xFF274700, 0xFF233B00, 0xFF1B2F00, 0xFF132300, 0xFF0F1700, 0xFF070B00,
        // Row 3 (0x30-0x3F)
        0xFFBB7B4B, 0xFFB37343, 0xFFAB6B43, 0xFFA3633B, 0xFF9B633B, 0xFF935B33, 0xFF8B5B33, 0xFF83532B,
        0xFF734B2B, 0xFF6B4B23, 0xFF5F4323, 0xFF533B1B, 0xFF47371B, 0xFF43331B, 0xFF3B2B13, 0xFF2B230B,
        // Row 4 (0x40-0x4F)
        0xFFFFFFD7, 0xFFEFEFBB, 0xFFDFDFA3, 0xFFCFCF8B, 0xFFC3C377, 0xFFB3B363, 0xFFA3A353, 0xFF939343,
        0xFF878733, 0xFF777727, 0xFF67671B, 0xFF5B5B13, 0xFF4B4B0B, 0xFF3B3B07, 0xFF2B2B00, 0xFF1F1F00,
        // Row 5 (0x50-0x5F)
        0xFFFBEBDB, 0xFFFBE3D3, 0xFFFBDBC3, 0xFFFBD3BB, 0xFFFBCBB3, 0xFFFBC3A3, 0xFFFBBB9B, 0xFFFBB78F,
        0xFFF7B383, 0xFFFBA773, 0xFFFB9B63, 0xFFF3935B, 0xFFEB8B5B, 0xFFDB8B53, 0xFFD38353, 0xFFCB7B4B,
        // Row 6 (0x60-0x6F)
        0xFFFFC79B, 0xFFF7B78F, 0xFFEFB387, 0xFFF3A77F, 0xFFEF9F73, 0xFFCF8353, 0xFFB36B3B, 0xFFA35B2F,
        0xFF934F23, 0xFF83431B, 0xFF773B13, 0xFF672F0B, 0xFF572707, 0xFF471B00, 0xFF6D1300, 0xFF2B0F00,
        // Row 7 (0x70-0x7F)
        0xFFE7FBFB, 0xFFD3F3F3, 0xFFC7E7EB, 0xFFB7DFE3, 0xFFA7D7DB, 0xFF97CFD3, 0xFF8BC7CB, 0xFF7FBBC3,
        0xFF73B3BB, 0xFF63A7AF, 0xFF47939B, 0xFF337B87, 0xFF1F676F, 0xFF0F535B, 0xFF004347, 0xFF003337,
        // Row 8 (0x80-0x8F)
        0xFFF7F7FF, 0xFFDFDFEF, 0xFFC7C7DF, 0xFFB3B3CF, 0xFF9F9FBF, 0xFF8B8BB3, 0xFF7B7BA3, 0xFF6B6B93,
        0xFF575783, 0xFF4B4B73, 0xFF3B3B67, 0xFF2F2F57, 0xFF272747, 0xFF1B1B37, 0xFF131327, 0xFF0B0B1B,
        // Row 9 (0x90-0x9F)
        0xFF37B3F7, 0xFF0793E7, 0xFF0B53FB, 0xFF0000FB, 0xFF0000CB, 0xFF00009F, 0xFF00006F, 0xFF000043,
        0xFFFBBBBF, 0xFFFB8B8F, 0xFFFB5B5F, 0xFFFFBB93, 0xFFF7975F, 0xFFEF7B3B, 0xFFC36323, 0xFFB35313,
        // Row A (0xA0-0xAF)
        0xFFFF0000, 0xFFEF0000, 0xFFE30000, 0xFFD30000, 0xFFC30000, 0xFFB70000, 0xFFA70000, 0xFF9B0000,
        0xFF8B0000, 0xFF7F0000, 0xFF6F0000, 0xFF630000, 0xFF530000, 0xFF470000, 0xFF370000, 0xFF2B0000,
        // Row B (0xB0-0xBF)
        0xFFFFFF00, 0xFFF7E300, 0xFFF3CF00, 0xFFEFB700, 0xFFEBA300, 0xFFE78B00, 0xFFDF7700, 0xFFDB6300,
        0xFFD74F00, 0xFFD33F00, 0xFFCF2F00, 0xFFFFFF97, 0xFFEFDF83, 0xFFDFC373, 0xFFCFA75F, 0xFFC38B53,
        // Row C (0xC0-0xCF)
        0xFF002B2B, 0xFF002323, 0xFF001B1B, 0xFF001313, 0xFF000BFF, 0xFF4B00FF, 0xFFA300FF, 0xFFFF00FF,
        0xFF00FF00, 0xFF004B00, 0xFF00FFFF, 0xFF2F33FF, 0xFFFF0000, 0xFF971F00, 0xFFFF00DF, 0xFF770073,
        // Row D (0xD0-0xDF)
        0xFFC37B6B, 0xFFAB5757, 0xFF934757, 0xFF7F3753, 0xFF67274F, 0xFF4F1B47, 0xFF3B133B, 0xFF777727,
        0xFF737323, 0xFF6F6F1F, 0xFF6B6B1B, 0xFF67671B, 0xFF6B6B1B, 0xFF6F6F1F, 0xFF737323, 0xFF777727,
        // Row E (0xE0-0xEF)
        0xFFEFFFFF, 0xFFDBF7F7, 0xFFCBEFF3, 0xFFBBEBEF, 0xFFCBEFF3, 0xFF0793E7, 0xFF0F97E7, 0xFF179FEB,
        0xFF23A3EF, 0xFF2BABF3, 0xFF37B3F7, 0xFF27A7EF, 0xFF1B9FEB, 0xFF0F97E7, 0xFFFBCB0B, 0xFFFBA30B,
        // Row F (0xF0-0xFF)
        0xFFFB730B, 0xFFFB4B0B, 0xFFFB230B, 0xFFFB730B, 0xFF931300, 0xFFD30B00, 0xFF000000, 0xFF000000,
        0xFF000000, 0xFF000000, 0xFF000000, 0xFF000000, 0xFF000000, 0xFF000000, 0xFF000000, 0xFFFFFFFF,
    };

    /// <summary>
    /// Gets the color for a palette index as ARGB32.
    /// </summary>
    public static uint GetColor(byte index) => Colors[index];

    /// <summary>
    /// Gets the color components for a palette index.
    /// </summary>
    public static (byte r, byte g, byte b, byte a) GetColorComponents(byte index)
    {
        var color = Colors[index];
        return (
            (byte)((color >> 16) & 0xFF),  // R
            (byte)((color >> 8) & 0xFF),   // G
            (byte)(color & 0xFF),           // B
            (byte)((color >> 24) & 0xFF)   // A
        );
    }

    /// <summary>
    /// Checks if the given palette index should be treated as transparent.
    /// </summary>
    public static bool IsTransparent(byte index) => index == 0;

    /// <summary>
    /// Static constructor - copy original colors to working palette.
    /// </summary>
    static Palette()
    {
        Array.Copy(OriginalColors, Colors, 256);
    }

    /// <summary>
    /// Sets the game type for palette animation (different games have different cycling regions).
    /// </summary>
    public static void SetGameType(GameType gameType)
    {
        _gameType = gameType;
        // Reset colors to original
        Array.Copy(OriginalColors, Colors, 256);
        _animationDirty = true;
    }

    /// <summary>
    /// Updates the palette animation. Call this every frame.
    /// </summary>
    /// <param name="deltaTime">Time since last frame in seconds</param>
    /// <returns>True if palette was modified and textures should be refreshed</returns>
    public static bool UpdateAnimation(double deltaTime)
    {
        _animationDirty = false;

        _fastTimer += deltaTime;
        _slowTimer += deltaTime;

        bool fastCycle = _fastTimer >= FastCycleTime;
        bool slowCycle = _slowTimer >= SlowCycleTime;

        if (fastCycle) _fastTimer = 0;
        if (slowCycle) _slowTimer = 0;

        if (!fastCycle && !slowCycle) return false;

        var cycles = _gameType == GameType.IndianaJones ? IndyCycles : YodaCycles;

        foreach (var (start, length, fast) in cycles)
        {
            if ((fast && fastCycle) || (!fast && slowCycle))
            {
                CycleColors(start, length);
                _animationDirty = true;
            }
        }

        return _animationDirty;
    }

    /// <summary>
    /// Cycles colors in a range by rotating them.
    /// </summary>
    private static void CycleColors(int start, int length)
    {
        if (start < 0 || start + length > 256 || length < 2) return;

        // Rotate colors: save first, shift all left, put saved at end
        uint first = Colors[start];
        for (int i = 0; i < length - 1; i++)
        {
            Colors[start + i] = Colors[start + i + 1];
        }
        Colors[start + length - 1] = first;
    }

    /// <summary>
    /// Returns true if the palette animation has changed since last check.
    /// </summary>
    public static bool IsAnimationDirty => _animationDirty;

    /// <summary>
    /// Checks if a palette index is in an animated color range.
    /// </summary>
    public static bool IsAnimatedIndex(byte index)
    {
        var cycles = _gameType == GameType.IndianaJones ? IndyCycles : YodaCycles;
        foreach (var (start, length, _) in cycles)
        {
            if (index >= start && index < start + length)
                return true;
        }
        return false;
    }
}
