using System.Reflection;
using System.Runtime.InteropServices;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Dispatch;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Hardware;
using RecompOne.Runtime.Host.Window;
using RecompOne.Runtime.Memory;
using Silk.NET.Input;

namespace KfIII;

/// <summary>
/// Quick save and load: F2 saves the whole machine, F4 loads it, also from the
/// title screen.
///
/// <para><b>Why a whole-machine state works in a recompilation, at one point.</b>
/// The generated code keeps every MIPS register in <see cref="CpuContext"/> and
/// every byte of the stack in RAM; a recompiled function holds no C# locals across
/// a call. So the only state outside RAM, registers and hardware is the C# call
/// chain, and that is the same every time execution reaches the same function from
/// the same caller. The state is therefore taken and applied at one point only: the
/// entry of kf_WorldUpdate, whose single caller is kf_MainLoop (at 0x80014F3C). A
/// key press is latched and served the next time execution arrives there, so in a
/// menu or a movie it waits until the game is walking around again.</para>
///
/// <para>Saved: RAM, scratchpad, CPU and GTE registers, VRAM, SPU RAM and registers,
/// the BIOS event table, and which FDAT area module is loaded. Not saved: sound
/// voices in flight (keyed off on load; the sequencer keys them again) and CD, DMA
/// and interrupt state, which is idle at that point in the loop.</para>
///
/// <para>From the title (OPEN.EXE) there is no main loop yet, so F4 leaves OPEN at
/// once and starts GAME.EXE as for New Game, and the state is applied on the first
/// world update (SaveState.Title.cs).</para>
///
/// <para>The file is quicksave.kfstate in the working directory; the one before is
/// kept as quicksave.kfstate.bak. Test switches and the F9 sound report are in
/// SaveState.Tests.cs.</para>
/// </summary>
public static partial class SaveState
{
    /// "KF2S", from the game's North American title. Existing saves carry it.
    const uint Magic = 0x5332464B;
    /// 2 adds the BIOS event table; version 1 files still load.
    const int Version = 2;
    const string FileName = "quicksave.kfstate";

    static volatile bool _wantSave, _wantLoad;
    static readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    static string PathOf => Path.Combine(Environment.CurrentDirectory, FileName);

    public static void Install()
    {
        InstallTests();

        Event.AddListener<KeyboardEvent>(e =>
        {
            if (!e.Pressed || e.Repeat) return;
            if (e.Key == (int)Key.F2) RequestSave();
            else if (e.Key == (int)Key.F4) RequestLoad();
            else if (e.Key == (int)Key.F9) SoundReport();
        });

        Event.AddListener<OverlayLoadedEvent>(e =>
        {
            if (e.Name == "game") DumpEventsIfAsked();
            // The title autopilot has done its job once GAME.EXE is loaded.
            if (e.Name == "game" && _autopilot)
            {
                _autopilot = false;
                Controller.Injected = 0xFFFF;
            }
        });
    }

    static void RequestSave()
    {
        if (!InGame())
        {
            Toast("Quick save", "Only in the game itself, not on the title screen");
            return;
        }
        _wantSave = true;
    }

    static void RequestLoad()
    {
        if (!File.Exists(PathOf))
        {
            Toast("Quick load", "No quick save yet (F2 saves)");
            return;
        }

        _wantLoad = true;
        MovieSkip.Force = true;
        if (!InGame() && Dispatcher.ActiveNames.Contains("open"))
        {
            if (DevSwitch.Is("KFIII_QUICKLOAD_TITLE", "menu"))
            {
                if (!_autopilot)
                {
                    _autopilot = true;
                    new Thread(Autopilot) { IsBackground = true, Name = "kf3-quickload" }.Start();
                }
            }
            else _leaveTitle = true;
        }
    }

    static bool InGame() => Dispatcher.ActiveNames.Contains("game");

    /// <summary>Pre-hook on kf_WorldUpdate: serves a pending load or save.</summary>
    public static void AtWorldUpdate(CpuContext c, IMemory m)
    {
        CheckTestSave();

        if (_wantLoad)
        {
            _wantLoad = false;
            _fromTitle = false;
            MovieSkip.Force = false;
            try { Load(c, m); }
            catch (Exception ex) { Console.WriteLine($"[KFIII] quick load failed: {ex}"); Toast("Quick load", "Failed: " + ex.Message); }
            return;
        }

        if (_wantSave)
        {
            _wantSave = false;
            try { Save(c, m); }
            catch (Exception ex) { Console.WriteLine($"[KFIII] quick save failed: {ex}"); Toast("Quick save", "Failed: " + ex.Message); }
        }
    }

