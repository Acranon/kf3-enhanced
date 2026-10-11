using System.Globalization;
using RecompOne.Runtime.Memory;

namespace KfIII;

// High frame rate: the debug probes (docs/testing.md). None runs unless its
// KFIII_* switch is set.
public static partial class Smooth
{
    /// The console's 2 MB of RAM, as the RAM diffs read it.
    const uint RamBase = 0x80000000;
    const int RamSize = 0x200000;
    /// Left out of the tick diff: the game's own primitive pools (kf_LayoutPrimPools
    /// lays them out here; Pools moves them above 2 MB) and the stack.
    const uint StockPoolsStart = 0x80116954, StockPoolsEnd = 0x80150000, StackStart = 0x801FE000;

    static readonly bool _pitchProbe = DevSwitch.Is("KFIII_PITCH_PROBE");
    static readonly bool _smoothProbe = DevSwitch.Is("KFIII_SMOOTH_PROBE");
    static long _smoothProbeN, _entProbeN;
    static readonly bool _fxProbe = DevSwitch.Is("KFIII_FX_PROBE");

    // ---- KFIII_SMOOTH_RATE: stock draws, redraws and gate calls per half second ----

    static readonly bool _rateProbe = DevSwitch.Is("KFIII_SMOOTH_RATE");
    static long _rateStock, _rateRedraw, _rateGate;
    static double _rateAt;
    /// When the gate last returned; the time from there to the next stock draw is the
    /// world update's.
    static double _gateLeftMs;
    static double _updateMs, _updateMax;
    static int _updateN;

    static void ReportRate()
    {
        double now = _clock.Elapsed.TotalMilliseconds;
        if (now - _rateAt < 500) return;
        Console.WriteLine($"[KFIII-RATE] {now / 1000:F1}s stock {_rateStock} redraw {_rateRedraw} gate {_rateGate} (per 0.5s) gate->stock avg {(_updateN > 0 ? _updateMs / _updateN : 0):F1} max {_updateMax:F1} ms");
        _updateMs = _updateMax = 0; _updateN = 0;
        _rateStock = _rateRedraw = _rateGate = 0; _rateAt = now;
    }

    // ---- KFIII_POOL_PROBE: the primitive pool descriptors -----------------------------

    static readonly bool _poolProbe = DevSwitch.Is("KFIII_POOL_PROBE");
    static long _poolN;

    static void PoolProbe(IMemory m)
    {
        if (_poolProbe && (++_poolN % 30) == 0)
        {
            for (uint d = GameAddr.PrimPool0; d <= GameAddr.PrimPool1; d += 0xC)
                Console.WriteLine($"[pool] desc {d:X8}: {m.ReadU32(d):X8} {m.ReadU32(d + 4):X8} {m.ReadU32(d + 8):X8}  active {m.ReadU32(GameAddr.PrimPoolActive):X8}");
        }
    }

    // ---- KFIII_ENT_DUMP, KFIII_PEEK: a creature record's halfwords, or four bytes of RAM ----

    static readonly int _entDump = DevSwitch.Int("KFIII_ENT_DUMP", -1);
    static long _entDumpN;
    static readonly uint _peek = uint.TryParse(DevSwitch.Text("KFIII_PEEK"), NumberStyles.HexNumber, null, out var pk) ? pk : 0;

    static void EntityProbes(IMemory m)
    {
        if (_peek != 0 && (_entDumpN % 15) == 0)
            Console.WriteLine($"[KFIII-PEEK] {_peek:X8}: {m.ReadU8(_peek):X2} {m.ReadU8(_peek + 1):X2} {m.ReadU8(_peek + 2):X2} {m.ReadU8(_peek + 3):X2}");
        if (_entDump >= 0 && (++_entDumpN % 15) == 0)
        {
            uint r = EntBase + (uint)(_entDump * EntStride);
            var sb = new System.Text.StringBuilder();
            for (uint o = 0; o < EntStride; o += 2) sb.Append($"{m.ReadU16(r + o):X4} ");
            Console.WriteLine($"[KFIII-ENT] {_entDump}: {sb}");
        }
    }

    // ---- KFIII_TICK_DIFF: halfwords six consecutive ticks changed ------------------------

    static readonly double _tickDiffAt = DevSwitch.Number("KFIII_TICK_DIFF") ?? 0;
    static byte[]? _tickSnap;
    static int _tickDiffN;

    static void TickDiff(IMemory m)
    {
        var now = Snap(m);
        if (_tickSnap != null && _tickDiffN < 6)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < now.Length; i += 2)
            {
                uint a = RamBase + (uint)i;
                if (a >= StockPoolsStart && a < StockPoolsEnd) continue;
                if (a >= StackStart) continue;
                ushort o = BitConverter.ToUInt16(_tickSnap, i), n = BitConverter.ToUInt16(now, i);
                if (o != n) sb.Append($" {a:X8}:{o:X4}>{n:X4}");
            }
            Console.WriteLine($"[KFIII-TICK] {_tickDiffN} {_clock.Elapsed.TotalSeconds:F2}s{sb}");
            _tickDiffN++;
        }
        _tickSnap = now;
    }

    // ---- KFIII_SMOOTH_DIFF: what a redraw changes in RAM, three times ----------------
    //
    // Any value: from the start; a number of seconds over 1: from then. This is how
    // the per-call state in Smooth.TickState.cs was found.

    static readonly bool _diffProbe = DevSwitch.Text("KFIII_SMOOTH_DIFF") is { } dv && dv != "0";
    static readonly double _diffAt = DevSwitch.Number("KFIII_SMOOTH_DIFF") is double da && da > 1 ? da : 0;
    static int _diffDone;

    static byte[] Snap(IMemory m)
    {
        var b = new byte[RamSize];
        for (uint i = 0; i < b.Length; i += 4) BitConverter.TryWriteBytes(b.AsSpan((int)i), m.ReadU32(RamBase + i));
        return b;
    }

    static void Diff(IMemory m, byte[] before)
    {
        var sb = new System.Text.StringBuilder();
        int runs = 0; long start = -1, last = -1;
        for (uint i = 0; i < before.Length; i += 4)
        {
            bool changed = BitConverter.ToUInt32(before, (int)i) != m.ReadU32(RamBase + i);
            if (changed)
            {
                if (start < 0 || i - last > 64) { if (start >= 0) { sb.Append($" {RamBase + start:X8}-{RamBase + last + 3:X8}"); runs++; } start = i; }
                last = i;
            }
        }
        if (start >= 0) { sb.Append($" {RamBase + start:X8}-{RamBase + last + 3:X8}"); runs++; }
        // Lone changed words in full: per-call counters show up as +1 steps.
        for (uint i = 0; i < before.Length; i += 4)
        {
            uint o = BitConverter.ToUInt32(before, (int)i), n = m.ReadU32(RamBase + i);
            if (o == n) continue;
            bool lone = (i < 8 || BitConverter.ToUInt32(before, (int)i - 4) == m.ReadU32(RamBase + i - 4))
                     && (i + 4 >= before.Length || BitConverter.ToUInt32(before, (int)i + 4) == m.ReadU32(RamBase + i + 4));
            if (lone) sb.Append(Environment.NewLine).Append($"    {RamBase + i:X8}: {o:X8} -> {n:X8}");
        }
        Console.WriteLine($"[KFIII-SMOOTH] redraw changed {runs} ranges:{sb}");
    }
}
