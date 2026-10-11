# Testing

The port is tested by running the game: from a save state at the place in question,
with scripted input, environment switches that force a setting or start a test, and
screenshots or log lines to check the result. Nothing runs unattended in the build
itself. This page describes the pieces and the habits that keep test runs away from the
player's own files.

## Save states

A test usually starts from a quick save (F2) made at the spot it is about: in front of
a shop, beside a trapped chest, on a bridge over lava. They live in `tests/saves/`,
named for the bug or place each was made for (`slimes.kfstate`, `throne.kfstate`,
`quicksave_bridge.kfstate`). The research notes cite them as evidence.

They are **kept locally and not committed**: a save state holds the whole machine,
including the game's RAM and VRAM, which is game content. `.gitignore` excludes
`tests/saves/` and every `*.kfstate`.

To make one: play to the spot in a test run, press F2, and copy the run's
`quicksave.kfstate` to `tests/saves/<name>.kfstate`.

To load one at the start of a run, copy it to the run's working directory as
`quicksave.kfstate` and set:

| Switch | What |
|---|---|
| `KFIII_TEST_QUICK=load@N` | Press F4 N seconds after start. From the title this skips straight into the save (about 2 s). |
| `KFIII_TEST_QUICK=save@N` | Press F2 N seconds after the first world update. |
| `KFIII_TEST_QUICK=load@3,save@25` | Both, comma-separated. |

A state is applied at the next world update, so a load requested in a menu or a movie
waits until the game is walking around again. States of version 1 (before the BIOS
event table was saved) still load.

## Isolation

The game keeps its settings, memory cards, quick save and map memory in its working
directory. A test must never read or write the player's:

- **Run from a separate working directory.** `scripts/test/shot.ps1` and
  `scripts/test/autorun.ps1` start the game in `tests/run/` by default, so it reads and
  writes that folder's `quicksave.kfstate`, `settings.json`, `interface.ini`,
  `enhancements.json`, `carda.sav` and `automap/`. A test that reveals or samples the map
  writes `.seen` files there, not into the player's map memory.
- **Build to a separate output** when the normal build may be in use (the player may be
  playing it): `dotnet build KingsFieldIII.csproj -c Release -o <scratch folder>`, then run
  that exe with `shot.ps1 -Exe <scratch>\KingsFieldIII.exe`.
- **Copy a player's save, never overwrite it.** To reproduce a report from the player's
  quick save, copy their `quicksave.kfstate` into `tests/saves/` under a new name and
  test from the copy. The same goes for their `automap/` files; back them up before any
  change that rewrites them.

A fresh `tests/run/` has no `settings.json` or `interface.ini`, so a run there starts
with the port's defaults (borderless fullscreen at render scale 8). It also has no
disc path: the runtime takes the disc only from `CdPath` in `settings.json` (the `.cue`
given on the command line is not used), so a run in an empty folder stops at the
"Disc image not found" prompt. Copy a `settings.json` with `CdPath` set into
`tests/run/` once.

## scripts/test/shot.ps1

Runs the game with environment switches, captures its window at given times, then
stops it.

```powershell
powershell -File scripts\test\shot.ps1 -Name throne -Save throne -At 8,12 -Seconds 13 `
    -Env @{KFIII_TEST_QUICK="load@4"}
