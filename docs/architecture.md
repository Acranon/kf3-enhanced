# Architecture

This is a map of the port for a developer new to it: how the game's code becomes a
Windows program, how the runtime stands in for the PlayStation, how the patches attach
to the game, and where to start for common tasks. The conventions it refers to are in
[CONTRIBUTING.md](../CONTRIBUTING.md); the facts about the game are in
[research/](research/README.md).

## The pieces

```
disc image ──► RecompOne.Recompiler ──► generated/*.cs ─┐
config/kf3.json, config/funcmaps/*.json                  ├─► KingsFieldIII.exe
tools/RecompOne/RecompOne.Runtime ───────────────────────┤
Program.cs, patches/**/*.cs ─────────────────────────────┘
```

- **The recompiler** (`tools/RecompOne/RecompOne.Recompiler`) translates the game's
  MIPS code, function by function, into C# methods that operate on a `CpuContext`
  (the registers) and an `IMemory` (RAM, scratchpad, hardware registers).
- **The runtime** (`tools/RecompOne/RecompOne.Runtime`) is the PlayStation the
  generated code runs on: BIOS calls, the PSY-Q SDK, GPU, GTE, SPU, CD-ROM, MDEC,
  controllers, memory cards, timers and interrupts, plus the window, settings UI and
  input of the host.
- **The patches** (`patches/`) are this project's C#: they replace or wrap particular
  game functions, listen to runtime events, and draw their own overlays.
- **`Program.cs`** is the entry point. It installs the patches, sets the port's
  defaults, gives the game an 8 MB machine, and runs the recompiled boot code.

## Recompilation

### Executables and overlays

The disc boots `SLUS_002.55`, a 2 KB loader that loops loading and executing three
executables in turn: `OPEN.EXE` (title, intro, memory-card load), `GAME.EXE` (the game)
and `END.EXE` (the ending). All three load at `0x80011000` and so overwrite each other:
they are overlays in RecompOne's terms. The game's areas add another layer: `GAME.EXE`
loads one area's code module at a time from `CD/COM/FDAT.T` to `0x801E8308`.

`config/kf3.json` declares all of them:

| Overlay | Source | Base |
|---|---|---|
| `main` | `SLUS_002.55` (the boot stub) | its PS-X EXE header |
| `open`, `game`, `end` | `OPEN.EXE`, `GAME.EXE`, `END.EXE` (header skipped) | `0x80011000` |
| `fdat02` .. `fdat83` | the 28 area modules in `CD/COM/FDAT.T`, at their offsets | `0x801E8308` |

Area *n* is module `fdat` + (2 + 3*n*): `FDAT.T` holds a (map, props, code) triplet per
area, and only the code entries are overlays.

Each overlay has a function map in `config/funcmaps/`: every function's address, size
and name. The executables' maps came from the recompiler's autoconfigure (a linear
sweep plus a PSY-Q signature bank that named about 600 library functions); the area
maps from `scripts/gen_fdat.py`. The recompiler emits one C# method per entry, so the
map must be right: a function split in the wrong place returns early in C# where the
MIPS code would have carried on (see `scripts/research/find_fallthrough.py` and
[runtime-changes.md](runtime-changes.md)).

Calls between functions go through the runtime's dispatcher by address
(`Dispatcher.Call(c, m, 0x8001A438)`), which finds the method in whichever overlay is
loaded at that address. `Program.cs` listens for `OverlayLoadedEvent` to set each
executable's interrupt-callback table and to unload the area module when `OPEN.EXE` or
`END.EXE` replaces `GAME.EXE` (the module lies outside the executables' range, so the
swap alone would not drop it).

### Names

Generated methods are named after their funcmap entry. Two scripts keep the names:

- `scripts/name_sdk.py` names PSY-Q functions that autoconfigure missed. The runtime's
  high-level emulation (HLE) of the SDK binds by exact PSY-Q name: naming a function
  `CdSync` makes the runtime's `CdSync` run in its place. A function whose contract in
  this game differs from the SDK's is given a different name, so that it is not bound,
  and shimmed by address instead (`CdRead_ret0`, see below).
- `scripts/name_game.py` names the game's own functions `kf_*` and library functions
  identified by behaviour `lib_*`. Neither prefix can collide with a PSY-Q name, so
  these names never bind the HLE. They make stack traces, crash logs and `[watch]`
  output readable (`kf_DrawModel < kf_DrawObjects < kf_Render`).

