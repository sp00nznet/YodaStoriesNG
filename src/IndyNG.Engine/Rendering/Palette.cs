namespace IndyNG.Engine.Rendering;

/// <summary>
/// 256-color palette for Indiana Jones Desktop Adventures with animated color cycling.
/// Loads palette from DAW file STUP section.
/// Format: ARGB (Alpha, Red, Green, Blue)
/// </summary>
public static class Palette
{
    /// <summary>
    /// Animation cycle definition: start index, length, is fast (true) or slow (false)
    /// Indiana Jones color cycles from webfun reference documentation.
    /// </summary>
    private static readonly (int start, int length, bool fast)[] AnimationCycles = new[]
    {
        (0xA0, 8, true),   // Fire/red effects (fast)
        (0xE0, 5, true),   // Water (fast)
        (0xE5, 9, true),   // More water (fast)
        (0xEE, 6, false),  // Lava (slow)
        (0xF4, 2, false),  // Additional effects (slow)
    };

    // Animation state
    private static double _fastTimer = 0;
    private static double _slowTimer = 0;
    private const double FastCycleTime = 0.15;  // 150ms
    private const double SlowCycleTime = 0.30;  // 300ms
    private static bool _animationDirty = false;

    /// <summary>
    /// The working color palette (may be modified by animation).
    /// </summary>
    public static readonly uint[] Colors = new uint[256];

    /// <summary>
    /// The original color palette (never modified after load).
    /// </summary>
    private static readonly uint[] OriginalColors = new uint[256];

    /// <summary>
    /// Whether the palette has been loaded from file.
    /// </summary>
    private static bool _isLoaded = false;

    /// <summary>
    /// Loads the palette from raw DAW STUP section data.
    /// The STUP section contains palette data in various formats.
    /// </summary>
    public static void LoadFromStupData(byte[] stupData)
    {
        if (stupData == null || stupData.Length < 1024)
        {
            Console.WriteLine($"STUP data too small ({stupData?.Length ?? 0} bytes), using fallback palette");
            LoadFallbackPalette();
            return;
        }

        // The STUP section format for Indy: first 1024 bytes are palette (256 * 4 bytes RGBX)
        // Each entry is R, G, B, X (padding byte)
        for (int i = 0; i < 256; i++)
        {
            int offset = i * 4;
            byte r = stupData[offset];
            byte g = stupData[offset + 1];
            byte b = stupData[offset + 2];
            // stupData[offset + 3] is padding/unused

            // Index 0 is transparent
            if (i == 0)
            {
                OriginalColors[i] = 0x00000000;
            }
            else
            {
                OriginalColors[i] = 0xFF000000U | ((uint)r << 16) | ((uint)g << 8) | b;
            }
        }

        // Copy to working palette
        Array.Copy(OriginalColors, Colors, 256);
        _isLoaded = true;

        Console.WriteLine("Loaded Indiana Jones palette from DAW file");
    }

