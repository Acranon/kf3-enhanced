using RecompOne.Runtime.Memory;

namespace KfIII;

// High frame rate: the state kf_Render advances on every call, put back after each
// redraw so that it steps once a tick as on the console.
//
//   * kf_StepModelAnims, called by the model drawer, advances a counter and the
//     frame byte (+5) of each of its animation slots: water, fire and model
//     animations would run several times too fast.
//   * kf_Render's own call counter (GameAddr.RenderCounter), which the water and
//     other texture animation run off.
//   * kf_UpdateMessage fades the current on-screen message in, holds it for a
//     countdown and fades it out, then frees its slot ("Empty" from a barrel, item
//     names): run once per draw, messages would flash by. Its state is the queue
//     and its indexes, phase, hold and fade bytes (GameAddr.MessageQueue).
public static partial class Smooth
{
    const uint AnimCounter = GameAddr.ModelAnimCounter, AnimSlots = GameAddr.ModelAnimSlots;
    const int AnimSlotCount = GameAddr.ModelAnimSlotCount, AnimStride = GameAddr.ModelAnimSlotStride;
    const uint DrawCounter = GameAddr.RenderCounter;
    const uint MessageState = GameAddr.MessageQueue;
    const int MessageStateLen = GameAddr.MessageStateLength;

    /// <summary>The per-call state a redraw (or Mirror's mirrored draw) puts back.
    /// Each caller keeps its own: Mirror's draw runs inside a redraw, and sharing one
    /// would let its save overwrite the redraw's with mid-frame values.</summary>
    public sealed class TickState
    {
        public uint DrawCounter, AnimCounter;
        public readonly byte[] Message = new byte[MessageStateLen];
        public readonly byte[] AnimFrames = new byte[AnimSlotCount];
    }

    static readonly TickState _tick = new();

    static void SaveTickState(IMemory m) => SaveTickState(m, _tick);
    static void RestoreTickState(IMemory m) => RestoreTickState(m, _tick);

    public static void SaveTickState(IMemory m, TickState t)
    {
        t.DrawCounter = m.ReadU32(DrawCounter);
        for (int i = 0; i < MessageStateLen; i++) t.Message[i] = m.ReadU8(MessageState + (uint)i);
        t.AnimCounter = m.ReadU32(AnimCounter);
        for (int i = 0; i < AnimSlotCount; i++) t.AnimFrames[i] = m.ReadU8(AnimSlots + 5 + (uint)(i * AnimStride));
    }

    public static void RestoreTickState(IMemory m, TickState t)
    {
        m.WriteU32(DrawCounter, t.DrawCounter);
        for (int i = 0; i < MessageStateLen; i++) m.WriteU8(MessageState + (uint)i, t.Message[i]);
        m.WriteU32(AnimCounter, t.AnimCounter);
        for (int i = 0; i < AnimSlotCount; i++) m.WriteU8(AnimSlots + 5 + (uint)(i * AnimStride), t.AnimFrames[i]);
    }
}
