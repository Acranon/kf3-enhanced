# Contributing

kf3-enhanced is a static recompilation of King's Field III (released in North
America as King's Field II, SLUS-00255) plus a set of patches that add to it. Most
work happens in `patches/`; [docs/architecture.md](docs/architecture.md) explains how
the pieces fit together, and [BUILDING.md](BUILDING.md) how to build.

## Ground rules

- **No game data in the repository.** Nothing extracted from the disc is committed:
  no executables, no recompiled code (`generated/`), no save states, no images or
  sound, and no game text beyond the names of places, items and creatures that the
  map and stats show. The build makes everything else from the player's own disc.
- **Everything new is a setting.** A change to how the game looks or plays gets a
  toggle (Settings > Display, Quality of life, Map...) and respects Classic mode.
- **Proven, not guessed.** An address or a behaviour goes into the code once it has
  been shown: by a watchpoint, a RAM diff, the disassembly, or a test in the game.
  Write the evidence down beside it or in docs/research.

## Naming

The game is King's Field III throughout, following the Japanese numbering (the same
as [kf1-enhanced](https://github.com/Acranon/kf1-enhanced)).

| What | Form | Example |
|---|---|---|
| C# namespace | `KfIII` | `KfIII.Automap` |
| Environment switches | `KFIII_*` (runtime: `RECOMPONE_*`) | `KFIII_TEST_QUICK` |
| Console log tags | `[KFIII]`, or `[KFIII-<AREA>]` | `[KFIII] the end: ...` |
| Files, settings keys, panel ids | `kf3` | `config/kf3.json`, `kf3.qol` |
| Game functions | `kf_PascalCase` in `scripts/name_game.py` | `kf_RecalcStats` |
| Game addresses | PascalCase constants in `patches/Core/GameAddr.cs` | `GameAddr.WeaponSlot` |

C# follows the [.NET conventions](https://learn.microsoft.com/dotnet/csharp/fundamentals/coding-style/coding-conventions);
`.editorconfig` holds the details and `dotnet format` applies them:

- PascalCase for types, methods, properties, constants and public fields.
- `_camelCase` for private fields, `camelCase` for locals and parameters.
- File-scoped namespaces, usings at the top, four-space indentation, Allman braces.
  Short one-line `if` bodies and blocks are fine.

## Addresses

- Every RAM address the patches touch is a named constant in `GameAddr`, with a
  one-line comment saying what it holds (type, units, range). A patch may keep a
  local alias (`const uint State = GameAddr.FallState;`), but not a bare number.
- Offsets inside a record (`+0x38`) can stay as numbers where they are used, with
  the record's layout described once, in `GameAddr` or at the top of the patch.
- Code addresses (hooks) are bound by name: the function gets a `kf_` name in
  `scripts/name_game.py`, and the hook goes in `scripts/name_sdk.py`'s `HOOKS`.

## Patches

One file per feature, grouped by folder:

| Folder | What |
|---|---|
| `Core/` | Addresses, SDK shims, memory pools, quick save, movies, the ending |
| `Graphics/` | Picture: precise geometry, widescreen, high frame rate, water, reflections |
| `Audio/` | Directional sound |
| `Input/` | Keyboard and mouse |
| `Gameplay/` | Jump, unlocking with carried keys |
| `Interface/` | Equipment stats, stat progress, damage numbers, key hints |
| `Map/` | The automap, minimap and their hints |
| `Settings/` | The settings pages and their saved values |
| `Debug/` | Test harness, probes and the debug menu |

Each patch is a `public static class` with:

- a `<summary>` saying what it does for the player and how it works in the game
  (which routine, which state), enough that someone new can find their way in;
- an `Install()` called from `Program.cs` if it registers listeners, and an
  `Apply(...)` if a setting drives it;
- hook methods with the runtime's signature, `(CpuContext c, IMemory m)`, each with
  a comment naming the function and whether it runs before or after it.

Keep a file to one job. When one grows past several hundred lines it usually holds
two (scanning and drawing, say) and should be split.

## Comments

Write for someone who knows C# but not this game:

- Say **why**, and what the game does; not what the next line of C# does.
- Use plain words and whole sentences. Name things rather than pointing at them
  ("the pillar's key model", not "+0x3C").
- No history ("was", "now", "fixed on ..."): the code says what is. History belongs
  in the commit message, research notes in docs/research.
- References to other projects name them in full the first time (Verdite2, the
  RecompOne port of the first North American King's Field, which several patches
  started from; kf1-enhanced, the port of the Japanese King's Field).

## Debug switches

Tests, probes and traces are switched on by `KFIII_*` environment variables, read
through `DevSwitch` (`patches/Debug/DevSwitch.cs`). They do nothing unless set,
and each one is listed in [docs/testing.md](docs/testing.md). Keep them: they are
how a bug gets reproduced. A switch that only made sense for one investigation
can go, once its finding is in docs/research.

## Scripts

| Folder | What |
|---|---|
| `scripts/` | The build pipeline: naming, overlay generation, disc reading |
| `scripts/test/` | Unattended runs and screenshots |
| `scripts/research/` | Tools used to work things out: disassembly, searches, OCR, save-state reading |

The release launcher is in `launcher/` and the packaging (installer, stub, icon,
licences) in `packaging/`; [docs/packaging.md](docs/packaging.md) explains both. A
patch must build in the launcher's compile too, which happens on the player's machine:
it may use only the runtime and the recompiled code, never a file outside
`Program.cs` and `patches/`.

Python scripts start with a docstring saying what they do and how to run them.
