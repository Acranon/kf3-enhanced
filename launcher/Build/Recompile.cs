using System.Reflection;

namespace KfIII.Launcher.Build;

/// <summary>
/// Runs the recompiler over the player's disc: MIPS out of the image, C# into a
/// folder. A few seconds for the whole game, so it is simply re-run whenever a build
/// is needed.
///
/// It runs in process: the recompiler's top-level Program compiles to an ordinary
/// entry-point method, reachable through Assembly.EntryPoint. A second process is not
/// an option, because a self-contained release has no `dotnet` to start one with.
/// </summary>
static class Recompile
{
    const string ConfigName = "kf3.json";

    public static void Run(string discPath, string outDir)
    {
        Directory.CreateDirectory(outDir);

        // The recompiler resolves cue, funcMap and output relative to the config file,
        // and the install folder is read-only, so the config folder is staged into the
        // data folder (only the files that changed) and a copy pointed at the player's
        // disc and our output is written beside it.
        var staged = Stage();
        var cfgPath = Path.Combine(staged, "kf3.build.json");
        var json = File.ReadAllText(Path.Combine(staged, ConfigName));
        json = ReplaceStringValue(json, "cue", discPath);
        json = ReplaceStringValue(json, "output", outDir);
        File.WriteAllText(cfgPath, json);

        try { Invoke(cfgPath); }
        finally { try { File.Delete(cfgPath); } catch (IOException) { } }
    }

    static string Stage()
    {
        var dst = Path.Combine(Paths.Data, "config");
        Directory.CreateDirectory(dst);
        foreach (var src in Directory.EnumerateFiles(Paths.ContentConfig, "*.json", SearchOption.AllDirectories))
        {
            var target = Path.Combine(dst, Path.GetRelativePath(Paths.ContentConfig, src));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            // Equal, not "no older": a staged copy from a newer install that was rolled
            // back is still the wrong config.
            var from = new FileInfo(src);
            var to = new FileInfo(target);
            if (to.Exists && to.Length == from.Length && to.LastWriteTimeUtc == from.LastWriteTimeUtc) continue;
            File.Copy(src, target, overwrite: true);
        }
        return dst;
    }

    /// <summary>
    /// Replaces one string value in place, leaving the rest of the file (addresses,
    /// overlays, patches) exactly as shipped.
    /// </summary>
    static string ReplaceStringValue(string json, string key, string value)
    {
        var needle = $"\"{key}\"";
        int at = json.IndexOf(needle, StringComparison.Ordinal);
        if (at < 0) throw new InvalidOperationException($"content/config/{ConfigName} has no \"{key}\" entry.");
        int colon = json.IndexOf(':', at + needle.Length);
        int open = colon < 0 ? -1 : json.IndexOf('"', colon + 1);
        int close = open < 0 ? -1 : json.IndexOf('"', open + 1);
        if (close < 0) throw new InvalidOperationException($"content/config/{ConfigName}: \"{key}\" is not a string.");
        return json[..(open + 1)] + System.Text.Json.JsonEncodedText.Encode(value) + json[close..];
    }

    static void Invoke(string cfgPath)
    {
        var asm = typeof(RecompOne.Recompiler.Config.ConfigLoader).Assembly;
        var main = asm.EntryPoint ?? throw new InvalidOperationException("The recompiler has no entry point.");

        object? result;
        try { result = main.Invoke(null, [new[] { cfgPath }]); }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            throw new InvalidOperationException($"The recompiler failed: {e.InnerException.Message}", e.InnerException);
        }

        // It returns an exit code rather than throwing for a bad config or disc.
        if (result is int code && code != 0)
            throw new InvalidOperationException($"The recompiler exited with code {code}. See the log for what it reported.");
    }
}
