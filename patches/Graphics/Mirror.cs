using RecompOne.Runtime;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace KfIII;

/// <summary>
/// Planar water reflections: every drawn frame, the game's renderer (kf_Render) is
/// run once more from the camera mirrored in the water, into the runtime's planar
/// texture (PlanarReflections), which the reflection pass reads wherever a surface
/// lies on the water's plane. Unlike the screen-space march it cannot be blocked by
/// a tree on the bank, and it reflects what is above the top of the screen.
///
/// <para>The mirrored call is made from inside kf_Present (<see cref="BeforePresent"/>),
/// after Smooth has placed the frame's camera and objects, and is treated like one of
/// Smooth's redraws, with the same per-call state put back afterwards
/// (Smooth.SaveTickState). Beyond that:</para>
/// <list type="bullet">
/// <item>it draws into its own ordering table and primitive pool
/// (<see cref="AfterBeginFrame"/>), and the buffer index kf_BeginFrame flips is put
/// back, so the game's two frames stay as built;</item>
/// <item>its kf_Present only links its table and draws it into the planar texture
/// (no DrawSync, VSync or PutDispEnv);</item>
/// <item>the frame gate and the tick stepper return at once (Smooth.Mirroring), and
/// kf_ScrollTextures is skipped (<see cref="BeforeScroll"/>);</item>
/// <item>the weapon swing time is set to -1 (kf_DrawWeapon draws in view space);</item>
/// <item>the runtime drops everything flat while capturing (Drop2D: HUD, text).</item>
/// </list>
///
/// <para>The camera: X and Z the same, Y mirrored about the plane, pitch and roll
/// negated; the backend flips the picture's rows. The plane is the runtime's, found
/// from the water drawn the frame before (PlanarReflections.TakePlane, fed by the
/// camera Water.Ground publishes).</para>
///
/// <code>
/// KFIII_PLANAR=0/1        on/off (default: with reflections)
/// KFIII_PLANAR_BIAS=8     how far above the plane geometry must be to be reflected, world units
/// KFIII_PLANAR_PROBE=1    plane, calls and timing every 2 s
/// KFIII_PLANAR_DEBUG, KFIII_PLANAR_RIPPLE, KFIII_PLANAR_TOL   the runtime pass's parameters
/// KFIII_PLANAR_NOCALL, KFIII_PLANAR_NOPRESENT, KFIII_PLANAR_NODRAW   tests: skip one stage
/// </code>
/// </summary>
public static class Mirror
{
    // ---- the game's frame (kf_BeginFrame, kf_Present) ----------------------------

    const uint BufferIndex = GameAddr.FrameBufferIndex;
    /// drawenv[index] at DrawEnvs + 92 * index, dispenv[index] at DispEnvs + 20 * index.
    const uint DrawEnvs = GameAddr.DrawEnvs, DispEnvs = GameAddr.DispEnvs;
    /// Pointers to the ordering table (0x2000 entries) and the 8-entry overlay table.
    const uint OtBasePtr = GameAddr.OrderingTablePtr, SmallOtPtr = GameAddr.OverlayOtPtr;
    /// Three per-frame words kf_BeginFrame zeroes.
    const uint FrameWords = GameAddr.BeginFrameWords;
    /// The renderer's camera, angles and tile grid (kf_BuildTileGrid's grid included).
    const uint RenderState = GameAddr.RenderState, RenderStateBytes = 0x400;
    const uint ScratchpadBase = 0x1F800000, ScratchpadBytes = 0x400;
    const uint OtEntries = 0x2000;

    // ---- the mirror's own memory, above the primitive pools (Pools: 0x80200000 + 2 x 0x80000) ----

    /// The mirrored camera's position and angle blocks.
    const uint PosBlock = 0x80300000, AngBlock = 0x80300010;
    /// Its pool descriptor, overlay table, ordering table and pool. Built into the
    /// other buffer's (which kf_BeginFrame's flip hands it), the mirrored frame would
    /// replace the frame the menu redraws as its background, and the menu would show
    /// the world from under the water.
    const uint MirrorDesc = 0x80300040, MirrorSmallOt = 0x80300080, MirrorOt = 0x80301000;
    const uint MirrorPool = 0x80310000, MirrorPoolSize = 0x80000;

    public static bool Active { get; private set; }

