# Local puzzle plans

The bot can compile a puzzle's local solution in a private headless engine, then use
that solution as a sequence of instructions in the global mission solver. Local plans
do not replace the game's scripts or assign inventory, counters, dead monsters, or
random variants to make a solution work.

## Catalogue

Run these commands from the repository root after building the engine:

```sh
dotnet src/YodaStoriesNG.Engine/bin/Debug/net8.0/YodaStoriesNG.Engine.dll \
  --puzzle-catalogue Yoda/yodesk.dta /tmp/puzzles/yoda

dotnet src/YodaStoriesNG.Engine/bin/Debug/net8.0/YodaStoriesNG.Engine.dll \
  --puzzle-catalogue /tmp/desktop.dta /tmp/puzzles/indy
```

Use unpacked game data. Retail `DESKTOP.DAW` can be SZDD compressed; the existing
`tools/decompress_szdd.py` utility expands it before analysis.

Each directory contains a readable coverage report and `catalogue.json`. The JSON
records every puzzle definition and area template, mechanics, linked rooms, item
parameters, action guards and effects, and producer dependencies between actions.
Metadata references describe where an item appears in native area metadata; they
are not a proof that generation can assign that definition to every referenced area.
Dialogue and artwork are not included.

The inspected files contain:

| Game | Definitions | Areas | Scripted actions |
|---|---:|---:|---:|
| Yoda Stories | 205 | 658 | 8,714 |
| Indiana Jones | 157 | 366 | 2,825 |

These counts describe catalogue coverage, **not solved puzzles or all possible
state combinations**. Unknown opcodes are reported explicitly. Known opcodes can
still have unresolved semantics or interactions.

## Offline solutions from checkpoints

A pristine area is insufficient: a plan must bind the selected layout, generated
required/find items, inventory, counters, actors, and actual entry component.
Use a checkpoint saved by a headless run:

```sh
dotnet src/YodaStoriesNG.Engine/bin/Debug/net8.0/YodaStoriesNG.Engine.dll \
  --puzzle-plan DATA_FILE CHECKPOINT_FILE SECTOR_ID AcquireItem ITEM_ID \
  /tmp/puzzle-plans/result.json 3000 30
```

Supported goals are `AcquireItem`, `SolveSector`, and `Win`. For the latter two,
pass `-1` in the item position. The last arguments cap expanded states and wall
seconds. Search is sequential and bounded. No window, renderer, or audio is opened.

The search works on the generated sector and its associated rooms. Candidates are
authored bump/use/walk interactions, pickups, internal doors, and cardinal pushes
and pulls. Every candidate uses the existing movement/action handlers, fixed game
ticks, script interpreter, and sector puzzle logic. A successful sequence is restored
to its initial state and replayed again, comparing every state contract. Item and
sector solutions also return to their entry, so acquiring a reward in a sealed cell
does not become a reusable solution. Native `WinGame` is required for a `Win` goal.

The result distinguishes:

- `Verified`: the complete sequence and goal replayed successfully.
- `AlreadySatisfied`: the requested postcondition already holds.
- `NeedsLiveActors`: combat or moving actors require the live executor.
- `Unsupported`: the request cannot be modeled safely.
- `SearchLimit`: the budget expired; the puzzle remains unresolved.
- `NoPlan`: no sequence was found using the supported interactions. This is not
  a proof that the native puzzle is impossible.
- `ReplayMismatch`: the proposed sequence failed independent replay.

Live AI/combat is deliberately an external prerequisite. Its random choices are
isolated from the live game's random stream; actor-dependent outcomes are not
certified as deterministic local plans. Specialized combat, actor guidance, and
larger joint block arrangements can be added as solver families behind this contract.
Results also expose `Deferred` branches with the observed actor index, position,
health, and triggering interaction. They describe encountered prerequisites, not
a demand to kill every enemy in the sector. Enemy movement or a moved block changes
the state contract and therefore the applicable sequence; there is no universal
zone-number-only walkthrough.

## Global solver and reuse

When the bot is in its active mission sector with the required item available,
it first asks for a local plan. Item dependencies also ask for a local plan when
the current sector family supplies that item. The deepest item goal stays active
across room transitions, so the bot uncovers a required key before returning to
the outer reward route. Candidate ordering favors the requested item's active
hotspots; after acquisition, the search immediately checks the required retreat
before expanding unrelated interactions. The retreat still replays and must
satisfy the same state contracts. Runtime search has a small budget (192 expanded
states or 1.5 seconds). Verified steps use the ordinary bot executor. Before each
new step, the solver compares the actual state to the expected contract; a
discrepancy discards the remaining steps and replans from reality. Failed searches
are deferred until the puzzle state or entry component changes. The existing
mission/route solver remains available for unsupported families and external
prerequisites.

Successful plans are written to the bot session's `local-plans` directory. Offline
search also writes a file named with the initial-state hash beside its result report.
To supply those files to a later bot run:

```sh
YSNG_PUZZLE_PLAN_DIR=/tmp/puzzle-plans \
  dotnet src/YodaStoriesNG.Engine/bin/Debug/net8.0/YodaStoriesNG.Engine.dll \
  DATA_FILE --headless-bot --seed SEED --bot-resume CHECKPOINT_FILE --no-smooth
```

Loaded plans must match their schema, native-data fingerprint, goal, scope, and
initial state. They are independently replayed again before use. Generation,
inventory, map, actor, and doorway-return changes invalidate the corresponding
state contracts. No save-format migration is needed.

`actions.log` records `local-puzzle-plan` outcomes, budgets, and state hashes.
`--self-test` covers a real scripted trade across waits, catalogue mechanics,
block movement, unselected variants, source isolation, sealed reward arrivals,
serialization, stale-plan rejection, and global plan selection.

## Validation and limits

The self-test suite covers script signal ordering, item handoffs, world routing,
object-plan replay, actor deferral, stale-plan rejection and collision agreement.
Run `dotnet run --project src/YodaStoriesNG.Engine -- --self-test` without game data.

For integration checks, run one headless seed at a time and retain its `actions.log`,
`events.jsonl`, checkpoint and `run-summary.json`. Seed `1077899649` is a regression
case for the zone 301 object-route loop. Run it with `--smooth` to check that the
bot still executes exact tile steps while preserving the movement preference.

The planner is bounded rather than complete. Large multi-room searches can exceed
local budgets, and global prerequisite searches can still spend substantial time
planning. Live enemies and moving actors are deferred to the real simulation, then
the pending dependency is reconsidered. A catalogue entry or a successful checkpoint
replay is not proof that every generated variant has a solution.
