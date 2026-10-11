using System.Numerics;
using ImGuiNET;
using RecompOne.Runtime.Host.Window;
using RecompOne.Runtime.Memory;

namespace KfIII;

/// <summary>
/// Enhancements "Progress to the next level": an overlay beside the main menu's status
/// window showing how far each growing stat is from its next point, then extended stats
/// (gauge refill times, regeneration) and, in a box below, ailment lengths and block
/// chances with the gear worn. Each line explains itself when clicked.
///
/// <para>The game keeps all of it. Experience (GameAddr.Exp) counts toward the total
/// for the next level (GameAddr.NextLevelExp), which kf_GainExp sets at each level-up
/// from GameAddr.LevelTable. PWR training (GameAddr.PowerPractice) counts weapon use
/// (kf_PowerUse); at 100 it resets and base PWR gains a point. Each element has its own
/// counter (GameAddr.MagicPractice, fire earth water wind holy): kf_MagicUse adds the
/// spell's practice value to every element the spell has, and at 400 the counter resets
/// and that base magic power gains a point.</para>
///
/// <para>Read on the game thread when kf_MenuButtons draws page 0, the main menu with
/// the status window (MenuMouse.BeforeButtons calls <see cref="Capture"/>), and drawn
/// over the frame's right margin. Test save: tests/saves/stats_progress.kfstate
/// (level 20 with 336 to go; PWR 13/100; fire 180, wind 176 and holy 10 of 400).</para>
/// </summary>
public static class StatProgress
{
    public static bool Enabled = true;

    const int PowerStep = 100, MagicStep = 400, StatMax = 999, MaxExp = 999999;
    // The Excellector grows a stage after this much experience from when it was got.
    const int ExcGrowth = 13000;
    // Level table records: 12 bytes, +8 the experience level L + 2 needs, so level L's
    // range runs from record L - 2's to record L - 1's.
    const uint LevelRecord = 12;

    struct Snapshot
    {
        public int Exp, From, To, Level;                   // experience, this level's start, the next level's
        public int Pwr, PwrBase;                           // PWR training count, base PWR
        public int[] Magic, MagicBase;                     // the same per element (fire earth water wind holy)
        public int PwrTotal;                               // PWR with equipment (GameAddr.Pwr)
        public float Swing, PerKg, CastFast, CastSel;      // seconds; -1 none
        public string? FastName, SelName;                  // the quickest learned spell, the selected one
        // Excellector growth: the stage held (1 or 2; 0 none, or the third and last form,
        // which has nothing left to grow into) and the experience it counts from.
        public int ExcStage, ExcBase;
        public float HpRate, MpRate, EarthRate;            // per second; EarthRate the Earth Shield's, -1 none
        public bool OnEarth;                               // standing where the Earth Shield works
        public GearEffects.Totals Gear;                    // ailment lengths and block chances with what is worn
    }
    static Snapshot _s = new() { Magic = new int[5], MagicBase = new int[5] };
    static long _at;

    /// <summary>From MenuMouse.BeforeButtons on page 0.</summary>
    public static void Capture(IMemory m)
    {
        if (!Enabled) return;
        var s = new Snapshot { Magic = new int[5], MagicBase = new int[5] };
        s.Exp = (int)m.ReadU32(GameAddr.Exp);
        s.To = (int)m.ReadU32(GameAddr.NextLevelExp);
        s.Level = m.ReadU8(GameAddr.Level);
        s.From = s.Level >= 2 && s.Level <= 99 ? (int)m.ReadU32(GameAddr.LevelTable + (uint)(s.Level - 2) * LevelRecord + 8) : 0;
        if (s.From > s.Exp) s.From = 0;
        s.Pwr = m.ReadU16(GameAddr.PowerPractice);
        s.PwrBase = m.ReadU16(GameAddr.BaseStats);
        for (int n = 0; n < 5; n++)
        {
            s.Magic[n] = m.ReadU16(GameAddr.MagicPractice + (uint)n * 2);
            s.MagicBase[n] = m.ReadU16(GameAddr.BaseStats + 2 + (uint)n * 2);
        }
        Extended(m, ref s);
        Regen(m, ref s);
        // The Excellector (kf_ExcellectorGrow, from kf_GainExp): while stage 1 (item 0) or
        // stage 2 (item 1) is in the bag or storage, GameAddr.ExcellectorExp holds the
        // experience the player had when it was got; past that + 13000 it grows a stage.
        bool Owns(uint id) => m.ReadU8(GameAddr.BagCounts + id) != 0 || m.ReadU8(GameAddr.StorageCounts + id) != 0;
        s.ExcStage = Owns(1) ? 2 : Owns(0) ? 1 : 0;
        s.ExcBase = (int)m.ReadU32(GameAddr.ExcellectorExp);
        _s = s;
        _at = Environment.TickCount64;
    }

