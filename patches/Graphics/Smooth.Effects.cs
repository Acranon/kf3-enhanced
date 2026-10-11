using RecompOne.Runtime.Memory;

namespace KfIII;

// High frame rate: spell effects between ticks.
//
// The effect table (GameAddr.EffectTable), drawn after the props by kf_DrawObjects:
// spells and clouds (a mushroom's spores rise and swell every tick). Treated like the
// creatures, with the scale blended too.
//
// An effect held in front of the player (the arrow on a drawn bow) keeps a fixed
// distance and turns with the player's yaw. It is placed from the drawn camera's
// facing instead, or it would trail the mouse and catch up a tick later.
public static partial class Smooth
{
    const uint FxBase = GameAddr.EffectTable;
    const int FxStride = GameAddr.EffectStride;
    /// The most records read; the table ends at the first whose first byte is 0xFF.
    const int FxMax = 64;

    static readonly ObjState[] _fxPrev = new ObjState[FxMax], _fxCur = new ObjState[FxMax];
    static readonly bool[] _fxHeld = new bool[FxMax];
    /// Each effect's distance from the player at the last tick (held effects keep it).
    static readonly int[] _fxDist = new int[FxMax];
    static int _fxCount;
    static bool _fxPlaced;

    static ObjState ReadFx(IMemory m, uint r) => new()
    {
        Live = (m.ReadU8(r + 8) & 3) != 0,
        X = (int)m.ReadU32(r + 0x18),
        Y = (int)m.ReadU32(r + 0x1C),
        Z = (int)m.ReadU32(r + 0x20),
        Ax = (short)m.ReadU16(r + 0x28),
        Ay = (short)m.ReadU16(r + 0x2A),
        Az = (short)m.ReadU16(r + 0x2C),
        Time = m.ReadU16(r + 0x14),
        Anim = m.ReadU8(r + 0x04),
        Sx = (short)m.ReadU16(r + 0x30),
        Sy = (short)m.ReadU16(r + 0x32),
        Sz = (short)m.ReadU16(r + 0x34),
    };

    static void WriteFx(IMemory m, uint r, in ObjState e)
    {
        m.WriteU32(r + 0x18, (uint)e.X); m.WriteU32(r + 0x1C, (uint)e.Y); m.WriteU32(r + 0x20, (uint)e.Z);
        m.WriteU16(r + 0x28, (ushort)e.Ax); m.WriteU16(r + 0x2A, (ushort)e.Ay); m.WriteU16(r + 0x2C, (ushort)e.Az);
        m.WriteU16(r + 0x14, e.Time);
        m.WriteU16(r + 0x30, (ushort)e.Sx); m.WriteU16(r + 0x32, (ushort)e.Sy); m.WriteU16(r + 0x34, (ushort)e.Sz);
    }

    static void CaptureEffects(IMemory m)
    {
        int n = 0;
        while (n < FxMax && m.ReadU8(FxBase + (uint)(n * FxStride)) != 0xFF) n++;
        for (int i = 0; i < n; i++)
        {
            var now = ReadFx(m, FxBase + (uint)(i * FxStride));
            if (_fxProbe && now.Live)
            {
                var sb = new System.Text.StringBuilder($"[fx] #{i} player {(int)m.ReadU32(PlayerPos)},{(int)m.ReadU32(PlayerPos + 8)} yaw {m.ReadU16(GameAddr.Yaw):X3}:");
                for (uint k = 0; k < FxStride; k += 2) sb.Append($" {m.ReadU16(FxBase + (uint)(i * FxStride) + k):X4}");
                Console.WriteLine(sb.ToString());
            }
            bool cont = i < _fxCount && _fxCur[i].Live && now.Live;
            _fxPrev[i] = cont ? _fxCur[i] : now;
            _fxCur[i] = now;
            // Held: close to the player, at the same distance as last tick, and facing
            // the way the player does.
            int rx = now.X - (int)m.ReadU32(PlayerPos), rz = now.Z - (int)m.ReadU32(PlayerPos + 8);
            int dist = (int)Math.Sqrt((double)rx * rx + (double)rz * rz);
            int dyaw = ((now.Ay - (short)m.ReadU16(ViewAngles + 2) + 2048) & 4095) - 2048;
            _fxHeld[i] = cont && dist < 1500 && Math.Abs(dist - _fxDist[i]) < 40 && Math.Abs(dyaw) <= 0x10;
            _fxDist[i] = dist;
        }
        _fxCount = n;
    }

