namespace KfIII;

/// <summary>
/// King's Field III (SLUS-00255) memory: every RAM address the patches read or
/// write, named once. Patches refer to these rather than to bare numbers; the
/// functions that own them are named in scripts/name_game.py (kf_*).
///
/// Only what has been proven goes in here (by a watchpoint, a RAM diff or the
/// disassembly), with the evidence beside it or in NOTES.md. A record's field
/// offsets are listed with the record; patches use them as plain numbers.
/// Addresses are GAME.EXE's unless the section says otherwise.
/// </summary>
public static class GameAddr
{
    // ---- frame timing -----------------------------------------------------------------

    /// u32: vblanks since the last world tick; kf_FrameGate waits for 4 (15 Hz).
    public const uint VblankCounter = 0x801C12EC;
    /// u32: vblanks since boot.
    public const uint VblankTotal = 0x801C12E8;
    /// u32: kf_Render's own call counter (a cycle of about 32); water and texture
    /// animation run off it.
    public const uint RenderCounter = 0x801AEB20;

    // ---- player and camera ------------------------------------------------------------

    /// 3 x s32: the player's position X/Y/Z. kf_BuildCamera builds the camera from it
    /// (Y plus the s16s at 0x801B2650/0x801B2654, minus 0x640).
    public const uint PlayerPos = 0x801B25F0;
    /// 3 x s16: view pitch, yaw and roll, which the camera block is copied from.
    public const uint ViewAngles = 0x801B2608;
    /// s16: pitch, held within +-PitchLimit; increases looking down.
    public const uint Pitch = 0x801B2610;
    /// u16: yaw, 12 bits (4096 a turn); increases turning left.
    public const uint Yaw = 0x801B2612;
    /// The game's pitch limit, either way (0x2BC of 4096, about 62 degrees).
    public const int PitchLimit = 0x2BC;
    /// u8: kf_PlayerVertical's state (0 ground, 0x10 short fall, 0x20 ledge hop,
    /// 0x40 long fall, 0x50 landing dip). s16: its fall speed (negative is up; a
    /// landing at 480 or more hurts).
    public const uint FallState = 0x801B25E8, FallSpeed = 0x801B2656;
    /// u16: maximum and current HP.
    public const uint HpMax = 0x801B24FA, Hp = 0x801B24FC;
    /// u8: set while running (stamina drains).
    public const uint RunningFlag = 0x801B24F3;

    // ---- pad --------------------------------------------------------------------------

    /// u16: the pad word this frame and last frame, in the game's byte order.
    public const uint PadThisFrame = 0x801B265C, PadLastFrame = 0x801B265E;
    /// 14 x u16: the button for each action (the control-config screen rewrites it).
    public const uint ActionButtons = 0x80081868;
    /// u16: action 7, interact (held, it runs).
    public const uint InteractButton = 0x80081876;

    // ---- weapon -----------------------------------------------------------------------

    /// s16: swing time, -1 when not swinging, 0..3840 in steps of 384 a tick.
    public const uint SwingTime = 0x801B25A4;
    /// u8: swing animation.
    public const uint SwingAnim = 0x801B25AE;

    // ---- stats and equipment (kf_RecalcStats; NOTES.md "Equipment stats") -------------

    /// u16 x 6: base PWR and magic powers, and the totals kf_RecalcStats makes from
    /// them (PWR, fire, earth, water, wind, holy).
    public const uint BaseStats = 0x801B2516, Pwr = 0x801B2524;
    /// u16 x 8 offense (slash, blow, stab, holy, fire, earth, wind, water) and u16 x 9
    /// defense (slash, blow, stab, dark, fire, earth, wind, water, poison).
    public const uint Offense = 0x801B2538, Defense = 0x801B254A;
    /// u8: the equipped weapon; seven u8 armour slots (head, body, shield, arms,
    /// feet, item 1, item 2).
    public const uint WeaponSlot = 0x801B25AF, ArmourSlots = 0x801B25D4;
    /// Records by item id: weapons 0x44 bytes (offense at +6), armour 0x20 bytes
    /// (defense at +2).
    public const uint WeaponRecords = 0x801D37A4, ArmourRecords = 0x801E6078;
    /// u8 per item id: how many are in the bag.
    public const uint BagCounts = 0x800C85E8;

    // ---- creatures (the entity table) -------------------------------------------------

