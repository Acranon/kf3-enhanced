# Packaging

How the port becomes something a player can download, and why it is shaped this way.
The design is adapted from Verdite2's (MIT, Voicedrew11), which solved the same
problem for the first North American King's Field.

## The problem

`generated/` is a translation of the game's code, and its compiled form is no less
derived, so there is no game binary that can be distributed. The inputs to a build
are distributable: `config/` holds addresses, names and sizes found in the code (no
code), `Program.cs` and `patches/` are this project's own work, and the recompiler and
runtime are MIT. So a release ships the inputs, and the game is built on the machine of
someone who owns the disc.

That is also more correct: the generated code bakes the disc's sector positions, so a
build made from the player's own image always matches it.

## Two projects

| Project | Builds without the disc | What it is |
|---|---|---|
| `launcher/KF3Enhanced.csproj` | yes | the release executable |
| `KingsFieldIII.csproj` | no | the developer build (generated/ and patches/ through the SDK) |

The launcher compiles neither `generated/` nor `patches/`: it carries `config/` and the
sources as payload (`content/`) and compiles them on first run. It compiles in one patch
file directly, `patches/Core/PortDefaults.cs`, because it opens the window before the
game exists and must set the same display defaults.

## First run

1. `Paths.Prepare` makes the data folder, `%LOCALAPPDATA%\kf3-enhanced` (or `KFIII_DATA`),
   and moves into it. Every file the runtime and the patches write is addressed
   relatively (settings.json, interface.ini, enhancements.json, the memory cards, the
   quick save, automap/), so they all land there and none needed changing.
2. `Runtime.DiscValidator = DiscCheck.Validate`: only SLUS-00255 is accepted, with the
   files the build reads present and long enough. The first North American King's
   Field (SLUS-00158) is named in its own message, since it is the likely mistake.
3. `Runtime.WaitForValidDisc()`: the runtime's own picker (a native file dialog); the
   chosen path is saved in settings.json, so later launches pass straight through.
4. `BuildKey` hashes the disc's code files (not the whole image), the shipped sources,
   the shipped config and the launcher version. If `builds/<key>/KingsFieldIII.dll`
   exists, the build is skipped.
5. `Recompile.Run` runs the recompiler in process (its entry point is an ordinary
   method; a self-contained release has no `dotnet` to start a second process), with a
   staged copy of the config pointed at the player's disc. A few seconds.
6. `GameCompile.Run` compiles the recompiled code and the patches together with Roslyn,
   with the same options as `KingsFieldIII.csproj`. About 15 to 20 seconds. The
   translated code is deleted afterwards; only the compiled assembly is kept.
7. The assembly is loaded and its entry point (`Program.cs`) run with the disc path.

A popup shows the two steps; a failure stays on screen with the error, and the full
report goes to `build.log` in the data folder.

One ordering detail: the launcher loads settings.json before the game's code exists,
so `Controls.Configure` only seeds the key layout when there is no settings.json yet,
and the stock-key migration in `Controls.Install` covers a first run.

## The package

```
KF3Enhanced.exe   a small .NET Framework stub that starts bin\KF3Enhanced.exe
bin\              the self-contained .NET runtime, RecompOne, Roslyn, the launcher
content\          config\ and src\ (Program.cs, patches\)
licenses\         this project's CC0 and every third-party licence it carries
README.md
```

The stub exists because a self-contained executable must sit beside its hundred DLLs;
the stub keeps them out of the folder the player opens.

## Building a release

```powershell
powershell -ExecutionPolicy Bypass -File packaging\windows\build-windows.ps1
```

Needs the .NET 10 SDK, and Inno Setup 6 for the installer
(`winget install JRSoftware.InnoSetup`). Writes to `dist\`:

- `KF3Enhanced-<version>-win-x64.zip`: portable, unzip and run.
- `KF3Enhanced-<version>-win-x64-setup.exe`: the installer. Per-user by default (no
  administrator prompt), Start-menu entries for the game and for the saves folder, and
  an uninstaller that leaves saves alone.

The script refuses to package if any file derived from the disc (recompiled sources,
executables, save states, disc images) is in the tree. The version comes from
`VERSION` at the repository root, the only place it is written; the launcher's version
is part of the build key, so a release that changes how the build is done should bump
it.

## Testing a release without touching your own files

Point `KFIII_DATA` at a scratch folder. To skip the picker, put a settings.json there
with only `{"CdPath": "<your .cue>"}`. Run `dist\win-x64\bin\KF3Enhanced.exe` (the real
executable has the window; the stub's child would outlive a test that kills only the
stub, which is why `scripts/test/shot.ps1` stops the whole process tree).
