# The world

Areas and how the game moves between them, the props (doors, chests, containers,
switches, traps and the rest), locks and keys, secrets, and the map data the floor
plan comes from. Addresses are `GAME.EXE`'s unless an area module is named. Prop and
creature numbers written `#n` are indices into their tables; positions written `x,z`
are in map samples (8 to a tile) unless stated.

## Areas

There are 28 areas. Area *n*'s map, props and code are `FDAT.T` entries 3*n*, 3*n*+1 and
3*n*+2, and its code module is called `fdat` + (2 + 3*n*) in this project.

| Area | Module | Name |
|---|---|---|
| 0 | `fdat02` | Quist |
| 1 | `fdat05` | Ralugo |
| 2 | `fdat08` | Lake Noel |
| 3 | `fdat11` | Dwarves Cave |
| 4 | `fdat14` | Ancient Battlefield |
| 5 | `fdat17` | Forest of Varde |
| 6 | `fdat20` | Cason |
| 7 | `fdat23` | High Elf Ruins |
| 8 | `fdat26` | Verdite Castle |
| 9 | `fdat29` | Hill of Prayer |
| 10 | `fdat32` | Field of Ice |
| 11 | `fdat35` | Valley of Garan |
| 12 | `fdat38` | Garrison Barracks |
| 13 | `fdat41` | Cave of Shudom |
| 14 | `fdat44` | Path of Poison |
| 15 | `fdat47` | Passage of Thieves |
| 16 | `fdat50` | Maze Temple of Orladin ("Shudom's Room" is a room inside it) |
| 17 | `fdat53` | Place of the Beginning |
| 18 | `fdat56` | Passage of Death |
| 19 | `fdat59` | First Level of the Royal Cemetery |
| 20 | `fdat62` | Sleeping Quarters of Guyra |
| 21 | `fdat65` | Unused: an endless flat grass field with no walls, music or Truth Glass entry, probably a test map |
| 22 | `fdat68` | Basement of the Castle |
| 23 | `fdat71` | Ground Floor of the Castle |
| 24 | `fdat74` | Second Floor of the Castle |
| 25 | `fdat77` | Seath's Space (Seath) |
| 26 | `fdat80` | Palace of Wind |
| 27 | `fdat83` | Seath's Space (the Swordsmaster's arena) |

Areas 20, 21, 25 and 27 hold very few props (377 .. 959 non-blank bytes of prop data).

## Changing area

`kf_ChangeArea` (`0x80017C78`) takes five bytes, the area's map, props and module
(all *n* for area *n*) and two bank numbers, followed by the arrival shift (signed
tiles; `0x7F` keeps the position). It fills the request at `0x8018FAD4` (+0 flag 1, +2
stage, +0x10 target area). `kf_AreaChangeStep` (`func_80018358`) runs the request stage by
stage; it also clears the prop table. Of the two banks, one is the sound bank
(the VAB file loaded is bank + 1, at `0x80018B78`) and the other the music (`0x80018BE0`);
`0xFF` keeps the current one.

### Exits

Walk-in exits are invisible props of class `0xE0` (type `0x118`). Each covers
+0x38 x +0x39 tiles from its own tile. The check at `0x8004A7F8` (its record pointer is
prop + 8) calls `kf_PlayerInTileRect` (`func_80046884`) to test whether the player is
inside; if so it calls
`kf_ChangeArea` with the area at +0x3A and the arrival bytes from +0x3B on.

Neighbouring areas overlap in coordinates: Quist from z 560 on is the same ground as the
Ancient Battlefield from z 0. Each side has a strip to the other area and a strip back to
itself, which the game ignores.

The exit graph, from the type-`0x118` records on the disc (each link joins two areas;
links of an area to itself are left out):

0-4, 1-5, 1-13, 1-14, 2-14, 2-15, 3-15, 4-12, 5-12, 5-26, 6-10, 6-15, 7-15, 8-9,
8-23, 9-18, 10-11, 10-18, 22-23, 23-24.

No walk-in exit leads to 16, 17, 19, 20, 21, 25
or 27. Area 17's two exit records have area `0xFF`: warps inside the area. The
Ancient Battlefield's exit to 12 is a strip at 480,304.

### Warps

