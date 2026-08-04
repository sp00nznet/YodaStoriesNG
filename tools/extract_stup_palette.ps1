# The STUP section is a raw bitmap with embedded palette
# Format: BMP-like structure starting at offset 8 in the DTA file
# After VERS (4 bytes tag + 4 bytes data) = 8 bytes
# STUP tag (4 bytes) + length (4 bytes) + data

$bytes = [System.IO.File]::ReadAllBytes('C:\YodaStoriesNG\Yoda\yodesk.dta')

# STUP section starts at offset 8 (after VERS section)
# STUP tag at 8, length at 12, data starts at 16
$stupOffset = 16

# The STUP data is 82944 bytes which is:
# 288 x 288 pixels = 82944 bytes (no palette, just raw indexed data)
# OR it could be: palette (256 * 4 = 1024 bytes) + pixel data

# Let's check the first bytes to understand the format
Write-Output "First 64 bytes of STUP data:"
$hexDump = ""
for ($i = 0; $i -lt 64; $i++) {
    $hexDump += "{0:X2} " -f $bytes[$stupOffset + $i]
    if (($i + 1) % 16 -eq 0) { $hexDump += "`n" }
}
Write-Output $hexDump

# Check if it looks like a BMP header
$sig = [System.Text.Encoding]::ASCII.GetString($bytes[$stupOffset..($stupOffset+1)])
Write-Output "Signature check: '$sig'"

# If STUP is raw pixel data without palette, we need to find palette elsewhere
# Let's also check the tile data format - maybe palette is before tiles

# Try offset 0x0550EF from exe but with different byte interpretation
$exeBytes = [System.IO.File]::ReadAllBytes('C:\YodaStoriesNG\Yoda\yodesk.exe')
$offset = 0x0550EF

Write-Output "`nChecking exe palette at 0x0550EF with RGBA interpretation:"
# Maybe the format is RGBA not BGRA?
$result = @()
for ($i = 0; $i -lt 256; $i++) {
    $idx = $offset + ($i * 4)
    $r = $exeBytes[$idx]
    $g = $exeBytes[$idx + 1]
    $b = $exeBytes[$idx + 2]
    # ARGB format: 0xAARRGGBB
    $hex = "0xFF{0:X2}{1:X2}{2:X2}" -f $r, $g, $b
    $result += $hex
}

# Show first 16 colors
Write-Output "First 16 colors (RGBA interpretation):"
for ($i = 0; $i -lt 16; $i++) {
    Write-Output "  [$i]: $($result[$i])"
}

# Also try searching for a palette signature in the exe
# Windows palettes often start with specific patterns
Write-Output "`nSearching for palette patterns in exe..."

# Look for a block of 1024 bytes that looks like a palette
# (sequences of RGB values followed by 0x00 padding)
for ($searchOffset = 0x50000; $searchOffset -lt 0x60000; $searchOffset += 0x1000) {
    $hasColors = $false
    $hasBlack = ($exeBytes[$searchOffset] -eq 0 -and $exeBytes[$searchOffset+1] -eq 0 -and $exeBytes[$searchOffset+2] -eq 0)

    # Check if position 40 has non-zero color data (past initial black entries)
    $idx40 = $searchOffset + 160  # 40 * 4
    if ($exeBytes[$idx40] -ne 0 -or $exeBytes[$idx40+1] -ne 0 -or $exeBytes[$idx40+2] -ne 0) {
        $hasColors = $true
    }

    if ($hasBlack -and $hasColors) {
        Write-Output "Potential palette at offset 0x$($searchOffset.ToString('X')):"
        for ($i = 0; $i -lt 4; $i++) {
            $pidx = $searchOffset + ($i * 4)
            Write-Output "  Color $i : R=$($exeBytes[$pidx]) G=$($exeBytes[$pidx+1]) B=$($exeBytes[$pidx+2])"
        }
    }
}
