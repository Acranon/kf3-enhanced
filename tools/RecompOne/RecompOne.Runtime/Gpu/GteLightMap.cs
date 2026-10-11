using System.Runtime.CompilerServices;

namespace RecompOne.Runtime;

/// <summary>
/// 0048. What a primitive's vertex colours were made from, so the fragment shader can
/// make them again at every pixel instead of interpolating the three or four the GTE
/// produced.
///
/// A packet's colour is the end of a short chain: a lit colour (a face's
/// <c>NormalColorCol</c>, or a normal through <c>LLM</c>, <c>LCM</c> and <c>BK</c>),
/// then the depth cue, whose weight is a clamped and curved function of the view
/// depth. Interpolating the result across a polygon is only right where none of the
/// clamps, the curve's knee or the terminator falls inside it. The port that builds
/// the packet records the chain's inputs here, keyed by the packet's address; the GPU
/// finds them by the address it read the command word from, and checks the command
/// word and the first vertex word before believing them.
///
/// Per vertex: a lit colour (or, for <see cref="Directional"/>, the three light dot
/// products before their clamp) and the raw depth-cue value, <c>MAC0 / 4096</c>,
/// which is affine in 1/z and so exact under screen-space interpolation. Per
/// primitive: the curve and the light colour. Per batch: <c>BK</c> and <c>LCM</c>,
/// which the game sets per area, as uniforms.
///
/// Nothing here writes guest memory or the GTE. A miss draws the packet's own
/// colours, as before.
/// </summary>
public static class GteLightMap
{
    /// <summary>Mode byte, in the top byte of <see cref="Rec.Light"/>: set on every record.</summary>
    public const uint Present = 0x40;

    /// <summary>Mode byte: per-vertex light dots and a light colour rather than a lit colour.</summary>
    public const uint Directional = 0x80;

    /// <summary>Depth-cue curves, the low three bits of the mode byte.</summary>
    public const uint CurveNone = 0, CurveOffset = 1, CurveKnee = 2, CurveHalf = 3, CurveWord = 4;

    public struct Rec
    {
        // The packet's command and first vertex words once it was finished.
        public uint Cmd, Xy0;
        // Mode byte on top, the light colour below.
        public uint Light;
        public int Gen;
        public float L0x, L0y, L0z, F0;
        public float L1x, L1y, L1z, F1;
        public float L2x, L2y, L2z, F2;
        public float L3x, L3y, L3z, F3;
    }

    /// <summary>The port's switch.</summary>
    public static bool Enabled;

    /// <summary>The backend can draw it: the core-profile GL shaders.</summary>
    public static bool Supported;

    public static bool Active => Enabled && Supported;

    static Rec[]? _recs;
    static Rec _discard;
    static uint _base, _count;

    /// <summary>The address range packets are built in.</summary>
    public static void SetRange(uint baseAddress, uint bytes)
    {
        _base = baseAddress & 0x1FFFFFFFu;
        _count = bytes >> 2;
        _recs = new Rec[_count];
    }

    /// <summary>The record for the packet at <paramref name="pkt"/>, or a scratch one
    /// when it is outside the range.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref Rec Slot(uint pkt)
    {
        uint i = ((pkt & 0x1FFFFFFFu) - _base) >> 2;
        if (_recs == null || i >= _count) return ref _discard;
        Recorded++;
        return ref _recs[i];
    }

    /// <summary>The record for the packet whose command word the GPU read from
    /// <paramref name="cmdSrc"/>, if it still describes these words.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref readonly Rec Find(uint cmdSrc, uint cmd, uint xy0, out bool found)
    {
        found = false;
        uint i = ((cmdSrc & 0x1FFFFFFFu) - 4u - _base) >> 2;
        if (_recs == null || cmdSrc == 0 || i >= _count) { Misses++; return ref _discard; }
        ref readonly var r = ref _recs[i];
        if (r.Light == 0 || r.Cmd != cmd || r.Xy0 != xy0) { Misses++; return ref _discard; }
        found = true;
        Hits++;
        return ref r;
    }

