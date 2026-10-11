using RecompOne.Runtime;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace KfIII;

/// <summary>
/// High frame rate: the world keeps the game's 15 Hz tick, and the picture is drawn
/// at the display's rate with the camera, creatures, props, effects and weapon
/// carried smoothly between ticks.
///
/// <para><b>The game's frame.</b> kf_MainLoop runs the world update, kf_BuildCamera
/// fills two stack blocks (X, Y, Z as s32; pitch, yaw, roll as s16), and kf_Render
/// draws, presents (kf_Present, which VSyncs) and then waits in kf_FrameGate until
/// the vblank counter (<see cref="GameAddr.VblankCounter"/>) reaches 4. Nothing in
/// the world advances during that wait.</para>
///
/// <para><b>The redraws.</b> <see cref="Gate"/> replaces kf_FrameGate: while the
/// counter is short of 4 it calls kf_Render again, nested, with its own gate call
/// returning at once. Each redraw is given the camera interpolated between the last
/// two ticks, at the time since the tick began over a tick's length. The world
/// update runs exactly as often as on the console; only drawing is repeated. The
/// present's frame-rate ceiling is raised to the target rate while drawing (menus
/// and message boxes pace on VSync too, so it is put back afterwards).</para>
///
/// <para>This is interpolation, not extrapolation: while tick N is current the
/// picture shows tick N-1 to N, a tick (67 ms) behind, but never a place the game
/// did not go.</para>
///
/// <para><b>Positions.</b> Every drawn frame writes in-between values into the
/// game's own tables (Smooth.Objects.cs, Smooth.Effects.cs) and <see cref="AfterRender"/>
/// puts the tick's values back, so the game's logic only ever sees its own.</para>
///
/// <para><b>Per-call state.</b> Some routines inside kf_Render advance state on
/// every call; a redraw must not, or water, animations and messages would run
/// several times too fast. kf_StepAfterGate is skipped in redraws
/// (<see cref="BeforeTickSlots"/>), and the rest is saved and restored around each
/// redraw (Smooth.TickState.cs). KFIII_SMOOTH_DIFF lists what a redraw changes in
/// RAM, which is how these were found (Smooth.Probes.cs).</para>
/// </summary>
public static partial class Smooth
{
    const uint VblankCounter = GameAddr.VblankCounter;
    /// A tick is four 60 Hz vblanks.
    const double TickMs = 4000.0 / 60.0;
    /// A move of more than a tile on any axis between ticks is a warp, drawn where it lands.
    const int WarpDistance = 2048;

    /// The camera blocks kf_BuildCamera fills and kf_Render reads.
    struct Cam { public int X, Y, Z; public short Pitch, Yaw, Roll; }

    /// Frames a second to draw: -1 the monitor's rate, 15 or less the console's.
    static double _fps;
    /// True while kf_Render is being run again from the gate.
    static bool _redraw;
    /// True once a tick has been captured (cleared by <see cref="Forget"/>).
    static bool _have;
    static uint _posPtr, _angPtr;
    static Cam _prev, _cur;
    static readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    static double _tickStart;

    /// <summary>The camera position block (X, Y, Z as s32) kf_Render was last called
    /// with: what the frame is drawn from (Water's seamless mapping, Mirror).</summary>
    public static uint CameraPtr;
    /// <summary>The camera angle block (pitch, yaw, roll as s16) kf_Render was last called with.</summary>
    public static uint AnglePtr;

    /// <summary>Set by Mirror around its mirrored call: the gate and the tick stepper
    /// leave it alone, as they leave a redraw.</summary>
    public static bool Mirroring;

    /// <summary>
    /// After a quick load: the last tick's camera and objects come from before it,
    /// and blending from them would show a jump (an animation restarting, the
    /// shopkeeper) on the first ticks after a reload in the same place.
    /// </summary>
    public static void Forget()
    {
        _have = false;
        _playerHave = false;
        _fxCount = 0;
        _swingCur = -1;
        Array.Clear(_entCur);
        Array.Clear(_propCur);
        Array.Clear(_fxCur);
    }

    public static bool Enabled => _fps < 0 || _fps > 15.5;

