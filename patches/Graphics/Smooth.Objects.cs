using RecompOne.Runtime.Memory;

namespace KfIII;

// High frame rate: creatures, props and the weapon swing between ticks.
public static partial class Smooth
{
    /// <summary>Off: creatures, props, effects and the weapon swing are drawn at their
    /// tick positions; only the camera is smoothed.</summary>
    public static bool Entities = true;

    /// <summary>One record of the creature, prop or effect table, as far as drawing
    /// it goes: position, angles, animation time and number, and (effects) scale.</summary>
    struct ObjState
    {
        public bool Live;
        public int X, Y, Z;
        public short Ax, Ay, Az;
        /// Animation time; Time2 is a prop's second timer (+0x10, passed to its drawer).
        public ushort Time, Time2;
        public short Sx, Sy, Sz;
        public byte Anim;
    }

    // ---- animation times ----------------------------------------------------------
    //
    // The model drawers blend the two key frames around an animation time, so an
    // in-between time is an in-between pose. Only small steps are carried, and only
    // within one animation: the animation number changing means the time restarted
    // in another one, and blending across it would make the shopkeeper jump when
    // spoken to.

    static readonly bool _animLerp = !DevSwitch.Is("KFIII_ANIM_LERP", "0");

    /// A creature's animation time: 0..0xFFF, stepping 0x80 a tick and looping.
    static ushort LerpLoop(ushort a, ushort b, double f)
    {
        if (!_animLerp) return b;
        int d = (b - a) & 0xFFF;
        if (d == 0 || d > 0x300 || a > 0xFFF || b > 0xFFF) return b;
        return (ushort)((a + (int)Math.Round(d * f)) & 0xFFF);
    }

    /// A prop's or effect's animation time, which can step either way (a secret door
    /// slides 0x48 a tick).
    static ushort LerpStep(ushort a, ushort b, double f)
    {
        if (!_animLerp) return b;
        int d = b - a;
        if (d == 0 || Math.Abs(d) > 0x300) return b;
        return (ushort)(a + (int)Math.Round(d * f));
    }

    // ---- creatures ------------------------------------------------------------------
    //
    // The entity table (GameAddr.EntityTable), walked by kf_DrawObjects. Each tick the
    // records are captured; each draw writes in-between values and AfterRender puts
    // the tick's back. A record that has just become live, or moved more than a tile
    // on an axis (a warp), is drawn where it is.

    const uint EntBase = GameAddr.EntityTable;
    const int EntCount = GameAddr.EntityCount, EntStride = GameAddr.EntityStride;

    static readonly ObjState[] _entPrev = new ObjState[EntCount], _entCur = new ObjState[EntCount];
    static bool _entPlaced;

    static ObjState ReadEnt(IMemory m, uint r) => new()
    {
        Live = m.ReadU8(r + 9) == 1,
        X = (int)m.ReadU32(r + 0x2C),
        Y = (int)m.ReadU32(r + 0x30),
        Z = (int)m.ReadU32(r + 0x34),
        Ax = (short)m.ReadU16(r + 0x40),
        Ay = (short)m.ReadU16(r + 0x42),
        Az = (short)m.ReadU16(r + 0x44),
        Time = m.ReadU16(r + 0x18),
        Anim = m.ReadU8(r + 0x0C),
    };

    static void WriteEnt(IMemory m, uint r, in ObjState e)
    {
        m.WriteU32(r + 0x2C, (uint)e.X); m.WriteU32(r + 0x30, (uint)e.Y); m.WriteU32(r + 0x34, (uint)e.Z);
        m.WriteU16(r + 0x40, (ushort)e.Ax); m.WriteU16(r + 0x42, (ushort)e.Ay); m.WriteU16(r + 0x44, (ushort)e.Az);
        m.WriteU16(r + 0x18, e.Time);
    }

