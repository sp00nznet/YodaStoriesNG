# The Desktop Adventures data file

`YODESK.DTA` (Yoda Stories) and `DESKTOP.DAW` (Indiana Jones) are the same container format
holding the same kinds of data. One file is the entire game: every tile, every zone, every
character, every script, the title image, and the sound file names.

Everything below describes what `src/YodaStoriesNG.Engine/Parsing/DtaParser.cs` actually
does. Where the format was worked out by cross-checking against
[WebFun](https://codeberg.org/cyco/WebFun), the relevant reference file is cited.

**All multi-byte integers are little-endian**, except the two halves of the version number,
which are big-endian. That inconsistency is in the original files, not a mistake here.

---

## The container

A flat sequence of tagged sections, each introduced by a four-byte ASCII tag. Nearly all of
them carry a `uint32` length; two do not.

```
+--------+--------+------------------------------+
| tag    | length | payload                      |
| 4 char | uint32 | `length` bytes               |
+--------+--------+------------------------------+
   ...repeats until ENDF...
```

The exceptions:

- **`VERS`** has no length field. It is always four bytes of payload.
- **`ENDF`** has no length field and no payload. It ends the file.

An unrecognised tag is logged and skipped using its length, so a file with sections this
engine does not know about still loads.

### Section order

| # | Tag | Payload |
|---|---|---|
| 1 | `VERS` | Format version |
| 2 | `STUP` | The title screen image |
| 3 | `SNDS` | Sound file names |
| 4 | `TILE` | Every 32x32 tile |
| 5 | `ZONE` | Every zone, with its scripts |
| 6 | `PUZ2` | Puzzle definitions |
| 7 | `CHAR` | Characters |
| 8 | `CHWP` | Character weapons |
| 9 | `CAUX` | Character auxiliary data |
| 10 | `TNAM` | Human-readable tile names |
| 11 | `ENDF` | End |

---

## `VERS` - version

Four bytes, two big-endian `uint16`s: major, then minor.

```
00 02  00 00   ->  2.0
```

**This does not identify the game.** Retail `DESKTOP.DAW` also reports 2.0. The engine logs
the version and identifies the game from the file name instead - see
[GAME-DATA.md](GAME-DATA.md#how-the-engine-decides-which-game-it-loaded).

---

## `STUP` - title screen

`length` raw bytes of 8-bit palette indices, 288x288, row-major, no header. Exactly 82,944
bytes for both games.

The engine converts it through the palette into an ARGB texture once, at startup, and it
becomes the title screen background under the flyby animation.

---

## `SNDS` - sound names

A list of file names, not audio. The actual `.wav` files sit next to the data file on disk
(`sfx/` for Yoda Stories, the game directory itself for Indiana Jones). `PlaySound`
instructions index into this list.

---

## `TILE` - the tile atlas

The section is a flat array of fixed-size records with no count field - the parser reads
records until the section's byte budget is exhausted.

```
+--------+-------------------------+
| flags  | pixels                  |
| uint32 | 1024 bytes              |
+--------+-------------------------+
   1028 bytes per tile
```

Yoda Stories has **2,123** tiles; Indiana Jones somewhat fewer. Tile IDs are simply the
index in this array, which is why hard-coded IDs such as the X-Wing's 948-951 work at all.

Each pixel is a palette index. **Index 0 is transparent**, which is why no tile uses it as a
colour.

### Tile flags

The `uint32` is a bit field describing what the tile is for. `TileFlags` in
`src/YodaStoriesNG.Engine/Data/Tile.cs`:

| Flag | Meaning |
|---|---|
| `Transparency` | Has transparent pixels; must be drawn with blending. |
| `Floor` | Walkable ground, drawn on layer 0. |
| `Object` | A thing in the world, drawn on layer 1. |
| `Draggable` | Can be pushed and pulled by the player. |
| `Roof` | Drawn on layer 2, above the player. |
| `Map` | Part of the world-map graphic. |
| `Weapon` | Usable as a weapon. |
| `Item` | Collectable into the inventory. |
| `Character` | A character animation frame. |

The Asset Viewer (`F4`) filters the atlas by exactly these flags.

![The asset viewer](shots/asset-viewer.png)

---

## `ZONE` - zones and their scripts

The largest section by far, and the most involved.

It opens with a `uint16` count and a `uint16` of padding. **The count is unreliable** - the
parser reads it and then ignores it, scanning forward for `IZON` markers instead and
stopping when it hits the tag of the next top-level section. Yoda Stories yields 658 zones
this way.

Each zone is an `IZON` record followed by a run of optional sub-sections.

### `IZON` header

```
"IZON"           4 bytes
size             uint32     (record size; not needed, the layout is self-describing)
width            uint16     (9 or 18)
height           uint16     (9 or 18)
flags            uint8      (ZoneFlags)
padding          5 bytes
planet           uint8      (Planet)
unused           1 byte
```

`width` and `height` are validated: anything zero or above 18 marks the zone as empty and
the parser resyncs. This is the guard rail that keeps a slightly-off offset from cascading
into hundreds of garbage zones.

**Planet** doubles as tile-set identity: `1` Desert (Tatooine), `2` Snow (Hoth), `3` Forest
(Endor), `5` Swamp (Dagobah). There is no 4.

### The tile grid

Immediately after the header, `width x height x 3` `uint16` tile IDs, in `y`, `x`, `layer`
order:

```
for y in 0..height:
  for x in 0..width:
    layer0 (floor)   uint16
    layer1 (object)  uint16
    layer2 (roof)    uint16
```

An 18x18 zone is therefore 1,944 bytes of grid. Tile ID `0xFFFF` means "nothing here".

### Zone objects

A `uint16` count, then that many 12-byte records:

```
type       uint16   (ZoneObjectType)
padding    uint16
x          uint16
y          uint16
padding    uint16
argument   uint16   (meaning depends on type)
```

`argument` is context-dependent: a tile ID for an item, a destination zone ID for a door, a
character ID for an NPC. `ZoneObjectType` is in `src/YodaStoriesNG.Engine/Data/Zone.cs`.

### Zone sub-sections

After the objects, a run of tagged sub-records until a tag the parser does not recognise -
which is how it knows the next zone (or the next top-level section) has started.

| Tag | Payload |
|---|---|
| `IZAX` | NPC spawns with the items they carry. Parsed into `ZoneAuxData`. |
| `IZX2` | Auxiliary data, kept raw. |
| `IZX3` | Auxiliary data, kept raw. |
| `IZX4` | Fixed 8 bytes, kept raw. |
| `IACT` | One action script. A zone may have many. |

`IZAX`, `IZX2` and `IZX3` carry a `uint16` length that **includes its own six-byte header**,
so the payload is `length - 6`. `IZX4` has no length at all - it is always eight bytes.

### `IACT` - action scripts

This is the one that repays care. The layout:

```
"IACT"                  4 bytes
size                    uint32
conditionCount          uint16
  condition x N         (see below)
instructionCount        uint16
  instruction x N       (see below)
```

and a condition and an instruction have **identical** layout:

```
opcode                  uint16
argument                int16   x 5      <- ALWAYS five slots, used or not
textLength              uint16
text                    textLength bytes, ISO-8859-2
```

The five fixed argument slots are the trap. They are not length-prefixed and there is no
argument count; an item is always at least 12 bytes. Reading the first slot as a count and
the second as a text length parses without error, produces plausible-looking opcodes, and
is completely wrong - every argument shifts by one and the text length picks up whatever
integer happens to sit in slot 1, so "dialogue" comes back as slices of the following
section headers. That is exactly the bug this parser used to have; it cost roughly 100 zones
to desynchronisation and made every scripted line of dialogue unreadable.

Reference: `webfun-reference/src/engine/file-format/categories/action.ts`.

The opcodes themselves are [SCRIPTING.md](SCRIPTING.md).

---

## `PUZ2` - puzzles

Puzzle definitions, scanned for `IPUZ` markers the same way zones are scanned for `IZON`.
Each puzzle carries a type and a set of strings: what the NPC says when you do not have the
item, what they say when you do, and the item IDs involved.

These are the raw material the world generator chains together into a mission. See
[WORLD-GENERATION.md](WORLD-GENERATION.md).

---

## `CHAR`, `CHWP`, `CAUX` - characters

`CHAR` defines each character: name, type (hero, enemy, friendly), and the tile IDs of its
animation frames - eight walking frames per direction, plus attack frames. Yoda Stories has
77 characters.

`CHWP` maps characters to their weapons. `CAUX` holds auxiliary per-character data, chiefly
damage and health values.

---

## `TNAM` - tile names

Maps tile IDs to human-readable names ("Lightsaber", "Bacta Tank"). Every dialogue line and
debug view that names an item reads from here. Not every tile has one.

---

## Reading it yourself

The fastest way to see any of this is `--diag`, which parses and reports without opening a
window:

```bash
dotnet run --project src/YodaStoriesNG.Engine -- --diag
```

For a live view, the Asset Viewer (`F4`) browses tiles by flag and the Script Editor (`F3`)
disassembles IACT scripts zone by zone. Both are in [DEBUG-TOOLS.md](DEBUG-TOOLS.md).
