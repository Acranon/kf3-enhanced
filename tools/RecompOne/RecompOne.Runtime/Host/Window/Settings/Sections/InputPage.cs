namespace RecompOne.Runtime.Host.Window;

/// <summary>
/// Lets a game describe its own controls on Settings > Input instead of the bare
/// PlayStation button table: rows named by what they do (with a second key where
/// the game reads one), extra controls under the table (mouse options), and
/// gamepad rows labelled by action. Everything is optional; unset, the page is
/// the stock one. Only player 1 is affected.
/// </summary>
public static class InputPage
{
    /// <summary>
    /// One keyboard row. <c>Get</c> null makes it a heading. <c>GetAlt</c> null:
    /// no second key. <c>Fixed</c> rows show their key but cannot be changed.
    /// </summary>
    public sealed record KeyRow(
        string Label,
        Func<string>? Get = null, Action<string>? Set = null,
        Func<string>? GetAlt = null, Action<string>? SetAlt = null,
        string? Hint = null, bool Fixed = false);

    /// <summary>Replaces player 1's keyboard table when set.</summary>
    /// <summary>Controllers the game takes: 1 hides the Pad 1 / Pad 2 tabs (a one-player game).</summary>
    public static int Pads = 2;

    public static Func<IReadOnlyList<KeyRow>>? KeyboardRows;

    /// <summary>Drawn under player 1's keyboard table (e.g. mouse settings).</summary>
    public static Action? KeyboardExtras;

    /// <summary>Replaces "Reset to Defaults" for player 1's keyboard.</summary>
    public static Action? ResetKeyboard;

    /// <summary>Called after a custom row changes (the game saves its own settings).</summary>
    public static Action? Changed;

    /// <summary>A stored key name as shown on the custom rows ("ShiftLeft" -> "L Shift").</summary>
    public static Func<string, string>? KeyLabel;

    /// <summary>Gamepad row label from the PlayStation button name ("Cross" -> "Interact (Cross)").</summary>
    public static Func<string, string>? ButtonLabel;
}
