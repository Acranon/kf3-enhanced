using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using Game = Recompiled.KingsFieldIII_game;

namespace KfIII;

// The floor plan, and what of it the player has seen.
//
// A tile's walls are not the tile: its collision shape (GameAddr.CollisionShapes) is a
// short list of commands -- 0x10 a floor at a height, 0x20..0x25 solid half-planes over
// a height range, 0x30 and up sloped floors, and more -- that the game's collision test
// runs. Rather than re-implement them, the map asks the game: kf_TerrainCollide, the
// terrain half of the player's own collision, run at 8 x 8 points a tile, just above the
// tile's floor, with a small radius. It returns 0 for free (and leaves the floor's Y in
// GameAddr.CollisionFloor), anything else for solid. The whole map is about 100,000
// samples in 75 ms, so a tile is sampled the first time it is needed.
//
// Kinds: floor; solid; outside (no tile); chasm (a floor 16000 or more below the tile's
// own height -- shape 0 is "floor at 32000"); water and lava (below a death height,
// coloured by Automap.Liquids.cs).
//
// What has been seen: every few ticks rays go out from the player across the samples,
// out to RevealTiles tiles; a ray stops at the first solid sample, which is revealed too.
public static partial class Automap
{
    const int RevealTiles = 10;

    /// <summary>
    /// The death heights kf_TerrainCollide leaves for the spot (GameAddr.DeathHeights,
    /// three s32, NoPlane when none). kf_DeathPlaneCheck kills the player each tick they
    /// are below one: they are the surfaces of lava and water, both of which kill at once.
    /// One above the floor means a liquid there; a bridge deck has none, or one below it.
    /// Measured: Ralugo's lava floor -8448, surface -10240, its bridge -13184 over
    /// -11776; Varde's river -9600 / -12160; the Cave of Shudom's lava -512 / -2560
    /// (tests/saves/lava, water_bridge, cave_lava).
    /// </summary>
    const int NoPlane = 100000;

    /// <summary>Samples one tile, once: each of its 8 x 8 samples gets a kind and a floor.</summary>
    static void SampleTile(CpuContext c, IMemory m, Area a, int tx, int tz)
    {
        if ((uint)tx >= Span || (uint)tz >= Span) return;
        int t = tz * Span + tx;
        if (a.Sampled[t]) return;
        a.Sampled[t] = true;
        uint rec = GameAddr.TileRecords + (uint)(TileRowStride * tz + TileStride * tx);
        // No model in either half and no collision shape: outside the area.
        bool none = m.ReadU8(rec + 5) == 0xFF && m.ReadU8(rec + 8) == 0xFF && m.ReadU8(rec) == 0xFF;
        int h = m.ReadU8(rec + 6), tileY = -(h << 7);
        for (int sz = 0; sz < PerTile; sz++)
            for (int sx = 0; sx < PerTile; sx++)
            {
                int i = (tz * PerTile + sz) * Size + tx * PerTile + sx;
                if (none) { a.Cells[i] = Kind.Outside; continue; }
                int wx = tx * TileUnits + sx * Sample + Sample / 2, wz = tz * TileUnits + sz * Sample + Sample / 2;
                // Just above the tile's floor. Where that is inside the ground (a ramp or
                // a raised doorway rises past the tile's height), again from 640 higher,
                // kept only if the floor it lands on is below that start.
                var (f, floor, death) = TestDeath(c, m, wx, tileY - 64, wz);
                if (f != 0 && !_noRetry)
                {
                    var (f2, floor2, death2) = TestDeath(c, m, wx, tileY - 640, wz);
                    if (f2 == 0 && floor2 > tileY - 640) (f, floor, death) = (f2, floor2, death2);
                }
                a.Deadly[i] = false;
                if (f != 0) a.Cells[i] = Kind.Solid;
                else
                {
                    a.Deadly[i] = death != NoPlane && death < floor - 256;
                    a.Cells[i] = a.Deadly[i] ? LiquidKind(a, t) : floor > tileY + 16000 ? Kind.Chasm : Kind.Floor;
                    a.Floor[i] = (short)Math.Clamp(floor / 16, short.MinValue, short.MaxValue);
                }
            }
        UpdateClosed(a, tx * PerTile - PerTile, tz * PerTile - PerTile, tx * PerTile + 2 * PerTile - 1, tz * PerTile + 2 * PerTile - 1);
        a.Version++;
    }

    /// <summary>
    /// kf_TerrainCollide at (x, y, z) with radius 0x80 and height 0x600: its result
    /// (0 = free), the floor Y it found, and the highest death height there (NoPlane if
    /// none). The caller has made stack room and saves the collision state.
    /// </summary>
    static (uint Flags, int Floor, int Death) TestDeath(CpuContext c, IMemory m, int x, int y, int z)
    {
        uint sp = c.SP;
        m.WriteU32(sp + 0x10, 0x600);
        c.A0 = (uint)x; c.A1 = (uint)y; c.A2 = (uint)z; c.A3 = 0x80;
        Game.kf_TerrainCollide(c, m);
        uint f = c.V0;
        int floor = (int)m.ReadU32(GameAddr.CollisionFloor);
        int death = NoPlane;
        for (uint k = 0; k < 3; k++) { int p = (int)m.ReadU32(GameAddr.DeathHeights + k * 4); if (p != NoPlane) death = Math.Min(death, p); }
        c.SP = sp;
        return (f, floor, death);
    }

