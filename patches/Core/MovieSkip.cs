using RecompOne.Runtime.Context;
using RecompOne.Runtime.Hardware;
using RecompOne.Runtime.Memory;

namespace KfIII;

/// <summary>
/// Lets the in-game movies (GAME.EXE's STR player, such as the elf scene
/// STR\S03.S after New Game) be skipped with Start or Cross, which the original
/// does not allow.
///
/// The player is kf_MoviePlay. Its frame loop ends at the stream's last frame, or
/// when <see cref="GameAddr.MovieStatus"/> is 0x63, the status its frame fetch
/// (kf_MovieFetchFrame) writes when it sees the stream end. Writing 0x63 there takes the
/// game's own ending path: the fade, StUnSetRing, the CD cleanup, and whatever the
/// caller does next. Only a fresh press counts: a button already held when the
/// movie starts does not skip it.
/// </summary>
public static class MovieSkip
{
    const uint Status = GameAddr.MovieStatus;
    const ushort StreamEnded = 0x63;
    const ushort Buttons = Controller.Start | Controller.Cross;
    /// Milliseconds without a frame after which the next frame belongs to a new movie.
    const long NewMovieGapMs = 500;

    static readonly bool _enabled = !DevSwitch.Is("KFIII_MOVIESKIP", "0");
    static bool _held = true;
    static long _lastFrameMs;

    /// <summary>
    /// Set while a quick load is on its way from the title (SaveState): the movie is
    /// skipped without a press, since the state about to be applied replaces it.
    /// </summary>
    public static volatile bool Force;

    /// <summary>Pre-hook on kf_MovieFlip, which the movie player calls once a frame at the top of its loop and nowhere else.</summary>
    public static void EachFrame(CpuContext c, IMemory m)
    {
        if (Force)
        {
            m.WriteU16(Status, StreamEnded);
            return;
        }

        if (!_enabled) return;

        long now = Environment.TickCount64;
        if (now - _lastFrameMs > NewMovieGapMs) _held = true;
        _lastFrameMs = now;

        // Controller.State is active low.
        bool down = (~Controller.State & Buttons) != 0;
        if (down && !_held)
        {
            m.WriteU16(Status, StreamEnded);
            Console.WriteLine("[KFIII] movie skipped");
        }

        _held = down;
    }
}
