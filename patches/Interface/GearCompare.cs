using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using Game = Recompiled.KingsFieldIII_game;

namespace KfIII;

/// <summary>
/// Equipment stats (Enhancements "Equipment stats"). On an equipment list, a panel
/// beside the item shows what the highlighted piece gives; once it is picked and the
/// USE / CANCEL prompt is up, the panel compares the player's totals now with the
/// totals wearing it. In a shop it lists the piece's own stats down the left side.
/// Modelled on the GearCompare mod of Verdite2 (the RecompOne port of the first North
/// American King's Field) and the equipment stats of kf1-enhanced (the port of the
/// Japanese King's Field).
///
/// <para><b>What is compared.</b> kf_RecalcStats rebuilds PWR and the magic powers
/// (GameAddr.Pwr) from the base stats, and the offense and defense words from the
/// weapon record, the seven armour records (through kf_AddArmorStats), the items with
/// special bonuses and the status effects. Besides those totals it writes only
/// GameAddr.RecalcScratch. So "with it" is: put the candidate in its slot byte, run
/// kf_RecalcStats, read the totals, and put back the slot byte and everything it wrote.
/// The real equip routines are never called (the weapon one also loads a model and
/// plays a sound). An item's own stats are the same measurement against the empty
/// slot, so the bonuses hard-coded for a ring or a helm show up with the rest.</para>
///
/// <para><b>Where it hooks.</b> kf_MenuEquipList(kind) is the item list of one
/// EQUIPMENT row (see <see cref="SlotFor"/>; kind 1 is magic and 9 the square button,
/// other pages). Each frame it draws the list and calls kf_ItemPreview(id) for the
/// highlighted row (0xFF on TAKE OFF). The prompt, kf_MenuPrompt(menu, kind, flags, id),
/// is a modal loop of its own that also calls kf_ItemPreview. The shop pages (kf_ShopBuy
/// lists the stock, kf_ShopSell the bag) call kf_ItemPreview for their highlighted row
/// too. The panel is drawn just before kf_MenuPresent, into the same menu frame, with
/// the status screens' own primitives (<see cref="MenuDraw"/>); the hidden effects go
/// in GearSpecial's box beside it.</para>
///
/// <para>Switches: KFIII_GEARSTATS=0 or 1 forces it off or on whatever the setting;
/// KFIII_GEARSTATS_PROBE=1 logs the rows each time they are worked out;
/// KFIII_GEARSTATS_GIVE=02,2B,... (hex item ids) puts one of each in the bag when an
/// equipment list opens, so a list has something to compare.</para>
/// </summary>
public static class GearCompare
{
    public static bool Enabled { get; private set; } = true;
    static readonly bool _forceOff = DevSwitch.Is("KFIII_GEARSTATS", "0");
    static readonly bool _forceOn = DevSwitch.Is("KFIII_GEARSTATS");
    static readonly bool _probe = DevSwitch.Is("KFIII_GEARSTATS_PROBE");
    static readonly string[] _give = DevSwitch.List("KFIII_GEARSTATS_GIVE");

    public static void Apply(bool on) => Enabled = !_forceOff && (_forceOn || on);

    // The stat totals kf_RecalcStats writes: PWR, the five magic powers, then the 8
    // offense and 9 defense words.
    const uint StatsStart = GameAddr.Pwr, StatsEnd = GameAddr.Defense + 9 * 2;

    static readonly string[] _groupHeads = ["", "MAGIC", "OFFENSE", "DEFENSE"];

