using RecompOne.Runtime.Hle;

namespace KfIII;

/// <summary>
/// Diagnostic: the GP0 commands of one or more frames, written to a text file, for
/// finding what the GPU was (or was not) asked to draw.
///
/// KFIII_GPU_CAPTURE=N starts recording at the first present N seconds after start
/// and writes gpu_capture.txt in the working directory; KFIII_GPU_CAPTURE_FRAMES=K
/// records K frames instead of one, each ended by a "----" line.
///
/// One line per polygon: command, vertex count, flags (g gouraud, t textured,
/// s semi-transparent), then x,y and colour per vertex as the packet gave them
/// (relative to the drawing offset, which is logged as it changes, as is the
/// drawing area), and the guest address the packet came from. Rectangles, fills,
/// VRAM copies and uploads get a line each, and the runtime's triangle probe
/// (Gpu.TriProbe) adds a "tri" line per triangle with the coordinates it drew at.
/// </summary>
public sealed class GpuCapture : IGpuTrace
{
    readonly List<uint> _cmd = [];
    readonly List<string> _out = [];
    readonly string _path;
    bool _armed, _recording;
    int _frames = DevSwitch.Int("KFIII_GPU_CAPTURE_FRAMES", 1);
    int _polys;
    /// Guest address of the current command's first word.
    uint _src0;

    GpuCapture(string path) { _path = path; }

    public static void Install()
    {
        if (DevSwitch.Number("KFIII_GPU_CAPTURE") is null) return;
        var cap = new GpuCapture(Path.Combine(Environment.CurrentDirectory, "gpu_capture.txt"));
        GpuTrace.Sink = cap;
        RecompOne.Runtime.Gpu.TriProbe = (ax, ay, aw, bx, by, bw, cx, cy, cw, tex) =>
        {
            if (cap._recording)
                cap._out.Add(FormattableString.Invariant($"tri {ax:F3},{ay:F3},{aw:F0} {bx:F3},{by:F3},{bw:F0} {cx:F3},{cy:F3},{cw:F0}"));
        };
        DevSwitch.At("KFIII_GPU_CAPTURE", _ => cap._armed = true);
    }

    public void Word(uint word, uint src) { if (_recording) { if (_cmd.Count == 0) _src0 = src; _cmd.Add(word); } }

    public void Gp1(uint word) { }

    public void Executed()
    {
        if (!_recording || _cmd.Count == 0) { _cmd.Clear(); return; }
        uint w0 = _cmd[0];
        uint op = w0 >> 24;
        if (op is >= 0x20 and <= 0x3F) Polygon(op);
        else if (op is 0xE3 or 0xE4) _out.Add($"area{(op == 0xE3 ? "TL" : "BR")} {w0 & 0x3FF},{(w0 >> 10) & 0x1FF}");
        else if (op == 0xE5) _out.Add($"offset {Sx(w0 & 0x7FF)},{Sx((w0 >> 11) & 0x7FF)}");
        else if (op == 0x02 && _cmd.Count >= 3)
            _out.Add($"fill {_cmd[1] & 0xFFFF},{_cmd[1] >> 16} {_cmd[2] & 0xFFFF}x{_cmd[2] >> 16} color {w0 & 0xFFFFFF:X6}");
        else if (op is >= 0x80 and <= 0x9F && _cmd.Count >= 4)
            _out.Add($"copy {_cmd[1] & 0xFFFF},{_cmd[1] >> 16} -> {_cmd[2] & 0xFFFF},{_cmd[2] >> 16} {_cmd[3] & 0xFFFF}x{_cmd[3] >> 16}");
        else if (op is >= 0xA0 and <= 0xBF && _cmd.Count >= 3)
            _out.Add($"upload {_cmd[1] & 0xFFFF},{_cmd[1] >> 16} {_cmd[2] & 0xFFFF}x{_cmd[2] >> 16}");
        else if (op is >= 0x60 and <= 0x7F && _cmd.Count >= 2)
        {
            // Rectangles: xy, then uv and clut if textured, then w,h if variable-sized.
            bool tex = (op & 4) != 0;
            int size = (int)((op >> 3) & 3);
            uint xy = _cmd[1];
            string wh = size == 0 && _cmd.Count > (tex ? 3 : 2) ? $"{_cmd[tex ? 3 : 2] & 0xFFFF}x{_cmd[tex ? 3 : 2] >> 16}"
                      : size == 1 ? "1x1" : size == 2 ? "8x8" : "16x16";
            string uv = tex && _cmd.Count > 2 ? $" uv {_cmd[2] & 0xFF},{(_cmd[2] >> 8) & 0xFF} clut {_cmd[2] >> 16:X4}" : "";
            _out.Add($"sprite {op:X2} {Sx(xy & 0x7FF)},{Sx((xy >> 16) & 0x7FF)} {wh} color {w0 & 0xFFFFFF:X6}{uv}");
            _polys++;
        }
        else if (op is >= 0x40 and <= 0x7F) _out.Add($"prim {op:X2} words {_cmd.Count}");
        else if (op == 0xE1) _out.Add($"texpage {w0 & 0xFFFFFF:X6}");
        _cmd.Clear();
    }

