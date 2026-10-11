using System.Numerics;
using System.Text;
using RecompOne.Runtime.Memory;

namespace KfIII;

// Markers: what the map shows on top of the floor plan, rebuilt every tick from the
// prop table and the creature (entity) table. A marker shows once the map under it has
// been seen, except the cheat markers, which show anyway.
//
// Props (GameAddr.PropTable, type at +6) are classed by the first byte of their type's
// 24-byte definition (GameAddr.PropDefs), as kf_InteractTarget switches on it. The
// classes the map knows are listed in CollectMarkers. Props that hold something (chests,
// searchable things, hidden items, Orladin's pillars) keep the index of the prop that is
// their content at u16 +0x3A (0xFFFF none); that prop's type is the item id, as it is for
// an item lying on the ground (class 0x40), and becomes 0xFF once the item is taken. A
// chest's or door's byte +0x38 is its lock (see LockOf). A door's position is its hinge,
// +0x26 its angle, and its definition's +6 its width.
//
// Creatures (GameAddr.EntityTable) are people when their definition (GameAddr.CreatureDefs,
// indexed by byte +2) points at +0x38 to a script whose first byte is 0x70: a
// conversation (kf_CreatureTalk).
public static partial class Automap
{
    const int PropDefStride = 24, CreatureDefStride = 0x78;
    /// <summary>Item ids are below this; prop types from it up are scenery and the like.</summary>
    const int ItemCount = 0xB5;
    /// <summary>Area index of the Palace of Wind (its crystal lamps).</summary>
    const int PalaceOfWind = 26;

    public enum MarkerKind : byte { Door, LockedDoor, Chest, Tree, Person, Barrel, Remains, Grave, Well, Trap, SecretDoor, Pedestal, Item, Enemy, SavePoint, Exit, Stump, Lamp, Hidden, Portal, Throne, Warp }

    public struct Marker
    {
        public MarkerKind Kind;
        /// <summary>Position in samples; a door's direction and length from its hinge.</summary>
        public float X, Z, DirX, DirZ, Length;
        /// <summary>A chest opened, or the throne holding Ichrius' Key.</summary>
        public bool Done;
        /// <summary>The item inside, lying there or dropped; -1 none.</summary>
        public int Item;
        /// <summary>The lock (LockOf): the key's item id, or a negative code.</summary>
        public int Key;
        /// <summary>A container known to hold nothing.</summary>
        public bool Empty;
        /// <summary>A trapped chest, and the trap's effect byte (0x1A poison gas).</summary>
        public bool Trapped;
        public byte TrapEffect;
        /// <summary>A crystal lamp: lit, unlit, or null where it is not known.</summary>
        public bool? Lit;
        /// <summary>What it is, where the kind's own word would be wrong (Guide's labels).</summary>
        public string? Label;
        /// <summary>A monster's drop chance: 256 certain, else out of 256.</summary>
        public int Chance;
        /// <summary>An exit: the trigger rectangle from (X, Z), in samples.</summary>
        public float W, H;
        /// <summary>A hand-written guide note (Guide's hints), shown with the keys cheat.</summary>
        public string? Hint;
        /// <summary>An enemy's name (CreatureNames).</summary>
        public string? Who;
        /// <summary>The name in the hover label: the item, the person, the area an exit leads to.</summary>
        public string? Name;
        /// <summary>A portal: its pair's number (the colour), -1 none found.</summary>
        public int Pair = -1;
        /// <summary>A portal or warp glyph: where it sends you (a portal in samples, a glyph in tiles).</summary>
        public float DestX, DestZ;
        public Marker() { }
    }

    /// <summary>This tick's markers; lock it to read.</summary>
    public static readonly List<Marker> Markers = new();

    /// <summary>The map's cheats (Enhancements, off by default and in Classic mode).</summary>
    public static bool ShowSecrets = DevSwitch.Is("KFIII_MAP_SECRETS");
    public static bool ShowTraps = DevSwitch.Is("KFIII_MAP_TRAPS");
    public static bool ShowItems, ShowContents, ShowEnemies, ShowKeys;

