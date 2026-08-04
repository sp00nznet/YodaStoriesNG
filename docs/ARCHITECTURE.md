# Architecture

How the engine is put together, and why it is put together that way.

The short version: a data file is parsed once into plain C# objects, a world generator
arranges those objects into a solvable map, and a single-threaded frame loop runs input →
update → render over SDL2 forever. Everything else is detail.

---

## The shape of it

```
Program.cs                     find the data file, construct the engine, run
 └── GameEngine                the frame loop, all input, all game rules
      ├── DtaParser            .dta / .daw  ->  GameData        (once, at startup)
      ├── WorldGenerator       GameData     ->  WorldMap        (once, per new game)
      │    └── MapGenerator    the sector grid underneath it
      ├── GameState            everything a save file needs to restore
      ├── ActionExecutor       runs a zone's IACT scripts each frame
      ├── GameRenderer         GameState    ->  pixels          (every frame)
      │    ├── TileRenderer    tile id      ->  SDL texture, cached
      │    ├── Palette         256 colours + the animation cycles
      │    └── BitmapFont      the in-game text
      ├── MissionBot           optional: plays the game for you
      └── UI/*                 title screen, HUD, and six separate tool windows
```

Roughly 23,800 lines of C# across 48 files. The distribution is lopsided on purpose:

| Area | Lines | What lives there |
|---|---|---|
| `Game/` | 9,542 | Rules, world generation, save/load. `GameEngine.cs` alone is 4,900. |
| `UI/` | 6,749 | Nine separate windows, most of them debug tools, plus the title screen and HUD. |
| `Bot/` | 2,903 | The automated player. |
| `Rendering/` | 2,039 | SDL2 renderer, palette, font. |
| `Parsing/` | 920 | The whole file format. |
| `Data/` | 566 | Plain data classes and the opcode enums. |
| `Dev/` | 473 | Screenshot harness and the self-test. |
| `Debug/` | 384 | The console inspector. |
| `Audio/` | 106 | Sound playback. |

---

## Startup

