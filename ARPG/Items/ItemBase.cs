using ARPG.Stats;

namespace ARPG.Items;

public enum ItemCategory
{
    Mace,
    Staff,
    Helmet,
    BodyArmor,
    Gloves,
    Boots,
    Belt,
    Amulet,
    Ring,
    SkillScroll,
}

public enum ItemRarity
{
    Normal,
    Magic,
    Rare,
}

public enum EquipSlot
{
    Helmet,
    BodyArmor,
    Gloves,
    Boots,
    Belt,
    Amulet,
    Ring1,
    Ring2,
    MainHand,
    OffHand,
}

/// <summary>
/// The immutable definition of an item type, loaded from Data/Items/*.json.
/// Generated items (ItemInstance) reference this by Id and never copy it.
/// </summary>
public class ItemBase
{
    public string Id { get; set; }
    public string Name { get; set; }
    public ItemCategory Category { get; set; }
    public int InventoryWidth { get; set; } = 1;
    public int InventoryHeight { get; set; } = 1;
    public int RequiredLevel { get; set; } = 1;

    /// <summary>Base modifier capacity of items generated from this base. NOT a global constant —
    /// each item's effective limit = BaseModifierLimit + any ModifierLimit stat rolled on the item.</summary>
    public int BaseModifierLimit { get; set; } = 6;

    /// <summary>Implicit/base stats, e.g. weapon damage, staff spell damage, armor's armor value.</summary>
    public Dictionary<StatType, float> BaseStats { get; set; } = new();

    /// <summary>Relative chance for the loot generator to pick this base within its category.</summary>
    public int DropWeight { get; set; } = 100;

    /// <summary>Only for Category == SkillScroll: which scroll definition this item carries.</summary>
    public string ScrollId { get; set; }

    /// <summary>Accent color (RRGGBB hex) for the procedural held-weapon sprite
    /// (mace head metal / staff orb). Falls back to a per-category default.</summary>
    public string SpriteColor { get; set; }

    public string Description { get; set; }

    public bool IsWeapon => Category is ItemCategory.Mace or ItemCategory.Staff;
    public bool IsEquippable => Category != ItemCategory.SkillScroll;

    /// <summary>Which equipment slots this category may occupy. Adding a new weapon category only
    /// requires extending this mapping and the data files — no inventory/skill system changes.</summary>
    public static IReadOnlyList<EquipSlot> CompatibleSlots(ItemCategory category) => category switch
    {
        ItemCategory.Mace => new[] { EquipSlot.MainHand },
        ItemCategory.Staff => new[] { EquipSlot.MainHand },
        ItemCategory.Helmet => new[] { EquipSlot.Helmet },
        ItemCategory.BodyArmor => new[] { EquipSlot.BodyArmor },
        ItemCategory.Gloves => new[] { EquipSlot.Gloves },
        ItemCategory.Boots => new[] { EquipSlot.Boots },
        ItemCategory.Belt => new[] { EquipSlot.Belt },
        ItemCategory.Amulet => new[] { EquipSlot.Amulet },
        ItemCategory.Ring => new[] { EquipSlot.Ring1, EquipSlot.Ring2 },
        _ => Array.Empty<EquipSlot>(),
    };
}
