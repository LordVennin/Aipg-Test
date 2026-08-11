using ARPG.Data;
using ARPG.Stats;

namespace ARPG.Items;

/// <summary>
/// A concrete generated item. References its ItemBase by id; rolls are stable once generated
/// (serialization preserves them exactly — clients and saves never reroll).
/// </summary>
public class ItemInstance
{
    public Guid InstanceId { get; set; } = Guid.NewGuid();
    public string BaseItemId { get; set; }
    public int ItemLevel { get; set; } = 1;
    public ItemRarity Rarity { get; set; } = ItemRarity.Normal;
    public List<ItemModifierRoll> Modifiers { get; set; } = new();

    /// <summary>Base modifier capacity copied from the base at generation time. The effective
    /// limit adds any ModifierLimit stat rolled on this item — there is NO universal cap.</summary>
    public int BaseModifierLimit { get; set; } = 6;

    public ItemBase GetBase(GameData data) => data.Items[BaseItemId];

    /// <summary>Base Modifier Limit + Modifier Limit bonuses = current Modifier Limit.</summary>
    public int CurrentModifierLimit(GameData data)
    {
        float bonus = 0;
        foreach (var roll in Modifiers)
            if (data.Modifiers.TryGetValue(roll.ModifierId, out var def) && def.StatAffected == StatType.ModifierLimit)
                bonus += roll.Value;
        return BaseModifierLimit + (int)bonus;
    }

    public int CountAffixes(GameData data, AffixType type)
    {
        int n = 0;
        foreach (var roll in Modifiers)
            if (data.Modifiers.TryGetValue(roll.ModifierId, out var def) && def.AffixType == type)
                n++;
        return n;
    }

    /// <summary>Display name, e.g. "Brutal Iron Mace of Haste" for magic items.</summary>
    public string DisplayName(GameData data)
    {
        var baseDef = GetBase(data);
        if (Rarity == ItemRarity.Normal || Modifiers.Count == 0) return baseDef.Name;
        if (Rarity == ItemRarity.Magic)
        {
            string prefix = null, suffix = null;
            foreach (var roll in Modifiers)
            {
                if (!data.Modifiers.TryGetValue(roll.ModifierId, out var def)) continue;
                if (def.AffixType == AffixType.Prefix) prefix ??= def.Name;
                else suffix ??= def.Name;
            }
            return $"{prefix} {baseDef.Name} {suffix}".Trim();
        }
        return $"Rare {baseDef.Name}";
    }

    /// <summary>
    /// Gold value of this item: base value + a contribution per rolled modifier (better
    /// rolls within a modifier's range are worth more), scaled by rarity and item level.
    /// Deterministic — the same item is always worth the same amount.
    /// </summary>
    public int GoldValue(GameData data)
    {
        var itemBase = GetBase(data);
        float value = itemBase.BaseGoldValue;
        foreach (var roll in Modifiers)
        {
            if (!data.Modifiers.TryGetValue(roll.ModifierId, out var def)) continue;
            float range = MathF.Max(0.001f, def.MaximumValue - def.MinimumValue);
            float quality = Math.Clamp((roll.Value - def.MinimumValue) / range, 0f, 1f);
            value += 12f + 18f * quality;
        }
        float rarityMult = Rarity switch
        {
            ItemRarity.Magic => 1.4f,
            ItemRarity.Rare => 2.2f,
            _ => 1f,
        };
        value *= rarityMult * (1f + 0.05f * (ItemLevel - 1));
        return Math.Max(1, (int)MathF.Round(value));
    }

    /// <summary>Total stats granted by this item: base stats plus all rolled modifiers.</summary>
    public StatCollection TotalStats(GameData data)
    {
        var stats = new StatCollection();
        stats.AddAll(GetBase(data).BaseStats);
        foreach (var roll in Modifiers)
            if (data.Modifiers.TryGetValue(roll.ModifierId, out var def))
                stats.Add(def.StatAffected, roll.Value);
        return stats;
    }
}