    // The stats in the status screens' order (kf_MenuOffenseDefense, and the main
    // menu's PWR / MAG panel), with their group: 0 PWR, 1 magic, 2 offense, 3 defense.
    static readonly (string Label, uint Addr, int Group)[] _stats =
    [
        ("PWR", GameAddr.Pwr, 0),
        ("HOLY", GameAddr.Pwr + 0xA, 1), ("FIRE", GameAddr.Pwr + 0x2, 1), ("EARTH", GameAddr.Pwr + 0x4, 1),
        ("WIND", GameAddr.Pwr + 0x8, 1), ("WATER", GameAddr.Pwr + 0x6, 1),
        ("SLASH", GameAddr.Offense, 2), ("BLOW", GameAddr.Offense + 0x2, 2), ("STAB", GameAddr.Offense + 0x4, 2),
        ("HOLY", GameAddr.Offense + 0x6, 2), ("FIRE", GameAddr.Offense + 0x8, 2), ("EARTH", GameAddr.Offense + 0xA, 2),
        ("WIND", GameAddr.Offense + 0xC, 2), ("WATER", GameAddr.Offense + 0xE, 2),
        ("SLASH", GameAddr.Defense, 3), ("BLOW", GameAddr.Defense + 0x2, 3), ("STAB", GameAddr.Defense + 0x4, 3),
        ("POISON", GameAddr.Defense + 0x10, 3), ("DARK", GameAddr.Defense + 0x6, 3), ("FIRE", GameAddr.Defense + 0x8, 3),
        ("EARTH", GameAddr.Defense + 0xA, 3), ("WIND", GameAddr.Defense + 0xC, 3), ("WATER", GameAddr.Defense + 0xE, 3),
    ];

    /// <summary>The slot byte behind kf_MenuEquipList's kind, or null for the other pages.</summary>
    static uint? SlotFor(int kind) => kind switch
    {
        0 => EquipSlot.Weapon,
        2 => EquipSlot.Shield,
        3 => EquipSlot.Head,
        4 => EquipSlot.Body,
        5 => EquipSlot.Arms,
        6 => EquipSlot.Feet,
        7 => EquipSlot.Item1,
        8 => EquipSlot.Item2,
        _ => null,
    };

    /// <summary>The slot an item id is worn in (ranges as in <see cref="EquipSlot"/>).</summary>
    static uint? SlotForItem(int id) => id switch
    {
        <= 0x21 => EquipSlot.Weapon,
        <= 0x29 => EquipSlot.Head,
        <= 0x32 => EquipSlot.Body,
        <= 0x3E => EquipSlot.Shield,
        <= 0x47 => EquipSlot.Arms,
        <= 0x50 => EquipSlot.Feet,
        <= 0x5C => EquipSlot.Item1,
        _ => null,
    };

    static int _kind = -1;          // the open equipment list's kind, -1 none
    static bool _prompt;            // its USE / CANCEL prompt is up
    static int _shop;               // kf_ShopBuy / kf_ShopSell calls under way
    static int _hover = -1;         // the id kf_ItemPreview was last given

    // What the rows were worked out for: the item id, plus PromptKey in the prompt or
    // ShopKey in a shop; -2 when they must be worked out again.
    static int _shownFor = -2;
    const int PromptKey = 0x100, ShopKey = 0x200;

    struct Row { public string Text; public int Now, New; public bool Head; }
    static readonly List<Row> _rows = [];

    // ---- hooks -----------------------------------------------------------------

    /// <summary>Before kf_MenuEquipList: an equipment list opens.</summary>
    public static void BeforeEquipList(CpuContext c, IMemory m)
    {
        foreach (var t in _give)
        {
            uint at = GameAddr.BagCounts + Convert.ToUInt32(t, 16);
            if (m.ReadU8(at) == 0) m.WriteU8(at, 1);
        }
        _kind = SlotFor((int)c.A0) is null ? -1 : (int)c.A0;
        _prompt = false; _hover = -1; _shownFor = -2;
    }

    /// <summary>After kf_MenuEquipList: the list has closed.</summary>
    public static void AfterEquipList(CpuContext c, IMemory m)
    {
        if (_probe && _kind >= 0 && SlotFor(_kind) is uint slot)
            Console.WriteLine($"[KFIII] gear stats left kind {_kind}: slot 0x{m.ReadU8(slot):X2}, "
                + string.Join(" ", _stats.Select(t => m.ReadU16(t.Addr))));
        _kind = -1; _prompt = false;
    }

    /// <summary>Before kf_ItemPreview(id): the highlighted row's item.</summary>
    public static void BeforePreview(CpuContext c, IMemory m)
    {
        _hover = (int)(c.A0 & 0xFF);
        if (_probe && _shop > 0) Console.WriteLine($"[KFIII] shop preview 0x{_hover:X2}");
    }

