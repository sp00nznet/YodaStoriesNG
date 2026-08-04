<#
.SYNOPSIS
    Rebuilds every screenshot and animation in docs/ from a real run of the game.

.DESCRIPTION
    Three stages, no manual steps:

      1. Build the engine in Release.
      2. Run it twice with YSNG_CAPTURE_DIR set - once on YODESK.DTA, once on
         DESKTOP.DAW. The in-engine harness (src/YodaStoriesNG.Engine/Dev/) walks a
         fixed timeline through every screen and dumps raw BMP frames.
      3. Convert those frames to the PNGs and GIFs the README references.

    Each run drives a freshly generated procedural world, so the gameplay shots differ
    every time. That is intended - they should look like the game, not like one blessed
    save file. Re-run until you like them.

    Requires: .NET 8 SDK, ffmpeg on PATH, and both original game data directories.
    See docs/CAPTURING-SCREENSHOTS.md for what each shot is and how to add one.

.PARAMETER YodaData
    Directory containing yodesk.dta. Defaults to <repo>/Yoda.

.PARAMETER IndyData
    Directory containing desktop.daw. Defaults to <repo>/INDYDESK.

.PARAMETER KeepFrames
    Keep the intermediate BMP frames instead of deleting them. Useful when you want to
    hand-pick a different frame for a still.

.EXAMPLE
    pwsh tools/capture-shots.ps1
#>
[CmdletBinding()]
param(
    [string]$YodaData,
    [string]$IndyData,
    [switch]$KeepFrames
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
if (-not $YodaData) { $YodaData = Join-Path $repo 'Yoda' }
if (-not $IndyData) { $IndyData = Join-Path $repo 'INDYDESK' }

$docs   = Join-Path $repo 'docs'
$shots  = Join-Path $docs 'shots'
$frames = Join-Path ([System.IO.Path]::GetTempPath()) 'ysng-capture'

# Frame timing must match the harness constants in Dev/CaptureScript.cs (75 ms/frame).
$fps = 13.34

function Require-Tool($name, $hint) {
    if (-not (Get-Command $name -ErrorAction SilentlyContinue)) {
        throw "$name not found on PATH. $hint"
    }
}

Require-Tool 'dotnet' 'Install the .NET 8 SDK.'
Require-Tool 'ffmpeg' 'Install ffmpeg (winget install Gyan.FFmpeg).'

if (-not (Test-Path (Join-Path $YodaData 'yodesk.dta'))) {
    throw "No yodesk.dta in $YodaData. Pass -YodaData <dir>."
}
$haveIndy = Test-Path (Join-Path $IndyData 'desktop.daw')
if (-not $haveIndy) {
    Write-Warning "No desktop.daw in $IndyData - skipping the Indiana Jones shots."
}

# --- 1. Build -----------------------------------------------------------------
Write-Host '==> Building engine (Release)' -ForegroundColor Cyan
$project = Join-Path $repo 'src/YodaStoriesNG.Engine'
dotnet build $project -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
$dll = Join-Path $project 'bin/Release/net8.0/YodaStoriesNG.Engine.dll'

# --- 2. Capture ---------------------------------------------------------------
Remove-Item -Recurse -Force $frames -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $frames | Out-Null
$env:YSNG_CAPTURE_DIR = $frames

Write-Host '==> Capturing Yoda Stories (~45s, windows will open and close)' -ForegroundColor Cyan
dotnet $dll $YodaData | Select-String '^\[capture\]' | Select-Object -Last 1

if ($haveIndy) {
    Write-Host '==> Capturing Indiana Jones (~45s)' -ForegroundColor Cyan
    dotnet $dll $IndyData | Select-String '^\[capture\]' | Select-Object -Last 1
}

Remove-Item Env:\YSNG_CAPTURE_DIR

# --- 3. Convert ---------------------------------------------------------------
Write-Host '==> Converting to PNG / GIF' -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $shots | Out-Null

function Convert-Still($source, $destination) {
    $src = Join-Path $frames "$source.bmp"
    if (-not (Test-Path $src)) { Write-Warning "missing frame: $source"; return }
    ffmpeg -loglevel error -y -i $src (Join-Path $shots "$destination.png")
    Write-Host "    $destination.png"
}

# ffmpeg's default 256-colour quantiser bands these palettes badly; generating a
# palette from the whole sequence first keeps the original 8-bit colours exact.
function Convert-Gif($sourceDir, $destination, $width, $crop) {
    $dir = Join-Path $frames $sourceDir
    if (-not (Test-Path $dir)) { Write-Warning "missing frames: $sourceDir"; return }

    $filter = "fps=$fps"
    if ($crop) { $filter += ",crop=$crop" }
    $filter += ",scale=${width}:-1:flags=neighbor"

    $palette = Join-Path $frames "$sourceDir-palette.png"
    ffmpeg -loglevel error -y -framerate $fps -i (Join-Path $dir 'f%03d.bmp') `
        -vf "$filter,palettegen=stats_mode=full" $palette
    ffmpeg -loglevel error -y -framerate $fps -i (Join-Path $dir 'f%03d.bmp') -i $palette `
        -lavfi "$filter [x]; [x][1:v] paletteuse=dither=none" `
        -loop 0 $destination

    $kb = [math]::Round((Get-Item $destination).Length / 1KB)
    Write-Host "    $(Split-Path -Leaf $destination) (${kb} KB)"
}

# The README hero: the title screen with the X-Wing crossing it.
Convert-Gif 'yoda-hero' (Join-Path $docs 'hero.gif') 640 $null

# A still of the same screen, taken mid-flyby so the X-Wing is over the logo.
Convert-Still 'yoda-hero/f045' 'title-screen'

# Colour cycling reads best cropped to the play area - the HUD beside it never moves.
Convert-Gif 'yoda-palette' (Join-Path $shots 'palette-animation.gif') 556 '556:556:0:0'

Convert-Still 'yoda-gameplay'      'gameplay-yoda'
Convert-Still 'indy-gameplay'      'gameplay-indy'
Convert-Still 'yoda-map'           'world-map'
Convert-Still 'yoda-script-editor' 'script-editor'
Convert-Still 'yoda-assets'        'asset-viewer'
Convert-Still 'yoda-save-editor'   'save-editor'
Convert-Still 'yoda-score'         'score-screen'
Convert-Still 'yoda-r2d2-help'     'r2d2-help'

if (-not $KeepFrames) {
    Remove-Item -Recurse -Force $frames
} else {
    Write-Host "Frames kept in $frames"
}

Write-Host '==> Done.' -ForegroundColor Green
