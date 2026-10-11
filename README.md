# kf3-enhanced

A PC port of **King's Field III** (FromSoftware, 1996), the PlayStation game sold in
North America as *King's Field II* (`SLUS-00255`). It is a static recompilation: the
game's own MIPS code is translated to C# ahead of time and runs natively on Windows,
on top of the [RecompOne](https://github.com/BlackLabelHQ/RecompOne) runtime, with a
set of patches that add higher picture quality, widescreen, high frame rates, an
automap, keyboard and mouse controls and other additions. Every addition can be
switched off, and Classic mode puts the game back as it was on the console.

This project uses the Japanese numbering of the series, like its sibling
[kf1-enhanced](https://github.com/Acranon/kf1-enhanced), the port of the Japanese
*King's Field*. "King's Field III" here and "King's Field II" on the North American
box are the same game.

The repository and its releases contain **no game code or data**. You supply your own
disc image, and the game is built from it on your machine.

## Features

### Picture

- **Stable geometry**: corners are drawn at their exact sub-pixel positions instead of
  snapping to the console's 320x240 grid, so walls and floors stop wobbling.
- **Straight textures**: perspective-correct texturing instead of the console's affine
  warping.
- **Smooth colour**: full colour precision for shading and fog, without the dither.
- **Texture filtering at a distance**: anisotropic filtering and mipmaps stop distant
  floors shimmering; close textures keep their sharp pixels.
- **Correct overlaps**: a depth buffer decides what is in front per pixel, so doors and
  props stop showing through the walls they stand behind.
- **High resolution**: the picture is rendered at a multiple of the original
  resolution (8x by default).
- **Anti-aliasing**: MSAA, off, 2x, 4x or 8x (8x by default).
- **Ambient occlusion**: soft shading in corners and where objects meet the floor, in
  three quality levels.
- **Smooth fog**: fog fades by each pixel's distance instead of following the tiles.
- **Per-pixel lighting**: models are lit at every pixel from the game's own lights.
- **Seamless water**: the river's texture is laid out across the world, without the
  blocky seams, and flows smoothly at high frame rates.
- **Water reflections**: banks, walls and bridges reflect in the water (subtle or
  strong).
- **Smooth movies** (work in progress, off by default): the movies are scaled up
  smoothly, with their compression blocks and colour banding softened. The picture can
  jitter slightly, most visibly in the opening movies.
- **Widescreen**: 4:3, 16:9, 16:10, 21:9 or 32:9. The extra width shows more of the
  world; nothing is stretched, and the HUD moves out to the edges. Menus and other 2D
  screens stay 4:3 in the middle.
- **High frame rate**: the picture is drawn at your monitor's rate (or 30, 60, 120,
  144, 165, 240) while the world keeps the original 15 updates a second, so the game
  plays at its original speed. The camera, creatures, objects, spell effects and the
  weapon swing are smoothed between updates.

### Sound

- **Directional sound**: a sound stays where it comes from as you turn and move, with
  settings for speakers or headphones (headphones also tell front from behind).
- Fixes for sounds that popped as they ended and for sound effects that stopped
  playing after a quick load.

### Controls

- **Keyboard and mouse**: WASD movement, mouse look with adjustable sensitivity and
  smoothing, run on its own key, and every key remappable on Settings > Input.
- **Mouse in menus and shops**: point at a row to pick it, click to select or go back,
  wheel to scroll.
- **Key hints in menus**: the keys bound to each button are shown under the game's
  button hints.
- Gamepads work as on the console, with R3 for jump.

### Quality of life

- **Quick save and load**: F2 saves the whole game at once, F4 loads it, also from the
  title screen.
- **Automap and minimap**: a floor plan that fills in as you explore, with doors,
  chests, save crystals, people, exits to other areas, portals and other landmarks.
  M opens the full map, which pauses the game and can be zoomed and dragged; pointing
  at a marker names it. The minimap's corner, size, zoom and opacity can be set.
- **Map cheats** (all off by default): the whole map, items on the ground, what chests,
  barrels, graves and monsters hold, which key opens a lock, enemies, traps and secret
  walls. Hover notes explain locks that the game's data cannot (for example a door
  opened by an event elsewhere).
- **Equipment stats**: on the equipment lists and in shops, the stats the highlighted
  piece gives; when you choose one, your totals now and with it on.
- **Hidden effects**: equipment's effects the game never shows (blocking chances,
  shorter ailments, regeneration, changed spell costs and so on) are listed with its
  stats.
- **Stat progress**: beside the status window, the experience to the next level, how
  close PWR and each magic element are to their next point, recharge rates and
  regeneration.
- **Damage numbers**: each hit on a creature is listed with the HP it has left, with a
  health bar for the last creature hit.
- **Jump**: on Space (R3 on a gamepad), built from the game's own falling and landing,
  in three heights. **Use it with care**: the game was never designed for jumping, so
  it can reach places early, clear some walls and carry you off the edge of the world,
  where there is no floor and no way back. Quick save (F2) before a risky jump.
- **Unlock with keys you carry**: interacting with a locked door or chest uses the
  right key from your bag, as using it from the item menu would.
- **Movie skip**: Enter or E skips a movie.

### Classic mode

Settings > Classic mode has three choices:

| Mode | What you get |
|---|---|
| Off | Everything as set on the other pages. |
| Full classic | The original picture (wobbling geometry, warping textures, dithered colour, 4:3, 15 fps) and none of the additions. |
| Classic + visuals | The game as it played on the console, with the enhanced picture: the Display settings stay as set, and none of the additions run. |

