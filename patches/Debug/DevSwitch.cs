using System.Globalization;

namespace KfIII;

/// <summary>
/// Developer switches: environment variables named <c>KFIII_*</c> that turn on a test,
/// a probe or a log. None is set in normal play, and every one is listed in
/// docs/testing.md. Each patch reads its own switches through these helpers, so a
/// search for the name finds both the reader and the documentation.
/// </summary>
public static class DevSwitch
{
    /// <summary>The trimmed value, or null when the variable is unset or blank.</summary>
    public static string? Text(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }

    /// <summary>True when the variable is set to <paramref name="value"/> ("1" by default).</summary>
    public static bool Is(string name, string value = "1") => Text(name) == value;

    /// <summary>The value as an integer (decimal, or hex with 0x), or <paramref name="fallback"/>.</summary>
    public static int Int(string name, int fallback = 0) => ParseInt(Text(name)) ?? fallback;

    /// <summary>The value as a number, or null.</summary>
    public static double? Number(string name) =>
        double.TryParse(Text(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    /// <summary>The comma-separated fields of the value, trimmed; empty when unset.</summary>
    public static string[] List(string name) =>
        Text(name)?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];

    /// <summary>
    /// For a value of comma-separated seconds ("12,15.5"), runs <paramref name="action"/>
    /// once at each of those times after the call, on a background thread. Tests use
    /// it to act at a fixed point of a scripted run.
    /// </summary>
    public static void At(string name, Action<double> action)
    {
        foreach (var field in List(name))
        {
            if (!double.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out var at))
                throw new ArgumentException($"{name}: '{field}' is not a number of seconds");
            new Thread(() =>
            {
                Thread.Sleep(TimeSpan.FromSeconds(at));
                action(at);
            })
            { IsBackground = true, Name = name.ToLowerInvariant() }.Start();
        }
    }

    /// <summary>Runs <paramref name="body"/> on a named background thread (probes that report on a timer).</summary>
    public static void Background(string name, Action body) =>
        new Thread(() => body()) { IsBackground = true, Name = name }.Start();

    static int? ParseInt(string? s)
    {
        if (s == null) return null;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return int.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h) ? h : null;
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) ? d : null;
    }
}