    /// <summary>From the settings (Enhancements): -1 the monitor's rate, 0 the console's 15.</summary>
    public static void SetFps(int fps) => _fps = fps < 0 ? -1 : fps;

    /// <summary>KFIII_FPS over the setting, when set.</summary>
    public static void ApplyEnvironment()
    {
        var v = DevSwitch.Text("KFIII_FPS");
        if (v == null) return;
        if (v is "off" or "0") _fps = 0;
        else if (DevSwitch.Number("KFIII_FPS") is double f) _fps = f;
    }

    public static string Describe => _fps < 0 ? "monitor" : _fps < 15.5 ? "original" : _fps.ToString("F0");

    static double TargetFps()
    {
        if (_fps >= 0) return _fps;
        int r = RecompOne.Runtime.Interp.Interp.RefreshRate;
        return r > 0 ? r : 60;
    }

    static Cam Read(IMemory m) => new()
    {
        X = (int)m.ReadU32(_posPtr),
        Y = (int)m.ReadU32(_posPtr + 4),
        Z = (int)m.ReadU32(_posPtr + 8),
        Pitch = (short)m.ReadU16(_angPtr),
        Yaw = (short)m.ReadU16(_angPtr + 2),
        Roll = (short)m.ReadU16(_angPtr + 4),
    };

    static void Write(IMemory m, in Cam k)
    {
        m.WriteU32(_posPtr, (uint)k.X); m.WriteU32(_posPtr + 4, (uint)k.Y); m.WriteU32(_posPtr + 8, (uint)k.Z);
        m.WriteU16(_angPtr, (ushort)k.Pitch); m.WriteU16(_angPtr + 2, (ushort)k.Yaw); m.WriteU16(_angPtr + 4, (ushort)k.Roll);
    }

    static int Lerp(int a, int b, double f) => a + (int)Math.Round((b - a) * f);

    /// 12-bit angles, the short way round.
    static short LerpAngle(short a, short b, double f)
    {
        int d = ((b - a + 2048) & 4095) - 2048;
        return (short)(a + (int)Math.Round(d * f));
    }

    static Cam At(double f)
    {
        bool warp = Math.Abs(_cur.X - _prev.X) > WarpDistance || Math.Abs(_cur.Y - _prev.Y) > WarpDistance || Math.Abs(_cur.Z - _prev.Z) > WarpDistance;
        if (warp) return _cur;
        return new Cam
        {
            X = Lerp(_prev.X, _cur.X, f),
            Y = Lerp(_prev.Y, _cur.Y, f),
            Z = Lerp(_prev.Z, _cur.Z, f),
            Pitch = LerpAngle(_prev.Pitch, _cur.Pitch, f),
            Yaw = LerpAngle(_prev.Yaw, _cur.Yaw, f),
            Roll = LerpAngle(_prev.Roll, _cur.Roll, f),
        };
    }

    /// How far into the current tick this frame is, 0..1.
    static double Frac() => Math.Clamp((_clock.Elapsed.TotalMilliseconds - _tickStart) / TickMs, 0, 1);

