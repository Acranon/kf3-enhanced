using System.Globalization;
using System.Text;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace KfIII;

// The automap's test and research switches (docs/testing.md). Each does nothing unless
// its KFIII_* variable is set; they log to the console with tags such as [ent], [prop],
// [scan] and [watch]. Most of the map's facts about props and creatures were worked out
// with them, from save states in tests/saves.
public static partial class Automap
{
    /// <summary>Main RAM, for the searches that sweep it.</summary>
    const uint RamScanStart = 0x80010000, RamScanEnd = 0x80200000;

    static readonly bool _probe = DevSwitch.Is("KFIII_MAP_PROBE");
    static readonly bool _noRetry = DevSwitch.Is("KFIII_MAP_RETRY", "0");
    static readonly bool _tileTex = DevSwitch.Is("KFIII_TILE_TEX");
    static readonly HashSet<(int, int, int)> _tileTexSeen = new();

    // Placement tests: counted in map ticks from the first one.
    static int _testTicks;
    static readonly int _testYaw = DevSwitch.Text("KFIII_TEST_YAW") is { } ty ? Convert.ToInt32(ty, 16) : -1;
    static readonly string? _gotoEnv = DevSwitch.Text("KFIII_TEST_GOTO");
    static readonly bool _gotoEntity = _gotoEnv?.StartsWith("e") == true;
    static readonly int _testGoto = int.TryParse(_gotoEntity ? _gotoEnv![1..] : _gotoEnv, out var tg) ? tg : -1;
    static readonly (float, float)[]? _colProbe = DevSwitch.Text("KFIII_COL_PROBE")?.Split(';')
        .Select(p => p.Split(',')).Select(p => (float.Parse(p[0], CultureInfo.InvariantCulture), float.Parse(p[1], CultureInfo.InvariantCulture))).ToArray();

    // RAM watches and pokes.
    static readonly uint _watchByte = DevSwitch.Text("KFIII_WATCH_BYTE") is { } wb ? Convert.ToUInt32(wb, 16) : 0;
    static int _watchLast = -1;
    static readonly (uint Addr, int Len)? _watchRange = DevSwitch.Text("KFIII_WATCH_RANGE")?.Split(',') is { Length: 2 } wrs
        ? (Convert.ToUInt32(wrs[0], 16), Convert.ToInt32(wrs[1], 16)) : null;
    static int[]? _watchPrev;
    static int _watchTicks;
    static (double At, (uint, byte)[] Writes)? _poke = DevSwitch.Text("KFIII_TEST_POKE") is { } tp
        ? (double.Parse(tp.Split(';')[0], CultureInfo.InvariantCulture),
           tp.Split(';')[1].Split(',').Select(w => (Convert.ToUInt32(w.Split('=')[0], 16), Convert.ToByte(w.Split('=')[1], 16))).ToArray())
        : null;
    static readonly long _pokeStart = Environment.TickCount64;

    // Prop and creature dumps.
    static readonly bool _entProbe = DevSwitch.Is("KFIII_ENT_PROBE");
    static bool _entProbed;
    static readonly int _propWatch = DevSwitch.Int("KFIII_PROP_WATCH", -1);
    static string? _propWatchLast;
    static readonly bool _scan = DevSwitch.Is("KFIII_SCAN");
    static bool _scanned;
    static readonly long _scanStart = Environment.TickCount64;

    static void AppendHex(StringBuilder sb, IMemory m, uint addr, int count)
    {
        for (int k = 0; k < count; k++) sb.Append($" {m.ReadU8(addr + (uint)k):X2}");
    }

    // ---- from the tick ---------------------------------------------------------------

    /// <summary>
    /// KFIII_TEST_GOTO=n (a prop) or en (an entity): 14 ticks in, stand on open floor
    /// KFIII_TEST_GOTO_DIST world units (5120 by default, 2.5 tiles) from it, facing it.
    /// KFIII_TEST_YAW=hex: one tick later, face that yaw.
    /// </summary>
    static void TestPlacement(CpuContext c, IMemory m, Area area)
    {
        _testTicks++;
        if (_testGoto >= 0 && _testTicks == 14) GotoProp(c, m, area, _testGoto);
        if (_testYaw >= 0 && _testTicks == 15) m.WriteU16(GameAddr.Yaw, (ushort)_testYaw);
    }

