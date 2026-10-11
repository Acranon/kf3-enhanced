using RecompOne.Runtime.Config;

namespace KfIII;

/// <summary>
/// The port's identity and its display defaults, set before the runtime opens its
/// window. Two programs call this: the game (Program.cs) and the release launcher,
/// which opens the window first (to ask for the disc and show the build) and so
/// must set the same defaults before it does. The launcher compiles this one file
/// in directly, which is why it depends on nothing but the runtime.
/// </summary>
public static class PortDefaults
{
    public const string AppId = "kf3-enhanced";
    public const string Title = "King's Field III Enhanced";

    /// <summary>
    /// The window's defaults, for a first run or keys an older interface.ini lacks:
    /// borderless fullscreen at 8x with the frame counter, in the port's colours.
    /// ConfigManager adds them only where interface.ini has no value.
    /// </summary>
    public static void Apply()
    {
        RecompOne.Runtime.Runtime.AppId = AppId;
        ConfigManager.ViewDefaults["Fullscreen"] = "True";
        ConfigManager.ViewDefaults["Borderless"] = "True";
        ConfigManager.ViewDefaults["RenderScale"] = "8";
        ConfigManager.ViewDefaults["ShowFps"] = "True";
        ConfigManager.ViewDefaults["Accent"] = "#22A06B";
        ConfigManager.ViewDefaults["Background"] = "#1B1B1D";
    }
}
