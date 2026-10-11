using RecompOne.Runtime.Context;
using RecompOne.Runtime.Hle;
using RecompOne.Runtime.Memory;

namespace KfIII;

/// <summary>
/// Widens the game's view cone to the widescreen picture, so the margins have
/// geometry in them (Visuals sets the aspect; this follows it).
///
/// <para><b>The game's cull.</b> kf_BuildTileGrid rebuilds a 25x25 byte grid of map
/// tiles once a frame (GameAddr.TileGrid; built in the scratchpad and copied out,
/// with the window's first column and row), and kf_DrawTiles draws a tile only when
/// its byte has the 0x02 bit set. For each cell the builder tests the distance from the
/// window centre and the two edges of the view cone, yaw (GameAddr.TileGridYaw)
/// +- a half-angle of 0x1B8 (about 39 degrees) plus a pitch term: 0x1E / 0x1A in the
/// cone (near / far), 8 out of it but close, 0 otherwise. The near tiles are
/// cone-tested too, which at 16:9 leaves black wedges in the bottom corners.</para>
///
/// <para><b>The widening.</b> The half-angle is computed inside the builder, so the
/// builder itself is run twice more with the yaw turned by +-(wide half-angle -
/// stock half-angle), and those grids are merged into the stock one, which is built
/// last so that everything else it leaves behind is stock. The window moves with the
/// yaw, so merged cells are aligned through each pass's own window origin. A cell only
/// ever gains a drawable value: the stock picture is always contained. Two fills
/// follow (<see cref="After"/>): out to the fog distance, and the tiles right around
/// the player.</para>
///
/// <para>KFIII_WIDECULL=0 leaves the cone at its 4:3 shape.</para>
/// </summary>
public static class WideCull
{
    const uint Yaw = GameAddr.TileGridYaw, Grid = GameAddr.TileGrid, Col0 = GameAddr.TileGridCol0, Row0 = GameAddr.TileGridRow0;
    const uint Scratch = GameAddr.TileGridScratch;
    const int N = GameAddr.TileGridSize, Cells = N * N;
    /// The builder's own half-angle, in radians.
    const double StockHalf = 0x1B8 * Math.PI * 2 / 4096;
    /// Cell values the builder gives near and far cells in the cone (both have the 0x02 "draw" bit).
    const byte NearCell = 0x1E, FarCell = 0x1A;

    static readonly bool _enabled = !DevSwitch.Is("KFIII_WIDECULL", "0");
    static readonly bool _fogFill = !DevSwitch.Is("KFIII_FOGFILL", "0");
    /// Squared radius, in cells, of the always-drawn ring around the player.
    static readonly int _nearR2 = DevSwitch.Int("KFIII_WIDECULL_NEAR", 8);
    static readonly byte[][] _side = [new byte[Cells], new byte[Cells]];
    static readonly int[] _sideCol = new int[2], _sideRow = new int[2];
    static bool _merge;

    /// Counters for the visuals probe: cells added by the turned passes and the near
    /// ring, frames widened, cells added by the fog fill.
    public static long Added, Passes, FogAdded;

    /// 12-bit angle units to turn the cone by for the current aspect, 0 at 4:3
    /// (KFIII_WIDECULL_DELTA forces a value).
    static int Delta()
    {
        if (int.TryParse(DevSwitch.Text("KFIII_WIDECULL_DELTA"), out var forced)) return forced;
        float wide = Display.WideAspect;
        if (wide <= 4f / 3f + 0.001f) return 0;
        double k = wide / (4.0 / 3.0);
        double half = Math.Atan(Math.Tan(StockHalf) * k);
        // A little slack past the screen edge, like the stock cone's own.
        return (int)Math.Ceiling((half - StockHalf) * 4096 / (Math.PI * 2)) + 16;
    }

    /// <summary>
    /// Pre-hook on libgte's polygon subdivision entries (0x80074D88, 0x80075188,
    /// 0x800756A8, 0x80075B48; kf_DrawGroundNear sends the near ground tiles through
    /// them). They split a close polygon into pieces and drop every piece lying wholly
    /// past a screen edge: |x - OFX| beyond half the width at a1+4 (and y against a1+8).
    /// The width is the game's 320, so every piece in a widescreen margin would be
    /// dropped, leaving black blocks with straight vertical edges in the bottom corners.
    /// The width is widened to cover the margins, idempotently (the caller reuses one
    /// block for many polygons).
    /// </summary>
    public static void BeforeDivide(CpuContext c, IMemory m)
    {
        int margin = Display.WideAspect > 0f ? Display.WideMargin(320) : 0;
        if (margin <= 0) return;
        uint at = c.A1 + 4;
        int w = (int)m.ReadU32(at);
        int want = 320 + 2 * margin;
        if (w > 0 && w < want) m.WriteU32(at, (uint)want);
    }

    /// <summary>Pre-hook on kf_BuildTileGrid: runs the two turned passes and keeps
    /// their grids. The stock pass then runs as the hooked call itself.</summary>
    public static bool Before(CpuContext c, IMemory m)
    {
        _merge = false;
        int d = _enabled ? Delta() : 0;
        if (d == 0) return true;

        ushort yaw = m.ReadU16(Yaw);
        var regs = c.Snapshot();
        for (int s = 0; s < 2; s++)
        {
            m.WriteU16(Yaw, (ushort)((yaw + (s == 0 ? -d : d)) & 0xFFFF));
            Recompiled.KingsFieldIII_game.kf_BuildTileGrid_Impl(c, m);
            c.Restore(regs);
            for (int i = 0; i < Cells; i++) _side[s][i] = m.ReadU8(Grid + (uint)i);
            _sideCol[s] = (int)m.ReadU32(Col0);
            _sideRow[s] = (int)m.ReadU32(Row0);
        }
        m.WriteU16(Yaw, yaw);
        _merge = true;
        Passes++;
        return true;
    }

