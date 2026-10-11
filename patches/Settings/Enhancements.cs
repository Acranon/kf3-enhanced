using System.Text.Json;
using RecompOne.Runtime;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Hle;
using RecompOne.Runtime.Host.Window;

namespace KfIII;

/// <summary>
/// The player's choices for everything the port adds, saved to enhancements.json in
/// the working directory beside the runtime's settings.json. <see cref="Apply"/> pushes
/// them into the runtime and the patches, and runs on every change, so every option
/// takes effect at once. The pages that edit them are in SettingsPages.cs.
///
/// Classic mode has two strengths, and neither touches the saved choices (turning it
/// off brings them back):
///
///     Full classic       the console's picture (whole-pixel wobble, affine textures,
///                        dithered 15-bit colour, no filtering, 4:3, 15 fps) and none
///                        of the additions (map, equipment stats, unlocking with
///                        carried keys, jump, ...)
///     Classic + visuals  the console's game with the enhanced picture: the Display
///                        page applies, the additions are off
///
/// Controls (mouse look, mouse in menus, key bindings), directional sound, quick save
/// and movie skip are never changed by either. The KFIII_* switches read here and in
/// Visuals and Smooth win over the saved choices when set, for testing.
/// </summary>
public static partial class Enhancements
{
    /// <summary>The contents of enhancements.json. Property names are its keys.</summary>
    public sealed class Settings
    {
        /// 0 off, 1 full classic, 2 classic game with the enhanced picture.
        public int ClassicMode { get; set; }
        /// Older enhancements.json files hold a plain on/off "Classic" instead of
        /// ClassicMode; this keeps them loading (true reads as full classic).
        public bool Classic { get => ClassicMode == 1; set { if (value) ClassicMode = 1; else if (ClassicMode == 1) ClassicMode = 0; } }

        // Picture (Settings > Display)
        public bool StableGeometry { get; set; } = true;
        public bool StraightTextures { get; set; } = true;
        public bool SmoothColour { get; set; } = true;
        public bool TextureFiltering { get; set; } = true;
        public bool DepthBuffer { get; set; } = true;
        public bool AmbientOcclusion { get; set; } = true;
        public bool SmoothFog { get; set; } = true;
        public bool PerPixelLighting { get; set; } = true;

        public int DirectionalSound { get; set; } = 2;     // Settings > Audio: 0 off, 1 speakers, 2 headphones

        // Additions (Settings > Quality of life)
        public bool EquipmentStats { get; set; } = true;
        public bool Jump { get; set; }
        public bool AutoKey { get; set; } = true;
        public bool StatProgress { get; set; } = true;
        public bool DamageNumbers { get; set; } = true;
        public int DamageScale { get; set; } = 50;         // % of the first size
        public int DamageLogPos { get; set; } = 0;         // 0 bottom left, 1 top left
        public int DamageBarPos { get; set; } = 1;         // 0 top, 1 bottom
        public int JumpHeight { get; set; } = 1;           // 0 low, 1 normal, 2 high

        // Map (Settings > Map); KeyHints and MenuMouse are on Quality of life
        public bool Automap { get; set; } = true;
        public bool Minimap { get; set; } = true;
        public bool KeyHints { get; set; } = true;
        public bool MenuMouse { get; set; } = true;
        // The minimap (Settings > Map)
        public int MinimapCorner { get; set; } = 1;        // 0 top right (under the compass), 1 bottom right, 2 top left, 3 bottom left
        public int MinimapSize { get; set; } = 20;         // % of the picture's height
        public int MinimapZoom { get; set; } = 7;          // tiles from the centre to an edge
        public int MinimapOpacity { get; set; } = 85;      // %

        // Mouse look (Settings > Input > Keyboard/Mouse)
        public int MouseSensitivity { get; set; } = 90;    // % of 0.15 degrees per pixel
        public int MouseSmoothing { get; set; } = 50;      // 0 off .. 100
        public bool MouseInvertY { get; set; }
        public Controls.Binds Input { get; set; } = new();

