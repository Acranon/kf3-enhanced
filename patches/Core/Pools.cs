using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace KfIII;

/// <summary>
/// Bigger primitive pools. Every frame the game writes its polygons and sprites
/// into one of two pools (descriptors GameAddr.PrimPool0 and PrimPool1, laid out by
/// kf_LayoutPrimPools at 0x80116954, 0x1A000 bytes each) and silently stops drawing
/// when one is full. The console's 4:3 view fits; the widescreen view, its wider
/// cone and the fog fill do not: the last things drawn in a frame (trees, items)
/// vanish, more so while the weapon swings, and dropped parts flicker.
///
/// The port runs with 8 MB of RAM (Program.cs), and this moves both pools above
/// the console's 2 MB, PoolSize bytes each: after the game's own layout, and again
/// before any frame whose pools are the small ones (see <see cref="Check"/>).
/// </summary>
public static class Pools
{
    const uint Desc0 = GameAddr.PrimPool0, Desc1 = GameAddr.PrimPool1, Base0 = GameAddr.PrimPoolBase;
    /// RAM above the console's 2 MB that the game never uses.
    const uint NewBase = 0x80200000, PoolSize = 0x80000;

    static readonly bool _noCheck = DevSwitch.Is("KFIII_POOL_NOCHECK");

    public static void Relocate(IMemory m)
    {
        uint a = NewBase, b = NewBase + PoolSize;
        m.WriteU32(Base0, a);
        m.WriteU32(Desc0, a); m.WriteU32(Desc0 + 4, a + PoolSize); m.WriteU32(Desc0 + 8, a);
        m.WriteU32(Desc1, b); m.WriteU32(Desc1 + 4, b + PoolSize); m.WriteU32(Desc1 + 8, b);
        Console.WriteLine($"[KFIII] primitive pools: 2 x 0x{PoolSize:X} at 0x{NewBase:X8}");
    }

    /// <summary>Post-hook on kf_LayoutPrimPools.</summary>
    public static void AfterLayout(CpuContext c, IMemory m) => Relocate(m);

    /// <summary>
    /// Before a frame (from Smooth): puts the big pools back if they are small. That
    /// happens after a quick save made with the small pools is loaded, and whenever
    /// the game lays them out again from its base with its own 0x1A000, which it does
    /// after a message box (the pools are its scratch while one is up). So the sizes
    /// are checked, not only the addresses.
    /// </summary>
    public static void Check(IMemory m)
    {
        if (_noCheck) return;
        if (m.ReadU32(Desc0) != NewBase || m.ReadU32(Desc0 + 4) != NewBase + PoolSize
            || m.ReadU32(Desc1) != NewBase + PoolSize || m.ReadU32(Desc1 + 4) != NewBase + 2 * PoolSize)
            Relocate(m);
    }
}
