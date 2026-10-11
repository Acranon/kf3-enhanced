# Graphics

How King's Field III draws a frame: the renderer and its buffers, the visible-tile grid,
the ground and model routines and their culling, fog, lighting, water and lava, the HUD,
and the state the renderer advances on each call. Addresses are `GAME.EXE`'s. For what
the port does with these facts, see the patches named in
[../architecture.md](../architecture.md).

## The frame

The main loop (`func_80014BD4`) runs, once a tick:

1. `kf_WorldUpdate` (`0x80030FCC`);
2. `kf_BuildCamera` (`0x8002B330`), which fills two blocks on the stack: position X, Y,
   Z (s32) and pitch, yaw, roll (s16), from `0x801B25F0` and `0x801B2608`;
3. `kf_Render` (`0x800422B8`)(pos, angles).

`kf_Render` draws the world, presents with `kf_Present` (`0x80035700`), and waits in
`kf_FrameGate` (`0x80019614`) until the vblank counter `0x801C12EC` reaches 4. After the
gate, `kf_StepAfterGate` (`0x80043940`) steps twelve `0x14`-byte slots at `0x801AAB64`.

`kf_BeginFrame` (`0x80035630`) flips the buffer index `0x801AEAE8`, picks the ordering
table (`[0x801A9174]`), the primitive pool and the overlay ordering table
(`[0x801A91B8]`) for that buffer, and clears them.

`kf_Present` does, in order: DrawSync, VSync, `PutDispEnv(0x801A9274 + 20*i)`,
`PutDrawEnv(0x801A91BC + 92*i)`, link the overlay table, `DrawOTag`. The game shows a
frame late: before the VSync, the buffer about to be drawn is the one on screen. Its
DRAWENV has `isbg = 1`, so every `PutDrawEnv` clears the buffer.

### The projection

In the world the GTE projects with H = 200 and the centre at 160,120. The compass is
also projected by the GTE, with its own offset and H, so the GTE's current OFX / H at
draw time is not necessarily the world's.

### The ordering table

The sky dome is drawn from ordering-table slot 2 on: two empty entries first, the sky
in entries 3 to 98, the world from about 3100. The sky dome is small and near the
camera; only the painter's order puts it behind the world.

### Primitive pools

The game writes each frame's polygons and sprites into one of two pools. The
descriptors are at `0x80199158` and `0x80199164` (u32 start, end, current), the base they
are laid out from at `0x80199154`, the pointer to the one in use at `0x80199170`.
`kf_LayoutPrimPools` (`func_80035394`) lays them out once at `0x80116954`, `0x1A000`
bytes each. When a pool is full the game silently stops drawing, so the last things in
the frame (trees, items, parts of models) are the ones that vanish.

While a message or menu is up, the game lays the descriptors out again from the base
for scratch use (`0x8003DB50`, `0x8003DEF0`, `0x80044240`, `0x80060E48`, `0x80061314`:
desc0 = base .. +0x7400, desc1 = +0x7400 .. +0xE800), and later back at `0x1A000` each,
from the same base.

Measured use with a widened view: `0x1A534` at one forest spot, `0x1CE08` at another.

## Visible tiles

`kf_BuildTileGrid` (`func_80034BF4`) rebuilds a 25 x 25 byte grid of map tiles once a
frame: built in the scratchpad at `0x1F800120`, copied to `0x801AEC84`. The window's
first column and row are at `0x801AEC74` / `0x801AEC78`, the yaw it was built for at
`0x801AEC5E`. `kf_DrawTiles` (`func_8003BFD0`) draws a tile only when its byte has bit 2
set.

