using RecompOne.Runtime.Context;
using RecompOne.Runtime.Dispatch;
using RecompOne.Runtime.Memory;
using Game = Recompiled.KingsFieldIII_game;

namespace KfIII;

/// <summary>
/// The automap (Enhancements "Automap"): a floor plan of the current area that fills in
/// as the player explores, with markers for doors, chests, people and the like. It is
/// drawn by <see cref="AutomapView"/> as a corner minimap and as a full map on the Map
/// key, in the style of kf1-enhanced (the port of the Japanese King's Field). This class
/// reads the world; AutomapView draws it.
///
/// All of its work runs once a world tick, from the hook before kf_WorldUpdate. It is
/// split by job:
/// <list type="bullet">
/// <item>Automap.Scan.cs: the floor plan, sampled with the game's own terrain collision
/// test, and the rays that mark what the player has seen.</item>
/// <item>Automap.Liquids.cs: water and lava, recognised by the texture a tile draws with.</item>
/// <item>Automap.Markers.cs: markers from the prop and creature tables.</item>
/// <item>Automap.Guide.cs: area names and hand-written notes the game's data does not hold.</item>
/// <item>Automap.Persistence.cs: what has been seen, saved per area.</item>
/// <item>Automap.Probes.cs: the KFIII_* test and research switches.</item>
/// </list>
///
/// An area is the game's 80 x 80 grid of tiles (GameAddr.TileRecords, 2048 world units
/// a tile); the map divides each tile into 8 x 8 samples, so an <see cref="Area"/> is
/// 640 x 640 samples. Positions handed to the view are in samples.
/// </summary>
public static partial class Automap
{
    /// <summary>Tiles across an area, samples across a tile, samples across an area,
    /// world units a tile, world units a sample.</summary>
    public const int Span = 80, PerTile = 8, Size = Span * PerTile, TileUnits = 2048, Sample = TileUnits / PerTile;

    // A tile record (GameAddr.TileRecords) is 10 bytes, 800 a row, in two 5-byte halves:
    // +0 model (0xFF none), +1 height (floor Y = -h << 7), +2 rotation (low 2 bits),
    // +3 collision shape, +4 flags. The first area uses only the second half (+5..+9).
    const int TileStride = 10, TileRowStride = Span * TileStride;

    /// <summary>What a sample of the floor plan is.</summary>
    public enum Kind : byte { Unknown, Floor, Solid, Outside, Chasm, Water, Lava }

    /// <summary>One area's floor plan and what the player has seen of it, by sample
    /// (z * Size + x) or by tile (tz * Span + tx).</summary>
    public sealed class Area
    {
        /// <summary>The area's module name (fdatNN).</summary>
        public readonly string Name;
        public readonly Kind[] Cells = new Kind[Size * Size];
        /// <summary>Floor Y / 16 of each free sample.</summary>
        public readonly short[] Floor = new short[Size * Size];
        public readonly bool[] Seen = new bool[Size * Size];
        /// <summary>Stood on by the player: floor at the player's height.</summary>
        public readonly bool[] Walk = new bool[Size * Size];
        /// <summary>Free, but too narrow for the player: shown as solid.</summary>
        public readonly bool[] Closed = new bool[Size * Size];
        /// <summary>Below one of the game's death heights: a liquid surface (TestDeath).</summary>
        public readonly bool[] Deadly = new bool[Size * Size];

        public readonly bool[] Sampled = new bool[Span * Span];
        public readonly bool[] WaterTile = new bool[Span * Span];
        /// <summary>Lava is water's twin: the same animated texture page with its own palette.</summary>
        public readonly bool[] LavaTile = new bool[Span * Span];
        /// <summary>Tiles whose texture has been looked at for water and lava.</summary>
        public readonly bool[] WaterChecked = new bool[Span * Span];
        public readonly HashSet<byte> WaterModels = new();
        public bool AnyLava;

        /// <summary>The extent of what has been seen, in samples (X1 &lt; 0: nothing yet).</summary>
        public int X0 = Size, Z0 = Size, X1 = -1, Z1 = -1;
        /// <summary>Bumped on every change, for the view's caches.</summary>
        public int Version;
        /// <summary>Seen bits changed since the last save; every tile sampled (reveal cheat).</summary>
        public bool Dirty, AllSampled;

        public Area(string name) { Name = name; }

        /// <summary>What the map shows: the sample's kind, or solid where the player
        /// cannot fit.</summary>
        public Kind Shown(int i) => Closed[i] ? Kind.Solid : Cells[i];

        /// <summary>Whether a sample is drawn: seen, or anything inside the area with the
        /// reveal cheat.</summary>
        public bool Visible(int i) => Seen[i] || (RevealAll && Cells[i] != Kind.Outside && Cells[i] != Kind.Unknown);

        public void Reveal(int i)
        {
            if (Seen[i]) return;
            Seen[i] = true; Dirty = true; Version++;
            int x = i % Size, z = i / Size;
            if (x < X0) X0 = x; if (x > X1) X1 = x;
            if (z < Z0) Z0 = z; if (z > Z1) Z1 = z;
        }
    }

    public static bool Enabled { get; private set; } = true;
    public static bool Minimap { get; private set; } = true;
    public static Area? Current { get; private set; }
    static readonly Dictionary<string, Area> _areas = new();

    /// <summary>Cheat: every sample shown, explored or not (the seen bits are kept).</summary>
    public static bool RevealAll;

    /// <summary>The player, for the view: position in samples, and yaw in radians (0 =
    /// north, increasing turns left).</summary>
    public static float PlayerX, PlayerZ, PlayerYaw;
    /// <summary>The player's Y in world units (floors are shaded against it).</summary>
    public static int PlayerFloor;
    /// <summary>Environment.TickCount64 of the last world tick.</summary>
    public static long LastTick;
    /// <summary>The in-game menu (kf_Menu) is up.</summary>
    public static bool InMenu;

