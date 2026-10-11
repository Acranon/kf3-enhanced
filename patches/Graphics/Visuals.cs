using RecompOne.Runtime;

namespace KfIII;

/// <summary>
/// Picture quality beyond the console's, over the runtime's GTE recovery (the same
/// runtime work Verdite2, the RecompOne port of the first North American King's
/// Field, uses; see its patches/Subpixel.cs and Perspective.cs).
///
/// <para>The GTE computes every projected vertex in 16.16 fixed point, the game keeps
/// only whole pixels, and the console's GPU interpolates textures without depth.
/// <c>Gte.Rtp</c> records the lost fraction and the view depth of each vertex,
/// <see cref="GteVertexMap"/> follows the stored word into the GP0 packet, and the
/// GPU end looks it up again. So at any render scale vertices do not snap to the
/// 320x240 grid (no wobble) and textures do not bend.</para>
///
/// <para>The settings (System > Settings > Enhancements, Enhancements.cs) drive all
/// of this; the KFIII_* switches below override them when set, for testing and
/// comparisons. Render scale is the runtime's own setting (System > Settings >
/// Display, 4x by default).</para>
///
/// <code>
/// KFIII_SUBPIXEL=0       whole-pixel vertices, as the console
/// KFIII_PERSPECTIVE=0    affine textures, as the console
/// KFIII_PRECISE_CULL=0   backface culling on whole pixels, as the console (see Culling)
/// KFIII_SUBPIXEL_FADE=a-b  view depths over which sub-pixel fades to whole pixels (default off)
/// KFIII_TRUECOLOR=0      15-bit shading with the console's 4x4 dither (default: 8 bits a
///                        channel, no dither)
/// KFIII_ANISO=8          texture taps along a receding surface, 1..16 (1 = off): stops
///                        floors crawling with distance
/// KFIII_MIPMAPS=0        no mipmaps for minified textures (the runtime's texture atlas is
///                        4096x4096 for this game's scenes)
/// KFIII_UNCLAMP=1        draw GTE-clamped corners (+-1024) at their true projection when
///                        their depth is at least KFIII_UNCLAMP_MINZ (default off: the
///                        corner wedges it was meant for are libgte's subdivision clip,
///                        see WideCull.BeforeDivide)
/// KFIII_NEARCLIP=0       no near-plane and guard-band clip for polygons with a clamped
///                        corner or over the GPU's size limit (it keeps the last ground
///                        pieces beside the camera from stretching and vanishing)
/// KFIII_WIDESCREEN=16:9  aspect ("1.78", "21:9"; "off" for 4:3): the runtime renders
///                        extra columns either side, nothing is stretched (WideCull, Hud)
/// KFIII_SMOOTHFOG, KFIII_AO*, KFIII_MSAA, KFIII_PERPIXEL, KFIII_ZBUFFER  the runtime's passes
/// KFIII_VISUALS_PROBE=1  every 2 s: how many vertices were recovered, and the passes' counters
/// </code>
/// </summary>
public static class Visuals
{
    /// Set: true unless "0". Unset: null.
    static bool? Flag(string name) => DevSwitch.Text(name) is { } v ? v != "0" : null;

    /// Set to anything but "0".
    static bool On(string name) => DevSwitch.Text(name) is { } v && v != "0";

