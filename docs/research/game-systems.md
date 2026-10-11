# Game systems

The player, controls, stats, equipment and its hidden effects, ailments, magic,
creatures, messages, dialog scripts and event flags. Addresses are `GAME.EXE`'s unless
an area module is named. Function names are those in `scripts/name_game.py`.

## Time

The world advances in ticks of four vblanks: 15 a second, 66.7 ms each (measured 66 ms).
Every duration below is in ticks unless it says otherwise. The tick counter used by
regeneration is `0x801B2580`. Play time is counted in minutes at `0x801B2588`, by the
vblank handler (see [boot-and-cd.md](boot-and-cd.md)).

## The player

| Address | Type | What |
|---|---|---|
| `0x801B25F0` | 3 x s32 | Position X, Y, Z |
| `0x801B2608` | 3 x s16 | View pitch, yaw, roll (the camera's angle block is copied from here) |
| `0x801B2610` | s16 | Pitch, within ±`0x2BC` (about 62°); increases looking down (R2) |
| `0x801B2612` | u16 | Yaw, 12 bits (4096 a turn); increases turning left |
| `0x801B264C` | s16 | Turn velocity |
| `0x801B2668` | | Turn rate |
| `0x801B264E` | s16 | Pitch velocity, ±32, step 3 |
| `0x801B2650`, `0x801B2654` | s16 | Added to Y when the camera is built |
| `0x801B25E8` | u8 | Vertical state (below) |
| `0x801B2656` | s16 | Fall speed; Y grows downward, so negative is up |
| `0x801B24FA` | u16 | Maximum HP |
| `0x801B24FC` | u16 | HP |
| `0x801B24E4` | u32 | Experience, at most 999999 |
| `0x801B24E8` | u32 | Experience total for the next level |
| `0x801B24F0` | u8 | Level |

`kf_BuildCamera` (`func_8002B330`) builds the camera into two blocks on the main loop's
stack (X, Y, Z ints; pitch, yaw, roll halves) from the position, with Y plus the two
s16s above minus `0x640`.

Turning and looking are `func_8002F5C0`; walking and strafing `func_8002F9BC`.

### Vertical movement

`kf_PlayerVertical` (`0x8002ED60`), called from `kf_WorldUpdate` every tick, is a state
machine on `0x801B25E8`:

| State | What |
|---|---|
| `0x00` | On the ground |
| `0x10` / `0x20` | Short fall / ledge hop |
| `0x40` | Long fall |
| `0x50` | The landing dip |

The `0x40` arm moves Y by the speed, adds 40 of gravity to the speed, and tests the new
position (`kf_Collide`, `func_80033F38`). A hit while rising zeroes the speed (a ceiling). A hit while
falling lands, with damage only at a speed of 480 or more (`func_8002A6F4`), then the
`0x50` dip. A jump can be made from this routine alone: from state 0 with HP above 0,
set state `0x40` and a negative speed before it runs. The routine is the same as in
the first North American *King's Field*.

## Controls

The control code tests the pad word (this frame `0x801B265C`, last frame `0x801B265E`)
against a table of 14 u16 masks at `0x80081868`, one per action, which the
control-configuration screen (`func_8001F4C0`) rewrites. The pad word is in the game's
byte order, with its two bytes swapped against the runtime's `Controller` bits.

| Slot | Stock button | Action | Routine |
|---|---|---|---|
| 0, 1 | Up, Down | Walk forward, back | `func_8002F9BC` |
| 2, 3 | Left, Right | Turn | `func_8002F5C0` |
| 4 | Triangle | Magic; Guide in menus | `kf_Actions` (`0x8002FE1C`) |
| 5 | (see below) | Open the menu | `kf_Interact` (`0x800305D8`) |
| 6 | Square | Attack with the equipped weapon (byte `0x801B25AB`) | `kf_Actions` |
| 7 | (see below) | Interact; held, run | `kf_Interact`, `kf_Actions` |
| 8, 10 | L1, R1 | Strafe | `func_8002F9BC` |
| 9, 11 | L2, R2 | Look up, down | `func_8002F5C0` |
| 12 | Select | Second item slot | |
| 13 | Start | Pause | `func_8001A634` |

The slot names in game byte order do not match the controller's button names, and
reading the table alone gave Cross for the menu and Circle for interact. In play it is
the other way round, as in *King's Field*: **Cross interacts and confirms, Circle opens
the menu and goes back.**

Interact (slot 7, the mask at `0x80081876`, stock `0x20`; a saved game had `0x40`) acts
when it is new this frame (`kf_Interact`). Running is the same button held this frame
and last, checked in `kf_Actions` at `0x80030010`: it sets `0x801B24F3` / `0x801B24F4`
and drains the gauges `0x801B2502` / `0x801B2506` (see "Gauges"). Message boxes read the
same button held to speed up text.

## Experience and levels

`kf_GainExp` (`0x8002A310`) adds experience and levels up from the table at
`0x8009F114`, 12 bytes a level:

| Offset | What |
|---|---|
| +0 | HP |
| +2 | MP |
| +8 | Experience |

Entry L-1 holds level L's goal and entry L-2 its start. Checked: at level 20 the range
is 8485 .. 9972, and 9636 is 336 to go, 77%.

## PWR and magic growth

- **PWR.** The u16 at `0x801B2508` counts swings (`kf_PowerUse`, `0x8002A074`). At 100 it
  resets and base PWR (`0x801B2516`, at most 999) gains 1; message 1 (Strength Up).
- **Magic.** The u16 at `0x801B250A + 2n` (n: fire, earth, water, wind, holy) counts
  practice. Each cast adds the spell's practice value (record +3) to every element in its
  element bits (record +5) (`kf_MagicUse`, `0x8002A10C`). At 400 it resets and base magic
  `0x801B2518 + 2n` gains 1; message `0x12 + n` (the element's Magic Up).

## Stats

`kf_RecalcStats` (`0x80029500`) rebuilds the totals from the base stats and equipment.
It writes nothing outside `0x801B2524..0x801B255B` except `0x801C12F0`, so its effect of
a piece of equipment can be measured by writing the slot byte, calling it, reading the
totals and restoring that range, `0x801C12F0` and the slot. After really equipping the
piece, the game's own totals match the prediction.

| Address | What |
|---|---|
| `0x801B2516` | u16 x 6 base: PWR, then fire, earth, water, wind, holy |
| `0x801B2524` | u16 PWR total |
| `0x801B2526` .. `0x801B252E` | u16 magic totals: fire, earth, water, wind, holy |
| `0x801B2530` | u16 WIS (not touched by `kf_RecalcStats`) |
| `0x801B2538` .. `0x801B2546` | u16 x 8 offense: slash, blow, stab, holy, fire, earth, wind, water |
| `0x801B254A` .. `0x801B255A` | u16 x 9 defense: slash, blow, stab, dark, fire, earth, wind, water, poison |

All totals are capped to 0..999. The item-id checks inside `kf_RecalcStats` are plain
stat bonuses.

## Equipment

### Slots

| Address | Slot |
|---|---|
| `0x801B25AF` | Weapon |
| `0x801B25D4` | Head |
| `0x801B25D5` | Body |
| `0x801B25D6` | Shield |
| `0x801B25D7` | Arms |
| `0x801B25D8` | Feet |
| `0x801B25D9`, `0x801B25DA` | Item 1, item 2 |

Each holds an item id, `0xFF` when empty. Pointers to the equipped records are kept too:
the weapon's at `0x801B2594`, the armour's at `0x801B25B8 + 4 * slot`.

### Item ids

| Ids | What |
|---|---|
| `0x00` .. `0x21` | Weapons (0x00 .. 0x02 the Excellector's three forms) |
| `0x22` .. `0x29` | Head |
| `0x2A` .. `0x32` | Body |
| `0x33` .. `0x3E` | Shield |
| `0x3F` .. `0x47` | Arms |
| `0x48` .. `0x50` | Feet |
| `0x51` .. `0x5C` | Items for the item slots (rings, amulets and others) |
| `0x86` .. `0x90` | Most keys (others lie elsewhere: Varde's Key is `0x66`) |
| `0x95` | Gold coin |
| `0x96` .. `0xB4` | Spells (spell *i* is item `0x96 + i`) |

Item names are 24-byte records by id at `0x8007F620`, in the menu's glyph code (see
[interface.md](interface.md)). The bag holds a u8 count per item id at `0x800C85E8`;
storage counts are at `0x800C867E + id`.

### Records

- **Weapons**: `0x801D37A4 + id * 0x44`. +1 weight in 0.1 kg (the Capricorn: 68, 6.8 kg),
  +3 / +4 its weapon spells (below), +6 .. +0x14 offense, +0x16 HP regeneration period,
  +0x18 MP regeneration period.
- **Armour**: `0x801E6078 + id * 0x20`, added by `kf_AddArmorStats`. +2 defense, +0x14 HP+
  period, +0x16 HP- period, +0x18 MP+ period, +0x1A MP- period.

## Gauges

Two u16 gauges, each 0..5000 (full means ready): the weapon at `0x801B2502`, magic at
`0x801B2506`.

`kf_ChargeRate(avg, w)` = `avg * 64 / (w + 1) + 1` is the refill per tick.

- **Weapon** (`func_8002D2A0`): 2 x `kf_ChargeRate(PWR total, w)`, w = the weapon's
  weight (record +1). The Capricorn (68) gives 92 a tick, as measured. Gloves change w:
  Rai-Jin (`0x42`) w -= w/4, Groundal Arms (`0x44`) w += w/2, Ichrius' Gloves (`0x46`)
  w /= 2. These are the only checks of those gloves.
- **Magic** (in `kf_Actions`, `0x8002FE1C`, around `0x800301xx`): `kf_ChargeRate(avg, w)`, avg = the
  five magic totals (`0x801B2526..2E`) / 5 (WIS is not used), w = byte +1 of the
  selected spell's record (pointer at `0x801B2590`). Orladin's Hood (head `0x25`) makes
  w -= w/4; Groundal Mail (body `0x30`) w *= 2. Checked: avg 126/5 = 25, w 18 gives 85,
  as measured.

**Running** (`kf_Actions`, around `0x8003006x`) drains both gauges by 500 a tick. When
a gauge empties, its refill is held off by a delay, `0x801B24F3` for the weapon and
`0x801B24F4` for magic: 60 ticks (4 s), or none with Fu-Jin (`0x4D`) or Ichrius' Boots
(`0x4F`). Groundal Boots (`0x4C`) stop running altogether. No refill happens while the
delay counts down.

## Regeneration

Each tick n (`0x801B2580`), when n is a multiple of a period, one point is gained or
lost. The periods are in the equipped records:

| Record | +0x14 | +0x16 | +0x18 | +0x1A |
|---|---|---|---|---|
| Weapon (`0x801B2594`) | | HP+1 | MP+1 | |
| Armour (`0x801B25B8 + 4 * slot`) | HP+1 | HP-1 | MP+1 | MP-1 |

Examples: Ivory Bracelet HP+ every 90, Globe Amulet HP+ every 240, Orladin's Guard
(`0x39`) HP- every 240, Phantom Crown MP- every 60; the Triple Fang and the Moonlight
Swords regenerate (Triple Fang 90 / 90).

The Earth Shield (`0x35`) gives MP+1 every 900 ticks while the u8 at `0x801AEAEA` is set.
Zone props set it (+0x33, `kf_PlayerInTileRect`), so it is ground defined by the map.

## Status ailments

The player's damage routine `func_8002AB18` inflicts an ailment by the attack's low
nibble (the a3 argument):

| Nibble | Ailment | Timer | Stock duration |
|---|---|---|---|
| 1 | Curse | `0x801B2560` (flag `0x801B255E`, stats recalculated) | 1350 |
| 2 | Darkness | `0x801B2564` (level `0x801B2562`, capped at 64 by `func_8001B564`) | 2700 |
| 3 | Poison, if a roll beats the poison defense `0x801B255A` | `0x801B255C` | 900 |
| 4 | Paralysis | `0x801B2568` | 200 |
| 5 | Slow | `0x801B2566` | 450 |
| 6 | Drains MP by MPmax/16 | | |
| 7 | Cures poison | | |

Poison lands when the poison defense is below `rand() * 100 >> 15`, that is with a
chance of 99 - defense percent. `rand()` is `func_800796C0` (0..0x7FFF).

The condition labels are at `0x8007F3C8`: PARALYZE, CURSE, SLOW, POISON, DARK, NORMAL.

**Darkness timing.** Each tick `kf_WorldUpdate` → `func_80030E14` →
`func_80030B24(level 0x801B2562, target 0x801B2564, 0x40, 0xC00)`: the level climbs 1 a
tick toward the target, then the target clears and the level falls 1 a tick to 0. The
screen is fully dark from level 64. A target of 2700 is 180 s up and 180 s down, about
6 minutes, 5.8 of them fully dark. Measured by writing the target: +1 a tick.

### Gear that shortens ailments

| Item | Effect |
|---|---|
| Silver Arms (`0x41`) | Paralysis 200 → 150 |
| Gold Ring (`0x56`) | Paralysis / 4 |
| Silver Boots (`0x4A`) | Poison 675 |
| Silver Ring (`0x53`) | Poison / 4 |
| Groundal Boots (`0x4C`) | Paralysis / 2, curse 675, darkness 1350, slow 225 |
| Globe Amulet (`0x58`) | Curse, darkness and slow / 2 (not poison, not paralysis) |
| Luster Wing (`0x5B`) | Curse, darkness and slow / 4 |
| Ichrius' Helm (`0x29`) | Curse, darkness and slow / 4 |
| Ichrius' Boots (`0x4F`) | Paralysis and poison / 4 |

The Silver Arms do not change slow: the `0x1C2` in that code is in a delay slot and
always executes.

## Hidden gear effects

Every read of the equipped slots in `GAME.EXE` was found with a capstone sweep and each
id compare read, minding the delay slots.

### Damage taken (`func_8002AB18`)

Arguments: slash, blow and stab in a0..a2 (against defense `0x254A`, `0x254C`, `0x254E`),
a3 the type (low nibble the ailment), and on the stack dark (`0x2550`), fire (`0x2552`),
earth, wind, water.

| Item | Effect |
|---|---|
| Dragon Head (`0x27`) | 25%: physical damage zero |
| Groundal Mail (`0x30`), Ichrius' Armor (`0x31`) | 25%: physical damage zero |
| Full Promeus set (slot word `0x453B2F28` plus feet `0x4E`) | Physical damage always zero |
| Groundal Shield (`0x3A`), Ichrius' Shield (`0x3C`) | 25%: the five magic parts zero |
| Ebony Bracelet (`0x55`) | 50%: the five magic parts zero |
| Fire Mail (`0x2D`) | Fire part zero |

### Each tick (`kf_WorldUpdate`)

| Item | Effect |
|---|---|
| Evil Crescent (`0x16`) | Every 150 ticks, 1 in 8: paralysis 200 |
| Groundal Shield (`0x3A`) | Fog `func_8003456C(10, 10, 10, 0xD05, 6000)` each tick, as darkness does (`func_80030E14` uses `0x1388`): a dimmer view |
| Evil Ring (`0x52`) | 1 in 256 a tick: poison 900 |
| Earth Shield (`0x35`) | MP+1 every 900 ticks on zone ground |

### Spells (`func_8002DEEC`)

| Item | Effect |
|---|---|
| Groundal Crown (`0x26`) | MP cost (spell +0x16) x 2 |
| Orladin's Mail (`0x2E`) | MP cost / 2 |
| Blood Star (`0x1F`) equipped | No casting |
| Orladin's Hood (`0x25`) | Magic gauge refills faster (w - 25%) |
| Groundal Mail (`0x30`) | Magic gauge refills slower (w x 2) |

### Weapon hits (`func_8004C668`) and shots

| Item | Effect |
|---|---|
| Blood Star (`0x1F`) | Each hit heals 1..9 HP (`func_80030BE0` adds to HP) |
| Silence Rod (`0x20`) | Damage equals the target's HP for creature types 3B 3C 3D 3F 42 43 4F 58 59 5B (the undead) |
| Elchris Bow (`0x1C`) | Each shot costs 3..7 HP, and can kill (`func_8002D2A0`) |

### King of the East set

Ryu-ga (sword `0x08`), Ryu-Jin (shield `0x37`), Rai-Jin (gloves `0x42`), Fu-Jin (boots
`0x4D`). No routine checks two of them together: there is no set bonus. Rai-Jin lightens
the weapon in the weapon refill, Fu-Jin removes the refill delay after running; Ryu-Jin
and Ryu-ga have no special code, only their records' stats.

## The Excellector

`kf_ExcellectorGrow` (`func_8002A210`), called from `kf_GainExp`. While you own stage 1
(item 0) or stage 2 (item 1), in the bag or in storage (`0x800C867E + id`), the u32 at
`0x801B24EC` holds your experience when you got it, and -1 while you own neither. Once
experience passes that value + 13000 (`0x32C8`), the sword grows, 1 → 2 or 2 → 3 (item 2,
the final form), and if it is equipped (weapon below 2) the next stage is equipped.
Message 4. After a growth `0x801B24EC` is -1 again, so the next experience gain restarts
the count: stage 3 comes about 13000 experience after stage 2.

## Spells

Spell records: `0x801B77EC + 24 * i`.

| Offset | What |
|---|---|
| +0 | Learned (for a weapon spell: usable) |
| +1 | Weight, for the magic refill |
| +3 | Practice added per cast |
| +5 | Element bits: 1 fire, 2 earth, 4 water, 8 wind, 0x10 holy |
| +0x16 | MP cost |

The name is item `0x96 + i`. The selected spell's record is pointed to by
`0x801B2590`.

### Weapon magic

A weapon record's +3 and +4 index the spell records (i `0x36` .. `0x4C`, spells only
weapons cast). `func_8002CFE0` decides whether the weapon's spell is usable: always
with the Excellector's last form (2) and weapons `0x0D` .. `0x11`; otherwise only with PWR
total ≥ 50 and every magic total ≥ 50, and the spell record's +0 set.

That +0 is set in two levels, by two people:

| Level | Given by | Spells |
|---|---|---|
| 1 | Veyrac, in Cason (`fdat20` dialog case 4) | `0x38` Ryu-ga, `0x3A` Capricorn, `0x3C` (no weapon), `0x3E` Anorium, `0x40` Vaculacia, `0x4C` Morguline |
| 2 | Alexander, in Verdite Castle (`fdat26` `0x801E8818` case 0, flag `0x35`) | `0x36`/`0x37` Excellector (last form), `0x42`/`0x43` Seath's Sword, `0x44`/`0x45` Dark Slayer, `0x46`/`0x47` Triple Fang, `0x48` .. `0x4B` Moonlight Swords |

Alexander's script gives level 2 on the first talk with flag `0x2F` set (set in Verdite
Castle's module; Lyn's Ring at the fountain) and flag `0x79` clear.

## Using items

`kf_UseItem` (`0x8005CBE0`)(`PlayerPos`, `0x801B2610`, item) is called by `kf_Interact`
right after `kf_Menu` returns the item chosen. Items `0x65` .. `0xA1` jump through the
table at `0x800131A0`.

| Item | Handler | What |
|---|---|---|
| Royal, Copper, Silver, Magician's, Jail, Dwarves', Lyn's keys | `0x8005D188` (shared) | `kf_UseKey` on the lock ahead; the key stays in the bag |
| Silviera's Key (`0x8D`) | `0x8005D244` | Into an empty pedestal; used up (see [world.md](world.md)) |
| Orladin's Key (`0x88`) | `0x8005D300` | Into a pillar; used up by `kf_RemoveItem` (`0x8005D7F8`) |
| Varde's Key (`0x66`) | | Turns a keyhole dial; stays in the bag |
| Wise Fruit (`0x70`) | `0x8005CE10` | WIS + 1, at most 4 stored |
| Ichrius' Key | `func_8001C030` | The warp map (see [world.md](world.md)) |

`kf_UseKey` (`0x80046608`) compares a lock byte with the item and sets it to `0xFF` on a
match, returning 1. `kf_UnlockSound` (`0x8005BD34`, sound effect `0x10`) plays the unlock.
The item menu runs both at `0x8005D1D8`.

### Wisdom and item descriptions

`kf_ItemGuide` (`0x80024F88`) shows an item's full description only if WIS (`0x801B2530`)
is at least the item's requirement (u8 table at `0x80081780 + id`, 0..5). Otherwise it
shows a header-only version ("Special Armor  Gloves 1.8 kg"). Requirements seen: Gold
Ring, Rai-Jin and Fu-Jin 3; Globe Amulet 2. The status screen may show stored WIS + 1
(a stored 2 displayed as 3); not checked.

## On-screen messages

`kf_QueueMessage` (`func_80041EEC`) queues a message and `kf_UpdateMessage` shows it.
Their state is 13 bytes from `0x801AEAED`: an 8-slot queue (`0xFF` free; a message is
dropped when it is full), read index `0x801AEAF5`, write index `0x801AEAF6`, phase
`0x801AEAF7`, hold countdown `0x801AEAF8` (counted down once per renderer call), fade
level `0x801AEAF9`.

| Id | Message | Id | Message |
|---|---|---|---|
| 0 | Level Up | 18 .. 22 | The five element Magic Ups |
| 1 | Strength Up | 23 | Spell is Cast |
| 2 | Intelligence Up | 24 | Spell is Broken |
| 3 | Magic Learned | 25 | Gold |
| 4 | Excellector Up | 26 | Empty |
| 5 | Sealed | 27 | Worn Off |
| 6 | Locked | 28 | Broken Sword |
| 7 | Wrong key! | 29 | Broken Shovel |
| 8 | Tombstone | 30 | Dead |
| 9 | Need More MP | 31 | Dead Body |
| 10 | Nothing Happens | 32 | Soldier's Body |
| 11 | Can't Carry Any More | 33, 34 | Skeleton |
| 12 | Drew Some Water | 35 | Can't Used Here |
| 13 | Magic Power Up | | |
| 14 | Broken | | |
| 15 | Doesn't Open | | |
| 16 | No Qualifications | | |
| 17 | Digits | | |

Ids above 35 repeat from 0. Ids 11 and up were read from a capture that lagged, so they
may be off by one. There is no "unlocked" message.

## Creatures

### The entity table

200 records of `0x88` from `0x80185DA8`, run by the object pass `kf_DrawObjects`
(`func_80040AE4`) and drawn with `kf_DrawModel` (`func_8003E34C`).

| Offset | Size | What |
|---|---|---|
| +0 | u8 | 1: placed (people and fixed creatures); 0 or 4: respawning |
| +2 | u8 | Creature type (index into the definitions) |
| +9 | u8 | 1 live; 2 / 3 dying / gone |
| +0x0A | u8 | Unknown; `0xFF` on every creature seen |
| +0x0B | u8 | Item dropped on death, `0xFF` none |
| +0x0C | u8 | Animation number |
| +0x18 | u16 | Animation time, 0..0xFFF, `+0x80` a tick, loops (holds at 0xFFF then restarts) |
| +0x1A | u16 | HP |
| +0x2C, +0x30, +0x34 | s32 | Position |
| +0x40, +0x42, +0x44 | s16 | Angles (not wrapped to 12 bits) |

Records are loaded (+9 = 1) only near the player, but every record keeps its position.

### Definitions

`0x78` bytes per type from `0x8018C7E8`:

| Offset | What |
|---|---|
| +5 | `0x28` on monsters that strike you; 0 on ghosts (they only cast Darkness), the statue and the hidden pair. Likely a melee setting. |
| +0x0C | Item a respawning creature may drop |
| +0x0D | Its chance, out of 256 |
| +0x1A | Maximum HP |
| +0x38 | Script; it starts with `0x70` when the creature talks (`kf_CreatureTalk`) |

Checked: a Green Slime has 300 HP and loses 64 a swing (300 → 236 → 172).

### Drops

The death code at `0x80050708`:

- Byte 0 == 1: drops +0x0B, unless it is `0xFF`.
- Byte 0 == 0 or 4 (respawning): rolls the type's item, def +0x0C, with chance
  def +0x0D / 256.
- Anything else: no drop.

So +0x0B on a respawning creature is a stale value. Silviera's Key is such a drop; area
1 (Ralugo) also has carriers of Earth Herb x3, Leather Gloves, Blood Stone and Dragon
Crystal.

### Types with a special role

| Type | What |
|---|---|
| `0x00` .. `0x2B` | People |
| `0x20` .. `0x22` | Leon Shore, by story stage; only one, with +0 == 1, is placed |
| `0x3C` | Skeletons |
| `0x43` | Ghosts (the Race; sprites) |
| `0x6E` | A hidden pair of creatures, one on each side of every secret wall |
| `0x6F` | The Statue of the Hero: a creature so that a weapon can hit it |
| `0x72` | The Palace of Wind's crystal lamps (see [world.md](world.md)) |
| `0x73` | The two lamps at the back of the Palace of Wind |
| `0x74` | Green Slime |

The undead, for the Silence Rod: 3B 3C 3D 3F 42 43 4F 58 59 5B.

Types `0x6E` and `0x6F` are not enemies; their definitions have no stats and def +5 is 0
(as for the ghosts, which are enemies). Striking the Statue of the Hero's star with the
Capricorn (sword `0x09`) or a better sword unseals the chest beside it, according to
community guides.

### Names

The game's names for creatures are its Truth Glass profiles, pre-rendered images in
`ITEM.T` (see [interface.md](interface.md)), in creature-type order with gaps:

| Types | Image |
|---|---|
| People `0x00` .. `0x2B` | 77 + type |
| `0x2C` .. `0x31` | none |
| Monsters `0x32` .. `0x71` | 71 + type |
| `0x74` | 185, Green Slime |

The first line of each image is the name. Checked against Christy, Rene, Jack,
Marilyn, Ed, Leon and McCain (with the Truth Glass), the skeletons, the ghosts, the
statue and the slimes. The game's own table mapping types to images has not been
found, so the gaps could shift in areas not yet checked.

People matched by position against the community maps (kingsfield.fandom.com,
kings-field.neocities.org): in Quist, `0x20` .. `0x22` Leon Shore, `0x00` Christy Clements
(the shop), `0x05` Rene Thomas (the inn), `0x0E` Jack Leininger; in the Ancient
Battlefield, Ed Edmund and Marilyn Miller and her son.

## Dialog scripts and event flags

Dialog scripts are byte code. Opcodes `0xF0` .. `0xFF` go through the jump table at
`0x80013160`. `F7 <flag> <value>` sets `flag[0x801BA988 + flag]`. An area module adds its
own dialog cases (`F4 <case>` calls the module's case).

Event flags are bytes from `0x801BA988`. Known flags:

| Flag | Address | What |
|---|---|---|
| 4, 5, 6, 7 | `0x801BA98C` .. | Leon Shore's last job (below) |
| `0x16` | `0x801BA99E` | Opens Quist's event locks (Ed Edmund, after Marilyn's quest) |
| `0x2F` | | Set in Verdite Castle (Lyn's Ring at the fountain); needed by Alexander |
| `0x35` | | Alexander's weapon magic |
| `0x62`, `0x63` | `0x801BA9EA` | The Palace of Wind's lit lamps, as a u16 bit set |
| `0x79` | | Must be clear for Alexander's gift |

### Marilyn's quest and Quist's mystery door

Quist's module (`fdat02`, code at `0x801E8D68`) changes door #70 and chests #33 and #116
from lock `0xC8` to `0xFF` once flag `0x16` is set. The only writer is the Ancient
Battlefield's module (`fdat14`, `0x801E8D8C`), one case of its event switch: Ed Edmund
sets it after Marilyn's quest, which is giving her son's corpse the charm, lifting the
darkness. Ed then moves and points you to your mother's grave.

### Leon Shore's last job (fdat02)

Leon's script (type `0x22`) shows a page, runs `F4 04` (Quist's dialog case 4,
`0x801E8C20`), then "if flag 6 goto done". Case 4 sets flag 6 when the play-time minute
(`0x801B2588 % 60`) is below the stamp in flag 4 or 10 or more past it, or when
`(exp % 10000) / 100` is outside flag 5 .. flag 5 + 10. The stamp and experience are
taken when he takes your items (case 2). So: about 10 minutes of play (sooner if the
hour turns over first), or about 1000 experience, then talk to him.

With flag 6 set and flag 7 clear, the module's prop routine (slot 4, `0x801E87E0`, for a
prop with +0x3B == 0) hides entities 0..2 (Leon) and shows the house's items (prop
byte 0 = 2). It runs as the prop comes back into range: walking far enough away in
Quist and back does it, interacting with the house's props does not. Until then the
Excellector, Leon's Note and Ichrius' Key sit in his house with prop byte 0 = 0.
