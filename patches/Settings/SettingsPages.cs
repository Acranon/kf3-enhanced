using System.Numerics;
using ImGuiNET;
using RecompOne.Runtime.Host.Window;

namespace KfIII;

// The settings pages under System > Settings: Classic mode, Quality of life and Map of
// their own, and a "Picture" and a "Sound" group added to the runtime's Display and
// Audio pages. Each page edits Enhancements.Current and, on any change, applies and
// saves it. The settings themselves are in Enhancements.cs.
public static partial class Enhancements
{
    static readonly (string Label, string Value)[] _aspects =
    [
        ("4:3 (original)", "4:3"), ("16:9", "16:9"), ("16:10", "16:10"), ("21:9", "21:9"), ("32:9", "32:9"),
    ];

    static readonly (string Label, int Value)[] _rates =
    [
        ("Match monitor", -1), ("Original (15)", 0), ("30", 30), ("60", 60), ("120", 120),
        ("144", 144), ("165", 165), ("240", 240),
    ];

    static readonly (string Label, int Value)[] _aaModes = [("Off", 0), ("2x", 2), ("4x", 4), ("8x", 8)];
    static readonly string[] _aoQualities = ["Low", "Medium", "High"];
    static readonly string[] _reflectionLevels = ["Off", "Subtle", "Strong"];
    static readonly string[] _soundModes = ["Off", "Speakers", "Headphones"];
    static readonly string[] _jumpHeights = ["Low", "Normal", "High"];
    static readonly string[] _damageLogPositions = ["Bottom left", "Top left"];
    static readonly string[] _damageBarPositions = ["Top", "Bottom"];
    static readonly string[] _minimapCorners = ["Top right", "Bottom right", "Top left", "Bottom left"];

    // ---- controls ----------------------------------------------------------------------
    //
    // Each control takes a localization key for its label, shows key + "_hint" as its
    // tooltip when there is one, and marks the page changed; Done applies and saves.

    static bool _changed;
    static void Done() { if (_changed) { _changed = false; Apply(); Save(); } }

    static void Check(string key, bool value, Action<bool> set)
    {
        bool v = value;
        if (ImGui.Checkbox(Localization.T(key), ref v)) { set(v); _changed = true; }
        Hint(key + "_hint");
    }

    static void Combo(string key, int value, string[] items, Action<int> set)
    {
        int v = Math.Clamp(value, 0, items.Length - 1);
        if (ImGui.Combo(Localization.T(key), ref v, items, items.Length)) { set(v); _changed = true; }
        Hint(key + "_hint");
    }

    /// A combo over labelled values; an unknown value shows as the first.
    static void ValueCombo<T>(string key, (string Label, T Value)[] items, T value, Action<T> set,
        Func<int, string>? previewOf = null)
    {
        int current = Math.Max(0, Array.FindIndex(items, item => EqualityComparer<T>.Default.Equals(item.Value, value)));
        string preview = previewOf != null ? previewOf(current) : items[current].Label;
        if (ImGui.BeginCombo(Localization.T(key), preview))
        {
            for (int i = 0; i < items.Length; i++)
                if (ImGui.Selectable(items[i].Label, i == current)) { set(items[i].Value); _changed = true; }
            ImGui.EndCombo();
        }
        Hint(key + "_hint");
    }

    static void Slider(string key, int value, int min, int max, string format, Action<int> set)
    {
        int v = Math.Clamp(value, min, max);
        if (ImGui.SliderInt(Localization.T(key), ref v, min, max, format)) { set(v); _changed = true; }
        Hint(key + "_hint");
    }

    static void Hint(string key)
    {
        if (ImGui.IsItemHovered() && Localization.T(key) is var t && t != key) ImGui.SetTooltip(t);
    }

    static readonly Vector4 _warnColour = new(1f, 0.78f, 0.35f, 1f);

    /// A caution shown under an option, always visible (not only on hover).
    static void Warning(string key)
    {
        ImGui.Indent();
        ImGui.PushTextWrapPos(0f);
        ImGui.TextColored(_warnColour, Localization.T(key));
        ImGui.PopTextWrapPos();
        ImGui.Unindent();
    }

