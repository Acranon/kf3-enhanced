using System.Runtime.CompilerServices;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime;

/// <summary>
/// The GTE's per-vertex depth and sub-pixel position, carried to the GPU by the one
/// thing that identifies a vertex exactly: <b>the address of the word its screen
/// coordinate lives at</b>.
///
/// <see cref="GteDepth"/> keyed the same two numbers on the screen position, because
/// that is what visibly survives into a GP0 packet. But a screen position is not a
/// vertex. Two vertices land on one pixel constantly — a distant wall behind a near
/// column — and every off-screen vertex collapses onto the ±1024 clamp, so the answer
/// was *a* depth for that pixel rather than *this* vertex's depth, and everything
/// built on top of it (four samples per key, a geometric-mean pick per primitive, an
/// outlier veto) was a heuristic guessing which. W is the denominator of the
/// perspective divide, so a wrong guess does not degrade the texture, it detonates
/// it; and the same wrong guess hands a vertex another vertex's fraction, which is a
/// corner that jumps a pixel for no reason the player can see.
///
/// There is an exact identity available, and it survives the whole way:
///
///   * A screen coordinate leaves the GTE only through <c>swc2</c> of SXY0/1/2 —
///     the recompilation contains no <c>mfc2</c> of those registers at all — which
///     emits as <c>m.WriteU32(addr, Gte.StoreWord(14))</c>. The destination address
///     is in hand at the store.
///   * King's Field projects a whole vertex list into an 8-byte-per-vertex scratch
///     array first and assembles polygons later, and the assembler copies each
///     coordinate into the packet as a whole 32-bit word — <c>lw</c> immediately
///     followed by <c>sw</c>, into the <c>xy0..xy3</c> fields of a POLY_GT3/GT4.
///   * The packet reaches the GPU by <c>DrawOTag</c> or by DMA, both of which read
///     each word out of guest memory at an address they know.
///
/// So: record the attributes at the address the GTE stores to, follow them across the
/// copy, and look them up at the address the GPU read the word from. Three exact
/// hops, no search, nothing to pick between.
///
/// The copy is followed with a small <b>pending ring</b> rather than by tainting
/// registers, which would mean instrumenting every instruction the recompiler emits.
/// A GTE store and a load from a mapped address both publish (value, attributes) into
/// the ring; a store whose value matches an entry takes its attributes. That is the
/// <c>lw</c>/<c>sw</c> pair exactly, and the ring is searched newest-first preferring
/// an entry that has not already been matched, so three <c>swc2</c>s of three
/// identical clamped coordinates still bind in the order they were stored.
///
/// A lookup verifies the stored word against the word the GPU is actually drawing, so
/// an entry that was overwritten — by a half-word write, by a packet being rebuilt,
/// by anything at all — cannot answer with someone else's depth. A vertex the map
/// does not know is a miss, and a miss is the affine interpolation the console did,
/// which is what makes this safe to leave on.
///
/// Cost: everything is behind <see cref="Active"/>, and then behind a one-bit test in
/// a presence bitmap of one bit per RAM word (64 KB for the retail 2 MB). The
/// attribute array is allocated on first use and touched only on a bitmap hit.
/// </summary>
public static class GteVertexMap
{
    /// <summary>What a vertex carries: the view depth SZ3 the GTE divided by, and the
    /// [0, 1) fraction of a pixel it truncated off the screen position. The fraction
    /// is zero for a vertex that saturated at ±1024, where the packet coordinate is
    /// the whole position the GPU must keep.</summary>
    public struct Attr
    {
        public float Z, Fx, Fy;
        public bool Clipped;
        public float Vx, Vy, Vz;
        public bool HasView;
        public float Ph, Pox, Poy;
        // Smooth fog (GteDepth.SmoothFog). On a coordinate word: the depth cue's raw
        // MAC0 / 4096 for this vertex, before the GTE clamped it into IR0 (NaN: none).
        // On a colour word: the colour before the depth cue faded it.
        public float FogRaw;
        public uint PreFog; public bool HasPreFog;
        /// <summary>Watched stores since the attributes were bound to this word.</summary>
        public uint Age;
        // Per-pixel lighting. On a coordinate word: the hardware depth cue
        // (DQB + DQA * H / SZ) / 4096 for this vertex, unclamped (NaN: none).
        // On a colour word: what NCDS/NCDT lit it from -- the three light dots
        // (LLM . normal / 4096), the material colour, the IR0 it was cued with and
        // the BK/LCM generation (GteLightMap).
        public float Cue;
        public bool HasLit; public float D0, D1, D2, LitIr0; public uint Rgbc; public int LitGen;
        public byte Tag;   // what drew it (CurrentTag at the GTE op): the port's source id
    }

