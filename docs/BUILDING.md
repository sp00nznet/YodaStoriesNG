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
  -p:IncludeAllContentForSelfExtract=true \
  -o publish/windows/YodaStoriesNG
```

Swap `-r win-x64` for `linux-x64`, `osx-x64` or `osx-arm64`. All four cross-compile from any
host, which is why CI builds every platform on a single Linux runner.

**Both self-extract properties are load-bearing.** This is the one non-obvious thing about
publishing this project.

With only `IncludeNativeLibrariesForSelfExtract`, you get a binary that looks fine: it
starts, finds your data file, parses all 2,123 tiles and 658 zones - and then dies on the
first SDL call with

```
Error: The type initializer for 'Hexa.NET.SDL2.SDL' threw an exception.
```

`Hexa.NET.SDL2` resolves its native library from disk rather than from the bundle, so SDL2
has to be present in the single-file extraction directory. `IncludeAllContentForSelfExtract`
is what puts it there. Omit it and every release archive you publish will be broken in a way
that a smoke test of "does it parse" will not catch.

Test a packaged build with the flag that needs neither data nor a display:

```bash
./YodaStoriesNG.Engine --self-test
```

and then with a real data file, which exercises SDL properly:

```bash
./YodaStoriesNG.Engine /path/to/Yoda --diag
```

`--diag` creates the window, the renderer and the tile atlas before exiting, so it fails
loudly if the native library is missing.

The published executable looks for game data next to itself, so ship the `Yoda/` folder
alongside it - or ship neither and let the player point the file picker at their own copy.

---

## The two projects

| Project | What it is |
|---|---|
| `src/YodaStoriesNG.Engine` | **The game.** ~23,800 lines. Plays both `.dta` and `.daw` files, with the full renderer, script engine, world generator, UI, debug tools and bot. |
| `src/IndyNG.Engine` | A ~2,000-line standalone testbed that was used to work out the Indiana Jones palette and `.daw` parsing in isolation. It renders zones and nothing else. Kept because it is a useful minimal harness for format work; **not** the way to play Indiana Jones. |

If you are here to change how the game behaves, everything you want is in the first one.
See [ARCHITECTURE.md](ARCHITECTURE.md).

---

## Continuous integration and releases

`.github/workflows/build.yml`, two jobs.

**`check`** runs on every push to `main`, every pull request, and on demand. It restores,
builds the solution in Release, and runs the self-test:

```bash
dotnet run --project src/YodaStoriesNG.Engine -- --self-test
```

That check needs no game data and opens no window, so it runs on a bare runner. It asserts
the IACT binary layout round-trips - see
[SCRIPTING.md](SCRIPTING.md#the-five-argument-trap) for why that is the thing worth
guarding.

**`release`** runs only for a `v*` tag, after `check` passes. It publishes both projects
self-contained for all four runtime identifiers, zips each with the README, licence and
data-setup guide, and creates a GitHub Release with the four archives attached.

### Cutting a release

```bash
git tag v0.2.0
git push origin v0.2.0
```

That is the whole procedure. Version numbers come from the tag: `v0.2.0` produces
`YodaStoriesNG-win-x64-0.2.0.zip` and friends. Delete the tag and the release to redo one.

> Releases contain the engine only. They must never contain game data - see
> [GAME-DATA.md](GAME-DATA.md#legal).

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