    /// Why a page's options are greyed out, and where to change it. A page of picture
    /// options (<paramref name="pictureOnly"/>) is greyed by full classic only, a page
    /// of additions by either classic mode.
    static void ClassicNote(bool pictureOnly)
    {
        int c = Current.ClassicMode;
        if (c == 0 || (pictureOnly && c == 2)) return;
        ImGui.PushTextWrapPos(0f);
        ImGui.TextColored(_warnColour,
            Localization.T(c == 1 ? "kf3.classic.note_full" : "kf3.classic.note_visual"));
        ImGui.PopTextWrapPos();
        ImGui.Spacing();
    }

    // ---- Classic mode ------------------------------------------------------------------

    sealed class ClassicSection : ISettingsSection
    {
        public string Id => "kf3.classic";
        public string TitleKey => "kf3.classic";
        public int Order => -5;

        public void Draw()
        {
            var s = Current;
            ImGui.PushTextWrapPos(0f);
            ImGui.TextUnformatted(Localization.T("kf3.classic.intro"));
            ImGui.PopTextWrapPos();
            ImGui.Spacing();
            for (int i = 0; i < 3; i++)
            {
                if (ImGui.RadioButton(Localization.T($"kf3.classic.m{i}"), s.ClassicMode == i)) { s.ClassicMode = i; _changed = true; }
                ImGui.Indent();
                ImGui.PushTextWrapPos(0f);
                ImGui.TextDisabled(Localization.T($"kf3.classic.m{i}_desc"));
                ImGui.PopTextWrapPos();
                ImGui.Unindent();
                ImGui.Spacing();
            }
            ImGui.Separator();
            ImGui.PushTextWrapPos(0f);
            ImGui.TextDisabled(Localization.T("kf3.classic.kept"));
            ImGui.PopTextWrapPos();
            Done();
        }
    }

    // ---- Display: a "Picture" group under the runtime's own page ------------------------

    static void DrawDisplay()
    {
        var s = Current;
        ImGui.Spacing();
        ImGui.SeparatorText(Localization.T("kf3.disp.group"));
        ClassicNote(true);
        ImGui.BeginDisabled(s.ClassicMode == 1);

        ValueCombo("kf3.enh.aspect", _aspects, s.Aspect, v => s.Aspect = v);
        ValueCombo("kf3.enh.fps", _rates, s.Fps, v => s.Fps = v, i => _rates[i].Value == -1
            ? $"{_rates[i].Label} ({RecompOne.Runtime.Interp.Interp.RefreshRate} Hz)"
            : _rates[i].Label);
        ValueCombo("kf3.enh.aa", _aaModes, s.AntiAliasing, v => s.AntiAliasing = v);

        ImGui.Spacing();
        Check("kf3.enh.stable", s.StableGeometry, v => s.StableGeometry = v);
        Check("kf3.enh.straight", s.StraightTextures, v => s.StraightTextures = v);
        Check("kf3.enh.colour", s.SmoothColour, v => s.SmoothColour = v);
        Check("kf3.enh.filter", s.TextureFiltering, v => s.TextureFiltering = v);
        Check("kf3.enh.depth", s.DepthBuffer, v => s.DepthBuffer = v);
        Check("kf3.enh.fog", s.SmoothFog, v => s.SmoothFog = v);
        Check("kf3.enh.ppl", s.PerPixelLighting, v => s.PerPixelLighting = v);
        Check("kf3.enh.ao", s.AmbientOcclusion, v => s.AmbientOcclusion = v);
        ImGui.BeginDisabled(!s.AmbientOcclusion);
        ImGui.Indent();
        Combo("kf3.enh.ao_quality", s.AoQuality, _aoQualities, v => s.AoQuality = v);
        ImGui.Unindent();
        ImGui.EndDisabled();
        Check("kf3.enh.water", s.SeamlessWater, v => s.SeamlessWater = v);
        Combo("kf3.enh.reflect", s.WaterReflections, _reflectionLevels, v => s.WaterReflections = v);
        Check("kf3.enh.movies", s.SmoothMovies, v => s.SmoothMovies = v);

        ImGui.EndDisabled();
        Done();
    }

