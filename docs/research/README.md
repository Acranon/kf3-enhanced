# Research notes

What is known about King's Field III's code and data (the North American release,
`SLUS-00255`), as reference for working on the port. Each fact here has been shown by a
watchpoint, a RAM diff, the disassembly or a test in the game; where something is only
likely, the text says so.

| File | What |
|---|---|
| [boot-and-cd.md](boot-and-cd.md) | The executables and the boot stub, interrupts and the clock, SDK functions identified by hand, the CD read contract, the data archives and `FDAT.T`, save states, movies, the ending, the memory card |
| [game-systems.md](game-systems.md) | The player, controls, experience and stats, equipment and its records, gauges, regeneration, ailments, hidden gear effects, the Excellector, spells and weapon magic, using items, messages, creatures, dialog scripts and event flags |
| [world.md](world.md) | The 28 areas, area changes, exits, warps and portals, the prop table and prop classes, locks, chests and containers, keys and puzzles, traps and secret walls, the floor plan, collision and liquids |
| [graphics.md](graphics.md) | The frame, ordering table and primitive pools, the visible-tile grid, ground and model drawing and their culling, animation, the per-call renderer state, fog, lighting, the HUD, water and lava |
| [audio.md](audio.md) | Positioned sounds, area sounds, libsnd's voice management, SPU behaviours |
| [interface.md](interface.md) | The menu's routines and records, menu text, the equipment screen, shops, the item preview, the text images in `ITEM.T` |

## Conventions

- **Addresses** are `GAME.EXE`'s unless an executable or area module is named
  (`open:0x8001D018`, `fdat02 0x801E8D68`). Area modules are all linked at `0x801E8308`,
  so a module address means nothing without the module.
- **Names** of functions are those in `scripts/name_game.py` (`kf_*` for the game's own
  code, `lib_*` for library code identified by behaviour) and `scripts/name_sdk.py`
  (PSY-Q names). Where a function has no name yet it is written `func_XXXXXXXX`. RAM
  addresses the patches use are named in `patches/Core/GameAddr.cs`.
- **Record offsets** are written `+0x38` from the start of the record.
- **Ticks** are the world's 15 Hz updates; **samples** are the automap's 8 x 8 points a
  tile (a tile is 2048 world units).
- **Item, creature and prop ids** are hex. Prop and creature records are written `#n`,
  their index in the table, which is fixed per area.
- **Evidence** often names a save state (`tests/saves/<name>.kfstate`) made at the place
  in question. These are kept locally and are not in the repository, because they
  contain the game's RAM. [../testing.md](../testing.md) describes how they are used.
- **Debug switches** named here (`KFIII_*` for the patches, `RECOMPONE_*` for the
  runtime) are listed in [../testing.md](../testing.md).

## Tools

| Tool | What |
|---|---|
| `scripts/research/mipsdis.py` | Disassemble a function or range of an overlay, with funcmap names on `jal` targets |
| `scripts/research/findimm.py` | Functions of an overlay whose code uses all the given immediates |
| `scripts/research/neighbors.py` | The funcmap entries around an address |
| `scripts/research/peek.py` | Words from an overlay's file image (static data only) |
| `scripts/research/irq_table.py` | Each executable's interrupt-callback table |
| `scripts/research/find_fallthrough.py` | Functions that run off their end into the next |
| `scripts/research/savestate.py` | Read RAM from a quick save, or diff two |
| `scripts/research/ocr/` | Extract and read the text images in `ITEM.T` |
| `RECOMPONE_WATCH=<hex>` | Log the recompiled call chains that write a word |
| `KFIII_WATCH_RANGE=addr,len` | Log halfwords in a range that change each tick |
| `KFIII_GPU_CAPTURE=<s>` | Write one frame's GP0 polygons to `gpu_capture.txt` |
| `RECOMPONE_GTE_CALLERS=1` | Count NCLIP calls by the routine that makes them (finds a drawing routine and its culling) |

Other sources: KingsFieldRE ([github.com/IvanDSM/KingsFieldRE](https://github.com/IvanDSM/KingsFieldRE))
for the object records; the community maps at kingsfield.fandom.com and
kings-field.neocities.org for names and places.
