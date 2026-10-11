# Building from source

This is how the game is built today, from a clone of the repository and your own disc
image. The installer described in the [README](README.md) will run the same steps for
the player.

Nothing the build produces from the disc may be committed or distributed:
`build/`, `generated/`, `bin/` and the disc image itself stay on your machine
(`.gitignore` lists them).

## Prerequisites

| What | Why |
|---|---|
| Windows 10 or 11, 64-bit | The runtime's window, input and audio are built and tested on Windows. |
| [.NET 10 SDK](https://dotnet.microsoft.com/download) | The recompiler, the runtime and the game are C# projects targeting `net10.0`. |
| Python 3 | The pipeline scripts in `scripts/`. They use only the standard library. |
| `pip install capstone` | Only for the research tools in `scripts/research/` (disassembly and searches). |
| `pip install numpy pillow` | Only for the OCR tools in `scripts/research/ocr/`. |
| `dotnet tool install -g dotnet-stack` | Optional: `scripts/test/autorun.ps1` uses it to sample a hung game thread. |

## The disc image

You need a dump of the North American disc, *King's Field II* (`SLUS-00255`), as a
`.bin` (raw 2352-byte sectors) and its `.cue`.

The configuration expects it at `disc/KingsFieldIII.cue`. Create the `disc` folder,
put the `.bin` in it, and write a `.cue` named `KingsFieldIII.cue` that points at it,
for example:

```
FILE "King's Field II (USA).bin" BINARY
  TRACK 01 MODE2/2352
    INDEX 01 00:00:00
```

The `FILE` line must name your `.bin` exactly. If your dump came with a `.cue`, copying
it to `disc/KingsFieldIII.cue` beside the `.bin` is enough.

To check the image, `python scripts/inspect_disc.py disc/KingsFieldIII.cue` prints
`SYSTEM.CNF` (it should boot `SLUS_002.55`) and the file listing.

## Build

From the repository root:

```powershell
python scripts/name_game.py
python scripts/name_sdk.py
dotnet run --project tools/RecompOne/RecompOne.Recompiler -c Release -- config/kf3.json
dotnet build KingsFieldIII.csproj -c Release
```

What each step does:

1. **`scripts/name_game.py`** writes the names of the game's identified functions
   (`kf_*`, and `lib_*` for library code identified by behaviour) into
   `config/funcmaps/game.json`. The recompiler names each generated method after its
   funcmap entry, so stack traces and logs read `kf_DrawModel` instead of
   `func_8003E34C`.
2. **`scripts/name_sdk.py`** writes the hand-identified PSY-Q library names into the
   funcmaps of the three executables (`open`, `game`, `end`). The runtime binds its
   high-level emulation of the SDK by exact function name, so naming a function is what
   binds it. It also rewrites the `patches` list in `config/kf3.json` from its `SHIMS`
   and `HOOKS` tables: every place where the port replaces a game function, or runs C#
   before or after one. It then runs `name_game.py` again; both scripts are idempotent,
   so running them twice changes nothing.
3. **The recompiler** reads `config/kf3.json`, loads the executables and the area code
   from the disc image, translates every function in the funcmaps to C#, and writes
   the result to `generated/` (one file per executable and per area module, plus
   `Entry.cs` and `Stubs.cs`). The `dotnet run` builds the recompiler first.
4. **`dotnet build`** compiles the runtime (`tools/RecompOne/RecompOne.Runtime`), the
   generated code, `Program.cs` and `patches/` into `bin/Release/net10.0/KingsFieldIII.exe`.

Steps 1 to 3 are needed again only when the funcmaps, the hooks in `name_sdk.py` or the
names in `name_game.py` change. A change to `patches/` or `Program.cs` needs only step 4.
A change to the runtime needs step 4 too; a change to the recompiler needs steps 3 and 4.

## Run

```powershell
.\run.cmd
```

`run.cmd` starts `bin\Release\net10.0\KingsFieldIII.exe` with
`disc\KingsFieldIII.cue` and writes all console output to `run.log` in the repository
root. The game reads the disc image while it runs (data files, music, movies), so the
image must stay where it is.

The working directory is where the game keeps its settings, memory cards, quick save
and map memory (see "Where your files are" in the README). Started from Explorer,
`run.cmd`'s working directory is the repository root. An unhandled exception is also
written to `crash.log` beside the exe.

## Regenerating the function maps

`config/funcmaps/*.json` and the overlay list in `config/kf3.json` are part of the
repository. They hold function addresses, sizes and names, not code. They only need to
be regenerated if the way the area code is split into functions changes.

- **`open.json`, `game.json`, `end.json`, `main.json`** came from the recompiler's
  autoconfigure, which matched about 600 PSY-Q library functions against the signature
  bank in `tools/RecompOne/RecompOne.Recompiler/AutoConfigure/signatures`. They have
  been edited since (names, one unbound function in `end.json`); do not regenerate
  them.
- **`fdat02.json` .. `fdat83.json`** are the 28 area code modules in `CD/COM/FDAT.T`,
  made by `scripts/gen_fdat.py`.

To rebuild the area maps:

```powershell
dotnet build tools/RecompOne/RecompOne.Recompiler -c Release
python scripts/extract_file.py disc/KingsFieldIII.cue CD/COM/FDAT.T -o build/FDAT.T
python scripts/gen_fdat.py
python scripts/merge_branch_spans.py
python scripts/name_sdk.py
```

- `gen_fdat.py` reads `build/FDAT.T`, runs the recompiler's linear sweep over each
  module's code (with `--no-build`, so the recompiler must be built first), adds every
  dispatch-table target, header pointer and internal `jal` target as a function start,
  writes `config/funcmaps/fdatNN.json`, and replaces the `fdat*` overlays in
  `config/kf3.json`.
- `merge_branch_spans.py` rejoins functions the sweep cut in two at a `jr` through a
  switch table or a `j` to a shared epilogue, using conditional branches that cross a
  function boundary as proof. Run it on the `fdat*` maps only: on `game.json` it
  mistakes a function-pointer table at `0x800136A0` for a switch table and would
  collapse 1203 functions to 603, so it refuses to write that file.
- `name_sdk.py` again, because `gen_fdat.py` rewrites the config.

Then run steps 3 and 4 of the build.

## Other scripts

| Script | What |
|---|---|
| `scripts/inspect_disc.py` | Prints `SYSTEM.CNF` and the ISO 9660 file listing of a disc image. |
| `scripts/extract_file.py` | Extracts one file from the image and prints its PS-X EXE header. |
| `scripts/fdat.py` | Lists `FDAT.T`'s entries and how code-like each one looks. |
| `scripts/gen_creature_names.py` | Writes `patches/Map/CreatureNames.cs` from the OCR output in `data/item_t_text_all.json` (see [docs/research/game-systems.md](docs/research/game-systems.md)). Needs the OCR to have been run on your disc. |
| `scripts/test/` | Unattended runs and screenshots; see [docs/testing.md](docs/testing.md). |
| `scripts/research/` | Disassembly, searches, the OCR and a save-state reader. |

## Making a release

```powershell
powershell -ExecutionPolicy Bypass -File packaging\windows\build-windows.ps1
```

Builds the portable zip and, with Inno Setup 6 installed
(`winget install JRSoftware.InnoSetup`), the installer, into `dist\`. It needs no disc
and packages nothing from it; the release builds the game on the player's machine.
[docs/packaging.md](docs/packaging.md) explains how. Set the version in `VERSION`.

## Folders

| Folder | What | In the repository |
|---|---|---|
| `config/` | Recompiler configuration and function maps | yes |
| `patches/` | The port's C# patches | yes |
| `scripts/` | Pipeline, test and research scripts | yes |
| `tools/RecompOne/` | The vendored, modified RecompOne | yes |
| `docs/` | Documentation | yes |
| `launcher/` | The release executable, which builds the game on first run | yes |
| `packaging/` | Installer script, stub, icon and third-party licences | yes |
| `dist/` | Release output | no |
| `disc/` | Your disc image | no |
| `build/` | Files extracted from the disc (PlayStation files, not Windows programs) | no |
| `generated/` | Recompiler output, derived from the disc | no |
| `bin/`, `obj/` | Build output | no |
| `data/` | Text extracted from the disc by the OCR tools | no |
| `tests/saves/`, `tests/logs/`, `tests/shots/`, `tests/run/` | Test save states and test output | no |
