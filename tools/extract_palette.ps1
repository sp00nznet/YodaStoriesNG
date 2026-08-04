$bytes = [System.IO.File]::ReadAllBytes('C:\YodaStoriesNG\Yoda\yodesk.exe')
$offset = 0x0550EF
$result = @()

for ($i = 0; $i -lt 256; $i++) {
    $idx = $offset + ($i * 4)
    $b = $bytes[$idx]
    $g = $bytes[$idx + 1]
    $r = $bytes[$idx + 2]
    # ARGB format: 0xAARRGGBB
    $hex = "0xFF{0:X2}{1:X2}{2:X2}" -f $r, $g, $b
    $result += $hex
}

# Output in C# array format
Write-Output "// Palette extracted from yodesk.exe at offset 0x0550EF"
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