    static void GotoProp(CpuContext c, IMemory m, Area a, int index)
    {
        uint rec = _gotoEntity ? GameAddr.EntityTable + (uint)(index * GameAddr.EntityStride) : GameAddr.PropTable + (uint)(index * GameAddr.PropStride);
        int ox = (int)m.ReadU32(rec + (_gotoEntity ? 0x2Cu : 0x14u)), oz = (int)m.ReadU32(rec + (_gotoEntity ? 0x34u : 0x1Cu));
        int dist = DevSwitch.Int("KFIII_TEST_GOTO_DIST", 5120);
        var regs = c.Snapshot();
        try
        {
            c.SP -= 0x100;
            // Sixteen directions round it, the first on open floor.
            for (int k = 0; k < 16; k++)
            {
                float ang = k * MathF.PI / 8f;
                int x = ox + (int)(MathF.Cos(ang) * dist), z = oz + (int)(MathF.Sin(ang) * dist);
                int sx = x / Sample, sz = z / Sample;
                if (CellAt(c, m, a, sx, sz) != Kind.Floor) continue;
                // A creature's own height: indoors the floor samples can find the roof.
                int floor = _gotoEntity ? (int)m.ReadU32(rec + 0x30) : a.Floor[sz * Size + sx] * 16;
                m.WriteU32(GameAddr.PlayerPos, (uint)x); m.WriteU32(GameAddr.PlayerPos + 4, (uint)floor); m.WriteU32(GameAddr.PlayerPos + 8, (uint)z);
                // The player faces (-sin yaw, cos yaw).
                float fx = ox - x, fz = oz - z;
                int yaw = (int)MathF.Round(MathF.Atan2(-fx, fz) * 4096f / (2f * MathF.PI)) & 0xFFF;
                m.WriteU16(GameAddr.Yaw, (ushort)yaw);
                Console.WriteLine($"[KFIII] test: at {(_gotoEntity ? "entity" : "prop")} {index} type 0x{(_gotoEntity ? m.ReadU8(rec + 1) : m.ReadU16(rec + 6)):X} from {x},{z} yaw 0x{yaw:X}");
                return;
            }
            Console.WriteLine($"[KFIII] test: no floor round prop {index}");
        }
        finally { c.Restore(regs); }
    }

    /// <summary>KFIII_COL_PROBE=x,z;x,z (samples): 20 ticks in, the collision state after
    /// a floor test at each point, once. Runs inside the tick's sampling block.</summary>
    static void CollisionProbe(CpuContext c, IMemory m)
    {
        if (_colProbe == null || _testTicks != 20) return;
        foreach (var (cx, cz) in _colProbe)
        {
            int wx = (int)(cx * Sample), wz = (int)(cz * Sample);
            int th = -(m.ReadU8(GameAddr.TileRecords + (uint)(TileRowStride * (wz / TileUnits) + TileStride * (wx / TileUnits)) + 6) << 7);
            var (f, fl, _) = TestDeath(c, m, wx, th - 64, wz);
            var sbc = new StringBuilder($"[col] {cx},{cz} f {f:X} floor {fl}:");
            for (uint k = 0; k < GameAddr.CollisionStateEnd - GameAddr.CollisionState; k += 4) sbc.Append($" {m.ReadU32(GameAddr.CollisionState + k):X8}");
            Console.WriteLine(sbc.ToString());
        }
    }

    /// <summary>KFIII_MAP_DUMP=1: with the reveal cheat, once every tile is sampled, each
    /// sample's shown kind (u8) and floor (s16) to automap_dump.bin, for offline checks.</summary>
    static void DumpMap(Area area)
    {
        if (DevSwitch.Is("KFIII_MAP_DUMP"))
            using (var bw = new BinaryWriter(File.Create("automap_dump.bin")))
                for (int i = 0; i < Size * Size; i++) { bw.Write((byte)area.Shown(i)); bw.Write(area.Floor[i]); }
    }

    /// <summary>KFIII_MAP_PROBE=1: an ASCII map round the player 45 ticks in, and the
    /// player's position and the water tile counts every 15 ticks.</summary>
    static void MapProbe(Area area, int rx, int rz, int px, int pz, int yaw)
    {
        if (_probe && _ticks == 45) PrintMap(area, rx, rz);
        if (_probe && _ticks % 15 == 0)
            Console.WriteLine($"[KFIII] map player {px},{pz} yaw 0x{yaw:X3} water tiles {area.WaterTile.Count(w => w)} checked {area.WaterChecked.Count(w => w)}");
    }