    /// <summary>How many of the Palace of Wind's twelve lamps are lit, or null elsewhere.</summary>
    public static int? LampsLit { get; private set; }

    /// <summary>The memory last read, for KeyName.</summary>
    static IMemory? _mem;

    public static string KeyName(int id) => _mem != null ? ItemName(_mem, id) : $"item {id:X2}";

    /// <summary>An item's name from the game's own table (GameAddr.ItemNames), in title case.</summary>
    public static string ItemName(IMemory m, int id)
    {
        if (id < 0 || id >= ItemCount) return $"item {id:X2}";
        uint rec = GameAddr.ItemNames + (uint)id * 24;
        var sb = new StringBuilder();
        bool start = true;
        for (uint k = 0; k < 24; k++)
        {
            byte g = m.ReadU8(rec + k);
            if (g == 0xFF) break;
            char ch = g switch
            {
                < 26 => (char)('A' + g),
                >= 0x20 and < 0x2A => (char)('0' + g - 0x20),
                0x32 => '\'',
                0x33 => '-',
                0x7F => ' ',
                _ => '?',
            };
            sb.Append(start || !char.IsLetter(ch) ? ch : char.ToLowerInvariant(ch));
            start = ch is ' ' or '-';
        }
        return sb.Length == 0 ? $"item {id:X2}" : sb.ToString();
    }

    /// <summary>
    /// A chest's or door's lock byte (+0x38) as Marker.Key:
    /// <list type="bullet">
    /// <item>the key's item id;</item>
    /// <item>-1 not locked (0xFF, or 0xFE opened);</item>
    /// <item>-2 sealed (0xF0/0xF1: a message, never opens);</item>
    /// <item>-3 opened by an event: Quist's area code turns door #70 and chests #33/#116
    /// from 0xC8 to 0xFF once the flag at 0x801BA99E is set (fdat02 0x801E8D68,
    /// tests/saves/lockeddoor.kfstate);</item>
    /// <item>-5 sealed until a switch or event: 0x96/0x98/0x9A on a door are not spells
    /// (though those item ids are Fire Ball, Fire Storm...). kf_InteractTarget
    /// (0x8005E610) treats them as 0xF0/0xF1/0xF2 -- messages 23, 5 "Sealed", 15 "Doesn't
    /// Open" -- and nothing compares a cast spell with +0x38. The area opens them: in
    /// fdat32 a switch appears after a conversation and an area reload
    /// (tests/saves/sealed98.kfstate, door #85);</item>
    /// <item>-4 anything else.</item>
    /// </list>
    /// </summary>
    static int LockOf(int lk) => lk switch
    {
        0xFF or 0xFE => -1,
        0xF0 or 0xF1 => -2,
        0xC8 => -3,
        0x96 or 0x98 or 0x9A => -5,
        < ItemCount => lk,
        _ => -4,
    };

    /// <summary>The item a prop is (its type), or -1 when it is not an item or has been taken.</summary>
    static int PropItem(IMemory m, int index)
    {
        if (index < 0 || index >= GameAddr.PropCount) return -1;
        int t = m.ReadU16(GameAddr.PropTable + (uint)(index * GameAddr.PropStride) + 6);
        return t is 0xFF or >= ItemCount ? -1 : t;
    }

    /// <summary>Whether anything within 3 samples of (x, z) is shown on the map.</summary>
    static bool SeenAt(Area a, float x, float z)
    {
        int sx = (int)x, sz = (int)z;
        for (int dz = -3; dz <= 3; dz++)
            for (int dx = -3; dx <= 3; dx++)
            {
                int xx = sx + dx, zz = sz + dz;
                if ((uint)xx < Size && (uint)zz < Size && a.Visible(zz * Size + xx)) return true;
            }
        return false;
    }