| Value | Meaning |
|---|---|
| `0x1E` | In the cone, near (drawn through libgte's subdivision) |
| `0x1A` | In the cone, far |
| `0x08` | Out of the cone but close |
| `0x00` | Not drawn |

For each cell the builder tests the distance from the window centre and the two edges
of the view cone: the yaw ± a half-angle of `0x1B8` (about 39°), plus a pitch term.
Cells are drawn only within a radius of the window (`0x801AEAE9`). The player's own cell
in the grid is `(0xC7FF - 8cos) >> 12`.

The fog is by view depth: the ground fades from `*(0x801AEC7C)` (start) to
`*(0x801AEC80)` (end), 2048 being a tile.

## The ground

`kf_DrawTile` (`0x8003BB04`), called per tile by `kf_DrawTiles`, calls one of the two
ground routines. Its a2 bit 4 picks `kf_DrawGroundNear` (`0x8003AB04`) or `kf_DrawGround`
(`0x80039D50`); forcing either changes nothing visible, so it is not a detail level.

`kf_DrawGround` works from the scratchpad:

| Address | What |
|---|---|
| `0x1F800014`, `0x1F800018` | Primitive write pointer and its limit |
| `0x1F800020` | The current face record: +0 uv0, +2 clut, +4 uv1, +6 tpage, +8 uv2, +0xC normal, +0x10 / +0x12 vertex offsets; byte +3 is the packet code (`0x24` / `0x2C` / `0x34`) |
| `0x1F800030` .. `0x38` | Pointers to the three vertex words loaded before NCLIP |
| `0x1F800058`, `0x1F80005C` | Copies of the fog start and end (written by the routine on entry) |

Each face record carries its own UVs, turned differently per face.

### Back-face culling on whole pixels

Every drawing routine below reloads each triangle's SXY0-2 from its vertex buffer with
`mtc2` and culls on `NCLIP <= 0` (`blez`):

| Routine | Draws | Where its corners are |
|---|---|---|
| `kf_DrawGround` (`0x80039D50`) | Map tiles | Scratchpad `+0x30/34/38` |
| `kf_DrawGroundNear` (`0x8003AB04`) | Near map tiles | Scratchpad `+0x30/34/38` |
| `kf_DrawModelPolys` (`0x80035CA4`) | Models, from `kf_DrawModel` and `func_8003C35C` | t0, t1, t2 |
| `kf_DrawModelPolys2` (`0x80039428`) | Models, from `kf_DrawPropModel` (`func_800400AC`) | t2, t1, t3 |
| `kf_DrawModelPolys3` (`0x80037BEC`) | Models from `kf_DrawModel` (the crystal tree); four NCLIP sites | Scratchpad `0x1F800030/34/38` |

Far rows of ground are slivers that are flat in whole pixels, so NCLIP gives 0 and they
are dropped. On the console the neighbouring triangles meet on the same pixel row, so
nothing shows; with sub-pixel corners the dropped sliver is a visible gap. A GPU capture
with true corners shows the neighbours do share corners at every depth: the triangles
are missing, not misplaced. The same happens to distant objects and to the crystal
tree's limbs (302 crystal polygons a frame with whole-pixel culling, 433 with sub-pixel).

### libgte's subdivision clip

Near tiles go through libgte's polygon subdivision (entries `func_80074D88`,
`func_80075188`, `func_800756A8`, `func_80075B48`; parameter block in a1 on the caller's
stack). It drops every sub-piece lying wholly past a screen edge, `|x - OFX| > (a1+4)/2`
and the same for y against a1+8. The width at a1+4 is 320.

## Models

`kf_DrawObjects` (`func_80040AE4`) walks the creature, prop and effect tables and draws
each with `kf_DrawModel` (`func_8003E34C`). Creatures are drawn by the call that returns
to `0x80040D2C`; `kf_DrawModel`'s other callers draw props and effects. Props are drawn
from `func_80040E3C`, effects by the loop at `0x8004165C` after the props.

Creatures and props both draw through `kf_AnimateModel` (`func_800431E8`), which blends the key frames
around an animation time; the weapon uses the same routine. The animation number
selects the animation, and the time runs within it:

| Table | Number | Time |
|---|---|---|
| Creatures (`0x80185DA8`, 200 x `0x88`) | +0x0C | +0x18, 0..0xFFF, `+0x80` a tick, loops |
| Props (`0x80191A5C`, 396 x `0x44`) | +0x01 | +0x0A (a secret door: `+0x48` a tick); +0x10 a second timer |
| Effects (`0x801B80EC`, records of `0x4C`) | +0x04 | +0x14 |

The animation number changes when an animation switches (the shopkeeper goes from 0 to
1 when spoken to). Limb animation frames step at 15 Hz.

### Spell effects

Effect records of `0x4C` from `0x801B80EC`, ended by a record whose first byte is `0xFF`;
live when `(+8 & 3) != 0`.

| Offset | What |
|---|---|
| +0x04 | Animation number (the byte the object pass passes to `kf_DrawModel`) |
| +0x14 | Time |
| +0x18, +0x1C, +0x20 | Position, s32 |
| +0x28, +0x2A, +0x2C | Angles, s16 |
| +0x30, +0x32, +0x34 | Scale, s16 (`0x1000` = 1); a mushroom's spore cloud rises and swells each tick |

