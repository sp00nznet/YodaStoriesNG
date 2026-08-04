# IACT scripts

Zone behaviour is data. Every zone carries a list of **actions**; an action is a set of
**conditions** and a set of **instructions**. Each frame, for the zone the player is in, the
engine walks the actions and runs the instructions of every action whose conditions all
pass.

That is the whole model. No loops, no branches, no expressions - a flat AND of predicates
followed by a flat sequence of effects. Everything the original game does with doors,
traps, NPC trades, teleports, cutscenes and win conditions is built out of it.

```
ACTION
├── CONDITION  ZoneNotInitialized
├── CONDITION  SectorCounterIsLessThan 20
└── INSTRUCTION SetVariable (0,0,0) = 1
```

The implementation is `src/YodaStoriesNG.Engine/Game/ActionExecutor.cs`. The binary layout
is in [DATA-FORMAT.md](DATA-FORMAT.md#iact---action-scripts).

---

## Reading scripts in the game

Press `F3`. The Script Editor lists every zone that has scripts, disassembles the selected
one into readable conditions and instructions, and marks the coordinates a script touches in
the world.

![The script editor](shots/script-editor.png)

A healthy Yoda Stories file yields around 8,700 scripts across 555 zones. If you are seeing
far fewer, or seeing dialogue that reads like fragments of section names, your parser is
misaligned - see the [warning below](#the-five-argument-trap).

---

## The five-argument trap

Read this before touching anything that reads or writes IACT data.

**Every condition and every instruction carries exactly five `int16` argument slots**,
whether the opcode uses them or not. There is no argument count in the file. The slots are
followed by a `uint16` text length and that many bytes of text.

```
opcode      uint16
arg0..arg4  int16 x 5      <- always five
textLength  uint16
text        textLength bytes, ISO-8859-2
```

Reading slot 0 as an argument *count* and slot 1 as the text length is the natural mistake,
and it is a quiet one: the file still parses, opcodes still look sane, and the game still
mostly runs. What actually happens is that every argument shifts by one, text lengths come
from whatever integer sat in slot 1, and dialogue comes back as slices of the following
section headers. This engine shipped that bug for a long time - it cost about 100 zones to
stream desynchronisation and made every scripted line unreadable.

There is a check that will fail if it ever comes back:

```bash
dotnet run --project src/YodaStoriesNG.Engine -- --self-test
```

It builds a data file by hand with known values in known slots and asserts they come back
where they went in. `src/YodaStoriesNG.Engine/Dev/SelfTest.cs`.

**Argument order is per-opcode and not always obvious.** Most tile opcodes are
`(x, y, layer, tile)`. Two conditions - `TileAtIs` and `IsVariable` - put the tile or value
in slot 0 and the position in slots 1 to 3. The tables below give the real order for each.

---

## Conditions

36 opcodes, `0x00` to `0x23`. All are implemented.

| Op | Name | Arguments | Passes when |
|---|---|---|---|
| `0x00` | `ZoneNotInitialized` | - | This zone has not been initialised. True exactly once; the standard way to run one-time setup. |
| `0x01` | `ZoneEntered` | - | The hero just entered this zone. |
| `0x02` | `Bump` | x, y, tile | The hero walked into the tile at (x, y) and it is `tile`. |
| `0x03` | `PlacedItemIs` | x, y, layer, tileA, tileB | The player dropped an item onto (x, y, layer) and it matches. |
| `0x04` | `StandingOn` | x, y, tile | The hero is at (x, y) and the floor there is `tile`. |
| `0x05` | `CounterIs` | value | The zone counter equals `value`. |
| `0x06` | `RandomIs` | value | The zone random equals `value`. |
| `0x07` | `RandomIsGreaterThan` | value | Zone random > `value`. |
| `0x08` | `RandomIsLessThan` | value | Zone random < `value`. |
| `0x09` | `EnterByPlane` | - | The hero arrived by vehicle rather than on foot. |
| `0x0A` | `TileAtIs` | **tile**, x, y, layer | The tile at (x, y, layer) is `tile`. **Note the order.** |
| `0x0B` | `MonsterIsDead` | index | Monster `index` in this zone is dead. |
| `0x0C` | `HasNoActiveMonsters` | - | Every monster in this zone is dead. |
| `0x0D` | `HasItem` | tile | The inventory contains `tile`. `-1` means the zone's own puzzle item. |
| `0x0E` | `RequiredItemIs` | tile | The zone's required item is `tile`. |
| `0x0F` | `EndingIs` | tile | The current goal item is `tile`. |
| `0x10` | `ZoneIsSolved` | - | **This** zone is solved. Takes no arguments. |
| `0x11` | `NoItemPlaced` | - | The player has not dropped an item. |
| `0x12` | `HasGoalItem` | - | The inventory contains the story's goal item. |
| `0x13` | `HealthIsLessThan` | value | Hero health < `value`. |
| `0x14` | `HealthIsGreaterThan` | value | Hero health > `value`. |
| `0x15` | *unused* | - | Present in the format, never used. |
| `0x16` | `FindItemIs` | tile | The zone's find-item is `tile`. |
| `0x17` | `PlacedItemIsNot` | x, y, layer, tileA, tileB | Inverse of `PlacedItemIs`. |
| `0x18` | `HeroIsAt` | x, y | The hero is standing at (x, y). |
| `0x19` | `SectorCounterIs` | value | The zone sector-counter equals `value`. |
| `0x1A` | `SectorCounterIsLessThan` | value | Sector-counter < `value`. |
| `0x1B` | `SectorCounterIsGreaterThan` | value | Sector-counter > `value`. |
| `0x1C` | `GamesWonIs` | value | Total games won equals `value`. |
| `0x1D` | `DropsQuestItemAt` | x, y | The player dropped the quest item at (x, y). |
| `0x1E` | `HasAnyRequiredItem` | - | The inventory holds any item this zone requires. |
| `0x1F` | `CounterIsNot` | value | Zone counter != `value`. |
| `0x20` | `RandomIsNot` | value | Zone random != `value`. |
| `0x21` | `SectorCounterIsNot` | value | Sector-counter != `value`. |
| `0x22` | `IsVariable` | **value**, x, y, layer | The variable at (x, y, layer) equals `value`. Same order as `TileAtIs`. |
| `0x23` | `GamesWonIsGreaterThan` | value | Total games won > `value`. |

---

## Instructions

38 opcodes, `0x00` to `0x25`. All are implemented.

### Tiles and the map

| Op | Name | Arguments | Effect |
|---|---|---|---|
| `0x00` | `PlaceTile` | x, y, layer, tile | Put `tile` at (x, y, layer). `-1` removes. |
| `0x01` | `RemoveTile` | x, y, layer | Clear (x, y, layer). |
| `0x02` | `MoveTile` | srcX, srcY, layer, dstX, dstY | Move a tile within the zone. |
| `0x03` | `DrawTile` | 5 numbers | Draw a tile without changing zone data. |
| `0x06` | `SetTileNeedsDisplay` | x, y | Mark one tile for redraw. |
| `0x07` | `SetRectNeedsDisplay` | x, y, w, h | Mark a rectangle for redraw. |
| `0x09` | `Redraw` | - | Redraw the whole scene now. |
| `0x0F` | `SetVariable` | x, y, layer, value | Set the variable at (x, y, layer). Stored as a tile write. |

### The hero

| Op | Name | Arguments | Effect |
|---|---|---|---|
| `0x10` | `HideHero` | - | Hide the hero sprite. |
| `0x11` | `ShowHero` | - | Show it again. |
| `0x12` | `MoveHeroTo` | x, y | Teleport to (x, y) in this zone. |
| `0x13` | `MoveHeroBy` | dx, dy, absX, absY, - | Move relative, or absolute if the relative pair is zero. |
| `0x21` | `ChangeZone` | zoneId, x, y | Move to another zone at (x, y). |
| `0x25` | `AddHealth` | value | Add `value` to health, capped at maximum. Negative values damage. |

### Inventory

| Op | Name | Arguments | Effect |
|---|---|---|---|
| `0x1B` | `DropItem` | tile, x, y | Drop `tile` into the zone at (x, y). `-1` means the zone's find-item. |
| `0x1C` | `AddItem` | tile | Add `tile` to the inventory. |
| `0x1D` | `RemoveItem` | tile | Remove one `tile` from the inventory. |

### Speech and sound

| Op | Name | Arguments | Effect |
|---|---|---|---|
| `0x04` | `SpeakHero` | - | Speech bubble by the hero. **Uses the text field.** |
| `0x05` | `SpeakNpc` | x, y | Speech bubble at (x, y). **Uses the text field.** |
| `0x08` | `Wait` | - | Pause script execution for one tick. |
| `0x0A` | `PlaySound` | soundId | Play a sound from the `SNDS` list. |
| `0x0B` | `StopSound` | - | Stop playing sounds. |

### Entities

| Op | Name | Arguments | Effect |
|---|---|---|---|
| `0x15` | `EnableHotspot` | index | Enable a hotspot in this zone. |
| `0x16` | `DisableHotspot` | index | Disable it. |
| `0x17` | `EnableMonster` | index | Spawn a monster. |
| `0x18` | `DisableMonster` | index | Despawn it. |
| `0x19` | `EnableAllMonsters` | - | Spawn all of them. |
| `0x1A` | `DisableAllMonsters` | - | Despawn all of them. |

### Counters and flow

| Op | Name | Arguments | Effect |
|---|---|---|---|
| `0x0C` | `RollDice` | max | Set zone random to 1..`max`. |
| `0x0D` | `SetCounter` | value | Set the zone counter. |
| `0x0E` | `AddToCounter` | value | Add to it. |
| `0x14` | `DisableAction` | - | Disable the action currently running. The standard "do this once" idiom. |
| `0x1E` | `MarkAsSolved` | - | Mark this zone solved. |
| `0x1F` | `WinGame` | - | Win. |
| `0x20` | `LoseGame` | - | Lose. |
| `0x22` | `SetSectorCounter` | value | Set the zone sector-counter. |
| `0x23` | `AddToSectorCounter` | value | Add to it. |
| `0x24` | `SetRandom` | value | Set zone random directly. |

---

## Idioms you will see constantly

**Run once on first visit.** `ZoneNotInitialized` is true exactly once per zone per game, so
it is the natural home for placing items, spawning monsters and one-off dialogue.

```
IF   ZoneNotInitialized
THEN PlaceTile ... ; EnableMonster 0
```

**Run once, ever.** `DisableAction` as the last instruction stops the action being
considered again, whatever its conditions later say.

```
IF   HeroIsAt 8,8
THEN SpeakNpc 8,7 "You made it!" ; DisableAction
```

**The trade puzzle**, which is the entire game in four lines: gate on holding the required
item, take it, give the reward, mark the zone solved.

```
IF   HasItem <required>
THEN RemoveItem <required> ; AddItem <reward> ; MarkAsSolved
```

**A door.** A `Bump` on the door tile changes zone.

```
IF   Bump 9,0,<door tile>
THEN ChangeZone <zone>, 9, 17
```

**Randomised behaviour.** Roll in one action, read in others.

```
IF   ZoneEntered            THEN RollDice 3
IF   RandomIs 1             THEN SpeakNpc ... "..."
IF   RandomIsGreaterThan 1  THEN EnableMonster 0
```

---

## Known divergences from the original

Honest accounting of where this engine does not match
[WebFun](https://codeberg.org/cyco/WebFun), which is the closest thing to a specification
the format has.

- **`ConditionOpcode` declares aliases that collide.** `NpcIs` and `HasNpc` are declared with
  the same values as `SectorCounterIs` (`0x19`) and `SectorCounterIsLessThan` (`0x1A`).
  Because C# cannot switch on two names for one value, opcodes `0x19` and `0x1A` are
  currently evaluated as NPC-interaction checks rather than sector-counter comparisons. Real
  scripts use them as sector-counter checks. This is the largest remaining scripting
  divergence and is on the [roadmap](ROADMAP.md).
- **Sector-counter opcodes take a key plus a value** in this engine
  (`variable[arg0 + 3000] op arg1`), where the original has one counter per zone compared
  against `arg0`.
- **`ConditionOpcode` and `InstructionOpcode` declare members past the real range** -
  conditions `0x24`, `0x25`, `0x30` and instructions `0x26` to `0x28`. No data file contains
  them; they are engine-internal and can never fire from parsed script data.
- **`Wait` does not actually suspend the script.** The executor runs an action's instructions
  to completion within one frame.

If you are extending the script engine, `webfun-reference/src/engine/script/` is the file to
read: one small module per opcode, each with the original's semantics in about ten lines.