    /// <summary>Pre-hook on kf_Render, for the stock call and every redraw: captures a
    /// new tick (stock call) and places the camera and objects for this frame.</summary>
    public static void BeforeRender(CpuContext c, IMemory m)
    {
        CameraPtr = c.A0; AnglePtr = c.A1;
        // Mirror's mirrored call is a nested kf_Render of its own; nothing here is for it.
        if (Mirror.Active) return;
        if (!_redraw) Pools.Check(m);
        if (_rateProbe) { if (_redraw) _rateRedraw++; else _rateStock++; ReportRate(); }
        if (!Enabled) { Water.Place(1.0, false, true); PositionalAudio.EachFrame(m); return; }
        if (!_redraw)
        {
            // The stock call starts a new tick. Its present gets the redraws' frame-rate
            // ceiling too: at the console's 60 it would wait up to a whole 60 Hz frame,
            // holding one frame per tick twice as long, and the motion would catch up in
            // a jump -- a 15 Hz stutter on everything moving steadily (spells, arrows).
            _stockFps = Runtime.TargetFps;
            Runtime.TargetFps = TargetFps();
            _stockRaised = true;
            _posPtr = c.A0; _angPtr = c.A1;
            var now = Read(m);
            _prev = _have ? _cur : now;
            _cur = now;
            _have = true;
            // The tick's start, on a steady clock. Measured here it wanders by how long
            // the world update and the last redraw took, so a tick's blend would often
            // end at 0.8 or begin at 0.25 and anything moving steadily (a Stone, an
            // arrow) would jump once a tick. Ticks are 4 vblanks apart, so each is
            // expected one tick after the last, pulled a tenth of the way towards where
            // it was measured (so it cannot drift); when far off (a load, a menu, a
            // pause) it starts over from now.
            double nowMs = _clock.Elapsed.TotalMilliseconds, expect = _tickStart + TickMs;
            if (_rateProbe && _gateLeftMs > 0) { double u = nowMs - _gateLeftMs; _updateMs += u; _updateMax = Math.Max(_updateMax, u); _updateN++; }
            _tickStart = Math.Abs(nowMs - expect) < TickMs * 0.5 ? expect + (nowMs - expect) * 0.1 : nowMs;
            CaptureEntities(m);
            CaptureProps(m);
            CaptureEffects(m);
            for (int i = 0; i < 3; i++)
            {
                int pos = (int)m.ReadU32(PlayerPos + (uint)(i * 4));
                _playerPrev[i] = _playerHave ? _playerCur[i] : pos;
                _playerCur[i] = pos;
            }
            for (int i = 0; i < 3; i++)
            {
                short ang = (short)m.ReadU16(ViewAngles + (uint)(i * 2));
                _viewPrev[i] = _playerHave ? _viewCur[i] : ang;
                _viewCur[i] = ang;
            }
            _playerHave = true;
            CaptureWeapon(m);
            Controls.NewTick();
            if (_tickDiffAt > 0 && _clock.Elapsed.TotalSeconds > _tickDiffAt) TickDiff(m);
        }
        double f = Frac();
        var k = At(f);
        // Mouse look without the tick of lag (see Controls.Preview).
        var (py, pp) = Controls.Preview();
        _mouseYaw = (int)Math.Round(Controls.LastYaw * (1 - f) + py);
        // The game holds pitch within +-PitchLimit, and the preview has to stop there
        // too, or looking hard up or down draws past the limit and snaps back each tick.
        int basePitch = m.ReadU16(GameAddr.Pitch) & 0xFFF;
        if (basePitch >= 0x800) basePitch -= 0x1000;
        pp = Math.Clamp(pp, -GameAddr.PitchLimit - basePitch, GameAddr.PitchLimit - basePitch);
        _mousePitch = (int)Math.Round(Controls.LastPitch * (1 - f) + pp);
        k.Yaw = (short)(k.Yaw + _mouseYaw);
        k.Pitch = (short)(k.Pitch + _mousePitch);
        if (_pitchProbe) Console.WriteLine($"[KFIII-PITCH] f {f:F2} drawn {basePitch + (int)Math.Round(Controls.LastPitch * (1 - f) + pp) - Controls.LastPitch} base {basePitch} last {Controls.LastPitch} pp {pp:F1}");
        if (_smoothProbe && (++_smoothProbeN % 40) == 0)
            Console.WriteLine($"[KFIII-SMOOTH] f {f:F2} yaw {k.Yaw} (cur {_cur.Yaw} base {m.ReadU16(GameAddr.Yaw)}) pitch {k.Pitch} (cur {_cur.Pitch} base {(short)m.ReadU16(GameAddr.Pitch)}) mouse {py:F1},{pp:F1}");
        Write(m, k);
        PlaceEntities(m, f);
        PlaceProps(m, f);
        PlaceEffects(m, f);
        PlaceWeapon(m, f);
        PlacePlayer(m, f);
        Water.Place(f, true, !_redraw);
        PositionalAudio.EachFrame(m);
    }

    // ---- the player -------------------------------------------------------------
    //
    // The player's position, which the camera is built from and which effects
    // centred on the player read while drawing (the poison cloud around the player
    // would jump 15 times a second against the smoothed camera), and the view
    // angles the camera block is copied from, with the mouse preview added.

