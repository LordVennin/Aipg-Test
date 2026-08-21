using ARPG.Stats;

namespace ARPG.Items;

public enum ItemCategory
{
    Mace,
    Staff,
    Shield,
    Helmet,
    BodyArmor,
    Gloves,
    Boots,
    Belt,
    Amulet,
    Ring,
    SkillScroll,
    EnchantScroll,
    /// <summary>Potion flasks: equippable restore-over-time drinkables (two flask slots).</summary>
    Flask,
    /// <summary>Two-handed ranged weapons; pair with a Quiver in the off-hand.</summary>
    Bow,
    /// <summary>Off-hand companions to bows (the one off-hand a two-handed bow allows).</summary>
    Quiver,
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
    Flask1,
    Flask2,
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

    /// <summary>Attribute requirements to EQUIP (0 = none). Checked server-side against
    /// the character's computed attributes and shown in tooltips like the level.</summary>
    public int RequiredStrength { get; set; }
    public int RequiredDexterity { get; set; }
    public int RequiredIntelligence { get; set; }

    /// <summary>Base modifier capacity of items generated from this base. NOT a global constant —
    /// each item's effective limit = BaseModifierLimit + any ModifierLimit stat rolled on the item.</summary>
    public int BaseModifierLimit { get; set; } = 6;

    /// <summary>Implicit/base stats, e.g. weapon damage, staff spell damage, armor's armor value.</summary>
    public Dictionary<StatType, float> BaseStats { get; set; } = new();

    /// <summary>Relative chance for the loot generator to pick this base within its category.</summary>
    public int DropWeight { get; set; } = 100;

    /// <summary>Base gold value before modifiers/rarity are factored in.</summary>
    public int BaseGoldValue { get; set; } = 8;

    /// <summary>Weapons only: a two-handed weapon (staffs) occupies both hands — nothing can be
    /// equipped in the off-hand alongside it. One-handed weapons (maces) leave the off-hand free
    /// for a shield. Data-driven so future weapon types choose per base.</summary>
    public bool TwoHanded { get; set; }

    /// <summary>Only for Category == SkillScroll: which scroll definition this item carries.</summary>
    public string ScrollId { get; set; }

    /// <summary>Only for Category == EnchantScroll: which crafting behavior this scroll applies.</summary>
    public EnchantType EnchantType { get; set; } = EnchantType.None;

    /// <summary>Maximum stack size in one inventory slot (1 = not stackable).</summary>
    public int MaxStack { get; set; } = 1;

    // Flask stats (Category == Flask): the drink restores this much over FlaskDuration
    // seconds (one of Heal/Mana per flask), from a supply of FlaskChargesMax charges.
    // Charges do NOT regenerate — the sanctum fountain refills them.
    public float FlaskHeal { get; set; }
    public float FlaskMana { get; set; }
    public int FlaskChargesMax { get; set; } = 3;
    public float FlaskDuration { get; set; } = 4f;

    /// <summary>Accent color (RRGGBB hex) for the procedural sprite: held-weapon metal /
    /// staff orb, and for worn armor the garment color painted onto the body rig.
    /// Falls back to a per-category default.</summary>
    public string SpriteColor { get; set; }

    /// <summary>Worn-armor overlay silhouette painted onto the body rig. BodyArmor:
    /// "cloth" (full robe), "leather", "mail", "plate". Helmet: "hood", "cowl", "cap",
    /// "helm". Null/unknown = no visible overlay (small slots recolor rig pixels via
    /// SpriteColor instead).</summary>
    public string ArmorStyle { get; set; }

    public string Description { get; set; }

    public bool IsWeapon => Category is ItemCategory.Mace or ItemCategory.Staff or ItemCategory.Bow;
    /// <summary>Items rendered in the character's hands (weapons and shields).</summary>
    public bool IsHandheld => IsWeapon || Category == ItemCategory.Shield;
    public bool IsEquippable => Category is not (ItemCategory.SkillScroll or ItemCategory.EnchantScroll);
    /// <summary>Categories that can receive prefix/suffix modifiers (enchantable gear).
    /// Flasks carry only their base stats for now.</summary>
    public bool IsEnchantable => IsEquippable && Category != ItemCategory.Flask;

    /// <summary>Which equipment slots this category may occupy. Adding a new weapon category only
    /// requires extending this mapping and the data files — no inventory/skill system changes.</summary>
    public static IReadOnlyList<EquipSlot> CompatibleSlots(ItemCategory category) => category switch
    {
        ItemCategory.Mace => new[] { EquipSlot.MainHand },
        ItemCategory.Staff => new[] { EquipSlot.MainHand },
        ItemCategory.Bow => new[] { EquipSlot.MainHand },
        ItemCategory.Quiver => new[] { EquipSlot.OffHand },
        // Shields are one-handed and fit EITHER hand (off-hand preferred by quick-equip).
        ItemCategory.Shield => new[] { EquipSlot.OffHand, EquipSlot.MainHand },
        ItemCategory.Helmet => new[] { EquipSlot.Helmet },
        ItemCategory.BodyArmor => new[] { EquipSlot.BodyArmor },
        ItemCategory.Gloves => new[] { EquipSlot.Gloves },
        ItemCategory.Boots => new[] { EquipSlot.Boots },
        ItemCategory.Belt => new[] { EquipSlot.Belt },
        ItemCategory.Amulet => new[] { EquipSlot.Amulet },
        ItemCategory.Ring => new[] { EquipSlot.Ring1, EquipSlot.Ring2 },
        ItemCategory.Flask => new[] { EquipSlot.Flask1, EquipSlot.Flask2 },
        _ => Array.Empty<EquipSlot>(),
    };
}