    /// 200 records of 0x88, drawn by kf_DrawObjects. +0x09 u8 == 1 live; +0x0C u8
    /// animation; +0x18 u16 animation time (0..0xFFF, loops); +0x2C/30/34 s32
    /// position; +0x40/42/44 s16 angles (not wrapped to 12 bits).
    public const uint EntityTable = 0x80185DA8;
    public const int EntityCount = 200, EntityStride = 0x88;
    /// Offsets in a chest's prop record: +0x3C not 0xFF means trapped (it fires as
    /// the lid opens), +0x3F is the trap's effect (0x1A poison gas).
    public const uint PropChestTrap = 0x3C, PropChestTrapEffect = 0x3F;
    /// Offset in an entity record, u8: the item the creature drops when killed, 0xFF none.
    public const uint EntityDrop = 0x0B;
    /// u16: the Palace of Wind's lit crystal lamps (type 0x72), bit n for entity
    /// LampFirstEntity + n (set by the code at 0x801E8758 in area module fdat80).
    public const uint LampFlags = 0x801BA9EA;
    public const int LampFirstEntity = 40;

    // ---- items, doors, props ----------------------------------------------------------

    /// 396 records of 0x44 (kf_FindFreeProp allocates). +0x01 u8 animation; +0x06
    /// u16 the prop type, 0xFF when free; +0x0A u16 animation time; +0x10 s16
    /// (passed to the drawer); +0x14/18/1C s32 position; +0x24/26/28 s16 angles.
    public const uint PropTable = 0x80191A5C;
    public const int PropCount = 396, PropStride = 0x44;
    /// 24 bytes per prop type: byte 0 the class (0x01..0x05 doors, 0x06/0x07 chests,
    /// 0x40 items, 0xE0 and up triggers, 0xFF scenery), +6 a door's width.
    public const uint PropDefs = 0x8018FB3C;
    /// 24-byte item names by item id, in menu glyphs (A = 0, space 0x7F, 0xFF ends).
    public const uint ItemNames = 0x8007F620;
    /// 0x78 bytes per creature type (creature record +2): +0x38 a script, which
    /// starts with 0x70 when the creature talks.
    public const uint CreatureDefs = 0x8018C7E8;

    // ---- spells and effects -----------------------------------------------------------

    /// Records of 0x4C, ended by one whose first byte is 0xFF. +0x04 u8 animation;
    /// +0x08 u8, live when either of its low two bits is set; +0x14 u16 animation
    /// time; +0x18/1C/20 s32 position; +0x28/2A/2C s16 angles; +0x30/32/34 s16
    /// scale (0x1000 = 1).
    public const uint EffectTable = 0x801B80EC;
    public const int EffectStride = 0x4C;

    // ---- model animation --------------------------------------------------------------

    /// A u32 counter and 128 slots of 0x18 (frame byte at +5) that kf_StepModelAnims
    /// advances on every call.
    public const uint ModelAnimCounter = 0x80182964, ModelAnimSlots = 0x80182968;
    public const int ModelAnimSlotCount = 128, ModelAnimSlotStride = 0x18;

    // ---- on-screen messages -----------------------------------------------------------

    /// kf_QueueMessage / kf_UpdateMessage's state, 13 bytes: an 8-slot queue (0xFF =
    /// free) at 0x801AEAED, read index 0x801AEAF5, write index 0x801AEAF6, phase
    /// 0x801AEAF7, hold countdown 0x801AEAF8, fade level 0x801AEAF9.
    public const uint MessageQueue = 0x801AEAED;
    public const int MessageStateLength = 13;

    // ---- menu -------------------------------------------------------------------------

    /// u8: enables kf_ItemPreview's rotating item model (its own state is at
    /// gp+0x1C..0x30, model buffer gp+0x168).
    public const uint ItemPreviewEnabled = 0x801B25DF;

    // ---- the area's floor plan (patches/Map/Automap.cs) -------------------------------

    /// 80 x 80 tile records of 10 bytes (800 a row), two 5-byte halves: +0 model
    /// (0xFF none), +1 height (floor Y = -h * 128), +2 rotation, +3 collision shape,
    /// +4 flags. Shapes are found by u16 offsets from CollisionShapes; CollisionFloor
    /// is the floor Y the last collision test found.
    public const uint TileRecords = 0x801D4464, CollisionShapes = 0x801E4464, CollisionFloor = 0x801E6474;
    /// u8[0x60]: kf_TerrainCollide's working state, 0x801E6460..0x801E64C0 (the automap
    /// saves and restores it around its samples).
    public const uint CollisionState = 0x801E6460, CollisionStateEnd = 0x801E64C0;
    /// 3 x s32: death heights kf_TerrainCollide leaves for the spot (100000 = none);
    /// kf_DeathPlaneCheck kills the player below one (lava and water surfaces).
    public const uint DeathHeights = 0x801E647C;

