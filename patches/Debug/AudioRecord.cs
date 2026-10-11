using RecompOne.Runtime;
using RecompOne.Runtime.Memory;

namespace KfIII;

/// <summary>
/// Test tool: records the final SPU mix to a WAV file (44.1 kHz, 16-bit stereo),
/// from start until exit, so a sound bug (a pop, a cut) can be found in the samples.
/// KFIII_WAV names the file, for example tests\logs\door.wav.
///
/// Its Install also installs <see cref="AreaTrace"/>, and <see cref="Snapshot"/>
/// passes the world tick on to it, which is where Program.cs and Automap call in.
/// </summary>
public static class AudioRecord
{
    const int BytesPerFrame = 4;   // two 16-bit channels
    const int WavHeaderSize = 44;

    static FileStream? _f;
    static long _frames;

    public static void Install()
    {
        AreaTrace.Install();

        var path = DevSwitch.Text("KFIII_WAV");
        if (path == null) return;
        _f = File.Create(path);
        _f.Write(new byte[WavHeaderSize]);
        Spu.Mixed += (dry, _, frames) =>
        {
            var buf = new byte[frames * BytesPerFrame];
            Buffer.BlockCopy(dry, 0, buf, 0, frames * BytesPerFrame);
            lock (_f) { _f.Write(buf); _frames += frames; Header(); }
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { lock (_f) { Header(); _f.Flush(); } };
    }

    /// <summary>Once per world tick, from Automap's kf_WorldUpdate hook: see <see cref="AreaTrace.Snapshot"/>.</summary>
    public static void Snapshot(IMemory m) => AreaTrace.Snapshot(m);

    /// <summary>Rewrites the RIFF header for the samples so far, so the file is valid even if the process is killed.</summary>
    static void Header()
    {
        long pos = _f!.Position;
        int data = (int)(_frames * BytesPerFrame);
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + data); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(44100); w.Write(44100 * 4); w.Write((short)4); w.Write((short)16);
        w.Write("data"u8); w.Write(data);
        _f.Position = 0; _f.Write(ms.ToArray()); _f.Position = pos;
    }
}
