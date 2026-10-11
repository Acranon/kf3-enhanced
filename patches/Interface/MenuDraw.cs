using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using Game = Recompiled.KingsFieldIII_game;

namespace KfIII;

/// <summary>
/// Labels, numbers and windows drawn with the game's own menu routines, so an addition
/// to a menu screen looks like the rest of it: kf_MenuText for a label, kf_FormatNumber
/// and kf_MenuNumber for a number, kf_MenuWindow for the frame. Each call builds its
/// text record on the game's stack, as the status screens do, so the caller must have
/// moved SP down to make room first. Draw a window after its text: the ordering table
/// then puts it beneath, as kf_MenuOffenseDefense does.
/// </summary>
static class MenuDraw
{
    /// <summary>Width of one glyph, in game pixels.</summary>
    public const int Advance = 7;
    /// <summary>Places in a number: stats stop at 999 (kf_RecalcStats).</summary>
    public const int Digits = 3;
    /// <summary>kf_MenuWindow tiles its texture and shows a seam when narrower than about this.</summary>
    public const int SeamlessWidth = 118;
    /// <summary>
    /// kf_MenuWindow's corner pieces are 0x42 tall (it sizes its middle as h - 0x42); a
    /// shorter window overlaps them and the overlap shows as a bright band.
    /// </summary>
    public const int MinWindowHeight = 0x44;

    // Glyph tint: kf_MenuText and kf_MenuNumber modulate each glyph by the three words at
    // gp+0x5C/0x60/0x64 (0x80 = as drawn). The game's blue unselected text is these set.
    const uint TintR = GameAddr.MenuTint, TintG = GameAddr.MenuTint + 4, TintB = GameAddr.MenuTint + 8;
    public static readonly (uint R, uint G, uint B) Plain = (0x80, 0x80, 0x80);
    public static readonly (uint R, uint G, uint B) Better = (0x50, 0xC0, 0x50);
    public static readonly (uint R, uint G, uint B) Worse = (0xD0, 0x48, 0x40);

    // The text record, at sp+0x20: s16 x, s16 y, then up to 24 glyphs ending in 0xFF.
    // kf_FormatNumber writes its digits straight into the record's glyphs.
    const uint Record = 0x20;
    const int MaxGlyphs = 24;

    public static void Text(CpuContext c, IMemory m, int x, int y, string s) => Text(c, m, x, y, s, Plain);

    public static void Text(CpuContext c, IMemory m, int x, int y, string s, (uint R, uint G, uint B) tint)
    {
        uint rec = c.SP + Record;
        m.WriteU16(rec, (ushort)x);
        m.WriteU16(rec + 2, (ushort)y);
        int n = 0;
        foreach (char ch in s.ToUpperInvariant())
        {
            // The menu font: A = 0, digits from 0x20, '-' 0x33, space 0x7F.
            byte g = ch switch
            {
                >= 'A' and <= 'Z' => (byte)(ch - 'A'),
                >= '0' and <= '9' => (byte)(0x20 + ch - '0'),
                '-' => 0x33,
                _ => 0x7F,
            };
            m.WriteU8(rec + 4 + (uint)n++, g);
            if (n >= MaxGlyphs) break;
        }
        m.WriteU8(rec + 4 + (uint)n, 0xFF);
        Tinted(c, m, tint, () => { c.A0 = GameAddr.MenuTextFont; c.A1 = rec; Game.kf_MenuText(c, m); });
    }

    /// <summary>
    /// A number right-aligned in <see cref="Digits"/> places at x (capped at 999); a
    /// negative one gets the font's minus sign in front.
    /// </summary>
    public static void Number(CpuContext c, IMemory m, int x, int y, int value, (uint R, uint G, uint B) tint)
    {
        int mag = Math.Min(Math.Abs(value), 999);
        if (value < 0)
        {
            int digits = mag.ToString().Length;
            Text(c, m, x + (Digits - digits - 1) * Advance, y, "-", tint);
        }
        uint rec = c.SP + Record, sp = c.SP;
        m.WriteU16(rec, (ushort)x);
        m.WriteU16(rec + 2, (ushort)y);
        m.WriteU32(c.SP + 0x10, rec + 4);   // kf_FormatNumber's fifth argument: where the digits go
        c.A0 = (uint)mag; c.A1 = Digits; c.A2 = 0; c.A3 = 0;
        Game.kf_FormatNumber(c, m);
        c.SP = sp;
        Tinted(c, m, tint, () => { c.A0 = GameAddr.MenuNumberFont; c.A1 = rec; Game.kf_MenuNumber(c, m); });
    }

    /// <summary>A menu window, at least <see cref="MinWindowHeight"/> tall.</summary>
    public static void Window(CpuContext c, IMemory m, int x, int y, int w, int h)
    {
        uint sp = c.SP;
        h = Math.Max(h, MinWindowHeight);
        c.A0 = (uint)x; c.A1 = (uint)y; c.A2 = (uint)w; c.A3 = (uint)h;
        Game.kf_MenuWindow(c, m);
        c.SP = sp;
    }

    static void Tinted(CpuContext c, IMemory m, (uint R, uint G, uint B) tint, Action draw)
    {
        uint r = m.ReadU32(TintR), g = m.ReadU32(TintG), b = m.ReadU32(TintB), sp = c.SP;
        m.WriteU32(TintR, tint.R); m.WriteU32(TintG, tint.G); m.WriteU32(TintB, tint.B);
        try { draw(); }
        finally
        {
            c.SP = sp;
            m.WriteU32(TintR, r); m.WriteU32(TintG, g); m.WriteU32(TintB, b);
        }
    }
}
