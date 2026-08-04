# World generation

Every new game builds a fresh world. Not a fresh arrangement of pre-made levels - a fresh
*puzzle*, chained backwards from a goal so that it is always solvable and always requires
crossing most of the map to solve.

It happens in two passes with a clean seam between them:

```
MapGenerator     -> a grid of SECTOR TYPES     ("puzzle here, blockade there, island there")
WorldGenerator   -> real ZONES and a PUZZLE CHAIN bound to that grid
```

`MapGenerator` knows nothing about the game's content - it shapes space. `WorldGenerator`
knows nothing about grid growth - it fills that shape with zones the data file actually
contains, and works out what you must do in what order.

![The generated world in the map viewer](shots/world-map.png)

---

## Pass one: the sector grid

`src/YodaStoriesNG.Engine/Game/MapGenerator.cs`. Seeded, so the same seed gives the same
layout, though the seed is not stored - a save keeps the finished world instead.

### The grid and its distance table

10x10 for Small, Medium and Large; 15x15 for X-tra Large. Before anything is placed, a
**distance table** assigns every cell a ring number measured from the four centre cells:

```
distance = max(|x - centreX|, |y - centreY|) + 1,  capped at (width + 1) / 2
```

so the centre cells are ring 1, the ones around them ring 2, and so on. Growth happens ring
by ring, and this table is what makes it possible.

```
4 4 4 4 4 4 4 4 4 4
4 3 3 3 3 3 3 3 3 4
4 3 2 2 2 2 2 2 3 4
4 3 2 1 1 1 1 2 3 4      <- the spaceport goes in one of the four 1s
4 3 2 1 1 1 1 2 3 4
4 3 2 2 2 2 2 2 3 4
4 4 4 4 4 4 4 4 4 4
```

### Step 1: counts

Before placement, two rolls decide the world's shape:

- **0 to 2 travel points** - vehicle routes to isolated islands.
- **0 to 3 blockades** - one-way barriers that need a specific item to pass.

Then a random number is deliberately drawn and discarded, to stay in step with the
original's generator.

### Step 2: the spaceport

Placed in one of the four centre cells. It is where you land, where the X-Wing waits, and
the origin of every distance calculation that follows.

### Step 3: grow outward, ring by ring

`DeterminePuzzleLocations(ring, count)` runs three times, at rings 2, 3 and 4, each placing
a number of sectors drawn from a per-size range. Then `DetermineAdditionalPuzzleLocations`
adds a final batch at the edges.

| Size | Ring 2 | Ring 3 | Ring 4 | Edges |
|---|---|---|---|---|
| Small | 5-8 | 4-6 | 1 | 1 |
| Medium | 5-9 | 5-9 | 4-8 | 3-8 |
| Large | 6-12 | 6-12 | 6-11 | 4-11 |
| X-tra Large | 10-18 | 10-18 | 10-16 | 8-16 |

The ring-2 figure also absorbs the travel and blockade counts, and is capped at 12.

Because placement is outward-only, the map is connected by construction: every sector is
adjacent to one placed before it, so there are no islands except the ones deliberately made.

### Step 4: islands

`PlaceIslands` builds one isolated region per travel point placed. An island cannot be
walked to - only flown to - which is what makes the vehicle a real key rather than a
convenience.

### Step 5: ordering

`PlaceIntermediateWorldThing` fixes which candidate cells become real puzzles and assigns
each an **order index**. That index is the solve order, and it is what turns a set of rooms
into a sequence.

### Sector types

| Type | Meaning |
|---|---|
| `Spaceport` | Landing zone. Start here; the X-Wing is parked here. |
| `Puzzle` | Holds a step of the puzzle chain. |
| `Candidate` | Could have held one. Did not. |
| `Empty` | Ordinary walkable zone. |
| `BlockNorth` / `South` / `East` / `West` | Blocks that exit until you hold the right item. |
| `TravelStart` / `TravelEnd` | The two ends of a vehicle route. |
| `Island` | Only reachable by travel. |
| `KeptFree` | Reserved. No zone placed. |

---

## Pass two: zones and the puzzle chain

`src/YodaStoriesNG.Engine/Game/WorldGenerator.cs`.

### Step 1: pick a mission

A goal puzzle is drawn from the `PUZ2` table (205 puzzles in Yoda Stories: 73 Quest, 74
Transport, 43 Trade, 15 Use). Used goals are tracked, so a full fifteen-mission cycle gives
you fifteen distinct ones rather than the same handful.

The mission's puzzle determines the **planet**, and therefore the tile set: Desert
(Tatooine), Snow (Hoth), Forest (Endor) or Swamp (Dagobah).

### Step 2: build Dagobah

The starting area is fixed rather than generated. It is where you begin, where you return
between missions, and where R2-D2 waits to be collected.

### Step 3: fill the grid with real zones

Each placed sector needs an actual zone from the data file, matching the sector's type and
the mission's planet, of the right size, and not already used. This is a filtered pick over
all 658 zones, and it is where generation can fail on Indiana Jones data - the filters are
written around Yoda Stories' planet structure. See
[GAME-DATA.md](GAME-DATA.md#indiana-jones-caveats).

### Step 4: chain the puzzles backwards

This is the interesting part, and it runs *after* the grid exists, so it can bind each step
to a zone that is really there.

Starting from the goal, `BuildPuzzleChain` works backwards. The goal needs an item; that
item is the reward of another puzzle; that puzzle needs an item of its own; and so on until
a step needs nothing you do not already have.

```
GOAL: give <artifact> to the NPC in zone 412
  <- <artifact> is the reward of the trade in zone 87, which needs <medallion>
    <- <medallion> is the reward of the quest in zone 233, which needs <key>
      <- <key> is lying in a crate in zone 91
```

Played forwards that is: find the key, do the quest, do the trade, finish the mission. The
chain is generated backwards precisely so that it can never be unsolvable.

Blockades slot into the same mechanism: a blockade's required item is a reward from a step
that is reachable without crossing it.

### Step 5: guarantee a weapon

Yoda Stories places **The Force** (tile 511) two sectors from the start, guaranteeing a
lightsaber early. Indiana Jones gets an equivalent weapon cache near the start. Without
this, a hostile world can be unwinnable through no fault of the player.

### Step 6: place the item chain

`SetupItemChain` distributes the chain's items into the zones that need them - crates, NPC
inventories, floor drops - so that each is where the puzzle expects it.

---

## Watching it happen

Generation logs the whole thing to the console: the selected mission, the ASCII sector map,
the numbered puzzle chain with each step's required and reward item and the zone it landed
in.

```
[MapGenerator] Generated world: seed=1729384756, size=Medium
  Puzzles: 11, Blockades: 2, Travels: 1

=== MISSION: <name> ===
Planet: Forest
Puzzle chain (4 steps):
  1. [Quest] Need: <nothing> -> Get: Ration Pack [Zone 91]
  2. [Trade] Need: Ration Pack -> Get: Comlink [Zone 233]
  ...
```

Press `F2` for the same thing as a picture, with your position, zone types and mission
progress. `I` dumps the current zone's full state to the console. Both are in
[DEBUG-TOOLS.md](DEBUG-TOOLS.md).

---

## Related documents

- [PLAYING.md](PLAYING.md#world-sizes) - what the four sizes feel like to play
- [DATA-FORMAT.md](DATA-FORMAT.md#puz2---puzzles) - the puzzle table this draws from
- [SCRIPTING.md](SCRIPTING.md) - the IACT scripts that make a placed puzzle behave
