# Scripted pickups and NPC interactions

Tile flags alone do not determine whether a map object can be collected. The original
Yoda Stories data uses Item flags for some rescued people, and Character flags for R2.

## Runtime rules

- An applicable bump script runs before ordinary walking/pickup. Its conditions and
  disabled state are checked without executing it during movement planning.
- A tile addressed by item-use/trade scripts remains an interaction target until its
  script releases it.
- A script that removes its interaction target and issues `DropItem` for that same
  target explicitly hands it to Luke. The engine awards one inventory copy, registers
  weapons/companions, and marks the source collected. It does not recreate a map tile
  which could trigger the same pickup script again.
- Disabled introductory dialogue, or dialogue whose conditions are no longer true,
  does not permanently prevent collection of ordinary items.
- Other drops, including Ben's Force reward, retain physical placement on an accessible,
  uncovered square (or inventory fallback when surrounded).

There is no blanket R2 exemption in the interaction guard. R2's original conversation
and remove/drop instructions are followed, and receiving the locator enables hints.

## Audit

Run without a game window:

```
dotnet run --project src/YodaStoriesNG.Engine -- --pickup-audit /path/to/yodesk.dta
```

The local original data contains 658 zones and 2,123 tiles. The scan finds 301 distinct
non-solid scripted interaction positions, using 98 tile types. At 51 positions a bump
script explicitly removes and hands over the same object. These are candidate counts,
not a claim that every mission state has been replayed.

The replay checks isolate original authored encounter actions and run them in classic
and smooth movement: R2 (93), General Marutz (316), C-3PO (401), credits (307), mushroom
(263), rifle (149), stranded pilot (391), and introductory/conditional junk dialogue
(207). They check collection timing, single awards, companion/weapon registration,
NPC protection, and release after flavor text. No generated artwork or SDL window is
needed. Full mission playthroughs and visual confirmation remain separate checks.

Validation on the local original data: all 18 encounter checks passed across both
movement modes. The existing `--self-test` suite and solution build also passed.
