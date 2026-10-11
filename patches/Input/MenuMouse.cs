using System.Diagnostics;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Hardware;
using RecompOne.Runtime.Hle;
using RecompOne.Runtime.Host;
using RecompOne.Runtime.Host.Window;
using RecompOne.Runtime.Memory;
using Game = Recompiled.KingsFieldIII_game;
using MouseButton = Silk.NET.Input.MouseButton;

namespace KfIII;

/// <summary>
/// The menus and shops by mouse (Settings "Mouse in menus and shops"): the row or
/// button under the pointer is highlighted, left click is Cross, right click Circle,
/// middle click Triangle, and the wheel moves up and down. A key press hands the menu
/// back to the keyboard until the mouse moves again, so a resting pointer never fights
/// W/S.
///
/// Menus open with kf_MenuEnter and close with kf_MenuLeave; a pointer captured for
/// mouse look is freed between the two.
///
/// Lists: kf_MenuListInput(list, items, ...) handles a list's pad input. After it runs,
/// the highlight is moved to the row under the pointer, with the click and the item's
/// rotating model (kf_MenuSound, kf_MenuShowItem) that an Up/Down press gives. The list
/// record: +0x1C x, +0x1D y, +0x1E entries, +0x1F rows shown, +0x20 first row shown,
/// +0x21 the selected entry (first row + highlighted row), +0x22 the highlighted row.
/// Rows are 16 apart from y + 5, in a window 266 wide.
///
/// The main menu's buttons: kf_MenuButtons(page, count, selected) draws them from
/// <see cref="GameAddr.MenuButtonPages"/>; kf_MenuButtonInput(selected, last, ...)
/// returns the new selection, which is replaced by the button under the pointer.
///
/// Clicks and the wheel are pad buttons pressed in the pad read while a menu is open.
/// </summary>
public static class MenuMouse
{
    public static bool Enabled = true;

    static readonly bool _probe = DevSwitch.Is("KFIII_MENUMOUSE_PROBE");

    static bool _inMenu, _wasCaptured;
    // The mouse has the menu: it moved or clicked since the last key press.
    static bool _mouseMode;
    // The wheel moves the highlight itself (Up/Down presses). Hover would put it back
    // under the pointer, so hover waits until the mouse moves again.
    static bool _hoverOff;
    static int _mx, _my, _anchorX, _anchorY;
    // Wheel notches not yet turned into presses; the current press and its direction.
    static float _wheel;
    static long _wheelPressUntil;
    static int _wheelDir;

    public static void Install()
    {
        Event.AddListener<MouseEvent>(e =>
        {
            if (e.Action == MouseAction.Move)
            {
                _mx = e.X; _my = e.Y;
                // More than a jitter (4 pixels) from where it rested: the mouse takes over.
                if (Math.Abs(e.X - _anchorX) + Math.Abs(e.Y - _anchorY) > 4) { _mouseMode = true; _hoverOff = false; }
            }
            else if (e.Action == MouseAction.Wheel && _inMenu && Enabled)
            {
                _wheel += e.Wheel;
                _hoverOff = true;
                _anchorX = _mx; _anchorY = _my;
            }
            else if (e.Action == MouseAction.Button && e.Pressed) _mouseMode = true;
        });
        Event.AddListener<KeyboardEvent>(e =>
        {
            if (!e.Pressed) return;
            _mouseMode = false; _anchorX = _mx; _anchorY = _my;
        });
        Event.AddListener<PadReadEvent>(OnPad);
        if (_testPoints.Count > 0)
            Event.AddListener<OverlayLoadedEvent>(e =>
            {
                if (_testStartAt < 0 && e.Name.StartsWith("fdat", StringComparison.Ordinal))
                    _testStartAt = _clock.Elapsed.TotalSeconds;
            });
    }

    // ---- menu open / closed ---------------------------------------------------------------

    /// <summary>After kf_MenuEnter: frees a captured pointer; the keyboard has the menu first.</summary>
    public static void AfterMenuEnter(CpuContext c, IMemory m)
    {
        _inMenu = true;
        if (!Enabled) return;
        _wasCaptured = Controls.Captured;
        if (_wasCaptured) Controls.SetCaptured(false);
        _mouseMode = false; _anchorX = _mx; _anchorY = _my;
    }

