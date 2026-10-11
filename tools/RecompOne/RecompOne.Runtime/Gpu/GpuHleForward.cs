using RecompOne.Runtime.Hle;

namespace RecompOne.Runtime;

public sealed partial class Gpu
{
    bool HleOn => !Detached && GpuHle.Active && GpuHle.Backend is { Ready: true };

    int CurTPage() => ((_texPageX / 64) & 0xf) | (((_texPageY / 256) & 1) << 4)
                    | ((_blendMode & 3) << 5) | ((_texDepth & 3) << 7);

    HleDrawEnv CurEnv() => new()
    {
        ClipX0 = _drawAreaLeft, ClipY0 = _drawAreaTop, ClipX1 = _drawAreaRight, ClipY1 = _drawAreaBottom,
        TwMaskX = _texWinMaskX, TwMaskY = _texWinMaskY, TwOffX = _texWinOffX, TwOffY = _texWinOffY,
        SetMask = _setMask, CheckMask = _checkMask, Dither = _dither,
    };

    // The backend has taken a float position all along, so the sub-pixel fraction
    // needs nothing of it: adding the fraction back here is the whole of the
    // hardware path. Fx and Fy are zero for a vertex that recovered nothing, and
    // for every vertex at all while the setting is off, so this is an exact no-op
    // in both cases rather than an almost-no-op.
    //
    // HasPersp and HasGteZ are independent: the first is "use Z as clip W" (the
    // perspective-correct path), the second is "this Z is a real view depth" for
    // the Z-buffer. Untextured walls have the second without the first.
    // The fraction fades out with view depth between SubpixelFadeNear and
    // SubpixelFadeFar (both 0: never). King's Field III draws its distant ground as
    // rows of separate tile pieces whose seams only close when every corner sits
    // on a whole pixel, as on the console; at their true sub-pixel positions the
    // seams opened as black lines across the road. Nearer ground shares its
    // corners, so a partial fraction there cannot open anything, and a seam's two
    // sides are at almost the same depth, so they always fade together.
    /// <summary>Diagnostic: every triangle's true (x, y, view depth) corners as drawn.</summary>
    public static Action<float, float, float, float, float, float, float, float, float, bool>? TriProbe;

    static float SubpixelWeight(in Vert v)
    {
        float far = GteDepth.SubpixelFadeFar;
        if (far <= 0f || !v.HasW) return 1f;
        float near = GteDepth.SubpixelFadeNear;
        if (v.W <= near) return 1f;
        if (v.W >= far) { GteDepth.FadedVertices++; return 0f; }
        return (far - v.W) / (far - near);
    }

    static HleVertex HV(in Vert v, bool persp, bool z) => HV(v, persp, z, SubpixelWeight(v));

    // A polygon with a corner the GTE clamped (behind the camera, or too close to
    // project) is drawn from its view-space corners instead: clipped against a
    // plane at GteDepth.NearClipZ, projected with the GTE's own H and centre plus
    // the drawing offset, colour and texture coordinates interpolated at the cut
    // points (linearly in view space, which is perspective-correct), each point
    // carrying its true depth as W. The console never draws these at all within a
    // 320-wide screen; a widescreen margin shows them, as black wedges beside the
    // camera.
    // Lx/Ly/Lz/Fog: the per-pixel lighting and smooth-fog inputs (0048), which a
    // cut point has to take from its edge like the colour. Copied from corner A to
    // every point, a polygon cut by the near plane drew A's pre-fog colour and fog
    // everywhere -- black where A was far in the fog.
    struct ClipV { public float X, Y, Z, R, G, B, U, V, Lx, Ly, Lz, Fog; }
    // Debug (RECOMPONE_CLIP_PAINT=1): draw everything from the clip path magenta.
    static readonly bool ClipPaint = Environment.GetEnvironmentVariable("RECOMPONE_CLIP_PAINT") == "1";

