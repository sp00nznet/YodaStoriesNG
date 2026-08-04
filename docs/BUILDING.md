# Building and running

## What you need

| | |
|---|---|
| **.NET SDK 8.0** | The only hard requirement. `dotnet --version` should print `8.x`. |
| **SDL2** | Comes from the `Hexa.NET.SDL2` NuGet package - native binaries included, nothing to install. |
| **Game data** | See [GAME-DATA.md](GAME-DATA.md). The build succeeds without it; the game does not. |
| **ffmpeg** | Only if you want to regenerate the docs screenshots. See [CAPTURING-SCREENSHOTS.md](CAPTURING-SCREENSHOTS.md). |

Windows, Linux and macOS are all supported. Development happens on Windows; the Linux and
macOS builds are produced by CI but are less travelled.

---

## Step 1: clone and restore

```bash
git clone https://github.com/sp00nznet/YodaStoriesNG.git
cd YodaStoriesNG
dotnet restore
```

`dotnet restore` pulls exactly one package, `Hexa.NET.SDL2`. If it fails, that is a NuGet
connectivity problem, not a project one.

## Step 2: build

The solution holds two projects. You almost always want the first.

```bash
# The game. Plays both Yoda Stories and Indiana Jones.
dotnet build src/YodaStoriesNG.Engine

# Both projects at once
dotnet build YodaStoriesNG.sln
```

A clean build prints `Build succeeded` with a handful of nullable-reference warnings. Those
are known and harmless; there are no errors.

## Step 3: run

```bash
dotnet run --project src/YodaStoriesNG.Engine
```

With no arguments the engine searches for game data in the [documented
locations](GAME-DATA.md#step-2-put-the-files-where-the-engine-looks). To be explicit:

```bash
dotnet run --project src/YodaStoriesNG.Engine -- Yoda
dotnet run --project src/YodaStoriesNG.Engine -- INDYDESK
dotnet run --project src/YodaStoriesNG.Engine -- "D:/Games/Yoda/yodesk.dta"
```

You should get a window titled **Yoda Stories NG**, a starfield, the original title art,
and an X-Wing crossing it. Press any key to start. If you get a file picker instead, the
engine did not find your data.

### Command-line flags

| Flag | Effect |
|---|---|
| *(first positional argument)* | Data file or directory to load. |
| `--diag`, `-d` | Parse the data file, print a summary, exit without opening a window. The fastest way to check that a data file is intact. |
| `--export-tiles` | Reserved for tile-atlas export. |

`--diag` on a healthy Yoda Stories file prints roughly:

```
Loaded 658 valid zones
Loaded: 2123 tiles, 658 zones, 77 characters
```

---

## Release builds

```bash
dotnet build src/YodaStoriesNG.Engine -c Release
```

Release output lands in `src/YodaStoriesNG.Engine/bin/Release/net8.0/`. Run it directly with
`dotnet YodaStoriesNG.Engine.dll` if you want to skip the `dotnet run` overhead - the
capture harness does exactly that.

## Self-contained single-file publishing

This is what CI ships. It produces one executable per platform with the runtime baked in,
so the end user installs nothing.

```bash
dotnet publish src/YodaStoriesNG.Engine \
  -c Release -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -o publish/windows/YodaStoriesNG
```

Swap `-r win-x64` for `linux-x64` or `osx-x64`. All three cross-compile from any host - the
GitLab pipeline builds every platform on one Windows runner.

`IncludeNativeLibrariesForSelfExtract` is not optional: without it the bundled SDL2 native
library is not unpacked and the game fails at startup with a `DllNotFoundException`.

The published executable looks for game data next to itself, so ship the `Yoda/` folder
alongside it - or ship neither and let the player point the file picker at their own copy.

---

## The two projects

| Project | What it is |
|---|---|
| `src/YodaStoriesNG.Engine` | **The game.** ~15k lines. Plays both `.dta` and `.daw` files, with the full renderer, script engine, world generator, UI, debug tools and bot. |
| `src/IndyNG.Engine` | A ~2k-line standalone testbed that was used to work out the Indiana Jones palette and `.daw` parsing in isolation. It renders zones and nothing else. Kept because it is a useful minimal harness for format work; **not** the way to play Indiana Jones. |

If you are here to change how the game behaves, everything you want is in the first one.
See [ARCHITECTURE.md](ARCHITECTURE.md).

---

## Continuous integration

`.gitlab-ci.yml` defines three stages - `build`, `package`, `deploy` - that run on a
Windows shell runner tagged `win10`:

1. **build** publishes both projects self-contained for `win-x64`, `linux-x64` and
   `osx-x64` into `publish/`.
2. **package** zips each platform into `YodaStoriesNG-<platform>-<version>.zip`.
3. **deploy** copies the zips to a network share.

There is no test stage, because there are no tests yet. See [ROADMAP.md](ROADMAP.md).

---

## Troubleshooting

**`DllNotFoundException: SDL2`**
The native SDL2 library did not make it next to your binary. For a normal build this means
a broken restore - `dotnet restore --force`. For a published single-file build it means
`-p:IncludeNativeLibrariesForSelfExtract=true` was omitted.

**The window opens black and nothing happens**
The data file parsed but produced no zones. Run with `--diag` and check the zone count. A
zone count of zero usually means a still-compressed or truncated data file - see
[GAME-DATA.md](GAME-DATA.md#sanity-check-what-you-extracted).

**`Note: No game data file found`**
Expected when you have not supplied data yet. The engine will offer a file picker. See
[GAME-DATA.md](GAME-DATA.md).

**Debug windows open behind the game window**
They are separate OS windows, by design - each one owns its own SDL window and renderer, so
you can put the map on a second monitor. Alt-Tab to them.
