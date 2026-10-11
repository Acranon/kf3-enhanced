using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace KfIII;

// Water and lava. Whether a sample is a liquid comes from the game's death heights
// (Automap.Scan.cs, TestDeath); which liquid it is comes from the texture its tile draws
// with. kf_DrawTile is hooked on both sides, and the primitive packets it wrote are
// searched for the river's animated texture page: palette 0x79CC is water, 0x7D00 lava.
// A hit marks every tile of the same model, as a tile drawn only near the player never
// shows its texture here (libgte's subdivision writes its packets elsewhere).
public static partial class Automap
{
    /// <summary>
    /// Liquid is taken from the tiles only once their records have stayed the same for
    /// this long (KFIII_TILES_SETTLE_MS, 0 by default). While an area loads, the map can
    /// already be on the new area with the old area's tiles still in RAM, and one hit
    /// spread by model number would copy the old area's river into the new one (Quist's
    /// into the Palace of Wind, tests/saves/palace_water.kfstate). Hits before then are
    /// kept with the tiles' fingerprint and used only if it still matches: far tiles are
    /// looked at mostly as they come into view, so dropping those would miss most of a
    /// river.
    /// </summary>
    static readonly long _tilesSettleMs = DevSwitch.Int("KFIII_TILES_SETTLE_MS", 0);
    static ulong _tilesSum;
    static long _tilesSince;
    static readonly List<(int T, bool Lava, ulong Sum)> _pendingHits = new();
    static readonly HashSet<(Area, byte)> _lavaModels = new();

    /// <summary>The tile kf_DrawTile is drawing (-1 none to look at), and where its packets start.</summary>
    static int _drawTile = -1;
    static uint _drawStart;

    /// <summary>Whether any tile of the current area has drawn lava (the legend lists it).</summary>
    public static bool HasLava => Current is { } la && Array.IndexOf(la.LavaTile, true) >= 0;

    static bool TilesSettled => Environment.TickCount64 - _tilesSince >= _tilesSettleMs;

    /// <summary>From the tick: a fingerprint (FNV-1a) of the tile records; when it
    /// changes, the settling time starts again.</summary>
    static void CheckTiles(IMemory m)
    {
        ulong h = 1469598103934665603UL;
        for (uint o = 0; o < Span * Span * TileStride; o += 4) h = (h ^ m.ReadU32(GameAddr.TileRecords + o)) * 1099511628211UL;
        if (h != _tilesSum)
        {
            _tilesSum = h; _tilesSince = Environment.TickCount64;
        }
    }

    /// <summary>From the tick: hits seen while the tiles were settling, applied once
    /// they have, and only those taken from the tiles as they are now.</summary>
    static void ApplyPendingHits(IMemory m, Area a)
    {
        if (_pendingHits.Count == 0 || !TilesSettled) return;
        foreach (var (t, lava, sum) in _pendingHits)
            if (sum == _tilesSum) LiquidHit(m, a, t, lava);
        _pendingHits.Clear();
    }

    /// <summary>Before kf_DrawTile (a0 = the tile record): note the tile and the
    /// primitive write pointer, if the tile still needs looking at.</summary>
    public static void BeforeDrawTile(CpuContext c, IMemory m)
    {
        _drawTile = -1;
        if (!Enabled || Current is not { } a) return;
        uint rec = c.A0 & 0x1FFFFFFF, b = GameAddr.TileRecords & 0x1FFFFFFF;
        if (rec < b || rec >= b + Span * Span * TileStride) return;
        int t = (int)((rec - b) / TileStride);
        if (a.WaterChecked[t] && !_tileTex) return;
        _drawTile = t;
        _drawStart = m.ReadU32(GameAddr.ScratchPrimCur);
    }