    void DrawNearClipped(IGpuBackend be, in Vert a, in Vert b, in Vert c, PrimFlags flags, bool depth)
    {
        // The projection the corners were made with, not the GTE's current one.
        float h = a.Ph, ofx = a.Pox, ofy = a.Poy;
        if (h <= 0f) return;

        // Clip in view space against the near plane and a guard band of +-G screen
        // pixels around the projection centre, so every output coordinate stays in
        // range (a corner just past the near plane otherwise projects to tens of
        // thousands of pixels, which the backend cannot carry). Each plane is
        // linear in view space, so attributes interpolate exactly.
        const float G = 2048f;
        float zn = GteDepth.NearClipZ;
        // Planes as (cx, cy, cz, d): keep cx*X + cy*Y + cz*Z + d >= 0.
        Span<float> planes = stackalloc float[]
        {
            0f, 0f, 1f, -zn,                       // Z >= zn
            h, 0f, G + ofx, 0f,                    // x >= -G : X*h + (G+ofx)*Z >= 0
            -h, 0f, G - ofx, 0f,                   // x <=  G : -X*h + (G-ofx)*Z >= 0
            0f, h, G + ofy, 0f,                    // y >= -G
            0f, -h, G - ofy, 0f,                   // y <=  G
        };

        Span<ClipV> bufA = stackalloc ClipV[12];
        Span<ClipV> bufB = stackalloc ClipV[12];
        bufA[0] = CV(a); bufA[1] = CV(b); bufA[2] = CV(c);
        int n = 3;
        for (int pi = 0; pi < 5 && n >= 3; pi++)
        {
            float cx = planes[pi * 4], cy = planes[pi * 4 + 1], cz = planes[pi * 4 + 2], d = planes[pi * 4 + 3];
            int m = 0;
            for (int i = 0; i < n; i++)
            {
                ref var p = ref bufA[i];
                ref var q = ref bufA[(i + 1) % n];
                float dp = cx * p.X + cy * p.Y + cz * p.Z + d;
                float dq = cx * q.X + cy * q.Y + cz * q.Z + d;
                if (dp >= 0f) bufB[m++] = p;
                if ((dp >= 0f) != (dq >= 0f))
                {
                    float t = dp / (dp - dq);
                    bufB[m++] = new ClipV
                    {
                        X = p.X + (q.X - p.X) * t, Y = p.Y + (q.Y - p.Y) * t, Z = p.Z + (q.Z - p.Z) * t,
                        R = p.R + (q.R - p.R) * t, G = p.G + (q.G - p.G) * t, B = p.B + (q.B - p.B) * t,
                        U = p.U + (q.U - p.U) * t, V = p.V + (q.V - p.V) * t,
                        Lx = p.Lx + (q.Lx - p.Lx) * t, Ly = p.Ly + (q.Ly - p.Ly) * t, Lz = p.Lz + (q.Lz - p.Lz) * t,
                        Fog = p.Fog + (q.Fog - p.Fog) * t,
                    };
                }
            }
            n = m;
            var tmp = bufA; bufA = bufB; bufB = tmp;
        }
        if (n < 3) return;
        GteDepth.NearClipped++;

        Span<HleVertex> hv = stackalloc HleVertex[12];
        for (int i = 0; i < n; i++)
        {
            ref var p = ref bufA[i];
            float k = h / p.Z;
            var v = HV(a, true, depth, 0f);
            v.X = ofx + p.X * k + _drawOffsetX;
            v.Y = ofy + p.Y * k + _drawOffsetY;
            v.R = (byte)Math.Clamp(p.R, 0f, 255f); v.G = (byte)Math.Clamp(p.G, 0f, 255f); v.B = (byte)Math.Clamp(p.B, 0f, 255f);
            v.U = p.U; v.V = p.V;
            v.Lx = p.Lx; v.Ly = p.Ly; v.Lz = p.Lz; v.Fog = p.Fog;
            v.Z = p.Z; v.HasPersp = true; v.HasGteZ = depth;
            if (ClipPaint) { v.R = 255; v.G = 0; v.B = 255; }
            hv[i] = v;
        }
        for (int i = 1; i + 1 < n; i++) be.DrawTri(hv[0], hv[i], hv[i + 1], flags);
    }

    static ClipV CV(in Vert v) => new() { X = v.Vx, Y = v.Vy, Z = v.Vz, R = v.R, G = v.G, B = v.B, U = v.U, V = v.V,
                                          Lx = v.Lx, Ly = v.Ly, Lz = v.Lz, Fog = v.Fog };