    // ---- extended stats: recharge times -------------------------------------------
    //
    // Both gauges (u16 0x801B2502 weapon, 0x801B2506 magic) fill to 5000, one step a
    // world tick (15 a second, measured at 66 ms). The step is kf_ChargeRate(stat, w) =
    // stat * 64 / (w + 1) + 1:
    // - weapon (func_8002D2A0): 2 x kf_ChargeRate(the PWR total, the weapon's weight in
    //   0.1 kg, weapon record +1). Gloves change the weight: 0x42 w -= w / 4, 0x44
    //   w += w / 2, 0x46 w /= 2. The gauge pauses while GameAddr.RunningFlag counts
    //   (just swung, or running).
    // - magic (kf_Actions): kf_ChargeRate(the average of the five magic totals, the
    //   spell's weight, spell record +1); Orladin's Hood (head 0x25) w -= w / 4,
    //   Groundal Mail (body 0x30) w *= 2.
    // Spell records (GameAddr.SpellRecords): +0 learned, +1 weight, +0x16 MP cost. On
    // stats_progress.kfstate the steps come to 92 and 85 a tick, as measured.

    const int GaugeFull = 5000, SpellCount = 31;
    const uint SpellStride = 24;
    // Spell names follow the item names, from this id.
    const int FirstSpellItem = 0x96;

    static int ChargeRate(int stat, int w) => Math.Min(GaugeFull, stat * 64 / (w + 1) + 1);

    /// <summary>Seconds to fill a gauge at this step a tick, or -1.</summary>
    static float Seconds(int perTick) =>
        perTick <= 0 ? -1 : (float)Math.Ceiling(GaugeFull / (double)perTick) / GearEffects.TicksPerSecond;

    static void Extended(IMemory m, ref Snapshot s)
    {
        s.PwrTotal = m.ReadU16(GameAddr.Pwr);
        int wid = m.ReadU8(EquipSlot.Weapon);
        s.Swing = s.PerKg = s.CastFast = s.CastSel = -1;
        if (wid < 0x22)
        {
            int w = m.ReadU8(GameAddr.WeaponRecords + (uint)wid * 0x44 + 1);
            int gloves = m.ReadU8(EquipSlot.Arms);
            if (gloves == 0x42) w -= w >> 2;
            if (gloves == 0x44) w += w >> 1;
            if (gloves == 0x46) w >>= 1;
            s.Swing = Seconds(2 * ChargeRate(s.PwrTotal, w));
            // A kilogram more (10 units of weight), with these gloves' factor.
            float k = gloves switch { 0x42 => 0.75f, 0x44 => 1.5f, 0x46 => 0.5f, _ => 1f };
            s.PerKg = Seconds(2 * ChargeRate(s.PwrTotal, w + (int)(10 * k))) - s.Swing;
        }
        int avg = 0;
        for (int n = 0; n < 5; n++) avg += m.ReadU16(GameAddr.Pwr + 2 + (uint)n * 2);
        avg /= 5;
        int head = m.ReadU8(EquipSlot.Head), body = m.ReadU8(EquipSlot.Body);
        float Cast(int w)
        {
            if (head == 0x25) w -= w >> 2;   // Orladin's Hood
            if (body == 0x30) w <<= 1;       // Groundal Mail
            return Seconds(ChargeRate(avg, w));
        }
        uint sel = m.ReadU32(GameAddr.SelectedSpell);
        for (int i = 0; i < SpellCount; i++)
        {
            uint rec = GameAddr.SpellRecords + (uint)i * SpellStride;
            if (m.ReadU8(rec) == 0) continue;
            float t = Cast(m.ReadU8(rec + 1));
            if (s.CastFast < 0 || t < s.CastFast) { s.CastFast = t; s.FastName = Automap.ItemName(m, FirstSpellItem + i); }
            if (rec == sel) { s.CastSel = t; s.SelName = Automap.ItemName(m, FirstSpellItem + i); }
        }
    }