    static readonly bool _probe = DevSwitch.Is("KFIII_PLANAR_PROBE");
    static readonly bool _noPresent = DevSwitch.Is("KFIII_PLANAR_NOPRESENT");
    static readonly bool _noCall = DevSwitch.Is("KFIII_PLANAR_NOCALL");
    static readonly bool _noDraw = DevSwitch.Is("KFIII_PLANAR_NODRAW");
    static readonly float _bias = DevSwitch.Number("KFIII_PLANAR_BIAS") is double b ? (float)b : 8f;

    static readonly Gte.State _gte = new();
    static readonly Smooth.TickState _tick = new();
    static readonly uint[] _renderState = new uint[RenderStateBytes / 4], _scratchpad = new uint[ScratchpadBytes / 4];

    /// The real camera's rotation, and the mirrored one's (when the mirrored call has
    /// drawn ground this frame).
    static readonly short[] _cameraRot = new short[9], _mirrorRot = new short[9];
    static bool _haveMirrorRot;
    /// The buffer index at the mirrored call's start, put back after kf_BeginFrame's flip.
    static byte _bufferIndex;
    /// The water's plane Y and the mirrored camera's Y, for the clip plane and the probe.
    static float _plane;
    static int _mirY;

    static readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    static double _reportedAt, _ms;
    static long _calls, _noWater, _below;

    /// <summary>From Water.Ground during the mirrored call: the mirrored camera's
    /// rotation, for the clip plane.</summary>
    public static void NoteRotation(ReadOnlySpan<short> r)
    {
        r.CopyTo(_mirrorRot);
        _haveMirrorRot = true;
    }

    /// <summary>From Water.Ground in the real call: the camera, for finding the
    /// water's plane in what is drawn.</summary>
    public static void NoteCamera(ReadOnlySpan<short> r, float camY)
    {
        r.CopyTo(_cameraRot);
        PlanarReflections.SetCamera(r, camY);
    }

    /// <summary>From Water.Apply: on with the water reflections setting, unless KFIII_PLANAR says otherwise.</summary>
    public static void Apply(bool on)
    {
        PlanarReflections.Enabled = on;
        PlanarReflections.Drop2D = true;
        PlanarReflections.Probe = _probe;
        if (int.TryParse(DevSwitch.Text("KFIII_PLANAR_DEBUG"), out var dbg)) PlanarReflections.Debug = dbg;
        if (DevSwitch.Text("KFIII_PLANAR") is { } e) PlanarReflections.Enabled = e != "0";
        if (DevSwitch.Number("KFIII_PLANAR_RIPPLE") is double rp) PlanarReflections.Ripple = (float)rp;
        if (DevSwitch.Number("KFIII_PLANAR_TOL") is double tl) PlanarReflections.Tolerance = (float)tl;
    }

    static bool Ready => PlanarReflections.Enabled && PlanarReflections.Supported && ScreenReflections.Enabled && !Active;

    /// <summary>
    /// Pre-hook on kf_Present (false skips the routine).
    ///
    /// In the real call, the routine is done here instead, step for step (DrawSync,
    /// VSync, PutDispEnv, PutDrawEnv, the table link, DrawOTag), with the mirrored
    /// frame drawn between PutDrawEnv and DrawOTag. That is the one moment it can go:
    /// the game shows a picture a frame late, so before the VSync the buffer about to
    /// be drawn is the one on screen, and a capture made then would reach the screen
    /// paired with a picture two frames older.
    ///
    /// In the mirrored call (Active), it links the mirror's table and draws it into the
    /// planar texture, under the draw buffer the real call has just set.
    /// </summary>
    public static bool BeforePresent(CpuContext c, IMemory m)
    {
        if (Active) { DrawMirrored(c, m); return false; }
        if (!Ready) { PlanarReflections.TakePlane(out _, out _); return true; }

        var regs = c.Snapshot();
        c.A0 = 0; Recompiled.KingsFieldIII_game.DrawSync_game(c, m);
        c.A0 = 0; Recompiled.KingsFieldIII_game.VSync_game(c, m);
        byte index = m.ReadU8(BufferIndex);
        c.A0 = DispEnvs + 20u * index; Recompiled.KingsFieldIII_game.PutDispEnv_game(c, m);
        c.A0 = DrawEnvs + 92u * index; Recompiled.KingsFieldIII_game.PutDrawEnv_game(c, m);
        c.Restore(regs);

        Capture(c, m);

        Link(m);
        c.A0 = m.ReadU32(OtBasePtr) + 0x7FFC;
        Recompiled.KingsFieldIII_game.DrawOTag_game(c, m);
        c.Restore(regs);
        return false;
    }