    static void PrintMap(Area a, int rx, int rz)
    {
        Console.WriteLine($"[KFIII] map probe at sample {rx},{rz}; seen {a.X0}..{a.X1} x {a.Z0}..{a.Z1}");
        for (int z = rz + 40; z >= rz - 40; z -= 2)
        {
            var line = new StringBuilder();
            for (int x = rx - 60; x <= rx + 60; x++)
            {
                if ((uint)x >= Size || (uint)z >= Size) { line.Append(' '); continue; }
                int i = z * Size + x;
                line.Append(x == rx && Math.Abs(z - rz) <= 1 ? '@' : !a.Seen[i] ? ' ' : a.Cells[i] switch
                {
                    Kind.Floor => '.',
                    Kind.Solid => '#',
                    Kind.Chasm => 'X',
                    Kind.Water => '~',
                    Kind.Lava => '^',
                    Kind.Outside => ' ',
                    _ => '?',
                });
            }
            Console.WriteLine("[KFIII] map " + line);
        }
    }

    /// <summary>KFIII_TILE_TEX=1: each tile's textures, once each (from AfterDrawTile).</summary>
    static void TileTextureProbe(IMemory m, int t, int clut, int tpage, byte code, int len)
    {
        if (_tileTex && _tileTexSeen.Add((t, clut, tpage)))
            Console.WriteLine($"[tiletex] t {t % Span},{t / Span} model {m.ReadU8(GameAddr.TileRecords + (uint)t * TileStride + 5):X2} clut {clut:X4} tpage {tpage:X4} code {code:X2} len {len}");
    }

    // ---- from CollectMarkers ---------------------------------------------------------

    /// <summary>
    /// Each tick:
    /// KFIII_WATCH_BYTE=addr (hex): log that byte whenever it changes.
    /// KFIII_WATCH_RANGE=addr,len (hex): log every halfword there that changes.
    /// KFIII_TEST_POKE=secs;addr=byte,addr=byte (hex): write those bytes once, that many
    /// seconds after start.
    /// </summary>
    static void WatchProbes(IMemory m)
    {
        if (_watchByte != 0)
        {
            int wv = m.ReadU8(_watchByte);
            if (wv != _watchLast) { _watchLast = wv; Console.WriteLine($"[watch] {Environment.TickCount64 % 1000000} {_watchByte:X8} = {wv:X2}"); }
        }
        if (_watchRange is { } wr)
        {
            _watchPrev ??= new int[wr.Len / 2];
            var sbw = new StringBuilder();
            for (int k = 0; k < wr.Len / 2; k++)
            {
                int v = m.ReadU16(wr.Addr + (uint)k * 2);
                if (_watchTicks > 0 && v != _watchPrev[k]) sbw.Append($" {wr.Addr + k * 2:X}:{_watchPrev[k]}->{v}");
                _watchPrev[k] = v;
            }
            if (sbw.Length > 0) Console.WriteLine($"[range] t{_watchTicks} ms{Environment.TickCount64 % 1000000}{sbw}");
            _watchTicks++;
        }
        if (_poke != null && Environment.TickCount64 - _pokeStart > _poke.Value.At * 1000)
        {
            foreach (var (addr, v) in _poke.Value.Writes) { m.WriteU8(addr, v); Console.WriteLine($"[KFIII] test: poke {addr:X8} = {v:X2}"); }
            _poke = null;
        }
    }

    /// <summary>
    /// For every live prop, before it is classed. With KFIII_ENT_PROBE=1, once: the record
    /// of each prop within 60 samples of the player (KFIII_PROP_ALL=1) or of one class
    /// (KFIII_PROP_CLASS=hex, two digits). KFIII_PROP_WATCH=n: prop n's record whenever it
    /// changes.
    /// </summary>
    static void PropRecordProbes(IMemory m, int i, uint rec, int type, byte cls, float x, float z)
    {
        if (_entProbe && !_entProbed && ((DevSwitch.Is("KFIII_PROP_ALL")
            && Math.Abs(x - PlayerX) < 60 && Math.Abs(z - PlayerZ) < 60)
            || DevSwitch.Is("KFIII_PROP_CLASS", $"{cls:X2}")))
        {
            var sb = new StringBuilder($"[all] #{i} type {type:X3} cls {cls:X2} at {x:F1},{z:F1} rec:");
            AppendHex(sb, m, rec, GameAddr.PropStride);
            Console.WriteLine(sb.ToString());
        }
        if (_propWatch == i)
        {
            var sb = new StringBuilder();
            AppendHex(sb, m, rec, GameAddr.PropStride);
            string now = sb.ToString();
            if (now != _propWatchLast) { _propWatchLast = now; Console.WriteLine($"[watch] {Environment.TickCount64 % 1000000} #{i}:{now}"); }
        }
    }

