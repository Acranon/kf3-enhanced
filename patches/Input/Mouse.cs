using System.Diagnostics;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Hardware;
using RecompOne.Runtime.Host;
using RecompOne.Runtime.Host.Window;
using RecompOne.Runtime.Memory;
using MouseButton = Silk.NET.Input.MouseButton;

namespace KfIII;

// Mouse look and the mouse buttons while playing (in menus, MenuMouse.cs). The
// keyboard half of Controls, and the overview, are in Controls.cs.
public static partial class Controls
{
    static bool _mouse = true;

    /// <summary>Settings "Mouse sensitivity" times KFIII_MOUSE_SENS (<see cref="EnvSens"/>).</summary>
    public static float Sens = 1.0f, EnvSens = 1.0f;
    /// <summary>Settings "Invert mouse up/down", or KFIII_MOUSE_INVERTY (<see cref="EnvInvertY"/>).</summary>
    public static bool InvertY, EnvInvertY;
    /// <summary>The pointer is captured for looking (Esc toggles it).</summary>
    public static bool Captured { get; private set; }

    // ---- mouse buttons ----------------------------------------------------------------

    /// <summary>What a mouse button can be set to (<see cref="Binds.MouseLeft"/> and the others hold an index).</summary>
    public static readonly string[] MouseActions = ["Nothing", "Attack", "Magic", "Interact", "Run", "Jump"];
    const int MouseAttack = 1, MouseMagic = 2, MouseInteract = 3, MouseRun = 4, MouseJump = 5;

    /// <summary>Is a mouse button set to <paramref name="action"/> held, with the pointer captured?</summary>
    static bool MouseHeld(int action) => Captured && _mouse &&
        ((B.MouseLeft == action && HostWindow.IsMouseButtonDown(MouseButton.Left)) ||
         (B.MouseRight == action && HostWindow.IsMouseButtonDown(MouseButton.Right)) ||
         (B.MouseMiddle == action && HostWindow.IsMouseButtonDown(MouseButton.Middle)));

    // The pad button for the actions that are one. By default left attacks (Square),
    // right casts (Triangle) and middle interacts (Cross). Circle is not offered: it
    // opens the menu, a trap under a captured pointer. Run and jump are read directly
    // (MouseHeld), not through the pad.
    static ushort MouseBit(int action) => action switch
    {
        MouseAttack => Controller.Square,
        MouseMagic => Controller.Triangle,
        MouseInteract => Controller.Cross,
        _ => 0,
    };

    static readonly Action<PadReadEvent> _buttons = Buttons;

    // Pad-read listener while the pointer is captured: presses the buttons the mouse
    // buttons stand for. A popup opening frees the pointer.
    static void Buttons(PadReadEvent e)
    {
        if (e.Port != 0 || !Captured) return;
        if (PopupManager.AnyOpen) { SetCaptured(false); return; }

        ushort press = 0;
        if (HostWindow.IsMouseButtonDown(MouseButton.Left)) press |= MouseBit(B.MouseLeft);
        if (HostWindow.IsMouseButtonDown(MouseButton.Right)) press |= MouseBit(B.MouseRight);
        if (HostWindow.IsMouseButtonDown(MouseButton.Middle)) press |= MouseBit(B.MouseMiddle);
        if (press == 0) return;
        // Active low, bytes swapped (see SecondaryKeys).
        e.Buttons &= (ushort)~(ushort)((press >> 8) | (press << 8));
    }

    /// <summary>Captures or frees the pointer for looking.</summary>
    public static void SetCaptured(bool on)
    {
        if (on == Captured) return;
        HostWindow.MouseCaptured = on;
        Captured = HostWindow.MouseCaptured;
        if (Captured) Event.AddListener(_buttons);
        else Event.RemoveListener(_buttons);
        ClearMotion();
        _lastLookAt = Environment.TickCount64;
        _carryYaw = _carryPitch = 0;
        Console.WriteLine(Captured ? "[KFIII] mouse captured (Esc releases)" : "[KFIII] mouse released");
    }

    // ---- mouse look ---------------------------------------------------------------------

    /// Angles are 12-bit, 4096 to the circle; 0.15 degrees per window pixel at 100%,
    /// as in Verdite2.
    const float UnitsPerPixel = 0.15f * 4096f / 360f;
    /// The most one tick turns or looks, in angle units (a quarter turn).
    const float StepCap = 1024f;
    /// Motion gathered over a longer gap between look calls (a menu, a pickup) is
    /// dropped rather than applied in one jump.
    const long StaleMs = 250;

    const uint Yaw = GameAddr.Yaw, Pitch = GameAddr.Pitch;
    const int PitchLimit = GameAddr.PitchLimit;

    // The fractions of an angle unit not yet applied, carried to the next tick.
    static float _carryYaw, _carryPitch;
    static long _lastLookAt;