Classic mode never changes key bindings, mouse look, the mouse in menus, directional
sound, quick save or movie skip. Turning it off brings back your other choices.

## Requirements

- Windows 10 or 11, 64-bit.
- A graphics card with OpenGL 3.3 or later.
- Your own disc image of *King's Field II* for the North American PlayStation
  (`SLUS-00255`), as a `.bin` with its `.cue`. Other regions are different programs
  and will not work.

## Installing and first run

1. Download `KF3Enhanced-<version>-win-x64-setup.exe` from the Releases page and run
   it. It installs for your user only, with no administrator prompt. (Or take the
   `.zip`, unzip it anywhere and run `KF3Enhanced.exe`.)
2. Start King's Field III Enhanced. The first time, it asks for your disc image: pick
   the `.cue` (or a `.chd`). It accepts only the North American *King's Field II*
   (`SLUS-00255`) and says so if you pick another disc.
3. It builds the game from your disc, which takes about half a minute, and starts.
   This happens once, and again only after an update; later launches start straight
   away.

Nothing needs installing besides the game itself: the release carries its own .NET
runtime. To build from source instead, see [BUILDING.md](BUILDING.md).

## Controls

The keyboard defaults. Everything except the fixed keys can be changed on
Settings > Input > Keyboard/Mouse.

| Action | Key | Mouse |
|---|---|---|
| Walk forward / back | W / S (also Up / Down) | |
| Strafe left / right | A / D | |
| Turn | Left / Right | move |
| Look up / down | (unbound) | move |
| Run (hold) | Left Shift | |
| Jump | Space | |
| Attack | Keypad + | left button |
| Interact, confirm | E | middle button |
| Magic; in menus, Guide | Q | right button |
| Menu, back | Tab | |
| Pause | Enter | |
| Select | R | |
| Full map | M | |
| Quick save / quick load | F2 / F4 (fixed) | |
| Free or capture the mouse | Esc (fixed) | |
| Port menu (System, Settings) | F1 | |
| Fullscreen | F11 | |

In menus and shops, with "Mouse in menus" on: point at a row, left click selects,
right click goes back, middle click shows the Guide, and the wheel scrolls.

## Where your files are

Everything the game writes is in `%LOCALAPPDATA%\kf3-enhanced` (the Start menu has
a "Saves and settings" shortcut to it). Uninstalling leaves it in place. A build from
source keeps the same files in the folder it is started from.

| File | What |
|---|---|
| `carda.sav`, `cardb.sav` | The memory cards: the game's own saves. |
| `quicksave.kfstate`, `.bak` | The quick save (F2) and the one before it. |
| `automap\*.seen` | What you have explored, one file per area. |
| `enhancements.json` | The port's settings (picture, quality of life, map, mouse). |
| `settings.json` | Key bindings and the runtime's game settings. |
| `interface.ini` | Display settings (window, fullscreen, render scale). |
| `builds\` | The game built from your disc; safe to delete (it is rebuilt). |

Keep a copy of the saves when you move to another PC.

## Credits

- **FromSoftware** made *King's Field III*.
- **RecompOne**, by flaffy ([BlackLabelHQ/RecompOne](https://github.com/BlackLabelHQ/RecompOne)),
  MIT licence: the recompiler and runtime. This project carries a modified copy in
  `tools/RecompOne`; [docs/runtime-changes.md](docs/runtime-changes.md) lists the
  changes.
- **Verdite2**, the RecompOne port of the first North American *King's Field*: several
  patches here (controls, jump, directional sound, equipment stats, the picture
  enhancements) started from its patches, its copy of the runtime is the one this
  project began with, and the release launcher (building the game from your disc on
  first run) and installer are adapted from its own, by Voicedrew11 (MIT).
- **[kf1-enhanced](https://github.com/Acranon/kf1-enhanced)**, the port of the Japanese
  *King's Field*: the automap's design and the equipment stats follow it.
- **[KingsFieldRE](https://github.com/IvanDSM/KingsFieldRE)** documented the object
  records that the map's trap reading builds on.
- The community maps and guides at kingsfield.fandom.com and
  kings-field.neocities.org were used to check names and places.

## Legal

This project is not affiliated with or endorsed by FromSoftware or Sony Interactive
Entertainment. It contains no game code, data files, images or sound; the only things
from the game are the names of places, items and creatures that the map and the
equipment stats show. You need your own copy of the game. *King's Field* is the
property of FromSoftware.

## Licence

The code of this project is released under
[CC0 1.0](https://creativecommons.org/publicdomain/zero/1.0/) ([LICENSE](LICENSE)):
you may use it for anything, without asking. The vendored RecompOne in `tools/RecompOne` remains under the
MIT licence; see [tools/RecompOne/LICENSE](tools/RecompOne/LICENSE).

## For developers

- [BUILDING.md](BUILDING.md): building from source.
- [CONTRIBUTING.md](CONTRIBUTING.md): conventions, naming and layout.
- [docs/architecture.md](docs/architecture.md): how the port works.
- [docs/runtime-changes.md](docs/runtime-changes.md): changes to the vendored RecompOne.
- [docs/testing.md](docs/testing.md): save states, scripted runs and debug switches.
- [docs/packaging.md](docs/packaging.md): how a release is built and how its first run works.
- [docs/research](docs/research/README.md): what is known about the game's code and
  data.