    /// <summary>After kf_MenuLeave: captures the pointer again if the menu freed it.</summary>
    public static void AfterMenuLeave(CpuContext c, IMemory m)
    {
        _inMenu = false;
        if (_wasCaptured && !AutomapView.FullOpen) Controls.SetCaptured(true);
        _wasCaptured = false;
    }

    public static bool InMenu => _inMenu;

    // ---- the pointer ----------------------------------------------------------------------

    // KFIII_TEST_MENUPOINT=8:60,40;11:60,70 -- a virtual pointer at those menu
    // coordinates from those seconds on. The times count from the first area module
    // (fdat overlay) loading, as KFIII_AUTOPAD's fdat+ times do.
    static readonly List<(double At, float X, float Y)> _testPoints = ParseTestPoints();
    static readonly Stopwatch _clock = Stopwatch.StartNew();
    static double _testStartAt = -1;

    static List<(double, float, float)> ParseTestPoints()
    {
        var points = new List<(double, float, float)>();
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var part in (DevSwitch.Text("KFIII_TEST_MENUPOINT") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var a = part.Split(':'); var xy = a[1].Split(',');
            points.Add((double.Parse(a[0], inv), float.Parse(xy[0], inv), float.Parse(xy[1], inv)));
        }
        return points;
    }

    /// <summary>The pointer in the game's 320 x 240 menu coordinates (widescreen margins taken off).</summary>
    static bool Pointer(out float gx, out float gy)
    {
        gx = gy = 0;
        if (_testPoints.Count > 0)
        {
            if (_testStartAt < 0) return false;
            double t = _clock.Elapsed.TotalSeconds - _testStartAt;
            bool any = false;
            foreach (var (at, x, y) in _testPoints)
                if (t >= at) { gx = x; gy = y; any = true; }
            if (any) { _mouseMode = true; return true; }
        }
        if (!OutputView.Valid) return false;
        var p0 = OutputView.Min; var p1 = OutputView.Max;
        int margin = Display.WideAspect > 0f ? Math.Max(0, Display.WideMargin(320)) : 0;
        float sx = (p1.X - p0.X) / (320f + 2 * margin), sy = (p1.Y - p0.Y) / 240f;
        gx = (_mx - p0.X) / sx - margin; gy = (_my - p0.Y) / sy;
        return true;
    }

    static bool Active => Enabled && _inMenu && (_mouseMode || _testPoints.Count > 0) && !AutomapView.FullOpen;

    /// kf_MenuSound's cursor-move click, which an Up/Down press plays.
    const uint MoveClick = 0x0C;

    // ---- lists ------------------------------------------------------------------------------

    const int ListWidth = 266, RowHeight = 16, FirstRowY = 5;

    static uint _list, _listItems;

    /// <summary>Before kf_MenuListInput: notes the list record (a0) and its item array (a1, may be 0).</summary>
    public static void BeforeListInput(CpuContext c, IMemory m) { _list = c.A0; _listItems = c.A1; }

    /// <summary>After kf_MenuListInput: highlights the row under the pointer.</summary>
    public static void AfterListInput(CpuContext c, IMemory m)
    {
        uint l = _list;
        if (!Active || _hoverOff || l == 0 || !Pointer(out var gx, out var gy)) return;
        int x = m.ReadU8(l + 0x1C), y = m.ReadU8(l + 0x1D), rows = m.ReadU8(l + 0x1F), count = m.ReadU8(l + 0x1E);
        if (rows == 0) return;
        if (gx < x || gx > x + ListWidth || gy < y + FirstRowY) return;
        int row = (int)((gy - y - FirstRowY) / RowHeight);
        if (row < 0 || row >= rows || row >= Math.Max(count, 1)) return;
        int first = m.ReadU8(l + 0x20);
        if (m.ReadU8(l + 0x22) != row || m.ReadU8(l + 0x21) != first + row)
        {
            // The highlight drawn, and the entry the page acts on.
            m.WriteU8(l + 0x22, (byte)row);
            m.WriteU8(l + 0x21, (byte)(first + row));
            // As an Up/Down press does in kf_MenuListInput (0x800223B0): the click, and
            // with an item array the rotating model of the item now selected.
            var saved = c.Snapshot();
            try
            {
                c.A0 = MoveClick; Game.kf_MenuSound(c, m);
                if (_listItems != 0) { c.A0 = m.ReadU8(_listItems + (uint)(first + row)); Game.kf_MenuShowItem(c, m); }
            }
            finally { c.Restore(saved); }
            if (_probe) Console.WriteLine($"[KFIII] menumouse list {l:X8} at {x},{y} rows {rows} count {count} -> row {row}");
        }
    }