    struct Entry
    {
        public uint Value;      // the packed XY word this attribute belongs to
        public uint Seq;
        public float Z, Fx, Fy;
        public bool Clipped;
        public float Vx, Vy, Vz;
        public bool HasView;
        public float Ph, Pox, Poy;
        public float FogRaw;
        public uint PreFog; public bool HasPreFog;
        public float Cue;
        public bool HasLit; public float D0, D1, D2, LitIr0; public uint Rgbc; public int LitGen;
        public byte Tag;   // what drew it (CurrentTag at the GTE op): the port's source id
    }

    struct Pending
    {
        public uint Value;
        public uint Tick;
        public float Z, Fx, Fy;
        public bool Clipped;
        public float Vx, Vy, Vz;
        public bool HasView;
        public float Ph, Pox, Poy;
        public float FogRaw;
        public uint PreFog; public bool HasPreFog;
        public float Cue;
        public bool HasLit; public float D0, D1, D2, LitIr0; public uint Rgbc; public int LitGen;
        public byte Tag;   // what drew it (CurrentTag at the GTE op): the port's source id
        public bool Matched;
        // Straight out of the GTE rather than copied from another address. Only the
        // counters care, but the difference between the two is the whole picture of
        // whether the association is being followed or only started.
        public bool Root;
        // A packed (0, 0) is a real coordinate, so a slot needs saying-so of its own
        // rather than a zero value standing in for "never used".
        public bool Live;
    }

    /// <summary>Nothing is recorded, followed or looked up while this is false, so the
    /// map costs one predictable branch on the memory path when neither perspective
    /// correction, sub-pixel positioning nor the Z-buffer wants it. <see cref="GteDepth"/> owns it.</summary>
    public static bool Active;

    // A vertex word copied more than this many RAM writes after it was read is not
    // the copy we were following. Generous: the lw/sw pairs this exists for are
    // adjacent instructions, and an entry that ages out simply stops answering.
    const uint PendingMaxAge = 64;

    // An attribute older than this has to be from a packet that was built and never
    // rebuilt, which the value check would almost certainly catch anyway. Roughly a
    // frame of stores.
    const uint EntryMaxAge = 1u << 20;

    const int PendingCount = 8;

    static readonly Pending[] _pending = new Pending[PendingCount];
    static int _pendingHead;

    // Lets NoteWrite skip the ring scan: nothing can match once the newest entry has
    // aged out, or when no published value set this value's hash bit.
    static uint _newestPendingTick;
    static ulong _pendingValueBits;

    static ulong ValueBit(uint value) => 1UL << (int)((value * 0x9E3779B1u) >> 26);

    static Entry[]? _map;
    static ulong[]? _mark;
    static uint _ramMask;
    static uint _tick;

    /// <summary>Vertices whose attributes were recorded straight from a GTE store,
    /// and words that carried them on to another address — the two halves of the
    /// association. <see cref="Hits"/> and <see cref="Misses"/> are the GPU end:
    /// vertex words that found their own attributes, and those that did not and so
    /// stayed affine.</summary>
    public static long Roots, Propagated, Hits, Misses;

    public static void ResetCounters() => Roots = Propagated = Hits = Misses = 0;

    /// <summary>0046. Never reset, so a frame capture can difference them across a
    /// call: coordinates the GTE offered, loads that offered a known word on, ring
    /// scans past the filter, and stores that bound. <see cref="Stores"/> is every
    /// store watched, and wraps.</summary>
    public static long TracePublished, TraceRepublished, TraceScans, TraceBound;

    public static uint Stores => _tick;