Effects attached to the player read the player position `0x801B25F0` and view angles
`0x801B2608`. The arrow on a drawn bow is effect type `0x42` with flags 1, kept 808 units
from the player and turned with the yaw at each tick (its +0x2A matches the view yaw).

### The weapon

`kf_DrawWeapon` (`func_8003DF50`) draws the weapon, only while the swing time `0x801B25A4` is set (s16, -1
when not swinging, 0..3840 in steps of 384 a tick). The swing animation is
`0x801B25AE`. `kf_AnimateModel` blends the key frames around the time. The weapon is
drawn in view space.

### Model animation slots

`kf_StepModelAnims` (`func_80041514`, which also draws) advances a u32 counter at
`0x80182964` and the frame byte (+5) of 128 slots of `0x18` from `0x80182968` on every
call. The tile and water animation reads them.

## State the renderer advances on every call

Everything below changes each time `kf_Render` runs, not each tick. A second call of
the renderer within a tick (to redraw at a higher frame rate, or from a mirrored camera)
must save and restore it, or the animation it drives runs as many times faster.

| What | Where |
|---|---|
| Model animation counter and frame bytes | `0x80182964`, `0x80182968` (128 x `0x18`) |
| Render call counter, a cycle of about 32; water and texture animation run off it | `0x801AEB20` |
| Scrolling textures (`kf_ScrollTextures`, `0x800351FC`: steps each slot, `0x18` each at `0x801AEB1C`, and MoveImages it) | |
| The slots after the gate (`kf_StepAfterGate`) | `0x801AAB64` |
| On-screen message state, including the hold countdown | `0x801AEAED` .. `0x801AEAF9` |

The per-draw changes that are correct to leave alone: the double-buffer index and
pointers (`0x801AEAE8`, `0x80199170`, `0x801A9174`, `0x801A91B8`), the SDK's counters
`0x8009C07C` / `0x8009C20C`, draw stamps such as `0x801920FC`. `0x800C87xx` changes
between draws because the sound sequencer runs from the vblank interrupt.

A call of the renderer from another camera also leaves that camera's state in the
renderer's globals (`0x801AEC00` .. `0x801AF000`, `0x801AEB10` .. `0x801AEB18`) and the
scratchpad (`0x1F800000` .. `+0x400`), which the menu reads to draw its background.

## Fog

King's Field III's ground fog is not the GTE's depth cue. `kf_DrawGround` projects each
corner (RTPS), stores SZ/4 at corner +4 and its own fog value at corner +6:

```
ir = 4096 * (SZ - start) / (end - start), clamped to 0..0x1F0F
```

from the scratchpad copies of the fog start and end (a start of `0x7D00` or more means no
fog). It lights each tile face once (NCCS), then per corner loads that colour, sets IR0
to corner +6 (`mtc2`) and runs DPCS toward the far colour FC (8,8,8 here; FC is 8-bit
<< 4). An IR0 past 4096 overshoots FC, which is why the far distance goes black. The fog
is linear in view depth, so interpolating it per vertex across a big tile is wrong: the
fog's edge follows the tiles.

The scratchpad copies are written by the routine itself on entry; the values for the
current call are at `0x801AEC7C` / `0x801AEC80`.

The GTE's own cue, DQB + DQA * H / SZ, does not match these values: the game's are about
four times larger and not proportional.

## Lighting

Models are lit with NCDS / NCDT: per-vertex normals and up to three coloured
directional lights, set per area (outdoors in Quist: three lights in LLM, a bluish LCM,
BK 1824). The near ground uses NCDS too; the far ground NCCS plus DPCS (the fog above).
The game switches between the world's, the compass's and the item preview's light sets
about ten times a frame; a busy forest scene sets about twenty new sets a second. The
menu's item model is depth-cued differently from the world.

## The HUD

From a GPU capture in the first area:

- **The HP / MP panel**: textured sprites (GP0 `0x64` .. `0x67`) in palettes `7D45` /
  `7E05`, inside x 0..110, y 0..75.
- **The compass**: textured, gouraud, semi-transparent polygons, projected by the GTE,
  in the compass palettes, inside x 255..320, y 0..65. One palette per face, in VRAM
  column 16, rows 480 .. 487 (`7841`, `7881`, `78C1` = "E", `7901` seen).
