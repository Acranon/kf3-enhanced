using System.Numerics;
using ImGuiNET;
using RecompOne.Runtime.Hle;
using RecompOne.Runtime.Host.Window;

namespace KfIII;

/// <summary>
/// What the ImGui overlays in Interface/ share (the SPECIAL box, stat progress, damage
/// numbers, key hints): the wide-screen margin, the colour format, and the framed box
/// their panels sit in.
/// </summary>
static class ScreenOverlay
{
    /// <summary>Game pixels the wide-screen picture adds on each side (0 at 4:3).</summary>
    public static int WideMargin => Display.WideAspect > 0f ? Math.Max(0, Display.WideMargin(320)) : 0;

    /// <summary>An ImGui colour from 0..255 components and an opacity of 0..1.</summary>
    public static uint Rgba(int r, int g, int b, float a = 1f) =>
        (uint)(Math.Clamp((int)(a * 255), 0, 255) << 24 | b << 16 | g << 8 | r);

    /// <summary>The panels' text colour, and the dimmer one for headings and values at their limit.</summary>
    public static readonly uint TextColour = Rgba(232, 228, 215), DimColour = Rgba(160, 158, 150);

    /// <summary>A panel's background: a dark, slightly see-through fill in a thin light frame.</summary>
    public static void Box(ImDrawListPtr dl, Vector2 a, Vector2 b, float fillAlpha, float sy)
    {
        dl.AddRectFilled(a, b, Rgba(20, 22, 28, fillAlpha), 4f);
        dl.AddRect(a, b, Rgba(170, 170, 160, 0.9f), 4f, ImDrawFlags.None, Math.Max(1f, sy * 0.5f));
    }
}

/// <summary>
/// The output view this frame, and where a point of the game's 320 x 240 picture lands
/// in it. Game x runs from -Margin to 320 + Margin across a wide-screen picture.
/// </summary>
readonly struct GameView
{
    public readonly Vector2 Min, Max;
    public readonly int Margin;
    /// <summary>Screen pixels per game pixel.</summary>
    public readonly float Sx, Sy;

    GameView(Vector2 min, Vector2 max, int margin)
    {
        Min = min;
        Max = max;
        Margin = margin;
        Sx = (max.X - min.X) / (320f + 2 * margin);
        Sy = (max.Y - min.Y) / 240f;
    }

    public static GameView Current => new(OutputView.Min, OutputView.Max, ScreenOverlay.WideMargin);

    public Vector2 At(float gx, float gy) => new(Min.X + (gx + Margin) * Sx, Min.Y + gy * Sy);
}