    /// <summary>After kf_DrawTile: look through the packets it wrote for water or lava.</summary>
    public static void AfterDrawTile(CpuContext c, IMemory m)
    {
        if (_drawTile < 0 || Current is not { } a) return;
        int t = _drawTile; _drawTile = -1;
        uint end = m.ReadU32(GameAddr.ScratchPrimCur);
        // The tile is done once a textured polygon of it has been looked at: near tiles
        // go through libgte's subdivision and write elsewhere, so a tile only ever drawn
        // near is looked at again each time until it is drawn far.
        bool sawTextured = false;
        // Packets lie one after another: a tag word (length in words in the top byte),
        // then the command word. A textured polygon's CLUT is at +0xE and its texture
        // page at +0x16 (flat) or +0x1A (gouraud).
        for (uint p = _drawStart; p < end && end - _drawStart < 0x10000;)
        {
            uint tag = m.ReadU32(p);
            int len = (int)(tag >> 24);
            if (len == 0) break;
            byte code = m.ReadU8(p + 7);
            if ((code & 0xE4) == 0x24)   // textured polygon
            {
                sawTextured = true;
                int clut = m.ReadU16(p + 0xE);
                int tpage = m.ReadU16((code & 0x10) != 0 ? p + 0x1A : p + 0x16);
                TileTextureProbe(m, t, clut, tpage, code, len);
                bool lava = IsLava(clut, tpage);
                if (lava || RecompOne.Runtime.WaterUv.IsWater(clut, tpage))
                {
                    if (!TilesSettled)
                    {
                        if (_pendingHits.Count < 4096) _pendingHits.Add((t, lava, _tilesSum));
                        return;
                    }
                    LiquidHit(m, a, t, lava);
                    break;
                }
            }
            p += 4u * (uint)(len + 1);
        }
        if ((sawTextured && TilesSettled) || a.WaterTile[t]) a.WaterChecked[t] = true;
    }

    /// <summary>Lava's texture (Ralugo, Cave of Shudom): the water's animated page
    /// (tpage &amp; 0x1F == 0x0F) with palette 0x7D00, drawn additive in Ralugo and
    /// averaged in the cave. The river is palette 0x79CC on the same page.</summary>
    static bool IsLava(int clut, int tpage) => clut == 0x7D00 && (tpage & 0x1F) == 0x0F;

    /// <summary>Tile t drew water or lava: mark it, and every tile of the same model.</summary>
    static void LiquidHit(IMemory m, Area a, int t, bool lava)
    {
        byte model = m.ReadU8(GameAddr.TileRecords + (uint)t * TileStride + 5);
        if (lava)
        {
            if (!a.LavaTile[t]) { a.LavaTile[t] = true; MarkWater(a, t); }
            PropagateLava(m, a, model);
        }
        else
        {
            if (!a.WaterTile[t]) { a.WaterTile[t] = true; MarkWater(a, t); }
            if (a.WaterModels.Add(model)) PropagateWater(m, a, model);
        }
        a.WaterChecked[t] = true;
    }

    static void PropagateLava(IMemory m, Area a, byte model)
    {
        if (!_lavaModels.Add((a, model))) return;
        for (int t = 0; t < Span * Span; t++)
            if (!a.LavaTile[t] && m.ReadU8(GameAddr.TileRecords + (uint)t * TileStride + 5) == model)
            {
                a.LavaTile[t] = a.WaterChecked[t] = true;
                MarkWater(a, t);
            }
    }

    static void PropagateWater(IMemory m, Area a, byte model)
    {
        for (int t = 0; t < Span * Span; t++)
            if (!a.WaterTile[t] && m.ReadU8(GameAddr.TileRecords + (uint)t * TileStride + 5) == model)
            {
                a.WaterTile[t] = a.WaterChecked[t] = true;
                MarkWater(a, t);
            }
    }

    /// <summary>Which liquid a deadly sample is: what its tile drew, else the area's
    /// (lava if any tile drew lava, else water). The texture only colours it; whether it
    /// is a liquid at all is the game's death height. Palette 0x7D00 alone is not lava:
    /// the Cave of Shudom has tiles of it by its save point with no death height
    /// (tests/saves/false_lava.kfstate).</summary>
    static Kind LiquidKind(Area a, int t) => a.LavaTile[t] ? Kind.Lava : a.WaterTile[t] ? Kind.Water : a.AnyLava ? Kind.Lava : Kind.Water;

    /// <summary>A tile's texture said water or lava: colour its deadly samples (and, when
    /// lava is new to the area, every deadly sample in the area).</summary>
    static void MarkWater(Area a, int t)
    {
        bool newLava = a.LavaTile[t] && !a.AnyLava;
        if (a.LavaTile[t]) a.AnyLava = true;
        if (newLava)
        {
            for (int i = 0; i < Size * Size; i++)
                if (a.Deadly[i] && !a.Walk[i]) { int ti = (i / Size / PerTile) * Span + (i % Size) / PerTile; a.Cells[i] = LiquidKind(a, ti); }
            a.Version++;
            return;
        }
        if (!a.Sampled[t]) return;
        int tx = t % Span, tz = t / Span;
        for (int sz = 0; sz < PerTile; sz++)
            for (int sx = 0; sx < PerTile; sx++)
            {
                int i = (tz * PerTile + sz) * Size + tx * PerTile + sx;
                if (a.Deadly[i] && !a.Walk[i]) a.Cells[i] = LiquidKind(a, t);
            }
        a.Version++;
    }
}
