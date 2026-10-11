using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace KfIII;

// What the player has seen is kept per area module in automap\<fdatNN>.seen, under the
// working directory: the seen bits (one per sample), then one bit per tile for water and
// one for lava. It is saved on leaving an area, every 5 s while it changes, when the full
// map closes and on exit. The samples themselves are not saved: they are taken again
// from the game as they are needed.
public static partial class Automap
{
    const int SeenBytes = Size * Size / 8, TileBitBytes = Span * Span / 8;

    static long _savedAt;
    static bool _pendingTiles;

    static string PathOf(Area a) => Path.Combine(Environment.CurrentDirectory, "automap", a.Name + ".seen");

    public static void SaveNow() { if (Current != null) Save(Current); }

    static void Save(Area a)
    {
        _savedAt = Environment.TickCount64;
        if (!a.Dirty) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathOf(a))!);
            var bits = new byte[SeenBytes + 2 * TileBitBytes];
            for (int i = 0; i < a.Seen.Length; i++) if (a.Seen[i]) bits[i >> 3] |= (byte)(1 << (i & 7));
            for (int t = 0; t < a.WaterTile.Length; t++) if (a.WaterTile[t]) bits[SeenBytes + (t >> 3)] |= (byte)(1 << (t & 7));
            for (int t = 0; t < a.LavaTile.Length; t++) if (a.LavaTile[t]) bits[SeenBytes + TileBitBytes + (t >> 3)] |= (byte)(1 << (t & 7));
            File.WriteAllBytes(PathOf(a), bits);
            a.Dirty = false;
        }
        catch (Exception e) { Console.WriteLine($"[KFIII] automap save: {e.Message}"); }
    }

    /// <summary>Seen bits from an earlier run (files without the water or lava bits
    /// are read too). The tiles under them are sampled from the tick by SamplePending.</summary>
    static void Load(Area a)
    {
        try
        {
            if (!File.Exists(PathOf(a))) return;
            var bits = File.ReadAllBytes(PathOf(a));
            if (bits.Length < SeenBytes) return;
            for (int i = 0; i < a.Seen.Length; i++)
                if ((bits[i >> 3] & (1 << (i & 7))) != 0) a.Reveal(i);
            if (bits.Length >= SeenBytes + TileBitBytes)
                for (int t = 0; t < a.WaterTile.Length; t++)
                    if ((bits[SeenBytes + (t >> 3)] & (1 << (t & 7))) != 0) a.WaterTile[t] = a.WaterChecked[t] = true;
            if (bits.Length >= SeenBytes + 2 * TileBitBytes)
                for (int t = 0; t < a.LavaTile.Length; t++)
                    if ((bits[SeenBytes + TileBitBytes + (t >> 3)] & (1 << (t & 7))) != 0) a.LavaTile[t] = a.WaterChecked[t] = a.AnyLava = true;
            a.Dirty = false;
            _pendingTiles = true;
        }
        catch (Exception e) { Console.WriteLine($"[KFIII] automap load: {e.Message}"); }
    }

    /// <summary>From the tick, after a load: samples the tiles under loaded seen bits,
    /// up to 200 a tick (the view draws nothing for an unsampled sample).</summary>
    public static void SamplePending(CpuContext c, IMemory m)
    {
        if (!_pendingTiles || Current is not { } a) return;
        int budget = 200;
        for (int t = 0; t < Span * Span && budget > 0; t++)
        {
            if (a.Sampled[t]) continue;
            int tx = t % Span, tz = t / Span;
            bool any = false;
            for (int sz = 0; sz < PerTile && !any; sz++)
                for (int sx = 0; sx < PerTile && !any; sx++)
                    any = a.Seen[(tz * PerTile + sz) * Size + tx * PerTile + sx];
            if (!any) continue;
            SampleTile(c, m, a, tx, tz);
            budget--;
        }
        if (budget > 0) _pendingTiles = false;
    }
}
