using System.Numerics;
using ImGuiNET;
using RecompOne.Runtime.Host.Window;

namespace KfIII;

/// <summary>
/// The SPECIAL box of "Equipment stats": the highlighted piece's hidden effects
/// (GearEffects.Lines), drawn as an overlay hung under GearCompare's stats window with
/// the right edges lined up, so it never covers the stats. Where that would run into
/// the item list (its window starts at game y 147 and reaches x 258), it goes beside the
/// stats window instead, its right edge against the window's left, level with its top.
/// GearCompare calls <see cref="Show"/> on each menu frame it draws; the box stays up
/// while those calls keep coming.
/// </summary>
public static class GearSpecial
{
    static volatile string[] _lines = [];
    static long _at;
    // The stats window, in game pixels (x across the wide picture, -margin .. 320 + margin).
    static float _left, _right, _top, _bottom;
    const float ListTop = 147, ListRight = 258;

    public static void Install() => PanelManager.Register(Panel.Instance);

    /// <summary>From the game thread, each frame the stats panel is drawn.</summary>
    public static void Show(List<string> lines, int left, int right, int top, int bottom)
    {
        _lines = lines.ToArray();
        (_left, _right, _top, _bottom) = (left, right, top, bottom);
        _at = Environment.TickCount64;
    }

    /// <summary>The lines drawn in red: curses and other drawbacks.</summary>
    static bool IsDrawback(string l) =>
        l.StartsWith("Cursed") || l.StartsWith("Can't") || l.StartsWith("Each shot") || l.StartsWith("-1")
        || l.StartsWith("Dims") || l.Contains("double MP") || l.Contains("heavier") || l.Contains("half as fast");

    sealed class Panel : IFloatingPanel
    {
        public static readonly Panel Instance = new();
        public string Name => "kf3gearspecial";
        public bool IsOpen
        {
            get => GearCompare.Enabled && _lines.Length > 0 && Environment.TickCount64 - _at < 250 && !AutomapView.FullOpen;
            set { }
        }

        public void Draw()
        {
            if (!OutputView.Valid) return;
            var lines = _lines;
            var view = GameView.Current;
            float sx = view.Sx, sy = view.Sy;

            var font = ImGui.GetFont();
            float ts = Math.Max(12f, 6.6f * sy), line = ts * 1.08f, pad = 4f * sx;
            const string Head = "SPECIAL";
            float w = font.CalcTextSizeA(ts * 0.9f, float.MaxValue, 0f, Head).X;
            foreach (var l in lines) w = Math.Max(w, font.CalcTextSizeA(ts, float.MaxValue, 0f, l).X);
            float h = 2 * pad + line * 1.05f + line * lines.Length;
            var under = view.At(_right, _bottom + 2);
            bool hitsList = under.Y + h > view.At(0, ListTop).Y && under.X - w - 2 * pad < view.At(ListRight, 0).X;
            var b = hitsList ? view.At(_left - 3, _top) : under;
            var a = new Vector2(Math.Max(view.Min.X + 2, b.X - w - 2 * pad), b.Y);
            var dl = ImGui.GetForegroundDrawList();
            ScreenOverlay.Box(dl, a, new Vector2(b.X, a.Y + h), 0.82f, sy);
            float x = a.X + pad, y = a.Y + pad;
            dl.AddText(font, ts * 0.9f, new Vector2(x, y), ScreenOverlay.DimColour, Head);
            y += line * 1.05f;
            foreach (var l in lines)
            {
                uint col = IsDrawback(l) ? ScreenOverlay.Rgba(235, 120, 105) : ScreenOverlay.TextColour;
                dl.AddText(font, ts, new Vector2(x, y), col, l);
                y += line;
            }
        }
    }
}
