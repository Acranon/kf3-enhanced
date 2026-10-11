#!/usr/bin/env python3
"""Write the hand-identified PSY-Q names into config/funcmaps/*.json.

SdkPatches binds the runtime's HLE by exact function name, so naming a function
is what binds it. Autoconfigure's signature match named most of the SDK; these
are the ones it missed, identified by hand in `open` (see the notes per entry)
and carried to `game` and `end`, whose copies of the same objects have identical
function sizes at a constant delta per object.

Idempotent: re-running it renames nothing twice, and it refuses to rename a
function that already carries a different non-func_ name.
"""

import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

# name: (open, game, end)
NAMES = {
    # libcd sys.c. CdSync/CdReady wrap the internal wait loops (0x8001BBEC /
    # 0x8001BE70 in open); the two setters store a0 into the callback slots
    # CdControl saves and clears (sync) and StUnSetRing clears (ready).
    "CdSync":          (0x8001AF98, 0x80061D78, 0x80018110),
    "CdReady":         (0x8001AFB8, 0x80061D98, 0x80018130),
    "CdSyncCallback":  (0x8001AFD8, 0x80061DB8, 0x80018150),
    "CdReadyCallback": (0x8001AFF0, 0x80061DD0, 0x80018168),
    # CdControl passes its result buffer to the command write, CdControlF passes
    # NULL, CdControlB ends in the sync wait.
    "CdControl":       (0x8001B020, 0x80061E00, 0x80018198),
    "CdControlF":      (0x8001B168, 0x80061F48, 0x800182E0),
    "CdControlB":      (0x8001B2A4, 0x80062084, 0x8001841C),
    # libcd cdread.c: CdRead picks 0x200/0x249/0x246 words from the mode's
    # sector-size bits; CdReadSync(mode, result) follows it and arms a 480-vsync
    # watchdog labelled "CD_read".
    #
    # CdRead is deliberately NOT given its SDK name: this libcd returns 0 on
    # success and -1 on failure, the reverse of the HLE, so it is bound by
    # address to KfIII.SdkShims.CdRead instead (SHIMS below).
    "CdRead_ret0":     (0x8001D018, 0x80063DF8, 0x80019F60),
    "CdReadSync":      (0x8001D110, 0x80063EF0, 0x8001A058),
    # libcd's ISO9660 lookup; its strings name it ("CdSearchFile: searching
    # %s..."). Run raw, its sector-16 read never satisfied its own check and the
    # boot spun forever in "CdSearchFile: disc error" retries.
    "CdSearchFile":    (0x8001D9F4, 0x800648CC, 0x800175B4),
    # libgpu: hStart = scrX*10 + 0x260 clamped to [0x1F4, 0xCDA]; the only
    # function using both clamp constants, and it follows GetDrawEnv.
    "PutDispEnv":      (0x8001683C, 0x8007A274, 0x80014598),
}


# SDK entry points bound by address to a shim in patches/Core/SdkShims.cs, written
# into config/kf3.json's patches[].
SHIMS = {
    "CdRead_ret0": "KfIII.SdkShims.CdRead",
}