Warp props carry: +0x38 area, +0x39 / +0x3A sound bank and music, +0x3B / +0x3C arrival
tile. They are of area-specific types (`0x12C`, `0x12D`, `0x134`, `0x139`, `0x141`,
`0x144`; types from `0x100` up are defined per area). `GAME.EXE` also holds a warp table
at `0x800130F0`, 14 entries of 8 bytes (area, banks, ...), used by `func_8005C864`.
Entries `0x0C` and `0x0D` are event warps: to area 27 from `func_80061940` and to area 24
from `func_8005EB20`.

Warps found: 7 → 16, 16 ↔ 17, 8 → 19, 19 ↔ 20, 24 → 25. No way into area 21 was found.

**Warp glyphs** are class `0xEB` (the High Elf Ruins' #118, type `0x141`): +0x38 area
(16), +0x39 / +0x3A sound bank and music (11, 13), +0x3B / +0x3C arrival tile (2, 2), +0x3E
facing.

**Ichrius' Key** opens a map (`func_8001C030`): a 4 x 3 grid moved with the d-pad only
(left and right within a row, up and down by a row; slot 8 is skipped), with no unlock
flags. The pick returns `0x96 + n` to `kf_UseItem`, which calls `kf_WarpTable(n)`.

### The High Elf Ruins' portals

Portals are class `0xE8` (type `0x13F`): +0x38 / +0x39 the trigger tile (1 x 1, also
+0x3A / +0x3B), +0x3C / +0x3D the destination tile, +0x3F the facing. `func_80047010`
(at `0x8004A66C`) puts the player at the destination tile's centre. The arch posts on
either side are class `0xE9` (type `0x123`). Area 7 has 12, six pairs, each sending you
onto its partner's own tile.

The **Throne of Ichrius** is class `0xA7` (type `0x140`, #12). Its +0x38 becomes `0x85`
once Ichrius' Key is placed in it (`kf_UseKey`'s `0xA7` case). The portals keep working
after the key is taken out.

## Props

### The prop table

396 records of `0x44` from `0x80191A5C`, allocated by `kf_FindFreeProp`, cleared in
`func_80018358`, drawn from `func_80040E3C` through `kf_DrawModel`.

| Offset | Size | What |
|---|---|---|
| +0 | u8 | Bit 1 set: the object is out in the world (shown, interactable). Clear on the extra pieces of a multi-part model and on items not yet placed. |
| +1 | u8 | Animation number |
| +6 | u16 | Prop type; `0xFF` when the record is free |
| +8 | u8 | State: a chest is 1 shut, 3 opened; a pedestal or dial is busy while set |
| +0x0A | u16 | Animation time (a secret door: `+0x48` a tick) |
| +0x10 | s16 | Second timer, passed to the drawer |
| +0x14, +0x18, +0x1C | s32 | Position (a door: its hinge) |
| +0x24, +0x26, +0x28 | s16 | Angles (+0x26 a door's heading) |
| +0x33 | | Zone flag (zone props; see "Ground zones") |
| +0x38 .. +0x41 | | Ten parameter bytes, by class (below) |

The ten parameter bytes are what KingsFieldRE calls an ObjectInstance's `Flags[10]`.

`kf_FindPropAhead` (`0x80045C7C`) finds the prop an interact is aimed at, and
`kf_InteractTarget` (`0x8005E2D0`) acts on it.

### Prop types and classes

Types are described by 24-byte definitions from `0x8018FB3C + type * 24`. Byte 0 is the
class; +3 sorts graves; +6 is a door's width. Types below `0x100` are shared; from
`0x100` up they are defined per area (in area 2, `0x12C` .. `0x131` are bodies).

| Class | What |
|---|---|
| `0x00` | Barred jail door (type `0x12D` in the Garrison Barracks, three, the Jail Key) |
| `0x01` .. `0x05` | Doors |
| `0x06`, `0x07` | Chests |
| `0x09` | Searchable: barrels, bodies, skeletons, graves, wells |
| `0x0D` | A post |
| `0x0E` | Save crystal (type `0xE3`) |
| `0x14` | Possibly a wall switch; also the model records of giant skeletons |
| `0x15` | Hidden item (type `0x12A`) |
| `0x17` | Orladin's pillar (type `0xA2`) |
| `0x18` | Key pedestal (type `0x101`) |
| `0x1B` | Door (type `0xA5`, whose leaf hangs the other way from its hinge) |
| `0x24` | Type `0xA4`, beside one `0xA5`; perhaps its second leaf |
| `0x40` | An item on the ground; its type is the item id |
| `0x51` | Wall spear trap (type `0xDB`) |
| `0x52` | Proximity spear trap (type `0xDC`) |
| `0x53` | Keyhole dial (type `0x12C` in the Forest of Varde) |
| `0x56` | Giant tree stump (type `0x12D` in the Forest of Varde) |
| `0xA7` | Throne of Ichrius (type `0x140`) |
| `0xD1` | Wall torches |
| `0xE0` | Area exit (type `0x118`) |
| `0xE8` | Portal (type `0x13F`) |
| `0xE9` | Portal arch post (type `0x123`) |
| `0xEB` | Warp glyph (type `0x141`) |
| `0xFF` | Scenery (including trees, types `0x144` .. `0x146`, and the models of giant skeletons) |

Classes from `0xE0` up are triggers.

**Multi-part models.** A class-9 prop with byte 0 bit 1 clear is an extra piece of a
bigger model, not an object of its own. Quist's queen's grave is #3 plus #111 and #214.

### The props on the disc

An area's prop entry (`FDAT.T` 3*n*+1) holds 24-byte records from offset 8: +4 the type
(u16), +0x10 / +0x11 the bytes that become RAM +0x38 / +0x39, +0x12 a target area.

## Locks

The lock byte is prop +0x38, for chests and doors (and, with other meanings, for the
other classes).

| Value | Meaning |
|---|---|
| `0xFF` | Open (not locked) |
| `0xFE` | Opened (a chest after opening) |
| `0xF0`, `0xF1` | Sealed |
| `0xC8` | Opened by an event (Quist's door #70 and chests #33, #116; see [game-systems.md](game-systems.md)) |
| `0x96`, `0x98`, `0x9A` | Sealed codes on doors of classes 3..5 and `0x1B` |
| other, below `0xB5` | The item id of the key that opens it |

`kf_InteractTarget` treats the sealed codes `0x96` / `0x98` / `0x9A` (at `0x8005E610`) as
`F0` / `F1` / `F2`, with messages 23, 5 (Sealed) and 15 (Doesn't Open). They are the item
ids of the spells Fire Ball, Fire Storm and Flame, but nothing compares a cast spell with
+0x38. The Field of Ice's (`fdat32`) door #85 has `0x98` and is opened by a switch that
appears after a conversation and an area reload.

A class-1 door with +0x38 not `0xFF` just says it is locked (`0x8005E5E8`). Doors of
classes 3..5 and `0x1B` go through `0x8005E600`. Every class opens when +0x38 is `0xFF`.
Class 2 is not a key class: many class-2 doors are unlocked doors to other areas. The
pedestal door in Quist is type `0xAF`.

A locked chest or door makes `kf_InteractTarget` queue message 6 (Locked), with the prop
record in s0 on every path to it (`0x8005E7B4`). Using the key from the item menu runs
`kf_UseKey` and `kf_UnlockSound`; the key is not used up, except Orladin's and
Silviera's (see [game-systems.md](game-systems.md)).

Whether a lock has been opened is the lock byte itself; how the locked class-2 doors
choose their key, if any do, is not known.

## Chests and containers

### Chests

Classes 6 and 7. +8 is 1 shut and 3 opened; +0x38 is the lock (above), `0xFE` once
opened. +0x3A is the index of the prop holding the chest's item; that prop's type is the
item id, and it reads `0xFF` once taken.

**Traps.** A chest is trapped when +0x3C is not `0xFF`; the trap fires as the lid opens.
+0x3F is the effect: `0x1A` is poison gas (HP 64 → 49 and the red tint); `0x02` looked
slightly different (some MP loss); other values are not mapped. Setting +0x3C to `0xFF`
disarms a chest; `0x01` still gasses. Area 1 (Ralugo) has a single trapped chest, #168,
holding the Iron Boots. Barrels and graves (the area module's routine `0x801E8400`) use
+0x3C / +0x3F as a message flag and id, so the trap reading applies to chests only.

### Class-9 containers

A class-9 prop holds the index of the prop with its item at +0x3A, like a chest; its
item is u16 +0x3A, `0xFFFF` when it holds nothing. The kind, by definition and type:

| Rule | Kind |
|---|---|
| def +3 == 4 (types `0xBE`, `0xBF`, `0xC0`, `0xC4`), and `0xB8` | Grave (`0xB8` / `0xBE` Quist's queen's grave) |
| type `0xD4` | Barrel |
| type `0xD5` (shared) | Small bucket (Dwarves Cave #67, Garrison Barracks #203) |
| type `0xF7`; Ralugo's `0x134` | Pot |
| type `0xF5` | Well |
| types `0xB7`, `0xBA` | Skeletons |
| types `0x12C` .. `0x131` in areas 4 and 12 | Bodies (in the Ancient Battlefield `0x12D` .. `0x131`), including the Soldier's Body `0x12E` |
| type `0x134` in area 13 | A strange rock |

Other class-9 types have not been identified.

### Hidden items

Class `0x15` (type `0x12A`) is an invisible spot that gives what it holds (+0x3A) on
interact. Ralugo has five by the graves; the Ancient Battlefield has one (#195, Flame);
the Cave of Shudom has one at a giant skeleton (#31, holding #10, the Magician's Key).
The other two giant skeletons in that room are only models (class `0xFF` plus class `0x14`
records).

### Items on the ground

Class `0x40` props whose type is the item id. An item is in the world only with prop byte
0 bit 1 set; items that appear later (Leon's house: the Excellector, Leon's Note,
Ichrius' Key) have byte 0 = 0 until their event. The item a chest or container holds is
also a class-`0x40` prop. `func_8005DB30` lifts an item being picked up and turns its
+0x26 by `0x40` a tick.

## Keys, pedestals and puzzles

### Key pedestals

Class `0x18` (type `0x101`). +0x3A / +0x3B hold the pedestal's number and its key's item
id. Silviera's Key (`0x8D`) goes in through `kf_UseItem`'s `0x8D` branch (`0x8005D244`):
on an empty pedestal (+0x38 == `0xFF`) it writes the key to +0x38, clears +8, and
`func_8005C7E8` opens the pedestal's door; the key leaves the bag. Interacting with a
pedestal that holds a key lifts the key out (it floats); interacting again takes it.

### Keyhole dials and the stumps of the Forest of Varde

Class `0x53` (type `0x12C`, prop #3 in the Forest of Varde) is a keyhole dial: +0x3D the
key item (`0x66`, Varde's Key), +0x38 counts the turns, +8 busy. `kf_InteractTarget`
(`0x8005E594`) does nothing, without a message, unless +0x3D is `0xFF`. Using Varde's Key
from the menu (`kf_UseItem`) turns the dial; the key stays in the bag.

Each turn swaps giant tree stumps, class `0x56` (type `0x12D`). Many come in pairs: the
standing one has byte 0 bit 1 set and +8 = 1, the hidden one byte 0 = 0 and +8 = 2. The
key swaps them (for example #11 at 60,176 with #14 at 36.8,195.5). A turned stump
animates and hides about 2 s later, and a standing stump blocks the path.

### Orladin's pillars

Class `0x17` (type `0xA2`). +0x38 is the lock (`0x88`, Orladin's Key; `0xFE` once opened),
+0x3A the prize prop, +0x3C the key model that appears in the pillar. Using the key from
the menu (`kf_UseItem`'s `0x88` case, `0x8005D300`) sets `0xFE`, and `kf_RemoveItem`
(`0x8005D7F8`) takes the key. `kf_UseKey` on a pillar opens it without taking the key,
which the game itself never does.

### The Palace of Wind's crystal lamps

Creatures of type `0x72` (#40 .. #49 in area 26) are crystal lamps that Fire Ball lights.
Area 26's module sets bit (entity index - 40) of the u16 at `0x801BA9EA` (flags `0x62` /
`0x63`, at most 12 lamps) at `0x801E8758`, and turns the bits into the palace's
brightness at `0x801E86C4` (writing 2 or 0 to the model slots `0x8018296A + 0x18 * n`,
`+0x140` a lamp). Type `0x73` (#50, #51, the two at the back, bits 10 and 11) are lamps
too. With all twelve lit, the palace's chests unlock. Lighting #49 took the bits from
`0x0100` to `0x0300`; with `0x03FF`, Fire Ball lights #50. The rule is that module's, so
lit state is known only in area 26.

### The Statue of the Hero

A creature (type `0x6F`), so that a weapon can hit it. Striking its star with the
Capricorn or a better sword unseals the chest beside it (Quist #344), according to
community guides.

## Traps and secrets

### Spear traps

- Class `0x51` (type `0xDB`): a wall spear trap.
- Class `0x52` (type `0xDC`): a wall spear that strikes by itself when you come near; a
  pair facing each other across a corridor in the Passage of Thieves (`fdat47`, #22 and
  #32).

### Secret walls and doors

Every secret wall has a hidden pair of type-`0x6E` creatures, one on each side, about 6
samples away. Class 3 type `0xAB` is a secret wall. Secret doors also come in ordinary
door classes: the Cave of Shudom's #18 (type `0xB4`, class 5) is one and has the pair;
the area's other class-5 door of the same type (#6) has none. A door (class 0..5 or
`0x1B`) with a `0x6E` creature within 12 samples is therefore a secret door. The Phantom
Crown (`0x5C`) "is used to find the secret entrance"; the Phantom Rod reveals hidden
things in play.

## Ground zones

Zone props (+0x33, tested with `kf_PlayerInTileRect`, `0x80046884`) set the u8 at `0x801AEAEA` while
the player stands in them, for the Earth Shield's MP regeneration.

Ambient sound zones are handled by `kf_DrawObjects`; see [audio.md](audio.md).

## The floor plan

### Tile records

The area is an 80 x 80 grid of 10-byte tile records at `0x801D4464` (800 bytes a row, 10
a column, 2048 world units a tile; `kf_DrawTiles` walks it). Each record has two 5-byte
halves:

| Offset | What |
|---|---|
| +0 | Model (`0xFF` none; drawn when below `0xF0`) |
| +1 | Height: floor Y = -h << 7 |
| +2 | Rotation (low 2 bits, quarter turns) |
| +3 | Collision shape |
| +4 | Flags |

The first area uses only the second half. Two-storey tiles use both.

### Collision

A collision shape is a short list of commands (`0x10` a floor at a height, `0x20` ..
`0x25` solid half-planes over a height range, `0x30` .. sloped floors, ...), found by
u16 offsets from the table at `0x801E4464`. Shape 0 is "floor at 32000".

`kf_TerrainCollide` (`func_80033D08`)(x, y, z, radius, [sp+0x10] height) is the terrain
half of the player's collision. It returns 0 when the spot is free, and then
`0x801E6474` holds the floor's Y. It keeps its state at `0x801E6460` .. `0x801E64C0`. It
also leaves three **death heights** for the spot, u32 at `0x801E647C` / `80` / `84`, 100000
when there is none.

Sampling it at 8 x 8 points a tile, 64 above the tile's floor with a radius of `0x80`,
gives the floor plan; a whole area is about 100,000 samples in about 75 ms. A sample that
is solid at the tile's height + 64 may still be floor higher up: cave mouths, stairs and
bridge railings rise 256 .. 512 above the tile's height, so a second test from 640
higher is needed. A floor 16000 or more below the tile's own height is a chasm.

### Liquids

`kf_DeathPlaneCheck` (`0x80028D54`) kills the player each tick they are below a death
height: lava and water both kill at once (walking into Ralugo's lava: HP 193 → 188 from
the fall, then 0). So a spot is liquid when it has a death height above its floor (death
below floor - 256).

| Place | Floor | Death height |
|---|---|---|
| Ralugo, lava | -8448 | -10240 |
| Ralugo, lava bridge deck | -13184 | -11776 (below the deck: safe) |
| Forest of Varde, river | -9600 | -12160 |
| Forest of Varde, bridge deck | -13824 | none |
| Cave of Shudom, lava | -512 | -2560 |
| Cave of Shudom, walkway | -2560 | none |

The texture tells water from lava. Both use the water's animated texture page
(`tpage & 0x1F == 0x0F`):

| Liquid | Palette | Blend |
|---|---|---|
| Water (the river) | `0x79CC` | Semi-transparent average (tpage `0x0F`) |
| Lava | `0x7D00` | Additive (tpage `0x2F`) |

Tile model `0x50` by the Cave of Shudom's save point draws palette `0x7D00` with
averaged blending (a translucent tile) and has no death height: it is floor, not lava.
Ralugo's lava tiles are all additive (48 of 48).