    static void CaptureEntities(IMemory m)
    {
        EntityProbes(m);
        for (int i = 0; i < EntCount; i++)
        {
            var now = ReadEnt(m, EntBase + (uint)(i * EntStride));
            _entPrev[i] = _entCur[i].Live && now.Live ? _entCur[i] : now;
            _entCur[i] = now;
        }
    }

    static void PlaceEntities(IMemory m, double f)
    {
        _entPlaced = false;
        if (!Entities) return;
        for (int i = 0; i < EntCount; i++)
        {
            ref var c = ref _entCur[i];
            ref var p = ref _entPrev[i];
            if (!c.Live) continue;
            if (Math.Abs(c.X - p.X) > WarpDistance || Math.Abs(c.Y - p.Y) > WarpDistance || Math.Abs(c.Z - p.Z) > WarpDistance) continue;
            if (c.X == p.X && c.Y == p.Y && c.Z == p.Z && c.Ax == p.Ax && c.Ay == p.Ay && c.Az == p.Az && c.Time == p.Time) continue;
            WriteEnt(m, EntBase + (uint)(i * EntStride), new ObjState
            {
                Live = true,
                X = Lerp(p.X, c.X, f),
                Y = Lerp(p.Y, c.Y, f),
                Z = Lerp(p.Z, c.Z, f),
                Ax = LerpAngle(p.Ax, c.Ax, f),
                Ay = LerpAngle(p.Ay, c.Ay, f),
                Az = LerpAngle(p.Az, c.Az, f),
                Time = p.Anim == c.Anim ? LerpLoop(p.Time, c.Time, f) : c.Time,
            });
            _entPlaced = true;
            if (_smoothProbe && (++_entProbeN % 200) == 0)
                Console.WriteLine($"[KFIII-SMOOTH] entity {i} f {f:F2} prev {p.X},{p.Z} cur {c.X},{c.Z} yaw {p.Ay}->{c.Ay}");
        }
    }

    static void RestoreEntities(IMemory m)
    {
        if (!_entPlaced) return;
        for (int i = 0; i < EntCount; i++)
            if (_entCur[i].Live) WriteEnt(m, EntBase + (uint)(i * EntStride), _entCur[i]);
        _entPlaced = false;
    }

    // ---- items, doors, props -----------------------------------------------------------
    //
    // The prop table (GameAddr.PropTable), separate from the creatures: cleared by
    // kf_AreaChangeStep and drawn by kf_DrawObjects. An item being picked up is one of
    // these (kf_PickupItemStep lifts it in front of the camera and turns it 0x40 a
    // tick), and so is anything that swings or slides. Treated like the creatures.

    const uint PropBase = GameAddr.PropTable;
    const int PropCount = GameAddr.PropCount, PropStride = GameAddr.PropStride;

    static readonly ObjState[] _propPrev = new ObjState[PropCount], _propCur = new ObjState[PropCount];
    static bool _propPlaced;

    static ObjState ReadProp(IMemory m, uint r) => new()
    {
        Live = m.ReadU16(r + 6) != 0xFF,
        X = (int)m.ReadU32(r + 0x14),
        Y = (int)m.ReadU32(r + 0x18),
        Z = (int)m.ReadU32(r + 0x1C),
        Ax = (short)m.ReadU16(r + 0x24),
        Ay = (short)m.ReadU16(r + 0x26),
        Az = (short)m.ReadU16(r + 0x28),
        Time = m.ReadU16(r + 0x0A),
        Time2 = m.ReadU16(r + 0x10),
        Anim = m.ReadU8(r + 0x01),
    };

    static void WriteProp(IMemory m, uint r, in ObjState e)
    {
        m.WriteU32(r + 0x14, (uint)e.X); m.WriteU32(r + 0x18, (uint)e.Y); m.WriteU32(r + 0x1C, (uint)e.Z);
        m.WriteU16(r + 0x24, (ushort)e.Ax); m.WriteU16(r + 0x26, (ushort)e.Ay); m.WriteU16(r + 0x28, (ushort)e.Az);
        m.WriteU16(r + 0x0A, e.Time);
        m.WriteU16(r + 0x10, e.Time2);
    }