    /// <summary>An 11-bit signed GPU coordinate.</summary>
    static int Sx(uint v) => ((int)(v << 21)) >> 21;

    void Polygon(uint op)
    {
        bool g = (op & 0x10) != 0, quad = (op & 0x08) != 0, tex = (op & 0x04) != 0, semi = (op & 0x02) != 0;
        int n = quad ? 4 : 3, i = 0;
        var sb = new System.Text.StringBuilder();
        sb.Append($"poly {op:X2} {n} {(g ? "g" : "")}{(tex ? "t" : "")}{(semi ? "s" : "")}");
        uint color = _cmd[i++] & 0xFFFFFF;
        uint clut = 0, tpage = 0;
        var uvs = new System.Text.StringBuilder();
        for (int v = 0; v < n; v++)
        {
            if (v > 0 && g) color = i < _cmd.Count ? _cmd[i++] & 0xFFFFFF : 0;
            uint xy = i < _cmd.Count ? _cmd[i++] : 0;
            if (tex)
            {
                uint uv = i < _cmd.Count ? _cmd[i++] : 0;
                if (v == 0) clut = uv >> 16;
                if (v == 1) tpage = uv >> 16;
                uvs.Append($" {uv & 0xFF},{(uv >> 8) & 0xFF}");
            }
            sb.Append($" {Sx(xy & 0x7FF)},{Sx((xy >> 16) & 0x7FF)}:{color:X6}");
        }
        if (tex) sb.Append($" | tpage {tpage:X4} clut {clut:X4} uv{uvs}");
        sb.Append($" @{_src0:X8}");
        _out.Add(sb.ToString());
        _polys++;
    }

    public void Flush(FlushReason why, int verts) { }
    public void Flushed() { }
    public void Vertices(int asked, int hits) { }

    public void Work(GpuWork what, long start, long end, uint query)
    {
        if (what != GpuWork.Present) return;
        // The game draws at 15 Hz and presents every vblank, so a frame ends at a
        // present that follows some polygons.
        if (_recording && _polys > 0 && --_frames > 0)
        {
            _out.Add($"---- present (frame end) at {Environment.TickCount64}");
            _polys = 0;
        }
        else if (_recording && _polys > 0)
        {
            _recording = false;
            File.WriteAllLines(_path, _out);
            Console.WriteLine($"[KFIII] gpu capture: {_out.Count} lines -> {_path}");
            GpuTrace.Sink = null;
            RecompOne.Runtime.Gpu.TriProbe = null;
        }
        else if (_armed && !_recording)
        {
            _armed = false;
            _recording = true;
        }
    }
}
