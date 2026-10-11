using RecompOne.Runtime.Memory;

namespace KfIII;

/// <summary>
/// The equipment effects the game never shows: those hard-coded for particular item
/// ids in GAME.EXE, and the regeneration and drain periods in the item records. Shared
/// by the equipment panel's SPECIAL box (GearSpecial, through GearCompare) and the
/// ailment box of the status screen's extended stats (StatProgress). The list comes
/// from a sweep of every read of the equipped slots in GAME.EXE, each id compare
/// followed up; NOTES.md "Hidden gear effects" has the sites.
/// </summary>
public static class GearEffects
{
    /// <summary>World ticks a second: periods and ailment lengths are counted in ticks.</summary>
    public const float TicksPerSecond = 15f;

    // Weapon records (0x44 bytes): +3 and +4 the weapon's two spells (index into the
    // spell records, 0xFF none), +0x16 HP and +0x18 MP regeneration periods.
    // Armour records (0x20 bytes): +0x14 HP gain, +0x16 HP drain, +0x18 MP gain and
    // +0x1A MP drain periods. A period is in ticks, 0 for none.
    // Spell records (24 bytes): +5 the element bits (1 fire, 2 earth, 4 water, 8 wind, 0x10 holy).
    const uint SpellStride = 24;
    const uint PoisonDefense = GameAddr.Defense + 0x10;

    const int LastItem = 0x5C, LastWeapon = 0x21;

    static string Secs(float ticks) => ticks / TicksPerSecond is var s && s >= 10 ? $"{s:F0}s" : $"{s:F1}s";

    // Effects tied to an item id; the sites are in NOTES.md.
    static readonly Dictionary<int, string[]> _fixed = new()
    {
        [0x16] = ["Cursed: 1 in 8 chance every 10s", "to paralyze you (13s)"],   // Evil Crescent
        [0x1C] = ["Each shot costs 3-7 HP", "(it can kill you)"],                // Elchris Bow
        [0x1F] = ["Heals 1-9 HP per hit", "Can't cast spells"],                  // Blood Star
        [0x20] = ["Kills undead in one hit"],                                    // Silence Rod
        [0x25] = ["Spells charge 25% faster"],                                   // Orladin's Hood
        [0x26] = ["Spells cost double MP"],                                      // Groundal Crown
        [0x27] = ["25% chance to block physical hits"],                          // Dragon Head
        [0x29] = ["Curse, darkness, slow: 1/4 as long"],                         // Ichrius' Helm
        [0x2D] = ["Immune to fire damage"],                                      // Fire Mail
        [0x2E] = ["Spells cost half MP"],                                        // Orladin's Mail
        [0x30] = ["25% chance to block physical hits", "Spells charge half as fast"],   // Groundal Mail
        [0x31] = ["25% chance to block physical hits"],                          // Ichrius' Armor
        [0x35] = ["+1 MP every 60s on bare ground"],                             // Earth Shield
        [0x3A] = ["25% chance to block magic", "Dims your vision"],              // Groundal Shield
        [0x3C] = ["25% chance to block magic"],                                  // Ichrius' Shield
        [0x41] = ["Paralysis 13s -> 10s"],                                       // Silver Arms
        [0x42] = ["Swings charge as if the", "weapon were 25% lighter"],
        [0x44] = ["Swings charge as if the", "weapon were 50% heavier"],
        [0x46] = ["Swings charge as if the", "weapon were half the weight"],
        [0x4A] = ["Poison 60s -> 45s"],
        [0x4C] = ["Can't run", "Curse, darkness, slow: 1/2 as long", "Paralysis: 1/2 as long"],   // Groundal Boots
        [0x4D] = ["No recharge pause after running"],
        [0x4F] = ["No recharge pause after running", "Paralysis, poison: 1/4 as long"],   // Ichrius' Boots
        [0x52] = ["Cursed: randomly poisons you", "(about once every 17s)"],     // Evil Ring
        [0x53] = ["Poison: 1/4 as long"],
        [0x55] = ["50% chance to block magic"],                                  // Ebony Bracelet
        [0x56] = ["Paralysis: 1/4 as long"],                                     // Gold Ring
        [0x58] = ["Curse, darkness, slow: 1/2 as long"],                         // Globe Amulet
        [0x5B] = ["Curse, darkness, slow: 1/4 as long"],                         // Luster Wing
    };

    // The Promeus Helm, Armor, Shield, Hands and Boots, by slot: all five worn and no
    // physical damage gets through. kf_PlayerHurt tests the head..arms bytes as one word
    // (0x453B2F28) and the feet byte (0x4E) at 0x8002AD48.
    static readonly int[] _promeus = [0x28, 0x2F, 0x3B, 0x45, 0x4E];
    static readonly uint[] _promeusSlots = [EquipSlot.Head, EquipSlot.Body, EquipSlot.Shield, EquipSlot.Arms, EquipSlot.Feet];

    // By element bit, low bit first.
    static readonly string[] _elementNames = ["fire", "earth", "water", "wind", "holy"];

