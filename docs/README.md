# Documentation

The README is the pitch. Everything that needs more than a paragraph lives here.

## Start here

| Guide | What it covers |
|---|---|
| [GAME-DATA.md](GAME-DATA.md) | Getting `YODESK.DTA` / `DESKTOP.DAW` out of your own copy and into the right folder. **Do this first** - nothing runs without it. |
| [BUILDING.md](BUILDING.md) | Build, run, and publish on Windows, Linux and macOS, step by step. |
| [PLAYING.md](PLAYING.md) | Controls, the menu bar, saving, scoring, and what the fifteen missions actually are. |

## Going deeper

| Reference | What it covers |
|---|---|
| [ARCHITECTURE.md](ARCHITECTURE.md) | How the engine is put together: the frame loop, the renderer, where every subsystem lives and why. |
| [DATA-FORMAT.md](DATA-FORMAT.md) | The `.dta` / `.daw` container, section by section, byte by byte. |
| [SCRIPTING.md](SCRIPTING.md) | IACT scripts: the full condition and instruction opcode reference with argument layouts. |
| [WORLD-GENERATION.md](WORLD-GENERATION.md) | How a new world is grown from a sector grid into a solvable puzzle chain. |
| [DEBUG-TOOLS.md](DEBUG-TOOLS.md) | The five debug windows, the mission bot, and the console inspector. |
| [CAPTURING-SCREENSHOTS.md](CAPTURING-SCREENSHOTS.md) | The in-engine capture harness that produces every image in these docs. |
| [ROADMAP.md](ROADMAP.md) | What is missing, what is next, and what is deliberately not being done. |

## Conventions used in these docs

- Paths are relative to the repository root: `src/YodaStoriesNG.Engine/Game/GameEngine.cs`.
- Byte offsets and opcodes are hex; counts and indices are decimal.
- "The original" means the retail 1996/1997 LucasArts builds.
- "WebFun" means [cyco's WebFun](https://codeberg.org/cyco/WebFun), the reference
  implementation this project cross-checks its file-format work against. Where a format
  claim here is load-bearing, the corresponding WebFun source file is cited.
