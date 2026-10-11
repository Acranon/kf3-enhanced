using System.Collections.Concurrent;
using System.Diagnostics;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Hardware;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Sdk;

namespace KfIII;

/// <summary>
/// Process-wide test tools, switched on by environment variables (docs/testing.md):
/// the runtime's log channels, scripted pad input, a timed hard reset
/// (KFIII_TEST_RESET=seconds, comma-separated for several) and the game-speed probe.
/// Nothing here runs unless its switch is set.
/// </summary>
public static class TestHarness
{
    /// <summary>Installs every switch that is set. <paramref name="memory"/> returns the
    /// current machine's RAM (a hard reset replaces it).</summary>
    public static void Install(Func<IMemory> memory)
    {
        LogChannels();
        AutoPad();
        DevSwitch.At("KFIII_TEST_RESET", _ => RecompOne.Runtime.Runtime.HardReset());
        if (DevSwitch.Is("KFIII_RATE_PROBE")) DevSwitch.Background("kf3-rate", () => RateProbe(memory));
    }

    /// <summary>KFIII_LOG=bios,cd,gpu,dma,sdk,spu,mdec (or all): the runtime's log channels.</summary>
    static void LogChannels()
    {
        var channels = DevSwitch.List("KFIII_LOG").Select(s => s.ToLowerInvariant()).ToHashSet();
        if (channels.Count == 0) return;
        bool all = channels.Contains("all");
        RecompOne.Runtime.Log.BiosOn = all || channels.Contains("bios");
        RecompOne.Runtime.Log.CdOn = all || channels.Contains("cd");
        RecompOne.Runtime.Log.GpuOn = all || channels.Contains("gpu");
        RecompOne.Runtime.Log.DmaOn = all || channels.Contains("dma");
        RecompOne.Runtime.Log.SdkOn = all || channels.Contains("sdk");
        RecompOne.Runtime.Log.SpuOn = all || channels.Contains("spu");
        RecompOne.Runtime.Log.MdecOn = all || channels.Contains("mdec");
        Console.WriteLine($"[KFIII] log channels: {string.Join(",", channels)}");
    }

    /// <summary>
    /// Scripted pad input, for testing without a person at the keyboard:
    ///
    ///     KFIII_AUTOPAD=12:Cross:150,game+3:Start:150,fdat+10:Triangle:150
    ///
    /// Each step is [anchor+]seconds:button:holdMs. With no anchor the clock is the
    /// process start; "game" counts from GAME.EXE loading and "fdat" from the first
    /// area module, so a script does not drift with movie and loading times. Held
    /// buttons go through Controller.Injected, which is ANDed into the keyboard's state
    /// each poll, so the keyboard still works alongside.
    /// </summary>
    static void AutoPad()
    {
        var script = DevSwitch.Text("KFIII_AUTOPAD");
        if (script == null) return;

        var buttons = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
        {
            ["Select"] = Controller.Select,
            ["Start"] = Controller.Start,
            ["Cross"] = Controller.Cross,
            ["Circle"] = Controller.Circle,
            ["Square"] = Controller.Square,
            ["Triangle"] = Controller.Triangle,
            ["L1"] = Controller.L1,
            ["R1"] = Controller.R1,
            ["L2"] = Controller.L2,
            ["R2"] = Controller.R2,
            ["Up"] = Controller.Up,
            ["Down"] = Controller.Down,
            ["Left"] = Controller.Left,
            ["Right"] = Controller.Right,
        };

        var steps = new List<(string Anchor, double At, double Hold, ushort Bit)>();
        foreach (var step in script.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = step.Split(':');
            if (f.Length != 3 || !buttons.TryGetValue(f[1].Trim(), out var bit))
                throw new ArgumentException($"KFIII_AUTOPAD: bad step '{step}'");
            var when = f[0].Trim();
            int plus = when.IndexOf('+');
            var anchor = plus > 0 ? when[..plus] : "";
            double at = double.Parse(plus > 0 ? when[(plus + 1)..] : when, System.Globalization.CultureInfo.InvariantCulture);
            steps.Add((anchor, at, double.Parse(f[2], System.Globalization.CultureInfo.InvariantCulture) / 1000.0, bit));
        }

        var clock = Stopwatch.StartNew();
        var anchors = new ConcurrentDictionary<string, double> { [""] = 0 };
        Event.AddListener<OverlayLoadedEvent>(e =>
        {
            var key = e.Name.StartsWith("fdat", StringComparison.Ordinal) ? "fdat" : e.Name;
            if (anchors.TryAdd(key, clock.Elapsed.TotalSeconds))
                Console.WriteLine($"[KFIII] autopad anchor {key} at {clock.Elapsed.TotalSeconds:F1}s");
        });

        DevSwitch.Background("kf3-autopad", () =>
        {
            Console.WriteLine($"[KFIII] autopad: {steps.Count} step(s) armed");
            int last = 0xFFFF;
            while (true)
            {
                double now = clock.Elapsed.TotalSeconds;
                int mask = 0xFFFF;
                foreach (var (anchor, at, hold, bit) in steps)
                {
                    if (!anchors.TryGetValue(anchor, out var t0)) continue;
                    double t = now - t0;
                    if (t >= at && t < at + hold) mask &= ~bit;
                }

                // Only on a change, so another writer (the quick-load autopilot) is
                // not overwritten every millisecond.
                if (mask != last)
                {
                    Controller.Injected = (ushort)mask;
                    Console.WriteLine($"[KFIII] autopad t={now:F1}s state=0x{mask:X4}");
                    last = mask;
                }
                Thread.Sleep(1);
            }
        });
    }

    /// <summary>
    /// KFIII_RATE_PROBE=1: a line a second with how far the game's own vblank counter
    /// moved (60 on hardware), the VSync calls the runtime presented, movie frames,
    /// sound key-ons and the game's draws.
    /// </summary>
    static void RateProbe(Func<IMemory> memory)
    {
        uint lastVb = 0, lastDraw = 0;
        long lastCalls = 0, lastFrames = 0, lastKeyOns = 0;
        var serials = new int[24];
        while (true)
        {
            Thread.Sleep(1000);
            var m = memory();
            uint vb = m.ReadU32(GameAddr.VblankTotal);
            uint draw = m.ReadU32(GameAddr.RenderCounter);
            long calls = LibEtc.VSyncCalls;
            long frames = LibCdStream.FramesDelivered;
            long keyOns = 0;
            var spu = RecompOne.Runtime.Runtime.Spu;
            if (spu != null)
            {
                spu.CopyKeyOnSerials(serials);
                foreach (var k in serials) keyOns += k;
            }
            Console.WriteLine($"[KFIII-RATE] game vblanks/s={vb - lastVb} vsync calls/s={calls - lastCalls} " +
                              $"movie frames/s={frames - lastFrames} sectors/frame={LibCdStream.LastFrameSectors} " +
                              $"key-ons/s={keyOns - lastKeyOns} game draws/s={draw - lastDraw}");
            (lastVb, lastDraw, lastCalls, lastFrames, lastKeyOns) = (vb, draw, calls, frames, keyOns);
        }
    }
}
