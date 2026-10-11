using ImGuiNET;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Host.Window;

namespace KfIII;

/// <summary>
/// Settings > Input for King's Field III, through the runtime's InputPage hooks. The
/// player 1 keyboard tab, renamed "Keyboard/Mouse", lists the controls by what they do
/// in the game rather than by PlayStation button: every key can be remapped, with a
/// second key where the port reads one (walking on the arrows too), the port's own
/// keys (run, jump, map), and below them the mouse buttons and the mouse-look
/// settings. The gamepad tab keeps the button table, with each button's action in its
/// label.
///
/// Rows for a PlayStation button edit ConfigManager.Game.Keys (settings.json); the
/// rest are <see cref="Controls.Binds"/> and the mouse settings in enhancements.json.
/// With KFIII_KEYS=stock the page is the runtime's stock one.
/// </summary>
public static class ControlsPage
{
    static KeyBindings K => ConfigManager.Game.Keys;
    static Controls.Binds B => Controls.B;

    // A row for a PlayStation button, with an optional second key.
    static InputPage.KeyRow Psx(string label, Func<KeyBindings, string> get, Action<KeyBindings, string> set,
        string? hint = null, Func<string>? getAlt = null, Action<string>? setAlt = null) =>
        new(label, () => get(K), v => set(K, v), getAlt, setAlt, hint);

    // A row for one of the port's own keys.
    static InputPage.KeyRow Port(string label, Func<string> get, Action<string> set, string? hint = null) =>
        new(label, get, set, Hint: hint);

    static InputPage.KeyRow Heading(string label) => new(label);

    // A key that cannot be changed, listed so the page shows every control.
    static InputPage.KeyRow Fixed(string label, string key, string? hint = null) => new(label, () => key, Hint: hint, Fixed: true);

    static readonly IReadOnlyList<InputPage.KeyRow> _rows =
    [
        Heading("Moving"),
        Psx("Walk forward", k => k.Up, (k, v) => k.Up = v, "Also moves up in menus.",
            () => B.WalkAlt, v => B.WalkAlt = v),
        Psx("Walk back", k => k.Down, (k, v) => k.Down = v, "Also moves down in menus.",
            () => B.BackAlt, v => B.BackAlt = v),
        Psx("Strafe left", k => k.L1, (k, v) => k.L1 = v),
        Psx("Strafe right", k => k.R1, (k, v) => k.R1 = v),
        Psx("Turn left", k => k.Left, (k, v) => k.Left = v, "Also moves left in menus."),
        Psx("Turn right", k => k.Right, (k, v) => k.Right = v, "Also moves right in menus."),
        Psx("Look up", k => k.L2, (k, v) => k.L2 = v, "The mouse looks around too."),
        Psx("Look down", k => k.R2, (k, v) => k.R2 = v),
        Port("Run (hold)", () => B.Run, v => B.Run = v, "Hold to run. Uses stamina."),
        Port("Jump", () => B.Jump, v => B.Jump = v, "Needs Jump turned on in Settings > Quality of life."),

        Heading("Actions"),
        Psx("Attack", k => k.Square, (k, v) => k.Square = v, "Swing the equipped weapon (left mouse button by default)."),
        Psx("Interact / confirm", k => k.Cross, (k, v) => k.Cross = v,
            "Talk, open, pick up, read. Confirms in menus, speeds up text and skips movies."),
        Psx("Magic / guide", k => k.Triangle, (k, v) => k.Triangle = v,
            "Cast the equipped spell. In menus and shops: the description (Guide)."),
        Psx("Menu / back", k => k.Circle, (k, v) => k.Circle = v, "Opens the menu. Goes back in menus."),
        Psx("Pause", k => k.Start, (k, v) => k.Start = v, "Also skips movies."),
        Psx("Select button", k => k.Select, (k, v) => k.Select = v, "The PlayStation Select button."),
        Port("Map", () => B.Map, v => B.Map = v, "Opens and closes the full map (Settings > Map)."),

        Heading("Fixed keys"),
        Fixed("Quick save", "F2", "Saves the whole game to quicksave.kfstate."),
        Fixed("Quick load", "F4", "Loads quicksave.kfstate, also from the title screen."),
        Fixed("Free / capture mouse", "Esc"),
    ];

