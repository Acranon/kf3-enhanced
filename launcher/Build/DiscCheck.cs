using RecompOne.Runtime.Cdrom;

namespace KfIII.Launcher.Build;

/// <summary>
/// Accepts only the disc this port is built for: the North American King's Field II
/// (SLUS-00255), King's Field III in Japan. A wrong disc would not fail; it would
/// recompile into a game that is wrong in ways that show up hours later. So the
/// picker refuses it, naming what it is:
///
///   - the first North American King's Field (SLUS-00158) is the one most likely to be
///     picked by mistake, and gets its own message;
///   - FDAT.T holds the area code modules, cut out by offset; one shorter than the
///     config's last module means a truncated or different archive.
/// </summary>
static class DiscCheck
{
    public const string Serial = "SLUS-00255";
    public const string BootExe = "SLUS_002.55";
    const string Fdat = "CD/COM/FDAT.T";

    /// <summary>Files the recompile reads, and the smallest each may be.</summary>
    static readonly (string Path, uint MinSize)[] _required =
    [
        ("OPEN.EXE", 0x800),
        ("GAME.EXE", 0x800),
        ("END.EXE", 0x800),
        (Fdat, 0),   // checked against the config, see FdatFloor
    ];

    static readonly Dictionary<string, string?> _cache = new(StringComparer.Ordinal);
    static uint? _fdatFloor;

    /// <summary>
    /// Null if the image is usable, otherwise why not. Remembered per path, size and
    /// time, because the picker asks every frame while it is open.
    /// </summary>
    public static string? Validate(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "No disc image selected.";
        if (!File.Exists(path)) return $"Not found: {path}";

        var info = new FileInfo(path);
        string key = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        lock (_cache)
            if (_cache.TryGetValue(key, out var known)) return known;

        var verdict = Check(path);
        lock (_cache) _cache[key] = verdict;
        return verdict;
    }

    static string? Check(string path)
    {
        DiscFs fs;
        try { fs = DiscFs.Open(path); }
        catch (Exception e) { return $"Could not read this image as a cue/bin pair or a CHD: {e.Message}"; }

        using (fs)
        {
            string boot;
            try { boot = System.Text.Encoding.ASCII.GetString(fs.ReadFile("SYSTEM.CNF")); }
            catch (Exception) { return "No SYSTEM.CNF on this disc, so it is not a PlayStation game image."; }

            if (boot.IndexOf(BootExe, StringComparison.OrdinalIgnoreCase) < 0)
                return Wrong(boot);

            foreach (var (file, min) in _required)
            {
                if (!fs.Locate(file, out _, out uint size))
                    return $"This disc is missing {file}, which the build needs. The image may be incomplete.";
                uint floor = file == Fdat ? FdatFloor() : min;
                if (size < floor)
                    return $"{file} is {size} bytes on this disc; {Serial} has at least {floor}. The image may be truncated.";
            }
        }
        return null;
    }

    /// <summary>The end of the last area module in config/kf3.json, read from the
    /// config so the two cannot drift apart.</summary>
    static uint FdatFloor()
    {
        if (_fdatFloor is { } cached) return cached;
        uint end = 0;
        try
        {
            var json = File.ReadAllText(Path.Combine(Paths.ContentConfig, "kf3.json"));
            using var doc = System.Text.Json.JsonDocument.Parse(json, new System.Text.Json.JsonDocumentOptions
            {
                CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            foreach (var overlay in doc.RootElement.GetProperty("overlays").EnumerateArray())
            {
                if (!overlay.TryGetProperty("file", out var f)) continue;
                if (!string.Equals(f.GetString(), Fdat, StringComparison.OrdinalIgnoreCase)) continue;
                uint offset = overlay.TryGetProperty("offset", out var o) ? ParseU32(o) : 0;
                uint size = overlay.TryGetProperty("size", out var z) ? ParseU32(z) : 0;
                end = Math.Max(end, offset + size);
            }
        }
        catch (Exception)
        {
            // A payload we cannot parse is a broken install, not a bad disc: pass, and
            // let the recompile report the real problem.
        }
        return (_fdatFloor = end).Value;
    }

    /// <summary>The config writes numbers either as JSON numbers or as "0x..." strings.</summary>
    static uint ParseU32(System.Text.Json.JsonElement e)
    {
        if (e.ValueKind == System.Text.Json.JsonValueKind.Number) return e.GetUInt32();
        var s = e.GetString() ?? "0";
        return s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? Convert.ToUInt32(s[2..], 16)
            : uint.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Names the disc the player chose, from the boot file in SYSTEM.CNF.</summary>
    static string Wrong(string systemCnf)
    {
        var found = SerialOf(systemCnf);
        if (found == "SLUS-00158")
            return "This is King's Field (SLUS-00158), the first North American King's Field, which is " +
                   "a different game. This port is of King's Field II (SLUS-00255), King's Field III in Japan.";
        string got = found is null ? "" : $" This one is {found}.";
        return $"This is not King's Field II for the North American PlayStation ({Serial}).{got}";
    }

    static string? SerialOf(string systemCnf)
    {
        foreach (var raw in systemCnf.Split('\n'))
        {
            int at = raw.IndexOf("cdrom", StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;
            var name = raw[at..].Trim().TrimEnd('\r');
            int slash = name.LastIndexOfAny(['\\', '/', ':']);
            if (slash >= 0) name = name[(slash + 1)..];
            name = name.Split(';')[0].Trim();
            if (name.Length == 11 && name[4] == '_' && name[8] == '.')   // SLUS_002.55 -> SLUS-00255
                return $"{name[..4]}-{name[5..8]}{name[9..]}".ToUpperInvariant();
        }
        return null;
    }
}