    static void CaptureProps(IMemory m)
    {
        for (int i = 0; i < PropCount; i++)
        {
            var now = ReadProp(m, PropBase + (uint)(i * PropStride));
            _propPrev[i] = _propCur[i].Live && now.Live ? _propCur[i] : now;
            _propCur[i] = now;
        }
    }

    static void PlaceProps(IMemory m, double f)
    {
        _propPlaced = false;
        if (!Entities) return;
        for (int i = 0; i < PropCount; i++)
        {
            ref var c = ref _propCur[i];
            ref var p = ref _propPrev[i];
            if (!c.Live) continue;
            if (Math.Abs(c.X - p.X) > WarpDistance || Math.Abs(c.Y - p.Y) > WarpDistance || Math.Abs(c.Z - p.Z) > WarpDistance) continue;
            if (c.X == p.X && c.Y == p.Y && c.Z == p.Z && c.Ax == p.Ax && c.Ay == p.Ay && c.Az == p.Az && c.Time == p.Time && c.Time2 == p.Time2) continue;
            WriteProp(m, PropBase + (uint)(i * PropStride), new ObjState
            {
                Live = true,
                X = Lerp(p.X, c.X, f),
                Y = Lerp(p.Y, c.Y, f),
                Z = Lerp(p.Z, c.Z, f),
                Ax = LerpAngle(p.Ax, c.Ax, f),
                Ay = LerpAngle(p.Ay, c.Ay, f),
                Az = LerpAngle(p.Az, c.Az, f),
                Time = p.Anim == c.Anim ? LerpStep(p.Time, c.Time, f) : c.Time,
                Time2 = LerpStep(p.Time2, c.Time2, f),
            });
            _propPlaced = true;
        }
    }

    static void RestoreProps(IMemory m)
    {
        if (!_propPlaced) return;
        for (int i = 0; i < PropCount; i++)
            if (_propCur[i].Live) WriteProp(m, PropBase + (uint)(i * PropStride), _propCur[i]);
        _propPlaced = false;
    }

    // ---- the weapon swing -------------------------------------------------------------
    //
    // kf_DrawWeapon draws the weapon in hand only while it swings (GameAddr.SwingTime
    // is -1 otherwise). kf_AnimateModel picks the two key frames around the swing
    // time and blends them (kf_BlendKeyframe), so the game can already draw any
    // in-between pose: redraws are given a time between the last two ticks. The
    // first tick of a swing blends from 0.

    const uint SwingTime = GameAddr.SwingTime, SwingAnim = GameAddr.SwingAnim;
    static short _swingPrev = -1, _swingCur = -1;
    static byte _swingAnimPrev, _swingAnimCur;
    static bool _swingPlaced;

    static void CaptureWeapon(IMemory m)
    {
        _swingPrev = _swingCur; _swingAnimPrev = _swingAnimCur;
        _swingCur = (short)m.ReadU16(SwingTime);
        _swingAnimCur = m.ReadU8(SwingAnim);
    }

    static void PlaceWeapon(IMemory m, double f)
    {
        _swingPlaced = false;
        if (!Entities || _swingCur < 0) return;
        short from = _swingPrev;
        if (from < 0 || _swingAnimPrev != _swingAnimCur)
        {
            if (_swingCur > 1024) return;   // joined mid-swing (after a load): drawn as it is
            from = 0;
        }
        if (_swingCur <= from) return;      // a new swing straight after the last
        m.WriteU16(SwingTime, (ushort)Lerp(from, _swingCur, f));
        _swingPlaced = true;
    }

    static void RestoreWeapon(IMemory m)
    {
        if (!_swingPlaced) return;
        m.WriteU16(SwingTime, (ushort)_swingCur);
        _swingPlaced = false;
    }
}