    const uint PlayerPos = GameAddr.PlayerPos;
    const uint ViewAngles = GameAddr.ViewAngles;
    static readonly int[] _playerPrev = new int[3], _playerCur = new int[3];
    static readonly short[] _viewPrev = new short[3], _viewCur = new short[3];
    /// The mouse turn drawn this frame on top of the tick's view, in angle units.
    static int _mouseYaw, _mousePitch;
    static bool _playerHave, _playerPlaced;

    static void PlacePlayer(IMemory m, double f)
    {
        _playerPlaced = false;
        if (Math.Abs(_playerCur[0] - _playerPrev[0]) > WarpDistance || Math.Abs(_playerCur[1] - _playerPrev[1]) > WarpDistance || Math.Abs(_playerCur[2] - _playerPrev[2]) > WarpDistance) return;
        for (int i = 0; i < 3; i++) m.WriteU32(PlayerPos + (uint)(i * 4), (uint)Lerp(_playerPrev[i], _playerCur[i], f));
        for (int i = 0; i < 3; i++)
        {
            int mouse = i == 0 ? _mousePitch : i == 1 ? _mouseYaw : 0;
            m.WriteU16(ViewAngles + (uint)(i * 2), (ushort)(LerpAngle(_viewPrev[i], _viewCur[i], f) + mouse));
        }
        _playerPlaced = true;
    }

    static void RestorePlayer(IMemory m)
    {
        if (!_playerPlaced) return;
        for (int i = 0; i < 3; i++) m.WriteU32(PlayerPos + (uint)(i * 4), (uint)_playerCur[i]);
        for (int i = 0; i < 3; i++) m.WriteU16(ViewAngles + (uint)(i * 2), (ushort)_viewCur[i]);
        _playerPlaced = false;
    }

    // ---- after the draw ----------------------------------------------------------

    /// The present ceiling the stock call raised, to put back after it.
    static double _stockFps;
    static bool _stockRaised;

    /// <summary>Post-hook on kf_Render: everything placed for the frame goes back to
    /// what the game built.</summary>
    public static void AfterRender(CpuContext c, IMemory m)
    {
        if (Mirror.Active) return;
        if (_stockRaised && !_redraw) { Runtime.TargetFps = _stockFps; _stockRaised = false; }
        PoolProbe(m);
        if (!Enabled || !_have) return;
        Write(m, _cur);
        RestoreEntities(m);
        RestoreProps(m);
        RestoreEffects(m);
        RestoreWeapon(m);
        RestorePlayer(m);
    }

    // ---- the frame gate ------------------------------------------------------------

    /// <summary>Pre-hook on kf_StepAfterGate (it steps twelve 0x14-byte slots at
    /// 0x801AAB64 once a call): false skips it in redraws and the mirrored draw.</summary>
    public static bool BeforeTickSlots(CpuContext c, IMemory m) => !_redraw && !Mirroring;

    /// <summary>Replaces kf_FrameGate: instead of idling until the fourth vblank,
    /// redraws the frame for as long as the wait lasts.</summary>
    public static void Gate(CpuContext c, IMemory m)
    {
        if (_redraw || Mirroring) return;
        _rateGate++;

        if (!Enabled || !_have)
        {
            while (m.ReadU32(VblankCounter) < 4)
            {
                c.A0 = 0;
                Recompiled.KingsFieldIII_game.VSync_game(c, m);
            }
            m.WriteU32(VblankCounter, 0);
            return;
        }

        var regs = c.Snapshot();
        double saved = Runtime.TargetFps;
        Runtime.TargetFps = TargetFps();
        try
        {
            while (m.ReadU32(VblankCounter) < 4)
            {
                byte[]? before = _diffProbe && _diffDone < 3 && _clock.Elapsed.TotalSeconds >= _diffAt ? Snap(m) : null;
                SaveTickState(m);
                _redraw = true;
                c.A0 = _posPtr; c.A1 = _angPtr;
                Recompiled.KingsFieldIII_game.kf_Render(c, m);
                _redraw = false;
                RestoreTickState(m);
                c.Restore(regs);
                if (before != null) { Diff(m, before); _diffDone++; }
            }
        }
        finally
        {
            _redraw = false;
            Runtime.TargetFps = saved;
        }
        m.WriteU32(VblankCounter, 0);
        _gateLeftMs = _clock.Elapsed.TotalMilliseconds;
    }
}
