# Changes to the vendored RecompOne

`tools/RecompOne` is a modified copy of [RecompOne](https://github.com/BlackLabelHQ/RecompOne)
(MIT licence, `tools/RecompOne/LICENSE`). It began as the copy vendored by Verdite2, the
RecompOne port of the first North American *King's Field*, at upstream commit
`d81dec8c9622fdcd0865d73588a3baa8d3c3a605` (`tools/RecompOne/UPSTREAM`). That copy
already carried Verdite2's own runtime work: the GTE recovery behind sub-pixel
vertices and perspective-correct textures, the depth-recovering ambient occlusion pass,
screen-space and planar reflections, and the spatial SPU mixer.

This page lists what this project changed on top of that, and why. Each change is meant to be
general rather than specific to King's Field III, and all of them are candidates
to offer upstream.

## Debug switches

The runtime's own environment switches are named `RECOMPONE_*`. In Verdite2's copy they
were `KF2_*`; they were renamed so that the runtime's switches are told apart from the
game's (`KFIII_*`, read by the patches). For example `RECOMPONE_WATCH`,
`RECOMPONE_SPU_PROBE`, `RECOMPONE_CD_SECTOR_TRACE`, `RECOMPONE_SWAP`,
`RECOMPONE_PGXP*`. The full list is in [testing.md](testing.md).

## Recompiler

- **A function that ends on a call falls through** (`CodeGen/FunctionEmmiter.FallsThrough`).
  A function whose code ended on `jal` plus its delay slot was treated as not falling
  through, so its generated method returned where the MIPS code carries on into the
  next funcmap entry. `GAME.EXE`'s memory-card load screen (`func_8001FA60`) ends on such
  a call and falls into the load menu; Continue on the title started a broken new game
  instead of loading. The change altered one line in all of the generated code. A callee
  that never returns makes the extra call unreachable, which is harmless.
  `scripts/research/find_fallthrough.py` lists functions that run off their end.

## CD-ROM and streaming (`sdk/LibCd.cs`, `sdk/LibCdStream.cs`)

- **`StUnSetRing` clears the CD data and ready callbacks**, as PSY-Q's does. `CdRead2`
  installs them for a movie; left behind, `CdReady` refused every later polled read and
  the title's `OP.D` load looped forever.
- **`CdSync` reports the last command's own interrupt** (`_cmdIntr`) instead of the
  shared `_lastIntr`, which `PumpReady` sets to DataReady for every streamed sector. A
  `while (CdSync(0) != CdlComplete)` loop issued during a ready-callback stream never
  ended: the in-game menu froze (`func_80027D88`).
- **The first sector of a ReadN is held once** (`_holdFirst`) if the ready callback did
  not `CdGetSector` it. `CdSync` pumps right after the command, before the game is
  ready, and the drive used to step past the ignored sector: the menu's item model read
  started one sector late, failed the archive checksum and never drew.
- **Data IRQs are dropped while the HLE ring owns a stream** (`QueueDataIrq` while
  `LibCdStream.Streaming`). `END.EXE` starts the ending movie with the HLE
  `StSetRing`/`StSetStream`, then its own `CdRead2` installs libcdstream's interrupt
  routine as the ready callback. That routine reads ring variables only the real
  `StSetRing` fills; in `END.EXE` they held the main game's leftovers and it crashed on
  an unmapped address.
- **`Runtime.AbandonExecutable()`** resets libcd, libcdstream and the XA router for an
  executable that is left by unwinding out of it (the quick load from the title) rather
  than by its own return.
- Extra `Log.Sdk` lines in `CdReady` and the callback setters, silent unless the `sdk`
  log channel is on.

## Timing and interrupts (`sdk/LibEtc.cs`, `Hardware/Interrupts.cs`)

- **Each vblank is delivered once.** `LibEtc.TickVBlank` delivered the RCntCNT3 event
  itself, and the IRQ 0 dispatch it makes delivered it again (`BiosB.DeliverIrqEvents`).
  King's Field III's whole clock is that event, so the game ran at twice its speed.
- **Vblanks keep coming while the game is not calling VSync.** On the grid timeline
  IRQ 0 came only from `LibEtc.AdvanceVBlanks`, called by VSync, so a loop without VSync
  stopped the game's clock and the music (the sequencer ticks from the vblank
  callback). The message box loops on PadRead and froze the music. `Interrupts.PollSlow`
  now calls `LibEtc.AdvanceVBlanksFromPoll` (inside its register snapshot), which
  delivers the grid's due vblanks once VSync has been absent for over two vblanks. With
  VSync running it does nothing.
