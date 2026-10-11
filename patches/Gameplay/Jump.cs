using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Host;
using RecompOne.Runtime.Memory;

namespace KfIII;

/// <summary>
/// A jump on Space (or R3 on a gamepad), built out of the game's own fall (Settings
/// "Jump"). Ported from the jump mod of Verdite2 (the RecompOne port of the first North
/// American King's Field), whose game has the same routine.
///
/// kf_PlayerVertical, called from kf_WorldUpdate every tick, is a state machine on
/// <see cref="GameAddr.FallState"/>, with the fall speed in
/// <see cref="GameAddr.FallSpeed"/> (Y grows downward, so negative is up):
///
///     0x00  on the ground      0x10 / 0x20  short fall / ledge hop
///     0x40  long fall          0x50         the landing dip
///
/// The 0x40 arm moves Y by the speed, adds 40 of gravity, and tests the new position
/// (kf_Collide). A hit while rising zeroes the speed (a ceiling); a hit while falling
/// lands, with damage only at a speed of 480 or more (func_8002A6F4), then the 0x50
/// dip. So a jump is two writes before the routine runs, on a tick the player is on
/// the ground: state 0x40 and a negative speed. Launched below 480, a jump lands below
/// 480 on flat ground and costs nothing; jumping off a ledge hurts as walking off it
/// would.
///
/// The rise is slowed by <see cref="RiseSpeed"/> as in Verdite2 (launch at strength * s,
/// gravity 40 * s^2: the same height, gentler). Verdite2's world ticks at 20 Hz and this
/// one at 15, so the whole jump is also time-scaled by k = 4/3 (speeds * k, gravity *
/// k^2) to take the same time on screen. The faster fall is held under the
/// landing-damage speed until back at the take-off height, then left to the game.
///
/// The press is latched from the pad read (many a frame) and used on the next tick, so
/// a tap between the world's ticks is not lost; one older than <see cref="LatchMs"/> is
/// dropped, so a press in the air or in a menu does not jump later.
/// </summary>
public static class Jump
{
    public static bool Enabled { get; private set; } = true;
    /// <summary>Launch speed: the peak is about strength^2 / 80 (eye height is 1600).</summary>
    public static int Strength { get; private set; } = 373;
    /// <summary>Low, normal and high (Settings "Jump height"); all land under 480.</summary>
    public static readonly int[] Strengths = [280, 373, 440];

    const float RiseSpeed = 0.8f;
    const int Gravity = 40, LatchMs = 150;
    /// Time scale from Verdite2's 20 Hz world to this one's 15 Hz.
    const float K = 20f / 15f;
    /// Under the landing-damage speed of 480.
    const int SafeLanding = 470;

    const uint State = GameAddr.FallState, Speed = GameAddr.FallSpeed, PlayerY = GameAddr.PlayerPos + 4;
    const byte OnGround = 0x00, LongFall = 0x40;
    /// R3 as an SDL GameControllerButton, which HostWindow.IsPadButtonDown takes.
    const int R3 = 8;

    static readonly bool _probe = DevSwitch.Is("KFIII_JUMP_PROBE");
    static int _probeTicks;

    static bool _held;
    static long _pressedAt = -1;

    // The jump in progress: the port is steering the speed (_managed), still rising
    // (_rising), the unrounded speed and the value last written.
    static bool _managed, _rising;
    static float _velocity;
    static short _written;
    static int _takeoffY;

    public static void Apply(bool on, int height)
    {
        Enabled = on;
        Strength = Strengths[Math.Clamp(height, 0, Strengths.Length - 1)];
    }

    public static void Install()
    {
        Event.AddListener<PadReadEvent>(e =>
        {
            if (e.Port != 0) return;
            bool down = Controls.JumpHeld() || HostWindow.IsPadButtonDown(R3);
            if (down && !_held) _pressedAt = Environment.TickCount64;
            _held = down;
        });

        DevSwitch.At("KFIII_TEST_JUMP", at =>
        {
            Console.WriteLine($"[KFIII] test: jump at {at}s");
            _pressedAt = Environment.TickCount64;
        });
    }

    /// <summary>Before kf_PlayerVertical: starts a latched jump if the player is on the ground and alive.</summary>
    public static void BeforeVertical(CpuContext c, IMemory m)
    {
        if (_probe && _probeTicks > 0)
        {
            _probeTicks--;
            Console.WriteLine($"[KFIII] jump: y {(int)m.ReadU32(PlayerY)} speed {(short)m.ReadU16(Speed)} state 0x{m.ReadU8(State):X2} hp {m.ReadU16(GameAddr.Hp)}");
        }

        long at = _pressedAt;
        if (at < 0) return;
        if (Environment.TickCount64 - at > LatchMs) { _pressedAt = -1; return; }
        if (!Enabled || m.ReadU8(State) != OnGround || m.ReadU16(GameAddr.Hp) == 0) return;
        _pressedAt = -1;

        _velocity = -Strength * RiseSpeed * K;
        _written = (short)MathF.Round(_velocity);
        _managed = _rising = true;
        _takeoffY = (int)m.ReadU32(PlayerY);
        m.WriteU8(State, LongFall);
        m.WriteU16(Speed, unchecked((ushort)_written));
        if (_probe) { _probeTicks = 40; Console.WriteLine($"[KFIII] jump: launch {_written} from y {_takeoffY}"); }
    }

    /// <summary>
    /// After kf_PlayerVertical: the 0x40 arm has just moved Y and added 40 to the speed.
    /// While the jump is the port's, that is replaced by the scaled gravity (gentler on
    /// the way up); on the way down the speed is kept under the landing-damage speed
    /// until back at the take-off height. Anything but exactly +40 means the game
    /// stepped in (a ceiling, a landing) and the rest is left to it; so is a drop below
    /// the take-off height.
    /// </summary>
    public static void AfterVertical(CpuContext c, IMemory m)
    {
        if (!_managed) return;
        short now = unchecked((short)m.ReadU16(Speed));
        if (m.ReadU8(State) != LongFall || now != _written + Gravity) { _managed = false; return; }

        if (_rising)
        {
            _velocity += Gravity * RiseSpeed * RiseSpeed * K * K;
            if (_velocity >= 0) { _rising = false; _velocity = 0; }
        }
        else
        {
            _velocity = Math.Min(_velocity + Gravity * K * K, SafeLanding);
            if ((int)m.ReadU32(PlayerY) > _takeoffY) _managed = false;
        }
        _written = (short)MathF.Round(_velocity);
        m.WriteU16(Speed, unchecked((ushort)_written));
    }
}
