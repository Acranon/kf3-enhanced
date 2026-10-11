namespace RecompOne.Runtime;

/// <summary>
/// World-aligned water. A game that maps its water texture per face, each face
/// turned its own way (King's Field III: the river's faces come from shared tile
/// meshes), shows a seam along every edge, and a texture the game scrolls to make
/// the water flow then flows a different way on every face. With this on, a water
/// polygon's texture coordinates are taken from its corners' world X/Z instead, so
/// the texture runs on across the edges, and a texture window wraps it inside the
/// water's own texel square.
///
/// The port supplies the camera (<see cref="SetCamera"/>, the GTE rotation the
/// scene is drawn with and the camera's world position) and what water is
/// (<see cref="Clut"/>, <see cref="TexPage"/>, the square at <see cref="U0"/>/
/// <see cref="V0"/> of <see cref="Size"/> texels, <see cref="Period"/> world units
/// per repeat). Corners need a view-space position from the vertex map; a polygon
/// with one missing keeps the game's own mapping.
///
/// <see cref="Flow"/> picks which world axis the texture's V runs along (its scroll
/// direction): 0 per face, the axis nearest the game's own V on that face, sign
/// included, so the water still flows the way each face was authored; 1 world Z;
/// 2 world X.
/// </summary>
public static class WaterUv
{
    public static bool Enabled;
    public static int Clut = -1, TexPage = -1;
    public static int U0, V0, Size = 32;
    public static float Period = 2048f;
    public static int Flow;

    static float _r0, _r2, _r3, _r5, _r6, _r8, _cx, _cz;
    static bool _camera;

    /// <summary>Faces remapped, faces left alone for want of a view position, and
    /// the orientations chosen (+Z, -Z, +X, -X), for a probe.</summary>
    public static long Mapped, NoView;
    public static readonly long[] Orient = new long[4];

    /// <summary>Probe: the world corners of the faces mapped since the last take,
    /// rounded, so a port can check they hold still while the camera turns.</summary>
    public static bool Record;
    public static int DebugN;
    public static int Frame;
    public static float CamX => _cx;
    public static float CamZ => _cz;
    public static readonly HashSet<(int, int)> Corners = [];

    /// <param name="r">GTE rotation, world to view, row-major, 4096 = 1.</param>
    public static void SetCamera(ReadOnlySpan<short> r, float camX, float camZ)
    {
        // The true inverse, not the transpose: at 12 fractional bits the matrix is
        // not quite orthonormal, and its transpose put a corner 25000 units away
        // ~20 units off, which moved as the view turned (the water crept).
        double a = r[0], b = r[1], c = r[2], d = r[3], e = r[4], f = r[5], g = r[6], h = r[7], k = r[8];
        double det = a * (e * k - f * h) - b * (d * k - f * g) + c * (d * h - e * g);
        if (Math.Abs(det) < 1.0) return;
        double s = 4096.0 / det;
        // Rows 0 and 2 of the inverse: world X and Z from a view position.
        _r0 = (float)((e * k - f * h) * s); _r3 = (float)((c * h - b * k) * s); _r6 = (float)((b * f - c * e) * s);
        _r2 = (float)((d * h - e * g) * s); _r5 = (float)((b * g - a * h) * s); _r8 = (float)((a * e - b * d) * s);
        _cx = camX; _cz = camZ;
        _camera = true;
    }

    /// <summary>Draw water after the rest of the table it is in, against the depth
    /// buffer: in the game's order a bank under the water is now and then drawn
    /// after the water and over it, unblended -- at the widescreen edges, where the
    /// console never showed it. Needs the depth buffer.</summary>
    public static bool Defer = true;
    public static long Deferred;

    /// <summary>The port's water texture, whether or not the mapping is on.</summary>
    public static bool IsWater(int clut, int tpage) =>
        clut == Clut && (tpage & 0x19F) == (TexPage & 0x19F);

    public static bool Matches(int clut, int tpage) =>
        Enabled && _camera && clut == Clut && (tpage & 0x19F) == (TexPage & 0x19F);