- **`Runtime.ResetForBoot` is public**, for a port that calls `Entry.Run` itself and so
  catches `HardResetSignal` itself (System > Hard reset).

## BIOS (`Bios/BiosA.cs`, `Bios/BiosB.cs`)

- **`BiosB.SaveEvents` / `LoadEvents`** save and restore the 64-slot event table. It
  lives outside RAM, but games keep handles to it (`0xF1000000 | slot`) in RAM, so a
  save state must include it.

## Memory, input, settings

- **`Controller.Injected`**: a mask ANDed into the pad state after `InputManager` polls,
  for scripted input (`KFIII_AUTOPAD`).
- **`InputPage`** (`Host/Window/Settings/Sections/InputPage.cs`): hooks for a port to
  list its own rows on Settings > Input (actions instead of PlayStation buttons, extra
  keys, mouse settings). `InputPage.Pads` (default 2) set to 1 hides the Pad 1 / Pad 2
  tabs.
- **`ConfigManager.ViewDefaults`**: a game's own defaults for `interface.ini` keys
  (fullscreen, borderless, render scale, the frame counter, colours), used only where
  the file has no value yet.
- **`SettingsPopup.Select`** opens a given settings section the next time the popup
  draws (tests and shortcuts).
- **`RamProbe`**: `RECOMPONE_WATCH=<hex>` logs the recompiled call chains that write a
  word, once per distinct chain.

## Geometry and culling (`Hardware/Gte.cs`, `Gpu/GteVertexMap.cs`, `Gpu/GteDepth.cs`)

- **Sub-pixel back-face culling** (`GteDepth.PreciseCull`, `Gte.PreciseCullCorner`).
  Distant triangles are slivers that are flat in whole pixels, so the game's
  `NCLIP <= 0` test dropped front faces; with sub-pixel corners the gaps showed as
  black lines across far ground and missing pieces of far objects. A port tells the GTE
  where a routine keeps the vertex word of each corner; `Gte.Nclip` then reads each
  corner's recovered position from the vertex map and turns a lost front face into +1.
  It only ever rescues a face, never drops one.
- **Near-plane and guard-band clipping** for polygons with a clamped corner or larger
  than the GPU's 1023x511 limit, in view space, with camera-space facing in NCLIP. View
  positions and the projection each vertex was made with travel through the vertex map.
  The GTE's current OFX/H at draw time can be another projection's (the compass's), so
  each vertex carries its own.
- **`GteVertexMap.CurrentTag`**: a source tag recorded with every projected vertex and
  carried through the address map (`Attr.Tag`), so a port can mark what is being drawn
  (creature models).
- **`Gte.Rotation` / `Gte.Translation`** read accessors.

## Depth buffer

- **`GteDepth.SkySlots`**: ordering-table slots `0..SkySlots` never depth-test (it was
  hard-coded to slot 0). King's Field III's sky dome sits in slots 3 to 98 after two
  empty entries; with depth testing it is small and near and hid everything above the
  horizon.
- **`Gte.DepthScale`** scales the published Z of what is drawn under it. A uniform W
  scale per polygon leaves the perspective unchanged; the port pushes the ground back
  (1.04) and pulls the weapon forward (0.25).
- **`RenderPrimEvent.NoDepth`** lets a listener clear a primitive's depth (`HasZ`), for
  a GTE-projected HUD element such as the compass.

## Anti-aliasing (`GteDepth.Msaa`, `GlDisplayRt.MsFbo`, `GlCore`)

MSAA on the two display targets only (VRAM, the planar reflection target and the
OpenGL 2.1 path stay single-sample). The game draws into `rt.MsFbo`; `rt.Tex` and
`rt.Depth` remain the resolved copy every other reader uses.

- Reads resolve first (`MsResolve`, colour and depth, when `MsNewer`): writeback,
  present (before AO and reflections), snapshots, CopySnap, depth capture. A batch that
  samples its destination (mask bit, subtractive blend) resolves only its rectangle
  (`MsResolveRect`).
- Writes into `rt.Tex` (VRAM syncs, snapshot restores) are copied into every sample over
  the written rectangle (`MsUpload`, `MsCopyFs`); `FillRtFull` clears both.
- `MsSync` makes or drops the buffers when the setting changes.
- `centroid` interpolation on `vUV` (`PrimVs`/`PrimFs`): with sub-pixel corners, a
  partly covered edge pixel was shaded at its centre, outside the quad, and sampled the
  texel row next to the art (a yellow line over the fire torches).

## Textures

- **The mip atlas (`GlTexCache`) is 4096x4096**, not 2048x2048. Entries are per UV
  rectangle, and a King's Field III scene filled 2048x2048 within one frame (522
  entries, about 100 polygons a second refused); 4096 holds about 1500 with none
  refused. The entry flags are repacked (block x/8 in bits 0-8, y/8 in 9-17, log in
  18-21), with the shader reads to match.
