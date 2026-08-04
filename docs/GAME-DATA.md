# Supplying the game data

This project ships **no game content**. It is an engine: it reads the data files from a
copy of the original game that you own, and reimplements the code that reads them. Without
those files it will start, fail to find data, and offer you a file picker.

You need one of:

| Game | File | Approx. size |
|---|---|---|
| Star Wars: Yoda Stories (1997) | `YODESK.DTA` | 4.6 MB |
| Indiana Jones and His Desktop Adventures (1996) | `DESKTOP.DAW` | 2.3 MB |

Yoda Stories is the better-supported of the two - see [the caveats](#indiana-jones-caveats).

---

## Step 1: get the files off your original media

The data file sits next to the game executable in the installed game directory, or in the
installer payload on the CD. Where exactly depends on how you own the game.

### From an installed copy

Look in the install directory. You want the whole folder, not just the one file - the
engine also reads the `sfx/` sounds and `bitmaps/` from alongside it.

```
Yoda/
├── yodesk.dta          <- the one that matters
├── Yodesk.exe
├── sfx/
└── bitmaps/
```

### From the CD or an installer

The installer payload stores files **SZDD-compressed** - a Microsoft `COMPRESS.EXE` format
from the DOS era. A compressed file has an underscore as the last character of its
extension (`YODESK.DT_`) and starts with the magic bytes `SZDD`.

Two decompressors ship in `tools/`:

```bash
# Python, standard library only
python tools/decompress_szdd.py YODESK.DT_ yodesk.dta
```

`tools/DecompressSzdd.cs` is the same algorithm in C#, kept as a single file with its own
`Main` so it can be dropped into a scratch console project if you would rather not have
Python. It is not part of the solution build.

Simplest of all: Microsoft's own `EXPAND.EXE`, still shipped with Windows, reads the format
natively.

```
expand YODESK.DT_ yodesk.dta
```

### Sanity-check what you extracted

The first four bytes of a valid file are the ASCII tag `VERS`:

```bash
# Should print: VERS
head -c 4 yodesk.dta
```

```powershell
# PowerShell equivalent
[System.Text.Encoding]::ASCII.GetString([byte[]](Get-Content yodesk.dta -Encoding Byte -TotalCount 4))
```

If you see `SZDD` instead, the file is still compressed - go back and decompress it.

---

## Step 2: put the files where the engine looks

Create the folders in the repository root:

```
YodaStoriesNG/
├── Yoda/
│   └── yodesk.dta
└── INDYDESK/
    └── DESKTOP.DAW
```

Both folders are in `.gitignore`. They will never be committed, which is the point.

The engine searches these locations in order, and uses the first hit
(`src/YodaStoriesNG.Engine/Program.cs`):

1. `<executable dir>/Yoda/yodesk.dta`
2. `<executable dir>/../../../../../Yoda/yodesk.dta` - i.e. the repo root when running
   from `bin/Debug/net8.0/`
3. `C:\YodaStoriesNG\Yoda\yodesk.dta`
4. `./Yoda/yodesk.dta` relative to the working directory
5. the same five patterns again for `desktop.daw`, under `Indy/`, `ida/` and `INDYDESK/`

## Step 3: or just point at it

Any of these work and skip the search entirely:

```bash
# A directory containing yodesk.dta or desktop.daw
dotnet run --project src/YodaStoriesNG.Engine -- "D:/Games/Yoda Stories"

# A file, directly
dotnet run --project src/YodaStoriesNG.Engine -- "D:/Games/Yoda Stories/yodesk.dta"
dotnet run --project src/YodaStoriesNG.Engine -- "D:/Games/Indy/DESKTOP.DAW"
```

You can also switch data files at runtime: **Config → Select Data File**.

---

## How the engine decides which game it loaded

The file **name** decides, not the contents. `DetectGameType` in
`src/YodaStoriesNG.Engine/Parsing/DtaParser.cs` reads Indiana Jones from a name containing
`DESKTOP` or ending in `.DAW`, and Yoda Stories otherwise.

This is worth spelling out because it used to be done the other way round. Both files carry
a `VERS` section, and it is tempting to read Indiana Jones out of a version of 1.x. Retail
`DESKTOP.DAW` reports **2.0**, exactly like Yoda Stories does, so that heuristic silently
loaded every Indiana Jones game as a Yoda Stories one - wrong palette animation cycles,
wrong vehicle on the title screen. The version is now logged and otherwise ignored.

If you rename your data file to something exotic, name it with a `.daw` extension for
Indiana Jones and a `.dta` extension for Yoda Stories.

---

## Indiana Jones caveats

Indiana Jones loads, parses, renders and plays with the correct palette and animation
cycles. What it does not do yet:

- **World generation produces no mission chain.** The generator is written around Yoda
  Stories' Dagobah-and-planets structure and logs `No Dagobah zones found!` on a
  `.daw` world. You get a browsable world of real zones, but no puzzle sequence to solve.
- **The mission bot cannot play it**, for the same reason.
- **The title screen has no biplane.** The X-Wing flyby is driven by four known tile IDs
  (948-951); the equivalent biplane tiles have not been identified, so Indiana Jones gets a
  static title screen. See `src/YodaStoriesNG.Engine/UI/TitleScreen.cs`.

See [ROADMAP.md](ROADMAP.md) for where that work sits.

---

## Legal

You must own a legal copy of the game whose data you use. This repository contains no
LucasArts content, and the MIT licence on the source code grants you no rights to any.
