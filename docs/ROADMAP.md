# Roadmap

What is done, what is broken, what is next, and what is deliberately not being done.

---

## Done

The original roadmap was a list of WebFun-style development tools. All of it shipped:

- Save game inspector and editor (`F8`)
- Zone editor with tile placement (`F9`)
- Asset viewer with flag filtering (`F4`)
- Script editor with disassembly, world highlighting and teleport (`F3`)
- Map viewer (`F2`), debug overlay (`F1`), console inspector (`I`)
- Zone teleportation, item spawning through the save editor, a debug menu

Beyond that: both games parse and play, all 36 conditions and 38 instructions are
implemented, palette animation is authentic, there is a mission bot, a score system, save
and load, controller support, a native menu bar, and a screenshot harness that regenerates
every image in these docs from a real run.

---

## Known problems

Ordered by how much they actually matter.

### Indiana Jones has no mission chain

**Where:** `src/YodaStoriesNG.Engine/Game/WorldGenerator.cs`

The generator is written around Yoda Stories' Dagobah-plus-planets structure. On a `.daw`
world it logs `No Dagobah zones found!` and produces a browsable world of real zones with no
puzzle sequence to solve. Everything else about Indiana Jones works - parsing, rendering,
palette cycling, combat.

This is the single largest gap. It also blocks the bot from playing Indiana Jones, and it is
why `gameplay-indy.png` is a picture of a place rather than of a game in progress.

### Opcodes `0x19` and `0x1A` are evaluated as the wrong condition

**Where:** `src/YodaStoriesNG.Engine/Data/Action.cs`,
`src/YodaStoriesNG.Engine/Game/ActionExecutor.cs`

`ConditionOpcode` declares `NpcIs` and `HasNpc` with the same values as `SectorCounterIs`
(`0x19`) and `SectorCounterIsLessThan` (`0x1A`). C# cannot switch on two names for one
value, so those opcodes are currently evaluated as NPC-interaction checks. Real scripts use
them as sector-counter comparisons.

Fixing it means also reconciling how sector counters are stored: this engine keys them as
`variable[arg0 + 3000]` compared against `arg1`, where the original keeps one counter per
zone compared against `arg0`. See
[SCRIPTING.md](SCRIPTING.md#known-divergences-from-the-original).

### `TNAM` parses zero tile names

**Where:** `src/YodaStoriesNG.Engine/Parsing/DtaParser.cs`

`Loaded 0 tile names` on a healthy Yoda Stories file. The section is present and the parser
runs, but the record layout is not right, so every place that would show an item's name
("Lightsaber") falls back to `Tile#510`. Cosmetic, but it makes dialogue and the debug tools
noticeably less readable.

### `Wait` does not wait

**Where:** `src/YodaStoriesNG.Engine/Game/ActionExecutor.cs`

Instruction `0x08` should suspend script execution for a tick. The executor runs an action's
instructions to completion within one frame, so scripted pauses do not pause. Visible in
cutscene-like sequences that were written expecting them.

### No biplane on the Indiana Jones title screen

**Where:** `src/YodaStoriesNG.Engine/UI/TitleScreen.cs`

The flyby animation needs four tile IDs. Yoda Stories' X-Wing is 948-951; the equivalent
biplane tiles have not been identified in the Indiana Jones atlas, so its title screen is
static. Finding them is an afternoon with the Asset Viewer.

### There are no automated tests

There is one self-check, covering the IACT binary layout because that is where the most
expensive bug lived:

```bash
dotnet run --project src/YodaStoriesNG.Engine -- --self-test
```

Nothing else is covered, and CI has no test stage. The natural next targets are the rest of
the file format and the world generator's solvability guarantee - both are pure functions
over inputs the repository can synthesise.

---

## Next

Roughly in order of value per unit of work.

1. **Fix `TNAM`.** Small, self-contained, and improves every screen that names an item.
2. **Reconcile the sector-counter opcodes.** Removes the last known scripting divergence.
3. **Generalise world generation to Indiana Jones.** The big one. It unlocks Indiana Jones
   as a game rather than a viewer, and the bot along with it.
4. **Extend the self-test to the whole container format.** `SelfTest.cs` already builds a
   data file by hand; extending it to tiles, characters and puzzles is more of the same, and
   would have caught both the IACT bug and the `TNAM` one.
5. **Add a CI test stage** once there is enough to run.
6. **Find the biplane tiles.**

## Deliberately not doing

- **A content authoring pipeline.** The Zone Editor is for understanding zones, not for
  shipping new ones. This is a reimplementation of an engine, not a level editor.
- **Networked or multiplayer anything.** The original is a single-player game that generates
  a private world.
- **A rewrite of `GameEngine.cs` for its own sake.** It is 4,900 lines and that is not
  comfortable, but every split attempted so far produced two classes sharing the same
  private state. It gets refactored when a feature needs it to be, not before.