    /// <summary>
    /// Loads the correct Indiana Jones palette (extracted from DESKADV.EXE).
    /// </summary>
    private static void LoadFallbackPalette()
    {
        // Indiana Jones Desktop Adventures palette extracted from DESKADV.EXE
        // Format: ARGB (0xAARRGGBB)
        uint[] indyPalette = new uint[256]
        {
            0x00000000, 0xFF000000, 0xFF000000, 0xFF000000, 0xFF000000, 0xFF000000, 0xFF000000, 0xFF000000, 0xFF000000, 0xFF000000, 0xFFFF0000, 0xFFD70000, 0xFFB30000, 0xFF8B0000, 0xFF670000, 0xFF430000,
            0xFFFBFBFB, 0xFFE3E3E3, 0xFFD3D3D3, 0xFFC3C3C3, 0xFFB3B3B3, 0xFFABABAB, 0xFF9B9B9B, 0xFF8B8B8B, 0xFF7B7B7B, 0xFF737373, 0xFF636363, 0xFF535353, 0xFF4B4B4B, 0xFF3B3B3B, 0xFF2B2B2B, 0xFF232323,
            0xFF43C700, 0xFF3FB700, 0xFF3FAB00, 0xFF3B9F00, 0xFF379300, 0xFF338700, 0xFF337B00, 0xFF2F6F00, 0xFF2B6300, 0xFF235300, 0xFF1F4700, 0xFF173700, 0xFF0F2700, 0xFF0B1B00, 0xFF070B00, 0xFF000000,
            0xFF7BFB3B, 0xFFC37B6B, 0xFFAB535B, 0xFF934353, 0xFF7B2B53, 0xFF631B4B, 0xFF3B133B, 0xFFFFD7AB, 0xFFF3C38F, 0xFFE7B373, 0xFFDBA35B, 0xFFCF9743, 0xFFC38B2F, 0xFFB77F1B, 0xFFAF730B, 0xFFA36B00,
            0xFFFFFFEB, 0xFFF3F3D7, 0xFFE7E7C7, 0xFFDBDBB7, 0xFFCFCFA3, 0xFFC3C397, 0xFFB3B37F, 0xFFA3A363, 0xFF93934F, 0xFF83833B, 0xFF73732B, 0xFF5F5F1B, 0xFF4F4F0F, 0xFF3F3F07, 0xFF2F2F00, 0xFF1F1F00,
            0xFFD3FB5B, 0xFFC3FB43, 0xFFB3FB23, 0xFFA3FB00, 0xFF93E300, 0xFF83CB00, 0xFF73B300, 0xFF639B00, 0xFF8B5B00, 0xFF774F00, 0xFF674300, 0xFF573700, 0xFF472F00, 0xFF372300, 0xFF271700, 0xFF170F00,
            0xFF4FFB00, 0xFF4BEF00, 0xFF47DF00, 0xFF47D300, 0xFF679F00, 0xFF5B7F00, 0xFF436300, 0xFF274700, 0xFF1B2B00, 0xFF002323, 0xFF000000, 0xFF000000, 0xFF000000, 0xFF000000, 0xFFDB378B, 0xFFB32B77,
            0xFFDBFBFB, 0xFFBBFBFB, 0xFF9BFBFB, 0xFF7BFBFB, 0xFF5BFBFB, 0xFF43FBFB, 0xFF23FBFB, 0xFF00FBFB, 0xFF00E3E3, 0xFF00CBCB, 0xFF00B3B3, 0xFF009B9B, 0xFF008383, 0xFF007373, 0xFF005B5B, 0xFF004343,
            0xFF47BFFF, 0xFF33AFF7, 0xFF1FA3EF, 0xFF0F97E7, 0xFF008BE3, 0xFF007BCB, 0xFF006BB3, 0xFF005B9B, 0xFF00477B, 0xFF00375F, 0xFF002743, 0xFF001727, 0xFF5B63FB, 0xFF4343FB, 0xFF2323FB, 0xFF0000FB,
            0xFF0000FB, 0xFF0000DB, 0xFF0000C3, 0xFF0000AB, 0xFF00008B, 0xFF000073, 0xFF00005B, 0xFF000043, 0xFFFBBBBF, 0xFFF7ABAF, 0xFFF39BA3, 0xFFEF8F97, 0xFFEB7F87, 0xFFE7737F, 0xFFDF5B6B, 0xFFCB3B47,
            0xFF43B3F7, 0xFF4FBBF7, 0xFF5BC7F7, 0xFF6BCFF7, 0xFF77D7F7, 0xFF83DFF7, 0xFF93E7F7, 0xFF6BCFF7, 0xFFCB4300, 0xFFBB3300, 0xFFA32300, 0xFF931B00, 0xFF7B0B00, 0xFF6B0000, 0xFF530000, 0xFF430000,
            0xFFFFFF00, 0xFFF7E300, 0xFFF3CF00, 0xFFEFB700, 0xFFEBA300, 0xFFE78B00, 0xFFDF7700, 0xFFDB6300, 0xFFD74F00, 0xFFD33F00, 0xFFCF2F00, 0xFFE3C777, 0xFFDBB76B, 0xFFD3A763, 0xFFCB975B, 0xFFC38B53,
            0xFFFBEBDB, 0xFFFBE3D3, 0xFFFBDBC3, 0xFFFBD3BB, 0xFFFBCBB3, 0xFFFBC3A3, 0xFFFBBB9B, 0xFFFBB78F, 0xFFFBB383, 0xFFFBA373, 0xFFFB9B63, 0xFFF3935B, 0xFFEB8B5B, 0xFFDB8B53, 0xFFD38353, 0xFFCB7B4B,
            0xFFBB7B4B, 0xFFB37343, 0xFFAB6B43, 0xFFA3633B, 0xFF9B633B, 0xFF935B33, 0xFF8B5B33, 0xFF83532B, 0xFF734B2B, 0xFF6B4B23, 0xFF5B4323, 0xFF533B1B, 0xFF4B3B1B, 0xFF43331B, 0xFF3B2B13, 0xFF2B230B,
            0xFF6FAB00, 0xFF6BA300, 0xFF679F00, 0xFF6BA300, 0xFF6FAB00, 0xFF0793E7, 0xFF0F97E7, 0xFF179FEB, 0xFF23A3EF, 0xFF2BABF3, 0xFF37B3F7, 0xFF27A7EF, 0xFF1B9FEB, 0xFF0F97E7, 0xFFFBCB0B, 0xFFFBA30B,
            0xFFFB730B, 0xFFFB4B0B, 0xFFFB230B, 0xFFFB730B, 0xFF931300, 0xFFD30B00, 0xFF000000, 0xFF000000, 0xFF000000, 0xFF000000, 0xFF000000, 0xFF000000, 0xFF000000, 0xFF000000, 0xFF000000, 0xFFFFFFFF
        };

        Array.Copy(indyPalette, OriginalColors, 256);
        Array.Copy(indyPalette, Colors, 256);
        _isLoaded = true;
    }

    /// <summary>
    /// Gets the color for a palette index as ARGB32.
    /// </summary>
    public static uint GetColor(byte index) => Colors[index];

    /// <summary>
    /// Checks if the given palette index should be treated as transparent.
    /// </summary>
    public static bool IsTransparent(byte index) => index == 0;

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

        foreach (var (start, length, fast) in AnimationCycles)
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
        foreach (var (start, length, _) in AnimationCycles)
        {
            if (index >= start && index < start + length)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Static constructor - load fallback palette in case LoadFromStupData is not called.
    /// </summary>
    static Palette()
    {
        LoadFallbackPalette();
    }
}
