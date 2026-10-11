using System.Numerics;
using ImGuiNET;
using RecompOne.Runtime.Host.Window;
using RecompOne.Runtime.Memory;

namespace KfIII;

/// <summary>
/// Enhancements "Damage numbers", as Shadow Tower shows them: each hit on a creature is
/// listed (bottom left, or top left) with the amount, the creature and the HP it has
/// left, and fades after a few seconds; the creature last hit also gets a health bar
/// (top centre, or bottom centre).
///
/// <para>The game never reports a hit, so the creatures' HP is watched instead. On each
/// world tick (Automap's world-update hook calls <see cref="Tick"/>) every creature's HP
/// is compared with the last tick's, and a drop is a hit, whatever caused it (weapon,
/// magic, arrows). On tests/saves/slimes.kfstate a Green Slime goes 300, 236, 172: 64 a
/// swing. Tracking starts over when the area changes or a creature's slot changes type.</para>
///
/// <para>Entity record bytes used (GameAddr.EntityTable): +0x00 u8 1 and +0x09 u8 1 or 2
/// for a live creature, +0x01 its type, +0x02 its creature definition, +0x1A u16 its HP.
/// The definition (GameAddr.CreatureDefs) holds the full HP at +0x1A.</para>
/// </summary>
public static class DamageLog
{
    public static bool Enabled = true;
    /// <summary>Text size, 1 = full.</summary>
    public static float Scale = 0.7f;
    /// <summary>The hit list at the top left instead of the bottom left; the health bar at the bottom instead of the top.</summary>
    public static bool LogTop, BarBottom;

    const uint HpOffset = 0x1A, DefMaxHpOffset = 0x1A;
    static readonly bool _log = DevSwitch.Is("KFIII_DAMAGE_LOG");

    // Per entity slot: the HP and type last tick (-1 type: not a live creature).
    static readonly int[] _hp = new int[GameAddr.EntityCount];
    static readonly int[] _type = new int[GameAddr.EntityCount];
    static string? _area;

    record struct Hit(int Amount, string Who, int Left, long At);
    static readonly List<Hit> _hits = new();
    const long ShowMs = 2800, FadeMs = 600;
    const int MaxLines = 6;

    // The health bar's creature: the last one hit, for TargetMs after the hit.
    static int _target = -1, _targetHp, _targetMax;
    static long _targetAt;
    static string _targetName = "";
    const long TargetMs = 5000;

    public static void Install() => PanelManager.Register(Panel.Instance);

    /// <summary>From Automap's world-update hook, once a tick, with the area's overlay name.</summary>
    public static void Tick(IMemory m, string? area)
    {
        if (area != _area) { _area = area; Array.Fill(_type, -1); }
        for (int i = 0; i < GameAddr.EntityCount; i++)
        {
            uint rec = GameAddr.EntityTable + (uint)(i * GameAddr.EntityStride);
            bool live = m.ReadU8(rec) == 1 || m.ReadU8(rec + 9) is 1 or 2;
            int type = live ? m.ReadU8(rec + 1) : -1;
            int hp = m.ReadU16(rec + HpOffset);
            if (type != _type[i]) { _type[i] = type; _hp[i] = hp; continue; }
            if (type < 0) continue;
            // Drops of 10000 or more are not taken as hits.
            if (Enabled && hp < _hp[i] && _hp[i] - hp < 10000)
            {
                if (_log) Console.WriteLine($"[KFIII] damage {_hp[i] - hp} on #{i} type {type:X2} -> {hp}");
                _target = i; _targetAt = Environment.TickCount64;
                _targetName = Automap.CreatureName(type, false);
                uint def = GameAddr.CreatureDefs + (uint)m.ReadU8(rec + 2) * 0x78;
                _targetMax = Math.Max(m.ReadU16(def + DefMaxHpOffset), _hp[i]);
                lock (_hits)
                {
                    _hits.Add(new Hit(_hp[i] - hp, Automap.CreatureName(type, false), hp, Environment.TickCount64));
                    if (_hits.Count > MaxLines) _hits.RemoveAt(0);
                }
            }
            _hp[i] = hp;
        }
        if (_target >= 0)
        {
            _targetHp = _type[_target] >= 0 ? _hp[_target] : 0;
            if (_type[_target] < 0 || Environment.TickCount64 - _targetAt > TargetMs) _target = -1;
        }
    }

    static uint Col(int r, int g, int b, float a) => ScreenOverlay.Rgba(r, g, b, a);