`Program.Main` does three things: locate a data file, construct `GameEngine`, call `Run()`.
Data-file location is documented in [GAME-DATA.md](GAME-DATA.md); the flags are in
[BUILDING.md](BUILDING.md#command-line-flags).

`GameEngine.Initialize()` then, in order:

1. **Parses the data file.** `DtaParser.Parse` walks the tagged container and fills a
   `GameData` - tiles, zones, characters, puzzles, sounds, the title image, tile names. See
   [DATA-FORMAT.md](DATA-FORMAT.md). This is the only time the file is read.
2. **Sets the palette for the game type.** `Palette.SetGameType` swaps in the correct set of
   colour-cycling ranges; Yoda Stories and Indiana Jones animate different indices.
3. **Creates the SDL window and renderer**, 796x576 logical, and uploads the tile atlas.
4. **Builds the UI**: native menu bar (Windows only), title screen, HUD, and the debug
   windows, each of which owns its own OS window and stays closed until asked for.
5. **Shows the title screen** and waits for a key.

Starting a game runs `WorldGenerator.GenerateWorld(size)`, which is
[a whole document of its own](WORLD-GENERATION.md).

---

## The frame loop

`GameEngine.Run()`, and it is deliberately boring:

```csharp
while (_isRunning)
{
    var deltaTime = /* time since last frame */;

    ProcessInput();      // drain the SDL event queue
    Update(deltaTime);   // advance the world
    Render();            // draw it

    // sleep out the rest of a 1/60s budget
}
```

Single-threaded, fixed 60 FPS target, variable delta passed to `Update`. No fixed-timestep
accumulator, no interpolation - the original was a tile-stepped game where the hero moves a
whole tile at a time, so there is nothing to interpolate.

### ProcessInput

Events are offered to consumers in a fixed priority order, and the first one that claims an
event stops the chain:

1. `Quit` and main-window-close, handled immediately and unconditionally, before anything
   can swallow them.
2. The title screen, while it is up.
3. The menu bar.
4. Each open debug window, in turn. They filter by their own window ID, so a keypress in
   the Asset Viewer never reaches the game.
5. The game itself: `HandleKeyDown`, mouse, controller.

Controller analogue-stick movement is polled once per frame after the queue is drained,
rather than being event-driven, because a held stick has no repeat event.

### Update

Per frame: palette animation timers, NPC movement and AI, projectiles, combat and damage
flashes, message and dialogue expiry, then `ActionExecutor.Execute` over the current zone's
IACT scripts. The bot, if running, gets a slice here too.

### Render

```
clear
  zone tiles, three layers                (GameRenderer.RenderZone)
  the parked X-Wing, zone items, NPCs, the player, projectiles
  HUD sidebar: health, weapon, inventory
  messages and dialogue
  debug overlay, if F1 is up
  menu bar
present
  then each open tool window renders and presents itself
```

The main window presents *before* the tool windows draw, because each tool window is a
separate SDL renderer with its own present. They are not sub-views.

---

## Rendering

**Everything is one 32x32 indexed-colour tile.** Floors, walls, items, characters, the
X-Wing, the HUD icons. A tile is 1,024 bytes of palette indices. `TileRenderer` converts a
tile to an SDL texture on first use and caches it; a typical world touches a few hundred of
the 2,123 tiles.

**Zones are three layers deep.** Floor, object, roof. `Zone.TileGrid` is
`[width, height, 3]`, and the renderer draws them bottom-up so a roof tile can cover a
player standing under it.

**The palette animates by rotation.** `Palette` keeps an immutable `OriginalColors[256]` and
a mutable working `Colors[256]`. Named index ranges - water, lava, fire, forest, ice - are
rotated within themselves on a timer: 150 ms for the fast cycles, 300 ms for the slow ones.
Rotating a colour range is the entire effect; no tile data changes, no textures are
re-uploaded except the ones that use animated indices.

![Water cycling in a swamp zone](shots/palette-animation.gif)

Yoda Stories and Indiana Jones use different index ranges, which is why loading a `.daw`
as if it were a `.dta` used to produce colours that pulsed in the wrong places.

**The window is 796x576 logically**, of which 576x576 is the 18x18-tile play area at 2x
scale and 220 is the HUD sidebar. `SDL_RenderSetLogicalSize` handles scaling, so switching
between 1x, 2x and 4x resizes the OS window and changes nothing else in the code.

---

## Scripts

Zone behaviour is data, not code. Every zone carries a list of IACT actions, each a set of
conditions and a set of instructions; if every condition passes, every instruction runs.
`ActionExecutor` implements all 38 instruction opcodes and all 37 condition opcodes the
original uses.

The full reference is [SCRIPTING.md](SCRIPTING.md). It is worth reading before touching the
parser - the fixed five-argument layout of a script item is the single easiest thing in this
codebase to get subtly wrong.

![The script editor](shots/script-editor.png)

---

## State and saving

`GameState` is the boundary: if it is in `GameState`, it survives a save; if it is not, it
is derived and will be rebuilt. Player position and health, inventory and weapons, zone
variables and counters, solved and visited zone sets, mission progress, collected-object
keys.

`SaveGameManager` serialises `GameState` plus the generated `WorldMap` to JSON. Loading
restores the world rather than regenerating it, so a seed is not enough and is not stored.

---

## Where the seams are

A few deliberate choices that shape everything else:

- **`GameEngine` is one big class.** 4,900 lines holding the loop, input, rules, and
  orchestration. It is the obvious refactor and it has not been done, because every split so
  far has produced two classes that both need the same private state. The capture harness
  attaches to it as a `partial` for exactly this reason.
- **Tool windows are real OS windows.** More SDL bookkeeping than an in-game overlay, but
  you can put the map on a second monitor while you play, and a crash in a tool window does
  not take the game's renderer with it.
- **Parsing happens once, eagerly, into plain objects.** No lazy loading, no streaming. The
  biggest data file is 4.6 MB; the whole thing fits in memory with room to spare, and the
  simplicity is worth more than the megabytes.
- **The bot uses the same public entry points a player does.** It is a client of the engine,
  not a special mode inside it, which keeps it honest about what is actually reachable.

---

## Related documents

- [DATA-FORMAT.md](DATA-FORMAT.md) - what `DtaParser` reads
- [SCRIPTING.md](SCRIPTING.md) - what `ActionExecutor` runs
- [WORLD-GENERATION.md](WORLD-GENERATION.md) - what `WorldGenerator` builds
- [DEBUG-TOOLS.md](DEBUG-TOOLS.md) - the windows in `UI/`
- [CAPTURING-SCREENSHOTS.md](CAPTURING-SCREENSHOTS.md) - what `Dev/` is for
