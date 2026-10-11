#!/usr/bin/env python3
"""Write the identified King's Field III game functions into config/funcmaps/game.json.

The recompiler names each generated C# method after its funcmap entry, so a name
here is what shows up in the generated code, stack traces, [watch] output and
crash logs instead of func_XXXXXXXX. The memory those functions own is named in
patches/Core/GameAddr.cs.

Rules:
  * Only proven functions -- by a watchpoint, a RAM diff, a hook or the
    disassembly -- each with one line of evidence. A guess stays func_XXXXXXXX.
  * Every name starts with kf_ (King's Field's own code) or lib_ (PSY-Q library
    code identified by what it does, not by its official name): the runtime binds
    its HLE by exact PSY-Q name (scripts/name_sdk.py), and neither prefix can
    collide with one. A library function goes in name_sdk.py instead only once
    its real name is certain and binding it is wanted.
  * The address stays the key. Comments, NOTES.md and hooks in name_sdk.py keep
    using it, so nothing breaks when a name is refined later.
  * Anything newly identified while fixing something is added here as part of
    that fix.

Run by scripts/name_sdk.py (after the SDK names); idempotent. Renaming a kf_
name to a better one is allowed; renaming anything else is refused.
"""

import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

# address: (name, evidence)
NAMES = {
    # ---- main loop and frame timing ------------------------------------------
    0x80014BD4: ("kf_MainLoop", "world update, camera, render, once per 15 Hz tick"),
    0x80030FCC: ("kf_WorldUpdate", "single caller is the main loop at 0x80014F3C; quick save/load point"),
    0x8002B330: ("kf_BuildCamera", "fills the pos/angle blocks kf_Render takes from 0x801B25F0/0x801B2608"),
    0x800422B8: ("kf_Render", "draws the frame, presents, waits in kf_FrameGate"),
    0x80019614: ("kf_FrameGate", "only caller kf_Render; waits until vblank counter 0x801C12EC >= 4"),
    0x80035700: ("kf_Present", "DrawOTag + VSync at the end of kf_Render"),
    0x80043940: ("kf_StepAfterGate", "after the gate in kf_Render; steps twelve 0x14-byte slots at 0x801AAB64"),

    # ---- player actions -----------------------------------------------------------
    0x8002F5C0: ("kf_TurnAndLook", "adds turn/look velocity to yaw 0x801B2612 / pitch 0x801B2610; not called while holding an item"),
    0x8005E2D0: ("kf_InteractTarget", "first call of kf_Interact: the creature or prop in front; switches on the prop type's class (definition byte 0, 0x8018FB3C + type*24): 0x40 pickup, 1..5 doors, 6/7 chests ..."),
    0x8005C308: ("kf_CreatureTalk", "(creature): its definition (byte +2 into 0x8018C7E8, 0x78 each) +0x38 points at a script; 0x70 first = a conversation"),
    0x80033D08: ("kf_TerrainCollide", "(x, y, z, radius, [sp+0x10] height): kf_TileSelect + kf_RunCollisionShape; 0 = free, floor Y in 0x801E6474 (patches/Map/Automap.cs samples the map with it)"),
    0x800324F0: ("kf_TileSelect", "(x, y, z): the tile record 0x801D4464 + 800*(z>>11) + 10*(x>>11) into 0x801E6464, the half (0/5) for this height into 0x801E646E"),
    0x8003260C: ("kf_RunCollisionShape", "runs the half's collision shape (+3, offsets at 0x801E4464): opcodes 0x10 floor, 0x20..0x25 half-planes, 0x30.. slopes"),
    0x80033F38: ("kf_Collide", "(x, y, z, radius, [sp+0x10] height, [sp+0x14] mask): terrain + objects; the player's move test"),
    0x8002ED60: ("kf_PlayerVertical", "per tick from kf_WorldUpdate: fall state 0x801B25E8 (0 ground, 0x40 long fall +40 gravity), speed 0x801B2656; landing damage at >= 480"),
    0x8002F9BC: ("kf_Move", "walk forward/back and strafe actions"),
    0x8002FE1C: ("kf_Actions", "attack, magic and run (held interact at 0x80030010)"),
    0x800305D8: ("kf_Interact", "interact / menu button; starts item pickups and messages"),
    0x8001A634: ("kf_Pause", "Start: pause"),
    0x8001F4C0: ("kf_ControlConfigScreen", "rewrites the action button table 0x80081868"),
    0x8005DB30: ("kf_PickupItemStep", "lifts the picked-up prop and turns it 0x40 a tick"),

    # ---- map -------------------------------------------------------------------------
    0x80034BF4: ("kf_BuildTileGrid", "25x25 draw grid from yaw 0x801AEC5E, scratch 0x1F800120 -> 0x801AEC84"),
    0x8003BFD0: ("kf_DrawTiles", "walks the tile grid; draws cells with bit 2"),
    0x8003BB04: ("kf_DrawTile", "called per tile by kf_DrawTiles; calls the two ground routines"),
    0x80039D50: ("kf_DrawGround", "map tile polygons; NCLIP corners at scratch 0x1F800030"),
    0x8003AB04: ("kf_DrawGroundNear", "near map tiles through libgte's subdivision"),

    # ---- models and objects ---------------------------------------------------------------
    0x80040AE4: ("kf_DrawObjects", "walks the creature, prop and effect tables"),
    0x8003E34C: ("kf_DrawModel", "creatures, props and effects: transform, animate, draw"),
    0x800400AC: ("kf_DrawPropModel", "props on the second path (secret doors)"),
    0x80035CA4: ("kf_DrawModelPolys", "model polygons; NCLIP corners in t0/t1/t2"),
    0x80037BEC: ("kf_DrawModelPolys3", "model polygons from kf_DrawModel (the crystal tree); four NCLIP sites, corners at scratch 0x1F800030/34/38, cull on <= 0"),
    0x80039428: ("kf_DrawModelPolys2", "model polygons from kf_DrawPropModel; corners in t2/t1/t3"),
    0x800431E8: ("kf_AnimateModel", "picks and blends the key frames around an animation time"),
    0x80042CAC: ("kf_FindKeyframe", "key frame and sub-frame for an animation time"),
    0x80042EB0: ("kf_BlendKeyframe", "applies a key frame delta scaled by the sub-frame"),
    0x80041514: ("kf_StepModelAnims", "advances counter 0x80182964 and the frame bytes of 128 slots per call"),
    0x8003DF50: ("kf_DrawWeapon", "the weapon in hand, only while swing time 0x801B25A4 >= 0"),
    0x80046034: ("kf_FindFreeProp", "first prop record with +6 == 0xFF in a range"),

    # ---- 2D, messages, screen effects ---------------------------------------------------------
    0x80041E68: ("kf_DrawSpriteList", "2D sprite list at 0x800819B4 (stride 0x14), via kf_DrawSprite"),
    0x80041AD4: ("kf_DrawSprite", "one 2D sprite packet from a sprite-list record"),
    0x80041EEC: ("kf_QueueMessage", "8-slot message queue 0x801AEAED; drops the request when full"),
    0x80046608: ("kf_UseKey", "(prop, item): prop lock byte +0x38 == item -> 0xFF, returns 1; 2 not locked, 3 wrong key, 4/0 not lockable"),
    0x8005BD34: ("kf_UnlockSound", "kf_PlaySound2D(0x10, 100): the key-turned sound (item menu: kf_UseKey returned 1)"),
    0x80045C7C: ("kf_FindPropAhead", "(start, &pos, near, far, [sp+0x10] yaw, [sp+0x14] width): next prop index from start in front of pos, or -1 (kf_InteractTarget, kf_UseItem)"),
    0x8002792C: ("kf_MenuSound", "(se): a menu sound; 0x0C the cursor-move click (kf_MenuListInput on Up/Down)"),
    0x80027A9C: ("kf_MenuShowItem", "(item): show that item's rotating model behind the menu (kf_MenuListInput after a move, when its item array a1 is set)"),
    0x8002A310: ("kf_GainExp", "(amount): add experience (0x801B24E4, max 999999); level-ups from the table 0x8009F114 (12 bytes a level), next total at 0x801B24E8"),
    0x8002A074: ("kf_PowerUse", "counts 0x801B2508 up; at 100: base PWR 0x801B2516 + 1 (max 999), message 1"),
    0x8002A10C: ("kf_MagicUse", "(spell): adds spell+3 to the counters 0x801B250A + 2n of the elements in spell+5; at 400: base magic 0x801B2518 + 2n + 1, message 0x12 + n"),
    0x8005D7F8: ("kf_RemoveItem", "(id): one of that item out of the bag (Orladin's Key on its pillar, used-up items)"),
    0x8002AB18: ("kf_PlayerHurt", "(slash, blow, stab, type, dark, fire, earth, wind, water, ...): damage to the player; gear blocks and ailment lengths (NOTES Hidden gear effects)"),
    0x80030BE0: ("kf_AddHp", "(n): HP 0x801B24FC += n, capped at max; 0 or less -> death"),
    0x8003456C: ("kf_SetFog", "(r, g, b, near, far, ...): the fog / light; darkness and the Groundal Shield call it"),
    0x8002CFE0: ("kf_WeaponMagicReady", "(slot 0/1): the weapon's spell record +3/+4 usable (special weapons, or PWR and all magic >= 50)"),
    0x8005C864: ("kf_WarpTable", "(n): area change from the 14 x 8 byte table at 0x800130F0 (area, sound bank, music)"),
    0x80029390: ("kf_ChargeRate", "(avg magic, spell weight): magic gauge refill per tick = avg*64/(weight+1) + 1, max 5000"),
    0x80028D54: ("kf_DeathPlaneCheck", "each tick: the player below a death height (0x801E647C/80/84, left by kf_TerrainCollide; lava and water surfaces) -> func_80030A6C(0), death"),
    0x8002A210: ("kf_ExcellectorGrow", "from kf_GainExp: owning Excellector stage 1/2 (items 0/1, bag or storage), u32 0x801B24EC = exp when got (-1 none); past +13000 it grows a stage (message 4)"),
    0x80024F88: ("kf_ItemGuide", "Triangle GUIDE: picture item+0x186 of file 6 if WIS (u16 0x801B2530) >= the item's need (u8 0x80081780 + id), else item+0x21C (the header line only)"),
    0x80043B38: ("kf_LoadTims", "uploads each TIM of a loaded file (LoadImage, its CLUT too)"),
    0x8005CBE0: ("kf_UseItem", "(0x801B25F0 player pos, 0x801B2610 view angles, item): use an item, as the menu does (kf_Interact after kf_Menu); items 0x65..0xA1 via the jump table 0x800131A0"),
    0x80017C78: ("kf_ChangeArea", "(area, ...): fills the area-change request 0x8018FAD4 (area at +0x10); 0xFF args keep the current"),
    0x80018358: ("kf_AreaChangeStep", "runs the area-change request 0x8018FAD4 stage by stage (+2)"),
    0x80046884: ("kf_PlayerInTileRect", "(tx, tz, w, h, y): is the player in that tile rectangle (exit strips, class 0xE0)"),
    0x80041F9C: ("kf_UpdateMessage", "fades in, holds, fades out the current message; called per kf_Render"),
    0x8003D64C: ("kf_DrawScreenTint", "full-screen 320x240 tint (poison), palette 7A8D"),
    0x80046C00: ("kf_FadeScreen", "32-frame screen fade (two at New Game)"),

    # ---- memory, files, movies -------------------------------------------------------------------
    0x800351FC: ("kf_ScrollTextures", "steps each scrolling texture slot (0x801AEB1C, 0x18 each) and MoveImages it; once per kf_Render"),
    0x80035630: ("kf_BeginFrame", "flips buffer index 0x801AEAE8, picks OT [0x801A9174] / pool / overlay OT [0x801A91B8], clears them"),
    0x80015410: ("kf_Sound3D", "sound effect at a position: (se|0x8000, &pos, vol, maxDist, [sp+0x10] fade, [sp+0x14] note); listener 0x801B0A10, yaw 0x801B0A24"),
    0x80015790: ("kf_PlaySound", "(se, left, right, note): first free voice, lib_SndKeyOn"),
    0x8001576C: ("kf_PlaySound2D", "(se, vol): kf_PlaySound with left = right"),
    0x80035394: ("kf_LayoutPrimPools", "lays out the two primitive pools from base 0x80199154"),
    0x8001A34C: ("kf_OpenArchive", "opens an FDAT archive table"),
    0x8001A154: ("kf_LoadArchiveEntry", "reads one archive entry into a buffer"),
    0x8001A774: ("kf_Menu", "the in-game menu, entered from kf_Interact"),
    0x8001AA84: ("kf_MenuItemList", "item / equipment list page with the rotating preview"),
    0x800270F8: ("kf_MenuPresent", "draws and flips the menu screen"),
    0x80025BE8: ("kf_ItemPreview", "loads (async) and draws the rotating item model; state at gp+0x1C..0x30"),
    0x80027AC0: ("kf_ItemModelLoad", "starts the async read of an item model (gp+0x28 = item)"),
    0x80027D88: ("kf_CdReadArchiveAsync", "Pause, Setmode 0x80, ReadN of an archive entry; returns sectors"),
    0x80027EA8: ("kf_ItemSectorReady", "CdReadyCallback: CdGetSector each sector into gp+0x168"),
    0x80019B68: ("kf_ArchiveChecksum", "0x12345678 + sum of words must equal the last word"),
    0x80061878: ("kf_MovieFlip", "one-line buffer flip, once a frame in the movie player"),
    0x80060D20: ("kf_MoviePlay", "GAME.EXE's STR movie player and its frame loop (patches/Core/MovieSkip.cs)"),
    0x800616DC: ("kf_MovieFetchFrame", "next movie frame; writes 0x63 to the movie status 0x801E824E when the stream ends"),
    0x80016AB8: ("kf_AngleTo", "(dx, dz): -atan2 in 4096ths of a turn (kf_Sound3D's pan)"),

    # ---- equipment and stats (patches/Interface/GearCompare.cs) ---------------------------------------------
    0x80029500: ("kf_RecalcStats", "rebuilds PWR/MAG 0x801B2524.. and offense/defense 0x801B2538..0x801B255A from base stats and equipment"),
    0x800293E4: ("kf_AddArmorStats", "(id): adds armour record 0x801E6078 + id*0x20 to the defense words"),
    0x8001C7A8: ("kf_MenuEquipment", "EQUIPMENT page: the ten slot rows; row -> kf_MenuEquipList(row)"),
    0x8001CBB8: ("kf_MenuEquipList", "(kind): the owned items for one slot, USE/CANCEL prompt, equips through kf_EquipWeapon / kf_EquipArmor"),
    0x80024C70: ("kf_MenuPrompt", "(menu, kind, flags, item): the modal USE/CANCEL prompt; -1 on cancel"),
    0x8002BDC0: ("kf_EquipWeapon", "(id): equips a weapon (0x801B25AF), loads its model"),
    0x8002BB84: ("kf_EquipArmor", "(id, slot 0..6): writes 0x801B25D4..DA, refreshes the record pointers, kf_RecalcStats"),
    0x80021298: ("kf_ShopBuy", "a shop's stock list (func_8001AEA4 builder), GOLD/QTY, kf_ItemPreview of the highlighted row"),
    0x80021724: ("kf_ShopSell", "the bag listed for selling (func_8001AF88), kf_ItemPreview of the highlighted row"),
    0x800222FC: ("kf_MenuListInput", "(list, ...): a list's pad input; list +0x1C x, +0x1D y, +0x1F rows shown, +0x20 first row, +0x22 highlighted row"),
    0x800252F4: ("kf_MenuButtons", "(page, count, selected): a page of buttons, text records of 0x1C from 0x8007E660 + page*0xFC + 0x1C"),
    0x800221E8: ("kf_MenuButtonInput", "(selected, last, &chosen, &confirmed): up/down/confirm/cancel -> the new selection"),
    0x80023300: ("kf_MenuOffenseDefense", "the OFFENSE/DEFENSE status screen"),
    0x800261DC: ("kf_MenuText", "(font 0x8007E570, rec): rec = s16 x, s16 y, glyphs (A = 0, space 0x7F, 0xFF ends)"),
    0x800277C0: ("kf_FormatNumber", "(value, width, 0, 0, [sp+0x10] out): digits for kf_MenuNumber"),
    0x8002636C: ("kf_MenuNumber", "(font 0x8007E564, rec): a number formatted by kf_FormatNumber"),
    0x80026ACC: ("kf_MenuWindow", "(x, y, w, h): a menu window, drawn after its text so it sorts beneath"),
    0x80027198: ("kf_MenuEnter", "first call of kf_Menu and kf_Pause: the menu screen over the frozen world"),
    0x80027310: ("kf_MenuLeave", "(0): last call of kf_Menu and kf_Pause"),
    0x80026FE4: ("kf_MenuBeginFrame", "starts each menu frame (before the page draws, then kf_MenuPresent)"),
    0x80025468: ("kf_MenuDrawList", "(menu, flags): draws a menu list window"),

    # ---- sound library (libsnd), by behaviour ----------------------------------------------------
    0x8006DCE8: ("lib_SndKeyOn", "SsUtKeyOn-like: (vab, prog, tone, note, fine, [sp+0x14] left, [sp+0x18] right) -> voice in v0"),
    0x80071FF0: ("lib_SndTick", "vblank IRQ sound tick: lib_SndVoiceFlush, then steps the sequences; every key-on comes from here"),
    0x8006C720: ("lib_SndVoiceFlush", "records each voice's envelope-zero bit (16-deep history 0x8009EEC4) and clears the busy byte (+0x33) of voices zero throughout"),
}


