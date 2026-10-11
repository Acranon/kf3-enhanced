using System.Runtime.InteropServices;

namespace KfIII.Launcher;

/// <summary>
/// Gives a release its console output back when it was started from a terminal. The
/// launcher is a WinExe (no console window behind the game), so it starts with no
/// console; attaching to the parent's, when there is one, makes the [KFIII] lines and
/// KFIII_* switch output readable again. From Explorer or a shortcut it does nothing.
/// </summary>
static class ConsoleAttach
{
    const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AttachConsole(int processId);

    public static void ToParent()
    {
        if (!OperatingSystem.IsWindows()) return;
        // Output already going to a file or pipe (a test run, `> log.txt`) stays there.
        if (Console.IsOutputRedirected) return;
        try
        {
            if (!AttachConsole(AttachParentProcess)) return;
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        }
        catch (Exception)
        {
            // No console is not a reason to refuse to start the game.
        }
    }
}