    /// <summary>Called when either half of the feature is switched on or off. The
    /// arrays are allocated on the first switch-on and then kept, since turning the
    /// setting off and on again in the menu is not a reason to hand 10 MB back.</summary>
    public static void SetActive(bool on)
    {
        if (on && _map == null)
        {
            Allocate();
        }
        // Switching off stops the stores being watched, so every address the game
        // wrote in the meantime is still marked and still holds what it meant
        // before. Switching on again forgets the lot rather than trusting an
        // address whose word happens to have come back around.
        else if (on && !Active && _mark != null)
        {
            Array.Clear(_mark);
        }
        Active = on && _map != null;
    }

    // Sized from the guest's RAM, which PSMemory may construct after a patch has
    // already switched the map on.
    static void Allocate()
    {
        uint ram = Runtime.RamSize;
        _ramMask = ram - 1;
        _map = new Entry[ram >> 2];
        _mark = new ulong[(ram >> 2) / 64];
    }

    internal static void RamSizeChanged()
    {
        if (_map != null && (uint)_map.Length != Runtime.RamSize >> 2) Allocate();
    }

    static int Index(uint phys) => (int)((phys & _ramMask) >> 2);

    static bool Marked(int i) => (_mark![i >> 6] & (1UL << (i & 63))) != 0;

    /// <summary>The presence bit alone, for the memory fast path to test before calling
    /// <see cref="NoteRead"/>. Only meaningful while <see cref="Active"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool MaybeBound(uint phys) => Marked(Index(phys));

    static void Mark(int i) => _mark![i >> 6] |= 1UL << (i & 63);

    static void Unmark(int i) => _mark![i >> 6] &= ~(1UL << (i & 63));

    /// <summary>Offer a value and the attributes that belong to it, for whichever
    /// store copies it next. Called from <c>Gte.Read</c> as a screen coordinate
    /// leaves the GTE, and from a load of a word this map already knows.</summary>
    public static void Publish(uint value, float z, float fx, float fy, bool clipped)
    {
        TracePublished++;
        Publish(value, z, fx, fy, clipped, 0f, 0f, 0f, false, true, 0f, 0f, 0f, float.NaN, 0u, false);
    }

    public static void Publish(uint value, float z, float fx, float fy, bool clipped, float vx, float vy, float vz, bool hasView,
                               float ph, float pox, float poy, float fogRaw = float.NaN, float cue = float.NaN)
    {
        TracePublished++;
        _nextCue = cue;
        Publish(value, z, fx, fy, clipped, vx, vy, vz, hasView, true, ph, pox, poy, fogRaw, 0u, false);
    }

    /// <summary>Per-pixel lighting: a colour leaving the GTE after NCDS/NCDT, with
    /// what it was lit from.</summary>
    public static void PublishLit(uint value, float d0, float d1, float d2, uint rgbc, float ir0, int gen)
    {
        TracePublished++;
        _nextLit = true; _nD0 = d0; _nD1 = d1; _nD2 = d2; _nRgbc = rgbc; _nIr0 = ir0; _nGen = gen;
        Publish(value, 0f, 0f, 0f, false, 0f, 0f, 0f, false, true, 0f, 0f, 0f, float.NaN, 0u, false);
    }

    // The extra fields of the next Publish, so the private overload keeps its shape.
    static float _nextCue = float.NaN;
    static bool _nextLit;
    static float _nD0, _nD1, _nD2, _nIr0;
    static uint _nRgbc;
    static int _nGen;
    static byte _nTag;

    /// <summary>The port's source id for what the GTE is projecting now (0 = nothing in
    /// particular), recorded with each vertex and read back by the GPU through
    /// <see cref="Attr.Tag"/>: KF3 tags creature models so their translucent skins are
    /// not taken for water.</summary>
    public static byte CurrentTag;
    public const byte TagCreature = 1;

    /// <summary>Smooth fog: a colour leaving the GTE after the depth cue, with the
    /// colour it was before, for the packet's colour word.</summary>
    public static void PublishColor(uint value, uint preFog)
    {
        TracePublished++;
        Publish(value, 0f, 0f, 0f, false, 0f, 0f, 0f, false, true, 0f, 0f, 0f, float.NaN, preFog, true);
    }

