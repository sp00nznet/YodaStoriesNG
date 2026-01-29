using YodaStoriesNG.Engine.Data;

namespace YodaStoriesNG.Engine.Rendering;

/// <summary>
/// Converts tile pixel data to ARGB32 format for rendering.
/// </summary>
public class TileRenderer
{
    /// <summary>
    /// Converts a tile's indexed pixel data to ARGB32 format.
    /// </summary>
    /// <param name="tile">The tile to convert.</param>
    /// <returns>Array of ARGB32 pixel values (32x32 = 1024 pixels).</returns>
    public uint[] ConvertTileToArgb32(Tile tile)
    {
        var result = new uint[Tile.PixelCount];

        for (int i = 0; i < Tile.PixelCount; i++)
        {
            var paletteIndex = tile.PixelData[i];
            result[i] = Palette.GetColor(paletteIndex);
        }

        return result;
    }

    /// <summary>
    /// Converts a tile's indexed pixel data to a raw byte array (RGBA format).
    /// </summary>
    /// <param name="tile">The tile to convert.</param>
    /// <returns>Array of bytes in RGBA format (32x32x4 = 4096 bytes).</returns>
    public byte[] ConvertTileToRgba(Tile tile)
    {
        var result = new byte[Tile.PixelCount * 4];

        for (int i = 0; i < Tile.PixelCount; i++)
        {
            var paletteIndex = tile.PixelData[i];
            var (r, g, b, a) = Palette.GetColorComponents(paletteIndex);

            // Handle transparency (index 0)
            if (Palette.IsTransparent(paletteIndex))
            {
                a = 0;
            }

            result[i * 4 + 0] = r;
            result[i * 4 + 1] = g;
            result[i * 4 + 2] = b;
            result[i * 4 + 3] = a;
        }

        return result;
    }

    /// <summary>
    /// Renders multiple tiles into a combined texture atlas.
    /// </summary>
    /// <param name="tiles">The tiles to combine.</param>
    /// <param name="tilesPerRow">Number of tiles per row in the atlas.</param>
    /// <returns>Combined ARGB32 pixel data and dimensions.</returns>
    public (uint[] pixels, int width, int height) CreateTileAtlas(IList<Tile> tiles, int tilesPerRow)
    {
        if (tiles.Count == 0)
            return (Array.Empty<uint>(), 0, 0);

        var tilesPerColumn = (tiles.Count + tilesPerRow - 1) / tilesPerRow;
        var atlasWidth = tilesPerRow * Tile.Width;
        var atlasHeight = tilesPerColumn * Tile.Height;
        var pixels = new uint[atlasWidth * atlasHeight];

        for (int tileIndex = 0; tileIndex < tiles.Count; tileIndex++)
        {
            var tile = tiles[tileIndex];
            var tileX = (tileIndex % tilesPerRow) * Tile.Width;
            var tileY = (tileIndex / tilesPerRow) * Tile.Height;

            for (int py = 0; py < Tile.Height; py++)
            {
                for (int px = 0; px < Tile.Width; px++)
                {
                    var srcIndex = py * Tile.Width + px;
                    var dstIndex = (tileY + py) * atlasWidth + (tileX + px);
                    var paletteIndex = tile.PixelData[srcIndex];
                    pixels[dstIndex] = Palette.GetColor(paletteIndex);
                }
            }
        }

        return (pixels, atlasWidth, atlasHeight);
    }

    /// <summary>
    /// Exports the tile atlas as a BMP file for visual inspection.
    /// </summary>
    public void ExportAtlasToBmp(IList<Tile> tiles, int tilesPerRow, string filename)
    {
        var (pixels, width, height) = CreateTileAtlas(tiles, tilesPerRow);

        // BMP file format (24-bit, no alpha)
        using var fs = new FileStream(filename, FileMode.Create);
        using var bw = new BinaryWriter(fs);

        int rowSize = ((width * 3 + 3) / 4) * 4; // Rows padded to 4-byte boundary
        int imageSize = rowSize * height;
        int fileSize = 54 + imageSize; // Header + pixel data

        // BMP Header (14 bytes)
        bw.Write((byte)'B');
        bw.Write((byte)'M');
        bw.Write(fileSize);
        bw.Write((short)0); // Reserved
        bw.Write((short)0); // Reserved
        bw.Write(54); // Pixel data offset

        // DIB Header (40 bytes - BITMAPINFOHEADER)
        bw.Write(40); // Header size
        bw.Write(width);
        bw.Write(height);
        bw.Write((short)1); // Color planes
        bw.Write((short)24); // Bits per pixel
        bw.Write(0); // No compression
        bw.Write(imageSize);
        bw.Write(2835); // Horizontal resolution (72 DPI)
        bw.Write(2835); // Vertical resolution
        bw.Write(0); // Colors in palette
        bw.Write(0); // Important colors

        // Pixel data (bottom-up, BGR format)
        byte[] rowBuffer = new byte[rowSize];
        for (int y = height - 1; y >= 0; y--)
        {
            for (int x = 0; x < width; x++)
            {
                uint argb = pixels[y * width + x];
                byte r = (byte)((argb >> 16) & 0xFF);
                byte g = (byte)((argb >> 8) & 0xFF);
                byte b = (byte)(argb & 0xFF);
                byte a = (byte)((argb >> 24) & 0xFF);

                // For transparent pixels, use magenta as background
                if (a == 0)
                {
                    r = 255; g = 0; b = 255;
                }

                rowBuffer[x * 3 + 0] = b;
                rowBuffer[x * 3 + 1] = g;
                rowBuffer[x * 3 + 2] = r;
            }
            bw.Write(rowBuffer);
        }

        Console.WriteLine($"Exported tile atlas to {filename} ({width}x{height}, {tiles.Count} tiles)");
    }
}