        // Map cheats (Settings > Map)
        public bool MapRevealAll { get; set; }
        public bool CheatItems { get; set; }
        public bool CheatContents { get; set; }
        public bool CheatEnemies { get; set; }
        public bool CheatTraps { get; set; }
        public bool CheatSecrets { get; set; }
        public bool CheatKeys { get; set; }

        // More picture (Settings > Display)
        public int AoQuality { get; set; } = 2;            // 0 low, 1 medium, 2 high
        public int AntiAliasing { get; set; } = 8;         // samples: 0 off, 2, 4, 8
        public bool SeamlessWater { get; set; } = true;
        public bool SmoothMovies { get; set; }   // work in progress: the opening movies jitter with it on
        public int WaterReflections { get; set; } = 1;     // 0 off, 1 subtle, 2 strong
        public string Aspect { get; set; } = "16:9";
        public int Fps { get; set; } = -1;                 // -1 the monitor's rate, 0 the original 15
    }

    public static Settings Current { get; private set; } = new();

    static string PathOf => Path.Combine(Environment.CurrentDirectory, "enhancements.json");
    static readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public static void Load()
    {
        try
        {
            if (File.Exists(PathOf))
                Current = JsonSerializer.Deserialize<Settings>(File.ReadAllText(PathOf)) ?? new Settings();
        }
        catch (Exception e)
        {
            Console.WriteLine($"[KFIII] enhancements.json unreadable ({e.Message}); using defaults");
            Current = new Settings();
        }
    }

    internal static void Save()
    {
        try { File.WriteAllText(PathOf, JsonSerializer.Serialize(Current, _json)); }
        catch (Exception e) { Console.WriteLine($"[KFIII] could not save enhancements.json: {e.Message}"); }
    }

    /// <summary>Pushes the settings into the runtime and the patches.</summary>
    public static void Apply()
    {
        var s = Current;
        s.ClassicMode = Math.Clamp(s.ClassicMode, 0, 2);
        bool on = s.ClassicMode != 1;          // the enhanced picture (not in full classic)
        bool qol = s.ClassicMode == 0;         // the additions (off in either classic mode)
        bool stable = on && s.StableGeometry;
        GteDepth.Subpixel = stable;
        GteDepth.PreciseCull = stable;
        GteDepth.Enabled = on && s.StraightTextures;
        GteDepth.TrueColor = on && s.SmoothColour;
        GteDepth.Anisotropy = on && s.TextureFiltering ? 8 : 1;
        GteDepth.Mipmaps = on && s.TextureFiltering;
        GteDepth.ZBuffer = on && s.DepthBuffer;
        GteDepth.AmbientOcclusion = on && s.AmbientOcclusion;
        GteDepth.SmoothFog = on && s.SmoothFog;
        GteLightMap.Enabled = on && s.PerPixelLighting;
        GpuHle.MovieFilter = on && s.SmoothMovies && !DevSwitch.Is("KFIII_MOVIEFILTER", "0");
        // The cost follows the pixels: low runs the pass at the game's own resolution
        // with 8 samples, medium at 2x at most, high at the render scale with 16.
        GteDepth.AoResolution = s.AoQuality switch { 0 => 1, 1 => 2, _ => 0 };
        GteDepth.AoSamples = s.AoQuality == 0 ? 8 : 16;
        GteDepth.Msaa = on ? s.AntiAliasing : 0;
        Water.Apply(on, s.SeamlessWater, s.WaterReflections);
        // Sound, not picture: Classic mode leaves it alone.
        PositionalAudio.Apply(true, s.DirectionalSound);
        // The additions: either classic mode turns them off.
        GearCompare.Apply(qol && s.EquipmentStats);
        Jump.Apply(qol && s.Jump, s.JumpHeight);
        AutoKey.Enabled = qol && s.AutoKey;
        StatProgress.Enabled = qol && s.StatProgress;
        DamageLog.Enabled = qol && s.DamageNumbers;
        DamageLog.Scale = Math.Clamp(s.DamageScale, 30, 150) / 100f;
        DamageLog.LogTop = s.DamageLogPos == 1;
        DamageLog.BarBottom = s.DamageBarPos == 1;
        Automap.Apply(qol && s.Automap, s.Minimap);
        KeyHints.Enabled = qol && s.KeyHints;
        // Controls, not picture or additions: Classic mode leaves them alone.
        MenuMouse.Enabled = s.MenuMouse;
        Controls.Sens = Math.Clamp(s.MouseSensitivity, 10, 300) / 100f * Controls.EnvSens;
        Controls.SmoothMs = Math.Clamp(s.MouseSmoothing, 0, 100) * 0.5f;   // 100 is 50 ms
        Controls.InvertY = s.MouseInvertY || Controls.EnvInvertY;
        s.Input ??= new();
        AutomapView.MinimapCorner = Math.Clamp(s.MinimapCorner, 0, 3);
        AutomapView.MinimapSize = Math.Clamp(s.MinimapSize, 10, 50);
        AutomapView.MinimapZoom = Math.Clamp(s.MinimapZoom, 3, 20);
        AutomapView.MinimapOpacity = Math.Clamp(s.MinimapOpacity, 20, 100);
        Automap.RevealAll = s.MapRevealAll || DevSwitch.Is("KFIII_MAP_REVEAL");
        // KFIII_MAP_CHEATS=items,contents,... (or all) turns map cheats on for a test,
        // whatever the settings and Classic mode say.
        string cheatEnv = DevSwitch.Text("KFIII_MAP_CHEATS") ?? "";
        bool Cheat(bool v, string name) => (qol && v) || cheatEnv.Contains(name) || cheatEnv == "all";
        Automap.ShowItems = Cheat(s.CheatItems, "items");
        Automap.ShowContents = Cheat(s.CheatContents, "contents");
        Automap.ShowEnemies = Cheat(s.CheatEnemies, "enemies");
        Automap.ShowTraps = Cheat(s.CheatTraps, "traps") || DevSwitch.Is("KFIII_MAP_TRAPS");
        Automap.ShowSecrets = Cheat(s.CheatSecrets, "secrets") || DevSwitch.Is("KFIII_MAP_SECRETS");
        Automap.ShowKeys = Cheat(s.CheatKeys, "keys");
        float aspect = on ? Visuals.ParseAspect(s.Aspect) : 0f;
        Display.WideAspect = aspect;
        GteDepth.NearClip = aspect > 0f;
        Smooth.SetFps(on ? s.Fps : 0);

        Visuals.ApplyEnvironment();
    }