    static void PlaceEffects(IMemory m, double f)
    {
        _fxPlaced = false;
        if (!Entities) return;
        for (int i = 0; i < _fxCount; i++)
        {
            ref var c = ref _fxCur[i];
            ref var p = ref _fxPrev[i];
            if (!c.Live) continue;
            if (_fxHeld[i])
            {
                // The offset from the player at the tick, turned by how far the drawn
                // view has turned since (the tick's blend plus the mouse preview).
                short drawnYaw = (short)(LerpAngle(_viewPrev[1], _viewCur[1], f) + _mouseYaw);
                int d = ((drawnYaw - _viewCur[1] + 2048) & 4095) - 2048;
                double a = d * Math.PI * 2 / 4096, cs = Math.Cos(a), sn = Math.Sin(a);
                double ox = c.X - _playerCur[0], oz = c.Z - _playerCur[2];
                WriteFx(m, FxBase + (uint)(i * FxStride), new ObjState
                {
                    Live = true,
                    X = Lerp(_playerPrev[0], _playerCur[0], f) + (int)Math.Round(ox * cs - oz * sn),
                    Y = Lerp(p.Y, c.Y, f),
                    Z = Lerp(_playerPrev[2], _playerCur[2], f) + (int)Math.Round(ox * sn + oz * cs),
                    Ax = LerpAngle(p.Ax, c.Ax, f),
                    Ay = (short)(c.Ay + d),
                    Az = LerpAngle(p.Az, c.Az, f),
                    Time = c.Time,
                    Sx = c.Sx,
                    Sy = c.Sy,
                    Sz = c.Sz,
                });
                _fxPlaced = true;
                continue;
            }
            if (Math.Abs(c.X - p.X) > WarpDistance || Math.Abs(c.Y - p.Y) > WarpDistance || Math.Abs(c.Z - p.Z) > WarpDistance) continue;
            if (c.X == p.X && c.Y == p.Y && c.Z == p.Z && c.Ax == p.Ax && c.Ay == p.Ay && c.Az == p.Az && c.Time == p.Time
                && c.Sx == p.Sx && c.Sy == p.Sy && c.Sz == p.Sz) continue;
            WriteFx(m, FxBase + (uint)(i * FxStride), new ObjState
            {
                Live = true,
                X = Lerp(p.X, c.X, f),
                Y = Lerp(p.Y, c.Y, f),
                Z = Lerp(p.Z, c.Z, f),
                Ax = LerpAngle(p.Ax, c.Ax, f),
                Ay = LerpAngle(p.Ay, c.Ay, f),
                Az = LerpAngle(p.Az, c.Az, f),
                Time = p.Anim == c.Anim ? LerpStep(p.Time, c.Time, f) : c.Time,
                Sx = (short)Lerp(p.Sx, c.Sx, f),
                Sy = (short)Lerp(p.Sy, c.Sy, f),
                Sz = (short)Lerp(p.Sz, c.Sz, f),
            });
            _fxPlaced = true;
            if (_fxProbe) Console.WriteLine($"[fx] place #{i} f {f:F2} x {Lerp(p.X, c.X, f)} z {Lerp(p.Z, c.Z, f)}");
        }
    }

    static void RestoreEffects(IMemory m)
    {
        if (!_fxPlaced) return;
        for (int i = 0; i < _fxCount; i++)
            if (_fxCur[i].Live) WriteFx(m, FxBase + (uint)(i * FxStride), _fxCur[i]);
        _fxPlaced = false;
    }
}