    /// <summary>KFIII_ENT_PROBE=1, once: the record and definition of every container and door.</summary>
    static void PropProbe(IMemory m, int i, MarkerKind kind, int type, byte cls, float x, float z, uint rec, uint def)
    {
        if (_entProbe && !_entProbed && kind is MarkerKind.Chest or MarkerKind.Barrel or MarkerKind.Remains or MarkerKind.Grave
                or MarkerKind.Door or MarkerKind.LockedDoor or MarkerKind.SecretDoor)
        {
            var sb = new StringBuilder($"[prop] #{i} {kind} type {type:X2} cls {cls:X2} at {x:F1},{z:F1} rec:");
            AppendHex(sb, m, rec, GameAddr.PropStride);
            sb.Append(" def:");
            AppendHex(sb, m, def, PropDefStride);
            Console.WriteLine(sb.ToString());
        }
    }

    /// <summary>KFIII_ENT_PROBE=1, once: every live or placed creature's record, its drop
    /// and its definition.</summary>
    static void EntityProbe(IMemory m, int i, uint rec, uint def, float x, float z)
    {
        if (!_entProbe || _entProbed) return;
        int drop = m.ReadU8(rec + GameAddr.EntityDrop);
        var sb = new StringBuilder($"[ent] #{i} at {x:F1},{z:F1} type {m.ReadU8(rec + 1):X2} +0B {drop:X2} {(drop != 0xFF ? ItemName(m, drop) : "")} rec:");
        AppendHex(sb, m, rec, 0x40);
        sb.Append(" def:");
        AppendHex(sb, m, def, CreatureDefStride);
        Console.WriteLine(sb.ToString());
    }

    /// <summary>
    /// KFIII_SCAN=1: 7 s after start, once, every hidden item, unidentified container and
    /// unopened chest in the area with what it holds: a checklist for backtracking.
    /// </summary>
    static void ScanProbe(IMemory m, Area a)
    {
        if (!_scan || _scanned || Environment.TickCount64 - _scanStart <= 7000) return;
        _scanned = true;
        int area = AreaIndex(a.Name);
        Console.WriteLine($"[scan] area {area} {AreaName(area)}");
        for (int i = 0; i < GameAddr.PropCount; i++)
        {
            uint rec = GameAddr.PropTable + (uint)(i * GameAddr.PropStride);
            int type = m.ReadU16(rec + 6);
            if (type == 0xFF) continue;
            byte cls = m.ReadU8(GameAddr.PropDefs + (uint)type * PropDefStride);
            float x = (int)m.ReadU32(rec + 0x14) / (float)Sample, z = (int)m.ReadU32(rec + 0x1C) / (float)Sample;
            int h = m.ReadU16(rec + 0x3A);
            string holds = h == 0xFFFF ? "(empty)" : ItemName(m, PropItem(m, h));
            if (cls == 0x15) Console.WriteLine($"[scan] hidden #{i} at {x:F0},{z:F0}: {holds}");
            else if (cls == 9 && (m.ReadU8(rec) & 2) != 0 && type is not (0xB7 or 0xBA or 0xB8 or 0xD4 or 0xD5 or 0xF5 or 0xF7)
                     && m.ReadU8(GameAddr.PropDefs + (uint)type * PropDefStride + 3) != 4 && !_labels.ContainsKey((area, type)))
                Console.WriteLine($"[scan] unknown type {type:X3} #{i} at {x:F0},{z:F0}: {holds}");
            else if (cls is 6 or 7 && m.ReadU8(rec + 0x38) != 0xFE && m.ReadU16(rec + 8) != 3 && h != 0xFFFF)
                Console.WriteLine($"[scan] chest #{i} at {x:F0},{z:F0}: {holds}");
        }
    }

