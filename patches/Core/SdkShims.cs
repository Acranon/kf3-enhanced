using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace KfIII;

/// <summary>
/// SDK entry points whose contract in this game's libcd differs from the one the
/// runtime's HLE implements. Each replaces the routine in all three executables,
/// bound by address through scripts/name_sdk.py's SHIMS (config/kf3.json patches[]).
/// </summary>
public static class SdkShims
{
    /// <summary>
    /// Replaces this game's CdRead, which differs from the PSY-Q CdRead the runtime
    /// implements in two ways:
    /// <list type="bullet">
    /// <item>Its arguments are (buf, sectors, mode), not (sectors, buf, mode). The
    /// read start routine (0x8001CC9C in OPEN.EXE) copies a0 into the running buffer
    /// pointer (0x8003E80C, advanced by the sector size) and a1 into the
    /// sectors-remaining count (0x8003E818, decremented per sector). Passed through
    /// unswapped, the runtime reads about 0x800C8954 sectors to address 1, every
    /// archive table comes up empty, and the checksum runs off the end of RAM.</item>
    /// <item>It returns <c>-(result &lt; 1)</c>: 0 on success, -1 on failure. The
    /// runtime's 1 for success reads as a failure to the game's retry wrappers
    /// (0x8001B4A4 in OPEN.EXE, 0x80062284 in GAME.EXE), which then retry forever.</item>
    /// </list>
    /// </summary>
    public static void CdRead(CpuContext c, IMemory m)
    {
        (c.A0, c.A1) = (c.A1, c.A0);
        RecompOne.Runtime.Sdk.LibCd.CdRead(c, m);
        c.V0 = c.V0 != 0 ? 0u : 0xFFFFFFFFu;
    }
}