```

| Parameter | What |
|---|---|
| `-Name` | Names the logs and screenshots. |
| `-Save` | Copies `tests\saves\<name>.kfstate` into the working directory as `quicksave.kfstate` first. |
| `-At` | Seconds after start at which to take a screenshot. |
| `-Seconds` | When to stop the game (default 30). |
| `-Env` | Environment switches for this run only. |
| `-Exe` | Another build to run (default `bin\Release\net10.0\KingsFieldIII.exe`). |
| `-WorkDir` | Another working directory (default `tests\run`). |
| `-Millis` | Name screenshots in milliseconds instead of tenths of a second. |
| `-ClickAt`, `-CX`, `-CY` | Left-click at window position (CX, CY) at that time. |

Screenshots go to `tests\shots\<Name>_<t>.png` (t in tenths of a second), console output
to `tests\logs\<Name>.out` and `.err`. The capture uses `PrintWindow`, so it works even
when another window covers the game. Windows only.

## scripts/test/all_saves.ps1

The regression pass: quick-loads every save in `tests\saves` in turn (`-Filter` takes a
wildcard for some of them), lets the world run, takes a screenshot
(`tests\shots\all_<save>_100.png`) and checks the log: the load must succeed, with no
exception and no `crash.log`. It prints one line per save and writes
`tests\logs\all_saves.csv`. About 15 seconds a save; the game window takes over the
screen while it runs. Look over the screenshots too: a clean log does not catch a black
or broken picture.

## scripts/test/autorun.ps1

Runs the game with scripted pad input and reports how far it got.

```powershell
powershell -File scripts\test\autorun.ps1 -Pad "4:Start:150,fdat+20:Circle:150" -Seconds 200 -Name auto1
```

`-Pad` sets `KFIII_AUTOPAD`, `-Log` sets `KFIII_LOG` (runtime log channels: `bios`, `cd`,
`gpu`, `dma`, `sdk`, `spu`, `mdec`, or `all`). Output goes to `tests\logs\<Name>.out` /
`.err`. If the game is still running at the end, the script prints three stack samples
of the game thread (with `dotnet-stack`: `dotnet tool install -g dotnet-stack`), then
stops it, and lists the milestones from the log and the first lines of `crash.log`.
Recompiled functions carry their MIPS address or `kf_` name in their method name, so a
stack sample names the routine a hang is in.

### KFIII_AUTOPAD

A comma-separated list of steps, each `[anchor+]seconds:button:holdMs`:

```
KFIII_AUTOPAD=12:Cross:150,game+3:Start:150,fdat+10:Triangle:150
```

With no anchor the time counts from process start; `game+` counts from `GAME.EXE`
loading and `fdat+` from the first area module loading, so a script does not drift
with movie and loading times. Buttons are the PlayStation's (`Cross`, `Circle`, `Square`,
`Triangle`, `Start`, `Select`, `L1`, `R1`, `L2`, `R2`, `Up`, `Down`, `Left`, `Right`). Held buttons go through the
runtime's `Controller.Injected`, so the keyboard still works alongside.

A script that reaches the first area from boot:

```
4:Start:150,6:Cross:150,8:Start:150,10:Cross:150,fdat+8:Cross:150, ... (Cross every 4 s to fdat+40)
```

Do not tap Start once in the game: it pauses. A save state with `KFIII_TEST_QUICK` is
usually the quicker way in.

## Logs and crashes

- Console output: `run.log` beside `run.cmd` for a normal run; `tests\logs\` for the test
  scripts. The port's lines are tagged `[KFIII]` or `[KFIII-<AREA>]`.
- An unhandled exception is written to `crash.log` beside the exe before the process
  ends, since redirected console output has been seen to lose it.
- F9 appends the last 10 seconds of sound key-ons (voice, sample address, pitch, whether
  it took a sounding voice) to `sound_report.txt` in the working directory, for "that
  sound did not play" reports.

## Tools for finding things

| Tool | What |
|---|---|
| `RECOMPONE_WATCH=<hex>` | Logs the recompiled call chains that write a word, once per chain. |
| `KFIII_WATCH_RANGE=addr,len` | Logs the halfwords in a range that change each tick. |
| `KFIII_TICK_DIFF=<s>` | From that time, lists halfwords changed tick to tick. |
| `KFIII_SMOOTH_DIFF=1` (or `=<s>`) | Lists the RAM a redraw changed, to find per-draw state. |
| `KFIII_TEST_POKE=secs;addr=byte,...` | Writes RAM once at that time. |
| `KFIII_GPU_CAPTURE=<s>` | Writes one frame's GP0 polygons to `gpu_capture.txt`; `KFIII_GPU_CAPTURE_FRAMES=n` records n frames. |
| `KFIII_WAV=<path>` | Records the final mix to a WAV file. |
| `scripts/research/savestate.py --diff a b` | Diffs the RAM of two save states. |

## Known flake

The game sometimes hangs at start-up inside GLFW's `GetGamepadState` (input creation)
when a HID game controller is connected. Retry the run; it is not the port.

## Switches

Every switch does nothing unless it is set. Patch switches are `KFIII_*`, read through
`patches/Debug/DevSwitch.cs`; runtime switches are `RECOMPONE_*`. Where a switch and a
setting disagree, the switch wins.

A value of `0` in the rendering-internals table turns off something that is normally
on, for a before/after comparison. The runtime's own switches are listed in
[runtime-changes.md](runtime-changes.md).

### Test harness

| Switch | Value | What it does |
|---|---|---|
| `KFIII_AUTOPAD` | `[anchor+]s:Button:holdMs,...` | Scripted pad input; anchors `game` and `fdat` count from GAME.EXE / the first area loading |
| `KFIII_TEST_QUICK` | `save@N`, `load@N` | Quick save N s after the first world update; press F4 N s after start |
| `KFIII_QUICKLOAD_TITLE` | `menu` | F4 on the title drives the menu with Start/Cross taps instead of jumping to GAME.EXE |
| `KFIII_TEST_RESET` | `s[,s...]` | System > Hard reset at those times |
| `KFIII_TEST_TELEPORT` | `secs,area` | One debug teleport to that area |
| `KFIII_TEST_GOTO` | `n` or `en` | At tick 14, stand facing prop n (or entity n) on open floor |
| `KFIII_TEST_GOTO_DIST` | units | How far from it (default 5120) |
| `KFIII_TEST_YAW` | hex | Set the view yaw at tick 15 |
| `KFIII_TEST_POKE` | `secs;addr=byte,...` | Write those bytes (hex) once, after that many seconds |
| `KFIII_TEST_EQUIP` | hex weapon id | Equip that weapon at the first world tick |
| `KFIII_TEST_USEITEM` | `secs,hexitem` | List the bag, then use the item as the menu does |
| `KFIII_TEST_MSGS` | `start,first,last[,ms]` | From `start` s, queue messages first..last, one every ms (default 1000) |
| `KFIII_TEST_JUMP` | `s[,s...]` | Jump at those times |
| `KFIII_TEST_RUN` | `from-to` | Hold run between those seconds |
| `KFIII_TEST_MOUSE_DX` | `px/s,from,to` | Fake mouse turn between those seconds |
| `KFIII_TEST_MOUSE_DY` | px/s | Steady fake vertical mouse movement |
| `KFIII_TEST_MENUPOINT` | `secs:x,y;...` | Virtual menu pointer (320x240) from those seconds after the first area load |
| `KFIII_TEST_SETTINGS` | `secs[,section]` | Open Settings at that page (`display`, `audio`, `kf3.classic`, `kf3.qol`, `kf3.map`; Input by default) |
| `KFIII_TEST_SETTINGS_END` | `1` | Scroll the Keyboard/Mouse page to the bottom |
| `KFIII_TEST_CLASSIC` | `s[,s...]` | Step Classic mode (off, full, visuals) at those times, in order |
| `KFIII_TEST_MOVIEBLINK` | `1` | Movie filter on only in even half-seconds (A/B in one recording) |
| `KFIII_TEST_FULLMAP` | `s` or `s-s` | Open the full map at the first time, close it at the second |
| `KFIII_TEST_MAPZOOM` | zoom | The full map opens at that zoom, centred on the player |
| `KFIII_TEST_MAPWHEEL` | `secs:notches` | Inject that many wheel notches at that time |
| `KFIII_TEST_MAPHOVER` | `x,z` (samples) | The full map behaves as if the mouse is there |
| `KFIII_TEST_MINI_MENU` | `1` | Show the minimap outside the world too |
| `KFIII_TEST_SOUNDREPORT` | `s[,s...]` | Press F9 (sound report) at those times |
| `KFIII_TEST_VOICEDUMP` | `s[,s...]` | Print libsnd's voice table and the SPU envelopes at those times |

### Overrides of settings

| Switch | Value | What it does |
|---|---|---|
| `KFIII_FPS` | number, `off` or `0` | Frame rate, over the setting (`off`/`0` = the console's 15) |
| `KFIII_WIDESCREEN` | `16:9`, `1.78`, `21:9`, `off` | Aspect ratio |
| `KFIII_SUBPIXEL` | `0`/`1` | Sub-pixel vertices |
| `KFIII_PERSPECTIVE` | `0`/`1` | Perspective-correct textures |
| `KFIII_PRECISE_CULL` | `0`/`1` | Back-face culling on sub-pixel positions |
| `KFIII_TRUECOLOR` | `0`/`1` | 8-bit colour, no dither |
| `KFIII_ANISO` | 1..16 | Anisotropic texture taps |
| `KFIII_MIPMAPS` | `0`/`1` | Mipmaps |
| `KFIII_SMOOTHFOG` | `0`/`1` | Smooth fog |
| `KFIII_AO` | `0`/`1` | Ambient occlusion |
| `KFIII_AO_RADIUS`, `_STRENGTH`, `_BIAS` | number | Ambient occlusion parameters |
| `KFIII_AO_RES` | int | Ambient occlusion resolution |
| `KFIII_AO_NORMALS` | `0`/`1` | Ambient occlusion from normals |
| `KFIII_MSAA` | samples | Anti-aliasing |
| `KFIII_PERPIXEL` | `0`/`1` | Per-pixel lighting |
| `KFIII_ZBUFFER` | `0`/`1` | Depth buffer |
| `KFIII_WATER_WORLD` | `0`/`1` | Seamless water |
| `KFIII_WATER_PERIOD` | number | World units per water texture repeat |
| `KFIII_WATER_FLOW` | `0`/`1`/`2` | Water's V direction: the face's own / world Z / world X |
| `KFIII_WATER_SMOOTH` | `0`/`1` | Smooth water flow |
| `KFIII_SSR` | `0`/`1` | Screen-space water reflections |
| `KFIII_SSR_STRENGTH`, `_F0`, `_THICKNESS`, `_DISTANCE` | number | Reflection parameters |
| `KFIII_SSR_STEPS` | 1..128 | Reflection ray steps |
| `KFIII_SSR_RES` | int | Reflection resolution |
| `KFIII_PLANAR` | `0`/`1` | Planar water reflections |
| `KFIII_PLANAR_BIAS` | number (8) | Clip height above the water plane |
| `KFIII_PLANAR_RIPPLE`, `_TOL` | number | Planar pass parameters |
| `KFIII_POSAUDIO` | `off`/`0`, `speakers`/`1`, else headphones | Directional sound |
| `KFIII_GEARSTATS` | `0`/`1` | Equipment stats |
| `KFIII_MAP` | `0` | Automap off |
| `KFIII_MAP_REVEAL` | `1` | Show the whole map |
| `KFIII_MAP_CHEATS` | `items,contents,enemies,traps,secrets,keys` or `all` | Those map cheats, whatever the settings and Classic mode |
| `KFIII_MAP_TRAPS`, `KFIII_MAP_SECRETS` | `1` | That one map cheat |
| `KFIII_MOVIEFILTER` | `0` | Smooth-movies filter off |
| `KFIII_MOVIESKIP` | `0` | Movie skipping off |
| `KFIII_KEYS` | `stock` | RecompOne's stock keys and Keyboard/Mouse page, no WASD layout |
| `KFIII_MOUSE` | `0` | No mouse look or mouse buttons |
| `KFIII_MOUSE_SENS` | number | Mouse sensitivity multiplier |
| `KFIII_MOUSE_INVERTY` | `1` | Invert mouse up/down |

### Rendering internals (0 = off unless stated)

| Switch | Value | What it does |
|---|---|---|
| `KFIII_ANIM_LERP` | `0` | No blending of animation times between ticks |
| `KFIII_WIDECULL` | `0` | Don't widen the view cone for widescreen |
| `KFIII_FOGFILL` | `0` | Don't fill the tile grid out to the fog distance |
| `KFIII_WIDECULL_NEAR` | int (8) | Squared radius, in cells, of the always-drawn ring |
| `KFIII_WIDECULL_DELTA` | int | Force the cone turn, in 12-bit angle units |
| `KFIII_HUD_ANCHOR` | `0` | Leave the HUD in the 4:3 middle |
| `KFIII_GROUND_DEPTH` | number (1.04) | Depth scale for the ground; 1 = off |
| `KFIII_UNCLAMP` | `0`/`1` | Draw GTE-clamped corners at their true projection |
| `KFIII_UNCLAMP_MINZ` | int | Depth from which UNCLAMP applies |
| `KFIII_NEARCLIP` | `0`/`1` | Near-plane and guard-band clipping |
| `KFIII_SUBPIXEL_FADE` | `a-b` | View depths over which sub-pixel fades to whole pixels |
| `KFIII_POOL_NOCHECK` | `1` | Skip the per-frame check that restores the large primitive pools |
| `KFIII_TILES_SETTLE_MS` | ms (0) | How long tile records must stay unchanged before the map takes water and lava from them |
| `KFIII_MAP_RETRY` | `0` | No second floor test from 640 higher |
| `KFIII_CREATURE_WATER` | `1` | Don't tag creatures, so slimes take the water's effects |
| `KFIII_PLANAR_NOCALL`, `_NOPRESENT`, `_NODRAW` | `1` | Skip the mirrored kf_Render / its present / its DrawOTag |
| `KFIII_PLANAR_DEBUG` | int | Runtime debug view of the planar pass |
| `KFIII_VSYNC` | `block`/`grid` | Which vblank timeline the runtime runs (grid) |

### Probes and logs

| Switch | Value | What it logs |
|---|---|---|
| `KFIII_LOG` | `bios,cd,gpu,dma,sdk,spu,mdec` or `all` | The runtime's log channels |
| `KFIII_RATE_PROBE` | `1` | Game vblanks, VSync calls, movie frames, key-ons and draws per second |
| `KFIII_SMOOTH_RATE` | `1` | Stock draws, redraws, gate calls and update time every 0.5 s |
| `KFIII_SMOOTH_PROBE` | `1` | The camera every 40 frames, an entity every 200 placements |
| `KFIII_SMOOTH_DIFF` | any, or seconds | RAM ranges a redraw changed, three times |
| `KFIII_TICK_DIFF` | seconds | From then, halfwords changed across 6 ticks |
| `KFIII_PITCH_PROBE` | `1` | Drawn and base pitch every frame |
| `KFIII_POOL_PROBE` | `1` | Primitive pool descriptors every 30 frames |
| `KFIII_FX_PROBE` | `1` | Every live spell-effect record each tick, and each placement |
| `KFIII_ENT_DUMP` | entity index | That creature record's halfwords once a second |
| `KFIII_PEEK` | hex address (no 0x) | 4 bytes at that address |
| `KFIII_VISUALS_PROBE` | `1` | Recovery and pass counters every 2 s |
| `KFIII_PRESENT_PROBE` | `1` | The runtime's choice of frame to present |
| `KFIII_GTE_CALLERS_FROM` | seconds | With `RECOMPONE_GTE_CALLERS`: forget callers seen before then |
| `KFIII_AO_PROBE` | `1` | Ambient occlusion coverage |
| `KFIII_SSR_PROBE` | `1` | Reflection counters every 2 s |
| `KFIII_PLANAR_PROBE` | `1` | Planar reflection stats every 2 s |
| `KFIII_WATER_FLOW_PROBE` | `1` | Each water texture copy and placement |
| `KFIII_WATER_CORNERS` | `1` | The water's world corners every second |
| `KFIII_WATER_PROBE` | `1` | Which routine writes the water polygons (its hooks must be added to `HOOKS` first) |
| `KFIII_LIGHT_PROBE` | `1` | GTE light setup and lighting counters every second |
| `KFIII_MODEL_CALLERS` | `1` | Each distinct caller of kf_DrawModel, once |
| `KFIII_ITEMVIEW_PROBE` | `1` | kf_ItemPreview's state twice a second |
| `KFIII_DIAG_ARCHIVES` | `1` | Every archive table opened and entry loaded, also to diag.log |
| `KFIII_DIAG_VRAM` | `1` or `all` | VRAM transfers touching the sky palette rows 488-500, or all of them |
| `KFIII_GPU_CAPTURE` | seconds | GP0 commands of one frame to gpu_capture.txt, from that time |
| `KFIII_GPU_CAPTURE_FRAMES` | count (1) | How many frames to capture |
| `KFIII_WAV` | path | Record the final SPU mix to a WAV file |
| `KFIII_POSAUDIO_PROBE` | `1` | Directional sound: pan checks, counters every 2 s, unplaced sound callers |
| `KFIII_DUMP_EVENTS` | `1` | The BIOS event table each time GAME.EXE loads |
| `KFIII_CARD_PROBE` | `1` | The boot stub's hand-off bytes at each Exec (the runtime's side is `RECOMPONE_CARD_PROBE`) |
| `KFIII_AREA_STACK` | `1` | The call stack when an area module loads |
| `KFIII_AREA_DIFF` | `1` | Player position each tick and the RAM bytes that changed just before an area load |
| `KFIII_DEBUG_PAD` | `1` | The pad word on each change, and the running flag |
| `KFIII_MENUMOUSE_PROBE` | `1` | Menu lists and buttons seen, and the row under the pointer |
| `KFIII_KEYHINTS_PROBE` | `1` | Menu enter/leave and the primitives the key-hint detection looks at |
| `KFIII_GEARSTATS_PROBE` | `1` | Computed stat rows, stat words when a list closes, shop preview ids |
| `KFIII_GEARSTATS_GIVE` | hex ids `02,2B` | Put one of each in the bag when an equipment list opens |
| `KFIII_DAMAGE_LOG` | `1` | Each creature hit detected: amount, slot, type, HP left |
| `KFIII_JUMP_PROBE` | `1` | Height, speed, state and HP for 40 ticks of each jump |
| `KFIII_AUTOKEY_LOG` | `1` | Every message queued during an interact |
| `KFIII_ENH_PROBE` | `1` | Two localized settings strings when the runtime is ready |
| `KFIII_MAP_PROBE` | `1` | ASCII map round the player at tick 45, then position and water counts every 15 ticks |
| `KFIII_MAP_DUMP` | `1` | With reveal: kind and floor of every sample to automap_dump.bin |
| `KFIII_TILE_TEX` | `1` | Each tile's CLUT and texture page, once |
| `KFIII_TILE_DUMP` | `x0,x1,z0,z1` | With ENT_PROBE: the tile records there |
| `KFIII_COL_PROBE` | `x,z;x,z` | The collision state after a floor test at each point, at tick 20 |
| `KFIII_WATCH_BYTE` | hex address | That byte whenever it changes |
| `KFIII_WATCH_RANGE` | `addr,len` (hex) | Each halfword there that changes |
| `KFIII_ENT_PROBE` | `1` | Once: creature records and definitions, containers, doors, enemy markers, item names |
| `KFIII_PROP_ALL` | `1` | With ENT_PROBE: every prop within 60 samples |
| `KFIII_PROP_CLASS` | two hex digits | With ENT_PROBE: the props of that class |
| `KFIII_PROP_WATCH` | prop index | That prop's record whenever it changes |
| `KFIII_BYTES_NEAR` | `aabb,ccdd` | With ENT_PROBE: RAM spots where the two byte pairs lie within 24 bytes |
| `KFIII_HEX` | `addr,len` (hex) | With ENT_PROBE: a hex dump |
| `KFIII_FLAG_FIND` | hex flag | With ENT_PROBE: dialog-script commands that set that flag |
| `KFIII_SCAN` | `1` | 7 s in: hidden items, unknown containers and unopened chests with contents |