    /// <summary>
    /// KFIII_ENT_PROBE=1, at the end of the first pass, once: the enemy markers, the
    /// player's position and every item name, and with it:
    /// KFIII_BYTES_NEAR=aabb,ccdd: places in RAM where the two byte pairs lie within 24
    /// bytes of each other;
    /// KFIII_HEX=addr,len (hex): a hex dump;
    /// KFIII_TILE_DUMP=x0,x1,z0,z1: the tile records there;
    /// KFIII_FLAG_FIND=ff (hex): conversation "set flag" commands (F7 flag value) for that
    /// flag, each with the creature type whose script starts nearest before it.
    /// </summary>
    static void EntityProbeEnd(IMemory m, List<Marker> list)
    {
        if (!_entProbe || _entProbed) return;
        _entProbed = true;
        foreach (var mk in list.Where(k => k.Kind == MarkerKind.Enemy)) Console.WriteLine($"[ent] enemy marker at {mk.X:F1},{mk.Z:F1}");
        if (DevSwitch.Text("KFIII_BYTES_NEAR")?.Split(',') is { Length: 2 } bn)
        {
            byte a0 = Convert.ToByte(bn[0][..2], 16), a1 = Convert.ToByte(bn[0][2..], 16);
            byte b0 = Convert.ToByte(bn[1][..2], 16), b1 = Convert.ToByte(bn[1][2..], 16);
            for (uint ad = RamScanStart; ad < RamScanEnd; ad++)
            {
                if (m.ReadU8(ad) != a0 || m.ReadU8(ad + 1) != a1) continue;
                for (int d = -24; d <= 24; d++)
                {
                    uint q = (uint)(ad + d);
                    if (m.ReadU8(q) == b0 && m.ReadU8(q + 1) == b1)
                    {
                        var sb = new StringBuilder($"[near] {ad:X8} (+{d}):");
                        AppendHex(sb, m, ad - 16, 48);
                        Console.WriteLine(sb.ToString());
                    }
                }
            }
        }
        if (DevSwitch.Text("KFIII_HEX")?.Split(',') is { Length: 2 } hx)
        {
            uint h0 = Convert.ToUInt32(hx[0], 16); int hl = Convert.ToInt32(hx[1], 16);
            for (int r = 0; r < hl; r += 16)
            {
                var sb = new StringBuilder($"[hex] {h0 + r:X8}:");
                AppendHex(sb, m, h0 + (uint)r, 16);
                Console.WriteLine(sb.ToString());
            }
        }
        if (DevSwitch.Text("KFIII_TILE_DUMP")?.Split(',').Select(int.Parse).ToArray() is { Length: 4 } td)
            for (int tz = td[2]; tz <= td[3]; tz++)
            {
                var sb = new StringBuilder($"[tile] z{tz,2}:");
                for (int tx = td[0]; tx <= td[1]; tx++)
                {
                    sb.Append($"  x{tx}=");
                    for (int k = 0; k < TileStride; k++) sb.Append($"{m.ReadU8(GameAddr.TileRecords + (uint)(TileRowStride * tz + TileStride * tx + k)):X2}");
                }
                Console.WriteLine(sb.ToString());
            }
        Console.WriteLine($"[ent] player {PlayerX:F1},{PlayerZ:F1}");
        if (DevSwitch.Text("KFIII_FLAG_FIND") is { } ff)
        {
            byte fl = Convert.ToByte(ff, 16);
            var starts = new List<(uint At, int Type)>();
            for (int t = 0; t < 0x80; t++)
            {
                uint s = m.ReadU32(GameAddr.CreatureDefs + (uint)t * CreatureDefStride + 0x38);
                if (s != 0 && (s & 0x1FFFFFFF) < 0x800000) starts.Add((s, t));
            }
            for (uint ad = RamScanStart; ad < RamScanEnd; ad++)
                if (m.ReadU8(ad) == 0xF7 && m.ReadU8(ad + 1) == fl)
                {
                    var owner = starts.Where(st => st.At <= ad).OrderByDescending(st => st.At).FirstOrDefault();
                    Console.WriteLine($"[flag] F7 {fl:X2} {m.ReadU8(ad + 2):X2} at {ad:X8}; script of creature type {owner.Type:X2} starts {owner.At:X8}");
                }
        }
        var names = new StringBuilder("[items]");
        for (int id = 0; id < 0x100; id++) names.Append($" {id:X2}={ItemName(m, id)};");
        Console.WriteLine(names.ToString());
    }
}