    static int _ticks, _lastRx = int.MinValue, _lastRz, _lastRyaw;

    public static void Apply(bool on, bool minimap)
    {
        Enabled = on && !DevSwitch.Is("KFIII_MAP", "0");
        Minimap = minimap;
    }

    public static void Install()
    {
        AutomapView.Install();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => SaveNow();
    }

    /// <summary>The area module that is loaded (fdatNN), or null.</summary>
    static string? ActiveAreaModule() =>
        Dispatcher.ActiveNames.FirstOrDefault(n => n.StartsWith("fdat", StringComparison.Ordinal));

    // ---- hooks ---------------------------------------------------------------------

    /// <summary>Before kf_Menu (the in-game menu).</summary>
    public static void BeforeMenu(CpuContext c, IMemory m) => InMenu = true;

    /// <summary>After kf_Menu.</summary>
    public static void AfterMenu(CpuContext c, IMemory m) => InMenu = false;

    /// <summary>
    /// Before kf_WorldUpdate, once a world tick: the map's work for the tick, and the
    /// per-tick work of a few other patches that share this hook. While the full map is
    /// open the game is held here (<see cref="PausedForMap"/>). Always returns true, so
    /// kf_WorldUpdate runs.
    /// </summary>
    public static bool BeforeWorldUpdate(CpuContext c, IMemory m)
    {
        AudioRecord.Snapshot(m);
        AutoKey.Tick(c, m);
        Teleport.Tick(c, m);
        DamageLog.Tick(m, ActiveAreaModule());
        if (!Enabled) return true;
        if (AutomapView.FullOpen) PausedForMap(c, m);
        string? name = ActiveAreaModule();
        if (name == null) return true;
        if (Current?.Name != name)
        {
            if (Current != null) Save(Current);
            if (!_areas.TryGetValue(name, out var a)) { a = new Area(name); Load(a); _areas[name] = a; }
            Current = a;
            _lastRx = int.MinValue;
            _tilesSince = Environment.TickCount64;
            _pendingHits.Clear();
        }
        var area = Current;
        LastTick = Environment.TickCount64;

        int px = (int)m.ReadU32(GameAddr.PlayerPos), py = (int)m.ReadU32(GameAddr.PlayerPos + 4), pz = (int)m.ReadU32(GameAddr.PlayerPos + 8);
        TestPlacement(c, m, area);
        int yaw = m.ReadU16(GameAddr.Yaw) & 0xFFF;
        PlayerX = px / (float)Sample; PlayerZ = pz / (float)Sample;
        PlayerYaw = yaw * MathF.PI * 2f / 4096f;
        PlayerFloor = py;

        // The collision test leaves its state in RAM; it is put back afterwards, as are
        // the registers, so the game never sees the map's samples.
        var regs = c.Snapshot();
        var col = new byte[GameAddr.CollisionStateEnd - GameAddr.CollisionState];
        for (uint i = 0; i < col.Length; i++) col[i] = m.ReadU8(GameAddr.CollisionState + i);
        try
        {
            c.SP -= 0x100;
            CollisionProbe(c, m);
            Teleport.Place(c, m, area);
            if (RevealAll && !area.AllSampled)
            {
                for (int t = 0; t < Span * Span; t++) SampleTile(c, m, area, t % Span, t / Span);
                area.AllSampled = true;
                DumpMap(area);
            }

            // Rays go out when the player has moved two samples or turned, and every
            // 15 ticks regardless.
            int rx = (int)PlayerX, rz = (int)PlayerZ;
            bool moved = Math.Abs(rx - _lastRx) + Math.Abs(rz - _lastRz) >= 2 || Math.Abs(yaw - _lastRyaw) > 64;
            if (moved || ++_ticks % 15 == 0)
            {
                Reveal(c, m, area, PlayerX, PlayerZ);
                _lastRx = rx; _lastRz = rz; _lastRyaw = yaw;
            }
            SamplePending(c, m);
            CheckTiles(m);
            ApplyPendingHits(m, area);
            if (m.ReadU8(GameAddr.FallState) == 0) Walked(area, px, py, pz);
            CollectMarkers(m, area);
            MapProbe(area, rx, rz, px, pz, yaw);
        }
        catch (Exception e) { Console.WriteLine($"[KFIII] automap: {e.Message}"); }
        finally
        {
            c.Restore(regs);
            for (uint i = 0; i < col.Length; i++) m.WriteU8(GameAddr.CollisionState + i, col[i]);
        }
        if (area.Dirty && Environment.TickCount64 - _savedAt > 5000) Save(area);
        return true;
    }

    /// <summary>
    /// Holds the game while the full map is open, the way kf_Pause does: enter the menu
    /// screen (kf_MenuEnter), draw empty frames (kf_MenuBeginFrame, kf_MenuPresent) until
    /// the map closes, then leave it (kf_MenuLeave(0)). It runs from the head of the tick
    /// so that nothing in the main loop runs meanwhile; skipping kf_WorldUpdate alone
    /// would not do, as monsters are moved and strike from the main loop's other calls.
    /// The music carries on.
    /// </summary>
    static void PausedForMap(CpuContext c, IMemory m)
    {
        var regs = c.Snapshot();
        try
        {
            c.SP -= 0x40;
            Game.kf_MenuEnter(c, m);
            while (AutomapView.FullOpen)
            {
                LastTick = Environment.TickCount64;
                Game.kf_MenuBeginFrame(c, m);
                Game.kf_MenuPresent(c, m);
            }
            c.A0 = 0;
            Game.kf_MenuLeave(c, m);
        }
        finally { c.Restore(regs); }
    }
}