def main():
    path = ROOT / "config/funcmaps/game.json"
    data = json.loads(path.read_text())
    funcs = data["functions"] if isinstance(data, dict) else data
    by_addr = {int(f["address"], 16): f for f in funcs}
    names = [n for n, _ in NAMES.values()]
    dup = {n for n in names if names.count(n) > 1}
    if dup:
        raise SystemExit(f"duplicate names: {sorted(dup)}")
    for addr, (name, _why) in NAMES.items():
        if not name.startswith(("kf_", "lib_")):
            raise SystemExit(f"0x{addr:08X}: {name} does not start with kf_ or lib_")
        f = by_addr.get(addr)
        if f is None:
            raise SystemExit(f"game: no function at 0x{addr:08X} for {name}")
        if f["name"] == name:
            continue
        if not f["name"].startswith(("func_", "kf_", "lib_")):
            raise SystemExit(f"game: 0x{addr:08X} is already {f['name']}, not renaming to {name}")
        clash = [g for g in funcs if g["name"] == name]
        if clash:
            raise SystemExit(f"game: {name} already at {clash[0]['address']}")
        print(f"game: 0x{addr:08X} {f['name']} -> {name}")
        f["name"] = name
    path.write_text(json.dumps(data, indent=2) + "\n")


if __name__ == "__main__":
    main()
