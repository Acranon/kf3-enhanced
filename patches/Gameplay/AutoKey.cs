using System.Numerics;
using ImGuiNET;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Host.Window;
using RecompOne.Runtime.Memory;
using Game = Recompiled.KingsFieldIII_game;

namespace KfIII;

/// <summary>
/// Settings "Unlock with keys you carry": interacting with a locked chest or door
/// unlocks it when its key is in the bag, as using the key from the item menu would,
/// and the next interact opens it. Keyhole dials and Silviera's Key pedestal get the
/// key used on them the same way (see the keyhole section).
///
/// kf_InteractTarget queues message 6 ("Locked") for a locked chest or door, with the
/// object's prop record in s0 on every path to it (0x8005E7B4). A pre-hook on
/// kf_QueueMessage during the interact reads that record's lock byte (+0x38): if it is
/// an item id and the bag (<see cref="GameAddr.BagCounts"/>) holds one, the game's own
/// key routine kf_UseKey runs on it (lock byte == item: the byte becomes 0xFF and it
/// returns 1), and "Unlocked with &lt;key&gt;" replaces the message. Keys are not used
/// up; the item menu does not use them up either (tests/saves/copperchest.kfstate).
/// Lock bytes 0x96/0x98/0x9A are sealed-door codes, not spells (Automap.LockOf), and
/// never in the bag.
/// </summary>
public static class AutoKey
{
    public static bool Enabled = true;

    static readonly bool _log = DevSwitch.Is("KFIII_AUTOKEY_LOG");

    /// Item ids run below this; a lock byte at or above it is not a key.
    const int ItemCount = 0xB5;
    const int SilvieraKey = 0x8D, OrladinKey = 0x88;
    /// kf_QueueMessage's "Locked".
    const uint LockedMessage = 6;

    // kf_InteractTarget calls in progress (it can nest).
    static int _depth;

    /// <summary>Before kf_InteractTarget.</summary>
    public static void BeforeInteract(CpuContext c, IMemory m) { _depth++; _found.Clear(); }

    /// <summary>After kf_InteractTarget: checks the props it found for a keyhole to use a key on.</summary>
    public static void AfterInteract(CpuContext c, IMemory m)
    {
        if (_depth > 0) _depth--;
        if (Enabled && _depth == 0) KeyholeCheck(m);
    }

    // ---- keyholes (prop class 0x53): the Forest of Varde's stump dials ----------------------
    //
    // Interacting with a class 0x53 prop does nothing at all unless its key byte (+0x3D)
    // is 0xFF: kf_InteractTarget (0x8005E594) needs +0x3E != 0 and +8 == 0, then sets
    // +8 = 1. With a key there (Varde's Key, 0x66), the item menu's kf_UseItem turns the
    // dial (+0x38 counts up) and the stumps swap; the key stays in the bag. So while the
    // interact runs, the props kf_FindPropAhead finds are noted, and a keyhole whose key
    // is in the bag gets the key used on it at the next world tick, as the menu would
    // (tests/saves/keyhole.kfstate).

    const int KeyholeClass = 0x53;
    /// Silviera's Key pedestal (prop type 0x101, class 0x18).
    const int SilvieraPedestal = 0x101;

    static readonly List<int> _found = new();
    static int _pendingItem = -1;

    /// <summary>After kf_FindPropAhead: v0 is a prop index, or -1.</summary>
    public static void AfterFindProp(CpuContext c, IMemory m)
    {
        if (_depth > 0 && (int)c.V0 >= 0 && (int)c.V0 < GameAddr.PropCount) _found.Add((int)c.V0);
    }

