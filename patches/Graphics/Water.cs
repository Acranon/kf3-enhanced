using RecompOne.Runtime;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace KfIII;

/// <summary>
/// Water: a seamless texture, smooth flow and reflections (Enhancements "Seamless
/// water" and "Water reflections"; smooth flow follows the frame rate).
///
/// <para><b>Seamless texture.</b> The river is a few large semi-transparent
/// (average blend) polygons textured from tpage 0x000F (4-bit, x 960), uv
/// 224..255 x 96..127, clut 0x79CC: VRAM halfwords (1016,96), 8x32. Their mapping is
/// authored per face in the map's tile meshes, each face turned its own way, which
/// shows as seams. The runtime's WaterUv maps them from world X/Z instead (the camera
/// comes from <see cref="Ground"/>), keeping each face's flow direction. The texture
/// is published as <see cref="SurfaceMaterial.Water"/> for the reflection passes
/// (ScreenReflections, and Mirror's planar reflections).</para>
///
/// <para><b>Smooth flow</b> blends the texture's once-a-tick scroll across the
/// high-frame-rate redraws (<see cref="Moved"/>, <see cref="Place"/>).</para>
///
/// <code>
/// KFIII_WATER_WORLD=0/1   seamless water
/// KFIII_WATER_PERIOD=2048 world units per texture repeat (one tile)
/// KFIII_WATER_FLOW=0      V runs along: 0 each face's own direction, 1 world Z, 2 world X
/// KFIII_WATER_SMOOTH=0/1  smooth flow between ticks
/// KFIII_SSR=0/1           screen-space reflections on water
/// KFIII_SSR_STRENGTH, KFIII_SSR_F0, KFIII_SSR_STEPS, KFIII_SSR_RES, KFIII_SSR_THICKNESS,
/// KFIII_SSR_DISTANCE      the reflection pass's parameters
/// KFIII_WATER_FLOW_PROBE=1, KFIII_WATER_CORNERS=1, KFIII_SSR_PROBE=1   diagnostics
/// KFIII_CREATURE_WATER=1  test: leave creatures untagged (they take the water's effects)
/// </code>
/// </summary>
public static class Water
{
    /// <summary>
    /// From Culling.Ground (before each ground routine): the camera the ground is drawn
    /// with, for WaterUv. Each map tile is placed at one of four quarter turns, and the
    /// routine's GTE rotation is the camera's times the tile's; only a rotation whose
    /// first row is the camera's heading (cos, 0, sin of the yaw kf_Render was given) is
    /// the camera's own. Taking the last one regardless would map the water from the
    /// camera rather than the world whenever the last tile drawn was turned, and the
    /// texture would follow the player.
    /// </summary>
    public static void Ground(IMemory m)
    {
        if (!WaterUv.Enabled && !PlanarReflections.Enabled) return;
        var r = Gte.Rotation;
        if (Smooth.AnglePtr != 0)
        {
            double yaw = (short)m.ReadU16(Smooth.AnglePtr + 2) * (Math.PI * 2 / 4096);
            double dot = (r[0] * Math.Cos(yaw) + r[2] * Math.Sin(yaw)) / 4096.0;
            if (dot < 0.7) return;
        }
        if (Mirror.Active) { Mirror.NoteRotation(r); return; }
        uint cam = Smooth.CameraPtr != 0 ? Smooth.CameraPtr : GameAddr.PlayerPos;
        WaterUv.SetCamera(r, (int)m.ReadU32(cam), (int)m.ReadU32(cam + 8));
        Mirror.NoteCamera(r, (int)m.ReadU32(cam + 4));
    }

    // ---- creatures are not water ----------------------------------------------
    //
    // The slimes' translucent skins are cut from the water's texels in the same
    // averaging blend, so they would take the water's reflections and seamless mapping
    // (tests/saves/slimes.kfstate). While kf_DrawModel runs for the creature table
    // (called from kf_DrawObjects at 0x80040D24; the other callers draw props and
    // effects), every vertex the GTE projects is tagged, and the GPU gives those
    // polygons SurfaceMaterial.Creature instead.

    /// The return address of kf_DrawObjects' call that draws creatures.
    const uint CreatureDrawReturn = 0x80040D2C;
    static readonly bool _creatureWater = DevSwitch.Is("KFIII_CREATURE_WATER");

    /// <summary>Pre-hook on kf_DrawModel: tags a creature's vertices.</summary>
    public static void BeforeDrawModel(CpuContext c, IMemory m)
    {
        if (c.RA == CreatureDrawReturn && !_creatureWater) GteVertexMap.CurrentTag = GteVertexMap.TagCreature;
    }

    /// <summary>Post-hook on kf_DrawModel.</summary>
    public static void AfterDrawModel(CpuContext c, IMemory m)
        => GteVertexMap.CurrentTag = 0;