    static void Publish(uint value, float z, float fx, float fy, bool clipped, float vx, float vy, float vz, bool hasView, bool root,
                        float ph, float pox, float poy, float fogRaw, uint preFog, bool hasPreFog)
    {
        _pending[_pendingHead] = new Pending
        {
            Value = value, Tick = _tick, Z = z, Fx = fx, Fy = fy, Clipped = clipped,
            Vx = vx, Vy = vy, Vz = vz, HasView = hasView, Ph = ph, Pox = pox, Poy = poy,
            FogRaw = fogRaw, PreFog = preFog, HasPreFog = hasPreFog,
            Cue = _nextCue, HasLit = _nextLit, D0 = _nD0, D1 = _nD1, D2 = _nD2, Rgbc = _nRgbc, LitIr0 = _nIr0, LitGen = _nGen,
            Matched = false, Root = root, Live = true, Tag = root ? CurrentTag : _nTag,
        };
        _nTag = 0;
        _nextCue = float.NaN; _nextLit = false;
        _pendingHead = (_pendingHead + 1) % PendingCount;
        _newestPendingTick = _tick;
        _pendingValueBits |= ValueBit(value);
    }

    /// <summary>A guest word store. If it is carrying a value someone published, the
    /// destination inherits the attributes; if it is not, whatever the destination
    /// used to hold is no longer true and is dropped.</summary>
    public static void NoteWrite(uint phys, uint value)
    {
        _tick++;

        if (_tick - _newestPendingTick > PendingMaxAge || (_pendingValueBits & ValueBit(value)) == 0)
        {
            if (_tick - _newestPendingTick > PendingMaxAge) _pendingValueBits = 0;
            int stale = Index(phys);
            if (Marked(stale)) Unmark(stale);
            return;
        }

        TraceScans++;
        int found = -1;
        for (int k = 1; k <= PendingCount; k++)
        {
            int i = (_pendingHead - k + PendingCount) % PendingCount;
            ref var p = ref _pending[i];
            if (!p.Live || p.Value != value) continue;
            if (_tick - p.Tick > PendingMaxAge) continue;
            // Newest wins, but an entry nothing has taken yet wins over one that has
            // already been copied somewhere -- three coordinates stored in a row bind
            // in order even when they clamped onto the same pixel.
            if (found < 0) found = i;
            if (!p.Matched) { found = i; break; }
        }

        int idx = Index(phys);

        if (found < 0)
        {
            if (Marked(idx)) Unmark(idx);
            return;
        }

        ref var src = ref _pending[found];
        src.Matched = true;
        _map![idx] = new Entry
        {
            Value = value, Seq = _tick, Z = src.Z, Fx = src.Fx, Fy = src.Fy, Clipped = src.Clipped,
            Vx = src.Vx, Vy = src.Vy, Vz = src.Vz, HasView = src.HasView, Ph = src.Ph, Pox = src.Pox, Poy = src.Poy,
            FogRaw = src.FogRaw, PreFog = src.PreFog, HasPreFog = src.HasPreFog,
            Cue = src.Cue, HasLit = src.HasLit, D0 = src.D0, D1 = src.D1, D2 = src.D2, Rgbc = src.Rgbc, LitIr0 = src.LitIr0, LitGen = src.LitGen, Tag = src.Tag,
        };
        Mark(idx);
        TraceBound++;
        if (src.Root) Roots++; else Propagated++;
    }

    /// <summary>A guest word load. A word this map knows is offered to the next store,
    /// which is how a coordinate follows a <c>lw</c>/<c>sw</c> from the game's vertex
    /// scratch array into the primitive packet.</summary>
    public static void NoteRead(uint phys, uint value)
    {
        int idx = Index(phys);
        if (!Marked(idx)) return;

        ref var e = ref _map![idx];
        if (e.Value != value || _tick - e.Seq > EntryMaxAge) return;

        TraceRepublished++;
        _nextCue = e.Cue;
        _nextLit = e.HasLit; _nD0 = e.D0; _nD1 = e.D1; _nD2 = e.D2; _nRgbc = e.Rgbc; _nIr0 = e.LitIr0; _nGen = e.LitGen; _nTag = e.Tag;
        Publish(value, e.Z, e.Fx, e.Fy, e.Clipped, e.Vx, e.Vy, e.Vz, e.HasView, false, e.Ph, e.Pox, e.Poy, e.FogRaw, e.PreFog, e.HasPreFog);
    }

