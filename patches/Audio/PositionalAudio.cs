using RecompOne.Runtime;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace KfIII;

/// <summary>
/// Directional sound (Enhancements "Directional sound"): a door, a monster or a
/// spell is heard from where it is for as long as it plays, and on headphones from
/// behind as well as in front. The mixing is done by the runtime's SpuSpatialVoice;
/// this class finds the voices and says where their sources are. It is modelled on
/// the PositionalAudio of Verdite2, the RecompOne port of the first North American
/// King's Field.
///
/// <para><b>What the game does.</b> kf_Sound3D(se | flag 0x8000, &amp;pos, vol,
/// maxDist, [sp+0x10] fade, [sp+0x14] note) measures the distance d from the
/// listener (<see cref="GameAddr.ListenerPos"/>), gives up past maxDist, and makes
/// <code>
/// g    = 128 * (fade - d) / fade, at most 127
/// base = g + (127 - g) / 4,  or with flag 0x8000  g + 32 + (127 - g) / 2;  at most 127
/// </code>
/// It then pans by the angle to the source against the listener's yaw, lowering one
/// ear by sin(angle) * (127 - g) / 128: near sounds stay centred, far ones pan hard,
/// and behind is folded onto in front. kf_PlaySound takes a free voice and keys it
/// on through lib_SndKeyOn with those levels, once; the pan never follows the
/// player.</para>
///
/// <para><b>What this does.</b> The hooks on kf_Sound3D remember the source, and
/// the hooks on lib_SndKeyOn inside it learn the voice and tag it with the voice's
/// key-on serial. Every drawn frame (from Smooth, with the camera smoothed) each
/// live tag is aimed again from where the listener is now: on speakers with the
/// game's own law, not folded; on headphones binaurally. A voice keyed on again by
/// anything else loses its tag, so music is never touched. Area sounds (ambient
/// zones that kf_DrawObjects plays through kf_PlaySound2D) are heard from the
/// nearest point of their box.</para>
/// </summary>
public static class PositionalAudio
{
    public enum Mode { Off, Speakers, Headphones }

    const int Voices = 24;
    const uint ListenerPos = GameAddr.ListenerPos, ListenerYaw = GameAddr.ListenerYaw;

    /// <summary>
    /// Return address of kf_DrawObjects' call of kf_PlaySound2D for an area sound.
    /// Its object record is in s0: the box's corner X at +0x11 and Z at +0x19
    /// (unaligned s32s), and its half-size in units of 1024 at +0x35 (X) and +0x36
    /// (Z), so the box runs from X to X + 2048 * size.
    /// </summary>
    const uint AreaSoundRa = 0x80041284;

    // Headphones: a spherical head model (Brown and Duda), as in Verdite2.
    /// The head's radius over the speed of sound (8.75 cm, 343 m/s), in samples at 44.1 kHz.
    static readonly float _itdSamples = 0.0875f / 343f * 44100f;
    /// How much level moves from one ear to the other at full side.
    const float Ild = 0.5f;
    /// How much a source behind is dulled.
    const float RearDepth = 0.5f;
    /// Within this distance (half a tile) the direction fades to straight ahead, so
    /// a source on top of the player does not flip from ear to ear.
    const float Near = 0x400;

    public static Mode Current { get; private set; } = Mode.Headphones;
    static readonly bool _probe = DevSwitch.Is("KFIII_POSAUDIO_PROBE");

    sealed class Tag
    {
        public bool Live;
        /// The voice's key-on serial when tagged; a later key-on means another sound.
        public int Serial;
        public int X, Y, Z, Fade, Flag;
        /// The game's left plus right level at key-on.
        public float Sum;
        // An area sound: heard from the nearest point of its box, at the level the
        // game chose.
        public bool Area; public int X1, Z1; public float Level;
        // The game's key-on levels over its law's at key-on (kf_PlaySound applies
        // the effects volume after the law).
        public float Scale = 1f;
    }

    static readonly Tag[] _tags = Enumerable.Range(0, Voices).Select(_ => new Tag()).ToArray();
    static readonly int[] _serials = new int[Voices], _before = new int[Voices];

    // Between the pre- and post-hooks: which call is in progress, and what it said.
    static bool _inSound, _inKeyOn, _inArea;
    static int _ax0, _az0, _ax1, _az1;
    static int _sx, _sy, _sz, _sFade, _sFlag, _gameL, _gameR;