    // ---- room for the player --------------------------------------------------------
    //
    // The player is a cylinder of radius 0x320 (kf_PlayerVertical, kf_Collide): 800 world
    // units, over six samples across. Walls are often built of pieces with gaps between
    // them that only a sample-sized probe fits through: a house's inner wall reads as a
    // row of posts, and the reveal rays would see through it. So the map shows the space
    // the player's body can be in: the free samples eroded by the player's radius less
    // the probe's (672, a disc of 2.5 samples), then dilated back. Narrower gaps close; a
    // doorway (a tile, about 7 free samples) stays open.

    static readonly (int Dx, int Dz)[] _disc = BuildDisc();

    static (int, int)[] BuildDisc()
    {
        var l = new List<(int, int)>();
        for (int dz = -2; dz <= 2; dz++)
            for (int dx = -2; dx <= 2; dx++)
                if (dx * dx + dz * dz <= 6) l.Add((dx, dz));
        return l.ToArray();
    }

    /// <summary>Whether a sample could hold the player: anything not solid or outside,
    /// including a sample not yet tested.</summary>
    static bool Free(Area a, int x, int z)
    {
        if ((uint)x >= Size || (uint)z >= Size) return false;
        var k = a.Cells[z * Size + x];
        return k is Kind.Floor or Kind.Water or Kind.Lava or Kind.Chasm or Kind.Unknown;
    }

    /// <summary>Recomputes Area.Closed over samples x0..x1, z0..z1.</summary>
    static void UpdateClosed(Area a, int x0, int z0, int x1, int z1)
    {
        x0 = Math.Max(x0, 0); z0 = Math.Max(z0, 0); x1 = Math.Min(x1, Size - 1); z1 = Math.Min(z1, Size - 1);
        int w = x1 - x0 + 5, h = z1 - z0 + 5;
        // Where the player's centre fits, over the region and 2 samples beyond.
        var fits = new bool[w * h];
        for (int z = 0; z < h; z++)
            for (int x = 0; x < w; x++)
            {
                int gx = x0 - 2 + x, gz = z0 - 2 + z;
                bool ok = true;
                foreach (var (dx, dz) in _disc) if (!Free(a, gx + dx, gz + dz)) { ok = false; break; }
                fits[z * w + x] = ok;
            }
        // A free sample is closed when no centre that fits is within the disc of it.
        for (int z = z0; z <= z1; z++)
            for (int x = x0; x <= x1; x++)
            {
                int i = z * Size + x;
                bool closed = false;
                if (Free(a, x, z) && a.Cells[i] != Kind.Unknown && !a.Walk[i])
                {
                    closed = true;
                    foreach (var (dx, dz) in _disc)
                    {
                        int fx = x - x0 + 2 + dx, fz = z - z0 + 2 + dz;
                        if ((uint)fx < (uint)w && (uint)fz < (uint)h && fits[fz * w + fx]) { closed = false; break; }
                    }
                }
                a.Closed[i] = closed;
            }
    }

    /// <summary>Where the player stands is floor at the player's height, whatever the
    /// samples said: a bridge deck over the river is sampled as the river below it.</summary>
    static void Walked(Area a, int px, int py, int pz)
    {
        int cx = px / Sample, cz = pz / Sample;
        for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int x = cx + dx, z = cz + dz;
                if ((uint)x >= Size || (uint)z >= Size) continue;
                int i = z * Size + x;
                if (a.Cells[i] == Kind.Solid || a.Cells[i] == Kind.Outside || a.Cells[i] == Kind.Unknown) continue;
                short f16 = (short)Math.Clamp(py / 16, short.MinValue, short.MaxValue);
                if (a.Walk[i] && a.Floor[i] == f16) continue;
                a.Walk[i] = true;
                a.Closed[i] = false;
                a.Cells[i] = Kind.Floor;
                a.Floor[i] = f16;
                a.Version++; a.Dirty = true;
            }
    }

    /// <summary>What the map shows at a sample, sampling its tile first if need be
    /// (Teleport uses it to find open floor).</summary>
    internal static Kind CellAt(CpuContext c, IMemory m, Area a, int x, int z)
    {
        if ((uint)x >= Size || (uint)z >= Size) return Kind.Outside;
        int t = (z / PerTile) * Span + x / PerTile;
        if (!a.Sampled[t]) SampleTile(c, m, a, x / PerTile, z / PerTile);
        return a.Shown(z * Size + x);
    }

    /// <summary>Rays from the player across the samples; each stops at the first
    /// solid or outside sample, revealing a solid one.</summary>
    static void Reveal(CpuContext c, IMemory m, Area a, float ox, float oz)
    {
        const int Rays = 720;
        float reach = RevealTiles * PerTile;
        for (int r = 0; r < Rays; r++)
        {
            float ang = r * MathF.PI * 2f / Rays;
            float dx = MathF.Cos(ang), dz = MathF.Sin(ang);
            // Grid traversal: every sample the ray crosses, so it cannot slip between
            // two solid samples that only touch at a corner.
            int x = (int)MathF.Floor(ox), z = (int)MathF.Floor(oz);
            int stepX = dx >= 0 ? 1 : -1, stepZ = dz >= 0 ? 1 : -1;
            float tdx = dx == 0 ? float.MaxValue : MathF.Abs(1f / dx), tdz = dz == 0 ? float.MaxValue : MathF.Abs(1f / dz);
            float tx = dx == 0 ? float.MaxValue : ((dx > 0 ? x + 1 - ox : ox - x) * tdx);
            float tz = dz == 0 ? float.MaxValue : ((dz > 0 ? z + 1 - oz : oz - z) * tdz);
            while (MathF.Min(tx, tz) < reach)
            {
                var k = CellAt(c, m, a, x, z);
                if (k == Kind.Outside) break;
                a.Reveal(z * Size + x);
                if (k == Kind.Solid) break;
                if (tx < tz) { x += stepX; tx += tdx; } else { z += stepZ; tz += tdz; }
            }
        }
    }
}
