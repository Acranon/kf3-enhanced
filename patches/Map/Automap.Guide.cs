namespace KfIII;

// What the map says that the game's data does not: area names, what an odd container
// really is, what a person does, and guide notes for locks the data cannot explain. The
// map's cheats are meant to stand in for an online guide, so these tables grow as the
// game is played through.
public static partial class Automap
{
    /// <summary>Area names by area index (area n's module is fdat(2 + 3n)); an area
    /// missing here shows as "Area n".</summary>
    static readonly Dictionary<int, string> _areaNames = new()
    {
        [0] = "Quist",
        [1] = "Ralugo",
        [2] = "Lake Noel",
        [3] = "Dwarves Cave",
        [4] = "Ancient Battlefield",
        [5] = "Forest of Varde",
        [6] = "Cason",
        [7] = "High Elf Ruins",
        [8] = "Verdite Castle",
        [9] = "Hill of Prayer",
        [10] = "Field of Ice",
        [11] = "Valley of Garan",
        [12] = "Garrison Barracks",
        [13] = "Cave of Shudom",
        [14] = "Path of Poison",
        [15] = "Passage of Thieves",
        [16] = "Maze Temple of Orladin",   // Shudom's Room is a room inside it
        [17] = "Place of the Beginning",
        [18] = "Passage of Death",
        [19] = "First Level of the Royal Cemetery",
        [20] = "Sleeping Quarters of Guyra",
        // An endless flat grass field with no walls, music or Truth Glass entry: a test map.
        [21] = "Test Area? (unused)",
        [22] = "Basement of the Castle",
        [23] = "Ground Floor of the Castle",
        [24] = "Second Floor of the Castle",
        [25] = "Seath's Space",
        [26] = "Palace of Wind",
        [27] = "Seath's Space",   // the Swordsmaster's arena (25 is Seath's)
    };

    /// <summary>
    /// Guide notes for chests and doors whose lock the game's data cannot explain (sealed,
    /// or opened by an event), by (area, prop index). Prop indices are the map file's
    /// order, the same every visit.
    /// </summary>
    static readonly Dictionary<(int Area, int Prop), string> _hints = new()
    {
        // Quist: flag 0x16 (set in the Ancient Battlefield) opens these: Ed Edmund does,
        // once Marilyn's son has the charm.
        [(0, 70)] = "Opened by Ed Edmund, after Marilyn's quest",
        [(0, 33)] = "Opened by Ed Edmund, after Marilyn's quest",
        [(0, 116)] = "Opened by Ed Edmund, after Marilyn's quest",
        // Quist: the sealed chest beside the Statue of the Hero.
        [(0, 344)] = "Sealed: strike the star on the Statue of the Hero (Capricorn or better)",
    };

    /// <summary>What a container really is, by (area, prop type), where its class says
    /// "remains" and the player sees something else. Types from 0x100 are the area's own,
    /// so the same type means different things in different areas.</summary>
    static readonly Dictionary<(int Area, int Type), string> _labels = new()
    {
        // Cave of Shudom: the odd rock beside the giant skeletons that holds a ring
        // (tests/saves/giant_skeleton.kfstate, #42).
        [(13, 0x134)] = "Strange rock",
        // Ralugo: a clay pot (beside #254, type 0xF7; tests/saves/ralugo_hidden.kfstate).
        [(1, 0x134)] = "Pot",
        // Cave of Shudom: its hidden item is at a giant skeleton (the Magician's Key).
        [(13, 0x12A)] = "Hidden item (giant skeleton)",
        // Ancient Battlefield: soldiers' bodies (KFIII_SCAN of battlefield_exit.kfstate).
        [(4, 0x12D)] = "Soldier's body",
        [(4, 0x12E)] = "Soldier's body",
        [(4, 0x12F)] = "Soldier's body",
        [(4, 0x130)] = "Soldier's body",
        [(4, 0x131)] = "Soldier's body",
        // Garrison Barracks: soldiers' bodies (tests/saves/soldierbody.kfstate).
        [(12, 0x12C)] = "Soldier's body",
        [(12, 0x12D)] = "Soldier's body",
        [(12, 0x12E)] = "Soldier's body",
        [(12, 0x12F)] = "Soldier's body",
        [(12, 0x130)] = "Soldier's body",
        [(12, 0x131)] = "Soldier's body",
    };

    /// <summary>What a person does, by creature type (entity +1), where it is known; the
    /// names themselves come from the game's profiles (CreatureNames).</summary>
    static readonly Dictionary<int, string> _personRoles = new()
    {
        [0x00] = "merchant",    // Christy Clements
        [0x05] = "innkeeper",   // Rene Thomas
        [0x16] = "merchant",    // Ed Edmund
    };

    public static string AreaName(int n) => _areaNames.TryGetValue(n, out var s) ? s : $"Area {n}";

    /// <summary>The current area's index, or -1.</summary>
    public static int CurrentArea => Current != null ? AreaIndex(Current.Name) : -1;

    /// <summary>The area index of a module name: fdat(2 + 3n) is area n.</summary>
    static int AreaIndex(string fdat) => int.TryParse(fdat.AsSpan(4), out var k) ? (k - 2) / 3 : -1;

    /// <summary>A creature's name from its profile (the Truth Glass's), with a role for
    /// the people that have one; "Person (#type)" or "Enemy (#type)" without a profile.</summary>
    public static string CreatureName(int type, bool person)
    {
        string name = CreatureNames.ByType.TryGetValue(type, out var n) ? n : (person ? $"Person (#{type:X2})" : $"Enemy (#{type:X2})");
        return person && _personRoles.TryGetValue(type, out var role) ? $"{name} ({role})" : name;
    }
}