    public static void Install()
    {
        Localization.Merge(Strings);
        InputPage.Pads = 1;   // one player: no Pad 2 tab

        // KFIII_TEST_SETTINGS=8 opens Settings > Input 8 s after start;
        // KFIII_TEST_SETTINGS=8,kf3.qol opens that section instead (display, audio,
        // kf3.classic, kf3.qol, kf3.map).
        var ts = DevSwitch.Text("KFIII_TEST_SETTINGS")?.Split(',');
        if (ts != null && double.TryParse(ts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var at))
            DevSwitch.Background("kfiii_test_settings", () =>
            {
                Thread.Sleep((int)(at * 1000));
                SettingsPopup.Select = ts.Length > 1 ? ts[1] : "input";
                PopupManager.Open<SettingsPopup>();
            });

        if (!Controls.KeysOn) return;
        InputPage.KeyboardRows = () => _rows;
        InputPage.KeyboardExtras = DrawMouse;
        InputPage.KeyLabel = KeyHints.Pretty;
        InputPage.Changed = SaveAll;
        // Reset to Defaults restores the port's layout and mouse settings, not
        // RecompOne's stock keys.
        InputPage.ResetKeyboard = () =>
        {
            ConfigManager.Game.Keys = Controls.Layout();
            var s = Enhancements.Current;
            s.Input = new Controls.Binds();
            var d = new Enhancements.Settings();
            s.MouseSensitivity = d.MouseSensitivity;
            s.MouseSmoothing = d.MouseSmoothing;
            s.MouseInvertY = d.MouseInvertY;
            Enhancements.Apply();
            SaveAll();
        };
        InputPage.ButtonLabel = b => b switch
        {
            "Cross" => "Interact / run - Cross",
            "Circle" => "Menu / back - Circle",
            "Square" => "Attack - Square",
            "Triangle" => "Magic / guide - Triangle",
            "L1" => "Strafe left - L1",
            "R1" => "Strafe right - R1",
            "L2" => "Look up - L2",
            "R2" => "Look down - R2",
            "L3" => "L3 (unused)",
            "R3" => "Jump - R3",
            "Start" => "Pause - Start",
            "Select" => "Select",
            "Up" => "Walk forward - Up",
            "Down" => "Walk back - Down",
            "Left" => "Turn left - Left",
            "Right" => "Turn right - Right",
            _ => b,
        };
    }

    static void SaveAll()
    {
        ConfigManager.SaveGame();
        Enhancements.Save();
    }

    // KFIII_TEST_SETTINGS_END=1: scroll the page to the bottom, for a screenshot of the mouse settings.
    static readonly bool _testScroll = DevSwitch.Is("KFIII_TEST_SETTINGS_END");

    // The mouse settings, under the key table.
    static void DrawMouse()
    {
        var s = Enhancements.Current;
        bool changed = false;
        ImGui.Spacing();
        ImGui.SeparatorText("Mouse");

        int ms = Math.Clamp(s.MouseSensitivity, 10, 300);
        ImGui.SetNextItemWidth(260f);
        if (ImGui.SliderInt(Localization.T("kf3.enh.mouse_sens"), ref ms, 10, 300, "%d%%")) { s.MouseSensitivity = ms; changed = true; }
        Hint("kf3.enh.mouse_sens_hint");
        int msm = Math.Clamp(s.MouseSmoothing, 0, 100);
        ImGui.SetNextItemWidth(260f);
        if (ImGui.SliderInt(Localization.T("kf3.enh.mouse_smooth"), ref msm, 0, 100, msm == 0 ? "Off" : "%d")) { s.MouseSmoothing = msm; changed = true; }
        Hint("kf3.enh.mouse_smooth_hint");
        bool inv = s.MouseInvertY;
        if (ImGui.Checkbox(Localization.T("kf3.in.invert"), ref inv)) { s.MouseInvertY = inv; changed = true; }

        ImGui.Spacing();
        changed |= MouseButtonCombo("kf3.in.mouse_left", () => s.Input.MouseLeft, v => s.Input.MouseLeft = v);
        changed |= MouseButtonCombo("kf3.in.mouse_right", () => s.Input.MouseRight, v => s.Input.MouseRight = v);
        changed |= MouseButtonCombo("kf3.in.mouse_middle", () => s.Input.MouseMiddle, v => s.Input.MouseMiddle = v);
        ImGui.TextDisabled(Localization.T("kf3.in.mouse_note"));
        if (_testScroll) ImGui.SetScrollHereY(1f);

        if (changed)
        {
            Enhancements.Apply();
            Enhancements.Save();
        }
    }

    // A combo choosing what a mouse button does; true when changed.
    static bool MouseButtonCombo(string key, Func<int> get, Action<int> set)
    {
        int v = Math.Clamp(get(), 0, Controls.MouseActions.Length - 1);
        ImGui.SetNextItemWidth(260f);
        if (!ImGui.Combo(Localization.T(key), ref v, Controls.MouseActions, Controls.MouseActions.Length)) return false;
        set(v);
        return true;
    }

    static void Hint(string key)
    {
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Localization.T(key));
    }

    const string Strings = """
    {
      "strings": {
        "settings.input.keyboard": { "en": "Keyboard/Mouse" },
        "kf3.in.invert": { "en": "Invert mouse up/down" },
        "kf3.in.mouse_left": { "en": "Left button" },
        "kf3.in.mouse_right": { "en": "Right button" },
        "kf3.in.mouse_middle": { "en": "Middle button" },
        "kf3.in.mouse_note": { "en": "In menus and shops the mouse points and clicks instead (Mouse in menus, Settings > Quality of life)." }
      }
    }
    """;
}