    /// <summary>Post-hook on kf_BuildTileGrid: fills to the fog, fills the near ring,
    /// and merges the turned grids into the stock one.</summary>
    public static void After(CpuContext c, IMemory m)
    {
        if (!_merge) return;
        _merge = false;
        int col0 = (int)m.ReadU32(Col0), row0 = (int)m.ReadU32(Row0);
        FillToFog(m);
        FillNear(m);
        MergeSides(m, col0, row0);
    }

    /// <summary>
    /// Fills the wide view out to the fog distance. The builder draws only cells within
    /// a circle around the window (its radius is the byte at 0x801AEAE9), which covers
    /// the 4:3 view, while fog goes by straight-ahead view depth (GameAddr.FogStart to
    /// FogEnd, 2048 = one tile). Towards the sides of a wide picture a tile can be
    /// inside the fog's range but outside that circle, so walls would pop in at the
    /// edges while fog hid them in the middle. Cells are measured from the player's
    /// cell along the view direction; anything inside the wide cone and short of the
    /// fog's end (plus a tile) is drawn as a far cell.
    /// </summary>
    static void FillToFog(IMemory m)
    {
        int fogEnd = (int)m.ReadU32(GameAddr.FogEnd);
        double a = (m.ReadU16(Yaw) + 0x400) * Math.PI * 2 / 4096;
        double fc = Math.Cos(a), fs = Math.Sin(a);
        double pcx = ((0xC7FF - (int)Math.Round(fc * 4096) * 8) >> 12) + 0.5;
        double prx = ((0xC7FF - (int)Math.Round(fs * 4096) * 8) >> 12) + 0.5;
        double reach = fogEnd / 2048.0 + 1.0;
        double tanW = Math.Tan(StockHalf + Delta() * Math.PI * 2 / 4096);
        if (_fogFill && fogEnd > 0 && fogEnd < 0x7D00)
            for (int r = 0; r < N; r++)
                for (int col = 0; col < N; col++)
                {
                    double dx = col + 0.5 - pcx, dz = r + 0.5 - prx;
                    double fwd = dx * fc + dz * fs, side = Math.Abs(-dx * fs + dz * fc);
                    if (fwd < 0.5 || fwd > reach || side > fwd * tanW + 1.5) continue;
                    uint at = (uint)(r * N + col);
                    if ((m.ReadU8(Grid + at) & 2) != 0) continue;
                    m.WriteU8(Grid + at, FarCell);
                    m.WriteU8(Scratch + at, FarCell);
                    FogAdded++;
                }
    }

    /// <summary>
    /// The tiles right around the player, whatever the cone says. The builder
    /// cone-tests even its nearest ring, and a tile beside and slightly behind the
    /// camera (its centre outside any cone) is exactly what reaches into the bottom
    /// corners of a wide picture. The player's cell in the window is the builder's own
    /// (0xC7FF - 8 cos) >> 12, (0xC7FF - 8 sin) >> 12 of yaw + 0x400; anything behind
    /// the camera is then culled or near-clipped.
    /// </summary>
    static void FillNear(IMemory m)
    {
        double a = (m.ReadU16(Yaw) + 0x400) * Math.PI * 2 / 4096;
        int pc = (0xC7FF - (int)Math.Round(Math.Cos(a) * 4096) * 8) >> 12;
        int pr = (0xC7FF - (int)Math.Round(Math.Sin(a) * 4096) * 8) >> 12;
        for (int r = pr - 5; r <= pr + 5; r++)
            for (int col = pc - 5; col <= pc + 5; col++)
            {
                if ((uint)r >= N || (uint)col >= N) continue;
                int d2 = (r - pr) * (r - pr) + (col - pc) * (col - pc);
                if (d2 > _nearR2) continue;
                uint at = (uint)(r * N + col);
                if ((m.ReadU8(Grid + at) & 2) != 0) continue;
                m.WriteU8(Grid + at, NearCell);
                m.WriteU8(Scratch + at, NearCell);
                Added++;
            }
    }

    /// <summary>Merges the two turned passes' drawn cells into the stock grid, each
    /// aligned through its own window origin.</summary>
    static void MergeSides(IMemory m, int col0, int row0)
    {
        for (int s = 0; s < 2; s++)
        {
            int dc = _sideCol[s] - col0, dr = _sideRow[s] - row0;
            var g = _side[s];
            for (int r = 0; r < N; r++)
            {
                int fr = r + dr;
                if ((uint)fr >= N) continue;
                for (int col = 0; col < N; col++)
                {
                    int fc = col + dc;
                    if ((uint)fc >= N) continue;
                    byte v = g[r * N + col];
                    if ((v & 2) == 0) continue;
                    uint at = (uint)(fr * N + fc);
                    if ((m.ReadU8(Grid + at) & 2) != 0) continue;
                    m.WriteU8(Grid + at, v);
                    m.WriteU8(Scratch + at, v);
                    Added++;
                }
            }
        }
    }
}