    /// <summary>Before kf_MenuPrompt(menu, kind, flags, id): USE / CANCEL for an item.</summary>
    public static void BeforePrompt(CpuContext c, IMemory m)
    {
        if (_kind < 0) return;
        _prompt = true;
        _hover = (int)(c.A3 & 0xFF);
    }

    /// <summary>After kf_MenuPrompt.</summary>
    public static void AfterPrompt(CpuContext c, IMemory m) { _prompt = false; _shownFor = -2; }

    /// <summary>Before kf_ShopBuy and kf_ShopSell.</summary>
    public static void BeforeShop(CpuContext c, IMemory m) { _shop++; _hover = -1; _shownFor = -2; }

    /// <summary>After kf_ShopBuy and kf_ShopSell.</summary>
    public static void AfterShop(CpuContext c, IMemory m) { if (_shop > 0) _shop--; _hover = -1; _shownFor = -2; }

    /// <summary>Before kf_MenuPresent: draws the panel into the menu frame.</summary>
    public static void BeforePresent(CpuContext c, IMemory m)
    {
        if (Enabled && _kind < 0 && _shop > 0 && _hover >= 0 && SlotForItem(_hover) is uint shopSlot)
        {
            ShopPanel(c, m, shopSlot);
            return;
        }
        if (!Enabled || _kind < 0 || _hover < 0 || SlotFor(_kind) is not uint slot) return;
        var regs = c.Snapshot();
        try
        {
            c.SP -= 0x100u;   // room on the game's stack for MenuDraw's text record
            int key = _hover + (_prompt ? PromptKey : 0);
            if (key != _shownFor)
            {
                Work(c, m, slot, (byte)_hover, _prompt);
                _shownFor = key;
            }
            _winBottom = -1;
            if (_rows.Count > 0 || _prompt) Draw(c, m);
            ShowSpecial(m, _hover);
        }
        catch (Exception e)
        {
            Console.WriteLine($"[KFIII] gear stats: {e.Message}");
            _kind = -1;
        }
        finally
        {
            c.Restore(regs);
        }
    }

    // ---- the SPECIAL box -------------------------------------------------------

    static int _specialFor = -1;
    static List<string> _special = [];

    /// <summary>
    /// Hands the piece's hidden effects (GearEffects) to GearSpecial: under the stats
    /// window, or at the top right when no window was drawn.
    /// </summary>
    static void ShowSpecial(IMemory m, int id)
    {
        if (id == 0xFF) return;
        if (id != _specialFor) { _special = GearEffects.Lines(m, id); _specialFor = id; }
        if (_special.Count == 0) return;
        int margin = ScreenOverlay.WideMargin;
        if (_winBottom < 0) GearSpecial.Show(_special, 320 + margin - 5, 320 + margin - 5, Top - 2, Top - 2);
        else GearSpecial.Show(_special, _winLeft, _winRight, Top, _winBottom);
    }

    // ---- shops -----------------------------------------------------------------

    static void ShopPanel(CpuContext c, IMemory m, uint slot)
    {
        var regs = c.Snapshot();
        try
        {
            c.SP -= 0x100u;
            if (_hover + ShopKey != _shownFor) { Work(c, m, slot, (byte)_hover, false); _shownFor = _hover + ShopKey; }
            if (_rows.Count > 0) DrawShopColumn(c, m);
        }
        catch (Exception e) { Console.WriteLine($"[KFIII] shop stats: {e.Message}"); _shop = 0; }
        finally { c.Restore(regs); }
    }

