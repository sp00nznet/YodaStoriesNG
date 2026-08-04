using System;
using System.IO;

class Program
{
    static void Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.WriteLine("Usage: DecompressSzdd input.DA_ output.DTA");
            return;
        }

        DecompressSzdd(args[0], args[1]);
    }

    static void DecompressSzdd(string inputPath, string outputPath)
    {
        using var fs = File.OpenRead(inputPath);
        using var reader = new BinaryReader(fs);

        // Read magic "SZDD"
        var magic = reader.ReadBytes(4);
        if (magic[0] != 'S' || magic[1] != 'Z' || magic[2] != 'D' || magic[3] != 'D')
        {
            Console.WriteLine($"Not an SZDD file");
            return;
        }

        // Read header
        var compMode = reader.ReadByte();
        var missingChar = reader.ReadByte();
        reader.ReadBytes(2); // padding

        var uncompressedSize = reader.ReadUInt32();
        Console.WriteLine($"Uncompressed size: {uncompressedSize}");

        // Read compressed data
        var compressed = reader.ReadBytes((int)(fs.Length - fs.Position));

        // Decompress
        var output = new MemoryStream();
        var ringBuffer = new byte[4096];
        int ringPos = 4096 - 16;

        int i = 0;
        while (i < compressed.Length && output.Length < uncompressedSize)
        {
            byte flags = compressed[i++];

            for (int bit = 0; bit < 8 && i < compressed.Length && output.Length < uncompressedSize; bit++)
            {
                if ((flags & (1 << bit)) != 0)
                {
                    // Literal byte
                    byte b = compressed[i++];
                    output.WriteByte(b);
                    ringBuffer[ringPos] = b;
                    ringPos = (ringPos + 1) & 0xFFF;
                }
                else
                {
                    // Back reference
                    if (i + 1 >= compressed.Length) break;

                    int b1 = compressed[i++];
                    int b2 = compressed[i++];

                    int offset = b1 | ((b2 & 0xF0) << 4);
                    int length = (b2 & 0x0F) + 3;

                    for (int j = 0; j < length && output.Length < uncompressedSize; j++)
                    {
                        byte b = ringBuffer[offset];
                        output.WriteByte(b);
                        ringBuffer[ringPos] = b;
                        ringPos = (ringPos + 1) & 0xFFF;
                        offset = (offset + 1) & 0xFFF;
                    }
                }
            }
        }

        File.WriteAllBytes(outputPath, output.ToArray());
        Console.WriteLine($"Decompressed {output.Length} bytes to {outputPath}");
    }
}