    // ---- smooth flow ----------------------------------------------------------
    //
    // Every tick the game copies its water source (GameAddr.WaterSrcX/Y, 8x32) into
    // the water's square (GameAddr.WaterDstX/Y) shifted down a row: two MoveImages,
    // the one from the source's first row landing at row k. Between ticks the
    // high-frame-rate redraws would show the texture standing still for four frames
    // and then jumping; the runtime's fluid slot (GteDepth.Fluid) blends between rows
    // instead, offset by Off texels.

    const int SrcX = GameAddr.WaterSrcX, SrcY = GameAddr.WaterSrcY, DstX = GameAddr.WaterDstX, DstY = GameAddr.WaterDstY;
    const int Rows = 32;
    /// The row the source's first row was last copied to (-1 before the first copy),
    /// and the last non-zero change of it, in rows, -16..15.
    static int _shift = -1, _shiftStep;
    public static bool SmoothFlow = true;
    static readonly bool _flowProbe = DevSwitch.Is("KFIII_WATER_FLOW_PROBE");

    /// <summary>From VramDiag.MoveImage: a VRAM copy (source rect, destination x/y).</summary>
    public static void Moved(int sx, int sy, int w, int h, int dx, int dy)
    {
        if (sx != SrcX || sy != SrcY || dx != DstX || dy < DstY || dy >= DstY + Rows) return;
        int k = dy - DstY;
        if (_shift >= 0)
        {
            int d = ((k - _shift) % Rows + Rows) % Rows;
            if (d >= Rows / 2) d -= Rows;
            if (d != 0) _shiftStep = d;
        }
        if (_flowProbe) Console.WriteLine($"[KFIII] water flow: copy k {k} step {_shiftStep}");
        _shift = k;
    }

    /// <summary>From Smooth.BeforeRender, before every draw; f is how far into the tick
    /// this picture is. The copy is made inside the draw, from the game's animation
    /// counter, which the stock draw advances after the copy: so the stock draw still
    /// shows the last tick's rows and every redraw after it the new ones, a whole step
    /// on. Off takes the redraws back to step * f past the stock draw's rows.</summary>
    public static void Place(double f, bool smoothing, bool stock)
    {
        if (!SmoothFlow || !smoothing || _shift < 0 || _shiftStep == 0) { GteDepth.FluidN = 0; return; }
        GteDepth.Fluid[0] = new GteDepth.FluidRec { X = DstX, Y = DstY, W = 8, H = Rows, Off = stock ? 0f : (float)(_shiftStep * (1.0 - f)) };
        GteDepth.FluidN = 1;
        WaterUv.Frame++;
        if (_flowProbe) Console.WriteLine($"[KFIII] water flow: {(stock ? "stock" : "redraw")} f {f:F2}, k {_shift} off {GteDepth.Fluid[0].Off:F2}");
    }

    // ---- reflections ------------------------------------------------------------

    /// Per reflection level (off, low, high): reflectivity at a grazing angle, and
    /// looking straight down.
    static readonly (float Max, float F0)[] _levels = [(0f, 0f), (0.6f, 0.25f), (0.9f, 0.5f)];

    /// <summary>From Enhancements.Apply.</summary>
    public static void Apply(bool on, bool seamless, int reflections)
    {
        WaterUv.Enabled = on && seamless;
        SmoothFlow = on;
        if (!on) GteDepth.FluidN = 0;
        int l = on ? Math.Clamp(reflections, 0, 2) : 0;
        ScreenReflections.Enabled = l > 0;
        Mirror.Apply(l > 0);
        SurfaceMaterial.Reflectivity[SurfaceMaterial.Water] = _levels[l].Max;
        SurfaceMaterial.F0[SurfaceMaterial.Water] = _levels[l].F0;
        // 32 steps at half resolution leave stripes in the near water; 64 at the
        // render scale do not.
        ScreenReflections.Steps = 64;
        ScreenReflections.Resolution = 0;
    }