    // BK and LCM by generation, so a batch uploads the pair its primitives were built
    // with. A game may switch between a few sets inside one frame (King's Field III:
    // the world's, the compass's, an item's), so a set already in the ring keeps its
    // generation: making a new one on every switch wrapped the ring before the frame
    // was drawn, and the compass took another set's ambient (white, then dark).
    // A new set takes the least recently *used* slot: evicting the oldest-made one
    // threw out the compass's set, in use every frame, whenever a scene made ~20 new
    // sets a second (King's Field III's forest), and for a frame the compass and the
    // trees blinked with each other's light. A generation is (serial << 5) | slot.
    const int GenRing = 32;
    static readonly int[] _bk = new int[GenRing * 3];
    static readonly short[] _lcm = new short[GenRing * 9];
    static readonly int[] _slotGen = new int[GenRing];
    static readonly long[] _slotUse = new long[GenRing];
    static int _gen, _serial;
    static long _useClock;

    /// <summary>The generation of the current BK and LCM: the one already holding
    /// them, else a new one in the least recently used slot.</summary>
    public static int NoteConstants()
    {
        GenCalls++;
        Span<int> bk = stackalloc int[3];
        Span<short> lcm = stackalloc short[9];
        for (int i = 0; i < 3; i++) bk[i] = (int)Gte.ReadControl(13 + i);
        for (int i = 0; i < 9; i++) lcm[i] = Lcm(i);
        long use = ++_useClock;
        // The current one first: by far the usual answer.
        int cur = _gen & (GenRing - 1);
        if (_gen != 0 && _slotGen[cur] == _gen && Matches(cur, bk, lcm)) { _slotUse[cur] = use; return _gen; }
        int lru = 0;
        for (int k = 0; k < GenRing; k++)
        {
            if (_slotGen[k] != 0 && Matches(k, bk, lcm)) { _slotUse[k] = use; return _gen = _slotGen[k]; }
            if (_slotUse[k] < _slotUse[lru]) lru = k;
        }

        int g = (++_serial << 5) | lru;
        if (g <= 0) { _serial = 1; g = (1 << 5) | lru; }
        for (int i = 0; i < 3; i++) _bk[lru * 3 + i] = bk[i];
        for (int i = 0; i < 9; i++) _lcm[lru * 9 + i] = lcm[i];
        _slotGen[lru] = g;
        _slotUse[lru] = use;
        GenNew++;
        return _gen = g;
    }

    static bool Matches(int slot, ReadOnlySpan<int> bk, ReadOnlySpan<short> lcm)
    {
        for (int i = 0; i < 3; i++) if (_bk[slot * 3 + i] != bk[i]) return false;
        for (int i = 0; i < 9; i++) if (_lcm[slot * 9 + i] != lcm[i]) return false;
        return true;
    }

    static short Lcm(int i) =>
        i == 8 ? (short)Gte.ReadControl(20) : (short)(Gte.ReadControl(16 + i / 2) >> ((i & 1) * 16));

    public static int Bk(int gen, int c) => _bk[(gen & (GenRing - 1)) * 3 + c];
    public static short LcmAt(int gen, int i) => _lcm[(gen & (GenRing - 1)) * 9 + i];

    /// <summary>Records written, and primitives that found theirs or did not.</summary>
    public static long Recorded, Hits, Misses;
    public static long GenCalls, GenNew;

    /// <summary>Polygons lit per pixel from the GTE's own records: ground (curve 5),
    /// models with each vertex's cue, models with the face's IR0; and polygons that
    /// were looked at and had no record.</summary>
    public static long LitGround, LitModel, LitFaceCue, LitMissed;

    public static void ResetCounters() => Recorded = Hits = Misses = 0;
}