RAM addresses get the same treatment in `patches/Core/GameAddr.cs`.

## The runtime

The runtime runs the generated code on the game thread and emulates the hardware
around it.

- **BIOS and SDK.** BIOS calls (`BiosA`, `BiosB`: events, files, memory cards) are
  implemented in C#. The runtime emulates PSY-Q library functions (`libcd`,
  `libcdstream`, `libgpu`, `libetc`, `libpress`, `libpad`, `libapi`, `libmcrd`,
  `libds`) in `RecompOne.Runtime/sdk/`, bound by name as above. Functions
  that are not bound run as recompiled code against the emulated hardware registers.
- **GPU.** GP0 packets are rasterised with OpenGL at a multiple of the console's
  resolution, with a VRAM model kept in step for uploads, copies and readbacks.
- **GTE.** The geometry coprocessor is emulated exactly, and also records what the
  console throws away: each projected vertex's sub-pixel fraction and view depth
  (`GteVertexMap` follows the stored word into the GP0 packet). The GPU end looks it up
  again, which is what makes stable geometry, perspective-correct textures, the depth
  buffer, smooth fog, per-pixel lighting and ambient occlusion possible.
- **SPU.** The sound processor is emulated voice by voice (ADPCM, envelopes, reverb),
  with a spatial mixer (`SpuSpatialVoice`) that directional sound drives.
- **Host.** A Silk.NET window with an ImGui interface: F1 shows the top bar (System,
  Settings, Debug), F11 toggles fullscreen. Settings are saved to `settings.json` and
  `interface.ini` in the working directory.

The port's changes to the runtime are listed in
[runtime-changes.md](runtime-changes.md).

## How patches attach

### Hooks into game functions

`scripts/name_sdk.py` holds two tables that it writes into the `patches` list of
`config/kf3.json`:

- `SHIMS`: SDK functions replaced by a shim in `patches/Core/SdkShims.cs`, in all three
  executables.
- `HOOKS`: `(overlay, address, "KfIII.Class.Method", mode)` entries, each with a comment
  naming the patch and the function.

The recompiler bakes these into the generated code, so a hook change needs a
recompile. The modes:

| Mode | Generated code | Method |
|---|---|---|
| `pre` | calls the method on entry; if it returns `false`, the function returns at once | `void` or `bool M(CpuContext c, IMemory m)` |
| `post` | calls the method after the function body | `void M(CpuContext c, IMemory m)` |
| `replace` | the function's method forwards to the patch | `void M(CpuContext c, IMemory m)` |

A hook reads and writes the MIPS state directly: arguments in `c.A0`..`c.A3` (and on
the stack at `c.SP + 0x10` on), the return value in `c.V0`, RAM through `m`. A
`post` hook sees the registers as the function left them, so arguments it needs must be
saved by a matching `pre` hook.

`name_sdk.py` removes every `KfIII.*` entry before writing, so a hook deleted from
`HOOKS` does not linger in the config.

### Runtime events

Patches that do not need to be inside a game function listen to the runtime's events
(`Event.AddListener<T>`):

| Event | Used for |
|---|---|
| `OverlayLoadedEvent` | An executable or area module was loaded: per-area state, timers anchored to the first area. |
| `PadReadEvent` | The pad is being read: the port's own keys (run, jump, second walk keys), mouse buttons, scripted input, menu mouse clicks. |
| `KeyboardEvent`, `MouseEvent` | Fixed keys (F2, F4, F9, Esc), mouse capture, map zoom. |
| `RenderPrimEvent` | Each primitive before it is drawn: moving the HUD in widescreen. |
| `RuntimeReadyEvent` | The runtime has loaded its settings: applying the port's key layout. |

`Program.cs` installs the patches in a fixed order, which is also the order their
listeners run in.

### Settings

`patches/Settings/Enhancements.cs` holds every player-facing switch in one `Settings`
class, saved to `enhancements.json`. `Apply()` pushes the current values into the
runtime (`GteDepth`, `GteLightMap`, `Display`, ...) and into the patches, and works out
Classic mode: full classic turns off the picture enhancements and the additions,
classic + visuals only the additions. The pages are registered with the
runtime's `SettingsRegistry` (`Register` for the Classic mode, Quality of life and Map
pages, `Extend` for the "Picture" group on Display and the "Sound" group on Audio);
labels and hints are localisation keys (`kf3.enh.*`). The key layout page
(`ControlsPage.cs`) goes through the runtime's `InputPage` hooks.