    /// <summary>0052. <see cref="TryGet"/> without the hit and miss counters, for the
    /// port's cull to ask about a word it has not stored anywhere yet.</summary>
    public static bool Peek(uint phys, uint word, out Attr a)
    {
        a = default;
        if (!Active || phys == 0) return false;
        int idx = Index(phys);
        if (!Marked(idx)) return false;
        ref var e = ref _map![idx];
        if (e.Value != word || _tick - e.Seq > EntryMaxAge) return false;
        a.Z = e.Z; a.Fx = e.Fx; a.Fy = e.Fy; a.Clipped = e.Clipped; a.Vx = e.Vx; a.Vy = e.Vy; a.Vz = e.Vz; a.HasView = e.HasView; a.Ph = e.Ph; a.Pox = e.Pox; a.Poy = e.Poy; a.FogRaw = e.FogRaw; a.PreFog = e.PreFog; a.HasPreFog = e.HasPreFog; a.Cue = e.Cue; a.Tag = e.Tag;
        return true;
    }

    /// <summary>Smooth fog: the colour before the depth cue for the colour word at
    /// <paramref name="phys"/>, matched on its 24 colour bits only -- the first
    /// colour word is also the command word, whose top byte the game sets again
    /// after storing the colour.</summary>
    public static bool PeekColor(uint phys, uint word, out uint preFog)
    {
        preFog = 0;
        if (!Active || phys == 0) return false;
        int idx = Index(phys);
        if (!Marked(idx)) return false;
        ref var e = ref _map![idx];
        if (!e.HasPreFog || ((e.Value ^ word) & 0xFFFFFFu) != 0 || _tick - e.Seq > EntryMaxAge) return false;
        preFog = e.PreFog;
        return true;
    }

    /// <summary>Per-pixel lighting: what lit the colour word at <paramref name="phys"/>,
    /// matched on its 24 colour bits (see <see cref="PeekColor"/>).</summary>
    public static bool PeekLit(uint phys, uint word, out Attr a)
    {
        a = default;
        if (!Active || phys == 0) return false;
        int idx = Index(phys);
        if (!Marked(idx)) return false;
        ref var e = ref _map![idx];
        if (!e.HasLit || ((e.Value ^ word) & 0xFFFFFFu) != 0 || _tick - e.Seq > EntryMaxAge) return false;
        a.HasLit = true; a.D0 = e.D0; a.D1 = e.D1; a.D2 = e.D2; a.Rgbc = e.Rgbc; a.LitIr0 = e.LitIr0; a.LitGen = e.LitGen;
        return true;
    }

    /// <summary>The attributes of the vertex whose coordinate the GPU just read out of
    /// <paramref name="phys"/>. <paramref name="word"/> is the word it read, and it
    /// has to match the one the attributes were recorded for — that is what makes a
    /// stale or overwritten entry a miss instead of a lie.</summary>
    public static bool TryGet(uint phys, uint word, out Attr a)
    {
        a = default;
        if (!Active || phys == 0) { Misses++; return false; }

        int idx = Index(phys);
        if (!Marked(idx)) { Misses++; return false; }

        ref var e = ref _map![idx];
        if (e.Value != word || _tick - e.Seq > EntryMaxAge) { Misses++; return false; }

        a.Z = e.Z; a.Fx = e.Fx; a.Fy = e.Fy; a.Clipped = e.Clipped; a.Vx = e.Vx; a.Vy = e.Vy; a.Vz = e.Vz; a.HasView = e.HasView; a.Ph = e.Ph; a.Pox = e.Pox; a.Poy = e.Poy; a.FogRaw = e.FogRaw; a.PreFog = e.PreFog; a.HasPreFog = e.HasPreFog; a.Cue = e.Cue; a.Tag = e.Tag;
        a.Age = _tick - e.Seq;
        Hits++;
        return true;
    }
}
