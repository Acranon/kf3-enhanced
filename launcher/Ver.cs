using System.Reflection;

namespace KfIII.Launcher;

/// <summary>
/// What this build calls itself: <see cref="Number"/> is the release (from ../VERSION),
/// <see cref="Full"/> adds the commit it was built from, which is what a bug report
/// should quote.
/// </summary>
static class Ver
{
    public static string Number { get; } =
        typeof(Ver).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";

    public static string Full { get; } =
        typeof(Ver).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            is { Length: > 0 } s ? s : Number;
}