    /// <summary>
    /// The shop's panel: one thin column in the strip left of the shop's windows (they
    /// start at x 27; the wide-screen margin is free too), from y 30 down, with labels
    /// of at most five letters so it never reaches them.
    /// </summary>
    static void DrawShopColumn(CpuContext c, IMemory m)
    {
        int margin = ScreenOverlay.WideMargin;
        // EndPad keeps the first and last rows clear of the window border's corners.
        const int SidePad = 4, EndPad = 9, LabelIndent = 4;
        const int NumberWidth = 3 * MenuDraw.Advance, LabelWidth = 5 * MenuDraw.Advance;
        int x0 = -margin + 3, top = 30;
        int width = Math.Min(2 * SidePad + LabelIndent + LabelWidth + 3 + NumberWidth, margin + 24);
        int y = top + EndPad;
        foreach (var r in _rows)
        {
            if (r.Head) MenuDraw.Text(c, m, x0 + SidePad, y, r.Text == "OFFENSE" ? "ATTACK" : r.Text == "DEFENSE" ? "DEFEND" : r.Text);
            else
            {
                string label = r.Text == "POISON" ? "POISN" : r.Text;
                MenuDraw.Text(c, m, x0 + SidePad + LabelIndent, y, label.Length > 5 ? label[..5] : label);
                MenuDraw.Number(c, m, x0 + width - SidePad - NumberWidth, y, r.New, r.New < 0 ? MenuDraw.Worse : MenuDraw.Plain);
            }
            y += RowH;
        }
        // Drawn at the seamless width, the extra off the left edge of the screen.
        MenuDraw.Window(c, m, x0 + width - MenuDraw.SeamlessWidth, top, MenuDraw.SeamlessWidth, y - top + EndPad - 1);
    }

    // ---- what changes ----------------------------------------------------------

    /// <summary>
    /// The stat totals with <paramref name="id"/> in the slot; the slot and everything
    /// kf_RecalcStats wrote are put back afterwards.
    /// </summary>
    static int[] With(CpuContext c, IMemory m, uint slot, byte id)
    {
        byte old = m.ReadU8(slot);
        var block = new byte[StatsEnd - StatsStart];
        for (uint i = 0; i < block.Length; i++) block[i] = m.ReadU8(StatsStart + i);
        uint scratch = m.ReadU32(GameAddr.RecalcScratch);
        var v = new int[_stats.Length];
        try
        {
            m.WriteU8(slot, id);
            Game.kf_RecalcStats(c, m);
            for (int i = 0; i < _stats.Length; i++) v[i] = m.ReadU16(_stats[i].Addr);
        }
        finally
        {
            m.WriteU8(slot, old);
            for (uint i = 0; i < block.Length; i++) m.WriteU8(StatsStart + i, block[i]);
            m.WriteU32(GameAddr.RecalcScratch, scratch);
        }
        return v;
    }

    /// <summary>
    /// Fills the rows: the stats that differ, under their group's heading. Comparing,
    /// each row holds the total now and with the item; otherwise the item's own
    /// contribution (with it minus with the slot empty).
    /// </summary>
    static void Work(CpuContext c, IMemory m, uint slot, byte id, bool compare)
    {
        _rows.Clear();
        if (!compare && id == 0xFF) return;   // TAKE OFF while browsing: nothing to show

        int[] a, b;
        if (compare)
        {
            a = new int[_stats.Length];
            for (int i = 0; i < _stats.Length; i++) a[i] = m.ReadU16(_stats[i].Addr);
            b = With(c, m, slot, id);
        }
        else
        {
            a = With(c, m, slot, 0xFF);
            b = With(c, m, slot, id);
        }

        int group = -1;
        for (int i = 0; i < _stats.Length; i++)
        {
            if (a[i] == b[i]) continue;
            if (_stats[i].Group != group)
            {
                group = _stats[i].Group;
                if (_groupHeads[group].Length > 0) _rows.Add(new Row { Text = _groupHeads[group], Head = true });
            }
            _rows.Add(compare
                ? new Row { Text = _stats[i].Label, Now = a[i], New = b[i] }
                : new Row { Text = _stats[i].Label, New = b[i] - a[i] });
        }

        if (_probe)
            Console.WriteLine($"[KFIII] gear stats kind {_kind} id 0x{id:X2} {(compare ? "compare" : "item")}: "
                + string.Join(", ", _rows.Select(r => r.Head ? $"[{r.Text}]" : compare ? $"{r.Text} {r.Now}->{r.New}" : $"{r.Text} {r.New}")));
    }

    // ---- drawing on an equipment list -------------------------------------------

    const int RowH = 12, Pad = 7, Indent = 6, ColumnGap = 3;

    // The free space on an equipment list: from just under the top of the screen to
    // just above the item list's window (y 147 on every list, measured off screenshots).
    const int Top = 6, Bottom = 145;
    const int MaxLines = (Bottom - Top - 2 * Pad) / RowH;