- **`winUV()` in `PrimFs`**: the anisotropic and mip taps go through the texture window
  too (they clamped raw coordinates to the rectangle).

## Fog (`GteDepth.SmoothFog`)

King's Field III computes its own linear ground fog rather than using the GTE's depth
cue, and applies it per corner with DPCS, so the fog edge followed the tiles.

- `Gte.FogLinearStart` / `FogLinearEnd`, set by a port around the ground routines:
  `PublishSxy` attaches the unclamped linear fog to each coordinate word (`Attr.FogRaw`).
- `Dpcs` remembers its input colour, and reading RGB2 (`Read(22)`) publishes the
  result with it (`GteVertexMap.PublishColor`, matched on 24 bits by `PeekColor`
  because the first colour word is also the command word).
- `GpuRaster`: when every corner has both, the polygon carries the pre-fog colour and
  the raw fog (curve 5), and the shader fades per pixel,
  `pre + (FC - pre) * clamp(fog, 0, 7951) / 4096`, with the fog interpolated
  perspective-correct. A corner that lost its match takes the others' colour when they
  agree (one light per face).
- The near-plane clip interpolates these inputs along the cut edges (`ClipV`) instead
  of copying the first corner's.

## Per-pixel lighting (`GteLightMap`)

Lighting is captured at the GTE, not in a polygon builder.

- `Gte.NcdsOp` / `NcdtOp` record per vertex the three light dot products (before the
  clamp), RGBC, IR0 and the light-set generation, in a FIFO shifted with the RGB FIFO.
  Each record is checked by running the shader's formula on the CPU against the colour
  the GTE produced (`LitCheck`, within two levels a channel); only matching records are
  used, so a mismatch draws the game's own colour. IR0 is taken unclamped, because the
  game's linear fog drives it past 4096.
- Reading RGB0-2 publishes the record on the colour word (`PublishLit` / `PeekLit`).
  `PublishSxy` also attaches each vertex's hardware depth cue (`Attr.Cue`).
- `GpuRaster` lights per pixel when every corner has a record from one light set.
  Fog: inside the ground routines the linear ground fog; otherwise each vertex's own
  cue (curve 6, new) when it agrees with the game's IR0; otherwise the face's IR0.
- **Light-set generations** (`NoteConstants`): the game switches between the world,
  compass and item light sets about ten times a frame, and a busy scene makes about
  twenty new sets a second. A set already in the 32-slot ring keeps its generation, the
  least recently *used* slot is replaced, and a generation is `(serial << 5) | slot`.
  Evicting by age overwrote the compass's set mid-frame (the compass and trees blinked).

## Ambient occlusion

- The normal pass samples the target's depth (`uSceneDepth`) and discards an opaque
  fragment more than 3% nearer than it: that fragment lies over a hole of a cut-out
  polygon (a tree card), which the untextured normal pass otherwise drew as a solid
  box. The same discard applies where the scene depth is 1 or more (`sd >= 1.0`), over
  the void past the draw distance seen through water.
- **`SurfaceMaterial.Creature`** (4, reflectivity 0) for polygons tagged as creatures,
  and `SurfaceMaterial.Classify` maps it to opaque, so creature bodies cover the
  occlusion of what is behind them.

## Water and reflections

- **`Gpu/WaterUv.cs`** and its block in `GpuRaster.DrawPolygon`: world-aligned texture
  coordinates for a port's water polygons, under a texture window. The orientation of
  each 2048-unit tile is decided by its most decisive face (`WaterUv.Orientation`).
  Tagged creature polygons are skipped.
- **`WaterUv.Defer`**: water triangles (semi-transparent, the port's water texture,
  with depth) are held back and drawn at the end of the ordering table
  (`LibGpu.DrawOTag` → `FlushDeferred`) against the depth buffer, so what lies under the
  water shows through it and what stands in front covers it. Flushed early before
  anything flat (the HUD), any VRAM transfer or fill, and a present.
- **Screen-space reflections** (`SsrFs`): the march is a function; a ray that passes
  behind a surface nearer the camera than its water point takes a weighted blend of the
  reflections of the clear water to both sides (offsets `3*k*k` game pixels, k 1..8,
  weight 1/k), so a bank's reflection continues softly behind a tree instead of
  striping.
