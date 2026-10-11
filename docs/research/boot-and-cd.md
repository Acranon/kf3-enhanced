# Boot, executables, CD and save states

How the disc is laid out, how the executables hand over to each other, how the game
reads the CD, and how its movies, ending and memory card work. Addresses are
`GAME.EXE`'s unless an executable is named.

## Executables

| File | What | Load address |
|---|---|---|
| `SLUS_002.55` | 2 KB boot stub | its PS-X EXE header |
| `OPEN.EXE` | Intro movies, title, New Game / Continue | `0x80011000` |
| `GAME.EXE` | The game | `0x80011000` |
| `END.EXE` | The ending movie, credits, "The End" | `0x80011000` |

The three large executables share one load address and so replace each other. The
recompiler's autoconfigure, with the PSY-Q signature bank, named about 600 library
functions across them. The executables were linked from the same library objects:
each object's functions have identical sizes in all three, at a constant address delta
per object, so a function identified in one is found in the others.

### The boot stub

`SLUS_002.55` loops Load + Exec over `OPEN.EXE`, `GAME.EXE` and `END.EXE` (a name table
at `0x8001024C`). When an executable returns, the stub takes the index of the next one
from the byte at `0x800102F0` (0 OPEN, 1 GAME, 2 END). Before returning, an executable
writes its hand-over:

| Address | Size | What |
|---|---|---|
| `0x800102F0` | u8 | Next executable |
| `0x800102F8` | u8 | Mode |
| `0x800102FA` | u8 | Title menu choice: 0 New Game, 1 Continue |

`OPEN.EXE` leaves (at `0x800120D0`) by writing 1, 0 and the menu choice. The stub's
Exec call is the thunk at `0x8001022C`.

### Leaving the title early

The port's quick load from the title throws out of `OPEN.EXE`'s VSync (pre-hook at
`open:0x8001FE6C`), catches the exception in a replacement for the stub's Exec thunk
(`0x8001022C`), restores the stub's registers, resets the CD, streaming and XA state,
writes the New Game hand-over, and returns, so `GAME.EXE` starts as for New Game. The
two New Game fades (`kf_FadeScreen`, `func_80046C00`: 0x1000 to 0 in steps of -0x80, one
rendered frame each, about 2 s each at 15 Hz) and the elf movie are skipped.

When `OPEN.EXE` leaves normally it closes its four memory-card events (class
`F4000001`, slots 0..3), leaving libspu's DMA event (`F0000009`) in slot 4. An early exit
must close them too, or `GAME.EXE`'s events land four slots later and a restored save
state (which holds handles to the old slots) hangs in `SsVabTransCompleted` at the first
sound-bank upload.

## Interrupts and the clock

PSY-Q's interrupt-callback table, per executable (found by
`scripts/research/irq_table.py`):

| Executable | Table |
|---|---|
| `OPEN.EXE` | `0x8003E948` |
| `GAME.EXE` | `0x8009AF9C` |
| `END.EXE` | `0x80035A10` |

`InterruptCallback` in this libapi is a thunk: `lw v0, PTR; lw v0, 8(v0); jalr v0`. `PTR`
points at libapi's driver table, whose slot 2 is the real routine. That routine
indexes the callback table with `irq*4` and keeps the enable mask 11 slots past it.
The area modules share `GAME.EXE`'s libapi.

The game's clock is one BIOS event. `func_8001A438` opens
`OpenEvent(RCntCNT3, EvSpINT, EvMdINTR, func_80019570)`; the handler, once a vblank:

- increments the frame gate counter `0x801C12EC` (vblanks since the last world tick),
- increments the vblank total `0x801C12E8`,
- increments the play-time minutes at `0x801B2588` every 3600 vblanks.

The frame gate `kf_FrameGate` (`0x80019614`, only caller the renderer) waits for the
counter to reach 4, so the world runs at 15 Hz: one tick is 66.7 ms.

## The pause screen

`func_8001A634` loops until a button in the mask at `0x80081882` is pressed. Start
pauses the game; the loop is not a hang.

## SDK bindings identified by hand

The runtime binds its SDK emulation by function name. Functions the signature bank
missed, identified in `OPEN.EXE` and carried to the other two:

- **libcd `sys.c`**: `CdSync` and `CdReady` (they wrap the internal wait loops),
  `CdSyncCallback`, `CdReadyCallback`, `CdControl` (passes a result buffer),
  `CdControlF` (passes NULL), `CdControlB` (ends in the sync wait).
- **`CdReadSync`**: `(mode, result)`, with a 480-vsync watchdog labelled `"CD_read"`.
- **`CdSearchFile`**: named by its own strings. Run as recompiled code it retried
  "disc error" forever.
- **`PutDispEnv`**: the only function with both of the clamps `0x1F4` and `0xCDA`
  (`hStart = scrX*10 + 0x260` clamped to that range); it follows `GetDrawEnv`.

| Name | `open` | `game` | `end` |
|---|---|---|---|
| `CdSearchFile` | `0x8001D9F4` | `0x800648CC` | `0x800175B4` |
| `PutDispEnv` | `0x8001683C` | `0x8007A274` | `0x80014598` |
| `CdRead` (game's own contract) | `0x8001D018` | `0x80063DF8` | `0x80019F60` |

### CdRead is not PSY-Q's CdRead

The function in the `CdRead` position takes **(buf, sectors, mode)** and returns **0 on
success, -1 on failure** (`-(result < 1)`). PSY-Q's takes `(sectors, buf, mode)` and
returns non-zero on success. The read-start routine (`open:0x8001CC9C`) copies a0 into
the running buffer pointer (`open:0x8003E80C`) and a1 into the sectors-remaining count
(`open:0x8003E818`), which shows the order. It is named `CdRead_ret0`, so it does not
bind, and replaced by a shim that swaps a0 and a1 and maps the return value.

## Data archives

`GAME.EXE` opens nine archives in `\CD\COM\`: `MO.T`, `MOF.T`, `VAB.T`, `RTIM.T`,
`FDAT.T`, `RTMD.T`, `ITEM.T`, `TALK.T` and `STALK.T`. The menu's item model loader
and the item description reader both use archive 6, which is `ITEM.T` if the
descriptors follow that order (not checked directly); `ITEM.T` holds the pre-rendered
text images (see [interface.md](interface.md)). `FDAT.T` holds the areas.

The descriptors are at `0x801C172C`, 12 bytes each, filled by `kf_OpenArchive`:

| Offset | What |
|---|---|
| +0 | Pointer to the archive's u16 start-sector table (one entry per file, plus the end) |
| +4 | `CdlLOC` of the archive |
| +8 | Size |

An archive file is a u16 count followed by u16 start sectors in 2048-byte units
(`scripts/fdat.py` lists `FDAT.T`'s). Every entry ends in a checksum word, verified by
`kf_ArchiveChecksum` (`func_80019B68`): the sum of all words but the last, seeded with
`0x12345678`, must equal the last word. An entry read from the wrong sector fails it and
is silently not used.

### FDAT.T

`FDAT.T` holds the 28 areas as (map, props, code) triplets: entry 3*n* is area *n*'s
map, 3*n*+1 its props (format in [world.md](world.md)), 3*n*+2 its code module. Module
names in this project are `fdat` + the entry number (`fdat02` for area 0, `fdat83` for
area 27).

Every code module is linked at `0x801E8308`. `GAME.EXE` holds that address as three
static words at `0x800136E8`, and scoring each module's dispatch-table targets against
function starts peaks there unanimously (14/14 to 16/16 per module).

A module is a header followed by code:

| Offset | What |
|---|---|
| +0 | An id word |
| +4 .. +0x7C | 31 dispatch slots |
| after | In some modules a second, short pointer table |

Code starts at the lowest dispatch target, which is always a shared `jr ra` stub.
Known dispatch slots: slot 4 is the module's prop routine (for example Quist's
`0x801E87E0`); the module's dialog cases and event switch are reached through others
(see [game-systems.md](game-systems.md)).

## BIOS events in save states

The runtime's BIOS event table has 64 slots and lives outside RAM, but the game keeps
handles to it (`0xF1000000 | slot`) in RAM. A save state therefore has to include the
table; the port's state version 2 does.

## Quick save state

The port takes a whole-machine state at one point only: the entry of `kf_WorldUpdate`
(`0x80030FCC`), whose single caller is the main loop `func_80014BD4` (the call at
`0x80014F3C`). At that point the C# call chain is always the same and the generated
code holds no C# locals, so RAM, registers and hardware are the complete state. The
CD, DMA and interrupt state is idle there.

Saved: RAM, scratchpad, CPU and GTE registers, VRAM (read with GP0 `0xC0`, written with
`0xA0`), SPU RAM and registers, the active area module, and the BIOS event table. Not
saved: sound voices in flight (keyed off on load; the sequencer keys them again).
Taking or applying one takes about 10 ms; a state with the port's 8 MB RAM is about
9.6 MB.

The file starts: u32 magic `0x5332464B` ("KF2S"), s32 version, the active area module's
name (a .NET string: a length byte, then UTF-8), the CPU registers, then s32 RAM length
and the RAM. `scripts/research/savestate.py` reads it.

The SPU's per-voice envelope level (register +0xC) is mixer state, not something to
restore: see [audio.md](audio.md).

## Movies

| Movie | Player |
|---|---|
| `\OP\*.S` | `OPEN.EXE`'s intro and demo |
| `\STR\S03.S` .. `\STR\S15.S` | `GAME.EXE`'s scenes (S03 is the elf after New Game) |
| `\OP\M4.S` | `END.EXE`'s ending |

Movies are 16-bit MDEC frames written to VRAM and shown straight from VRAM at 320x240,
without any render target.

`GAME.EXE`'s STR player is `func_80060D20`. Its frame loop ends when the stream reaches
the last frame or when the s16 at `0x801E824E` is `0x63`, the status the frame fetch
`func_800616DC` writes when it sees the stream end. Writing `0x63` there takes the
game's own ending path (fade, `StUnSetRing`, CD cleanup). `func_80061878` (`kf_MovieFlip`)
is a one-line buffer flip called once a frame at the top of that loop and nowhere
else, which makes it the place to check for a skip.

## The ending (END.EXE)

- **Starting the movie.** `END.EXE` starts `\OP\M4.S` with `StSetRing` / `StSetStream`
  and then its own `CdRead2` (`end:0x80017488`), which installs libcdstream's interrupt
  routine (`end:0x8001A798`) as the CD ready callback and issues ReadS. That routine
  reads ring variables only the real `StSetRing` fills; with the SDK's ring emulated,
  they hold whatever `GAME.EXE` left in RAM. `OPEN.EXE`'s intro uses the same path on
  zeroed RAM.
- **Decoding.** `END.EXE`'s `DecDCToutCallback` (`end:0x80012BFC`) must run as recompiled
  code, not bound to the SDK emulation, because its `DecDCTout` is not bound either: the
  real `DMACallback(1, cb)` then delivers the MDEC DMA completion. `OPEN.EXE`'s copy
  (`open:0x80014E30`) is unbound for the same reason.
- **"The End".** After the credits, `END.EXE`'s main stops the pad and the drive and
  spins on itself (`end:0x80011AB8: j 0x80011AB8`); on the console the player reset.
  `PadStop` (`end:0x8001C08C`) is called only there, which makes it the place to keep the
  picture up and return to the title.

## The memory card and Continue

`GAME.EXE`'s card-load screen is `func_8001FA60`. It calls `kf_MenuEnter`, checks the
card, and on title choice 1 (Continue) plays a sound and falls through, without a jump,
into `func_8001FC10` and then `func_8001FC5C`, the load menu. A function map that
splits there must not end the first function: its last instruction is a call, and
execution continues into the next entry.

The memory cards are the runtime's `carda.sav` and `cardb.sav` in the working
directory.

## Function maps: known loose ends

- `scripts/merge_branch_spans.py` would collapse `game.json` from 1203 to 603
  functions: it reads the function-pointer table at `0x800136A0` as a switch table. It
  refuses to write that file; autoconfigure already sized `GAME.EXE`'s real jump-table
  functions.
- The `GAME.EXE` sweep decodes some data around `0x80087xxx` as code (`[Unknown]` opcode
  warnings at recompile). It has caused no problem.