    // An offset of a pixel or more is an unclamped corner (Gte.ProjectTail), not a
    // fraction. In a polygon that still has a clamped corner it is dropped, so the
    // polygon is drawn exactly as the console's clamped one rather than half-moved.
    static HleVertex HVGuarded(in Vert v, bool persp, bool z, bool anyClamped) =>
        anyClamped && (MathF.Abs(v.Fx) >= 1f || MathF.Abs(v.Fy) >= 1f) ? HV(v, persp, z, 0f) : HV(v, persp, z);

    static HleVertex HV(in Vert v, bool persp, bool z, float frac) => new()
    {
        X = v.X + v.Fx * frac, Y = v.Y + v.Fy * frac,
        R = (byte)v.R, G = (byte)v.G, B = (byte)v.B, U = (short)v.U, V = (short)v.V,
        Z = v.W, HasGteZ = z, HasPersp = persp,
        Lx = v.Lx, Ly = v.Ly, Lz = v.Lz, Fog = v.Fog, Light = v.Light, LightGen = v.LightGen,
        TexRect = v.TexRect, HasTexRect = v.HasTexRect, Solid = v.Solid, Material = v.Material, Projected = v.Projected,
    };

    PrimFlags PrimOf(bool tex, bool semi, bool raw, int clut, bool gouraud = false) => new()
    {
        Textured = tex, SemiTrans = semi, RawTexture = raw, Gouraud = gouraud, TPage = (ushort)CurTPage(), Clut = (ushort)clut,
    };

    void HleTri(in Vert a, in Vert b, in Vert c, bool tex, bool gouraud, bool semi, bool raw, int clut)
    {
        int spanX = Math.Max(a.X, Math.Max(b.X, c.X)) - Math.Min(a.X, Math.Min(b.X, c.X));
        int spanY = Math.Max(a.Y, Math.Max(b.Y, c.Y)) - Math.Min(a.Y, Math.Min(b.Y, c.Y));
        // The console's GPU drops a polygon wider than 1023 or taller than 511 pixels.
        // Within a 320-wide screen those are almost all off-screen, but a widescreen
        // margin shows them: ground tiles beside the camera (black wedges) and wall
        // pieces (seeing through walls). A 3D polygon whose corners all have view-space
        // positions is drawn anyway -- through the near-plane clip, so clamped corners
        // land where they belong. Anything else keeps the console's rule.
        bool oversize = spanX > 1023 || spanY > 511;
        if (oversize && !(GteDepth.NearClip && a.HasView && b.HasView && c.HasView)) return;

        // All three or none, as in the software path: the backend turns a vertex's
        // depth into gl_Position.w, and one vertex left at 1 would tear the
        // triangle's texture in half. The Z-buffer is the same rule for a different
        // reason — a corner at ndc.z = 0 among two real depths would punch a hole.
        // DepthWanted rather than ZBuffer: ambient occlusion reads the same
        // attachment and so needs the same writes, and differs only in that
        // nothing is ever rejected by them (GlCore.Flush leaves the func at
        // GL_ALWAYS unless the Z-buffer is on as well).
        bool z = GteDepth.DepthWanted && (GteDepth.OtSlot < 0 || GteDepth.OtSlot > GteDepth.SkySlots) && a.HasZ && b.HasZ && c.HasZ;

        // **A depth-tested triangle gets a real clip W whether or not its texture
        // is being corrected**, and that is a fix rather than tidiness. `vDepth` is
        // an ordinary varying, so OpenGL interpolates it in 1/w -- which is exact
        // when w is the view depth and *screen-linear* when w is 1. An untextured
        // wall never asked for texture correction, so it used to arrive with w = 1
        // and its interior depths came out linear in screen space, which is the one
        // thing a view depth is not: it is 1/z that is affine across a pixel row.
        // The software rasterizer has always interpolated the reciprocals and taken
        // one back (see `useZ` in DrawPolygon); this is the same arithmetic, done by
        // the rasterizer instead of by hand, and it makes the two agree.
        //
        // The visible cost is that a textured triangle drawn with the depth buffer
        // on and perspective correction off is now corrected anyway. That pair is a
        // comparison rather than a picture anyone ships, and a depth buffer fed
        // wrong depths is not a comparison of anything.
        bool persp = z || (tex && a.HasW && b.HasW && c.HasW);
        if (GteDepth.DepthWanted) { if (z) GteDepth.ZTris++; else GteDepth.ZSkipped++; }

        var be = GpuHle.Backend!;
        be.SetDrawEnv(CurEnv());
        if (TriProbe is { } probe)
            probe(a.X + a.Fx, a.Y + a.Fy, a.W, b.X + b.Fx, b.Y + b.Fy, b.W, c.X + c.Fx, c.Y + c.Fy, c.W, tex);
        bool anyClamped = a.Clamped || b.Clamped || c.Clamped;
        if ((anyClamped || oversize) && GteDepth.NearClip && a.HasView && b.HasView && c.HasView)
        {
            DrawNearClipped(be, a, b, c, PrimOf(tex, semi, raw, clut, gouraud), z);
            return;
        }
        be.DrawTri(HVGuarded(a, persp, z, anyClamped), HVGuarded(b, persp, z, anyClamped), HVGuarded(c, persp, z, anyClamped),
                   PrimOf(tex, semi, raw, clut, gouraud));
    }