    /// <summary>The KFIII_* switches, applied over the saved settings (from
    /// Enhancements.Apply) when set.</summary>
    public static void ApplyEnvironment()
    {
        if (Flag("KFIII_SUBPIXEL") is { } sub) GteDepth.Subpixel = sub;
        if (Flag("KFIII_PERSPECTIVE") is { } per) GteDepth.Enabled = per;
        if (Flag("KFIII_PRECISE_CULL") is { } pc) GteDepth.PreciseCull = pc;
        if (Flag("KFIII_UNCLAMP") is { } uc) GteDepth.Unclamp = uc;
        if (Flag("KFIII_NEARCLIP") is { } nc) GteDepth.NearClip = nc;
        if (int.TryParse(DevSwitch.Text("KFIII_UNCLAMP_MINZ"), out var minz)) GteDepth.UnclampMinZ = minz;
        var fade = DevSwitch.Text("KFIII_SUBPIXEL_FADE")?.Split('-');
        if (fade is { Length: 2 } && float.TryParse(fade[0], out var fn) && float.TryParse(fade[1], out var ff))
            (GteDepth.SubpixelFadeNear, GteDepth.SubpixelFadeFar) = (fn, ff);
        if (Flag("KFIII_TRUECOLOR") is { } tc) GteDepth.TrueColor = tc;
        if (int.TryParse(DevSwitch.Text("KFIII_ANISO"), out var aniso)) GteDepth.Anisotropy = Math.Clamp(aniso, 1, 16);
        if (Flag("KFIII_MIPMAPS") is { } mm) GteDepth.Mipmaps = mm;
        if (Flag("KFIII_SMOOTHFOG") is { } sfog) GteDepth.SmoothFog = sfog;
        if (Flag("KFIII_AO") is { } ao) GteDepth.AmbientOcclusion = ao;
        if (DevSwitch.Number("KFIII_AO_RADIUS") is double aor) GteDepth.AoRadius = (float)aor;
        if (DevSwitch.Number("KFIII_AO_STRENGTH") is double aos) GteDepth.AoStrength = (float)aos;
        if (DevSwitch.Is("KFIII_AO_PROBE")) GteDepth.AoProbe = true;
        if (Flag("KFIII_AO_NORMALS") is { } aon) GteDepth.AoNormals = aon;
        if (int.TryParse(DevSwitch.Text("KFIII_AO_RES"), out var aores)) GteDepth.AoResolution = aores;
        if (DevSwitch.Number("KFIII_AO_BIAS") is double aob) GteDepth.AoBias = (float)aob;
        if (int.TryParse(DevSwitch.Text("KFIII_MSAA"), out var msaa)) GteDepth.Msaa = msaa;
        if (Flag("KFIII_PERPIXEL") is { } ppl) GteLightMap.Enabled = ppl;
        if (Flag("KFIII_ZBUFFER") is { } zb) GteDepth.ZBuffer = zb;
        if (DevSwitch.Text("KFIII_WIDESCREEN") is { } ws) RecompOne.Runtime.Hle.Display.WideAspect = ParseAspect(ws);
        Smooth.ApplyEnvironment();
        Water.ApplyEnvironment();
    }

    public static void Install()
    {
        // The sky dome is linked into the third ordering-table slot (two empty
        // entries come first); it projects close round the camera and must not
        // write depth, or the depth test hides everything behind it.
        GteDepth.SkySlots = 2;
        Enhancements.Install();
        Water.Install();
        Console.WriteLine($"[KFIII] visuals: classic {Enhancements.Current.Classic}, subpixel {GteDepth.Subpixel}, perspective {GteDepth.Enabled}, " +
                          $"precise cull {GteDepth.PreciseCull}, true colour {GteDepth.TrueColor}, aniso {GteDepth.Anisotropy}, mipmaps {GteDepth.Mipmaps}, " +
                          $"wide {RecompOne.Runtime.Hle.Display.WideAspect:F3}, fps {Smooth.Describe}");

        // The runtime's RECOMPONE_GTE_CALLERS: after 12 s, the 25 code addresses that
        // used the GTE most. KFIII_GTE_CALLERS_FROM=9.6 forgets what came before that
        // time, to see one screen's callers.
        if (Gte.CallerProbe)
            DevSwitch.Background("kf3-gte-callers", () =>
            {
                if (DevSwitch.Number("KFIII_GTE_CALLERS_FROM") is double from)
                {
                    Thread.Sleep((int)(from * 1000));
                    lock (Gte.Callers) Gte.Callers.Clear();
                    Thread.Sleep(Math.Max(0, 12000 - (int)(from * 1000)));
                }
                else Thread.Sleep(12000);
                lock (Gte.Callers)
                    foreach (var (k, v) in Gte.Callers.OrderByDescending(x => x.Value).Take(25))
                        Console.WriteLine($"[KFIII-GTE] {v * 64,8} {k}");
            });

        if (On("KFIII_PRESENT_PROBE")) RecompOne.Runtime.Hle.GpuHle.PresentVerdictProbe = true;
        if (On("KFIII_VISUALS_PROBE")) DevSwitch.Background("kf3-visuals-probe", Probe);
    }

    /// <summary>An aspect ratio, "16:9" or "1.777": 16:9 when blank; 0 (no margin) for
    /// "off", "0", "4:3" or anything not wider than 4:3.</summary>
    public static float ParseAspect(string? v)
    {
        v = v?.Trim();
        if (string.IsNullOrEmpty(v)) return 16f / 9f;
        if (v is "0" or "off" or "4:3") return 0f;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var p = v.Split(':');
        float a = p.Length == 2 && float.TryParse(p[0], System.Globalization.NumberStyles.Float, inv, out var w)
                  && float.TryParse(p[1], System.Globalization.NumberStyles.Float, inv, out var h) && h > 0 ? w / h
                : float.TryParse(v, System.Globalization.NumberStyles.Float, inv, out var f) ? f : 0f;
        return a > 4f / 3f + 0.001f ? a : 0f;
    }