    // ---- Audio: a "Sound" group under the runtime's own page ----------------------------

    static void DrawAudio()
    {
        var s = Current;
        ImGui.Spacing();
        ImGui.SeparatorText(Localization.T("kf3.aud.group"));
        Combo("kf3.enh.sound", s.DirectionalSound, _soundModes, v => s.DirectionalSound = v);
        Done();
    }

    // ---- Quality of life ------------------------------------------------------------------

    sealed class QolSection : ISettingsSection
    {
        public string Id => "kf3.qol";
        public string TitleKey => "kf3.qol";
        public int Order => 12;

        public void Draw()
        {
            var s = Current;
            // Controls first: Classic mode never changes them.
            ImGui.SeparatorText(Localization.T("kf3.qol.controls"));
            Check("kf3.enh.menumouse", s.MenuMouse, v => s.MenuMouse = v);

            ImGui.SeparatorText(Localization.T("kf3.qol.additions"));
            ClassicNote(false);
            ImGui.BeginDisabled(s.ClassicMode != 0);
            Check("kf3.enh.gear", s.EquipmentStats, v => s.EquipmentStats = v);
            Check("kf3.enh.statprogress", s.StatProgress, v => s.StatProgress = v);
            Check("kf3.enh.keyhints", s.KeyHints, v => s.KeyHints = v);
            Check("kf3.enh.autokey", s.AutoKey, v => s.AutoKey = v);
            Check("kf3.enh.jump", s.Jump, v => s.Jump = v);
            Warning("kf3.enh.jump_warn");
            ImGui.BeginDisabled(!s.Jump);
            ImGui.Indent();
            Combo("kf3.enh.jump_height", s.JumpHeight, _jumpHeights, v => s.JumpHeight = v);
            ImGui.Unindent();
            ImGui.EndDisabled();
            Check("kf3.enh.damage", s.DamageNumbers, v => s.DamageNumbers = v);
            ImGui.BeginDisabled(!s.DamageNumbers);
            ImGui.Indent();
            Slider("kf3.enh.damage_scale", s.DamageScale, 30, 150, "%d%%", v => s.DamageScale = v);
            Combo("kf3.enh.damage_logpos", s.DamageLogPos, _damageLogPositions, v => s.DamageLogPos = v);
            Combo("kf3.enh.damage_barpos", s.DamageBarPos, _damageBarPositions, v => s.DamageBarPos = v);
            ImGui.Unindent();
            ImGui.EndDisabled();
            ImGui.EndDisabled();
            Done();
        }
    }

    // ---- Map --------------------------------------------------------------------------------

    sealed class MapSection : ISettingsSection
    {
        public string Id => "kf3.map";
        public string TitleKey => "kf3.map";
        public int Order => 14;