    /// <summary>
    /// Whether a crystal lamp (creature type 0x72 or 0x73) is lit; also updates LampsLit.
    /// The Palace of Wind's module (fdat80, 0x801E8758) lights entity #n by setting bit
    /// n - 40 of GameAddr.LampFlags (event flags 0x62/0x63; the palace's twelve are #40..#49
    /// of type 0x72 and #50/#51 of type 0x73), and 0x801E86C4 turns those bits into the
    /// palace's brightness (tests/saves/lamp_unlit, lamp_lit.kfstate: #49 0x0100 -> 0x0300).
    /// Elsewhere: null, not known.
    /// </summary>
    static bool? LampLit(IMemory m, uint rec, int area)
    {
        LampsLit = area == PalaceOfWind ? BitOperations.PopCount((uint)m.ReadU16(GameAddr.LampFlags) & 0xFFF) : null;
        int n = (int)(rec - GameAddr.EntityTable) / GameAddr.EntityStride - GameAddr.LampFirstEntity;
        if (area != PalaceOfWind || n is < 0 or >= 12) return null;
        return ((m.ReadU16(GameAddr.LampFlags) >> n) & 1) != 0;
    }

    /// <summary>From the tick: rebuilds <see cref="Markers"/> from the prop and creature tables.</summary>
    static void CollectMarkers(IMemory m, Area a)
    {
        _mem = m;
        WatchProbes(m);
        int areaIndex = AreaIndex(a.Name);
        var list = new List<Marker>();
        // Props a container holds: not shown as lying on the ground.
        var held = new HashSet<int>();
        // The hidden creature pair (type 0x6E) on either side of each secret wall: a door
        // with one against it is secret too, whatever its class (the Cave of Shudom's #18
        // is class 5, tests/saves/secret_door2.kfstate).
        var secretPair = new List<(float X, float Z)>();
        for (int e = 0; e < GameAddr.EntityCount; e++)
        {
            uint er = GameAddr.EntityTable + (uint)(e * GameAddr.EntityStride);
            if (m.ReadU8(er + 1) == 0x6E && (m.ReadU8(er) == 1 || m.ReadU8(er + 9) == 1))
                secretPair.Add(((int)m.ReadU32(er + 0x2C) / (float)Sample, (int)m.ReadU32(er + 0x34) / (float)Sample));
        }
        for (int i = 0; i < GameAddr.PropCount; i++)
        {
            uint rec = GameAddr.PropTable + (uint)(i * GameAddr.PropStride);
            int type = m.ReadU16(rec + 6);
            if (type == 0xFF) continue;
            byte cls = m.ReadU8(GameAddr.PropDefs + (uint)type * PropDefStride);
            if (cls is 6 or 7 or 9 or 0x15 or 0x17) { int h = m.ReadU16(rec + 0x3A); if (h != 0xFFFF) held.Add(h); }
            // An Orladin's pillar's +0x3C is the key model that shows in it once used.
            if (cls == 0x17) { int h = m.ReadU16(rec + 0x3C); if (h != 0xFFFF) held.Add(h); }
        }

        for (int i = 0; i < GameAddr.PropCount; i++)
        {
            uint rec = GameAddr.PropTable + (uint)(i * GameAddr.PropStride);
            int type = m.ReadU16(rec + 6);
            if (type == 0xFF) continue;
            uint def = GameAddr.PropDefs + (uint)type * PropDefStride;
            byte cls = m.ReadU8(def);
            float x = (int)m.ReadU32(rec + 0x14) / (float)Sample, z = (int)m.ReadU32(rec + 0x1C) / (float)Sample;
            PropRecordProbes(m, i, rec, type, cls, x, z);
            MarkerKind kind;
            // Class 3: a secret wall that opens (tests/saves/quicksave_spears). A cheat.
            if (cls == 3) { if (!ShowSecrets) continue; kind = MarkerKind.SecretDoor; }
            // Doors: classes 0..5 and 0x1B (the big double door, Garrison Barracks). Each
            // opens when its lock byte +0x38 is 0xFF and is locked otherwise
            // (kf_InteractTarget 0x8005E5E8 for 1/2, 0x8005E600 for 3..5 and 0x1B, which
            // also takes the sealed codes, see LockOf). Class 2 does not mean locked: the
            // Garrison Barracks' unlocked doors to other areas are class 2
            // (tests/saves/barracks_doors). Class 0 is the Barracks' jail doors (type
            // 0x12D; the interact switch sends 0..2 down one path, tests/saves/jaildoor.kfstate).
            else if (cls is <= 5 or 0x1B)
            {
                bool secret = secretPair.Exists(q => Math.Abs(q.X - x) < 12 && Math.Abs(q.Z - z) < 12);
                if (secret) { if (!ShowSecrets) continue; kind = MarkerKind.SecretDoor; }
                else kind = m.ReadU8(rec + 0x38) == 0xFF ? MarkerKind.Door : MarkerKind.LockedDoor;
            }
            // Class 0xE0 (type 0x118): an area exit, an invisible strip of +0x38 x +0x39
            // tiles from its tile. Stepping in calls kf_ChangeArea with area +0x3A (the
            // check is at 0x8004A7F8). Neighbouring areas overlap, and each side has one
            // strip to the other area and one back to itself, which the game ignores and
            // so does the map (tests/saves/battlefield_exit.kfstate).
            else if (cls == 0xE0)
            {
                int target = m.ReadU8(rec + 0x3A);
                if (target == 0xFF || target == areaIndex) continue;
                float w = m.ReadU8(rec + 0x38) * PerTile, h = m.ReadU8(rec + 0x39) * PerTile;
                bool seenExit = false;
                for (float sz = 0; sz < h && !seenExit; sz += PerTile)
                    for (float sx = 0; sx < w && !seenExit; sx += PerTile)
                        seenExit = SeenAt(a, x + sx + PerTile / 2f, z + sz + PerTile / 2f);
                if (!seenExit) continue;
                list.Add(new Marker
                {
                    Kind = MarkerKind.Exit,
                    X = x,
                    Z = z,
                    W = w,
                    H = h,
                    Item = -1,
                    Key = -1,
                    Name = AreaName(target)
                });
                continue;
            }
            // Class 0x18: a key pedestal (+0x3B the key's item id).
            else if (cls == 0x18) kind = MarkerKind.Pedestal;
            // Class 0x0E: the save crystal (type 0xE3).
            else if (cls == 0x0E) kind = MarkerKind.SavePoint;
            else if (cls is 6 or 7) kind = MarkerKind.Chest;
            // Class 0x17 (type 0xA2): an Orladin's pillar, a chest opened only by
            // Orladin's Key, which it keeps (+0x38 0x88 -> 0xFE, kf_UseItem 0x8005D300).
            // Its prize is the prop at +0x3A (tests/saves/orladin_pillar.kfstate, #10).
            else if (cls == 0x17) kind = MarkerKind.Chest;
            // Class 0x51 (type 0xDB, data at +0x38): the wall spear trap; class 0x52 (type
            // 0xDC): a wall spear that strikes by itself as you pass
            // (tests/saves/spear_traps2.kfstate, a pair facing each other). A cheat.
            else if (cls is 0x51 or 0x52) { if (!ShowTraps) continue; kind = MarkerKind.Trap; }
            // Class 9, searchable things, with byte 0 bit 1 clear: not an object of its
            // own. The queen's big grave in Quist is three grave records, two of them extra
            // pieces with byte 0 = 0 (tests/saves/queengrave.kfstate).
            else if (cls == 9 && (m.ReadU8(rec) & 2) == 0) continue;
            // Class 0x15 (type 0x12A): a hidden item, an invisible spot that gives what it
            // holds (+0x3A, as class 9) when you interact there. Five in Ralugo by the
            // graves (tests/saves/ralugo_hidden.kfstate); the Cave of Shudom's is at a
            // giant skeleton and holds the Magician's Key (giant_skeleton.kfstate, #31).
            // A cheat (with the items or contents cheat); gone once taken.
            else if (cls == 0x15)
            {
                if (!ShowItems && !ShowContents) continue;
                int hh = m.ReadU16(rec + 0x3A);
                if (hh == 0xFFFF) continue;
                kind = MarkerKind.Hidden;
            }
            // Type 0xF7: a clay pot (Ralugo).
            else if (cls == 9 && type == 0xF7) kind = MarkerKind.Barrel;
            // Other searchable things, told apart by type: graves have definition +3 == 4
            // (0xBE/0xBF/0xC0/0xC4) or are 0xB8; 0xD4 is the barrel (Quist and the next
            // area) and 0xD5 the small bucket (tests/saves/bucket.kfstate); 0xF5 a well.
            // Bodies (Soldier's Body, 0x12C..0x131, tests/saves/soldierbody.kfstate),
            // skeletons (0xB7/0xBA) and anything not identified are remains.
            else if (cls == 9)
                kind = type switch
                {
                    0xB8 => MarkerKind.Grave,
                    0xF5 => MarkerKind.Well,
                    0xD4 => MarkerKind.Barrel,
                    0xD5 => MarkerKind.Barrel,
                    _ when m.ReadU8(def + 3) == 4 => MarkerKind.Grave,
                    _ => MarkerKind.Remains,
                };
            // Class 0x40: an item on the ground, a cheat. Byte 0 bit 1 is set when it is
            // out in the world: clear for those a later event places (Leon's house holds
            // three from the start) and for those a container holds.
            else if (cls == 0x40 && type < ItemCount) { if (!ShowItems || held.Contains(i) || (m.ReadU8(rec) & 2) == 0) continue; kind = MarkerKind.Item; }
            // Scenery (class 0xFF) of types 0x144..0x146: the trees.
            else if (cls == 0xFF && type is >= 0x144 and <= 0x146) kind = MarkerKind.Tree;
            // Class 0x56 (type 0x12D): the Forest of Varde's giant tree stumps. Many come in
            // pairs, and Varde's Key swaps which one stands: the standing one has byte 0
            // bit 1 set (and +8 = 1), the other byte 0 = 0 (+8 = 2). Not a cheat: they can
            // be seen. tests/saves/stump_before, stump_after.kfstate (#11 and #14).
            else if (cls == 0x56) { if ((m.ReadU8(rec) & 2) == 0) continue; kind = MarkerKind.Stump; }
            // Class 0xE8 (type 0x13F): the High Elf Ruins' warp arches. Standing on the 1x1
            // tile at +0x38/+0x39 sends you to the tile at +0x3C/+0x3D, facing +0x3F
            // (func_80047010, 0x8004A66C). Each one's goal is its partner's own tile, so
            // they come in pairs (tests/saves/portals.kfstate: 6). The posts either side
            // are class 0xE9.
            else if (cls == 0xE8) kind = MarkerKind.Portal;
            // Class 0xA7 (type 0x140): the Throne of Ichrius. Ichrius' Key put in it
            // (+0x38 = 0x85, kf_UseKey's 0xA7 case) opens the portals; they keep working
            // when it is taken back out (tests/saves/throne.kfstate).
            else if (cls == 0xA7) kind = MarkerKind.Throne;
            // Class 0xEB: a warp glyph on the floor (the High Elf Ruins' type 0x141, to area
            // 16). +0x38 the area, +0x39/+0x3A its sound bank and music, +0x3B/+0x3C the
            // tile you arrive on, +0x3E the facing (func_80047010 0x8004A2D4; Teleport's
            // warp props). tests/saves/glyph.kfstate.
            else if (cls == 0xEB) kind = MarkerKind.Warp;
            else continue;

            PropProbe(m, i, kind, type, cls, x, z, rec, def);
            var mk = new Marker { Kind = kind, X = x, Z = z, Item = -1, Key = -1 };
            if (kind == MarkerKind.Item) mk.Item = type;
            if (kind == MarkerKind.Portal)
            {
                mk.DestX = m.ReadU8(rec + 0x3C) * PerTile + PerTile / 2f;
                mk.DestZ = m.ReadU8(rec + 0x3D) * PerTile + PerTile / 2f;
            }
            if (kind == MarkerKind.Warp)
            {
                int to = m.ReadU8(rec + 0x38);
                mk.Name = to == 0xFF || to == areaIndex ? null : AreaName(to);
                mk.DestX = m.ReadU8(rec + 0x3B); mk.DestZ = m.ReadU8(rec + 0x3C);
            }
            if (kind == MarkerKind.Throne) { mk.Label = "Throne of Ichrius"; mk.Done = m.ReadU8(rec + 0x38) == 0x85; }
            if (kind is MarkerKind.Chest or MarkerKind.Barrel or MarkerKind.Remains or MarkerKind.Grave or MarkerKind.Well or MarkerKind.Hidden)
            {
                int h = m.ReadU16(rec + 0x3A);
                mk.Item = h == 0xFFFF ? -1 : PropItem(m, h);
                mk.Empty = mk.Item < 0;
            }
            if (kind == MarkerKind.Chest)
            {
                byte lk = m.ReadU8(rec + 0x38);
                mk.Done = lk == 0xFE || m.ReadU16(rec + 8) == 3;
                mk.Key = LockOf(lk);
                // +0x3C not 0xFF: trapped, fires as the lid opens (map flag 4; setting it to
                // 0xFF disarms, tests/saves/gaschest.kfstate). +0x3F picks the effect, 0x1A
                // poison gas. Barrels and graves use +0x3C for messages instead, and a
                // pillar for its key model.
                mk.Trapped = cls != 0x17 && m.ReadU8(rec + GameAddr.PropChestTrap) != 0xFF;
                mk.TrapEffect = m.ReadU8(rec + GameAddr.PropChestTrapEffect);
            }
            if (kind == MarkerKind.LockedDoor) mk.Key = LockOf(m.ReadU8(rec + 0x38));
            if (cls == 0x17) mk.Label = "Orladin's pillar";
            if (type == 0xF7) mk.Label = "Pot";
            if (type == 0xD5) mk.Label = "Bucket";
            // A class-9 container of a type nobody has identified: say so rather than
            // guess. The skeletons (0xB7/0xBA) are known; others go in the guide's labels
            // as they are identified.
            if (kind == MarkerKind.Remains && type is not (0xB7 or 0xBA)) mk.Label = "Unknown container";
            if (_labels.TryGetValue((areaIndex, type), out var label))
            {
                mk.Label = label;
                if (label == "Pot" && kind == MarkerKind.Remains) mk.Kind = MarkerKind.Barrel;
            }
            if (_hints.TryGetValue((areaIndex, i), out var hint)) mk.Hint = hint;
            if (mk.Item >= 0) mk.Name = ItemName(m, mk.Item);

            bool seen;
            if (kind is MarkerKind.Door or MarkerKind.LockedDoor or MarkerKind.SecretDoor)
            {
                float ang = (short)m.ReadU16(rec + 0x26) * MathF.PI * 2f / 4096f;
                if (cls == 0x1B) ang += MathF.PI;   // its model hangs the other way from its hinge
                mk.DirX = MathF.Cos(ang); mk.DirZ = MathF.Sin(ang);
                mk.Length = Math.Clamp((int)(short)m.ReadU16(def + 6), 256, 4096) / (float)Sample;
                // Seen anywhere along the leaf (the hinge is often the wall's far corner).
                seen = false;
                for (float t = 0f; t <= 1.001f && !seen; t += 0.25f)
                    seen = SeenAt(a, x + mk.DirX * mk.Length * t, z + mk.DirZ * mk.Length * t);
            }
            else seen = SeenAt(a, x, z);
            // Cheat markers show whether seen or not.
            if (!seen && kind is not (MarkerKind.Item or MarkerKind.Trap or MarkerKind.SecretDoor or MarkerKind.Hidden)) continue;
            list.Add(mk);
        }

        for (int i = 0; i < GameAddr.EntityCount; i++)
        {
            uint rec = GameAddr.EntityTable + (uint)(i * GameAddr.EntityStride);
            // +0 == 1 is placed (people use it); +9 == 1 is alive (2/3 dying or gone).
            bool placed = m.ReadU8(rec) == 1, alive = m.ReadU8(rec + 9) == 1;
            if (!placed && !alive) continue;
            uint def = GameAddr.CreatureDefs + (uint)m.ReadU8(rec + 2) * CreatureDefStride;
            uint script = m.ReadU32(def + 0x38);
            bool talks = script != 0 && (script & 0x1FFFFFFF) < 0x800000 && m.ReadU8(script) == 0x70;
            float x = (int)m.ReadU32(rec + 0x2C) / (float)Sample, z = (int)m.ReadU32(rec + 0x34) / (float)Sample;
            EntityProbe(m, i, rec, def, x, z);
            // Types 0x72 and 0x73: the Palace of Wind's crystal lamps, lit with Fire Ball;
            // all twelve lit brighten the palace and unlock its chests (0x73 are the two
            // different-looking ones at the back, #50 and #51). Creatures to the game,
            // loaded only near the player like monsters, but each keeps its record, so it
            // is shown wherever seen, loaded or not (tests/saves/lamps.kfstate).
            if (m.ReadU8(rec + 1) is 0x72 or 0x73)
            {
                if (placed && SeenAt(a, x, z))
                    list.Add(new Marker { Kind = MarkerKind.Lamp, X = x, Z = z, Item = -1, Key = -1, Lit = LampLit(m, rec, areaIndex) });
                continue;
            }
            if (talks)
            {
                if (!placed) continue;
                if (!SeenAt(a, x, z)) continue;
                int ctype = m.ReadU8(rec + 1);
                list.Add(new Marker
                {
                    Kind = MarkerKind.Person,
                    X = x,
                    Z = z,
                    Item = -1,
                    Key = -1,
                    Name = CreatureName(ctype, true)
                });
            }
            // Creatures that never fight: 0x6F the Statue of the Hero, 0x6E the hidden pair
            // either side of each secret wall. (Definition +5 == 0 does not tell: the
            // ghosts, type 0x43, have it too, tests/saves/ghosts.kfstate.)
            else if (alive && m.ReadU8(rec + 1) is not (0x6E or 0x6F))
            {
                // What it drops when killed, as the death code at 0x80050708 decides: byte
                // 0 == 1 drops +0x0B (0xFF none; Silviera's Key in
                // tests/saves/keydrop.kfstate); byte 0 == 0 or 4 (respawning) rolls the
                // type's item, definition +0x0C, with chance +0x0D / 256; anything else
                // drops nothing. With the contents cheat a monster carrying something for
                // certain shows even when the enemies cheat is off.
                int mode = m.ReadU8(rec), drop = -1, chance = 256;
                if (mode == 1) drop = m.ReadU8(rec + GameAddr.EntityDrop);
                else if (mode is 0 or 4) { drop = m.ReadU8(def + 0x0C); chance = m.ReadU8(def + 0x0D); }
                if (drop == 0xFF || chance == 0 || !ShowContents) drop = -1;
                if (!ShowEnemies && (drop < 0 || chance < 256)) continue;
                list.Add(new Marker
                {
                    Kind = MarkerKind.Enemy,
                    X = x,
                    Z = z,
                    Item = drop,
                    Key = -1,
                    Chance = drop >= 0 ? chance : 0,
                    Name = drop >= 0 ? ItemName(m, drop) : null,
                    Who = CreatureName(m.ReadU8(rec + 1), false)
                });
            }
        }

        ScanProbe(m, a);
        EntityProbeEnd(m, list);
        PairPortals(list);
        lock (Markers) { Markers.Clear(); Markers.AddRange(list); }
    }

    /// <summary>Numbers the portal pairs, one colour each: a pair is two arches each
    /// sending you onto the other's tile. In list order, which is the prop table's, so
    /// the numbers are the same every visit.</summary>
    static void PairPortals(List<Marker> list)
    {
        int pairs = 0;
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].Kind != MarkerKind.Portal || list[i].Pair >= 0) continue;
            int dt = (int)(list[i].DestX / PerTile), dz = (int)(list[i].DestZ / PerTile);
            int j = -1;
            for (int k = 0; k < list.Count && j < 0; k++)
                if (k != i && list[k].Kind == MarkerKind.Portal && list[k].Pair < 0
                    && (int)(list[k].X / PerTile) == dt && (int)(list[k].Z / PerTile) == dz) j = k;
            var a1 = list[i]; a1.Pair = pairs; list[i] = a1;
            if (j >= 0 && j != i) { var b1 = list[j]; b1.Pair = pairs; list[j] = b1; }
            pairs++;
        }
    }
}
