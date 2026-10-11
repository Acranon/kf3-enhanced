using ImGuiNET;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Host.Window;
using RecompOne.Runtime.Memory;
using Game = Recompiled.KingsFieldIII_game;

namespace KfIII;

/// <summary>
/// Debug > Teleport to area: loads any of the 28 areas and puts the player on open floor.
///
/// It asks for the area the way an area exit (prop class 0xE0, type 0x118) does:
/// kf_ChangeArea with five bytes -- the area's map, props and module (all n for area n)
/// and two bank numbers -- then the arrival shift (signed tiles, 0x7F keeps the position;
/// applied inside kf_AreaChangeStep, at 0x800184F0). The banks are the area's sound bank and music (see
/// _banks), so the area plays its own.
///
/// The position is kept on the way in. Then, on the first tick the new module is active,
/// the player is put on the nearest sample where their whole body fits on level floor
/// (Automap.CellAt: no liquid, chasm or wall within 3 samples), at that floor's height.
/// Both steps run from Automap's world-update hook.
/// </summary>
public static class Teleport
{
    public const int Areas = 28;

    /// <summary>
    /// (sound bank, music) per area: kf_AreaChangeStep loads VAB file bank + 1
    /// (0x80018B78) and the music (0x80018BE0) from them. Taken from the exits (type
    /// 0x118), the warp props (+0x38 area, +0x39/+0x3A banks, +0x3B/+0x3C arrival tile;
    /// types 0x12C, 0x12D, 0x134, 0x139, 0x141, 0x144, giving 16 from 7 and 17, 17 from
    /// 16, 19 from 8 and 20, 20 from 19, 25 from 24) and GAME.EXE's warp table at
    /// 0x800130F0 (area, banks; 27 is there). 11 and 22 are only entered from areas that
    /// share their banks (their exits say 0xFF). Null: not found (area 21); 0xFF is then
    /// passed, which keeps the current sound and music.
    /// </summary>
    static readonly (int Sound, int Music)?[] _banks =
    {
        (0, 0), (4, 5), (2, 2), (16, 18), (13, 15), (4, 5), (17, 19), (7, 9),
        (5, 7), (5, 7), (6, 8), (6, 8), (4, 6), (1, 1), (2, 3), (3, 4),
        (11, 13), (15, 17), (14, 16), (12, 14), (12, 14), null, (8, 10), (8, 10),
        (9, 11), (10, 12), (4, 6), (9, 11),
    };

    /// <summary>
    /// Where the game itself puts you in an area, in tiles: the fountains of GAME.EXE's
    /// warp table (0x800130F0, used with Ichrius' Key), else a warp glyph's arrival tile.
    /// The search for open floor starts there; for the other areas, from the position
    /// kept on the way in, which can be any room (area 16's "Shudom's Room" was found
    /// that way).
    /// </summary>
    static readonly Dictionary<int, (int X, int Z)> _starts = new()
    {
        [0] = (10, 56),
        [1] = (32, 14),
        [2] = (57, 36),
        [3] = (7, 31),
        [4] = (56, 13),
        [5] = (29, 31),
        [6] = (34, 67),
        [7] = (19, 29),
        [9] = (39, 56),
        [10] = (37, 51),
        [11] = (58, 34),
        [24] = (7, 70),
        [27] = (40, 35),
        [16] = (2, 2),
        [17] = (38, 35),
        [19] = (15, 1),
        [20] = (15, 57),
        [25] = (40, 35),
    };

    /// <summary>KFIII_TEST_TELEPORT=secs,area: one teleport that long after start.</summary>
    static readonly int[]? _test = DevSwitch.Text("KFIII_TEST_TELEPORT")?.Split(',').Select(int.Parse).ToArray();
    static readonly long _start = Environment.TickCount64;
    static bool _testDone;
    /// <summary>The area picked in the menu (UI thread), taken by the next tick; -1 none.</summary>
    static volatile int _request = -1;
    /// <summary>The area being travelled to, -1 none, and the ticks spent waiting for it.</summary>
    static int _target = -1, _wait;

    public static void Install()
    {
        // This runs before the runtime adds its own menus, so Debug is placed after Mods
        // explicitly.
        MenuRegistry.Menu("menu.debug").After("menu.mods").Separator().Custom(DrawMenu);
    }

    static string Fdat(int n) => $"fdat{2 + 3 * n:D2}";