    /// <summary>
    /// Before kf_TurnAndLook, which adds a turn and a look velocity to yaw (increases
    /// turning left) and pitch (held within +-PitchLimit, increases looking down, R2).
    /// The mouse motion goes straight onto the angles, with the same limits; the
    /// routine's own velocities still serve the keys.
    /// </summary>
    public static void BeforeLook(CpuContext c, IMemory m)
    {
        LookCalls++;
        var (dx, dy) = TakeMotion();
        dx += _pendDx; dy += _pendDy;
        _pendDx = _pendDy = 0;
        LastYaw = LastPitch = 0;
        long now = Environment.TickCount64;
        long since = now - _lastLookAt;
        _lastLookAt = now;
        if (!Looking || since > StaleMs) return;

        float turn = Math.Clamp(-dx * UnitsPerPixel * Sens + _carryYaw, -StepCap, StepCap);
        float look = Math.Clamp(dy * UnitsPerPixel * Sens * (InvertY ? -1f : 1f) + _carryPitch, -StepCap, StepCap);
        int turnUnits = (int)turn, lookUnits = (int)look;
        _carryYaw = turn - turnUnits;
        _carryPitch = look - lookUnits;

        if (turnUnits != 0)
            m.WriteU16(Yaw, (ushort)((m.ReadU16(Yaw) + turnUnits) & 0xFFF));
        LastYaw = turnUnits;

        if (lookUnits != 0)
        {
            int p = m.ReadU16(Pitch) & 0xFFF;
            if (p >= 0x800) p -= 0x1000;
            int q = Math.Clamp(p + lookUnits, -PitchLimit, PitchLimit);
            m.WriteU16(Pitch, (ushort)(q & 0xFFF));
            LastPitch = q - p;
        }
    }

    /// Mouse look is live: the pointer is captured, or a test is moving a fake mouse.
    static bool Looking => _mouse && (Captured || _testDy != 0 || _testDx != null);

    // ---- smoothing ------------------------------------------------------------------------
    //
    // A mouse reports in uneven bursts (its polling rate beats against the frame rate),
    // so one frame gets two reports and the next none, and the view stutters. Motion
    // goes into a backlog that is let out exponentially with the time constant SmoothMs.
    // Nothing is lost, only spread over a few frames: the total turn is the same.

    /// <summary>Settings "Mouse smoothing", as a time constant in ms; 0 passes motion straight through.</summary>
    public static float SmoothMs = 0f;
    static float _backDx, _backDy;
    static long _smoothAt;

    // KFIII_TEST_MOUSE_DY=pixels/s: a steady fake vertical movement (looking up and down).
    static readonly double _testDy = DevSwitch.Number("KFIII_TEST_MOUSE_DY") ?? 0;

    // KFIII_TEST_MOUSE_DX=pixels/s,from,to: a fake turn between those seconds after start.
    static readonly double[]? _testDx = DevSwitch.Text("KFIII_TEST_MOUSE_DX")?.Split(',')
        .Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
    static readonly long _testStart = Stopwatch.GetTimestamp();

    /// The motion to apply now, in window pixels: the new motion (plus any test motion),
    /// smoothed. After a gap of more than 250 ms the backlog is let out at once.
    static (float, float) TakeMotion()
    {
        var (dx, dy) = HostWindow.TakeMouseMotion();
        long now = Stopwatch.GetTimestamp();
        double dt = (now - _smoothAt) * 1000.0 / Stopwatch.Frequency;
        _smoothAt = now;
        if (_testDy != 0 && dt < 250) dy += (float)(_testDy * dt / 1000.0);
        if (_testDx is { Length: 3 } && dt < 250)
        {
            double t = (now - _testStart) / (double)Stopwatch.Frequency;
            if (t >= _testDx[1] && t < _testDx[2]) dx += (float)(_testDx[0] * dt / 1000.0);
        }
        if (SmoothMs <= 0f) { dx += _backDx; dy += _backDy; _backDx = _backDy = 0; return (dx, dy); }
        _backDx += dx; _backDy += dy;
        float k = dt > 250 ? 1f : (float)(1.0 - Math.Exp(-dt / SmoothMs));
        float ox = _backDx * k, oy = _backDy * k;
        _backDx -= ox; _backDy -= oy;
        return (ox, oy);
    }

    static void ClearMotion() { HostWindow.TakeMouseMotion(); _backDx = _backDy = 0; }

    // ---- mouse look between ticks (patches/Graphics/Smooth.cs) -------------------------------
    //
    // Smooth draws several frames per 15 Hz world tick, with the camera interpolated a
    // tick behind. Mouse look must not lag like that, so the drawn view gets the turn
    // the last tick applied in full (LastYaw/LastPitch, not interpolated) plus the
    // motion gathered since (Preview). That motion is held in _pendDx/_pendDy and
    // applied by the next BeforeLook, so the game sees exactly the same input.

    static float _pendDx, _pendDy;
    /// <summary>How many times kf_TurnAndLook has run.</summary>
    public static long LookCalls;

    /// <summary>Mouse turn and look applied by the last tick, in angle units.</summary>
    public static int LastYaw { get; private set; }
    public static int LastPitch { get; private set; }

    // Whether the game is reading the mouse at all. kf_TurnAndLook is not called while
    // an item is being picked up (the camera is held on it), in message boxes or in
    // menus; the preview must not turn the picture then either, or the view swings
    // freely and snaps back at the next tick.
    static bool _lookLive;
    static long _looksSeen;

    /// <summary>From Smooth at the start of each tick: notes whether the last tick ran kf_TurnAndLook.</summary>
    public static void NewTick()
    {
        _lookLive = LookCalls != _looksSeen;
        _looksSeen = LookCalls;
        if (!_lookLive)
        {
            LastYaw = LastPitch = 0;
            _pendDx = _pendDy = 0;
            _backDx = _backDy = 0;
        }
    }

    /// <summary>
    /// Mouse turn and look gathered since the last tick, in angle units, not yet applied
    /// to the game.
    /// </summary>
    public static (float Yaw, float Pitch) Preview()
    {
        if (!Looking || !_lookLive) return (0f, 0f);
        var (dx, dy) = TakeMotion();
        _pendDx += dx; _pendDy += dy;
        return (-_pendDx * UnitsPerPixel * Sens + _carryYaw,
                _pendDy * UnitsPerPixel * Sens * (InvertY ? -1f : 1f) + _carryPitch);
    }
}
