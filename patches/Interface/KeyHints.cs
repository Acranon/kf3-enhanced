using System.Numerics;
using ImGuiNET;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Host.Window;
using RecompOne.Runtime.Memory;

namespace KfIII;

/// <summary>
/// Enhancements "Key hints in menus": under the menus' button hints, a line saying
/// which keyboard key is each button, from the current key bindings: "✕ E   △ Q   ○ Tab".
///
/// <para>The game's hint bar ("✕SELECT △GUIDE ○RETURN") is one pre-drawn image, sprites
/// of 127 x 14 in palette 0x7804 at y 195 or below (from a GPU capture of a menu frame),
/// in the same place in every menu. A menu screen that does not change is not redrawn,
/// so the sprites cannot be watched frame by frame; the legend is shown instead for as
/// long as a menu is open. Menus (kf_Menu, the shops, the pause screen) open with
/// kf_MenuEnter and close with kf_MenuLeave; the pause screen (kf_Pause) has no legend.</para>
///
/// <para>KFIII_KEYHINTS_PROBE=1 logs the menu hooks, the primitives drawn in the bottom
/// of the screen and palette 0x7804's sprites, and what the legend draws.</para>
/// </summary>
public static class KeyHints
{
    public static bool Enabled = true;
    static readonly bool _probe = DevSwitch.Is("KFIII_KEYHINTS_PROBE");

    // The hint bar's place in every menu, in game pixels; its palette.
    const int BarLeft = 96, BarRight = 224, BarY = 223;
    const int BarClut = 0x7804;

    static bool _inMenu, _paused;

    /// <summary>Before kf_Pause.</summary>
    public static void BeforePause(CpuContext c, IMemory m) => _paused = true;

    /// <summary>After kf_Pause.</summary>
    public static void AfterPause(CpuContext c, IMemory m) => _paused = false;

    /// <summary>After kf_MenuEnter: a menu screen is up.</summary>
    public static void AfterMenuEnter(CpuContext c, IMemory m)
    {
        _inMenu = true;
        if (_probe) Console.WriteLine("[KFIII] keyhints menu enter");
    }

    /// <summary>After kf_MenuLeave: the menu screen is gone.</summary>
    public static void AfterMenuLeave(CpuContext c, IMemory m)
    {
        _inMenu = false;
        if (_probe) Console.WriteLine("[KFIII] keyhints menu leave");
    }

    public static void Install()
    {
        Event.AddListener<RenderPrimEvent>(OnPrim);
        PanelManager.Register(Panel.Instance);
    }

    // The probe's counters: primitives and bar-palette primitives in the current second,
    // and the low-screen primitives logged since the last pause in that log.
    static int _primCount, _barCount, _lowLogged;
    static long _countSince, _lowPausedAt, _drawLoggedAt;

    static void OnPrim(RenderPrimEvent e)
    {
        if (!_probe) return;
        _primCount++;
        if (e.Clut == BarClut)
        {
            _barCount++;
            if (_inMenu && _barCount <= 12)
                Console.WriteLine($"[KFIII] keyhints bar? n{e.Count} tex {e.Textured} x {string.Join(',', e.X.Take(e.Count))} y {string.Join(',', e.Y.Take(e.Count))} area {e.DrawLeft},{e.DrawTop}");
        }
        long t = Environment.TickCount64;
        if (t - _countSince > 1000)
        {
            Console.WriteLine($"[KFIII] keyhints prims/s {_primCount} clut7804 {_barCount} inMenu {_inMenu}");
            _primCount = _barCount = 0; _countSince = t;
        }
        // Textured primitives low on the screen, 40 at a time with 2 s between.
        if (e.Textured && Environment.TickCount64 - _lowPausedAt > 2000)
        {
            int last = Math.Max(1, e.Count - 1);
            int yy = Math.Min(e.Y[0], e.Y[last]) - e.DrawTop;
            if (yy >= 180)
                Console.WriteLine($"[KFIII] keyhints prim n{e.Count} clut {e.Clut:X4} x {e.X[0] - e.DrawLeft}..{e.X[last] - e.DrawLeft} y {yy}..{e.Y[last] - e.DrawTop}");
            if (++_lowLogged > 40) { _lowPausedAt = Environment.TickCount64; _lowLogged = 0; }
        }
    }