    static void DrawMenu()
    {
        if (!ImGui.BeginMenu("Teleport to area")) return;
        string? cur = Automap.Current?.Name;
        for (int n = 0; n < Areas; n++)
        {
            string label = $"{n,2}  {Automap.AreaName(n)}{(_banks[n] == null ? "  (music unknown)" : "")}";
            if (ImGui.MenuItem(label, "", Fdat(n) == cur, _target < 0 && Fdat(n) != cur)) _request = n;
        }
        ImGui.EndMenu();
    }

    /// <summary>From Automap's world-update hook, at the head of the tick: start a
    /// requested change of area.</summary>
    public static void Tick(CpuContext c, IMemory m)
    {
        if (_test is { Length: 2 } && !_testDone && Environment.TickCount64 - _start > _test[0] * 1000L)
        { _testDone = true; _request = _test[1]; }
        int n = _request;
        if (n < 0) return;
        _request = -1;
        (int Sound, int Music) b = _banks[n] ?? (0xFF, 0xFF);
        Console.WriteLine($"[KFIII] teleport: to area {n} ({Automap.AreaName(n)}) banks {b.Sound},{b.Music}");
        _target = n; _wait = 0;
        var regs = c.Snapshot();
        try
        {
            c.SP -= 0x40;
            c.A0 = (uint)n; c.A1 = (uint)n; c.A2 = (uint)n; c.A3 = (uint)b.Sound;
            m.WriteU32(c.SP + 0x10, (uint)b.Music);
            m.WriteU32(c.SP + 0x14, 0x7F); m.WriteU32(c.SP + 0x18, 0x7F); m.WriteU32(c.SP + 0x1C, 0x7F);
            Game.kf_ChangeArea(c, m);
        }
        finally { c.Restore(regs); }
    }

    /// <summary>From Automap's sampling block (collision state saved, stack room made):
    /// once the target area is the current one, put the player on open floor. Gives up
    /// if the area has not loaded within 450 ticks.</summary>
    public static void Place(CpuContext c, IMemory m, Automap.Area a)
    {
        if (_target < 0) return;
        if (a.Name != Fdat(_target))
        {
            if (++_wait > 15 * 30) { Console.WriteLine("[KFIII] teleport: the area never loaded"); _target = -1; }
            return;
        }
        int area = _target;
        _target = -1;
        const int S = Automap.Size;
        int px = (int)m.ReadU32(GameAddr.PlayerPos) / Automap.Sample, pz = (int)m.ReadU32(GameAddr.PlayerPos + 8) / Automap.Sample;
        if (_starts.TryGetValue(area, out var st)) { px = st.X * Automap.PerTile + Automap.PerTile / 2; pz = st.Z * Automap.PerTile + Automap.PerTile / 2; }
        px = Math.Clamp(px, 0, S - 1); pz = Math.Clamp(pz, 0, S - 1);
        // Square rings outward from the start point, every 2 samples.
        for (int r = 0; r < S; r += 2)
            for (int dz = -r; dz <= r; dz += 2)
                for (int dx = -r; dx <= r; dx += 2)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != r) continue;
                    int x = px + dx, z = pz + dz;
                    if (!Fits(c, m, a, x, z)) continue;
                    int y = a.Floor[z * S + x] * 16;
                    int wx = x * Automap.Sample + Automap.Sample / 2, wz = z * Automap.Sample + Automap.Sample / 2;
                    m.WriteU32(GameAddr.PlayerPos, (uint)wx); m.WriteU32(GameAddr.PlayerPos + 4, (uint)y); m.WriteU32(GameAddr.PlayerPos + 8, (uint)wz);
                    Console.WriteLine($"[KFIII] teleport: placed at {wx},{y},{wz} (samples {x},{z}, {r} from the start)");
                    return;
                }
        Console.WriteLine("[KFIII] teleport: no open floor found; position left as it was");
    }

    /// <summary>Whether the player's body fits at sample (x, z): floor all round within a
    /// radius of about 3 samples, with no step or drop of more than about a quarter tile.</summary>
    static bool Fits(CpuContext c, IMemory m, Automap.Area a, int x, int z)
    {
        if (Automap.CellAt(c, m, a, x, z) != Automap.Kind.Floor) return false;
        int f = a.Floor[z * Automap.Size + x];
        for (int dz = -3; dz <= 3; dz++)
            for (int dx = -3; dx <= 3; dx++)
            {
                if (dx * dx + dz * dz > 10) continue;
                if (Automap.CellAt(c, m, a, x + dx, z + dz) != Automap.Kind.Floor) return false;
                if (Math.Abs(a.Floor[(z + dz) * Automap.Size + x + dx] - f) > 32) return false;
            }
        return true;
    }
}
