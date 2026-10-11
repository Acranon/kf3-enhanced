using RecompOne.Runtime.Config;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Hardware;
using RecompOne.Runtime.Host;
using RecompOne.Runtime.Host.Window;
using RecompOne.Runtime.Memory;
using Silk.NET.Input;

namespace KfIII;

/// <summary>
/// Keyboard and mouse controls: a WASD layout, a run key apart from interact, second
/// keys for walking, mouse look and the mouse buttons. The layout and the mouse code
/// started from Verdite2 (the RecompOne port of the first North American King's Field)
/// and its KeyLayout, Mouse and Analog patches, moved to King's Field III's addresses.
/// This file holds the keyboard; Mouse.cs the mouse.
///
/// The game tests the pad word (<see cref="GameAddr.PadThisFrame"/>, and
/// <see cref="GameAddr.PadLastFrame"/> for "new this frame") against a table of 14
/// button masks, one per action (<see cref="GameAddr.ActionButtons"/>, which
/// kf_ControlConfigScreen rewrites). The masks are in the game's byte order: the two
/// pad bytes swapped against RecompOne's <c>Controller</c>. Stock, by action slot:
///
///     0 Up        walk forward           kf_Move
///     1 Down      walk back              kf_Move
///     2 Left      turn left              kf_TurnAndLook
///     3 Right     turn right             kf_TurnAndLook
///     4 Triangle  magic (assumed from King's Field)   kf_Actions
///     5 Circle    open the menu          kf_Interact
///     6 Square    attack                 kf_Actions
///     7 Cross     interact (kf_Interact); held, run (kf_Actions)
///     8 L1, 10 R1 strafe                 kf_Move
///     9 L2, 11 R2 look up / down         kf_TurnAndLook
///     12 Select   second item slot
///     13 Start    pause                  kf_Pause
///
/// The verbs were settled by play, not by reading the code: read alone, kf_Interact
/// suggests Cross and Circle the other way round. As Verdite2 found, the table names
/// the button, not what it does.
///
/// The keyboard layout follows Verdite2's by action, not by button (see
/// <see cref="Layout"/>). The port's own keys (run, jump, map, second walk keys) and
/// the mouse buttons are <see cref="Binds"/>, saved in enhancements.json.
/// </summary>
public static partial class Controls
{
    // ---- the keyboard layout --------------------------------------------------------

    /// <summary>
    /// The port's layout: W/S walk, A/D strafe, arrows turn (Up/Down also walk, through
    /// <see cref="Binds.WalkAlt"/>), E interact, Tab menu, Q magic, keypad + attack
    /// (mostly done with the left mouse button), Enter pause, R Select. Run is left
    /// Shift and jump Space, both <see cref="Binds"/>.
    /// </summary>
    public static KeyBindings Layout() => new()
    {
        Up = "W",
        Down = "S",
        L1 = "A",
        R1 = "D",
        Left = "Left",
        Right = "Right",
        L2 = "",
        R2 = "",
        Cross = "E",           // interact
        Circle = "Tab",        // menu
        Triangle = "Q",        // magic
        Square = "KeypadAdd",  // attack
        Start = "Enter",
        Select = "R",
        L3 = "",
        R3 = "",
    };

    /// <summary>
    /// Layouts earlier builds of the port wrote to settings.json. A settings.json
    /// holding one is migrated to <see cref="Layout"/>, as one holding RecompOne's
    /// stock keys is.
    /// </summary>
    static readonly KeyBindings[] _superseded =
    [
        new()   // Cross and Circle the wrong way round
        {
            Up = "W", Down = "S", L1 = "A", R1 = "D", Left = "Left", Right = "Right",
            L2 = "", R2 = "", Circle = "E", Cross = "Tab", Triangle = "Q", Square = "KeypadAdd",
            Start = "Enter", Select = "ShiftRight", L3 = "", R3 = "",
        },
        new()   // Select on right Shift
        {
            Up = "W", Down = "S", L1 = "A", R1 = "D", Left = "Left", Right = "Right",
            L2 = "", R2 = "", Cross = "E", Circle = "Tab", Triangle = "Q", Square = "KeypadAdd",
            Start = "Enter", Select = "ShiftRight", L3 = "", R3 = "",
        },
    ];

    /// <summary>
    /// The port's own bindings, which the runtime's one-key-per-button table cannot
    /// hold. Saved in enhancements.json as "Input" and edited on Settings > Input >
    /// Keyboard/Mouse (ControlsPage).
    /// </summary>
    public sealed class Binds
    {
        public string Run { get; set; } = "ShiftLeft";
        public string Jump { get; set; } = "Space";
        public string Map { get; set; } = "M";
        /// Second keys for walking forward and back, added to the pad read (see
        /// SecondaryKeys), so they move the cursor in menus as well.
        public string WalkAlt { get; set; } = "Up";
        public string BackAlt { get; set; } = "Down";
        /// What each mouse button does: an index into <see cref="MouseActions"/>.
        public int MouseLeft { get; set; } = 1;
        public int MouseRight { get; set; } = 2;
        public int MouseMiddle { get; set; } = 3;
    }

