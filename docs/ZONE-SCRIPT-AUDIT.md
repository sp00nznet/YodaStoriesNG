# Zone script audit

Scanned **658 zones**, **8714 actions**, **16175 conditions**, **44659 instructions**, and **2067 monsters** from the original data. No game window was opened.

## Confirmed and fixed: enemy death rewards

The engine discarded the native death-drop flag and treated zero/-1 loot as no reward. Its fallback also searched ordinary crates instead of native quest-item hotspots (type 0). Both are corrected. There are 309 flagged death drops, including 19 generated-quest drops across 9 zones. Explicit carried items no longer override a false death-drop flag.

Zones with generated-quest death drops: 4, 37, 197, 289, 290, 294, 443, 502, 526.

Headless regression checks: **4142 passed** (every monster's flag/loot decoding, plus zone 289 quest reward, collection, explicit weapon reward and duplicate-death protection in both movement modes).

Additional script regression checks: **2688 passed**, including every native DrawTile tuple and quest-marker condition, all solved-state/active-monster conditions, scripted weapon removals, partial/full redraw, transient reset, invalid inventory recovery and sound dispatch. These are headless state checks; actual audio output and visual quality have not been observed.

## Corrected script behavior

### inactive monsters: 2 occurrences

Disabled/dead monsters no longer block HasNoActiveMonsters; enabled living native actors count regardless of hostility.

Zones: 223, 631.

### negative inventory item: 1 occurrences

AddItem(-1) is ignored as in the reference engine; old negative saved items are filtered on load.

Zones: 622.

### quest-marker condition: 39 occurrences

DropsQuestItemAt reads the enabled native quest-item hotspot at the authored coordinates.

Zones: 25, 37, 42, 57, 109, 124, 139, 146, 160, 488.

### solved-state consistency: 5 occurrences

ZoneIsSolved sees generated and saved progress; script and pickup solves propagate to all rooms of the sector.

Zones: 149, 150, 416, 458, 621.

### StopSound dispatch: 35 occurrences

StopSound stops active effects and clears queued audio; native music-only semantics are uncertain.

Zones: 93, 275, 287, 296, 359, 416, 465, 470, 537, 578, 629.

### temporary drawing: 638 occurrences

DrawTile(12,6,1188) renders a temporary frame without mutating collision layers.

Zones: 95, 117, 130, 142, 143, 151, 187, 194, 236, 287, 323, 331, 344, 382, 389, 391, 393, 402, 404, 409, 410, 436, 463, 464, 465, 489, 513, 548, 563, 599, 600, 621, 624, 632, 641, 644.

### weapon removal: 2 occurrences

RemoveItem(18) consumes one copy including equipped ownership; reserves and ammo remain when copies exist.

Zones: 93, 266.

Reference behavior checked against WebFun's [quest-marker condition](https://codeberg.org/cyco/WebFun/src/branch/master/src/engine/script/conditions/drops-quest-item-at.ts), [temporary drawing disassembly](https://codeberg.org/cyco/WebFun/src/branch/master/src/engine/script/instructions/draw-tile.ts), [active-monster condition](https://codeberg.org/cyco/WebFun/src/branch/master/src/engine/script/conditions/has-no-active-monsters.ts), [negative AddItem handling](https://codeberg.org/cyco/WebFun/src/branch/master/src/engine/script/instructions/add-item.ts), and solved-sector operations. StopSound is also a no-op in WebFun; stopping all currently playing effects here is an explicit engine policy pending original-game audio confirmation.

## Remaining data issues requiring follow-up

These are source/data findings, not proof every listed action fails during play. Coordinate findings may be deliberate off-map animations; generated rooms and travel sentinels require runtime context.

All opcodes present in the data have named switch cases. Out-of-range hotspot indices below are authored references with no matching hotspot; they remain safely ignored rather than guessing which puzzle object was intended.

### Invalid hotspot reference: 8 occurrences

- Zone 236, action 23 (zero-based): DisableHotspot(2); hotspot count 2
- Zone 335, action 2 (zero-based): DisableHotspot(1); hotspot count 1
- Zone 433, action 0 (zero-based): DisableHotspot(0); hotspot count 0
- Zone 433, action 0 (zero-based): DisableHotspot(1); hotspot count 0
- Zone 433, action 1 (zero-based): EnableHotspot(0); hotspot count 0
- Zone 433, action 2 (zero-based): EnableHotspot(1); hotspot count 0
- Zone 476, action 10 (zero-based): DisableHotspot(5); hotspot count 5
- Zone 595, action 10 (zero-based): DisableHotspot(2); hotspot count 2

### Missing authored quest marker: 1 occurrences

- Zone 160, action 7 (zero-based): DropsQuestItemAt(16,15) has no matching hotspot; left false (zone 160 also has a later correct action for its actual marker)

## Opcode coverage

| Kind | Opcode | Name | Uses | Zones |
|---|---|---|---:|---:|
| Condition | 0x00 | ZoneNotInitialized | 1635 | 396 |
| Condition | 0x01 | ZoneEntered | 203 | 103 |
| Condition | 0x02 | Bump | 3157 | 483 |
| Condition | 0x03 | PlacedItemIs | 789 | 223 |
| Condition | 0x04 | StandingOn | 228 | 65 |
| Condition | 0x05 | CounterIs | 1397 | 283 |
| Condition | 0x06 | RandomIs | 2136 | 334 |
| Condition | 0x07 | RandomIsGreaterThan | 109 | 52 |
| Condition | 0x08 | RandomIsLessThan | 87 | 41 |
| Condition | 0x09 | EnterByPlane | 285 | 138 |
| Condition | 0x0A | TileAtIs | 1312 | 167 |
| Condition | 0x0B | MonsterIsDead | 375 | 43 |
| Condition | 0x0C | HasNoActiveMonsters | 13 | 10 |
| Condition | 0x0D | HasItem | 169 | 96 |
| Condition | 0x0E | RequiredItemIs | 309 | 65 |
| Condition | 0x0F | EndingIs | 41 | 10 |
| Condition | 0x10 | ZoneIsSolved | 5 | 5 |
| Condition | 0x12 | HasGoalItem | 32 | 18 |
| Condition | 0x13 | HealthIsLessThan | 30 | 12 |
| Condition | 0x14 | HealthIsGreaterThan | 44 | 13 |
| Condition | 0x16 | FindItemIs | 5 | 2 |
| Condition | 0x17 | PlacedItemIsNot | 128 | 45 |
| Condition | 0x18 | HeroIsAt | 663 | 127 |
| Condition | 0x19 | SectorCounterIs | 549 | 106 |
| Condition | 0x1A | SectorCounterIsLessThan | 92 | 29 |
| Condition | 0x1B | SectorCounterIsGreaterThan | 97 | 34 |
| Condition | 0x1C | GamesWonIs | 67 | 5 |
| Condition | 0x1D | DropsQuestItemAt | 39 | 10 |
| Condition | 0x1E | HasAnyRequiredItem | 4 | 2 |
| Condition | 0x1F | CounterIsNot | 124 | 53 |
| Condition | 0x20 | RandomIsNot | 171 | 64 |
| Condition | 0x21 | SectorCounterIsNot | 122 | 26 |
| Condition | 0x22 | IsVariable | 1737 | 151 |
| Condition | 0x23 | GamesWonIsGreaterThan | 21 | 11 |
| Instruction | 0x00 | PlaceTile | 5517 | 396 |
| Instruction | 0x01 | RemoveTile | 3009 | 388 |
| Instruction | 0x02 | MoveTile | 3009 | 139 |
| Instruction | 0x03 | DrawTile | 638 | 36 |
| Instruction | 0x04 | SpeakHero | 1086 | 306 |
| Instruction | 0x05 | SpeakNpc | 1234 | 181 |
| Instruction | 0x06 | SetTileNeedsDisplay | 2821 | 436 |
| Instruction | 0x07 | SetRectNeedsDisplay | 2668 | 272 |
| Instruction | 0x08 | Wait | 5483 | 433 |
| Instruction | 0x09 | Redraw | 4524 | 325 |
| Instruction | 0x0A | PlaySound | 2702 | 451 |
| Instruction | 0x0B | StopSound | 35 | 11 |
| Instruction | 0x0C | RollDice | 475 | 296 |
| Instruction | 0x0D | SetCounter | 1451 | 277 |
| Instruction | 0x0E | AddToCounter | 137 | 24 |
| Instruction | 0x0F | SetVariable | 1364 | 160 |
| Instruction | 0x10 | HideHero | 494 | 146 |
| Instruction | 0x11 | ShowHero | 321 | 143 |
| Instruction | 0x12 | MoveHeroTo | 939 | 191 |
| Instruction | 0x13 | MoveHeroBy | 151 | 30 |
| Instruction | 0x14 | DisableAction | 1075 | 252 |
| Instruction | 0x15 | EnableHotspot | 446 | 135 |
| Instruction | 0x16 | DisableHotspot | 552 | 162 |
| Instruction | 0x17 | EnableMonster | 448 | 73 |
| Instruction | 0x18 | DisableMonster | 283 | 63 |
| Instruction | 0x19 | EnableAllMonsters | 11 | 9 |
| Instruction | 0x1A | DisableAllMonsters | 32 | 31 |
| Instruction | 0x1B | DropItem | 765 | 273 |
| Instruction | 0x1C | AddItem | 22 | 10 |
| Instruction | 0x1D | RemoveItem | 354 | 157 |
| Instruction | 0x1E | MarkAsSolved | 162 | 111 |
| Instruction | 0x1F | WinGame | 16 | 16 |
| Instruction | 0x21 | ChangeZone | 415 | 118 |
| Instruction | 0x22 | SetSectorCounter | 339 | 97 |
| Instruction | 0x23 | AddToSectorCounter | 59 | 18 |
| Instruction | 0x24 | SetRandom | 1551 | 160 |
| Instruction | 0x25 | AddHealth | 71 | 32 |

## Limits

This audit checks every stored script against implemented opcode families and meaningful coordinate/reference arguments. Unused argument slots contain arbitrary data and are intentionally ignored. It does not exhaustively simulate every mission, inventory, random roll, movement path or script-state combination. Wait resumes on the next update and redraw delay arguments are currently ignored; timing fidelity still needs review. Passing the audit does not prove all puzzles are playable.

Re-run: `dotnet run --project src/YodaStoriesNG.Engine -- --zone-script-audit Yoda/yodesk.dta docs/ZONE-SCRIPT-AUDIT.md`.