    /// <summary>kf_Present's table link: the overlay table spliced in at the end.</summary>
    static void Link(IMemory m)
    {
        uint small = m.ReadU32(SmallOtPtr), ot = m.ReadU32(OtBasePtr);
        m.WriteU32(small, (m.ReadU32(small) & 0xFF000000u) | (m.ReadU32(ot + 0x7FF8) & 0x00FFFFFFu));
        m.WriteU32(ot + 0x7FF8, (m.ReadU32(ot + 0x7FF8) & 0xFF000000u) | ((small + 0x1C) & 0x00FFFFFFu));
    }

    /// <summary>The mirrored call: kf_Render from the mirrored camera, with everything
    /// it would leave behind put back.</summary>
    static void Capture(CpuContext c, IMemory m)
    {
        if (!PlanarReflections.TakePlane(out float plane, out _)) { _noWater++; return; }
        uint pos = Smooth.CameraPtr, ang = Smooth.AnglePtr;
        if (pos == 0 || ang == 0) return;
        int camY = (int)m.ReadU32(pos + 4);
        // From under the water, or level with it, there is nothing above it to see.
        if (camY >= plane - 16f) { _below++; return; }

        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        int mirroredY = 2 * (int)MathF.Round(plane) - camY;
        m.WriteU32(PosBlock, m.ReadU32(pos));
        m.WriteU32(PosBlock + 4, (uint)mirroredY);
        m.WriteU32(PosBlock + 8, m.ReadU32(pos + 8));
        m.WriteU32(PosBlock + 12, m.ReadU32(pos + 12));
        m.WriteU16(AngBlock, (ushort)(-(short)m.ReadU16(ang)));
        m.WriteU16(AngBlock + 2, m.ReadU16(ang + 2));
        m.WriteU16(AngBlock + 4, (ushort)(-(short)m.ReadU16(ang + 4)));
        m.WriteU16(AngBlock + 6, m.ReadU16(ang + 6));

        // The real camera's plane, for the pass: a surface's height below the water
        // (Y is down), from its view position (row 1 of R transposed).
        PlanarReflections.ViewPlane[0] = _cameraRot[1] / 4096f;
        PlanarReflections.ViewPlane[1] = _cameraRot[4] / 4096f;
        PlanarReflections.ViewPlane[2] = _cameraRot[7] / 4096f;
        PlanarReflections.ViewPlane[3] = camY - plane;
        _plane = plane;
        _mirY = mirroredY;

        var regs = c.Snapshot();
        Gte.Save(_gte);
        byte index = m.ReadU8(BufferIndex);
        _bufferIndex = index;
        // The renderer's state and the scratchpad are saved too: left as the mirrored
        // call made them, the menu would draw its background from under the water.
        for (uint i = 0; i < RenderStateBytes; i += 4) _renderState[i / 4] = m.ReadU32(RenderState + i);
        for (uint i = 0; i < ScratchpadBytes; i += 4) _scratchpad[i / 4] = m.ReadU32(ScratchpadBase + i);
        uint pool = m.ReadU32(GameAddr.PrimPoolActive), ot = m.ReadU32(OtBasePtr), small = m.ReadU32(SmallOtPtr);
        uint w0 = m.ReadU32(FrameWords), w1 = m.ReadU32(FrameWords + 4), w2 = m.ReadU32(FrameWords + 8);
        short swing = (short)m.ReadU16(GameAddr.SwingTime);
        Smooth.SaveTickState(m, _tick);
        Active = true;
        Smooth.Mirroring = true;
        _haveMirrorRot = false;
        try
        {
            m.WriteU16(GameAddr.SwingTime, 0xFFFF);
            c.A0 = PosBlock;
            c.A1 = AngBlock;
            if (!_noCall) Recompiled.KingsFieldIII_game.kf_Render(c, m);
            _calls++;
        }
        finally
        {
            Active = false;
            Smooth.Mirroring = false;
            PlanarReflections.Capturing = false;
            Smooth.RestoreTickState(m, _tick);
            m.WriteU16(GameAddr.SwingTime, (ushort)swing);
            m.WriteU8(BufferIndex, index);
            m.WriteU32(GameAddr.PrimPoolActive, pool);
            m.WriteU32(OtBasePtr, ot);
            m.WriteU32(SmallOtPtr, small);
            m.WriteU32(FrameWords, w0); m.WriteU32(FrameWords + 4, w1); m.WriteU32(FrameWords + 8, w2);
            for (uint i = 0; i < RenderStateBytes; i += 4) m.WriteU32(RenderState + i, _renderState[i / 4]);
            for (uint i = 0; i < ScratchpadBytes; i += 4) m.WriteU32(ScratchpadBase + i, _scratchpad[i / 4]);
            Smooth.CameraPtr = pos; Smooth.AnglePtr = ang;
            Gte.Load(_gte);
            c.Restore(regs);
        }
        _ms += System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        if (_probe) Report();
    }

