using RecompOne.Runtime.Events;
using RecompOne.Runtime.Hle;

namespace KfIII;

/// <summary>
/// Moves the HUD out to the edges of a widescreen picture. The world widens, but the
/// HUD is drawn in the game's 320-wide screen space and would otherwise sit inset,
/// hugging the 4:3 box it was made for. KFIII_HUD_ANCHOR=0 leaves it where the game
/// puts it.
///
/// <para>The HUD's primitives, from a GPU capture in the first area (KFIII_GPU_CAPTURE):</para>
/// <list type="bullet">
/// <item>the HP/MP panel: textured sprites (GP0 0x64-0x67) in palettes 7D45 / 7E05,
/// within x 0..110, y 0..75; moved left by the margin;</item>
/// <item>the compass: textured, gouraud, semi-transparent polygons in the compass
/// palettes (one per face, VRAM column 16, rows 480-487), within x 255..320,
/// y 0..65; moved right.</item>
/// </list>
/// <para>Full-screen effects (one primitive covering exactly 320x240: the drowning
/// tint, fades, flashes) are stretched across the margins instead. The ground uses
/// palettes 7900 / 7940 and is not semi-transparent, so it never matches. The event's
/// coordinates are absolute VRAM ones; the boxes are tested relative to the drawing
/// area.</para>
/// </summary>
public static class Hud
{
    static readonly bool _enabled = !DevSwitch.Is("KFIII_HUD_ANCHOR", "0");

    public static void Install()
    {
        if (_enabled) Event.AddListener<RenderPrimEvent>(OnPrim);
    }

    static bool PanelClut(int clut) => clut is 0x7D45 or 0x7E05;

    /// One palette per compass face, all in VRAM column 16, rows 480-487 (7801-79C1;
    /// facing east draws the "E" in 78C1).
    static bool CompassClut(int clut) => (clut & 0x3F) == 1 && (clut >> 6) is >= 480 and <= 487;

    static void OnPrim(RenderPrimEvent e)
    {
        // The compass is a GTE-projected model drawn over the world: it is kept out of
        // the depth test and out of what the ambient occlusion pass reads, or the pass
        // would shade it.
        if (e.Textured && e.SemiTransparent && e.Gouraud && CompassClut(e.Clut)) e.NoDepth = true;
        if (Display.WideAspect <= 0f) return;
        int margin = Display.WideMargin(320);
        if (margin <= 0) return;

        int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
        for (int i = 0; i < e.Count; i++)
        {
            int x = e.X[i] - e.DrawLeft, y = e.Y[i] - e.DrawTop;
            if (x < minX) minX = x; if (x > maxX) maxX = x;
            if (y < minY) minY = y; if (y > maxY) maxY = y;
        }

        // Whole-screen effects (the drowning tint, the death fade, damage flashes) are
        // one primitive covering exactly the game's 320x240 screen; the drowning tint
        // is a semi-transparent POLY_FT4 0,0-320,240. Stretched across the margins, or
        // the sides of a wide picture would stay untinted.
        if (minX == 0 && maxX is 319 or 320 && minY == 0 && maxY is 239 or 240)
        {
            for (int i = 0; i < e.Count; i++)
                e.X[i] += e.X[i] - e.DrawLeft == minX ? -margin : margin;
            return;
        }

        if (!e.Textured) return;

        int shift;
        if (e.Count == 2 && PanelClut(e.Clut) && minX >= 0 && maxX <= 110 && minY >= 0 && maxY <= 75)
            shift = -margin;
        else if (e.Count >= 3 && e.SemiTransparent && e.Gouraud && CompassClut(e.Clut)
                 && minX >= 255 && maxX <= 320 && minY >= 0 && maxY <= 65)
            shift = margin;
        else return;

        for (int i = 0; i < e.Count; i++) e.X[i] += shift;
    }
}