    // ---- regeneration (kf_WorldUpdate, from 0x80032180) ------------------------------
    //
    // On each tick n (u32 0x801B2580), every equipped piece whose period divides n gives
    // or takes one point (periods in GearEffects). The game reads the records through
    // GameAddr.WeaponRecordPtr and GameAddr.ArmourRecordPtrs (func_80030CBC and
    // func_80030D68). The Earth Shield (shield 0x35) gives 1 MP every 900 ticks while
    // GameAddr.EarthGround is set: the area's zone props (+0x33, entered through
    // kf_PlayerInTileRect) mark the ground it works on.

    static void Regen(IMemory m, ref Snapshot s)
    {
        float hp = 0, mp = 0;
        float Rate(uint rec, uint off) { int p = m.ReadU16(rec + off); return p == 0 ? 0f : GearEffects.TicksPerSecond / p; }
        static bool InRam(uint p) => p >= 0x80000000u && p < 0x80200000u;
        uint w = m.ReadU32(GameAddr.WeaponRecordPtr);
        if (InRam(w)) { hp += Rate(w, 0x16); mp += Rate(w, 0x18); }
        for (uint k = 0; k < 7; k++)
        {
            if (m.ReadU8(GameAddr.ArmourSlots + k) == 0xFF) continue;
            uint r = m.ReadU32(GameAddr.ArmourRecordPtrs + k * 4);
            if (!InRam(r)) continue;
            hp += Rate(r, 0x14) - Rate(r, 0x16);
            mp += Rate(r, 0x18) - Rate(r, 0x1A);
        }
        s.HpRate = hp; s.MpRate = mp;
        s.EarthRate = m.ReadU8(EquipSlot.Shield) == 0x35 ? GearEffects.TicksPerSecond / 900f : -1f;
        s.OnEarth = m.ReadU8(GameAddr.EarthGround) != 0;
        s.Gear = GearEffects.Worn(m);
    }

    // ---- the panel ---------------------------------------------------------------

    public static void Install() => PanelManager.Register(Panel.Instance);

    static string? _tip;
    static Vector2 _tipAt;

    // What each line means, shown when it is clicked.
    static readonly Dictionary<string, string> _tips = new()
    {
        ["LEVEL"] = "EXP still needed for the next level. The bar is your progress from this level's start.",
        ["PWR"] = "Training toward the next point of PWR. Every weapon swing adds 1; at 100 your PWR goes up by 1.",
        ["SWING"] = "How long the weapon gauge takes to refill after a swing. Faster with more PWR, slower with a heavier weapon (some gloves change how heavy it counts).",
        ["PER KG"] = "How much longer the swing refill gets for each extra kilogram of weapon.",
        ["CAST"] = "How long the magic gauge takes to refill after your quickest learned spell. Faster with higher average magic power, slower for heavier spells.",
        ["HP/S"] = "HP restored per second by your equipment (some pieces drain instead). In brackets: seconds per point.",
        ["MP/S"] = "MP restored per second by your equipment (some pieces drain instead). In brackets: seconds per point.",
        ["EARTH SHIELD"] = "The Earth Shield restores 1 MP every 60 seconds while you stand on bare ground (it is working here).",
        ["EARTH (OFF)"] = "The Earth Shield restores 1 MP every 60 seconds, but only on bare ground, which is not where you are standing now.",
        ["POISON"] = "How long poison lasts with your gear (normally 60s), and the chance a poisonous hit actually poisons you: 99% minus your poison defense.",
        ["PARALYSIS"] = "How long paralysis lasts with your gear (normally 13s). Silver Arms, Groundal Boots, Gold Ring and Ichrius' Boots shorten it.",
        ["SLOW"] = "How long slowness lasts with your gear (normally 30s). Groundal Boots, Globe Amulet, Luster Wing and Ichrius' Helm shorten it.",
        ["CURSE"] = "How long a curse lasts with your gear (normally 90s). Groundal Boots, Globe Amulet, Luster Wing and Ichrius' Helm shorten it.",
        ["DARKNESS"] = "How long darkness lasts with your gear (normally 180s). Groundal Boots, Globe Amulet, Luster Wing and Ichrius' Helm shorten it.",
        ["BLOCK"] = "The chance a hit does no damage of that kind at all (an ailment it carries can still land). Physical: Dragon Head, Groundal Mail and Ichrius' Armor 25% each, the full Promeus set always. Magic: Groundal and Ichrius' shields 25%, Ebony Bracelet 50%. Fire: Fire Mail always.",
    };

