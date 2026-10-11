namespace KfIII;

/// <summary>
/// The equipped-item bytes, one per slot, each an item id or 0xFF when the slot is
/// empty: the weapon at GameAddr.WeaponSlot and the seven others at GameAddr.ArmourSlots,
/// in that table's order. Item ids run by slot (GameAddr.ItemNames order): weapons
/// 0x00..0x21, head to 0x29, body to 0x32, shield to 0x3E, arms to 0x47, feet to 0x50,
/// the two item slots to 0x5C.
/// </summary>
static class EquipSlot
{
    public const uint Weapon = GameAddr.WeaponSlot;
    public const uint Head = GameAddr.ArmourSlots, Body = Head + 1, Shield = Head + 2, Arms = Head + 3,
        Feet = Head + 4, Item1 = Head + 5, Item2 = Head + 6;
}
