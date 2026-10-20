# Playing

A guide to the game as this engine implements it: how to control it, what the fifteen
missions are, how scoring works, and where your saves go.

If you have never played a Desktop Adventure: it is a small top-down action-adventure that
generates a fresh world every time. You wander a grid of screens, solve item-trade puzzles
with the people you meet, hit things that need hitting, and finish a mission. Then it
generates another one.

---

## Your first game, step by step

1. **Start it.** `dotnet run --project src/YodaStoriesNG.Engine`
2. **Dismiss the title screen.** Any key, or click.
3. **Look at the sidebar on the right.** Health at the top, current weapon in the middle,
   your eight-by-seven inventory grid below. It is empty. It will not stay empty.
4. **Press `O`.** The current objective prints as a message - what you are looking for and
   roughly where.
5. **Walk.** `WASD` or the arrow keys. One tap is one tile. Enable **Config → Smooth Movement** to glide between tiles and move diagonally.
6. **Press `F` if the zone is dull.** It jumps you to the next zone that actually has NPCs
   or items in it. This is a debug shortcut, and it is the fastest way to see the game
   working.
7. **Walk into a person to talk to them.** They will tell you what they want. If you have
   it, they trade. That trade is the puzzle.
8. **Press `Space` to use the selected item or swing the selected weapon.** Direction
   comes from the way you are facing.
9. **Press `1`-`8` to pick an inventory slot**, or click a slot directly. Mouse wheel over
   the sidebar scrolls a long inventory.
10. **Find the X-Wing and press `X`** to travel between the starting swamp and the mission
    planet. Indiana Jones uses a different vehicle in the same role.
11. **Stuck?** Press `O` again, or - once you have the R2-D2 locator droid - let R2 tell
    you where to go next.

![R2-D2 giving a hint](shots/r2d2-help.png)

---

## Movable puzzle objects

If you move blocks, boulders, or other puzzle objects and leave an **unsolved** area,
the area returns to its initial layout and local puzzle state. Re-enter to try again.
Solved areas retain their completed state. Collected items stay in your inventory and
are not recreated by the reset.

## Smooth movement

Enable **Config → Smooth Movement**, or **Smooth Movement** in the controller pause menu.
The choice is remembered between launches and is off by default. It applies to both the
player and enemies. Hold movement keys or the D-pad to keep walking; two directions move
diagonally, and the analog stick controls walking speed. Releasing input finishes the
current tile step. Diagonals have the same overall speed and cannot cut through corners.

Pushing, pulling, boarding, and entering doors use a cardinal approach. Actions requested
while walking happen at the next tile center. A mode change takes effect when active
steps finish. Saves keep committed tile positions, so loading starts at a tile center.

## Keyboard

### Playing

| Key | Action |
|---|---|
| `W` `A` `S` `D` / arrow keys | Move one tile. Walking into a pushable block pushes it. |
| `Shift` + direction | Pull an adjacent block by stepping away from it. For a block on your right, hold `Shift` and move left. |
| `Space` | Use the selected item, talk to whoever is in front of you, or attack. |
| `Tab` | Cycle to the next weapon. |
| `1`-`9`, `0` | Select inventory slot 1-10, relative to the current scroll position. |
| `[` / `]` , `PgUp` / `PgDn` | Scroll the inventory list. |
| `O` | Print the current mission objective. |
| `X` | Board the X-Wing and travel. Only works where a vehicle is parked. |
| `M` | Mute or unmute sound. |
| `R` | Abandon this world and generate a new one. |
| `Esc` | Quit. |

### Debug and tools

| Key | Action |
|---|---|
| `F1` | Debug overlay (arrow keys change tab and scroll, `Esc` closes). |
| `F2` | Map Viewer window. |
| `F3` | Script Editor window. |
| `F4` | Asset Viewer window. |
| `F5` / `F6` / `F7` | Window scale 1x / 2x / 4x. |
| `F8` | Save Game Editor window. |
| `F9` | Zone Editor window. |
| `B` | Toggle the mission bot - it plays for you. |
| `F` | Jump to the next zone containing NPCs or items. |
| `N` / `P` | Next / previous zone by ID. |
| `I` | Dump the full game state to the console. |

All of these are covered in detail in [DEBUG-TOOLS.md](DEBUG-TOOLS.md).

Bindings are stored in `%APPDATA%/YodaStoriesNG/keybindings.json`
(`~/.config/YodaStoriesNG/` on Linux and macOS) and can be edited there.

---

## Controller

Any SDL2-recognised gamepad works; the layout is written for an Xbox pad. Plug it in at any
time - the engine picks it up and says so on screen.

| Input | Action |
|---|---|
| D-pad | Move |
| Left stick | Move (analogue, repeats while held) |
| `A` | Use item / talk / attack |
| `B` | Dismiss dialogue |
| `X` | Travel |
| `Y` | Show objective |
| `LB` / `RB` | Cycle weapon |
| `Start` / `Back` | Pause menu |
| Any button | Start the game from the title screen |

**The pause menu** is the controller's way into everything the menu bar does. It pauses
the game and offers Resume, Save and Load, New Game, and toggles for world size, graphics
scale, sound, the mission bot and the menu bar, plus Controller Controls, High Scores and
Quit. The
D-pad or left stick moves, `A` picks, `B` or `Start` closes it. The arrow keys, `Enter`
and `Esc` work too.