    /// <summary>The SPECIAL lines for one item id: its fixed effects, regeneration and weapon magic.</summary>
    public static List<string> Lines(IMemory m, int id)
    {
        var l = new List<string>();
        if (id < 0 || id > LastItem) return l;
        if (_fixed.TryGetValue(id, out var f)) l.AddRange(f);
        int pi = Array.IndexOf(_promeus, id);
        if (pi >= 0)
        {
            int worn = 0;
            for (int k = 0; k < 5; k++) if (m.ReadU8(_promeusSlots[k]) == _promeus[k] || k == pi) worn++;
            l.Add($"Full Promeus set: immune to");
            l.Add($"physical damage ({worn}/5 with this)");
        }
        if (id <= LastWeapon)
        {
            uint rec = GameAddr.WeaponRecords + (uint)id * 0x44;
            Regen(l, m.ReadU16(rec + 0x16), "HP", +1);
            Regen(l, m.ReadU16(rec + 0x18), "MP", +1);
            var els = new List<string>();
            foreach (uint o in new uint[] { 3, 4 })
            {
                int s = m.ReadU8(rec + o);
                if (s == 0xFF) continue;
                string e = Element(m.ReadU8(GameAddr.SpellRecords + (uint)s * SpellStride + 5));
                if (!els.Contains(e)) els.Add(e);
            }
            if (els.Count > 0)
            {
                l.Add($"Weapon magic: {string.Join(", ", els)}");
                // A weapon spell works only once its spell record's +0 is set. Veyrac in
                // Cason sets it for Ryu-ga, Capricorn, Anorium, Vaculacia and Morguline
                // (fdat20, case 4); Alexander in Verdite Castle, after Lyn's Ring at the
                // fountain, for the Excellector's last form, Seath's Sword, Dark Slayer,
                // Triple Fang and the Moonlight Swords (fdat26, 0x801E8818).
                // kf_WeaponMagicReady also wants PWR and every magic power at 50 or more
                // for the first group.
                bool high = id == 2 || id is >= 0x0D and <= 0x11;
                l.Add(high ? " needs weapon magic level 2 (Alexander)" : " needs weapon magic level 1 (Veyrac),");
                if (!high) l.Add("  and PWR and all magic 50+");
            }
        }
        else
        {
            uint rec = GameAddr.ArmourRecords + (uint)id * 0x20;
            Regen(l, m.ReadU16(rec + 0x14), "HP", +1);
            Regen(l, m.ReadU16(rec + 0x16), "HP", -1);
            Regen(l, m.ReadU16(rec + 0x18), "MP", +1);
            Regen(l, m.ReadU16(rec + 0x1A), "MP", -1);
        }
        return l;
    }

    static void Regen(List<string> l, int period, string what, int sign)
    {
        if (period <= 0) return;
        l.Add($"{(sign > 0 ? "+" : "-")}1 {what} every {Secs(period)}");
    }

    /// <summary>A spell's elements in words, from its element bits.</summary>
    static string Element(int bits)
    {
        if ((bits & 0x0F) == 0x0F) return "all four elements";
        var n = new List<string>();
        for (int i = 0; i < 5; i++) if ((bits & (1 << i)) != 0) n.Add(_elementNames[i]);
        return n.Count == 0 ? "none" : string.Join("/", n);
    }

    // ---- totals for what is worn now ---------------------------------------------

    public struct Totals
    {
        public float Curse, Dark, Poison, Paralysis, Slow;   // seconds
        public int PoisonChance;                             // percent chance a poisonous hit poisons
        public float BlockPhysical, BlockMagic;              // chance 0..1 a hit's part is blocked
        public bool FireImmune;
    }

    /// <summary>
    /// Ailment lengths and block chances for the gear worn now, worked out as
    /// kf_PlayerHurt does (0x8002ABB4..0x8002AE90).
    /// </summary>
    public static Totals Worn(IMemory m)
    {
        int head = m.ReadU8(EquipSlot.Head), body = m.ReadU8(EquipSlot.Body), shield = m.ReadU8(EquipSlot.Shield),
            arms = m.ReadU8(EquipSlot.Arms), feet = m.ReadU8(EquipSlot.Feet),
            i1 = m.ReadU8(EquipSlot.Item1), i2 = m.ReadU8(EquipSlot.Item2);
        bool Item(int id) => i1 == id || i2 == id;
        // Lengths in ticks.
        int curse = 1350, dark = 2700, poison = 900, para = 200, slow = 450;
        if (arms == 0x41) para = 150;                                          // Silver Arms
        if (feet == 0x4A) poison = 675;
        if (feet == 0x4C) { para >>= 1; dark = 1350; curse = 675; slow = 225; }   // Groundal Boots
        if (Item(0x53)) poison >>= 2;
        if (Item(0x56)) para >>= 2;                                            // Gold Ring
        if (Item(0x58)) { dark >>= 1; curse >>= 1; slow >>= 1; }               // Globe Amulet
        if (Item(0x5B)) { dark >>= 2; curse >>= 2; slow >>= 2; }               // Luster Wing
        if (head == 0x29) { dark >>= 2; curse >>= 2; slow >>= 2; }             // Ichrius' Helm
        if (feet == 0x4F) { para >>= 2; poison >>= 2; }                        // Ichrius' Boots

        float phys = 1f, magic = 1f;   // the chance a hit's part gets through
        bool promeus = head == 0x28 && body == 0x2F && shield == 0x3B && arms == 0x45 && feet == 0x4E;
        if (head == 0x27) phys *= 0.75f;                 // Dragon Head
        if (promeus) phys = 0f;
        if (body is 0x30 or 0x31) phys *= 0.75f;         // Groundal Mail, Ichrius' Armor
        if (shield is 0x3A or 0x3C) magic *= 0.75f;      // Groundal and Ichrius' Shields
        if (Item(0x55)) magic *= 0.5f;                   // Ebony Bracelet

        // Poison takes when the poison defense is below a roll of 0..99 (0x8002AF28).
        int def = m.ReadU16(PoisonDefense);
        return new Totals
        {
            Curse = curse / TicksPerSecond,
            Dark = dark / TicksPerSecond,
            Poison = poison / TicksPerSecond,
            Paralysis = para / TicksPerSecond,
            Slow = slow / TicksPerSecond,
            PoisonChance = Math.Clamp(99 - def, 0, 100),
            BlockPhysical = 1f - phys,
            BlockMagic = 1f - magic,
            FireImmune = body == 0x2D,                   // Fire Mail
        };
    }
}