    // ---- the main menu's buttons --------------------------------------------------------------

    static int _buttonsPage = -1, _buttonsCount;

    /// <summary>The last kf_MenuButtons page drawn (page 0 is the main menu, beside the status window).</summary>
    public static int ButtonsPage => _buttonsPage;
    /// <summary>When it was drawn (Environment.TickCount64).</summary>
    public static long ButtonsAt { get; private set; }

    /// <summary>Before kf_MenuButtons(page, count, selected): notes the page; on the main menu, StatProgress reads the stats.</summary>
    public static void BeforeButtons(CpuContext c, IMemory m)
    {
        if (_probe && (int)c.A0 != _buttonsPage) Console.WriteLine($"[KFIII] menumouse buttons page {c.A0} count {c.A1}");
        _buttonsPage = (int)c.A0; _buttonsCount = (int)c.A1; ButtonsAt = Environment.TickCount64;
        if (_buttonsPage == 0) StatProgress.Capture(m);
    }

    /// <summary>After kf_MenuButtonInput: the button under the pointer becomes the selection (v0).</summary>
    public static void AfterButtonInput(CpuContext c, IMemory m)
    {
        if (!Active || _hoverOff || _buttonsPage < 0 || !Pointer(out var gx, out var gy)) return;
        uint firstRecord = GameAddr.MenuButtonPages + (uint)_buttonsPage * 0xFC + 0x1C;
        for (int i = 0; i < _buttonsCount; i++)
        {
            uint rec = firstRecord + (uint)i * 0x1C;
            int bx = (short)m.ReadU16(rec), by = (short)m.ReadU16(rec + 2);
            // A button's box: from 6 above and left of its text, 116 wide and 22 high.
            if (gx >= bx - 6 && gx < bx + 110 && gy >= by - 6 && gy < by + 16)
            {
                if (c.V0 != (uint)i)
                {
                    if (_probe) Console.WriteLine($"[KFIII] menumouse button {i} at {bx},{by}");
                    // The click an Up/Down press gives.
                    var saved = c.Snapshot();
                    try { c.A0 = MoveClick; Game.kf_MenuSound(c, m); }
                    finally { c.Restore(saved); }
                }
                c.V0 = (uint)i;
                return;
            }
        }
    }

    // ---- clicks and the wheel -------------------------------------------------------------------

    /// <summary>When the pointer was last over an info panel that takes clicks itself
    /// (Environment.TickCount64; StatProgress sets it). A left click there is not Cross.</summary>
    public static long OverOverlayAt;

    // Each wheel notch is an Up or Down press of 60 ms, with 60 ms between presses.
    const long WheelPressMs = 60;

    static void OnPad(PadReadEvent e)
    {
        if (e.Port != 0 || !Enabled || !_inMenu || AutomapView.FullOpen) return;
        ushort press = 0;
        if (_mouseMode)
        {
            if (HostWindow.IsMouseButtonDown(MouseButton.Left) && Environment.TickCount64 - OverOverlayAt > 150) press |= Controller.Cross;
            if (HostWindow.IsMouseButtonDown(MouseButton.Right)) press |= Controller.Circle;
            if (HostWindow.IsMouseButtonDown(MouseButton.Middle)) press |= Controller.Triangle;
        }
        long now = Environment.TickCount64;
        if (_wheel != 0 && now >= _wheelPressUntil + WheelPressMs)
        {
            _wheelDir = _wheel > 0 ? -1 : 1;
            _wheel -= Math.Sign(_wheel);
            if (Math.Abs(_wheel) < 1) _wheel = 0;
            _wheelPressUntil = now + WheelPressMs;
        }
        if (now < _wheelPressUntil) press |= _wheelDir < 0 ? Controller.Up : Controller.Down;
        if (press == 0) return;
        // Active low, bytes swapped against Controller's layout (as in Controls.SecondaryKeys).
        e.Buttons &= (ushort)~(ushort)((press >> 8) | (press << 8));
    }
}
