using System.Globalization;
using System.Numerics;
using ImGuiNET;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Host.Window;
using MarkerKind = KfIII.Automap.MarkerKind;

namespace KfIII;

/// <summary>
/// Draws the automap (<see cref="Automap"/> holds the data): a north-up minimap in a
/// corner of the picture, and the full map on the Map key, in the look of kf1-enhanced
/// (the port of the Japanese King's Field): walkable samples filled, a light band where
/// they meet solid ones, water blue, chasms hatched grey, floors shaded by their height
/// against the player's (lower is darker).
///
/// Both are floating ImGui panels over the game picture (OutputView), like the map of
/// Verdite2 (the RecompOne port of the first North American King's Field). The full map
/// holds the game while it is open (Automap.PausedForMap) and frees the mouse: the wheel
/// zooms toward the cursor, a left drag moves, a right click resets.
///
/// AutomapView.Terrain.cs draws the floor plan, AutomapView.Markers.cs the markers and
/// the player, AutomapView.Hover.cs the label of the marker under the mouse, and
/// AutomapView.Palette.cs holds the colours.
/// </summary>
public static partial class AutomapView
{
    public static bool FullOpen { get; private set; }
    /// <summary>Settings: the minimap's corner (0 top right, 1 bottom right, 2 top left,
    /// 3 bottom left), its side as a percentage of the picture's height, the tiles from its
    /// centre to an edge, and its opacity in percent.</summary>
    public static int MinimapCorner = 1, MinimapSize = 20, MinimapZoom = 7, MinimapOpacity = 85;

    /// <summary>The mouse was captured when the full map opened (it is given back on close).</summary>
    static bool _wasCaptured;
    /// <summary>The full map's zoom (1 fits what has been seen) and centre in samples;
    /// _centred once the player has moved it from the fitted centre.</summary>
    static float _zoom = 1f, _cx, _cz;
    static bool _centred;
    /// <summary>Wheel notches from the runtime's mouse events while the full map is open.</summary>
    static float _wheel;
    /// <summary>The full map is drawn behind the menu screen, which is presented 4:3, so
    /// it keeps the picture's rectangle from when it opened.</summary>
    static Vector2 _full0, _full1;
    static bool _fullRect;

    static readonly bool _miniInMenu = DevSwitch.Is("KFIII_TEST_MINI_MENU");
    static readonly (float X, float Z)? _testHover = DevSwitch.Text("KFIII_TEST_MAPHOVER")?.Split(',') is { Length: 2 } th
        && float.TryParse(th[0], out var hx) && float.TryParse(th[1], out var hz) ? (hx, hz) : null;

    const ImGuiWindowFlags Flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoNav
        | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoDocking
        | ImGuiWindowFlags.NoBackground;