    /// <summary>The KFIII_* switches, over the settings (from Visuals.ApplyEnvironment
    /// and <see cref="Install"/>).</summary>
    public static void ApplyEnvironment()
    {
        if (DevSwitch.Number("KFIII_WATER_PERIOD") is double per && (float)per > 0f) WaterUv.Period = (float)per;
        if (int.TryParse(DevSwitch.Text("KFIII_WATER_FLOW"), out var flow)) WaterUv.Flow = flow;
        if (DevSwitch.Text("KFIII_WATER_WORLD") is { } ww) WaterUv.Enabled = ww != "0";
        if (DevSwitch.Text("KFIII_WATER_SMOOTH") is { } sm) SmoothFlow = sm != "0";
        if (DevSwitch.Text("KFIII_SSR") is { } on) ScreenReflections.Enabled = on != "0";
        if (DevSwitch.Number("KFIII_SSR_STRENGTH") is double st) SurfaceMaterial.Reflectivity[SurfaceMaterial.Water] = (float)st;
        if (DevSwitch.Number("KFIII_SSR_F0") is double f0) SurfaceMaterial.F0[SurfaceMaterial.Water] = (float)f0;
        if (int.TryParse(DevSwitch.Text("KFIII_SSR_STEPS"), out var steps)) ScreenReflections.Steps = Math.Clamp(steps, 1, 128);
        if (int.TryParse(DevSwitch.Text("KFIII_SSR_RES"), out var rs)) ScreenReflections.Resolution = rs;
        if (DevSwitch.Number("KFIII_SSR_THICKNESS") is double th) ScreenReflections.Thickness = (float)th;
        if (DevSwitch.Number("KFIII_SSR_DISTANCE") is double di) ScreenReflections.MaxDistance = (float)di;
        // Reflections switched on by KFIII_SSR with the setting off: the low level's strength.
        if (ScreenReflections.Enabled && SurfaceMaterial.Reflectivity[SurfaceMaterial.Water] <= 0f)
        {
            SurfaceMaterial.Reflectivity[SurfaceMaterial.Water] = _levels[1].Max;
            SurfaceMaterial.F0[SurfaceMaterial.Water] = _levels[1].F0;
        }
        ScreenReflections.Probe = DevSwitch.Is("KFIII_SSR_PROBE");
    }

    public static void Install()
    {
        SurfaceMaterial.Rects[0] = new SurfaceMaterial.TexRect
        {
            X = DstX,
            Y = DstY,
            W = 8,
            H = Rows,
            Material = SurfaceMaterial.Water,
            TranslucentOnly = true,
        };
        SurfaceMaterial.RectN = 1;
        WaterUv.Clut = 0x79CC; WaterUv.TexPage = 0x000F;
        WaterUv.U0 = 224; WaterUv.V0 = 96; WaterUv.Size = 32;
        ApplyEnvironment();
        Console.WriteLine($"[KFIII] water: seamless {WaterUv.Enabled}, smooth flow {SmoothFlow}, reflections {(ScreenReflections.Enabled ? SurfaceMaterial.Reflectivity[SurfaceMaterial.Water].ToString("F2") : "off")}");
        // KFIII_WATER_CORNERS: the water's world corners (/4) once a second.
        if (DevSwitch.Is("KFIII_WATER_CORNERS"))
            DevSwitch.Background("kf3-water-corners", () =>
            {
                WaterUv.Record = true;
                while (true)
                {
                    Thread.Sleep(1000);
                    string line;
                    lock (WaterUv.Corners)
                    {
                        line = string.Join(" ", WaterUv.Corners.OrderBy(c => c.Item1).ThenBy(c => c.Item2).Take(14).Select(c => $"{c.Item1},{c.Item2}"));
                        WaterUv.Corners.Clear();
                    }
                    Console.WriteLine($"[KFIII] water corners: {line}");
                }
            });
        // KFIII_SSR_PROBE: the reflection pass's and the water mapping's counters every 2 s.
        if (ScreenReflections.Probe)
            DevSwitch.Background("kf3-ssr-probe", () =>
            {
                while (true)
                {
                    ScreenReflections.WantMap = true;
                    Thread.Sleep(2000);
                    Console.WriteLine($"[KFIII] water: passes {ScreenReflections.Passes / 2}/s, no target {ScreenReflections.NoTarget / 2}/s, " +
                                      $"water tris {SurfaceMaterial.ByMaterial[SurfaceMaterial.Water] / 2}/s, blended {SurfaceMaterial.Blended / 2}/s, " +
                                      $"refused {string.Join('/', SurfaceMaterial.RefusedByBlend)}; reflective {ScreenReflections.ReflectivePct:F1}% " +
                                      $"hit {ScreenReflections.HitPct:F1}% sky {ScreenReflections.SkyPct:F1}% weight {ScreenReflections.MeanWeight:F3} " +
                                      $"hitLuma {ScreenReflections.HitLuma:F0} keep {ScreenReflections.HitKeep:F2}");
                    Console.WriteLine($"[KFIII] water: world uv {WaterUv.Mapped / 2}/s, no view {WaterUv.NoView / 2}/s, orient +Z {WaterUv.Orient[0]} -Z {WaterUv.Orient[1]} +X {WaterUv.Orient[2]} -X {WaterUv.Orient[3]}");
                    WaterUv.Mapped = WaterUv.NoView = 0; Array.Clear(WaterUv.Orient);
                    SurfaceMaterial.ResetCounters();
                    ScreenReflections.ResetCounters();
                }
            });
    }
}