    public static Binds B => Enhancements.Current.Input;

    /// <summary>False with KFIII_KEYS=stock: the port's layout and the Keyboard/Mouse page are off.</summary>
    public static bool KeysOn => _keys;

    static bool _keys = true;

    static readonly Dictionary<string, Key> _keyNames = new();

    /// <summary>Is the named key (a Silk.NET <c>Key</c> name, as the settings store it) held?</summary>
    public static bool KeyDown(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        Key k;
        lock (_keyNames)
            if (!_keyNames.TryGetValue(name, out k))
                _keyNames[name] = k = Enum.TryParse<Key>(name, out var p) ? p : Key.Unknown;
        return k != Key.Unknown && HostWindow.IsKeyDown(k);
    }

    /// <summary>Does the named key have the key code <paramref name="key"/> (a <c>KeyboardEvent.Key</c>)?</summary>
    public static bool IsKey(string name, int key) =>
        !string.IsNullOrEmpty(name) && Enum.TryParse<Key>(name, out var k) && (int)k == key;

    /// <summary>The jump key, or a mouse button set to jump, is held (Jump.cs).</summary>
    public static bool JumpHeld() => KeyDown(B.Jump) || MouseHeld(MouseJump);

    /// <summary>
    /// Reads the switches and puts the port's layout in place. Called from Program.cs
    /// before the runtime loads settings.json: the load keeps this layout when there is
    /// no settings.json and replaces it with the saved one when there is.
    /// </summary>
    public static void Configure()
    {
        _keys = DevSwitch.Text("KFIII_KEYS")?.ToLowerInvariant() != "stock";
        _mouse = !DevSwitch.Is("KFIII_MOUSE", "0");
        if (DevSwitch.Number("KFIII_MOUSE_SENS") is { } sens)
            EnvSens = (float)sens;
        EnvInvertY = InvertY = DevSwitch.Is("KFIII_MOUSE_INVERTY");

        // Only a first run's settings.json is written from this: an existing one is
        // loaded over it (and, in a release, was loaded already by the launcher, so
        // writing here would replace the player's own bindings). The migration in
        // Install covers a settings.json that still holds the stock keys.
        if (_keys && !File.Exists("settings.json")) ConfigManager.Game.Keys = Layout();
    }

    public static void Install()
    {
        // A settings.json still holding RecompOne's stock keys, or a layout this port
        // used to write, predates this layout: migrate it. Anything else was chosen by
        // hand and is left alone.
        Event.AddListener<RuntimeReadyEvent>(_ =>
        {
            if (!_keys) return;
            var keys = ConfigManager.Game.Keys;
            if (Matches(keys, Layout())) return;
            if (!Matches(keys, new KeyBindings()) && !_superseded.Any(old => Matches(keys, old))) return;
            ConfigManager.Game.Keys = Layout();
            ConfigManager.SaveGame();
            Console.WriteLine("[KFIII] keys: WASD layout applied (E interact/run, Tab menu, Q magic, " +
                              "mouse/keypad+ attack, Esc captures the mouse)");
        });

        if (_keys) Event.AddListener<PadReadEvent>(SecondaryKeys);

        // KFIII_TEST_RUN=from-to: hold "run" between those seconds after start.
        var testRun = DevSwitch.Text("KFIII_TEST_RUN")?.Split('-');
        if (testRun is { Length: 2 })
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            double from = double.Parse(testRun[0], inv), to = double.Parse(testRun[1], inv);
            DevSwitch.Background("kfiii_test_run", () =>
            {
                Thread.Sleep((int)(from * 1000)); ForceRun = true;
                Console.WriteLine("[KFIII] test: run on");
                Thread.Sleep((int)((to - from) * 1000)); ForceRun = false;
                Console.WriteLine("[KFIII] test: run off");
            });
        }

