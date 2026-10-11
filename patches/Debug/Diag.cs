using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace KfIII;

/// <summary>
/// Archive-load tracing: every archive table kf_OpenArchive builds and every entry
/// kf_LoadArchiveEntry reads, to the console and to diag.log, when KFIII_DIAG_ARCHIVES=1.
/// Archives are the game's data files (FDAT.T and others), each a table of start
/// sectors (<see cref="GameAddr.ArchiveDescs"/>).
/// </summary>
public static class Diag
{
    const uint Descs = GameAddr.ArchiveDescs;
    const int DescStride = GameAddr.ArchiveDescStride;
    static readonly bool _on = DevSwitch.Is("KFIII_DIAG_ARCHIVES");

    /// <summary>
    /// Appended straight to diag.log in the working directory, so a crash right after
    /// cannot lose it in a console buffer.
    /// </summary>
    static void Write(string line)
    {
        Console.WriteLine(line);
        // Best effort: a second instance holding the file must not end the game.
        try { File.AppendAllText("diag.log", line + Environment.NewLine); }
        catch (IOException) { }
    }

    /// <summary>Post-hook on kf_OpenArchive(archive, ...): the descriptors, with the first entries of each table.</summary>
    public static void ArchiveOpened(CpuContext c, IMemory m)
    {
        if (!_on) return;
        for (uint i = 0; i < GameAddr.ArchiveCount; i++)
        {
            uint d = Descs + i * DescStride;
            uint table = m.ReadU32(d);
            if (table == 0) continue;
            Write($"[KFIII-DIAG] archive {i}: table=0x{table:X8} loc={m.ReadU32(d + 4):X8} size={m.ReadU32(d + 8)} " +
                  $"t[0..3]={m.ReadU16(table)},{m.ReadU16(table + 2)},{m.ReadU16(table + 4)},{m.ReadU16(table + 6)}");
        }
    }

    /// <summary>Pre-hook on kf_LoadArchiveEntry(archive, entry, buf): the entry's sectors.</summary>
    public static void EntryLoad(CpuContext c, IMemory m)
    {
        if (!_on) return;
        uint arch = c.A0 & 0xFFFF, entry = c.A1 & 0xFFFF;
        uint table = m.ReadU32(Descs + arch * DescStride);
        uint start = m.ReadU16(table + entry * 2), next = m.ReadU16(table + entry * 2 + 2);
        Write($"[KFIII-DIAG] load archive={arch} entry={entry} buf=0x{c.A2:X8} table=0x{table:X8} start={start} next={next}");
    }
}

/// <summary>
/// Pre-hooks on libgpu's VRAM transfers: LoadImage, StoreImage and ClearImage
/// (a0 = RECT*) and MoveImage (a0 = RECT*, a1 = x, a2 = y).
///
/// MoveImage also feeds <see cref="Water.Moved"/>, which follows the river's
/// scrolling texture, so that hook is not only diagnostic. The logging traces the
/// transfers that touch the sky palettes (VRAM rows 488-500): KFIII_DIAG_VRAM=1
/// logs those, KFIII_DIAG_VRAM=all logs every transfer.
/// </summary>
public static class VramDiag
{
    const int SkyRowFirst = 488, SkyRowLast = 500;

    static readonly bool _all = DevSwitch.Is("KFIII_DIAG_VRAM", "all");
    static readonly bool _on = _all || DevSwitch.Is("KFIII_DIAG_VRAM");
    static int _n;

    static (int X, int Y, int W, int H) Rect(IMemory m, uint p) =>
        ((short)m.ReadU16(p), (short)m.ReadU16(p + 2), (short)m.ReadU16(p + 4), (short)m.ReadU16(p + 6));

    static bool TouchesSky(int y, int h) => y <= SkyRowLast && y + h >= SkyRowFirst;