![The pause menu over a live game](shots/pause-menu.png)

There is no controller binding for the debug tools. Use the keyboard.

---

## The menu bar

Every platform gets the same four menus.

| Menu | Contents |
|---|---|
| **File** | New Game at each of the four world sizes; Save Game; Save As...; Load Game; Exit |
| **Debug** | Asset Viewer (F4), Script Editor (F3), Map Viewer (F2), Save Editor (F8), Zone Editor (F9), Enable/Disable Bot |
| **Config** | Graphics scale 1x/2x/4x, Keyboard Controls, Controller Controls, Select Data File... |
| **About** | About, High Scores |

Windows draws them with a real native menu bar, outside the window at the OS's own DPI
(`UI/NativeMenuBar.cs`). That is user32, which Linux and macOS do not have, so there they
are drawn by SDL into a strip the window reserves above the game (`UI/MenuBar.cs`) - it
takes no gameplay area, and the menus behave the same way: click a title to open it, drag
along the bar to move between them, `Esc` or a click elsewhere to dismiss. The keyboard
works too, as it does on Windows: `F10` opens the bar, the arrows move through it, `Enter`
picks an item.

**Playing with a controller?** Config -> Hide Menu Bar puts the bar away on every
platform, and on Linux and macOS the window shrinks to just the game. Everything it offered
is in the controller's pause menu, which also has a Menu Bar toggle; `F10` brings it back
too. The choice is remembered.

**No GPU is fine.** A machine with no accelerated render driver - a VM, remote X, a bare
Linux box - gets SDL's software renderer instead of a window that refuses to open.

**Save As, Load Game and Select Data File need a file picker**, and off Windows the game
asks the desktop for its own: `osascript` on macOS, `zenity` or `kdialog` on Linux,
whichever is installed. On a machine with neither, those three items say so and do
nothing - Save Game still works, since it writes `quicksave.ysng` without asking, and
the data file can always come from the command line:

```
./YodaStoriesNG.Engine /path/to/YODESK.DTA
```

That path is matched case-insensitively, so `YODESK.DTA` straight off the disc is found on
a case-sensitive filesystem.

---

## World sizes

Chosen from **File → New Game**. Size changes the grid, the number of puzzles you must
chain together, and the scoring curve.

| Size | Grid | Puzzles | Feel |
|---|---|---|---|
| Small | 10x10 | 4-8 | Twenty minutes. |
| Medium | 10x10 | 6-12 | The default. |
| Large | 10x10 | 8-16 | A proper session. |
| X-tra Large | 15x15 | 12-24 | All fifteen missions on one map. |

How that grid becomes a world is [WORLD-GENERATION.md](WORLD-GENERATION.md).

---

## The fifteen-mission cycle

A full run is fifteen missions. Each one picks a goal puzzle from the game's puzzle table,
works backwards to build a chain of item trades that reaches it, and scatters that chain
across the world. Finishing a mission advances a counter that persists across new worlds
until you complete all fifteen, at which point you get scored.

Missions are drawn without replacement, so a full cycle shows you fifteen distinct goals
rather than the same three repeatedly.

---

## Scoring

![The score screen](shots/score-screen.png)

At the end of fifteen missions you get a **Force Factor** (Yoda Stories) or **Indy
Quotient** (Indiana Jones) out of 500, from four components:

| Component | Max | How it is earned |
|---|---|---|
| Time Bonus | 200 | Full marks under `5 x world-size-number` minutes, then -20 per further minute. Medium gives you 10 free minutes. |
| Puzzles Solved | 100 | Percentage of the world's puzzle sectors you actually solved. |
| Difficulty | 100 | Currently mirrors Puzzles Solved. |
| Exploration | 100 | Zones visited against `world-size-number x 10` expected. |

The exact arithmetic is `GameState.CalculateScore()` in
`src/YodaStoriesNG.Engine/Game/GameState.cs`.

| Score | Yoda Stories | Indiana Jones |
|---|---|---|
| 450+ | Legendary Hero! | Master Archaeologist! |
| 400-449 | Jedi Master | Professor of Antiquities |
| 350-399 | Jedi Knight | Seasoned Explorer |
| 300-349 | Padawan | Field Researcher |
| 250-299 | Force Sensitive | Museum Curator |
| 200-249 | Adventurer | Graduate Student |
| below 200 | Beginner | Amateur |

Scores are kept in `%APPDATA%/YodaStoriesNG/highscores.json` and shown under
**About → High Scores**.

---

## Saving

Saves are JSON, extension `.ysng`, written to:

| Platform | Location |
|---|---|
| Windows | `%APPDATA%\YodaStoriesNG\saves\` |
| Linux | `~/.config/YodaStoriesNG/saves/` |
| macOS | `~/.config/YodaStoriesNG/saves/` |

**File → Save Game** writes `quicksave.ysng`. **Save As...** lets you name it.

A save carries the whole world, not just your position: player state and inventory, the
generated world map and its zone connections, mission progress and the puzzle chain, every
zone variable and counter, and the set of objects you have already collected. Loading a
save restores the world exactly - it does not regenerate it.

Because it is plain JSON you can open a save in any text editor. Or use the built-in editor
(`F8`), which is friendlier and can add items to your inventory - see
[DEBUG-TOOLS.md](DEBUG-TOOLS.md#save-game-editor-f8).

![The save game editor](shots/save-editor.png)
