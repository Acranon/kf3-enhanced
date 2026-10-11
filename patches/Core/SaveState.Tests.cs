using RecompOne.Runtime.Dispatch;

namespace KfIII;

// Quick save's test switches, and the F9 sound report (a player's "a sound just
// didn't play" button that writes what the SPU was asked to play).
public static partial class SaveState
{
    static long _firstUpdateMs = -1;
    static string? _testMode;
    static double _testAt;

    /// <summary>
    /// KFIII_TEST_QUICK=save@N,load@N (either or both): "save@N" saves N seconds after
    /// the first world update; "load@N" presses F4 N seconds after start (a load from
    /// the title). KFIII_TEST_SOUNDREPORT=N presses F9 N seconds after start.
    /// KFIII_TEST_VOICEDUMP=N prints libsnd's voice table and the SPU's envelope
    /// levels N seconds after start.
    /// </summary>
    static void InstallTests()
    {
        DevSwitch.At("KFIII_TEST_VOICEDUMP", _ => VoiceDump());
        DevSwitch.At("KFIII_TEST_SOUNDREPORT", _ => SoundReport());
        foreach (var item in DevSwitch.List("KFIII_TEST_QUICK"))
        {
            var p = item.Split('@');
            if (p.Length != 2) continue;
            var mode = p[0].Trim().ToLowerInvariant();
            var at = double.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture);
            if (mode == "save") { _testMode = "save"; _testAt = at; }
            else if (mode == "load")
                DevSwitch.Background("kf3-test-quickload", () =>
                {
                    Thread.Sleep((int)(at * 1000));
                    // A slow boot may not have reached the title yet.
                    while (!Dispatcher.ActiveNames.Any(n => n is "open" or "game")) Thread.Sleep(100);
                    RequestLoad();
                });
        }
    }

    /// <summary>From <see cref="AtWorldUpdate"/>: asks for the KFIII_TEST_QUICK save once its time has come.</summary>
    static void CheckTestSave()
    {
        long now = _clock.ElapsedMilliseconds;
        if (_firstUpdateMs < 0) _firstUpdateMs = now;
        if (_testMode == "save" && now - _firstUpdateMs >= _testAt * 1000)
        {
            _testMode = null;
            _wantSave = true;
        }
    }

    /// <summary>KFIII_DUMP_EVENTS=1: the BIOS event table each time GAME.EXE loads.</summary>
    static void DumpEventsIfAsked()
    {
        if (!DevSwitch.Is("KFIII_DUMP_EVENTS")) return;
        var ev = RecompOne.Runtime.Bios.BiosB.SaveEvents();
        for (int i = 0; i < ev.Length / EventSlotWords; i++)
        {
            int s = i * EventSlotWords;
            if (ev[s] != 0)
                Console.WriteLine($"[KFIII] event slot {i}: status={ev[s]} class={ev[s + 1]:X8} spec={ev[s + 2]:X4} mode={ev[s + 3]:X4} func={ev[s + 4]:X8}");
        }
    }

    const int SpuVoices = 24;
    /// SPU register: voice 0's current envelope level (ENVX); each voice is 16 bytes on.
    const uint SpuVoice0Envelope = 0x1F801C0C;

    /// <summary>libsnd's voice table, one row of u16s per voice, then each SPU voice's envelope level.</summary>
    static void VoiceDump()
    {
        var m = RecompOne.Runtime.Runtime.Mem!;
        var sb = new System.Text.StringBuilder("[voices] libsnd voice table:").AppendLine();
        for (uint i = 0; i < SpuVoices; i++)
        {
            sb.Append($"  {i,2}:");
            for (uint o = 0; o < GameAddr.LibsndVoiceStride; o += 2) sb.Append($" {m.ReadU16(GameAddr.LibsndVoices + i * GameAddr.LibsndVoiceStride + o):X4}");
            sb.AppendLine();
        }
        sb.Append("  ENVX:");
        var spu = RecompOne.Runtime.Runtime.Spu!;
        for (uint i = 0; i < SpuVoices; i++) sb.Append($" {spu.ReadReg16(SpuVoice0Envelope + i * 16):X4}");
        Console.WriteLine(sb.ToString());
    }

    /// <summary>
    /// F9: the last ten seconds of key-ons, appended to sound_report.txt in the
    /// working directory (beside the quick save) and printed to the console, for a
    /// player to send in when a sound did not play.
    /// </summary>
    static void SoundReport()
    {
        try
        {
            var sw = new StringWriter();
            sw.WriteLine($"==== F9 sound report {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            RecompOne.Runtime.Runtime.Spu?.DumpRecentKeyOns(sw, 10000);
            var path = Path.Combine(Environment.CurrentDirectory, "sound_report.txt");
            File.AppendAllText(path, sw.ToString() + Environment.NewLine);
            Console.Write(sw.ToString());
            Toast("Sound report", "Saved to sound_report.txt");
        }
        catch (Exception ex) { Toast("Sound report", "Failed: " + ex.Message); }
    }
}