    // ---- visible tiles and the ground -------------------------------------------------

    /// kf_BuildTileGrid: a 25 x 25 byte grid (bit 2 = draw; 0x1E near, 0x1A far),
    /// built in the scratchpad and copied out; its first column and row; the yaw it
    /// was built for.
    public const uint TileGrid = 0x801AEC84, TileGridScratch = 0x1F800120;
    public const uint TileGridCol0 = 0x801AEC74, TileGridRow0 = 0x801AEC78;
    public const uint TileGridYaw = 0x801AEC5E;
    public const int TileGridSize = 25;
    /// s32: fog start and end in view depth (2048 = one tile).
    public const uint FogStart = 0x801AEC7C, FogEnd = 0x801AEC80;
    /// s32: the scratchpad copies kf_DrawGround reads. A corner's IR0 for the depth
    /// cue is 4096 * (SZ - start) / (end - start), clamped to 0..0x1F0F; a start of
    /// 0x7D00 or more means no fog.
    public const uint ScratchFogStart = 0x1F800058, ScratchFogEnd = 0x1F80005C;
    /// 3 x u32 in the scratchpad: the vertex words kf_DrawGround and
    /// kf_DrawGroundNear load before NCLIP.
    public const uint GroundCornerPtrs = 0x1F800030;
    /// Scratchpad: kf_DrawGround's primitive write pointer and its limit, and its
    /// current face record (+0 uv0, +2 clut, +4 uv1, +6 tpage, +8 uv2, +0xC normal,
    /// +0x10/+0x12 vertex offsets; byte +3 is the packet code, 0x24 / 0x2C / 0x34).
    public const uint ScratchPrimCur = 0x1F800014, ScratchPrimEnd = 0x1F800018, ScratchFace = 0x1F800020;

    /// VRAM, not RAM: the river's scrolling texture. Each draw copies the source
    /// (984,96, 8 x 32) to (1016,96) shifted down k rows (MoveImage); k steps once a tick.
    public const int WaterSrcX = 984, WaterSrcY = 96, WaterDstX = 1016, WaterDstY = 96;

    // ---- primitive pools (patches/Core/Pools.cs) --------------------------------------

    /// Two pool descriptors (u32 start, end, current), the base kf_LayoutPrimPools
    /// lays them out from, and the pointer to the one in use.
    public const uint PrimPool0 = 0x80199158, PrimPool1 = 0x80199164;
    public const uint PrimPoolBase = 0x80199154, PrimPoolActive = 0x80199170;

    // ---- archives (FDAT.T and the other data files) -----------------------------------

    /// ArchiveCount descriptors of 12 bytes, filled by kf_OpenArchive: +0 pointer to
    /// the archive's table of u16 start sectors, one per entry, +4 the CdlLOC of
    /// the archive, +8 its size.
    public const uint ArchiveDescs = 0x801C172C;
    public const int ArchiveCount = 9, ArchiveDescStride = 12;

    // ---- sound ------------------------------------------------------------------------

    /// 3 x s32: where kf_Sound3D measures a sound's distance from (the player).
    public const uint ListenerPos = 0x801B0A10;
    /// s16: the yaw kf_Sound3D pans against.
    public const uint ListenerYaw = 0x801B0A24;
    /// libsnd's voice table, 24 records of 0x34: +0x1E u16 the envelope level last
    /// read from the SPU, +0x33 u8 busy. lib_SndVoiceFlush clears busy once the
    /// voice's level has read 0 in every entry of the history below.
    public const uint LibsndVoices = 0x8009E9C4;
    public const int LibsndVoiceStride = 0x34;
    /// u8: how many voices libsnd manages (24).
    public const uint LibsndVoiceCount = 0x801B2684;
    /// 16 x u32: per vblank, one bit for each voice whose envelope read 0; the
    /// write index (0..15) is the u32 at LibsndEnvZeroIndex.
    public const uint LibsndEnvZeroHistory = 0x8009EEC4, LibsndEnvZeroIndex = 0x8009EEBC;