    static void Log(string what, IMemory m, uint rect, string extra, uint ra)
    {
        if (!_on) return;
        var r = Rect(m, rect);
        if (!TouchesSky(r.Y, r.H) && !_all) return;
        if (_n++ > (_all ? 4000 : 400)) return;
        Console.WriteLine($"[KFIII-VRAM] {Environment.TickCount64 % 100000} {what} {r.X},{r.Y} {r.W}x{r.H}{extra} ra=0x{ra:X8}");
    }

    /// <summary>Pre-hook on LoadImage: also logs the first four pixels of an upload to the sky rows.</summary>
    public static void LoadImage(CpuContext c, IMemory m)
    {
        string first = "";
        if (_on)
        {
            var r = Rect(m, c.A0);
            if (TouchesSky(r.Y, r.H))
                first = $" data[0..3]={m.ReadU16(c.A1):X4} {m.ReadU16(c.A1 + 2):X4} {m.ReadU16(c.A1 + 4):X4} {m.ReadU16(c.A1 + 6):X4}";
        }
        Log("LoadImage", m, c.A0, first, c.RA);
    }

    /// <summary>Pre-hook on StoreImage.</summary>
    public static void StoreImage(CpuContext c, IMemory m) => Log("StoreImage", m, c.A0, "", c.RA);

    /// <summary>Pre-hook on ClearImage.</summary>
    public static void ClearImage(CpuContext c, IMemory m) => Log("ClearImage", m, c.A0, "", c.RA);

    /// <summary>Pre-hook on MoveImage: tells Water about the copy, then logs it if it touches the sky rows at either end.</summary>
    public static void MoveImage(CpuContext c, IMemory m)
    {
        var r = Rect(m, c.A0);
        Water.Moved(r.X, r.Y, r.W, r.H, (short)c.A1, (short)c.A2);
        if (!_on) return;
        int dy = (short)c.A2;
        if (!(TouchesSky(r.Y, r.H) || TouchesSky(dy, r.H)) || _n++ > 400) return;
        Console.WriteLine($"[KFIII-VRAM] MoveImage {r.X},{r.Y} {r.W}x{r.H} -> {(short)c.A1},{dy} ra=0x{c.RA:X8}");
    }
}

/// <summary>
/// KFIII_MODEL_CALLERS=1: each distinct caller (return address) of kf_DrawModel,
/// once, with the time. Shows which code draws models where (it found the menu's
/// item preview).
/// </summary>
public static class ModelCallers
{
    static readonly bool _on = DevSwitch.Is("KFIII_MODEL_CALLERS");
    static readonly HashSet<uint> _seen = [];
    static readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>Pre-hook on kf_DrawModel.</summary>
    public static void Before(CpuContext c, IMemory m)
    {
        if (!_on) return;
        lock (_seen) if (_seen.Add(c.RA)) Console.WriteLine($"[models] {_clock.Elapsed.TotalSeconds:F1}s caller 0x{c.RA:X8} a1 {c.A1:X} a2 {c.A2:X8}");
    }
}

/// <summary>
/// KFIII_ITEMVIEW_PROBE=1: the menu's item preview state twice a second. Its gp
/// fields are +0x1C mode, +0x24 shown, +0x28 / +0x2C / +0x30 the model load's state
/// and +0x168 the model buffer; "en" is <see cref="GameAddr.ItemPreviewEnabled"/>.
/// </summary>
public static class ItemViewProbe
{
    static readonly bool _on = DevSwitch.Is("KFIII_ITEMVIEW_PROBE");
    static long _last;

    /// <summary>Pre-hook on kf_ItemPreview.</summary>
    public static void Before(CpuContext c, IMemory m)
    {
        if (!_on) return;
        long now = Environment.TickCount64;
        if (now - _last < 500) return;
        _last = now;
        uint gp = c.GP;
        Console.WriteLine($"[itemview] a0 {c.A0 & 0xFF:X2} en {m.ReadU8(GameAddr.ItemPreviewEnabled)} gp {gp:X8} +1c {m.ReadU32(gp + 0x1C):X} +24 {m.ReadU32(gp + 0x24):X} +28 {m.ReadU32(gp + 0x28):X} +2c {m.ReadU32(gp + 0x2C):X} +30 {m.ReadU32(gp + 0x30):X} +168 {m.ReadU32(gp + 0x168):X8}");
    }
}