# Hooks into the recompiled code: (overlay, address, patch method, "pre" or "post").
# Each runs before or after that function; the comment above each names it.
HOOKS = [
    ("game", "0x8001A34C", "KfIII.Diag.ArchiveOpened", "post"),
    ("game", "0x8001A154", "KfIII.Diag.EntryLoad", "pre"),
    ("game", "0x80079DC8", "KfIII.VramDiag.LoadImage", "pre"),
    ("game", "0x80079E2C", "KfIII.VramDiag.StoreImage", "pre"),
    ("game", "0x80079D34", "KfIII.VramDiag.ClearImage", "pre"),
    ("game", "0x80079E90", "KfIII.VramDiag.MoveImage", "pre"),
    # patches/Input/Controls.cs: mouse look, before the turn/look routine.
    ("game", "0x8002F5C0", "KfIII.Controls.BeforeLook", "pre"),
    # patches/Input/Controls.cs: run on left Shift, apart from interact (E).
    ("game", "0x8002FE1C", "KfIII.Controls.BeforeActions", "pre"),
    ("game", "0x8002FE1C", "KfIII.Controls.AfterActions", "post"),
    # patches/Core/Pools.cs: bigger primitive pools above 2 MB.
    ("game", "0x80035394", "KfIII.Pools.AfterLayout", "post"),
    # patches/Graphics/Smooth.cs: high frame rate (redraws in the frame gate).
    ("game", "0x800422B8", "KfIII.Smooth.BeforeRender", "pre"),
    ("game", "0x800422B8", "KfIII.Smooth.AfterRender", "post"),
    ("game", "0x80043940", "KfIII.Smooth.BeforeTickSlots", "pre"),
    ("game", "0x80019614", "KfIII.Smooth.Gate", "replace"),
    # patches/Graphics/WideCull.cs: the view-cone tile grid, widened for widescreen.
    ("game", "0x80034BF4", "KfIII.WideCull.Before", "pre"),
    ("game", "0x80034BF4", "KfIII.WideCull.After", "post"),
    ("game", "0x80074D88", "KfIII.WideCull.BeforeDivide", "pre"),
    ("game", "0x80075188", "KfIII.WideCull.BeforeDivide", "pre"),
    ("game", "0x800756A8", "KfIII.WideCull.BeforeDivide", "pre"),
    ("game", "0x80075B48", "KfIII.WideCull.BeforeDivide", "pre"),
    # patches/Graphics/Culling.cs: where each checked routine keeps its NCLIP corners.
    ("game", "0x80039D50", "KfIII.Culling.Ground", "pre"),
    ("game", "0x80039D50", "KfIII.Culling.Exit", "post"),
    ("game", "0x8003AB04", "KfIII.Culling.Ground", "pre"),
    ("game", "0x8003AB04", "KfIII.Culling.Exit", "post"),
    ("game", "0x80035CA4", "KfIII.Culling.Model", "pre"),
    ("game", "0x80035CA4", "KfIII.Culling.Exit", "post"),
    ("game", "0x80039428", "KfIII.Culling.Model2", "pre"),
    ("game", "0x80039428", "KfIII.Culling.Exit", "post"),
    ("game", "0x80037BEC", "KfIII.Culling.Model3", "pre"),
    ("game", "0x80037BEC", "KfIII.Culling.Exit", "post"),
    ("game", "0x8003DF50", "KfIII.Culling.Weapon", "pre"),
    # patches/Debug/Diag.cs ModelCallers: who draws models (KFIII_MODEL_CALLERS=1).
    ("game", "0x8003E34C", "KfIII.ModelCallers.Before", "pre"),
    ("game", "0x80035CA4", "KfIII.LightProbe.Before", "pre"),
    ("game", "0x80025BE8", "KfIII.ItemViewProbe.Before", "pre"),
    # patches/Debug/Diag.cs WaterProbe (who writes the water polygons): add its pre/post
    # pairs on 0x80039D50 / 0x8003AB04 / 0x80035CA4 / 0x80039428 / 0x8003BB04 to use.
    ("game", "0x8003DF50", "KfIII.Culling.Exit", "post"),
    # patches/Graphics/Mirror.cs: planar reflections; kf_Present replaced for the mirrored call.
    ("game", "0x80035700", "KfIII.Mirror.BeforePresent", "pre"),
    ("game", "0x800351FC", "KfIII.Mirror.BeforeScroll", "pre"),
    ("game", "0x80035630", "KfIII.Mirror.AfterBeginFrame", "post"),
    # patches/Audio/PositionalAudio.cs: directional sound.
    ("game", "0x80015410", "KfIII.PositionalAudio.BeforeSound", "pre"),
    ("game", "0x8001576C", "KfIII.PositionalAudio.BeforeFlat", "pre"),
    ("game", "0x8001576C", "KfIII.PositionalAudio.AfterFlat", "post"),
    ("game", "0x80015410", "KfIII.PositionalAudio.AfterSound", "post"),
    ("game", "0x8006DCE8", "KfIII.PositionalAudio.BeforeKeyOn", "pre"),
    ("game", "0x8006DCE8", "KfIII.PositionalAudio.AfterKeyOn", "post"),
    # patches/Interface/GearCompare.cs: equipment stats on the equipment lists.
    ("game", "0x8001CBB8", "KfIII.GearCompare.BeforeEquipList", "pre"),
    ("game", "0x8001CBB8", "KfIII.GearCompare.AfterEquipList", "post"),
    ("game", "0x80025BE8", "KfIII.GearCompare.BeforePreview", "pre"),
    ("game", "0x80024C70", "KfIII.GearCompare.BeforePrompt", "pre"),
    ("game", "0x80024C70", "KfIII.GearCompare.AfterPrompt", "post"),
    ("game", "0x800270F8", "KfIII.GearCompare.BeforePresent", "pre"),
    ("game", "0x80027198", "KfIII.KeyHints.AfterMenuEnter", "post"),
    ("game", "0x80027198", "KfIII.MenuMouse.AfterMenuEnter", "post"),
    ("game", "0x80027310", "KfIII.MenuMouse.AfterMenuLeave", "post"),
    ("game", "0x800222FC", "KfIII.MenuMouse.BeforeListInput", "pre"),
    ("game", "0x800222FC", "KfIII.MenuMouse.AfterListInput", "post"),
    ("game", "0x800252F4", "KfIII.MenuMouse.BeforeButtons", "pre"),
    ("game", "0x800221E8", "KfIII.MenuMouse.AfterButtonInput", "post"),
    ("game", "0x8001A634", "KfIII.KeyHints.BeforePause", "pre"),
    ("game", "0x8001A634", "KfIII.KeyHints.AfterPause", "post"),
    ("game", "0x80027310", "KfIII.KeyHints.AfterMenuLeave", "post"),
    ("game", "0x80021298", "KfIII.GearCompare.BeforeShop", "pre"),
    ("game", "0x80021298", "KfIII.GearCompare.AfterShop", "post"),
    ("game", "0x80021724", "KfIII.GearCompare.BeforeShop", "pre"),
    ("game", "0x80021724", "KfIII.GearCompare.AfterShop", "post"),
    # patches/Graphics/Water.cs: creature models tagged so their skins are never water.
    ("game", "0x8003E34C", "KfIII.Water.BeforeDrawModel", "pre"),
    ("game", "0x8003E34C", "KfIII.Water.AfterDrawModel", "post"),
    # patches/Gameplay/AutoKey.cs: interacting with a locked chest/door uses a key in the bag.
    ("game", "0x8005E2D0", "KfIII.AutoKey.BeforeInteract", "pre"),
    ("game", "0x8005E2D0", "KfIII.AutoKey.AfterInteract", "post"),
    ("game", "0x80041EEC", "KfIII.AutoKey.BeforeMessage", "pre"),
    ("game", "0x80045C7C", "KfIII.AutoKey.AfterFindProp", "post"),
    # patches/Gameplay/Jump.cs: a jump out of the game's own long fall.
    ("game", "0x8002ED60", "KfIII.Jump.BeforeVertical", "pre"),
    ("game", "0x8002ED60", "KfIII.Jump.AfterVertical", "post"),
    # patches/Core/MovieSkip.cs: once a frame inside GAME.EXE's movie player.
    ("game", "0x80061878", "KfIII.MovieSkip.EachFrame", "pre"),
    # patches/Core/EndScreen.cs: "The End" waits for a button instead of hanging.
    ("end", "0x8001C08C", "KfIII.EndScreen.BeforePadStop", "pre"),
    # patches/Core/SaveState.cs: the one point states are taken and applied.
    ("game", "0x80030FCC", "KfIII.SaveState.AtWorldUpdate", "pre"),
    # patches/Map/Automap.cs: the automap (reads the floor plan each tick).
    ("game", "0x80030FCC", "KfIII.Automap.BeforeWorldUpdate", "pre"),
    ("game", "0x8001A774", "KfIII.Automap.BeforeMenu", "pre"),
    ("game", "0x8001A774", "KfIII.Automap.AfterMenu", "post"),
    ("game", "0x8003BB04", "KfIII.Automap.BeforeDrawTile", "pre"),
    ("game", "0x8003BB04", "KfIII.Automap.AfterDrawTile", "post"),
    # Quick load from the title: leave OPEN.EXE from its VSync, caught at the
    # boot stub's Exec call, which then starts GAME.EXE.
    ("open", "0x8001FE6C", "KfIII.SaveState.TitleVSync", "pre"),
    ("main", "0x8001022C", "KfIII.SaveState.StubExec", "replace"),
    ("game", "0x80046C00", "KfIII.SaveState.BeforeFade", "pre"),
]