    /// <summary>The last-hit creature's name, health bar and HP, fading out at the end.</summary>
    static void TargetBar(Vector2 p0, Vector2 p1, long now)
    {
        float sy = (p1.Y - p0.Y) / 240f * Scale;
        float w = 110f * sy, h = Math.Max(4f, 4f * sy), ts = Math.Max(11f, 7.5f * sy);
        long age = now - _targetAt;
        float a = age < TargetMs - FadeMs ? 1f : Math.Max(0f, (TargetMs - age) / (float)FadeMs);
        var dl = ImGui.GetForegroundDrawList();
        var font = ImGui.GetFont();
        float cx = (p0.X + p1.X) * 0.5f;
        float fullH = ts * 1.15f + h + 3 + ts;   // name, bar, numbers
        float y = BarBottom ? p1.Y - 16f * (p1.Y - p0.Y) / 240f - fullH : p0.Y + 14f * (p1.Y - p0.Y) / 240f;
        var ext = font.CalcTextSizeA(ts, float.MaxValue, 0f, _targetName);
        dl.AddText(font, ts, new Vector2(cx - ext.X / 2 + 2, y + 2), Col(0, 0, 0, a * 0.85f), _targetName);
        dl.AddText(font, ts, new Vector2(cx - ext.X / 2, y), Col(235, 230, 215, a), _targetName);
        y += ts * 1.15f;
        float f = Math.Clamp(_targetHp / (float)_targetMax, 0f, 1f);
        var b0 = new Vector2(cx - w / 2, y); var b1 = new Vector2(cx + w / 2, y + h);
        dl.AddRectFilled(b0 - new Vector2(1, 1), b1 + new Vector2(1, 1), Col(0, 0, 0, a * 0.8f), 2f);
        dl.AddRectFilled(b0, b1, Col(60, 20, 20, a * 0.9f), 2f);
        if (f > 0) dl.AddRectFilled(b0, new Vector2(b0.X + w * f, b1.Y), Col(200, 40, 35, a), 2f);
        string hpText = $"{_targetHp} / {_targetMax}";
        var e2 = font.CalcTextSizeA(ts * 0.85f, float.MaxValue, 0f, hpText);
        dl.AddText(font, ts * 0.85f, new Vector2(cx - e2.X / 2 + 2, b1.Y + 3 + 2), Col(0, 0, 0, a * 0.85f), hpText);
        dl.AddText(font, ts * 0.85f, new Vector2(cx - e2.X / 2, b1.Y + 3), Col(220, 215, 200, a), hpText);
    }

    sealed class Panel : IFloatingPanel
    {
        public static readonly Panel Instance = new();
        public string Name => "kf3damagelog";
        public bool IsOpen
        {
            get { lock (_hits) return Enabled && (_hits.Count > 0 || _target >= 0) && !AutomapView.FullOpen; }
            set { }
        }

        public void Draw()
        {
            if (!OutputView.Valid) return;
            long now = Environment.TickCount64;
            Hit[] hits;
            lock (_hits) { _hits.RemoveAll(h => now - h.At > ShowMs); hits = _hits.ToArray(); }
            var p0 = OutputView.Min; var p1 = OutputView.Max;
            if (_target >= 0 && _targetMax > 0) TargetBar(p0, p1, now);
            if (hits.Length == 0) return;
            float sy0 = (p1.Y - p0.Y) / 240f, sy = sy0 * Scale;
            float ts = Math.Max(11f, 8.5f * sy), line = ts * 1.15f;
            var font = ImGui.GetFont();
            var dl = ImGui.GetForegroundDrawList();
            // Newest at the bottom. At the bottom left the list sits just above the
            // screen's edge; at the top left it starts under the game's HP/MP box
            // (which reaches about y 76 of 240).
            float x = p0.X + 12f * sy0, y = LogTop ? p0.Y + 80f * sy0 : p1.Y - 14f * sy0 - line * hits.Length;
            foreach (var h in hits)
            {
                long age = now - h.At;
                float a = age < ShowMs - FadeMs ? 1f : Math.Max(0f, (ShowMs - age) / (float)FadeMs);
                // The amount right-aligned in a four-digit column, then who and what is left.
                string num = h.Amount.ToString();
                var numW = font.CalcTextSizeA(ts * 1.15f, float.MaxValue, 0f, "0000").X;
                var nw = font.CalcTextSizeA(ts * 1.15f, float.MaxValue, 0f, num).X;
                var at = new Vector2(x + numW - nw, y);
                dl.AddText(font, ts * 1.15f, at + new Vector2(2, 2), Col(0, 0, 0, a * 0.85f), num);
                dl.AddText(font, ts * 1.15f, at, Col(255, 205, 90, a), num);
                string rest = h.Left > 0 ? $"{h.Who}  ({h.Left} left)" : $"{h.Who}  (defeated)";
                var rat = new Vector2(x + numW + ts * 0.6f, y + ts * 0.12f);
                dl.AddText(font, ts, rat + new Vector2(2, 2), Col(0, 0, 0, a * 0.85f), rest);
                dl.AddText(font, ts, rat, Col(230, 225, 210, a), rest);
                y += line;
            }
        }
    }
}