    public static void Install()
    {
        Load();
        Apply();
        Localization.Merge(Strings);
        SettingsRegistry.Register(new ClassicSection());
        SettingsRegistry.Register(new QolSection());
        SettingsRegistry.Register(new MapSection());
        SettingsRegistry.Extend("display", DrawDisplay);
        SettingsRegistry.Extend("audio", DrawAudio);

        // KFIII_TEST_MOVIEBLINK=1: the movie filter on in even half-seconds only, to
        // compare the two in one recording.
        if (DevSwitch.Is("KFIII_TEST_MOVIEBLINK"))
            DevSwitch.Background("kfiii_test_movieblink", () =>
            {
                while (true)
                {
                    GpuHle.MovieFilter = (Environment.TickCount64 / 500) % 2 == 0;
                    Thread.Sleep(20);
                }
            });

        // KFIII_TEST_CLASSIC=8,12,16: step Classic mode (off, full, visuals, off ...) at
        // these seconds after start, in order.
        var flips = DevSwitch.List("KFIII_TEST_CLASSIC");
        if (flips.Length > 0)
            DevSwitch.Background("kfiii_test_classic", () =>
            {
                var t0 = DateTime.Now;
                foreach (var at in flips.Select(double.Parse))
                {
                    var wait = t0.AddSeconds(at) - DateTime.Now;
                    if (wait > TimeSpan.Zero) Thread.Sleep(wait);
                    Current.ClassicMode = (Current.ClassicMode + 1) % 3;
                    Apply();
                    Console.WriteLine($"[KFIII] test: classic mode {Current.ClassicMode}");
                }
            });

        if (DevSwitch.Is("KFIII_ENH_PROBE"))
            Event.AddListener<RuntimeReadyEvent>(_ =>
                Console.WriteLine($"[KFIII] enh strings: '{Localization.T("kf3.enh")}' / '{Localization.T("kf3.enh.stable")}'"));
    }
}