# Earlier SDK names that turned out wrong: renaming away from these is allowed.
SUPERSEDED = {"CdRead"}


def write_shims():
    import re
    cfg_path = ROOT / "config/kf3.json"
    text = cfg_path.read_text(encoding="utf-8-sig")
    cfg = json.loads(re.sub(r"^\s*//.*$", "", text, flags=re.M))
    # Every KfIII.* patch is written from SHIMS and HOOKS below, so drop them all
    # first: a hook removed from HOOKS must not linger in the config.
    patches = [p for p in cfg.get("patches", []) if not p.get("target", "").startswith("KfIII.")]
    for name, target in SHIMS.items():
        for col, ov in enumerate(("open", "game", "end")):
            patches.append({"overlay": ov, "address": f"0x{NAMES[name][col]:08X}",
                            "target": target, "mode": "replace"})
    hook_targets = {t for _, _, t, _ in HOOKS}
    patches = [p for p in patches if not p.get("target", "").startswith("KfIII.Diag.")
               and p.get("target") not in hook_targets]
    for overlay, addr, target, mode in HOOKS:
        patches.append({"overlay": overlay, "address": addr, "target": target, "mode": mode})
    cfg["patches"] = patches
    cfg_path.write_text(json.dumps(cfg, indent=2) + "\n", encoding="utf-8")


def main():
    write_shims()
    for col, ov in enumerate(("open", "game", "end")):
        path = ROOT / "config/funcmaps" / f"{ov}.json"
        data = json.loads(path.read_text())
        funcs = data["functions"] if isinstance(data, dict) else data
        by_addr = {int(f["address"], 16): f for f in funcs}
        for name, addrs in NAMES.items():
            f = by_addr.get(addrs[col])
            if f is None:
                raise SystemExit(f"{ov}: no function at 0x{addrs[col]:08X} for {name}")
            if f["name"] == name:
                continue
            if not f["name"].startswith("func_") and f["name"] not in SUPERSEDED:
                raise SystemExit(f"{ov}: 0x{addrs[col]:08X} is already {f['name']}, not renaming to {name}")
            clash = [g for g in funcs if g["name"] == name]
            if clash:
                raise SystemExit(f"{ov}: {name} already at {clash[0]['address']}")
            f["name"] = name
            print(f"{ov}: 0x{addrs[col]:08X} -> {name}")
        path.write_text(json.dumps(data, indent=2) + "\n")


if __name__ == "__main__":
    main()
    # The game's own functions (kf_*), after the SDK's.
    import name_game
    name_game.main()