    static void KeyholeCheck(IMemory m)
    {
        foreach (int i in _found)
        {
            uint rec = GameAddr.PropTable + (uint)(i * GameAddr.PropStride);
            int type = m.ReadU16(rec + 6);
            if (type == 0xFF) continue;
            // kf_UseItem's branch for Silviera's Key (0x8005D244) puts it on an empty
            // pedestal (+0x38 0xFF -> 0x8D, the key leaves the bag) and func_8005C7E8
            // opens its door. A pedestal with the key on it is left to the game (which
            // gives the key back).
            if (type == SilvieraPedestal)
            {
                if (m.ReadU8(rec + 0x38) != 0xFF || m.ReadU8(GameAddr.BagCounts + SilvieraKey) == 0) return;
                _pendingItem = SilvieraKey;
                Console.WriteLine($"[KFIII] auto key: Silviera's Key on pedestal #{i}");
                return;
            }
            if (m.ReadU8(GameAddr.PropDefs + (uint)type * 24) != KeyholeClass) continue;
            int key = m.ReadU8(rec + 0x3D);
            if (key >= ItemCount || m.ReadU8(rec + 0x3E) == 0 || m.ReadU16(rec + 8) != 0) return;
            if (m.ReadU8(GameAddr.BagCounts + (uint)key) == 0) return;
            _pendingItem = key;
            Console.WriteLine($"[KFIII] auto key: {Automap.ItemName(m, key)} on keyhole #{i}");
            return;
        }
    }

    /// <summary>
    /// Before kf_QueueMessage: during an interact, a "Locked" for a lock whose key is in
    /// the bag unlocks it instead. Returns false to drop the message.
    /// </summary>
    public static bool BeforeMessage(CpuContext c, IMemory m)
    {
        if (_log && _depth > 0)
            Console.WriteLine($"[KFIII] interact message {c.A0} s0 {c.S0:X8} ({(c.S0 - GameAddr.PropTable) / (double)GameAddr.PropStride:F2}) ra {c.RA:X8}");
        if (!Enabled || _depth == 0 || c.A0 != LockedMessage) return true;
        uint rec = c.S0;
        if (rec < GameAddr.PropTable || rec >= GameAddr.PropTable + GameAddr.PropCount * GameAddr.PropStride
            || (rec - GameAddr.PropTable) % GameAddr.PropStride != 0) return true;
        int key = m.ReadU8(rec + 0x38);
        if (key >= ItemCount || m.ReadU8(GameAddr.BagCounts + (uint)key) == 0) return true;
        // Orladin's Key is used up by its own lock (kf_UseItem's 0x88 case, 0x8005D300:
        // the pillar's lock byte becomes 0xFE and the key leaves the bag), and there are
        // few of them: never spend one for the player. kf_UseKey would also open that lock
        // without using the key up.
        if (key == OrladinKey) return true;

        var regs = c.Snapshot();
        uint got;
        try
        {
            c.A0 = rec;
            c.A1 = (uint)key;
            Game.kf_UseKey(c, m);
            got = c.V0;
            // As the item menu does on success (0x8005D20C): the key-turned sound.
            if (got == 1) Game.kf_UnlockSound(c, m);
        }
        finally { c.Restore(regs); }
        string name = Automap.ItemName(m, key);
        Console.WriteLine($"[KFIII] auto key: {name} on prop #{(rec - GameAddr.PropTable) / GameAddr.PropStride} -> {got}");
        if (got != 1) return true;
        // The game has no "unlocked" message (messages 0..35, listed with KFIII_TEST_MSGS:
        // Locked, Wrong key!, Sealed, Doesn't Open ... none fits), so "Locked" is dropped
        // and the port's own line takes its place for a moment.
        _shown = $"Unlocked with {name}";
        _shownAt = Environment.TickCount64;
        return false;
    }

    // ---- the "Unlocked with ..." line, where the game prints its messages ------------------

    static string _shown = "";
    static long _shownAt = -100000;
    const long ShowMs = 2200;

    public static void Install() => PanelManager.Register(Panel.Instance);

    sealed class Panel : IFloatingPanel
    {
        public static readonly Panel Instance = new();
        public string Name => "kf3autokey";
        public bool IsOpen { get => Environment.TickCount64 - _shownAt < ShowMs; set { } }

        public void Draw()
        {
            if (!OutputView.Valid) return;
            long age = Environment.TickCount64 - _shownAt;
            // Fades out over the last 400 ms.
            float a = age < ShowMs - 400 ? 1f : Math.Max(0f, (ShowMs - age) / 400f);
            var p0 = OutputView.Min; var p1 = OutputView.Max;
            float sy = (p1.Y - p0.Y) / 240f;
            float ts = Math.Max(16f, 11f * sy);
            var font = ImGui.GetFont();
            var ext = font.CalcTextSizeA(ts, float.MaxValue, 0f, _shown);
            // The game's message line: centred, about y 205 of 240, in its parchment colour.
            var at = new Vector2((p0.X + p1.X - ext.X) * 0.5f, p0.Y + 202f * sy);
            var dl = ImGui.GetForegroundDrawList();
            uint alpha = (uint)(a * 255) << 24;
            dl.AddText(font, ts, at + new Vector2(2, 2), alpha | 0x00101010u, _shown);
            dl.AddText(font, ts, at, alpha | 0x0090D0D8u, _shown);
        }
    }