    /// <summary>World X/Z of a view-space point: R⁻¹·v plus the camera.</summary>
    public static void World(float vx, float vy, float vz, out float wx, out float wz)
    {
        wx = _r0 * vx + _r3 * vy + _r6 * vz + _cx;
        wz = _r2 * vx + _r5 * vy + _r8 * vz + _cz;
        if (Record) lock (Corners) if (Corners.Count < 4096) Corners.Add(((int)MathF.Round(wx / 4f), (int)MathF.Round(wz / 4f)));
    }

    /// <summary>The texture window (GP0 E2 units) that wraps coordinates inside the
    /// water's square.</summary>
    public static void Window(out int maskX, out int maskY, out int offX, out int offY)
    {
        int m = (~(Size - 1) & 0xFF) >> 3;
        maskX = maskY = m;
        offX = (U0 >> 3) & 0x1F;
        offY = (V0 >> 3) & 0x1F;
    }

    /// <summary>Which way V runs on a face, from the game's own coordinates:
    /// 0 +Z, 1 -Z, 2 +X, 3 -X. Decided per map tile (Period-sized cell of the face's
    /// centre) by the most decisive face seen there, so the pieces of one tile agree
    /// and keep agreeing: the near tiles are cut into small pieces whose rounded
    /// coordinates point a few degrees either way, and a 45-degree face ties the two
    /// axes -- decided face by face, their flow flipped as the view moved (the
    /// overlaps flashed).</summary>
    public static int Orientation(ReadOnlySpan<float> wx, ReadOnlySpan<float> wz, ReadOnlySpan<int> v)
    {
        if (Flow == 1) return 0;
        if (Flow == 2) return 2;
        // dV/dworld from the first three corners: solve the 2x2 system.
        float ax = wx[1] - wx[0], az = wz[1] - wz[0], bx = wx[2] - wx[0], bz = wz[2] - wz[0];
        float dv1 = v[1] - v[0], dv2 = v[2] - v[0];
        float det = ax * bz - az * bx;
        int o = 0;
        float conf = 0f;
        if (MathF.Abs(det) >= 1f)
        {
            float gx = (dv1 * bz - dv2 * az) / det;
            float gz = (ax * dv2 - bx * dv1) / det;
            float sx = MathF.Abs(gx), sz = MathF.Abs(gz);
            o = sz >= sx ? (gz >= 0f ? 0 : 1) : (gx >= 0f ? 2 : 3);
            // How clearly one axis wins, scaled by how much texture the face spans
            // (a big face's direction is better measured than a sliver's).
            conf = MathF.Abs(sz - sx) / MathF.Max(sz + sx, 1e-6f) * MathF.Min(1f, MathF.Abs(det) / (Period * Period * 0.05f));
        }
        float cx = (wx[0] + wx[1] + wx[2]) / 3f, cz = (wz[0] + wz[1] + wz[2]) / 3f;
        var key = ((int)MathF.Floor(cx / Period), (int)MathF.Floor(cz / Period));
        lock (_tiles)
        {
            if (_tiles.TryGetValue(key, out var t) && Frame - t.Seen < 600 && t.Conf >= conf)
            {
                _tiles[key] = t with { Seen = Frame };
                return t.O;
            }
            _tiles[key] = (o, conf, Frame);
            if (_tiles.Count > 4096) _tiles.Clear();
        }
        return o;
    }

    static readonly Dictionary<(int, int), (int O, float Conf, int Seen)> _tiles = [];

    /// <summary>A corner's texture coordinates for an orientation, in texels, before
    /// the window wraps them.</summary>
    public static void Map(float wx, float wz, int orient, out float u, out float v)
    {
        float k = Size / Period;
        switch (orient)
        {
            case 1: u = -wx * k; v = -wz * k; break;
            case 2: u = -wz * k; v = wx * k; break;
            case 3: u = wz * k; v = -wx * k; break;
            default: u = wx * k; v = wz * k; break;
        }
    }
}
