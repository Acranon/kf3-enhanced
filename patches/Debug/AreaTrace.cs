using RecompOne.Runtime.Events;
using RecompOne.Runtime.Memory;

namespace KfIII;

/// <summary>
/// Traces for finding the code and the variables behind an area change (walking
/// through an exit): what makes the game load another area module.
/// <list type="bullet">
/// <item>KFIII_AREA_STACK=1 logs the recompiled call stack each time an area module
/// (fdat*) loads, which names the code that started the load.</item>
/// <item>KFIII_AREA_DIFF=1 logs the player's position each world tick, and when an
/// area module loads, the RAM bytes that changed in each of the last six ticks
/// after holding steady before it: the game variables that led to the load.</item>
/// </list>
/// </summary>
public static class AreaTrace
{
    // The KFIII_AREA_DIFF window: game globals, the creature and prop tables
    // included. A ring of 32 snapshots, one a tick.
    const uint DiffLo = 0x80180000, DiffHi = 0x801C2000;
    const int Snaps = 32;

    static readonly bool _diff = DevSwitch.Is("KFIII_AREA_DIFF");
    static readonly byte[][] _snaps = new byte[Snaps][];
    static int _snapHead, _snapCount;
    static volatile bool _diffReport;
    static int _lastPx, _lastPz;

    /// <summary>Registers the area-load listeners whose switches are set.</summary>
    public static void Install()
    {
        if (DevSwitch.Is("KFIII_AREA_STACK"))
            Event.AddListener<OverlayLoadedEvent>(e =>
            {
                if (!e.Name.StartsWith("fdat")) return;
                var frames = Environment.StackTrace.Split('\n').Where(l => l.Contains("func_") || l.Contains("kf_")).Take(14);
                Console.WriteLine($"[area] {e.Name} loaded from:\n" + string.Join("\n", frames));
            });
        if (_diff)
            Event.AddListener<OverlayLoadedEvent>(e =>
            {
                if (e.Name.StartsWith("fdat")) _diffReport = true;
            });
    }

    /// <summary>Once per world tick (from Automap's kf_WorldUpdate hook, through AudioRecord.Snapshot).</summary>
    public static void Snapshot(IMemory m)
    {
        if (!_diff) return;
        if (_diffReport)
        {
            _diffReport = false;
            if (_snapCount >= 12) Report();
            _snapCount = 0;
        }
        // The player's position each tick: the exit is where the last ones are.
        int px = (int)m.ReadU32(GameAddr.PlayerPos), pz = (int)m.ReadU32(GameAddr.PlayerPos + 8);
        if (px != _lastPx || pz != _lastPz)
        {
            Console.WriteLine($"[pos] {px / 256f:F1},{pz / 256f:F1} tile {px >> 11},{pz >> 11} (raw {px},{pz})");
            _lastPx = px; _lastPz = pz;
        }
        var buf = _snaps[_snapHead] ??= new byte[DiffHi - DiffLo];
        for (uint a = 0; a < buf.Length; a++) buf[a] = m.ReadU8(DiffLo + a);
        _snapHead = (_snapHead + 1) & (Snaps - 1);
        _snapCount++;
    }

    /// <summary>
    /// For each of the last six ticks, the bytes that changed in it after holding
    /// the same value for the eight ticks before. Changed bytes are grouped into
    /// runs (gaps under 8 bytes join a run): runs of up to six are listed byte by
    /// byte as variables, longer ones as one line (a buffer). At most 120 runs a tick.
    /// </summary>
    static void Report()
    {
        for (int lag = 1; lag <= 6; lag++)
        {
            var now = _snaps[(_snapHead + Snaps - lag) & (Snaps - 1)];
            var before = _snaps[(_snapHead + Snaps - lag - 1) & (Snaps - 1)];
            var sb = new System.Text.StringBuilder($"[diff] tick -{lag}:\n");
            int n = 0;
            int runStart = -1, runEnd = -1, runN = 0;
            void Flush()
            {
                if (runStart < 0) return;
                if (runN <= 6)
                    for (int q = runStart; q <= runEnd; q++)
                        if (before[q] != now[q]) sb.Append($"  {DiffLo + q:X8}: {before[q]:X2} -> {now[q]:X2}\n");
                if (runN > 6) sb.Append($"  {DiffLo + runStart:X8}..{DiffLo + runEnd:X8}: {runN} bytes (buffer)\n");
                runStart = -1; runN = 0; n++;
            }
            for (int k = 0; k < now.Length && n < 120; k++)
            {
                byte old = before[k];
                if (old == now[k]) continue;
                bool steady = true;
                for (int s = 2; s <= 8 && steady; s++) steady = _snaps[(_snapHead + Snaps - lag - s) & (Snaps - 1)][k] == old;
                if (!steady) continue;
                if (runStart >= 0 && k - runEnd >= 8) Flush();
                if (runStart < 0) runStart = k;
                runEnd = k; runN++;
            }
            Flush();
            Console.WriteLine(sb.ToString());
        }
    }
}