Environment switches (`KFIII_*`, read through `patches/Debug/DevSwitch.cs`) override
the settings for tests; see [testing.md](testing.md).

### Overlays drawn by the port

The map, damage numbers, stat progress, the SPECIAL box and key hints are ImGui
drawing over the game picture, registered with the runtime's `PanelManager`.
`patches/Interface/ScreenOverlay.cs` holds what they share (the widescreen margin, the
panel style). Additions inside the game's own menus (the equipment stats) are drawn
with the game's own menu routines instead (`patches/Interface/MenuDraw.cs`), so they
look like the rest of the screen.

## The frame and tick model

The game's world runs at **15 Hz**. The clock is a vblank event: at boot `GAME.EXE`
opens an `RCntCNT3` interrupt event whose handler counts vblanks at `0x801C12EC` (since
the last tick), `0x801C12E8` (total) and the play-time minutes. The main loop
(`func_80014BD4`) runs the world update `kf_WorldUpdate` (`0x80030FCC`), builds the
camera, and calls the renderer `kf_Render` (`0x800422B8`). The renderer draws, presents
(`kf_Present` → VSync) and then waits in the frame gate `kf_FrameGate` (`0x80019614`)
until the counter reaches 4: four 60 Hz vblanks to a tick.

High frame rate (`patches/Graphics/Smooth*.cs`) replaces the frame gate. While the
counter is short of 4, the gate calls the renderer again, nested, with the camera
interpolated between the last two ticks, and so does every creature, prop and spell
effect, their animation time, and the weapon swing. The world update still runs once a
tick, so the game's speed is unchanged; only drawing repeats. Interpolation shows tick
N-1 to N while tick N is current, so the picture is one tick (67 ms) behind, except
for mouse look, which is applied between ticks without that delay.

The renderer advances some state on every call (model animation frames, the render
counter that drives water and texture animation, the on-screen message timer). A
redraw saves that state before and restores it after (`Smooth.TickState.cs`), or water
would flow and messages would fade several times too fast. When adding anything that
runs inside the renderer, check it with `KFIII_SMOOTH_DIFF`, which lists the RAM a
redraw changed.

Quick save (`patches/Core/SaveState.cs`) depends on the same loop: a whole-machine
state is taken and applied only at the entry of `kf_WorldUpdate`, where the C# call
chain is always the same and no recompiled function holds C# locals.

## The patches folder

**Core.** `GameAddr.cs` names every RAM address the patches use. `SdkShims.cs` holds the
`CdRead` shim (this game's `CdRead` takes `(buf, sectors, mode)` and returns 0 on
success). `SaveState.cs` and `SaveState.Title.cs` are the quick save and load,
including loading from the title by leaving `OPEN.EXE` early. `Pools.cs` moves the
game's two primitive pools above 2 MB and enlarges them, since the wide view overflows
them. `MovieSkip.cs` ends a movie through the player's own end-of-stream path.
`EndScreen.cs` keeps "The End" on screen and returns to the title on a key press
instead of spinning forever.

**Graphics.** `Visuals.cs` switches the runtime's picture features and sets the aspect
ratio. `Culling.cs` tells the GTE where each drawing routine keeps its corners so
back-face culling uses the sub-pixel positions. `WideCull.cs` widens the game's view
cone and libgte's subdivision clip for widescreen; `Hud.cs` moves the HP panel and
compass to the picture's edges. `Smooth.cs` (with `Smooth.Objects.cs`,
`Smooth.Effects.cs`, `Smooth.TickState.cs`) is the high frame rate. `Water.cs` makes
the river seamless and tags creature models so they are never taken for water;
`Mirror.cs` runs the renderer a second time from the mirrored camera for planar
reflections.

**Audio.** `PositionalAudio.cs` follows each sound the game positions (`kf_Sound3D`)
and each area ambience to its voice, and re-aims it every frame through the runtime's
spatial mixer.

**Input.** `Controls.cs` sets the keyboard layout, the run and jump keys, the second
walk keys and the mouse buttons; `Mouse.cs` is mouse look and its smoothing. `MenuMouse.cs`
drives the game's menus and shops with the mouse by writing the list's highlighted row
and the selected button.