    public static void Install()
    {
        PanelManager.Register(Mini.Instance);
        PanelManager.Register(Full.Instance);
        // KFIII_TEST_MAPWHEEL=secs:notches: that many wheel notches at that time.
        if (DevSwitch.Text("KFIII_TEST_MAPWHEEL")?.Split(':') is { Length: 2 } tw
            && double.TryParse(tw[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var wt)
            && float.TryParse(tw[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var wn))
            DevSwitch.Background("kf3-test-mapwheel", () => { Thread.Sleep((int)(wt * 1000)); _wheel += wn; Console.WriteLine("[KFIII] test: wheel " + wn); });
        Event.AddListener<MouseEvent>(e =>
        {
            if (FullOpen && e.Action == MouseAction.Wheel) { float w; do w = _wheel; while (Interlocked.CompareExchange(ref _wheel, w + e.Wheel, w) != w); }
        });
        Event.AddListener<KeyboardEvent>(e =>
        {
            if (!e.Pressed || e.Repeat || !Controls.IsKey(Controls.B.Map, e.Key)) return;
            if (PopupManager.AnyOpen && !FullOpen) return;
            SetFull(!FullOpen);
        });
        // KFIII_TEST_FULLMAP=secs or secs-secs: open the full map at the first time, and
        // close it at the second.
        var tf = DevSwitch.Text("KFIII_TEST_FULLMAP")?.Split('-');
        if (tf is { Length: >= 1 } && double.TryParse(tf[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var at))
        {
            double until = tf.Length > 1 && double.TryParse(tf[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var u) ? u : -1;
            DevSwitch.Background("kf3-test-fullmap", () =>
            {
                Thread.Sleep((int)(at * 1000)); Console.WriteLine("[KFIII] test: full map"); SetFull(true);
                if (until > at) { Thread.Sleep((int)((until - at) * 1000)); Console.WriteLine("[KFIII] test: full map closed"); SetFull(false); }
            });
        }
    }

    /// <summary>In the world: a world tick within the last half second and no menu up
    /// (KFIII_TEST_MINI_MENU=1 shows the minimap regardless).</summary>
    static bool InWorld => _miniInMenu || Automap.Enabled && Automap.Current != null && !Automap.InMenu
                           && Environment.TickCount64 - Automap.LastTick < 500;

    static void SetFull(bool open)
    {
        if (open && (!Automap.Enabled || Automap.Current == null)) return;
        if (open == FullOpen) return;
        FullOpen = open;
        if (open)
        {
            _wasCaptured = Controls.Captured;
            _fullRect = false; _wheel = 0f;
            Controls.SetCaptured(false);
            _zoom = 1f; _centred = false;
            // KFIII_TEST_MAPZOOM=z: open at that zoom, centred on the player.
            if (DevSwitch.Number("KFIII_TEST_MAPZOOM") is double z)
            { _zoom = (float)z; _cx = Automap.PlayerX; _cz = Automap.PlayerZ; _centred = true; }
        }
        else
        {
            if (_wasCaptured) Controls.SetCaptured(true);
            Automap.SaveNow();
        }
    }

    /// <summary>The game picture's rectangle on screen, or the main viewport's work area.</summary>
    static void Picture(out Vector2 p0, out Vector2 p1)
    {
        if (OutputView.Valid && OutputView.Size.X >= 32f && OutputView.Size.Y >= 32f) { p0 = OutputView.Min; p1 = OutputView.Max; return; }
        var vp = ImGui.GetMainViewport();
        p0 = vp.WorkPos; p1 = vp.WorkPos + vp.WorkSize;
    }

    static void Text(ImDrawListPtr dl, Vector2 at, float size, uint col, string s) =>
        dl.AddText(ImGui.GetFont(), size, at, col, s);

    /// <summary>A copy of this tick's markers.</summary>
    static Automap.Marker[] SnapshotMarkers()
    {
        lock (Automap.Markers) return Automap.Markers.ToArray();
    }

    static bool AnyMarker(MarkerKind kind)
    {
        lock (Automap.Markers) return Automap.Markers.Exists(q => q.Kind == kind);
    }

    // ---- the minimap ------------------------------------------------------------------

    sealed class Mini : IFloatingPanel
    {
        public static readonly Mini Instance = new();
        public string Name => "kf3minimap";
        public bool IsOpen { get => Automap.Minimap && !FullOpen && InWorld; set { } }

        public void Draw()
        {
            if (Automap.Current is not { } a) return;
            Picture(out var g0, out var g1);
            float gh = g1.Y - g0.Y;
            float side = gh * MinimapSize / 100f, margin = gh * 0.025f;
            // Top right sits below the compass, at the right edge of the (wide) picture;
            // top left below the HP/MP panel.
            bool right = MinimapCorner is 0 or 1, bottom = MinimapCorner is 1 or 3;
            float top = MinimapCorner == 0 ? g0.Y + gh * 0.16f : bottom ? g1.Y - side - margin : g0.Y + gh * 0.25f;
            var lo = new Vector2(right ? g1.X - side - margin : g0.X + margin, top);
            var hi = lo + new Vector2(side, side);
            ImGui.SetNextWindowPos(lo);
            ImGui.SetNextWindowSize(hi - lo);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            if (ImGui.Begin("##kf3minimap", Flags))
            {
                var dl = ImGui.GetWindowDrawList();
                float alpha = MinimapOpacity / 100f;
                dl.AddRectFilled(lo, hi, Col(10, 9, 8, 0.75f * alpha / 0.85f), side * 0.04f);
                dl.PushClipRect(lo, hi, true);
                int radius = MinimapZoom * Automap.PerTile;   // samples from the centre to an edge
                float s = side / (2f * radius);
                var o = (lo + hi) * 0.5f;
                int pcx = (int)Automap.PlayerX, pcz = (int)Automap.PlayerZ;
                DrawCells(dl, a, o, Automap.PlayerX, Automap.PlayerZ, s, pcx - radius - 1, pcz - radius - 1, pcx + radius + 1, pcz + radius + 1, alpha);
                DrawMarkers(dl, o, Automap.PlayerX, Automap.PlayerZ, s, lo, hi, false);
                DrawPlayer(dl, o, Math.Max(5f, side * 0.045f));
                dl.PopClipRect();
                dl.AddRect(lo, hi, Col(200, 180, 130, 0.8f), side * 0.04f, ImDrawFlags.None, 1.5f);
                float ts = Math.Max(12f, side * 0.08f);
                var n = ImGui.GetFont().CalcTextSizeA(ts, float.MaxValue, 0f, "N");
                Text(dl, new Vector2(o.X - n.X * 0.5f, lo.Y + side * 0.02f), ts, Col(230, 60, 50), "N");
            }
            ImGui.End();
            ImGui.PopStyleVar();
        }
    }

    // ---- the full map ---------------------------------------------------------------

    sealed class Full : IFloatingPanel
    {
        public static readonly Full Instance = new();
        public string Name => "kf3mapfull";
        public bool IsOpen
        {
            get
            {
                if (FullOpen && (!Automap.Enabled || Automap.Current == null)) SetFull(false);
                return FullOpen;
            }
            set { }
        }

        public void Draw()
        {
            if (Automap.Current is not { } a) return;
            if (!_fullRect) { Picture(out _full0, out _full1); _fullRect = true; }
            var g0 = _full0; var g1 = _full1;
            float gw = g1.X - g0.X, gh = g1.Y - g0.Y;
            ImGui.SetNextWindowPos(g0);
            ImGui.SetNextWindowSize(g1 - g0);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            if (ImGui.Begin("##kf3mapfull", Flags))
            {
                var dl = ImGui.GetWindowDrawList();
                dl.AddRectFilled(g0, g1, Col(6, 5, 4));

                // Fit what has been seen (the whole area with the reveal cheat) and the
                // player, with a little room.
                if (Automap.RevealAll) { a.X0 = Math.Min(a.X0, 0); a.Z0 = Math.Min(a.Z0, 0); a.X1 = Math.Max(a.X1, Automap.Size - 1); a.Z1 = Math.Max(a.Z1, Automap.Size - 1); }
                int x0 = a.X1 < 0 ? (int)Automap.PlayerX : Math.Min(a.X0, (int)Automap.PlayerX);
                int x1 = a.X1 < 0 ? (int)Automap.PlayerX : Math.Max(a.X1, (int)Automap.PlayerX);
                int z0 = a.X1 < 0 ? (int)Automap.PlayerZ : Math.Min(a.Z0, (int)Automap.PlayerZ);
                int z1 = a.X1 < 0 ? (int)Automap.PlayerZ : Math.Max(a.Z1, (int)Automap.PlayerZ);
                x0 -= 16; z0 -= 16; x1 += 16; z1 += 16;
                float legendW = gh * 0.30f;
                float areaX = g0.X + gh * 0.04f, areaY = g0.Y + gh * 0.08f;
                float areaW = gw - legendW - gh * 0.08f, areaH = gh * 0.86f;
                float fit = Math.Min(Math.Min(areaW / (x1 - x0), areaH / (z1 - z0)), gh / (3.5f * Automap.PerTile));
                if (!_centred) { _cx = (x0 + x1) * 0.5f; _cz = (z0 + z1) * 0.5f; }
                float s = fit * _zoom;
                var o = new Vector2(areaX + areaW * 0.5f, areaY + areaH * 0.5f);

                var io = ImGui.GetIO();
                var mouse = io.MousePos;
                bool inside = mouse.X >= areaX && mouse.X < areaX + areaW && mouse.Y >= areaY && mouse.Y < areaY + areaH;
                // The runtime's wheel events when there are any, else ImGui's: one source, not both.
                float ev = Interlocked.Exchange(ref _wheel, 0f), wheel = ev != 0f ? ev : io.MouseWheel;
                if (wheel != 0f)
                {
                    // Toward the cursor when it is over the map, else about the centre.
                    var zp = inside ? mouse : o;
                    float wx = _cx + (zp.X - o.X) / s, wz = _cz - (zp.Y - o.Y) / s;
                    _zoom = Math.Clamp(_zoom * MathF.Pow(1.25f, Math.Clamp(wheel, -4f, 4f)), 1f, 24f);
                    s = fit * _zoom;
                    _cx = wx - (zp.X - o.X) / s; _cz = wz + (zp.Y - o.Y) / s;
                    _centred = true;
                }
                if (inside && ImGui.IsMouseDown(ImGuiMouseButton.Left) && (io.MouseDelta.X != 0f || io.MouseDelta.Y != 0f))
                {
                    _cx -= io.MouseDelta.X / s; _cz += io.MouseDelta.Y / s; _centred = true;
                }
                if (inside && ImGui.IsMouseClicked(ImGuiMouseButton.Right)) { _zoom = 1f; _centred = false; _cx = (x0 + x1) * 0.5f; _cz = (z0 + z1) * 0.5f; s = fit; }
                _cx = Math.Clamp(_cx, x0, x1); _cz = Math.Clamp(_cz, z0, z1);

                var c0 = new Vector2(areaX, areaY);
                var c1 = new Vector2(areaX + areaW, areaY + areaH);
                dl.PushClipRect(c0, c1, true);
                float hw = areaW * 0.5f / s + 1f, hh = areaH * 0.5f / s + 1f;
                DrawCells(dl, a, o, _cx, _cz, s, (int)(_cx - hw), (int)(_cz - hh), (int)(_cx + hw), (int)(_cz + hh), 1f);
                DrawMarkers(dl, o, _cx, _cz, s, c0, c1, true);
                var pp = new Vector2(o.X + (Automap.PlayerX - _cx) * s, o.Y - (Automap.PlayerZ - _cz) * s);
                DrawPlayer(dl, pp, Math.Max(7f, s * Automap.PerTile * 0.45f));
                dl.PopClipRect();
                // KFIII_TEST_MAPHOVER=x,z (samples): point there.
                if (_testHover is { } th)
                { mouse = new Vector2(o.X + (th.X - _cx) * s, o.Y - (th.Z - _cz) * s); inside = true; }
                if (inside) DrawHover(dl, o, _cx, _cz, s, mouse, g0, g1, Math.Max(14f, gh / 38f));

                float ts = Math.Max(14f, gh / 34f);
                Text(dl, new Vector2(g0.X + gh * 0.04f, g0.Y + gh * 0.02f), ts * 1.4f, Col(240, 220, 170), "Map");
                DrawLegend(dl, new Vector2(g1.X - legendW, g0.Y + gh * 0.1f), ts);
            }
            ImGui.End();
            ImGui.PopStyleVar();
        }
    }

    /// <summary>The full map's legend from <paramref name="at"/> down (what the area has,
    /// and what the cheats add), then the controls.</summary>
    static void DrawLegend(ImDrawListPtr dl, Vector2 at, float ts)
    {
        void Legend(uint col, string label, bool line = false, bool hollow = false)
        {
            var cc = new Vector2(at.X + ts * 0.5f, at.Y + ts * 0.55f);
            if (line) dl.AddLine(cc - new Vector2(ts * 0.4f, 0), cc + new Vector2(ts * 0.4f, 0), col, Math.Max(2f, ts * 0.15f));
            else if (hollow) dl.AddRect(cc - new Vector2(ts * 0.35f, ts * 0.3f), cc + new Vector2(ts * 0.35f, ts * 0.3f), col, 0f, ImDrawFlags.None, Math.Max(1.5f, ts * 0.12f));
            else dl.AddRectFilled(cc - new Vector2(ts * 0.35f, ts * 0.3f), cc + new Vector2(ts * 0.35f, ts * 0.3f), col);
            Text(dl, new Vector2(at.X + ts * 1.3f, at.Y), ts, Col(230, 220, 200), label);
            at.Y += ts * 1.2f;
        }
        Legend(Col(78, 70, 56), "Floor (darker is lower)");
        Legend(Palette.Wall, "Wall", true);
        Legend(Palette.Water, "Water");
        if (Automap.HasLava) Legend(Palette.Lava, "Lava");
        Legend(Palette.Chasm, "Chasm (fatal drop)");
        Legend(Palette.Door, "Door", true);
        Legend(Palette.LockedDoor, "Locked door", true);
        Legend(Col(240, 200, 80), "Key pedestal");
        Legend(Palette.SavePoint, "Save point");
        Legend(Palette.Exit, "Exit to another area", hollow: true);
        Legend(Palette.Chest, "Chest");
        Legend(Palette.Chest, "Chest (opened)", hollow: true);
        Legend(Palette.Barrel, "Barrel");
        Legend(Col(225, 220, 200), "Remains (skull)");
        Legend(Col(170, 170, 175), "Grave");
        Legend(Col(60, 110, 170), "Well");
        Legend(Palette.Person, "Person");
        Legend(Palette.Tree, "Tree");
        if (AnyMarker(MarkerKind.Stump)) Legend(Palette.Stump, "Giant tree stump");
        if (AnyMarker(MarkerKind.Lamp)) { Legend(Palette.Lamp, "Crystal lamp"); Legend(Palette.LampLit, "Crystal lamp (lit)"); }
        if (AnyMarker(MarkerKind.Portal)) Legend(Palette.Portals[0].Col, "Portal (colour = its pair)");
        if (AnyMarker(MarkerKind.Throne)) Legend(Col(225, 185, 70), "Throne of Ichrius");
        if (AnyMarker(MarkerKind.Warp)) Legend(Palette.Warp, "Warp glyph (step on it)");
        if (Automap.ShowItems) Legend(Palette.Item, "Item");
        if (Automap.ShowItems || Automap.ShowContents) Legend(Palette.Item, "Hidden item (sparkle)");
        if (Automap.ShowEnemies) Legend(Palette.Enemy, "Enemy");
        if (Automap.ShowTraps) Legend(Col(220, 50, 40), "Trap");
        if (Automap.ShowTraps) Legend(Palette.Gas, "Trapped chest (badge)");
        if (Automap.ShowSecrets) Legend(Palette.Secret, "Secret wall", true);
        if (Automap.ShowContents) Legend(Palette.Item, "Has an item (ring)", hollow: true);
        at.Y += ts;
        Text(dl, at, ts * 0.85f, Col(170, 160, 140), "Scroll: zoom\nDrag: move\nRight click: reset\nPoint at a marker for details\n" + KeyHints.Pretty(Controls.B.Map) + " closes the map");
    }
}