    // Where the stats window was drawn this frame, for the SPECIAL box; _winBottom -1 none.
    static int _winLeft, _winRight, _winBottom;

    /// <summary>
    /// The rows in columns: whole groups, as many as fit in the free space in the first
    /// and the rest in a second beside it. When comparing, the NOW / NEW heads share a
    /// column's first line if that line is a group heading, and take a line of their own
    /// otherwise.
    /// </summary>
    static List<List<Row>> Columns(bool compare)
    {
        var blocks = new List<List<Row>>();
        foreach (var r in _rows)
        {
            if (r.Head || blocks.Count == 0) blocks.Add([]);
            blocks[^1].Add(r);
        }
        static int Lines(List<Row> col, bool compare) => col.Count + (compare && col.Count > 0 && !col[0].Head ? 1 : 0);

        var all = blocks.SelectMany(b => b).ToList();
        if (Lines(all, compare) <= MaxLines) return [all];
        var first = new List<Row>();
        int k = 0;
        while (k < blocks.Count && (first.Count == 0 || Lines([.. first, .. blocks[k]], compare) <= MaxLines))
            first.AddRange(blocks[k++]);
        var second = blocks.Skip(k).SelectMany(b => b).ToList();
        return second.Count > 0 ? [first, second] : [first];
    }

    /// <summary>The stats window at the top right: "NO CHANGE", or the rows in one or two columns.</summary>
    static void Draw(CpuContext c, IMemory m)
    {
        int margin = ScreenOverlay.WideMargin;
        bool compare = _prompt;
        int numW = MenuDraw.Digits * MenuDraw.Advance;
        int labelW = 6 * MenuDraw.Advance + Indent;   // POISON, the longest label, indented
        int colW = labelW + 8 + (compare ? numW + 6 : 0) + numW;
        int right = 320 + margin - 5;

        if (compare && _rows.Count == 0)
        {
            int w = 9 * MenuDraw.Advance + 2 * Pad;
            MenuDraw.Text(c, m, right - w + Pad, Top + Pad, "NO CHANGE");
            MenuDraw.Window(c, m, right - w, Top, w, RowH + 2 * Pad - 2);
            return;
        }

        var cols = Columns(compare);
        if (cols.Count == 1) colW = Math.Max(colW, MenuDraw.SeamlessWidth - 2 * Pad);
        int width = 2 * Pad + cols.Count * colW + (cols.Count - 1) * ColumnGap * 2;
        int x0 = right - width, bottom = Top;
        for (int ci = 0; ci < cols.Count; ci++)
        {
            int x = x0 + Pad + ci * (colW + ColumnGap * 2);
            int valueX = x + colW - numW, nowX = valueX - numW - 6;
            int y = Top + Pad;
            var col = cols[ci];
            for (int i = 0; i < col.Count; i++)
            {
                var r = col[i];
                if (compare && i == 0)
                {
                    MenuDraw.Text(c, m, nowX + numW - 3 * MenuDraw.Advance, y, "NOW");
                    MenuDraw.Text(c, m, valueX + numW - 3 * MenuDraw.Advance, y, "NEW");
                    if (!r.Head) y += RowH;
                }
                if (r.Head) MenuDraw.Text(c, m, x, y, r.Text);
                else
                {
                    MenuDraw.Text(c, m, x + Indent, y, r.Text);
                    if (compare)
                    {
                        MenuDraw.Number(c, m, nowX, y, r.Now, MenuDraw.Plain);
                        MenuDraw.Number(c, m, valueX, y, r.New,
                            r.New > r.Now ? MenuDraw.Better : r.New < r.Now ? MenuDraw.Worse : MenuDraw.Plain);
                    }
                    else MenuDraw.Number(c, m, valueX, y, r.New, r.New < 0 ? MenuDraw.Worse : MenuDraw.Plain);
                }
                y += RowH;
            }
            bottom = Math.Max(bottom, y);
        }

        MenuDraw.Window(c, m, x0, Top, width, bottom - Top + Pad - 2);
        _winLeft = x0; _winRight = x0 + width; _winBottom = Top + Math.Max(bottom - Top + Pad - 2, MenuDraw.MinWindowHeight);
    }
}
