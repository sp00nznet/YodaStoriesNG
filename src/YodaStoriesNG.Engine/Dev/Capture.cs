using Hexa.NET.SDL2;

namespace YodaStoriesNG.Engine.Dev;

/// <summary>
/// Screenshot sink for the docs capture harness. Off unless YSNG_CAPTURE_DIR is set.
///
/// Every SDL window in the game calls <see cref="Flush"/> immediately before its
/// RenderPresent, tagged with its own name ("game", "map", "assets", ...). The capture
/// script calls <see cref="Request"/> for a tag; the next frame that window renders,
/// its backbuffer is written out. Reading after RenderPresent is undefined, which is
/// why the hook sits before it rather than in the game loop.
/// </summary>
public static unsafe class Capture
{
    /// <summary>Output directory, or null when capture is off.</summary>
    public static readonly string? Dir = Environment.GetEnvironmentVariable("YSNG_CAPTURE_DIR");

    public static bool Enabled => !string.IsNullOrEmpty(Dir);

    private static string? _wantTag;
    private static string? _wantName;

    /// <summary>Ask the window tagged <paramref name="tag"/> to save its next frame as <paramref name="name"/>.bmp.</summary>
    public static void Request(string tag, string name)
    {
        if (!Enabled) return;
        _wantTag = tag;
        _wantName = name;
    }

    /// <summary>Called by each window right before RenderPresent. No-op unless this window was requested.</summary>
    public static void Flush(SDLRenderer* renderer, string tag)
    {
        if (!Enabled || renderer == null || _wantTag != tag || _wantName == null) return;

        var name = _wantName;
        _wantTag = null;
        _wantName = null;
        Write(renderer, name);
    }

    // ponytail: hand-rolled 32-bit BMP instead of an image library. SDL gives us
    // ARGB8888, which on little-endian is B,G,R,A in memory - exactly BMP's byte
    // order - so the "encoder" is a 54-byte header. ffmpeg turns these into
    // PNG/GIF in tools/capture-shots.ps1.
    private static void Write(SDLRenderer* renderer, string name)
    {
        int w, h;
        if (SDL.GetRendererOutputSize(renderer, &w, &h) != 0 || w <= 0 || h <= 0)
        {
            Console.WriteLine($"[capture] {name}: could not read renderer size: {SDL.GetErrorS()}");
            return;
        }

        var pixels = new byte[w * h * 4];
        fixed (byte* p = pixels)
        {
            if (SDL.RenderReadPixels(renderer, default(SDLRectPtr), (uint)SDLPixelFormatEnum.Argb8888, p, w * 4) != 0)
            {
                Console.WriteLine($"[capture] {name}: RenderReadPixels failed: {SDL.GetErrorS()}");
                return;
            }
        }

        var path = Path.Combine(Dir!, name + ".bmp");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var bmp = new BinaryWriter(File.Create(path));
        bmp.Write((ushort)0x4D42);          // "BM"
        bmp.Write(54 + pixels.Length);      // file size
        bmp.Write(0);                       // reserved
        bmp.Write(54);                      // pixel data offset
        bmp.Write(40);                      // BITMAPINFOHEADER size
        bmp.Write(w);
        bmp.Write(-h);                      // negative = top-down, matching SDL's row order
        bmp.Write((ushort)1);               // planes
        bmp.Write((ushort)32);              // bits per pixel
        bmp.Write(0);                       // BI_RGB
        bmp.Write(pixels.Length);
        bmp.Write(0); bmp.Write(0);         // pixels-per-metre x/y
        bmp.Write(0); bmp.Write(0);         // palette entries used/important
        bmp.Write(pixels);

        Console.WriteLine($"[capture] {name}.bmp ({w}x{h})");
    }
}
