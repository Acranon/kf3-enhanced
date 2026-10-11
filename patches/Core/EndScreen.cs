using RecompOne.Runtime.Config;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Hardware;
using RecompOne.Runtime.Host;
using RecompOne.Runtime.Memory;
using Silk.NET.Input;

namespace KfIII;

/// <summary>
/// Returns from "The End" to the title. After the credits, END.EXE stops the pad
/// and the drive and then loops on one instruction for good (0x80011AB8: j
/// 0x80011AB8); on the console the player pressed reset. That loop presents no
/// frames, so Windows would mark the window as not responding.
///
/// A pre-hook on END.EXE's PadStop, whose one call is that last step, keeps the
/// picture up instead, one VSync at a time. On a new press of any pad button, a
/// key bound to Start or Cross, or a left click, it throws <see cref="LeaveEnd"/>;
/// SaveState.StubExec catches it and has the boot stub run OPEN.EXE next, the same
/// way the quick load leaves the title.
/// </summary>
public static class EndScreen
{
    /// <summary>Thrown out of END.EXE to leave it; caught at the boot stub's Exec call.</summary>
    public sealed class LeaveEnd : Exception;

    /// A press only counts this long after The End appears, so one meant for the
    /// credits does not skip it.
    const long MinShowMs = 1500;

    /// <summary>Pre-hook on END.EXE's PadStop: shows The End until a press, then leaves.</summary>
    public static void BeforePadStop(CpuContext c, IMemory m)
    {
        Console.WriteLine("[KFIII] the end: waiting for a button to return to the title");
        var regs = c.Snapshot();
        long start = Environment.TickCount64;
        ushort prevPad = Controller.State;
        bool prevKeys = true;   // as if held, so a key already down does not count
        try
        {
            while (true)
            {
                c.A0 = 0;
                Recompiled.KingsFieldIII_end.VSync_end(c, m);
                // A new press is a bit going from up to down. The pad word is active
                // low, and not all ones at rest, so only changed bits are compared.
                ushort pad = Controller.State;
                bool keys = Controls.KeyDown(ConfigManager.Game.Keys.Start) || Controls.KeyDown(ConfigManager.Game.Keys.Cross)
                    || HostWindow.IsMouseButtonDown(MouseButton.Left);
                bool pressed = (prevPad & ~pad & 0xFFFF) != 0 || (keys && !prevKeys);
                prevPad = pad; prevKeys = keys;
                if (pressed && Environment.TickCount64 - start > MinShowMs) break;
            }
        }
        finally { c.Restore(regs); }
        Console.WriteLine("[KFIII] the end: back to the title");
        throw new LeaveEnd();
    }
}