**Gameplay.** `Jump.cs` starts the game's own long fall with an upward speed.
`AutoKey.cs` runs the game's own key routines when you interact with a lock whose key
you carry.

**Interface.** `GearCompare.cs` draws the equipment stats on the equipment lists and in
shops, by running the game's own `kf_RecalcStats` with the piece equipped. `GearEffects.cs`
lists the hard-coded effects of particular items; `GearSpecial.cs` shows them in the
SPECIAL box. `StatProgress.cs` is the panel beside the status window. `DamageLog.cs`
compares creature HP tick to tick. `KeyHints.cs` writes the bound keys under the menus'
button hints. `EquipSlot.cs`, `MenuDraw.cs` and `ScreenOverlay.cs` are shared helpers.

**Map.** `Automap.cs` reads the world: the tile records, the game's own collision test
sampled across each tile, the props and creatures that become markers, the area exits.
`Automap.Markers.cs`, `Automap.Scan.cs`, `Automap.Liquids.cs` and
`Automap.Persistence.cs` hold the markers, the sampling, water and lava detection, and
the `.seen` files. `AutomapView.cs` draws the
minimap and the full map. `CreatureNames.cs` is generated by
`scripts/gen_creature_names.py`.

**Settings.** `Enhancements.cs` is the settings class and the Classic mode, Display,
Audio, Quality of life and Map pages. `ControlsPage.cs` is Settings > Input >
Keyboard/Mouse.

**Debug.** `DevSwitch.cs` reads the `KFIII_*` switches. `TestHarness.cs` holds the
process-wide test tools (scripted pad input, log channels, timed reset, rate probe).
`Teleport.cs` is Debug > Teleport to area. `GpuCapture.cs` writes a frame's GP0
polygons to a file; `AudioRecord.cs` records the mix to a WAV; `Diag.cs` traces archive
loads, VRAM transfers and other probes.

## Where to look first

**Add an address.** Prove it first (a watchpoint with `RECOMPONE_WATCH=<hex>`, a RAM
diff with `scripts/research/savestate.py --diff`, or the disassembly with
`scripts/research/mipsdis.py`). Add a constant to `patches/Core/GameAddr.cs` with a
one-line comment saying what it holds, and write the evidence into the right file in
`docs/research/`.

**Add a hook.**

1. Find the function and name it in `scripts/name_game.py` (`0xADDRESS: ("kf_Name",
   "one line of evidence")`).
2. Write the method in the patch: `public static void AfterThing(CpuContext c, IMemory
   m)`, with a comment naming the function and whether it runs before or after.
3. Add `("game", "0xADDRESS", "KfIII.Patch.AfterThing", "post")` to `HOOKS` in
   `scripts/name_sdk.py`, under a comment naming the patch.
4. Run `python scripts/name_sdk.py`, recompile (`dotnet run --project
   tools/RecompOne/RecompOne.Recompiler -c Release -- config/kf3.json`) and build.

For a hook in an area module, the overlay is `fdatNN` and the address is the module's.

**Add a setting.**

1. Add a property with its default to `Enhancements.Settings`.
2. Push it out in `Enhancements.Apply()`, deciding how Classic mode treats it (`on` for
   picture features, `qol` for additions).
3. Draw it on the right page in `Enhancements.cs` and add its label and hint to the
   localisation strings at the bottom of the file (`kf3.enh.<name>`, `<name>_hint`).
4. If a test needs to force it, read a `KFIII_*` switch through `DevSwitch` and list it
   in [testing.md](testing.md).

**Add a map marker.**

1. Find what identifies the object: its prop class (byte 0 of the type's definition at
   `0x8018FB3C + type * 24`) or creature type, and the record fields that describe its
   state. `KFIII_PROP_CLASS=<hex>` lists the props of a class; `KFIII_PROP_WATCH=<prop>`
   logs a record as it changes; `KFIII_ENT_PROBE=1` dumps the live creatures.
2. Add a `MarkerKind` and collect it in `CollectMarkers` (`Automap.Markers.cs`); decide whether it is
   on the base map (shown once seen) or a cheat (gated by one of the `Show*` flags).
3. Draw it and give it a legend entry and a hover label in `AutomapView.cs`.
4. Make a save state beside it in `tests/saves/` and check it with
   `KFIII_TEST_FULLMAP` and `KFIII_TEST_MAPHOVER`.

For the props and their classes see [research/world.md](research/world.md).