    void HleRect(int x, int y, int w, int h, int u, int v, int clut, int r, int g, int b, bool tex, bool semi, bool raw)
    {
        var be = GpuHle.Backend!;
        be.SetDrawEnv(CurEnv());
        be.DrawRect(new HleRect { X = x, Y = y, W = w, H = h, U = (short)u, V = (short)v, R = (byte)r, G = (byte)g, B = (byte)b },
            PrimOf(tex, semi, raw, clut));
    }

    void HleLine(int x0, int y0, int r0, int g0, int b0, int x1, int y1, int r1, int g1, int b1, bool semi, bool gouraud)
    {
        if (Math.Abs(x1 - x0) > 1023 || Math.Abs(y1 - y0) > 511) return;

        var be = GpuHle.Backend!;
        be.SetDrawEnv(CurEnv());
        be.DrawLine(
            new HleVertex { X = x0, Y = y0, R = (byte)r0, G = (byte)g0, B = (byte)b0 },
            new HleVertex { X = x1, Y = y1, R = (byte)r1, G = (byte)g1, B = (byte)b1 },
            PrimOf(false, semi, false, 0, gouraud));
    }

    void HleFill(int x, int y, int w, int h, ushort color) => GpuHle.Backend!.FillRect(x, y, w, h, color);
    void HleCopy(int sx, int sy, int dx, int dy, int w, int h) => GpuHle.Backend!.CopyVram(sx, sy, dx, dy, w, h);

    ushort[] _readBuf = Array.Empty<ushort>();

    void HleReadback(int x, int y, int w, int h)
    {
        int n = w * h;
        if (_readBuf.Length < n) _readBuf = new ushort[n];
        var buf = _readBuf;

        if (Host.GpuJobs.Claimed && !Host.GpuJobs.IsOwner)
        {
            Host.GpuJobs.Run(() => GpuHle.Backend!.ReadVram(x, y, w, h, buf));
        }
        else
        {
            GpuHle.Backend!.ReadVram(x, y, w, h, buf);
        }

        for (int row = 0; row < h; row++)
        {
            int dst = ((y + row) & (VramHeight - 1)) * VramWidth;
            for (int col = 0; col < w; col++)
                Vram[dst + ((x + col) & (VramWidth - 1))] = _readBuf[row * w + col];
        }
        Assets.Textures.VramTracker.MarkCpuWrite(x, y, w, h);
    }

    //img load
    ushort[] _hleLoad = Array.Empty<ushort>();
    bool _hleLoadActive;
    int _hleLoadPos;

    void HleLoadBegin()
    {
        _hleLoadActive = HleOn;
        if (!_hleLoadActive) return;
        int n = _loadW * _loadH;
        if (_hleLoad.Length < n) _hleLoad = new ushort[n];
        _hleLoadPos = 0;
    }

    void HleLoadPut(ushort value)
    {
        if (_hleLoadActive && _hleLoadPos < _hleLoad.Length) _hleLoad[_hleLoadPos++] = value;
    }

    void HleLoadFlush()
    {
        if (!_hleLoadActive) return;
        GpuHle.Backend!.WriteVram(_loadX, _loadY, _loadW, _loadH, _hleLoad.AsSpan(0, _loadW * _loadH));
        _hleLoadActive = false;
    }
}