    /// <summary>A key's name as a player reads it.</summary>
    public static string Pretty(string k) => k switch
    {
        "" or null => "",
        "KeypadAdd" => "Num +",
        "KeypadSubtract" => "Num -",
        "KeypadEnter" => "Num Enter",
        "ShiftLeft" => "L Shift",
        "ShiftRight" => "R Shift",
        "ControlLeft" => "L Ctrl",
        "ControlRight" => "R Ctrl",
        "AltLeft" => "L Alt",
        "AltRight" => "R Alt",
        "Space" => "Space",
        "Escape" => "Esc",
        _ when k.StartsWith("Keypad") => "Num " + k[6..],
        _ when k.StartsWith("Number") => k[6..],
        _ => k,
    };

    sealed class Panel : IFloatingPanel
    {
        public static readonly Panel Instance = new();
        public string Name => "kf3keyhints";
        public bool IsOpen { get => Enabled && _inMenu && !_paused; set { } }

        public void Draw()
        {
            try { DrawCore(); } catch (Exception e) { Console.WriteLine($"[KFIII] keyhints: {e}"); }
        }

        void DrawCore()
        {
            if (_probe && Environment.TickCount64 - _drawLoggedAt > 1000)
            {
                _drawLoggedAt = Environment.TickCount64;
                Console.WriteLine($"[KFIII] keyhints draw valid {OutputView.Valid} bar {BarLeft}..{BarRight} y {BarY}");
            }
            if (!OutputView.Valid) return;
            var keys = ConfigManager.Game.Keys;
            var items = new List<(Shape Shape, string Key)>();
            void Add(Shape shape, string k) { if (!string.IsNullOrEmpty(k)) items.Add((shape, Pretty(k))); }
            Add(Shape.Cross, keys.Cross); Add(Shape.Triangle, keys.Triangle); Add(Shape.Circle, keys.Circle);
            if (_probe && Environment.TickCount64 - _drawLoggedAt < 5)
                Console.WriteLine($"[KFIII] keyhints keys {keys.Cross}/{keys.Triangle}/{keys.Circle}/{keys.Square} n {items.Count} at {OutputView.Min}..{OutputView.Max}");
            if (items.Count == 0) return;

            // Centred under the bar.
            var view = GameView.Current;
            float cx = view.At((BarLeft + BarRight) * 0.5f, 0).X;
            float y = view.Min.Y + (BarY + 2) * view.Sy;
            float ts = Math.Max(13f, 8.5f * view.Sy);
            var font = ImGui.GetFont();
            float icon = ts * 0.9f, gapIcon = ts * 0.35f, gapItem = ts * 1.4f;
            float total = 0f;
            foreach (var it in items) total += icon + gapIcon + font.CalcTextSizeA(ts, float.MaxValue, 0f, it.Key).X;
            total += gapItem * (items.Count - 1);
            float x = cx - total * 0.5f;

            var dl = ImGui.GetForegroundDrawList();
            dl.AddRectFilled(new Vector2(x - 8, y - 2), new Vector2(x + total + 8, y + ts + 4), 0xB0000000u, ts * 0.3f);
            foreach (var (shape, key) in items)
            {
                var c = new Vector2(x + icon * 0.5f, y + ts * 0.55f);
                float r = icon * 0.42f;
                // The button symbols in their PlayStation colours (ImGui colours are ABGR).
                switch (shape)
                {
                    case Shape.Cross:      // blue
                        dl.AddLine(c - new Vector2(r, r), c + new Vector2(r, r), 0xFFE0A070u, 2f);
                        dl.AddLine(c + new Vector2(-r, r), c + new Vector2(r, -r), 0xFFE0A070u, 2f);
                        break;
                    case Shape.Triangle:   // green
                        dl.AddTriangle(c + new Vector2(0, -r), c + new Vector2(r, r * 0.8f), c + new Vector2(-r, r * 0.8f), 0xFF80D070u, 2f);
                        break;
                    case Shape.Circle:     // red
                        dl.AddCircle(c, r, 0xFF6060E0u, 16, 2f);
                        break;
                }
                x += icon + gapIcon;
                dl.AddText(font, ts, new Vector2(x, y), 0xFFE8E8E8u, key);
                x += font.CalcTextSizeA(ts, float.MaxValue, 0f, key).X + gapItem;
            }
        }

        enum Shape { Cross, Triangle, Circle }
    }
}