    /// <summary>Pre-hook on kf_ScrollTextures: false in the mirrored call. It steps each
    /// scrolling texture's offset and copies it into VRAM on every call; run twice a
    /// frame, the water would flow at double speed, and the real call has already done
    /// this frame's.</summary>
    public static bool BeforeScroll(CpuContext c, IMemory m) => !Active;

    /// <summary>Post-hook on kf_BeginFrame: in the mirrored call, undoes the buffer flip
    /// and points the frame at the mirror's own table, overlay table and pool, cleared
    /// as the routine clears its own.</summary>
    public static void AfterBeginFrame(CpuContext c, IMemory m)
    {
        if (!Active) return;
        m.WriteU8(BufferIndex, _bufferIndex);
        m.WriteU32(MirrorDesc, MirrorPool);
        m.WriteU32(MirrorDesc + 4, MirrorPool + MirrorPoolSize);
        m.WriteU32(MirrorDesc + 8, MirrorPool);
        m.WriteU32(GameAddr.PrimPoolActive, MirrorDesc);
        ClearOt(m, MirrorOt, OtEntries);
        m.WriteU32(OtBasePtr, MirrorOt);
        ClearOt(m, MirrorSmallOt, 8);
        m.WriteU32(SmallOtPtr, MirrorSmallOt);
    }

    /// <summary>As ClearOTagR: each entry links to the one before, and the first ends the list.</summary>
    static void ClearOt(IMemory m, uint ot, uint n)
    {
        m.WriteU32(ot, 0x00FFFFFFu);
        for (uint i = 1; i < n; i++) m.WriteU32(ot + i * 4u, (ot + (i - 1u) * 4u) & 0x00FFFFFFu);
    }

    /// <summary>The mirrored call's present: its table, drawn into the planar texture.</summary>
    static void DrawMirrored(CpuContext c, IMemory m)
    {
        if (_noPresent) return;
        var regs = c.Snapshot();
        Link(m);
        // Only what is above the water is drawn in the mirrored view: with R' its
        // rotation, a view position's world Y is R'^T row 1 . p + Y'.
        var r = _haveMirrorRot ? (ReadOnlySpan<short>)_mirrorRot : _cameraRot;
        PlanarReflections.ClipPlane[0] = -r[1] / 4096f;
        PlanarReflections.ClipPlane[1] = -r[4] / 4096f;
        PlanarReflections.ClipPlane[2] = -r[7] / 4096f;
        PlanarReflections.ClipPlane[3] = _plane - _bias - _mirY;
        PlanarReflections.Serial++;
        PlanarReflections.Captures++;
        PlanarReflections.Capturing = true;
        try
        {
            c.A0 = m.ReadU32(OtBasePtr) + 0x7FFC;
            if (!_noDraw) Recompiled.KingsFieldIII_game.DrawOTag_game(c, m);
        }
        finally
        {
            PlanarReflections.Capturing = false;
            c.Restore(regs);
        }
    }

    /// KFIII_PLANAR_PROBE: every 2 s.
    static void Report()
    {
        double now = _clock.Elapsed.TotalSeconds, dt = now - _reportedAt;
        if (dt < 2.0) return;
        _reportedAt = now;
        Console.WriteLine($"[KFIII] planar: plane Y {_plane:F0}, mirrored Y {_mirY}; {_calls / dt:F1} mirrored draws/s at {(_calls == 0 ? 0 : _ms / _calls):F2} ms; " +
                          $"{_noWater / dt:F1}/s no water, {_below / dt:F1}/s under it; {PlanarReflections.Captures / dt:F1} captures/s, " +
                          $"{PlanarReflections.Cleared / dt:F1} cleared/s, {PlanarReflections.Read / dt:F1} passes read one; " +
                          $"{PlanarReflections.WaterTris / dt:F0} water tris/s binned, {PlanarReflections.WaterTilted / dt:F0} not level; " +
                          $"last readback {PlanarReflections.PlanarPct:F1}% of reflective pixels planar");
        _calls = _noWater = _below = 0; _ms = 0;
        PlanarReflections.ResetCounters();
        PlanarReflections.WaterTris = PlanarReflections.WaterTilted = 0;
        ScreenReflections.WantMap = true;
    }
}