    // KFIII_POSAUDIO_PROBE counters.
    static long _calls, _keyOns, _areas;
    static long _tagged, _noVoice, _dropped, _lawOk, _lawBad;
    static int _details;
    static long _lastProbe;
    static readonly Dictionary<(uint ra, uint se), int> _flat = [];

    /// <summary>From the settings: <paramref name="mode"/> is 0 off, 1 speakers, 2 headphones. KFIII_POSAUDIO overrides it.</summary>
    public static void Apply(bool on, int mode)
    {
        var m = on ? (Mode)Math.Clamp(mode, 0, 2) : Mode.Off;
        if (DevSwitch.Text("KFIII_POSAUDIO") is { } e)
            m = e.ToLowerInvariant() switch
            {
                "0" or "off" => Mode.Off,
                "1" or "speakers" => Mode.Speakers,
                _ => Mode.Headphones,
            };
        Set(m);
    }

    static void Set(Mode mode)
    {
        Current = mode;
        if (mode != Mode.Off) return;
        var spu = Runtime.Spu;
        for (int v = 0; v < Voices; v++)
        {
            if (!_tags[v].Live) continue;
            _tags[v].Live = false;
            spu?.SetSpatial(v, -1, 0, 0, 0, 0, 0, 1, 1, 0);
        }
    }

    /// <summary>Pre-hook on kf_Sound3D: remembers the source's position, fade distance and flag.</summary>
    public static void BeforeSound(CpuContext c, IMemory m)
    {
        _calls++;
        if (Current == Mode.Off && !_probe) return;
        _inSound = true;
        _sFlag = (int)(c.A0 & 0x8000);
        _sx = (int)m.ReadU32(c.A1);
        _sy = (int)m.ReadU32(c.A1 + 4);
        _sz = (int)m.ReadU32(c.A1 + 8);
        _sFade = (int)m.ReadU32(c.SP + 0x10);
    }

    /// <summary>Post-hook on kf_Sound3D.</summary>
    public static void AfterSound(CpuContext c, IMemory m) { _inSound = false; _inKeyOn = false; }

    /// <summary>
    /// Pre-hook on kf_PlaySound2D: remembers an area sound's box. With the probe on,
    /// also counts every caller of a sound with no position.
    /// </summary>
    public static void BeforeFlat(CpuContext c, IMemory m)
    {
        if (_probe) lock (_flat) { var k = (c.RA, c.A0 & 0xFFFF); _flat[k] = _flat.GetValueOrDefault(k) + 1; }
        if (c.RA != AreaSoundRa || Current == Mode.Off) return;
        uint o = c.S0;
        _ax0 = ReadS32Unaligned(m, o + 0x11); _az0 = ReadS32Unaligned(m, o + 0x19);
        _ax1 = _ax0 + (m.ReadU8(o + 0x35) << 11); _az1 = _az0 + (m.ReadU8(o + 0x36) << 11);
        _inArea = true;
    }

    /// <summary>Post-hook on kf_PlaySound2D.</summary>
    public static void AfterFlat(CpuContext c, IMemory m) { _inArea = false; _inKeyOn = false; }

    static int ReadS32Unaligned(IMemory m, uint a) =>
        (int)(m.ReadU8(a) | (uint)m.ReadU8(a + 1) << 8 | (uint)m.ReadU8(a + 2) << 16 | (uint)m.ReadU8(a + 3) << 24);

    /// <summary>Pre-hook on lib_SndKeyOn, inside a 3D or area sound: notes the voices' serials and the game's levels.</summary>
    public static void BeforeKeyOn(CpuContext c, IMemory m)
    {
        _keyOns++;
        if (!(_inSound || _inArea) || Runtime.Spu is not { } spu) return;
        _inKeyOn = true;
        spu.CopyKeyOnSerials(_before);
        _gameL = (short)m.ReadU32(c.SP + 0x14);
        _gameR = (short)m.ReadU32(c.SP + 0x18);
    }