        public void Draw()
        {
            var s = Current;
            ClassicNote(false);
            ImGui.BeginDisabled(s.ClassicMode != 0);
            Check("kf3.enh.map", s.Automap, v => s.Automap = v);
            ImGui.BeginDisabled(!s.Automap);

            ImGui.SeparatorText(Localization.T("kf3.map.minimap"));
            Check("kf3.enh.minimap", s.Minimap, v => s.Minimap = v);
            ImGui.BeginDisabled(!s.Minimap);
            Combo("kf3.enh.mm_corner", s.MinimapCorner, _minimapCorners, v => s.MinimapCorner = v);
            Slider("kf3.enh.mm_size", s.MinimapSize, 10, 50, "%d%%", v => s.MinimapSize = v);
            Slider("kf3.enh.mm_zoom", s.MinimapZoom, 3, 20, "%d tiles", v => s.MinimapZoom = v);
            Slider("kf3.enh.mm_opacity", s.MinimapOpacity, 20, 100, "%d%%", v => s.MinimapOpacity = v);
            ImGui.EndDisabled();

            ImGui.SeparatorText(Localization.T("kf3.enh.cheats"));
            ImGui.TextDisabled(Localization.T("kf3.map.cheats_note"));
            Check("kf3.enh.map_reveal", s.MapRevealAll, v => s.MapRevealAll = v);
            Check("kf3.enh.cheat_items", s.CheatItems, v => s.CheatItems = v);
            Check("kf3.enh.cheat_contents", s.CheatContents, v => s.CheatContents = v);
            Check("kf3.enh.cheat_keys", s.CheatKeys, v => s.CheatKeys = v);
            Check("kf3.enh.cheat_enemies", s.CheatEnemies, v => s.CheatEnemies = v);
            Check("kf3.enh.cheat_traps", s.CheatTraps, v => s.CheatTraps = v);
            Check("kf3.enh.cheat_secrets", s.CheatSecrets, v => s.CheatSecrets = v);

            ImGui.EndDisabled();
            ImGui.EndDisabled();
            Done();
        }
    }

    // ---- the pages' text --------------------------------------------------------------------