    /// KFIII_VISUALS_PROBE: the GTE recovery's and the runtime passes' counters every 2 s.
    static void Probe()
    {
        GteDepth.Probe = true;
        while (true)
        {
            Thread.Sleep(2000);
            long hits = GteDepth.Hits, misses = GteDepth.Misses, rec = GteDepth.Recorded;
            long mh = GteVertexMap.Hits, mm = GteVertexMap.Misses;
            long n = GteDepth.OffsetCount;
            double mean = n == 0 ? 0 : GteDepth.Offset / n;
            double rate = hits + misses == 0 ? 0 : 100.0 * hits / (hits + misses);
            double mapRate = mh + mm == 0 ? 0 : 100.0 * mh / (mh + mm);
            Console.WriteLine($"[KFIII-VIS] recorded {rec / 2}/s, depth hits {hits / 2}/s misses {misses / 2}/s ({rate:F1}%), " +
                              $"map {mapRate:F1}%, fractions {n / 2}/s mean {mean:F3} px, faded vertices {GteDepth.FadedVertices}, cull corrected {GteDepth.CullCorrected}, near-clipped {GteDepth.NearClipped}; " +
                              $"widecull passes {WideCull.Passes} added {WideCull.Added} fog {WideCull.FogAdded}; aniso live {GteDepth.AnisotropyLive}, mip live {GteDepth.MipmapsLive} ({GteDepth.MipEntries} entries, {GteDepth.MipFull} refused)");
            if (GteDepth.AoMap is { } aoMap && GteDepth.AoNormalMap is { } normalMap)
            {
                var sb = new System.Text.StringBuilder("[KFIII-AO] occlusion (0-9 dark) | normal disagreement (0-9 = 0..90 deg):").AppendLine();
                for (int row = 0; row < GteDepth.AoMapRows; row++)
                {
                    for (int col = 0; col < GteDepth.AoMapCols; col++) sb.Append((char)('0' + Math.Clamp((int)((1f - aoMap[row * GteDepth.AoMapCols + col]) * 20f), 0, 9)));
                    sb.Append("  ");
                    for (int col = 0; col < GteDepth.AoMapCols; col++) sb.Append((char)('0' + Math.Clamp((int)(normalMap[row * GteDepth.AoMapCols + col] / 10f), 0, 9)));
                    sb.AppendLine();
                }
                Console.Write(sb.ToString());
            }
            GteDepth.WantAoMap = true;
            Console.WriteLine($"[KFIII-VIS] smooth fog prims {GteDepth.SmoothFogPrims / 2}/s, tried {GteDepth.FogTried / 2}, cues {GteDepth.FogCues / 2}, published {GteDepth.FogPubs / 2}, no raw {GteDepth.FogNoRaw / 2}, no colour {GteDepth.FogNoColor / 2} (first corner {GteDepth.FogNoColor0 / 2}, flat {GteDepth.FogFlat / 2}), supported {GteLightMap.Supported}");
            Console.WriteLine($"[KFIII-VIS] msaa {GteDepth.Msaa}: resolves {GteDepth.MsaaResolves / 2}/s, uploads {GteDepth.MsaaUploads / 2}/s");
            GteDepth.MsaaResolves = GteDepth.MsaaUploads = 0;
            GteDepth.SmoothFogPrims = GteDepth.FogTried = GteDepth.FogCues = GteDepth.FogPubs = GteDepth.FogNoRaw = GteDepth.FogNoColor = GteDepth.FogNoColor0 = GteDepth.FogFlat = 0;
            Console.WriteLine($"[KFIII-VIS] ao projection H {GteDepth.ProjH:F0} centre {GteDepth.ProjCx:F1},{GteDepth.ProjCy:F1}, passes {GteDepth.AoPasses}");
            var fog = Gte.FogRegs;
            Console.WriteLine($"[KFIII-VIS] fog: FC {fog.R},{fog.G},{fog.B} DQA {fog.Dqa} DQB 0x{fog.Dqb:X}");
            GteDepth.ResetCounters();
            GteVertexMap.ResetCounters();
            GteDepth.ResetOffsets();
        }
    }
}
