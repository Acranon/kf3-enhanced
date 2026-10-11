using RecompOne.Runtime;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace KfIII;

/// <summary>
/// Precise backface culling and per-routine depth settings for the game's polygon
/// routines.
///
/// <para><b>Culling.</b> Every routine below culls a triangle when NCLIP &lt;= 0.
/// Distant geometry includes slivers that are flat in whole pixels, so NCLIP gives
/// 0 and they are dropped. On the console their neighbours close over them on the
/// same pixel row, but with sub-pixel corners the gaps show as black lines across
/// the ground or missing pieces of far objects. So each routine tells the GTE where
/// it keeps the three vertex words it loads into SXY0/1/2 before the NCLIP, and the
/// cull is decided on the sub-pixel positions the triangle is drawn at
/// (Gte.PreciseCullCorner; on while GteDepth.PreciseCull, KFIII_PRECISE_CULL=0 off).</para>
///
/// <code>
/// kf_DrawGround, kf_DrawGroundNear   map tiles: pointers in the scratchpad (GameAddr.GroundCornerPtrs)
/// kf_DrawModelPolys                  object models (from kf_DrawModel): addresses in t0, t1, t2
/// kf_DrawModelPolys2                 object models (from kf_DrawPropModel): addresses in t2, t1, t3
/// kf_DrawModelPolys3                 object models (from kf_DrawModel; the crystal tree):
///                                    pointers in the scratchpad
/// </code>
///
/// <para><b>Depth.</b> While each routine runs, it also sets how its depth is
/// published to the depth buffer (Gte.DepthScale) and, for the ground, the fog range
/// for smooth fog. Every pre-hook is paired with <see cref="Exit"/> as its post-hook.</para>
/// </summary>
public static class Culling
{
    static readonly Func<int, uint> _scratch = i => Runtime.Mem!.ReadU32(GameAddr.GroundCornerPtrs + (uint)i * 4u);
    static readonly Func<int, uint> _t012 = i => i switch { 0 => Runtime.Cpu!.T0, 1 => Runtime.Cpu!.T1, _ => Runtime.Cpu!.T2 };
    static readonly Func<int, uint> _t213 = i => i switch { 0 => Runtime.Cpu!.T2, 1 => Runtime.Cpu!.T1, _ => Runtime.Cpu!.T3 };

    // Restored on exit rather than cleared, in case one of these runs inside another.
    [ThreadStatic] static Stack<(Func<int, uint>? Corner, float Scale, float FogStart, float FogEnd)>? _saved;

    /// <summary>The ground's published depth is scaled by this (Gte.DepthScale), so
    /// items and creatures standing slightly sunk into it are not cut off by the depth
    /// buffer (a helmet would lose its lower half). 1.04 is about a tenth of a tile at
    /// close range. KFIII_GROUND_DEPTH overrides it (1 turns it off).</summary>
    public static float GroundDepthScale = DevSwitch.Number("KFIII_GROUND_DEPTH") is double g ? (float)g : 1.04f;

    static void Save() =>
        (_saved ??= new()).Push((Gte.PreciseCullCorner, Gte.DepthScale, Gte.FogLinearStart, Gte.FogLinearEnd));

    static void Enter(Func<int, uint> corner, float scale)
    {
        Save();
        Gte.PreciseCullCorner = corner;
        Gte.DepthScale = scale;
    }

    /// <summary>Pre-hook on kf_DrawGround and kf_DrawGroundNear. Also hands the ground's
    /// camera to Water, and sets the fog range for smooth fog: each corner's IR0 is
    /// the game's own 4096 * (SZ - start) / (end - start), clamped to 0..0x1F0F; a
    /// start of 0x7D00 or more is no fog.</summary>
    public static void Ground(CpuContext c, IMemory m)
    {
        Enter(_scratch, GroundDepthScale);
        Water.Ground(m);
        // The routine copies the fog range into the scratchpad (GameAddr.ScratchFogStart)
        // on entry, so before it runs the copy still holds the previous call's: read the
        // source. With the stale copy, wall pieces drawn after a differently fogged call
        // would get a raw fog in the millions and draw black.
        int start = (int)m.ReadU32(GameAddr.FogStart), end = (int)m.ReadU32(GameAddr.FogEnd);
        if (start < 0x7D00 && end > start) { Gte.FogLinearStart = start; Gte.FogLinearEnd = end; }
        else { Gte.FogLinearStart = 0f; Gte.FogLinearEnd = 0f; }
    }

    // Models keep the depth scale they were called under (the weapon's, below).

    /// <summary>Pre-hook on kf_DrawModelPolys.</summary>
    public static void Model(CpuContext c, IMemory m) => Enter(_t012, Gte.DepthScale);
    /// <summary>Pre-hook on kf_DrawModelPolys2.</summary>
    public static void Model2(CpuContext c, IMemory m) => Enter(_t213, Gte.DepthScale);
    /// <summary>Pre-hook on kf_DrawModelPolys3.</summary>
    public static void Model3(CpuContext c, IMemory m) => Enter(_scratch, Gte.DepthScale);

    /// <summary>Pre-hook on kf_DrawWeapon: the weapon in hand is pulled to a quarter of
    /// its depth. On the console it is drawn over everything; with the depth buffer a
    /// swing next to a wall would go into it.</summary>
    public static void Weapon(CpuContext c, IMemory m)
    {
        Save();
        Gte.DepthScale = 0.25f;
    }

    /// <summary>Post-hook on every routine above: puts back what its pre-hook changed.</summary>
    public static void Exit(CpuContext c, IMemory m)
    {
        if (_saved is { Count: > 0 } s) (Gte.PreciseCullCorner, Gte.DepthScale, Gte.FogLinearStart, Gte.FogLinearEnd) = s.Pop();
        else { Gte.PreciseCullCorner = null; Gte.DepthScale = 1f; Gte.FogLinearStart = Gte.FogLinearEnd = 0f; }
    }
}
