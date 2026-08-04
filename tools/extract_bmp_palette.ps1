$bytes = [System.IO.File]::ReadAllBytes('C:\YodaStoriesNG\Yoda\bitmaps\screen.bmp')

# BMP header structure:
# Offset 0: 'BM' signature
# Offset 10: Pixel data offset (4 bytes, little-endian)
# Offset 14: DIB header size (4 bytes)
# Offset 28: Bits per pixel (2 bytes)
# For 8-bit BMP, palette starts at offset 54 (14 + 40 for BITMAPINFOHEADER)

$signature = [System.Text.Encoding]::ASCII.GetString($bytes[0..1])
Write-Output "BMP Signature: $signature"

$pixelOffset = [BitConverter]::ToInt32($bytes, 10)
Write-Output "Pixel data offset: $pixelOffset"

$dibHeaderSize = [BitConverter]::ToInt32($bytes, 14)
Write-Output "DIB header size: $dibHeaderSize"

$bitsPerPixel = [BitConverter]::ToInt16($bytes, 28)
Write-Output "Bits per pixel: $bitsPerPixel"

if ($bitsPerPixel -eq 8) {
    # Palette starts after the DIB header (at offset 14 + dibHeaderSize)
    $paletteOffset = 14 + $dibHeaderSize
    Write-Output "Palette offset: $paletteOffset"

    # BMP palette is in BGRA format (Blue, Green, Red, Reserved/Alpha)
    $result = @()
    for ($i = 0; $i -lt 256; $i++) {
        $idx = $paletteOffset + ($i * 4)
        $b = $bytes[$idx]
        $g = $bytes[$idx + 1]
        $r = $bytes[$idx + 2]
        # ARGB format: 0xAARRGGBB
        $hex = "0xFF{0:X2}{1:X2}{2:X2}" -f $r, $g, $b
        $result += $hex
    }

    # Output in C# array format
    Write-Output ""
    Write-Output "// Palette extracted from screen.bmp"
    Write-Output "public static readonly uint[] Colors = new uint[256]"
    Write-Output "{"

    for ($row = 0; $row -lt 16; $row++) {
        $line = "    "
        for ($col = 0; $col -lt 16; $col++) {
            $idx = $row * 16 + $col
            $line += $result[$idx]
            if ($idx -lt 255) { $line += ", " }
        }
        Write-Output $line
    }

    Write-Output "};"
} else {
    Write-Output "Not an 8-bit BMP file"
}
