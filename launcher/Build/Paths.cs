namespace KfIII.Launcher.Build;

/// <summary>
/// Where the launcher reads its payload from, and where everything it and the game
/// write goes.
///
/// The runtime and the patches address every file they own with a bare relative path
/// (settings.json, interface.ini, enhancements.json, carda.sav, quicksave.kfstate,
/// automap/), so they land in the process's working directory. The launcher moves
/// into the data folder before touching the runtime, and that is the whole fix: they
/// keep resolving relatively and land in the right place.
/// </summary>
static class Paths
{
    /// <summary>
    /// The shipped tree, read-only. In the Windows package the runtime lives in bin\
    /// beside the real executable, so this is its parent, where content\ is; for a
    /// development run it is the executable's own folder.
    /// </summary>
    public static string Install { get; } = ResolveInstall();

    public static string Content { get; } = Path.Combine(Install, "content");
    public static string ContentConfig { get; } = Path.Combine(Install, "content", "config");
    public static string ContentSrc { get; } = Path.Combine(Install, "content", "src");

    /// <summary>
    /// Per-user and writable: KFIII_DATA if set, else %LOCALAPPDATA%\kf3-enhanced. Saves
    /// live here, so it does not move or vanish with the install.
    /// </summary>
    public static string Data { get; } = ResolveData();

    /// <summary>Built game assemblies, one folder per build key.</summary>
    public static string Builds => Path.Combine(Data, "builds");

    public static string BuildLog => Path.Combine(Data, "build.log");
    public static string CrashLog => Path.Combine(Data, "launcher-crash.log");

    static string ResolveInstall()
    {
        // TrimEnd matters: GetDirectoryName of a path ending in a slash is the path itself.
        var dir = Path.GetFullPath(AppContext.BaseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (Directory.Exists(Path.Combine(dir, "content"))) return dir;

        var parent = Path.GetDirectoryName(dir);
        if (!string.IsNullOrEmpty(parent) && Directory.Exists(Path.Combine(parent, "content"))) return parent;
        return dir;
    }

    static string ResolveData()
    {
        var pinned = Environment.GetEnvironmentVariable("KFIII_DATA");
        if (!string.IsNullOrWhiteSpace(pinned)) return Path.GetFullPath(pinned);

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(local)) return Path.Combine(local, "kf3-enhanced");
        return Path.Combine(Directory.GetCurrentDirectory(), "kf3-enhanced-data");
    }

    /// <summary>Makes the data folder and moves into it.</summary>
    public static void Prepare()
    {
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(Builds);
        Directory.SetCurrentDirectory(Data);
    }
}
