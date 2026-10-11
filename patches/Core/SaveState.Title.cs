using RecompOne.Runtime.Context;
using RecompOne.Runtime.Dispatch;
using RecompOne.Runtime.Hardware;
using RecompOne.Runtime.Memory;

namespace KfIII;

// Quick load from the title screen.
//
// The boot stub (SLUS_002.55) is a loop: Load and Exec the executable its index
// names (0 OPEN, 1 GAME, 2 END; the table is at 0x8001024C), and when Exec returns,
// take the next index from GameAddr.StubNextExe, which the executable set. OPEN.EXE
// leaves by writing 1 there, 0 to StubMode and the title menu's choice to
// StubMenuChoice (0 = New Game); see 0x800120D0 in OPEN.EXE.
//
// So F4 on the title throws LeaveTitle out of OPEN's next VSync. The stub's Exec
// call, which StubExec replaces, catches it, puts the stub's registers back as
// Exec's return would, writes OPEN's hand-off itself and returns: the stub starts
// GAME.EXE as for New Game, the elf movie is skipped (MovieSkip.Force), and the
// state replaces all of it on the first world update. No title menu and no New
// Game movie are shown.
//
// KFIII_QUICKLOAD_TITLE=menu instead drives the title menu with Start and Cross
// taps (Autopilot below), which is slower but goes through the game's own path.
public static partial class SaveState
{
    sealed class LeaveTitle : Exception;

    static volatile bool _leaveTitle, _fromTitle;
    static bool _autopilot;

    /// Words per slot of the BIOS event table: status, class, spec, mode, handler.
    const int EventSlotWords = 5;
    /// The class of the memory-card events, in its top byte.
    const uint CardEventClass = 0xF4000000u;

    /// <summary>
    /// Pre-hook on kf_FadeScreen (a1 to a2 in steps of a3, one rendered frame each).
    /// New Game runs two, 0x1000 to 0 by -0x80, about 2 s each at 15 Hz, just before
    /// the main loop. On the way in from the title the state replaces whatever they
    /// draw, so they are skipped.
    /// </summary>
    public static bool BeforeFade(CpuContext c, IMemory m) => !(_fromTitle && _wantLoad);

    /// <summary>Pre-hook on OPEN.EXE's VSync: leaves the title when F4 asked to.</summary>
    public static void TitleVSync(CpuContext c, IMemory m)
    {
        if (!_leaveTitle) return;
        _leaveTitle = false;
        throw new LeaveTitle();
    }

    /// <summary>
    /// Closes OPEN.EXE's four memory-card events (slots 0-3). OPEN's own exit closes
    /// them and leaves libspu's DMA event open, so GAME.EXE's OpenEvents get the same
    /// slots every session, and a quick save's RAM holds those handles. Left open by
    /// the unwind, GAME's events would land four slots higher, and the first sound
    /// bank upload after a load would wait forever on a handle that belongs to
    /// another event.
    /// </summary>
    static void CloseCardEvents()
    {
        var ev = RecompOne.Runtime.Bios.BiosB.SaveEvents();
        for (int i = 0; i < ev.Length / EventSlotWords; i++)
            if ((ev[i * EventSlotWords + 1] & 0xFF000000u) == CardEventClass)
                Array.Clear(ev, i * EventSlotWords, EventSlotWords);
        RecompOne.Runtime.Bios.BiosB.LoadEvents(ev);
    }

    /// <summary>
    /// Replaces the boot stub's Exec call (its BIOS A(43h) thunk at 0x8001022C). Runs
    /// Exec, and turns a <see cref="LeaveTitle"/> or <see cref="EndScreen.LeaveEnd"/>
    /// thrown out of the executable into a normal return with the next executable chosen.
    /// </summary>
    public static void StubExec(CpuContext c, IMemory m)
    {
        if (DevSwitch.Is("KFIII_CARD_PROBE"))
            Console.WriteLine($"[card] stub exec: next {m.ReadU8(GameAddr.StubNextExe)} mode {m.ReadU8(GameAddr.StubMode)} choice {m.ReadU8(GameAddr.StubMenuChoice)} bytes {string.Join(' ', Enumerable.Range(0, 16).Select(i => m.ReadU8(GameAddr.StubNextExe + (uint)i).ToString("X2")))}");
        var regs = c.Snapshot();
        try
        {
            // BIOS A(43h), Exec.
            c.T2 = 0xA0;
            c.T1 = 0x43;
            Dispatcher.Call(c, m, 0xA0);
        }
        catch (EndScreen.LeaveEnd)
        {
            // Run the title next, as after a power-on.
            c.Restore(regs);
            RecompOne.Runtime.Runtime.AbandonExecutable();
            CloseCardEvents();
            m.WriteU8(GameAddr.StubNextExe, 0);   // OPEN.EXE
            m.WriteU8(GameAddr.StubMode, 0);
            m.WriteU8(GameAddr.StubMenuChoice, 0);
            c.V0 = 1;
        }
        catch (LeaveTitle)
        {
            c.Restore(regs);
            RecompOne.Runtime.Runtime.AbandonExecutable();
            CloseCardEvents();
            m.WriteU8(GameAddr.StubNextExe, 1);   // GAME.EXE
            m.WriteU8(GameAddr.StubMode, 0);
            m.WriteU8(GameAddr.StubMenuChoice, 0);   // New Game
            c.V0 = 1;
            _fromTitle = true;
            Console.WriteLine($"[KFIII] quick load: left the title for GAME.EXE t={_clock.Elapsed.TotalSeconds:F1}s");
        }
    }

    /// <summary>
    /// KFIII_QUICKLOAD_TITLE=menu: alternates Start and Cross until GAME.EXE loads (or
    /// a minute passes). Start skips the title movies and opens the title menu; Cross
    /// takes its first entry. Whatever path the menu takes, it ends in GAME.EXE's
    /// main loop, where the state replaces everything anyway.
    /// </summary>
    static void Autopilot()
    {
        Console.WriteLine("[KFIII] quick load: driving the title into the game");
        var deadline = _clock.ElapsedMilliseconds + 60_000;
        bool start = true;
        while (_autopilot && _clock.ElapsedMilliseconds < deadline)
        {
            Controller.Injected = (ushort)~(start ? Controller.Start : Controller.Cross);
            Thread.Sleep(120);
            Controller.Injected = 0xFFFF;
            Thread.Sleep(380);
            start = !start;
        }
        _autopilot = false;
        Controller.Injected = 0xFFFF;
    }
}