    // ---- the state file ------------------------------------------------------------
    //
    // In order: u32 Magic, s32 Version, the area module's name (BinaryWriter string,
    // empty when none), the CpuSnapshot's bytes, s32 RAM length and the RAM, the
    // scratchpad (256 x u32), each GTE register array (s32 length, bytes), VRAM
    // (1024 x 512 x u16, as u32 pairs), SPU RAM, the SPU registers 0x1F801C00..
    // 0x1F801DFF (u16 each), and from version 2 the BIOS event table (s32 count, u32s).

    const uint RamBase = 0x80000000u, Scratch = 0x1F800000u, ScratchSize = 0x400;
    const uint SpuRegs = 0x1F801C00u, SpuRegsEnd = 0x1F801E00u;
    const int VramW = 1024, VramH = 512;
    /// GP0 commands: clear the texture cache, copy VRAM to the CPU, copy the CPU to VRAM.
    const uint Gp0ClearCache = 0x01000000u, Gp0VramToCpu = 0xC0000000u, Gp0CpuToVram = 0xA0000000u;

    /// SPU registers whose saved value is not written back.
    const uint SpuKeyOn = 0x1F801D88, SpuKeyOnHi = 0x1F801D8A, SpuKeyOff = 0x1F801D8C, SpuKeyOffHi = 0x1F801D8E;
    const uint SpuEndx = 0x1F801D9C, SpuEndxHi = 0x1F801D9E, SpuTransferFifo = 0x1F801DA8, SpuStat = 0x1F801DAE;
    /// The 24 voices' registers end here, 16 bytes each; +0xC is the current envelope level.
    const uint SpuVoiceRegsEnd = 0x1F801D80;
    const uint SpuVoiceEnvelope = 0xC;

    /// <summary>
    /// SPU registers that must not be written back: key on and off act rather than
    /// store, ENDX and SPUSTAT are read-only, the transfer FIFO writes SPU RAM, and
    /// each voice's current envelope level is the mixer's state, not a setting. A
    /// stale level written back to a voice that is off never decays, and the game's
    /// sound library frees a voice only once that level reads 0, so it would lose
    /// those voices for good and sound effects (the weapon swing) would go unplayed
    /// whenever music had the rest.
    /// </summary>
    static bool SkipSpuReg(uint a) => a is SpuKeyOn or SpuKeyOnHi or SpuKeyOff or SpuKeyOffHi
        or SpuEndx or SpuEndxHi or SpuTransferFifo or SpuStat
        || (a < SpuVoiceRegsEnd && (a & 0xF) == SpuVoiceEnvelope);