- **`GlCore.Invisible`**: an additive or subtractive primitive with a black colour is not
  recorded as an overlay in the surface buffer (faded-out message text was cutting a
  strip out of the water's reflections).
- **`PlanarReflections`**: `Drop2D` drops every primitive without a projected corner
  while capturing (HUD, text); `SetCamera` takes the plane from the port; `Capturing`
  tells the GPU which target it draws into.

## Presentation and VRAM snapshots (`GlCore`, `GlVram`)

- **`CopySnap`**: when the game copies the displayed frame into VRAM (behind signs and
  messages, a 320x240 copy), the runtime also blits the source display target (scaled,
  margins included) into a texture. A 15-bit primitive sampling inside the copied
  rectangle uses it through the replacement-texture path, and its corners on the
  drawing area's edges are pushed out over the margins, so the frozen frame stays sharp
  and wide. Dropped on any upload, fill, copy or untargeted draw over the rectangle.
- **`VramSnap` keeps the wide target** (`SnapTakeWide` / `SnapRestoreWide`) when a
  readback was a whole display buffer, and blits it back after the restore. The shop
  reads the frame back on opening and uploads it every frame; only the middle 320
  columns came back, and the margins blinked with stale HUD.
- **Display targets are kept while in use.** `PresentDisplay` destroyed targets idle
  for over 300 presents, which at high frame rates is a few seconds, and a dialog draws
  nothing: dialogs and shops fell back to 4:3. The target holding the display area, and
  wide targets that latched margin content, are no longer idled out.
- The shaders no longer wrap UVs in the replacement-texture path when no texture window
  is set.

## Movies (`Hardware/Mdec.cs`, `sdk/LibPress.cs`, `GlShaders`)

- `Mdec.DecodeAll` and `LibPress.DecDCTout` stamp `LibPress.LastDecodeTick`.
  `PresentDisplay` uses `PresentMovieFs` for the 4:3 VRAM fallback when the source is
  15-bit, `GpuHle.MovieFilter` is on and a frame was decoded in the last 500 ms: a
  Catmull-Rom bicubic of the 1x pixels, clamped to the 2x2 range (no ringing), blended in
  flat areas toward a tent average, which softens the 8x8 blocks and 15-bit banding and
  keeps edges. Strength `GpuHle.MovieSmooth`, 0.7.

## SPU (`Hardware/Spu.cs`)

- **An ADPCM block's end flags act once the block has played.** The SPU acted on them
  when it decoded the block, so an end without repeat muted the voice before its last
  28 samples, which are where a sound fades out: the sound was cut mid-wave and popped.
  `DecodeBlock` stores the flags in `Voice.BlockFlags` and `EndBlock` acts on them, as
  DuckStation does. Loop-start still sets the repeat address at decode time.
- **`Spu.SilenceAllVoices()`** turns every voice off and drops pending key-ons, for a
  save-state load.
- **`Spu.DumpRecentKeyOns`** returns the last ten seconds of key-ons (voice, sample
  address, pitch, whether a sounding voice was taken), for the F9 sound report.
- `RECOMPONE_SPU_CLICKS=1` logs voices cut while loud, with their last 64 ticks.

## Files

| Change | Files |
|---|---|
| Fall-through after a final call | `RecompOne.Recompiler/CodeGen/FunctionEmmiter.cs` |
| CD and streaming | `sdk/LibCd.cs`, `sdk/LibCdStream.cs`, `Runtime.cs` |
| Vblank delivery | `sdk/LibEtc.cs`, `Hardware/Interrupts.cs` |
| Event table in save states | `Bios/BiosB.cs` |
| Scripted input, input page, view defaults | `Hardware/Controller.cs`, `Host/InputManager.cs`, `Host/Window/Settings/Sections/InputPage.cs`, `InputSettingsSection.cs`, `SettingsPopup.cs`, `Config/ConfigManager.cs` |
| GTE capture, culling, clipping | `Hardware/Gte.cs`, `Gpu/GteVertexMap.cs`, `Gpu/GteDepth.cs`, `Gpu/GpuRaster.cs` |
| Lighting | `Gpu/GteLightMap.cs` |
| AA, snapshots, presentation, water deferral | `Gpu/Backends/Common/GlCore.cs`, `GlDisplayRt.cs`, `GlShaders.cs`, `GlTexCache.cs`, `GlVram.cs` |
| Water and reflections | `Gpu/WaterUv.cs`, `Gpu/ScreenReflections.cs`, `Gpu/PlanarReflections.cs`, `Gpu/SurfaceMaterial.cs` |
| Movies | `Hardware/Mdec.cs`, `sdk/LibPress.cs`, `Gpu/Hle/GpuHle.cs` |
| SPU | `Hardware/Spu.cs` |
| Watchpoints | `Memory/RamProbe.cs` |