/// <summary>
/// KFIII_WATER_PROBE=1: which draw routine writes the water polygons (those with
/// the water's palette, WaterClut) into the primitive pool, and with what
/// arguments, once a second. Each hook pair compares the pools' write pointers
/// before and after the routine and scans the packets in between. The hooks are
/// not bound by default: to use it, add Before / After* pairs on kf_DrawGround,
/// kf_DrawGroundNear, kf_DrawModelPolys, kf_DrawModelPolys2 and kf_DrawTile to
/// scripts/name_sdk.py's HOOKS.
/// </summary>
public static class WaterProbe
{
    const uint WaterClut = 0x79CC;
    /// Camera lines printed from the first kf_DrawGround calls.
    const int CameraLines = 12;

    static readonly bool _on = DevSwitch.Is("KFIII_WATER_PROBE");
    [ThreadStatic] static Stack<(uint c0, uint c1, uint a0, uint a1, uint a2, uint a3)>? _stack;
    static readonly Dictionary<string, int> _counts = [];
    static long _cameraAt; static int _cameraN;
    static long _last;

    /// <summary>Pre-hook: the pools' write pointers and the arguments.</summary>
    public static void Before(CpuContext c, IMemory m)
    {
        if (!_on) return;
        (_stack ??= new()).Push((m.ReadU32(GameAddr.PrimPool0 + 8), m.ReadU32(GameAddr.PrimPool1 + 8), c.A0, c.A1, c.A2, c.A3));
    }

    static void After(string name, CpuContext c, IMemory m)
    {
        if (!_on || _stack is not { Count: > 0 }) return;
        var (c0, c1, a0, a1, a2, a3) = _stack.Pop();
        uint n0 = m.ReadU32(GameAddr.PrimPool0 + 8), n1 = m.ReadU32(GameAddr.PrimPool1 + 8);
        uint from = n0 != c0 ? c0 : c1, to = n0 != c0 ? n0 : n1;
        if (name == "ground" && _cameraN < CameraLines && Environment.TickCount64 - _cameraAt > 3000)
        {
            var r = RecompOne.Runtime.Gte.Rotation; var t = RecompOne.Runtime.Gte.Translation;
            Console.WriteLine($"[water] cam a0 {a0:X} R {r[0]} {r[1]} {r[2]} / {r[3]} {r[4]} {r[5]} / {r[6]} {r[7]} {r[8]} TR {t[0]} {t[1]} {t[2]} player {(int)m.ReadU32(GameAddr.PlayerPos)} {(int)m.ReadU32(GameAddr.PlayerPos + 4)} {(int)m.ReadU32(GameAddr.PlayerPos + 8)} ang {m.ReadU16(GameAddr.ViewAngles):X} {m.ReadU16(GameAddr.ViewAngles + 2):X} {m.ReadU16(GameAddr.ViewAngles + 4):X}");
            if (++_cameraN == CameraLines) _cameraAt = Environment.TickCount64;
        }
        // Walk the packets: a tag word (length in the top byte), then the command
        // word whose top byte is the code. Textured polygons are 0x24..0x3F with
        // bit 2 set; the first uv word carries the palette in its top half.
        int water = 0;
        var sb = new System.Text.StringBuilder();
        for (uint p = from; p < to && p - from < 0x40000;)
        {
            uint tag = m.ReadU32(p);
            int len = (int)(tag >> 24);
            if (len == 0) { p += 4; continue; }
            byte code = m.ReadU8(p + 7);
            bool gouraud = (code & 0x10) != 0;
            uint uvClut = m.ReadU32(p + 12u);
            if ((code & 0xE4) == 0x24 && (uvClut >> 16) == WaterClut)
            {
                water++;
                if (sb.Length < 600)
                {
                    int vstride = gouraud ? 12 : 8;
                    sb.Append($" [{code:X2}");
                    int nv = (code & 8) != 0 ? 4 : 3;
                    for (int i = 0; i < nv; i++)
                    {
                        uint uv = m.ReadU32(p + 12u + (uint)(i * vstride));
                        sb.Append($" {uv & 0xFF},{(uv >> 8) & 0xFF}");
                    }
                    sb.Append(']');
                }
            }
            p += (uint)(len + 1) * 4;
        }
        if (water == 0) return;
        string key = $"{name} ra {c.RA:X8}";
        lock (_counts)
        {
            _counts[key] = _counts.GetValueOrDefault(key) + water;
            long now = Environment.TickCount64;
            if (now - _last < 1000) return;
            _last = now;
            foreach (var kv in _counts) Console.WriteLine($"[water] {kv.Key}: {kv.Value} water prims");
            Console.WriteLine($"[water] last {name} a0 {a0:X} a1 {a1:X} a2 {a2:X} a3 {a3:X}:{sb}");
            _counts.Clear();
        }
    }