    static void Save(CpuContext c, IMemory m)
    {
        var mem = (PSMemory)m;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, true))
        {
            w.Write(Magic);
            w.Write(Version);
            w.Write(Dispatcher.ActiveNames.FirstOrDefault(n => n.StartsWith("fdat", StringComparison.Ordinal)) ?? "");

            var snap = c.Snapshot();
            w.Write(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref snap, 1)));

            var ram = mem.Ram;
            w.Write(ram.Length);
            w.Write(ram);

            for (uint i = 0; i < ScratchSize; i += 4) w.Write(m.ReadU32(Scratch + i));

            WriteGte(w);

            var gpu = RecompOne.Runtime.Runtime.Gpu!;
            gpu.WriteGp0(Gp0ClearCache);
            gpu.WriteGp0(Gp0VramToCpu);
            gpu.WriteGp0(0u);
            gpu.WriteGp0((uint)(VramH << 16 | VramW));
            for (int i = 0; i < VramW * VramH / 2; i++) w.Write(gpu.ReadData());

            var spu = RecompOne.Runtime.Runtime.Spu!;
            w.Write(spu.Ram);
            for (uint a = SpuRegs; a < SpuRegsEnd; a += 2) w.Write(spu.ReadReg16(a));

            var ev = RecompOne.Runtime.Bios.BiosB.SaveEvents();
            w.Write(ev.Length);
            foreach (var x in ev) w.Write(x);
        }

        if (File.Exists(PathOf)) File.Copy(PathOf, PathOf + ".bak", true);
        File.WriteAllBytes(PathOf, ms.ToArray());
        Console.WriteLine($"[KFIII] quick saved ({ms.Length / 1024} KB, {sw.ElapsedMilliseconds} ms) {Marker(m)}");
        Toast("Quick save", "Saved");
    }

    static void Load(CpuContext c, IMemory m)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var r = new BinaryReader(File.OpenRead(PathOf));
        if (r.ReadUInt32() != Magic) throw new InvalidDataException("not a quick save");
        int version = r.ReadInt32();
        if (version is < 1 or > Version) throw new InvalidDataException("quick save from another version");
        string fdat = r.ReadString();

        var snap = new CpuSnapshot();
        r.Read(MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref snap, 1)));

        int ramLen = r.ReadInt32();
        var ram = r.ReadBytes(ramLen);
        var scratch = new uint[ScratchSize / 4];
        for (int i = 0; i < scratch.Length; i++) scratch[i] = r.ReadUInt32();

        // The area module first: its code must be what the restored RAM calls.
        if (fdat.Length > 0) Dispatcher.Load(fdat);

        for (int i = 0; i + 3 < ram.Length; i += 4)
            m.WriteU32(RamBase + (uint)i, BitConverter.ToUInt32(ram, i));
        for (int i = 0; i < scratch.Length; i++) m.WriteU32(Scratch + (uint)(i * 4), scratch[i]);

        ReadGte(r);

        var gpu = RecompOne.Runtime.Runtime.Gpu!;
        gpu.WriteGp0(Gp0ClearCache);
        gpu.WriteGp0(Gp0CpuToVram);
        gpu.WriteGp0(0u);
        gpu.WriteGp0((uint)(VramH << 16 | VramW));
        for (int i = 0; i < VramW * VramH / 2; i++) gpu.WriteGp0(r.ReadUInt32());

        // The registers go back (not the envelope levels, see SkipSpuReg), then every
        // voice is turned off at once, pending key-ons included (a key-off alone is
        // ignored for those). The game's sound library frees each voice it still
        // thinks busy once the level has read 0 for 15 vblanks.
        var spu = RecompOne.Runtime.Runtime.Spu!;
        r.Read(spu.Ram, 0, spu.Ram.Length);
        for (uint a = SpuRegs; a < SpuRegsEnd; a += 2)
        {
            ushort v = r.ReadUInt16();
            if (!SkipSpuReg(a)) spu.WriteReg16(a, v);
        }
        spu.SilenceAllVoices();

        // A version 1 file has no event table. The session's own is right as long as
        // its events were opened in the same order as the saved session's.
        if (version >= 2)
        {
            var ev = new uint[r.ReadInt32()];
            for (int i = 0; i < ev.Length; i++) ev[i] = r.ReadUInt32();
            RecompOne.Runtime.Bios.BiosB.LoadEvents(ev);
        }

        c.Restore(snap);
        Smooth.Forget();
        Console.WriteLine($"[KFIII] quick loaded{(fdat.Length > 0 ? " (" + fdat + ")" : "")} in {sw.ElapsedMilliseconds} ms {Marker(m)}");
        Toast("Quick load", "Loaded");
    }

    /// <summary>
    /// The GTE state's arrays are internal to the runtime, so the primitive ones are
    /// saved by reflection, in declaration order. SP (the projected-vertex FIFO) is
    /// per-polygon scratch and not saved.
    /// </summary>
    static IEnumerable<FieldInfo> GteFields() =>
        typeof(RecompOne.Runtime.Gte.State).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(f => f.FieldType.IsArray && f.FieldType.GetElementType()!.IsPrimitive)
            .OrderBy(f => f.MetadataToken);

    static void WriteGte(BinaryWriter w)
    {
        var s = new RecompOne.Runtime.Gte.State();
        RecompOne.Runtime.Gte.Save(s);
        foreach (var f in GteFields())
        {
            var a = (Array)f.GetValue(s)!;
            var bytes = new byte[Buffer.ByteLength(a)];
            Buffer.BlockCopy(a, 0, bytes, 0, bytes.Length);
            w.Write(bytes.Length);
            w.Write(bytes);
        }
    }

    static void ReadGte(BinaryReader r)
    {
        var s = new RecompOne.Runtime.Gte.State();
        RecompOne.Runtime.Gte.Save(s);   // SP and anything not saved keep their current values
        foreach (var f in GteFields())
        {
            var a = (Array)f.GetValue(s)!;
            var bytes = r.ReadBytes(r.ReadInt32());
            Buffer.BlockCopy(bytes, 0, a, 0, Math.Min(bytes.Length, Buffer.ByteLength(a)));
        }
        RecompOne.Runtime.Gte.Load(s);
    }

    /// <summary>Yaw and the game's vblank total, logged on save and load: enough to see that a load landed where the save was.</summary>
    static string Marker(IMemory m) => $"t={_clock.Elapsed.TotalSeconds:F1}s yaw=0x{m.ReadU16(GameAddr.Yaw):X3} vblanks={m.ReadU32(GameAddr.VblankTotal)}";

    static void Toast(string title, string text)
    {
        try { ToastNotifications.ShowText(title, text); } catch { }
    }
}
