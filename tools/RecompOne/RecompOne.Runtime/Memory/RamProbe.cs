using System.Runtime.CompilerServices;

namespace RecompOne.Runtime.Memory;

/// <summary>
/// Counts guest RAM accesses above the retail 2 MB, per 64 KiB page. With 2 MB of
/// RAM those addresses mirror the low 2 MB; with more they are separate memory, so
/// a game that reads through the mirror changes behaviour when the RAM grows.
/// <c>RECOMPONE_RAM_PROBE=1</c> arms it at boot; otherwise the JIT folds the test away.
/// </summary>
public static class RamProbe
{
    /// <summary>Diagnostic (RECOMPONE_WATCH=hexaddr): log the recompiled callers of writes to
    /// that word, once per distinct call chain.</summary>
    public static readonly uint WatchPhys =
        uint.TryParse(Environment.GetEnvironmentVariable("RECOMPONE_WATCH"), System.Globalization.NumberStyles.HexNumber, null, out var w) ? w & 0x1FFFFFFCu : 0u;
    public static readonly bool On = (Environment.GetEnvironmentVariable("RECOMPONE_RAM_PROBE") is { } v && v.Trim() != "0")
                                     || WatchPhys != 0;

    static readonly HashSet<string> _seen = [];

    const int PageShift = 16;

    /// <summary>Accesses in [2 MB, 8 MB), indexed by (phys - 2 MB) >> 16.</summary>
    public static readonly long[] Pages = new long[(MemoryMap.RamWindow - MemoryMap.RetailRamSize) >> PageShift];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Note(uint phys)
    {
        if (WatchPhys != 0 && (phys & ~3u) == WatchPhys)
        {
            var names = new List<string>();
            foreach (var fr in new System.Diagnostics.StackTrace(1, false).GetFrames())
            {
                var mt = fr.GetMethod();
                if (mt?.DeclaringType?.Namespace is "Recompiled" or "KfII") names.Add(mt.Name);
                if (names.Count == 6) break;
            }
            var key = string.Join(" < ", names);
            lock (_seen) if (_seen.Add(key)) Console.WriteLine($"[watch] {key}");
        }
        uint o = phys - MemoryMap.RetailRamSize;
        if (o < MemoryMap.RamWindow - MemoryMap.RetailRamSize) Pages[o >> PageShift]++;
    }
}
