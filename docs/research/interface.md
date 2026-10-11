# Menus and text

The in-game menu's routines and records, how menu text is drawn, the equipment and shop
screens, the item preview, and the game's text, which is stored only as pictures.
Addresses are `GAME.EXE`'s. Menu state is gp-relative, with gp = `0x8009C214`.

## The menu's structure

`kf_Interact` (`0x800305D8`) → `kf_Menu` (`0x8001A774`) → the pages. Every menu screen,
and the pause screen `kf_Pause`, opens with `kf_MenuEnter` (`0x80027198`: the menu over the
frozen world) and closes with `kf_MenuLeave(0)` (`0x80027310`). While a menu is up the
world does not tick; the game draws each menu frame between `kf_MenuBeginFrame` and
`kf_MenuPresent` (`0x800270F8`). Static menu screens are not redrawn while nothing
changes, and a dialog draws nothing at all while it waits.

Pausing the whole tick from outside the menu works the same way: `kf_MenuEnter`, then
`kf_MenuBeginFrame` + `kf_MenuPresent` each frame, then `kf_MenuLeave(0)`. Skipping
`kf_WorldUpdate` alone does not pause the game: monsters keep moving and hitting, because
they also run from the main loop's other calls. Music continues either way.

### Lists

`kf_MenuDrawList` (`0x80025468`)(menu, flags) draws a list window. The list record:

| Offset | What |
|---|---|
| +0x1C | x |
| +0x1D | y |
| +0x1F | Rows shown |
| +0x20 | First row |
| +0x21 | The selected entry (+0x20 + row) |
| +0x22 | The highlighted row |

Rows are 16 apart from y + 5; the window is 266 wide. The input routine is
`kf_MenuListInput` (`0x800222FC`)(list, items, ...). On an Up / Down move it plays
`kf_MenuSound(0x0C)` (`0x8002792C`, the move click) and, when the list has an item array
(a1), `kf_MenuShowItem(item)` (`0x80027A9C`, which switches the rotating model).

### Buttons

`kf_MenuButtons` (`0x800252F4`) draws a page of buttons from its page table (page 0 is
the main menu); `kf_MenuButtonInput` (`0x800221E8`) returns the chosen button in v0 and
plays `0x0C` on a move.

### Message boxes

The message box (`func_800441D4`) loops on PadRead without calling VSync while it waits;
it reads the interact button held to speed the text up.

### Prompts

`kf_MenuPrompt` (`0x80024C70`)(menu, kind, flags, id) is the modal USE / CANCEL loop.

## Drawing menu text

`kf_MenuText` (`0x8007E570`)(rec) draws a label; `kf_FormatNumber` and `kf_MenuNumber`
(`0x8007E564`)(rec) a number. The record is s16 x, s16 y, then glyphs:

| Glyph | Character |
|---|---|
| `0x00` .. | A .. Z |
| `0x20` .. | 0 .. 9 |
| `0x33` | `-` |
| `0x35` | `/` |
| `0x7F` | space |
| `0xFF` | end |

Each glyph advances 7 pixels. The tint is the words at gp+0x5C / 60 / 64 (`0x80` =
plain). The status screens build these records on the game's stack.

`kf_MenuWindow`(x, y, w, h) draws a window frame. Drawn after its text, it sorts beneath
it, as `kf_MenuOffenseDefense` (`0x80023300`, the OFFENSE / DEFENSE screen) does. Narrower
than about 118 its texture shows a seam; it needs h ≥ `0x44`, because its corner pieces
are `0x42` tall and a shorter window overlaps them as a bright band.

Item names are 24-byte glyph records by item id at `0x8007F620`.

### The button hints

The menus' hint bar ("✕SELECT △GUIDE ○RETURN") is one pre-drawn image: sprites of 127 x
14 in palette `0x7804`, at y 195 or below.

## The equipment screen

`kf_MenuEquipment` (`0x8001C7A8`) lists the slots:

| Row | Slot |
|---|---|
| 0 | Weapon |
| 1 | Magic |
| 2 | Shield |
| 3 | Head |
| 4 | Body |
| 5 | Arms |
| 6 | Feet |
| 7, 8 | Item 1, item 2 |
| 9 | Square button |

A row opens `kf_MenuEquipList(kind)` (`0x8001CBB8`); kinds 1 and 9 go elsewhere. The list
calls `kf_ItemPreview(id)` every frame for the highlighted row (`0xFF` = TAKE OFF), and
choosing a piece runs `kf_MenuPrompt`. The space above the item list is y 6..145; the list
window starts at y 147 and reaches x of about 258.

By pad, from the world: Circle (menu), Down x 2, Cross opens EQUIPMENT; Cross on row 0
opens the weapons.

## Shops

`kf_ShopBuy` (`0x80021298`) and `kf_ShopSell` (`0x80021724`) are a shop's lists (built by
`func_8001AEA4`; GOLD and QTY), and call `kf_ItemPreview` for the highlighted row. The
shop's windows leave a strip free on the left of the screen from y 30. The slot an item
goes in follows from its id (see [game-systems.md](game-systems.md)).

In Christy's shop in Quist the Iron Helm's DEFENSE reads 12 / 6 / 11.

The shop saves the frame on opening and uploads it again every frame (see
[graphics.md](graphics.md)).

## The item preview

The rotating model on the item and equipment pages: `kf_Interact` → `kf_Menu` →
`kf_MenuItemList` → `kf_ItemPreview` (`0x80025BE8`). The u8 at `0x801B25DF` enables it. Its
state is gp-relative:

| Offset | What |
|---|---|
| gp+0x1C | Read state (2 = done) |
| gp+0x20 | Sectors left |
| gp+0x28 | Item to load (-1 once started) |
| gp+0x2C | Sector count |
| gp+0x30 | Loaded item (-1 = checksum passed, draw it) |
| gp+0x168 | Buffer (`0x801EB9C4`) |

`kf_ItemModelLoad` starts `kf_CdReadArchiveAsync` (archive 6, descriptors at
`0x801C172C`) with `kf_ItemSectorReady` as the CD ready callback; the result must pass
`kf_ArchiveChecksum`. The CD read relies on the first sector of the read being
delivered to the ready callback even if the callback runs late.

## Item and spell descriptions

Triangle (GUIDE) on the item and magic pages and in shops shows an item's description,
through `kf_ItemGuide` (`0x80024F88`). The full text needs enough WIS; see
[game-systems.md](game-systems.md). The description is a picture from file 6: the game clears VRAM at 960,256 and uploads
it there (the header-only version is one 56 x 12 TIM). `kf_ItemGuide` picks picture
item + `0x186` for the full text and item + `0x21C` for the header-only one; these
numbers do not match the image indices in the table below (186 + id), and which
numbering is which has not been settled.

## Game text in ITEM.T

`\CD\COM\ITEM.T` holds 613 TIM images of pre-rendered text. No text is stored as
characters anywhere on the disc: a relative search of every file, 8- and 16-bit, found
nothing. The images can be read back by exact bitmap-font matching
(`scripts/research/ocr/`: a proportional font in 13-pixel cells, with a line pitch per
image).

| Images | What |
|---|---|
| 0 .. 77 | Signs and notes (another font, mostly not decoded) |
| 78 .. 139 | People's Truth Glass profiles (name plates too) |
| 140 .. 185 | Monster profiles |
| 186 + id | Item descriptions for ids `0x00` .. `0x95`; the first line is type / length / weight |
| 336 + (id - `0x96`) | Spells `0x96` .. `0xB4` |
| 552 .. 570 | Lore for special items |
| 571 .. 612 | Places |

The creature-type mapping into the profiles is in [game-systems.md](game-systems.md).
The Phantom Crown's (`0x5C`) description says it "is used to find the secret entrance".