    /// <summary>Post-hook on kf_DrawGround.</summary>
    public static void AfterGround(CpuContext c, IMemory m) => After("ground", c, m);
    /// <summary>Post-hook on kf_DrawGroundNear.</summary>
    public static void AfterGroundNear(CpuContext c, IMemory m) => After("groundNear", c, m);
    /// <summary>Post-hook on kf_DrawModelPolys.</summary>
    public static void AfterModel(CpuContext c, IMemory m) => After("model", c, m);
    /// <summary>Post-hook on kf_DrawModelPolys2.</summary>
    public static void AfterModel2(CpuContext c, IMemory m) => After("model2", c, m);
    /// <summary>Post-hook on kf_DrawTile.</summary>
    public static void AfterTile(CpuContext c, IMemory m) => After("tile", c, m);
}

/// <summary>
/// KFIII_LIGHT_PROBE=1: the GTE's light setup and the runtime's per-pixel lighting
/// counters, once a second, from inside a model draw.
/// </summary>
public static class LightProbe
{
    static readonly bool _on = DevSwitch.Is("KFIII_LIGHT_PROBE");
    static long _last;

    /// <summary>Pre-hook on kf_DrawModelPolys.</summary>
    public static void Before(CpuContext c, IMemory m)
    {
        if (!_on) return;
        long now = Environment.TickCount64;
        if (now - _last < 1000) return;
        _last = now;
        var l = RecompOne.Runtime.Gte.LightMatrix; var lc = RecompOne.Runtime.Gte.LightColorMatrix; var bk = RecompOne.Runtime.Gte.BackColor;
        var f = RecompOne.Runtime.Gte.FogRegs;
        Console.WriteLine($"[light] LLM {string.Join(' ', l.ToArray())} | LCM {string.Join(' ', lc.ToArray())} | BK {bk[0]} {bk[1]} {bk[2]} | FC {f.R} {f.G} {f.B} DQA {f.Dqa} DQB {f.Dqb} | ncds {RecompOne.Runtime.Gte.NormalOps} lit ok {RecompOne.Runtime.Gte.LitRecorded} rejected {RecompOne.Runtime.Gte.LitRejected} published {RecompOne.Runtime.Gte.LitPublished} | polys ground {RecompOne.Runtime.GteLightMap.LitGround} model {RecompOne.Runtime.GteLightMap.LitModel} facecue {RecompOne.Runtime.GteLightMap.LitFaceCue} missed {RecompOne.Runtime.GteLightMap.LitMissed} | gens new {RecompOne.Runtime.GteLightMap.GenNew} of {RecompOne.Runtime.GteLightMap.GenCalls}");
    }
}