    // In the status window's order (holy first, as its icons), with each element's
    // index in the counters and its colour.
    static readonly (int N, string Name, uint Col)[] _elements =
    {
        (4, "HOLY", ScreenOverlay.Rgba(235, 230, 170)), (0, "FIRE", ScreenOverlay.Rgba(240, 120, 40)),
        (1, "EARTH", ScreenOverlay.Rgba(170, 120, 70)), (3, "WIND", ScreenOverlay.Rgba(90, 190, 90)),
        (2, "WATER", ScreenOverlay.Rgba(70, 140, 235)),
    };

    static uint Col(int r, int g, int b, float a = 1f) => ScreenOverlay.Rgba(r, g, b, a);

    sealed class Panel : IFloatingPanel
    {
        public static readonly Panel Instance = new();
        public string Name => "kf3statprogress";
        public bool IsOpen
        {
            get => Enabled && MenuMouse.InMenu && MenuMouse.ButtonsPage == 0
                   && Environment.TickCount64 - MenuMouse.ButtonsAt < 250 && Environment.TickCount64 - _at < 250
                   && !AutomapView.FullOpen;
            set { }
        }

        public void Draw()
        {
            if (!OutputView.Valid) return;
            var s = _s;
            var view = GameView.Current;
            float sx = view.Sx, sy = view.Sy;
            int margin = view.Margin;

            // Right of the status window (which ends at game x 296), its top level with it (y 26).
            float left = 300, right = 320 + margin - 4;
            if (right - left < 60) { left = 320 + margin - 64; right = 320 + margin - 4; }
            var a = view.At(left, 26); var b = view.At(right, 186);
            var dl = ImGui.GetForegroundDrawList();
            // Contents go on channel 1 first; the box, sized to them, then goes beneath on channel 0.
            dl.ChannelsSplit(2);
            dl.ChannelsSetCurrent(1);
            var font = ImGui.GetFont();
            float ts = Math.Max(12f, 6.6f * sy), line = ts * 1.02f, bar = Math.Max(3f, 1.9f * sy), gap = ts * 0.4f;
            float pad = 4f * sx;

            float x = a.X + pad, w = b.X - a.X - 2 * pad, y = a.Y + pad;
            uint text = ScreenOverlay.TextColour, dim = ScreenOverlay.DimColour;
            var hits = new List<(Vector2 A, Vector2 B, string Tip)>();
            void Hit(string label, float h, string? tip)
            {
                tip ??= _tips.TryGetValue(label.Trim(), out var t) ? t : null;
                if (tip != null) hits.Add((new Vector2(x - pad * 0.5f, y), new Vector2(x + w + pad * 0.5f, y + h), tip));
            }
            // A label and value with a progress bar under them.
            void Row(string label, string value, float frac, uint col, bool max = false, string? tip = null)
            {
                Hit(label, line + bar + gap, tip);
                dl.AddText(font, ts, new Vector2(x, y), text, label);
                var ext = font.CalcTextSizeA(ts, float.MaxValue, 0f, value);
                dl.AddText(font, ts, new Vector2(x + w - ext.X, y), max ? dim : text, value);
                y += line;
                dl.AddRectFilled(new Vector2(x, y), new Vector2(x + w, y + bar), Col(60, 60, 64, 0.9f), bar / 2);
                if (frac > 0) dl.AddRectFilled(new Vector2(x, y), new Vector2(x + w * Math.Clamp(frac, 0f, 1f), y + bar), col, bar / 2);
                y += bar + gap;
            }

            dl.AddText(font, ts * 0.9f, new Vector2(x, y), dim, "TO NEXT");
            y += line * 1.05f;
            if (s.Exp >= MaxExp || s.To <= s.Exp) Row("LEVEL", "MAX", 1f, Col(220, 200, 120), true);
            else Row("LEVEL", $"{s.To - s.Exp}", s.To > s.From ? (s.Exp - s.From) / (float)(s.To - s.From) : 0f, Col(220, 200, 120));
            if (s.PwrBase >= StatMax) Row("PWR", "MAX", 1f, Col(210, 90, 80), true);
            else Row("PWR", $"{s.Pwr}/{PowerStep}", s.Pwr / (float)PowerStep, Col(210, 90, 80));
            foreach (var (n, name, col) in _elements)
            {
                string el = name.ToLowerInvariant();
                string et = $"Training toward the next point of {el} magic. Each cast of a spell with {el} in it adds that spell's practice value; at {MagicStep} your {el} power goes up by 1.";
                if (s.MagicBase[n] >= StatMax) Row(name, "MAX", 1f, col, true, et);
                else Row(name, $"{s.Magic[n]}/{MagicStep}", s.Magic[n] / (float)MagicStep, col, tip: et);
            }

            // Right after a growth the game sets the baseline to -1, and the next experience
            // gain sets it to the experience then; until that happens, count from now.
            if (s.ExcStage > 0 && s.ExcBase < 0) s.ExcBase = s.Exp;
            if (s.ExcStage > 0)
            {
                int toGrow = s.ExcBase + ExcGrowth + 1 - s.Exp;
                const string ExcTip = "EXP still to earn before the Excellector grows into its next form (13,000 from when you got this one). Its third form is the last.";
                Row($"EXCELLECTOR {s.ExcStage}", toGrow > 0 ? $"{toGrow}" : "NEXT EXP", Math.Clamp((s.Exp - s.ExcBase) / (float)ExcGrowth, 0f, 1f), Col(235, 235, 255), tip: ExcTip);
            }

            y += gap * 0.5f;
            dl.AddText(font, ts * 0.9f, new Vector2(x, y), dim, "EXTENDED STATS");
            y += line * 1.05f;
            // A label and value on one line. A rate too long to sit beside its label
            // ("+0.017 (1/60.0s)") keeps only its period ("1/60.0s").
            void Line(string label, string value, uint col, string? tip = null)
            {
                Hit(label, line, tip);
                dl.AddText(font, ts, new Vector2(x, y), col, label);
                var ext = font.CalcTextSizeA(ts, float.MaxValue, 0f, value);
                float labelW = font.CalcTextSizeA(ts, float.MaxValue, 0f, label).X;
                int open = value.IndexOf(" (", StringComparison.Ordinal);
                if (labelW + ext.X + ts * 0.5f > w && open > 0 && value.EndsWith(')'))
                {
                    value = value[(open + 2)..^1];
                    ext = font.CalcTextSizeA(ts, float.MaxValue, 0f, value);
                }
                dl.AddText(font, ts, new Vector2(x + w - ext.X, y), col, value);
                y += line;
            }
            if (s.Swing >= 0)
            {
                Line("SWING", $"{s.Swing:F1}s", text);
                Line(" PER KG", $"+{s.PerKg:F2}s", dim);
            }
            if (s.CastFast >= 0) Line("CAST", $"{s.CastFast:F1}s", text);
            static string Per(float r) => r == 0 ? "0" : (r > 0 ? "+" : "") + (Math.Abs(r) >= 0.1f ? r.ToString("F2") : r.ToString("F3"));
            const string SelTip = "The magic gauge's refill time for the spell you have selected (FASTEST: it is your quickest).";
            if (s.CastSel >= 0 && s.SelName != s.FastName) Line(" " + (s.SelName ?? "SPELL").ToUpperInvariant(), $"{s.CastSel:F1}s", dim, SelTip);
            else if (s.CastSel >= 0) Line(" " + (s.SelName ?? "SPELL").ToUpperInvariant(), "FASTEST", dim, SelTip);
            // Below one a second, also how long one point takes ("1/4.3s"), which reads more easily.
            static string Rate(float r) => r == 0 || Math.Abs(r) >= 1f ? Per(r)
                : $"{Per(r)} ({(r < 0 ? "-" : "")}1/{1f / Math.Abs(r):F1}s)";
            Line("HP/S", Rate(s.HpRate), text);
            Line("MP/S", Rate(s.MpRate), text);
            if (s.EarthRate >= 0) Line(s.OnEarth ? " EARTH SHIELD" : " EARTH (OFF)", Rate(s.EarthRate), s.OnEarth ? text : dim);

            var bb = new Vector2(b.X, Math.Max(b.Y, y + pad));
            dl.ChannelsSetCurrent(0);
            ScreenOverlay.Box(dl, a, bb, 0.78f, sy);
            dl.ChannelsMerge();

            // Ailments: how long each lasts with what is worn (GearEffects.Worn), in white
            // where the gear shortens it, and the hidden chances to shrug off a hit. A box
            // of its own under the one above (the bottom middle has the button prompts).
            var g = s.Gear;
            a = new Vector2(a.X, bb.Y + 3 * sy); b = new Vector2(bb.X, a.Y);
            x = a.X + pad; w = b.X - a.X - 2 * pad; y = a.Y + pad;
            dl.ChannelsSplit(2);
            dl.ChannelsSetCurrent(1);
            ts *= 0.9f; line = ts * 1.02f;   // smaller, so the box stays clear of the screen's bottom
            dl.AddText(font, ts * 0.9f, new Vector2(x, y), dim, "AILMENTS (WITH GEAR)");
            y += line * 1.05f;
            static string Sec(float v) => v >= 10 ? $"{v:F0}s" : $"{v:F1}s";
            // Dim where the length is the game's normal one.
            Line("POISON", $"{Sec(g.Poison)}, {g.PoisonChance}% CHANCE", g.Poison < 60 || g.PoisonChance < 99 ? text : dim);
            Line("PARALYSIS", Sec(g.Paralysis), g.Paralysis < 13.3f ? text : dim);
            Line("SLOW", Sec(g.Slow), g.Slow < 30 ? text : dim);
            Line("CURSE", Sec(g.Curse), g.Curse < 90 ? text : dim);
            Line("DARKNESS", Sec(g.Dark), g.Dark < 180 ? text : dim);
            var blocks = new List<string>();
            if (g.BlockPhysical > 0) blocks.Add(g.BlockPhysical >= 1 ? "PHYS ALWAYS" : $"PHYS {g.BlockPhysical * 100:F0}%");
            if (g.BlockMagic > 0) blocks.Add($"MAGIC {g.BlockMagic * 100:F0}%");
            if (g.FireImmune) blocks.Add("FIRE ALWAYS");
            Line("BLOCK", blocks.Count > 0 ? string.Join("  ", blocks) : "NONE", blocks.Count > 0 ? text : dim);
            var bb2 = new Vector2(b.X, Math.Max(b.Y, y + pad));
            dl.ChannelsSetCurrent(0);
            ScreenOverlay.Box(dl, a, bb2, 0.78f, sy);
            dl.ChannelsMerge();

            // Clicking a line shows what it means; clicking it again or anywhere else closes
            // it. While the pointer is over the boxes, the left button is not the game's Cross.
            var mp = ImGui.GetIO().MousePos;
            if (mp.X >= view.At(left, 0).X && mp.X <= view.At(right, 0).X && mp.Y >= view.At(0, 26).Y && mp.Y <= bb2.Y)
                MenuMouse.OverOverlayAt = Environment.TickCount64;
            bool In((Vector2 A, Vector2 B, string Tip) h) => mp.X >= h.A.X && mp.X <= h.B.X && mp.Y >= h.A.Y && mp.Y < h.B.Y;
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                string? hit = null;
                foreach (var h in hits) if (In(h)) { hit = h.Tip; break; }
                _tip = hit == null || hit == _tip ? null : hit;
                _tipAt = mp;
            }
            foreach (var h in hits)
                if (In(h)) dl.AddRectFilled(h.A, h.B, Col(255, 255, 255, 0.07f), 2f);
            if (_tip != null)
            {
                float tts = Math.Max(12f, 6.0f * sy), tw = tts * 22f;
                var size = font.CalcTextSizeA(tts, float.MaxValue, tw, _tip);
                // Left of the panels, level with the click, kept on screen.
                var ta = new Vector2(view.At(left, 0).X - size.X - 4 * pad, Math.Clamp(_tipAt.Y - size.Y / 2, view.Min.Y + 2, view.Max.Y - size.Y - 3 * pad));
                var tb = ta + size + new Vector2(2 * pad, 2 * pad);
                dl.AddRectFilled(ta, tb, Col(28, 30, 38, 0.96f), 4f);
                dl.AddRect(ta, tb, Col(200, 190, 150, 0.95f), 4f, ImDrawFlags.None, Math.Max(1f, sy * 0.5f));
                dl.AddText(font, tts, ta + new Vector2(pad, pad), Col(235, 232, 220), _tip, tw);
            }
        }
    }
}