    /// <summary>Post-hook on lib_SndKeyOn: tags the voice it returned and aims it.</summary>
    public static void AfterKeyOn(CpuContext c, IMemory m)
    {
        if (!_inKeyOn) return;
        _inKeyOn = false;
        if (Runtime.Spu is not { } spu) return;
        int v = (int)c.V0;
        if ((uint)v >= Voices || _gameL + _gameR <= 0) { _noVoice++; return; }

        var t = _tags[v];
        t.Serial = _before[v] + 1;
        if (_inArea)
        {
            t.Area = true; t.X = _ax0; t.Z = _az0; t.X1 = _ax1; t.Z1 = _az1;
            t.Sum = _gameL + _gameR; t.Level = (_gameL + _gameR) / 2f;
            t.Live = true; _tagged++; _areas++;
            Aim(t, spu, m, v);
            return;
        }
        t.Area = false;
        t.X = _sx; t.Y = _sy; t.Z = _sz; t.Fade = _sFade; t.Flag = _sFlag;
        t.Sum = _gameL + _gameR;

        // The law, checked against what the game itself asked for at key-on.
        var (l, r) = GameLaw(t, m, (int)m.ReadU32(ListenerPos), (int)m.ReadU32(ListenerPos + 4), (int)m.ReadU32(ListenerPos + 8), (short)m.ReadU16(ListenerYaw));
        float sum = l + r;
        bool ok = sum > 0 && MathF.Abs(l / sum - _gameL / t.Sum) < 0.06f;
        t.Scale = sum > 0 ? t.Sum / sum : 1f;
        if (ok) _lawOk++; else _lawBad++;
        if (_probe && _details < 16)
        {
            _details++;
            Console.WriteLine($"[KFIII] posaudio: voice {v} src {_sx},{_sy},{_sz} fade {_sFade} flag {_sFlag:X} -- game L {_gameL} R {_gameR}, law L {l:F0} R {r:F0} {(ok ? "ok" : "MISMATCH")}");
        }
        if (Current == Mode.Off) return;
        t.Live = true;
        _tagged++;
        Aim(t, spu, m, v);
    }

    /// <summary>From Smooth.BeforeRender, every drawn frame: aims every live tag again, and drops those whose voice was keyed on since.</summary>
    public static void EachFrame(IMemory m)
    {
        if (Runtime.Spu is not { } spu) return;
        if (Current != Mode.Off)
        {
            spu.CopyKeyOnSerials(_serials);
            for (int v = 0; v < Voices; v++)
            {
                var t = _tags[v];
                if (!t.Live) continue;
                if (_serials[v] > t.Serial) { t.Live = false; _dropped++; continue; }
                Aim(t, spu, m, v);
            }
        }
        if (_probe) Report(spu);
    }

    /// <summary>The listener this frame: the player's (smoothed) position and the
    /// view yaw, which Smooth has placed for the draw.</summary>
    static (int x, int y, int z, short yaw) Listener(IMemory m) =>
        ((int)m.ReadU32(GameAddr.PlayerPos), (int)m.ReadU32(GameAddr.PlayerPos + 4), (int)m.ReadU32(GameAddr.PlayerPos + 8),
         (short)m.ReadU16(GameAddr.Yaw));

    /// <summary>The left and right levels kf_Sound3D gives a source, for a listener.</summary>
    static (float l, float r) GameLaw(Tag t, IMemory m, int lx, int ly, int lz, short yaw)
    {
        float dx = t.X - lx, dy = t.Y - ly, dz = t.Z - lz;
        float d = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        float g = Gain(t, d);
        float b = Base(t, g);
        float rel = Angle(dx, dz) - yaw;
        float s = MathF.Sin(rel * MathF.Tau / 4096f);
        float cut = b * MathF.Abs(s) * (127f - g) / 128f;
        // The first half turn lowers the right ear, the other half the left.
        return s >= 0 ? (b, b - cut) : (b - cut, b);
    }

    static float Gain(Tag t, float d) =>
        t.Fade > 0 ? Math.Clamp(128f * (t.Fade - d) / t.Fade, 0f, 127f) : 0f;

    static float Base(Tag t, float g) =>
        Math.Min(127f, t.Flag != 0 ? g + 32f + (127f - g) / 2f : g + (127f - g) / 4f);

    /// <summary>The game's angle to a source, in 4096ths of a turn: kf_AngleTo(dx, dz)
    /// is -atan2(dx, dz), worked out by octants (x > 0 gives -1024).</summary>
    static float Angle(float dx, float dz) => -MathF.Atan2(dx, dz) * 4096f / MathF.Tau;

