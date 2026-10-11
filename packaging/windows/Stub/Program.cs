using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace KfIII.Stub;

/// <summary>
/// Starts bin\KF3Enhanced.exe with the same arguments and waits for it. It attaches
/// to a parent console when there is one, so a terminal launch still shows output.
/// </summary>
static class Program
{
    const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AttachConsole(int dwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    static int Main()
    {
        try { AttachConsole(AttachParentProcess); }
        catch (Exception) { }   // started from Explorer or a shortcut: no console

        var root = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var exe = Path.Combine(root, "bin", "KF3Enhanced.exe");
        if (!File.Exists(exe))
        {
            MessageBoxW(IntPtr.Zero, "King's Field III Enhanced cannot start: bin\\KF3Enhanced.exe is missing. Reinstalling should fix it.",
                "King's Field III Enhanced", 0x10);
            return 1;
        }

        var args = Environment.GetCommandLineArgs();
        var childArgs = new StringBuilder();
        for (int i = 1; i < args.Length; i++)
        {
            if (i > 1) childArgs.Append(' ');
            childArgs.Append(Quote(args[i]));
        }

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = childArgs.ToString(),
            UseShellExecute = false,
            WorkingDirectory = root,
        };
        using (var p = Process.Start(psi))
        {
            if (p == null) return 1;
            p.WaitForExit();
            return p.ExitCode;
        }
    }

    static string Quote(string value)
    {
        if (value.Length > 0 && value.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return value;
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