    // ---- once a world tick ------------------------------------------------------------------

    /// <summary>From Automap's world-update hook: uses a pending key, and runs the tests.</summary>
    public static void Tick(CpuContext c, IMemory m)
    {
        if (_testEquip >= 0 && !_equipped)
        {
            _equipped = true;
            var r = c.Snapshot();
            try { c.SP -= 0x40; c.A0 = (uint)_testEquip; Game.kf_EquipWeapon(c, m); Game.kf_RecalcStats(c, m); }
            finally { c.Restore(r); }
            Console.WriteLine($"[KFIII] test: equipped weapon {_testEquip:X2}");
        }
        UseItemTest(c, m);
        if (_pendingItem >= 0)
        {
            int item = _pendingItem;
            _pendingItem = -1;
            _shown = $"Used {Automap.ItemName(m, item)}";
            _shownAt = Environment.TickCount64;
            UseItem(c, m, item);
        }
        MessagesTest(c, m);
    }

    /// Uses an item as the item menu does: kf_UseItem(player position, view angles, item).
    static void UseItem(CpuContext c, IMemory m, int item)
    {
        var saved = c.Snapshot();
        try { c.A0 = GameAddr.PlayerPos; c.A1 = GameAddr.Pitch; c.A2 = (uint)item; Game.kf_UseItem(c, m); }
        finally { c.Restore(saved); }
    }

    // ---- tests ------------------------------------------------------------------------------

    static readonly long _start = Environment.TickCount64;

    // KFIII_TEST_EQUIP=1B: equip that weapon (hex id) at the first world tick.
    static readonly int _testEquip = DevSwitch.Text("KFIII_TEST_EQUIP") is { } te ? Convert.ToInt32(te, 16) : -1;
    static bool _equipped;

    // KFIII_TEST_USEITEM=seconds,item: list the bag, then use that item (hex id) as the
    // menu does, that many seconds after start.
    static readonly string[]? _useTest = DevSwitch.Text("KFIII_TEST_USEITEM")?.Split(',');
    static bool _useDone;

    static void UseItemTest(CpuContext c, IMemory m)
    {
        if (_useTest is not { Length: 2 } || _useDone) return;
        if (Environment.TickCount64 - _start < int.Parse(_useTest[0]) * 1000L) return;
        _useDone = true;
        for (int id = 0; id < ItemCount; id++)
            if (m.ReadU8(GameAddr.BagCounts + (uint)id) != 0)
                Console.WriteLine($"[KFIII] bag {id:X2} {Automap.ItemName(m, id)} x{m.ReadU8(GameAddr.BagCounts + (uint)id)}");
        int item = Convert.ToInt32(_useTest[1], 16);
        UseItem(c, m, item);
        Console.WriteLine($"[KFIII] test: used item {item:X2} {Automap.ItemName(m, item)}");
    }

    // KFIII_TEST_MSGS=start,first,last[,ms]: from start seconds, queue messages first..last
    // one at a time, every ms (default 1000).
    static readonly int[]? _testMessages = DevSwitch.Text("KFIII_TEST_MSGS")?.Split(',').Select(int.Parse).ToArray();
    static int _lastTestMessage = -1;

    static void MessagesTest(CpuContext c, IMemory m)
    {
        if (_testMessages is not { Length: >= 3 }) return;
        long t = Environment.TickCount64 - _start - _testMessages[0] * 1000L;
        if (t < 0) return;
        int id = _testMessages[1] + (int)(t / (_testMessages.Length > 3 ? _testMessages[3] : 1000));
        if (id == _lastTestMessage || id > _testMessages[2]) return;
        _lastTestMessage = id;
        var regs = c.Snapshot();
        try { c.A0 = (uint)id; Game.kf_QueueMessage(c, m); }
        finally { c.Restore(regs); }
        Console.WriteLine($"[KFIII] test: message {id} at {Environment.TickCount64 - _start} ms");
    }
}
