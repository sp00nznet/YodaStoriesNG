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
}