    const string Strings = """
    {
      "strings": {
        "kf3.classic": { "en": "Classic mode" },
        "kf3.classic.intro": { "en": "Play King's Field III as it was on the PlayStation. Your choices on the other pages are kept, and come back when you switch back to Off." },
        "kf3.classic.m0": { "en": "Off" },
        "kf3.classic.m0_desc": { "en": "Everything as set on the Display, Quality of life and Map pages." },
        "kf3.classic.m1": { "en": "Full classic" },
        "kf3.classic.m1_desc": { "en": "The original picture (wobbling geometry, warping textures, dithered colour, 4:3, 15 fps) and none of the port's additions: no map, equipment stats, damage numbers, jump or unlocking with carried keys." },
        "kf3.classic.m2": { "en": "Classic + visuals" },
        "kf3.classic.m2_desc": { "en": "The game as it played on the console, with the enhanced picture: everything on the Display page stays as set, and none of the port's additions run (no map, equipment stats, damage numbers, jump or unlocking with carried keys)." },
        "kf3.classic.kept": { "en": "Never changed by Classic mode: key bindings, mouse look, the mouse in menus, directional sound, quick save and movie skip." },
        "kf3.classic.note_full": { "en": "Full classic is on (Settings > Classic mode): these are off for now." },
        "kf3.classic.note_visual": { "en": "Classic + visuals is on (Settings > Classic mode): these are off for now." },
        "kf3.disp.group": { "en": "Picture" },
        "kf3.aud.group": { "en": "Sound" },
        "kf3.qol": { "en": "Quality of life" },
        "kf3.qol.controls": { "en": "Controls" },
        "kf3.qol.additions": { "en": "Additions" },
        "kf3.map": { "en": "Map" },
        "kf3.map.minimap": { "en": "Minimap" },
        "kf3.map.cheats_note": { "en": "A guide on the map. Off by default." },
        "kf3.enh.stable": { "en": "Stable geometry (no wobble)" },
        "kf3.enh.stable_hint": { "en": "Draws corners at their exact positions instead of snapping them to the console's\n320x240 pixel grid, so walls and floors stop wobbling and crawling as you move." },
        "kf3.enh.straight": { "en": "Straight textures (no warping)" },
        "kf3.enh.straight_hint": { "en": "Perspective-correct textures: patterns on walls and floors stay straight\ninstead of bending across each polygon." },
        "kf3.enh.colour": { "en": "Smooth colour (no dither)" },
        "kf3.enh.colour_hint": { "en": "Full colour precision for shading and fog, without the console's checkerboard dither." },
        "kf3.enh.filter": { "en": "Texture filtering at a distance" },
        "kf3.enh.filter_hint": { "en": "Stops distant floors and walls shimmering and sparkling as you move.\nTextures up close keep their sharp pixels." },
        "kf3.enh.depth": { "en": "Correct overlaps (depth buffer)" },
        "kf3.enh.depth_hint": { "en": "Decides what is in front per pixel instead of per polygon, so doors, props and\nmonsters stop showing through walls they are behind at some angles." },
        "kf3.enh.fog": { "en": "Smooth fog" },
        "kf3.enh.fog_hint": { "en": "Fades into the fog by each pixel's distance instead of by the corners of each polygon,\nso the fog's edge stops following the floor and wall tiles." },
        "kf3.enh.ppl": { "en": "Per-pixel lighting" },
        "kf3.enh.ppl_hint": { "en": "Lights monsters, items and other models at every pixel from the game's own lights,\ninstead of blending the colours of their corners, and fades them into the fog smoothly." },
        "kf3.enh.movies": { "en": "Smooth movies (work in progress)" },
        "kf3.enh.movies_hint": { "en": "Scales the low-resolution movies up smoothly and softens their compression blocks and colour banding,\ninstead of showing them as big square pixels.\nWork in progress: the picture can jitter slightly, most visibly in the opening movies." },
        "kf3.enh.ao": { "en": "Ambient occlusion (soft corner shadows)" },
        "kf3.enh.ao_hint": { "en": "Shades corners, the foot of walls and where objects meet the floor,\nso rooms and buildings look solid. Needs a little GPU time." },
        "kf3.enh.ao_quality": { "en": "Ambient occlusion quality" },
        "kf3.enh.ao_quality_hint": { "en": "Low suits integrated graphics; High runs at the full render resolution." },
        "kf3.enh.aa": { "en": "Anti-aliasing (smooth edges)" },
        "kf3.enh.aa_hint": { "en": "Smooths the jagged stair-steps along the edges of walls, floors, monsters and items.\nTextures and the HUD stay sharp. 8x costs the most GPU time." },
        "kf3.enh.water": { "en": "Seamless water" },
        "kf3.enh.water_hint": { "en": "Lays the river's texture out across the world instead of turning it on every triangle,\nso the water has no blocky seams. At high frame rates it also flows smoothly." },
        "kf3.enh.reflect": { "en": "Water reflections" },
        "kf3.enh.reflect_hint": { "en": "Walls, bridges and banks reflect in the water. Only what is on screen can be reflected." },
        "kf3.enh.sound": { "en": "Directional sound" },
        "kf3.enh.sound_hint": { "en": "Keeps a sound where it comes from while it plays, as you turn and move.\nHeadphones also tells sounds behind you from sounds in front. Never changed by Classic mode." },
        "kf3.enh.gear": { "en": "Equipment stats" },
        "kf3.enh.gear_hint": { "en": "On the equipment lists, shows what the highlighted weapon or armour gives,\nand when you pick one, your totals now and with it on. Classic mode turns it off." },
        "kf3.enh.jump": { "en": "Jump (key on Settings > Input, R3 on a gamepad)" },
        "kf3.enh.jump_hint": { "en": "Jump while standing on the ground, using the game's own falling and landing.\nA ceiling stops you rising; a big drop still hurts as walking off it would. Classic mode turns it off." },
        "kf3.enh.jump_warn": { "en": "Use with care: jumping was never part of this game. It can reach places early, clear some walls and carry you off the edge of the world, where there is no floor and no way back. Quick save (F2) before a risky jump." },
        "kf3.enh.jump_height": { "en": "Jump height" },
        "kf3.enh.jump_height_hint": { "en": "All three land gently on flat ground (no fall damage)." },
        "kf3.enh.mouse_sens": { "en": "Mouse sensitivity" },
        "kf3.enh.mouse_sens_hint": { "en": "How far the view turns for a given mouse movement. 100% is 0.15 degrees per pixel." },
        "kf3.enh.mouse_smooth": { "en": "Mouse smoothing" },
        "kf3.enh.mouse_smooth_hint": { "en": "Evens out jerky mouse turning by spreading each movement over a few frames.\nHigher is smoother but feels slightly floatier. Off is raw input." },
        "kf3.enh.autokey": { "en": "Unlock with keys you carry" },
        "kf3.enh.statprogress": { "en": "Progress to next level and stat" },
        "kf3.enh.damage": { "en": "Damage numbers" },
        "kf3.enh.damage_scale": { "en": "Damage numbers size" },
        "kf3.enh.damage_logpos": { "en": "Damage log position" },
        "kf3.enh.damage_barpos": { "en": "Health bar position" },
        "kf3.enh.damage_hint": { "en": "Each hit on a creature, weapon or magic, shows bottom left with the HP it has left,\nfading after a few seconds (as in Shadow Tower)." },
        "kf3.enh.statprogress_hint": { "en": "Beside the status window: experience to the next level, and how close PWR\n(100 per point) and each magic element (400 per point) are to going up." },
        "kf3.enh.autokey_hint": { "en": "Interacting with a locked chest or door unlocks it when its key is in your bag,\nas using the key from the item menu would. Interact again to open it." },
        "kf3.enh.menumouse": { "en": "Mouse in menus and shops" },
        "kf3.enh.menumouse_hint": { "en": "Point at a row to pick it, left click to select, right click to go back, middle click for Guide, wheel to scroll.\nPressing a key hands the menu back to the keyboard until you move the mouse." },
        "kf3.enh.keyhints": { "en": "Key hints in menus" },
        "kf3.enh.keyhints_hint": { "en": "Under the menus' button hints, shows which key is each button (from your key bindings)." },
        "kf3.enh.map": { "en": "Automap (M for the full map)" },
        "kf3.enh.map_hint": { "en": "A map of each area that fills in as you explore. M opens the full map and pauses the game.\nClassic mode turns it off." },
        "kf3.enh.minimap": { "en": "Minimap in the corner" },
        "kf3.enh.minimap_hint": { "en": "The map round you, north up." },
        "kf3.enh.mm_corner": { "en": "Corner" },
        "kf3.enh.mm_size": { "en": "Size" },
        "kf3.enh.mm_size_hint": { "en": "The minimap's size as a share of the picture's height." },
        "kf3.enh.mm_zoom": { "en": "Zoom" },
        "kf3.enh.mm_zoom_hint": { "en": "How many tiles the minimap shows from its centre to its edge." },
        "kf3.enh.mm_opacity": { "en": "Opacity" },
        "kf3.enh.cheats": { "en": "Map cheats" },
        "kf3.enh.cheat_items": { "en": "Items on the ground" },
        "kf3.enh.cheat_items_hint": { "en": "Every item lying in the area, seen or not. Point at one on the full map for its name." },
        "kf3.enh.cheat_contents": { "en": "What chests, barrels, graves and monsters hold" },
        "kf3.enh.cheat_contents_hint": { "en": "A gold ring round any that still hold something, and round monsters that drop an item when killed\n(shown even with the enemies cheat off). Point at one on the full map to see what." },
        "kf3.enh.cheat_keys": { "en": "Which key opens a lock" },
        "kf3.enh.cheat_keys_hint": { "en": "Shown when pointing at a locked chest on the full map." },
        "kf3.enh.cheat_enemies": { "en": "Enemies" },
        "kf3.enh.cheat_traps": { "en": "Traps" },
        "kf3.enh.cheat_traps_hint": { "en": "Wall traps, and a warning badge on trapped chests (green: poison gas)." },
        "kf3.enh.cheat_secrets": { "en": "Secret walls" },
        "kf3.enh.map_reveal": { "en": "Show the whole map" },
        "kf3.enh.map_reveal_hint": { "en": "Draws every area in full, explored or not. Turning it off brings back what you have really explored." },
        "kf3.enh.aspect": { "en": "Aspect ratio" },
        "kf3.enh.aspect_hint": { "en": "Widescreen shows more of the world at the sides; nothing is stretched.\nThe HUD moves out to the edges." },
        "kf3.enh.fps": { "en": "Frame rate" },
        "kf3.enh.fps_hint": { "en": "How often the picture is drawn. The world still runs at the original speed;\nthe camera is smoothed between the game's 15 updates a second." }
      }
    }
    """;
}
