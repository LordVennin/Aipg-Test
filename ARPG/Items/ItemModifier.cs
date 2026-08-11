using ARPG.Stats;

namespace ARPG.Items;

public enum AffixType
{
    Prefix,
    Suffix,
}

/// <summary>
/// A prefix/suffix definition, loaded from Data/Modifiers/*.json.
/// Rolled instances only store the definition id and the rolled value.
/// </summary>
public class ItemModifier
{
    public string Id { get; set; }
    public string Name { get; set; }
    public AffixType AffixType { get; set; }
    public int MinimumItemLevel { get; set; } = 1;

    /// <summary>Item categories this affix can appear on.</summary>
    public List<ItemCategory> CompatibleItemCategories { get; set; } = new();

    /// <summary>No two modifiers of the same group can appear on one item.</summary>
    public string ModifierGroup { get; set; }

    public StatType StatAffected { get; set; }
    public float MinimumValue { get; set; }
    public float MaximumValue { get; set; }
    public int Weight { get; set; } = 100;

    /// <summary>Tier within its family (1 = weakest). Tiers share a ModifierGroup, so two
    /// tiers of the same modifier can never coexist on one item; MinimumItemLevel gates
    /// the strong tiers to high-level items (low tiers can always roll).</summary>
    public int Tier { get; set; } = 1;

    /// <summary>True for percent-increased stats — controls tooltip formatting only.</summary>
    public bool IsPercent { get; set; }

    public bool CompatibleWith(ItemCategory category) =>
        CompatibleItemCategories == null || CompatibleItemCategories.Count == 0 ||
        CompatibleItemCategories.Contains(category);

    public string DescribeRoll(float value)
    {
        string amount = IsPercent ? $"{value:0}%" : $"{value:0}";
        string statName = StatAffected switch
        {
            StatType.MaxHealth => "Maximum Health",
            StatType.LifeRegeneration => "Life Regeneration per second",
            StatType.MovementSpeed => "Movement Speed",
            StatType.PhysicalDamage => "Physical Damage",
            StatType.SpellDamage => "Spell Damage",
            StatType.AttackSpeed => "Attack Speed",
            StatType.CastSpeed => "Cast Speed",
            StatType.Armor => "Armor",
            StatType.FireResistance => "Fire Resistance",
            StatType.ColdResistance => "Cold Resistance",
            StatType.LightningResistance => "Lightning Resistance",
            StatType.AcidResistance => "Acid Resistance",
            StatType.DarkResistance => "Dark Resistance",
            StatType.LightResistance => "Light Resistance",
            StatType.AddedPhysicalDamage => "Added Physical Damage",
            StatType.AddedFireDamage => "Added Fire Damage",
            StatType.AddedColdDamage => "Added Cold Damage",
            StatType.AddedLightningDamage => "Added Lightning Damage",
            StatType.AddedAcidDamage => "Added Acid Damage",
            StatType.AddedDarkDamage => "Added Dark Damage",
            StatType.AddedLightDamage => "Added Light Damage",
            StatType.ModifierLimit => "Modifier Limit",
            _ => StatAffected.ToString(),
        };
        return $"+{amount} {statName}";
    }
}

/// <summary>A rolled affix on a generated item. Value is rolled once and never regenerated.</summary>
public class ItemModifierRoll
{
    public string ModifierId { get; set; }
    public float Value { get; set; }
}