    // ---- boot stub (SLUS_002.55) ------------------------------------------------------

    /// u8: the next executable (0 OPEN, 1 GAME, 2 END); u8 mode; u8 the title menu's
    /// choice (0 New Game). Set by an executable before it returns to the stub.
    public const uint StubNextExe = 0x800102F0, StubMode = 0x800102F8, StubMenuChoice = 0x800102FA;

    // ---- movies -----------------------------------------------------------------------

    /// u16: GAME.EXE's movie player status (kf_MovieFlip runs once a frame); 0x63
    /// once the stream has ended.
    public const uint MovieStatus = 0x801E824E;

    // ---- menus (kf_MenuText / kf_MenuNumber) -------------------------------------------

    /// a0 for kf_MenuText / kf_MenuNumber: the menu text font and number font.
    public const uint MenuTextFont = 0x8007E570, MenuNumberFont = 0x8007E564;
    /// 3 x u32 (gp+0x5C/0x60/0x64): R, G, B every menu glyph is modulated by; 0x80 = as drawn.
    public const uint MenuTint = 0x8009C270;
    /// kf_MenuButtons' text: pages of 0xFC bytes, each a 0x1C-byte header then one
    /// 0x1C-byte record per button (s16 x at +0, s16 y at +2).
    public const uint MenuButtonPages = 0x8007E660;

    // ---- experience, training and spells ----------------------------------------------

    /// u32: the one word kf_RecalcStats writes outside the stat totals (0x801B2524..0x801B255B).
    public const uint RecalcScratch = 0x801C12F0;
    /// Spell records of 24 bytes (31 learnable): +0 learned / weapon spell enabled, +1 weight
    /// (cast refill), +3 practice value, +5 element bits (1 fire 2 earth 4 water 8 wind
    /// 0x10 holy), +0x16 MP cost.
    public const uint SpellRecords = 0x801B77EC;
    /// u32: pointer to the selected spell's record.
    public const uint SelectedSpell = 0x801B2590;
    /// u32: pointer to the equipped weapon's record; 7 x u32: the armour slots' record
    /// pointers (read by kf_WorldUpdate's regeneration).
    public const uint WeaponRecordPtr = 0x801B2594, ArmourRecordPtrs = 0x801B25B8;
    /// u32: experience (max 999999); u32: total needed for the next level (set by
    /// kf_GainExp); u8: level.
    public const uint Exp = 0x801B24E4, NextLevelExp = 0x801B24E8, Level = 0x801B24F0;
    /// 12-byte records by level: +8 the experience level L + 2 needs.
    public const uint LevelTable = 0x8009F114;
    /// u16: PWR training count (kf_PowerUse; base PWR +1 at 100); 5 x u16: magic training
    /// per element, fire earth water wind holy (kf_MagicUse; +1 at 400).
    public const uint PowerPractice = 0x801B2508, MagicPractice = 0x801B250A;
    /// u32: experience when the held Excellector stage was got, -1 right after a growth
    /// (kf_ExcellectorGrow; grows past +13000).
    public const uint ExcellectorExp = 0x801B24EC;
    /// u8 per item id: how many are in storage.
    public const uint StorageCounts = 0x800C867E;
    /// u8: set while the player stands where the Earth Shield works (marked by the area's zone props).
    public const uint EarthGround = 0x801AEAEA;

    // ---- frame buffers (kf_BeginFrame / kf_Present) -------------------------------------

    /// u8: which of the two frame buffers is being built (0/1); kf_BeginFrame flips it.
    public const uint FrameBufferIndex = 0x801AEAE8;
    /// 2 x DRAWENV (92 bytes) / 2 x DISPENV (20 bytes), indexed by FrameBufferIndex.
    public const uint DrawEnvs = 0x801A91BC, DispEnvs = 0x801A9274;
    /// u32: pointer to the frame's ordering table (0x2000 entries; kf_Present draws from
    /// +0x7FFC) / to the 8-entry overlay ordering table linked at its end.
    public const uint OrderingTablePtr = 0x801A9174, OverlayOtPtr = 0x801A91B8;
    /// 3 x u32: per-frame words kf_BeginFrame zeroes.
    public const uint BeginFrameWords = 0x801AEB10;
    /// 0x400 bytes: the renderer's camera, angles and tile grid (TileGrid lies inside it).
    public const uint RenderState = 0x801AEC00;
}