        // Esc captures the mouse for looking, and frees it.
        Event.AddListener<KeyboardEvent>(e =>
        {
            if (!e.Pressed || e.Key != (int)Key.Escape || !_mouse) return;
            if (!Captured && PopupManager.AnyOpen) return;
            SetCaptured(!Captured);
        });
    }

    // ---- second keys ----------------------------------------------------------------

    static ushort _extraPress, _extraRelease;
    static long _extraReadAt;

    /// <summary>
    /// Pad-read listener: presses the d-pad buttons that the second walk keys stand
    /// for. In a menu the strafe keys (A/D) also press Left/Right, and release L1/R1,
    /// because some screens read only the d-pad (the map of Ichrius' Key, func_8001C030,
    /// otherwise moves only with W/S). The pad is read many times a frame, so the keys
    /// are looked at once a millisecond at most.
    /// </summary>
    static void SecondaryKeys(PadReadEvent e)
    {
        if (e.Port != 0) return;
        long now = Environment.TickCount64;
        if (now != _extraReadAt)
        {
            _extraReadAt = now;
            _extraPress = 0;
            _extraRelease = 0;
            if (KeyDown(B.WalkAlt)) _extraPress |= Controller.Up;
            if (KeyDown(B.BackAlt)) _extraPress |= Controller.Down;
            if (Automap.InMenu)
            {
                if (KeyDown(ConfigManager.Game.Keys.L1)) { _extraPress |= Controller.Left; _extraRelease |= Controller.L1; }
                if (KeyDown(ConfigManager.Game.Keys.R1)) { _extraPress |= Controller.Right; _extraRelease |= Controller.R1; }
            }
        }

        if (_extraPress == 0) return;
        // The pad buffer is active low, with its two bytes swapped against Controller's
        // layout: clearing the swapped bit reads back as pressed.
        e.Buttons &= (ushort)~(ushort)((_extraPress >> 8) | (_extraPress << 8));
        e.Buttons |= (ushort)((_extraRelease >> 8) | (_extraRelease << 8));
    }

    // ---- run on its own key -----------------------------------------------------------
    //
    // Interact and run share one button, action slot 7 (GameAddr.InteractButton, Cross:
    // E in this layout). kf_Interact takes it as an interact when it is new this frame,
    // set in PadThisFrame and clear in PadLastFrame. kf_Actions runs when it is held, set
    // in both (the test at 0x80030010): that raises the running flags 0x801B24F3/24F4 and
    // drains stamina 0x801B2502/2506. Message boxes also read it held, to speed text up.
    //
    // So the bit is rewritten for the length of kf_Actions only, and put back after it:
    //
    //     new this frame                   left alone: an interact frame never runs
    //     run key, or the button on a pad  set in both: run
    //     otherwise                        cleared this frame: holding E does not run
    //
    // The run key never reaches the pad word, so it never interacts.

    const uint ThisFrame = GameAddr.PadThisFrame, LastFrame = GameAddr.PadLastFrame, InteractSlot = GameAddr.InteractButton;
    static ushort _savedThis, _savedLast;
    static bool _runRewritten;
    static readonly bool _debugPad = DevSwitch.Is("KFIII_DEBUG_PAD");
    static ushort _debugLastPad;
    static byte _debugLastRunning;

    /// <summary>Run as if the run key were held (KFIII_TEST_RUN).</summary>
    public static volatile bool ForceRun;

    /// <summary>Before kf_Actions: sets or clears the interact bit for its run test.</summary>
    public static void BeforeActions(CpuContext c, IMemory m)
    {
        _runRewritten = false;
        if (!_keys) return;
        ushort mask = m.ReadU16(InteractSlot);
        if (mask == 0) return;

        ushort now = m.ReadU16(ThisFrame), last = m.ReadU16(LastFrame);
        if (_debugPad && now != _debugLastPad)
        {
            _debugLastPad = now;
            Console.WriteLine($"[KFIII] pad word 0x{now:X4} (interact mask 0x{mask:X4})");
        }
        bool fresh = (now & mask) != 0 && (last & mask) == 0;
        if (fresh) return;

        // Held on a gamepad (not the keyboard's interact key or a mouse button): runs,
        // as on the console.
        bool padHeld = (now & mask) != 0 && !KeyDown(ConfigManager.Game.Keys.Cross) && !MouseHeld(MouseInteract);
        _savedThis = now;
        _savedLast = last;
        _runRewritten = true;
        if (ForceRun || padHeld || KeyDown(B.Run) || MouseHeld(MouseRun))
        {
            m.WriteU16(ThisFrame, (ushort)(now | mask));
            m.WriteU16(LastFrame, (ushort)(last | mask));
        }
        else m.WriteU16(ThisFrame, (ushort)(now & ~mask));
    }

    /// <summary>After kf_Actions: puts the pad words back as they were.</summary>
    public static void AfterActions(CpuContext c, IMemory m)
    {
        if (_debugPad && m.ReadU8(GameAddr.RunningFlag) != _debugLastRunning)
        {
            _debugLastRunning = m.ReadU8(GameAddr.RunningFlag);
            Console.WriteLine($"[KFIII] running flag {_debugLastRunning}");
        }
        if (!_runRewritten) return;
        _runRewritten = false;
        m.WriteU16(ThisFrame, _savedThis);
        m.WriteU16(LastFrame, _savedLast);
    }

    static bool Matches(KeyBindings a, KeyBindings b) =>
        a.Cross == b.Cross && a.Circle == b.Circle && a.Square == b.Square &&
        a.Triangle == b.Triangle && a.L1 == b.L1 && a.R1 == b.R1 &&
        a.L2 == b.L2 && a.R2 == b.R2 && a.L3 == b.L3 && a.R3 == b.R3 &&
        a.Start == b.Start && a.Select == b.Select &&
        a.Up == b.Up && a.Down == b.Down && a.Left == b.Left && a.Right == b.Right;
}