    static void Aim(Tag t, Spu spu, IMemory m, int voice)
    {
        var (lx, ly, lz, yaw) = Listener(m);
        float dx, dy, dz, g, level;
        if (t.Area)
        {
            dx = Math.Clamp(lx, t.X, t.X1) - lx; dz = Math.Clamp(lz, t.Z, t.Z1) - lz; dy = 0;
            level = t.Level; g = 127f - level;
        }
        else
        {
            dx = t.X - lx; dy = t.Y - ly; dz = t.Z - lz;
            float d0 = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            g = Gain(t, d0); level = Base(t, g) * t.Scale;
        }
        float h = MathF.Sqrt(dx * dx + dz * dz), d = MathF.Sqrt(h * h + dy * dy);

        // The direction relative to the view: left (positive on the side whose
        // positive sine lowers the right ear in the game's law), ahead, and up.
        float rel = (Angle(dx, dz) - yaw) * MathF.Tau / 4096f;
        float left = 0, ahead = 1, up = 0;
        if (d >= 1)
        {
            left = MathF.Sin(rel) * h / d;
            ahead = MathF.Cos(rel) * h / d;
            up = -dy / d;
        }
        float w = Math.Clamp(d / Near, 0f, 1f);
        left *= w; up *= w; ahead = ahead * w + (1 - w);
        float rear = RearDepth * Math.Max(0f, -ahead);

        if (Current == Mode.Speakers && t.Area)
        {
            float s = Math.Clamp(left, -1f, 1f);
            spu.SetSpatial(voice, t.Serial, t.Sum, level * MathF.Sqrt(1 + Ild * s), level * MathF.Sqrt(1 - Ild * s), 0, 0, 1, 1, rear);
        }
        else if (Current == Mode.Speakers)
        {
            // The game's own law, kept up to date and not folded.
            float cut = level * MathF.Abs(left) * (127f - g) / 128f;
            float l = left >= 0 ? level : level - cut, r = left >= 0 ? level - cut : level;
            spu.SetSpatial(voice, t.Serial, t.Sum, l, r, 0, 0, 1, 1, rear);
        }
        else
        {
            float s = Math.Clamp(left, -1f, 1f);
            float thetaL = MathF.Acos(s), thetaR = MathF.Acos(-s);
            spu.SetSpatial(voice, t.Serial, t.Sum,
                level * MathF.Sqrt(1 + Ild * s), level * MathF.Sqrt(1 - Ild * s),
                Itd(thetaL), Itd(thetaR), Shelf(thetaL), Shelf(thetaR), rear);
        }
    }

    /// <summary>The head-shadow high shelf for an ear at <paramref name="theta"/> from the source (bright facing it, dull away).</summary>
    static float Shelf(float theta) => 1.05f + 0.95f * MathF.Cos(theta * 1.2f);
    /// <summary>The delay in samples for an ear at <paramref name="theta"/> from the source (Brown and Duda's).</summary>
    static float Itd(float theta) => _itdSamples * (theta < MathF.PI / 2 ? 1 - MathF.Cos(theta) : theta - MathF.PI / 2 + 1);

    /// <summary>KFIII_POSAUDIO_PROBE: the counters every two seconds, and who played sounds with no position.</summary>
    static void Report(Spu spu)
    {
        long now = Environment.TickCount64;
        if (now - _lastProbe < 2000) return;
        _lastProbe = now;
        int live = _tags.Count(t => t.Live);
        Console.WriteLine($"[KFIII] posaudio: {Current}, 3D calls {_calls}, key-ons {_keyOns}, tagged {_tagged} (areas {_areas}), no voice {_noVoice}, dropped {_dropped}, live {live}, law ok {_lawOk} bad {_lawBad}, mixer peak {spu.Stats.SpatialVoicesPeak}");
        spu.Stats.SpatialVoicesPeak = 0;
        lock (_flat)
            if (_flat.Count > 0)
                Console.WriteLine("[KFIII] posaudio: 2D sounds: " + string.Join(", ", _flat.OrderBy(k => k.Key.ra).Select(k => $"ra {k.Key.ra:X8} se {k.Key.se:X} x{k.Value}")));
    }
}