- **Full-screen effects**: the drowning tint is a semi-transparent `POLY_FT4` from 0,0 to
  320,240; the death flash and fade are drawn the same way.
- **The message line** (x 96 .. 287, y 203 .. 217) keeps its faded-out text drawn every
  frame as additive sprites with colour `000000`: invisible.
- **The menus' button hints** are one pre-drawn image; see [interface.md](interface.md).

## Water and lava

The river is a few big semi-transparent (average blend) polygons, texture page `0x000F`
(4-bit, x 960), palette `0x79CC`, uv 224..255 x 96..127, which is VRAM (1016,96) 8 x 32;
vertex colour `0x717171`. `kf_DrawGround` draws them from the map's tile meshes, and each
face record carries its own UVs, turned differently per face: the blocky seams, which
are there on the console too. Map tiles sit at quarter turns, and the ground routine's
GTE rotation is camera x tile.

The flow: every draw copies the source (984,96) into (1016,96) shifted down k rows (two
MoveImages); k steps once a tick from the model animation counter, advanced by the
tick's own draw after its copy.

Lava uses the same animated texture page (`tpage & 0x1F == 0x0F`) with palette `0x7D00`,
drawn additive (tpage `0x2F`). See [world.md](world.md) for telling liquids apart.

In the game's order, a river bank polygon under the water can be drawn after the water
polygon over it, and water writes no depth: at the 4:3 edges these polygons are never
seen.

## Other textures

- **The sky behind the trees.** The big black area behind the trees is mostly the game's
  own darkness (DuckStation shows it too). The sky strips there use palettes `7B4D` /
  `7B8D` (VRAM rows 493 / 494 at x 208), which the area data uploads as all zeros
  (transparent).
- **Fire torches**: a 64 x 64 block (tpage `0039`, v 192..255) drawn twice, additively.
  Texel row 191, just outside it, is other art.
- **Tree cards** are alpha-tested cut-out polygons.
- **The frozen frame behind signs and messages.** Reading a sign, the game copies the
  displayed frame to VRAM 320,0 (GP0 copy from 0,240 to 320,0, 320 x 240) and redraws it
  each frame as two 15-bit textured quads (tpage `0105`, x 0..192; tpage `0108`,
  x 192..320), darkened by vertex colour, under the text.
- **The shop** reads the frame back on opening (StoreImage) and uploads it again every
  frame (LoadImage 0,0 320 x 240), drawing the shopkeeper live over it.

## Movies

Movies are 16-bit MDEC frames written to VRAM and shown from VRAM at 320 x 240. See
[boot-and-cd.md](boot-and-cd.md) for the players.

## Measurements

Taken with the port, for scale:

- At Leon Shore's house the vertex map follows 98% of projected vertices (about 40,000 a
  second) into GP0 packets; the recovered sub-pixel fraction averages 0.77 px, uniformly
  spread, as expected.
- One scene fills a 2048 x 2048 mip atlas with 522 entries within a frame; 4096 x 4096
  holds about 1500.
- The river draws about 6200 water triangles a second in Quist; the slimes' skins are cut
  from the water's texels in the same blend (about 6300 triangles a second where they
  are), so texture and blend alone do not identify water.
- An item being picked up rises out of the ground: for a moment its bottom really is
  below the ground.
- A second run of the renderer from a mirrored camera costs about 2.8 ms of CPU.

## Open questions

- With the port's reflections on, the bridge's reflection shows a black streak at some
  camera angles, and where the sky shows in a reflection it comes in streaky, blocky
  shapes. The suspect is the reflection pass's sky fallback (it takes the background a
  missed ray crossed) together with the march's step pattern.
- A lighter band in the water beside a tree near the left of the view at one spot in
  the first area.
- Effects held at the player (the drawn bow's arrow) follow the view's turn between
  ticks but not its pitch.
- The Smooth movies filter (runtime `GpuHle.MovieFilter`: bicubic scaling of the MDEC
  frames plus a deblock of flat areas) makes the picture jitter slightly, most visibly
  in the opening movies. Not yet investigated; the option is off by default and labelled
  a work in progress until it is.
- Smooth fog covers the ground; models are faded through per-pixel lighting's
  per-vertex cue, and near ground drawn through libgte's subdivision lies inside the fog
  start.
