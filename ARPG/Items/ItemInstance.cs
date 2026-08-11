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

    /// <summary>Legacy total capacity (pre slot-split saves); still used to migrate old items.</summary>
    public int BaseModifierLimit { get; set; } = 6;

    /// <summary>Per-item modifier slot caps, rolled at generation time (3-8 total across both,
    /// 5 being typical). These — not rarity — are the hard capacity of the item.</summary>
    public int MaxPrefixes { get; set; }
    public int MaxSuffixes { get; set; }

    /// <summary>Sealed items (Scroll of Sealing) can never be enchanted again.</summary>
    public bool Locked { get; set; }

    /// <summary>Stack size for stackable items (Enchanting Scrolls); 1 for everything else.</summary>
    public int StackCount { get; set; } = 1;

    public ItemBase GetBase(GameData data) => data.Items[BaseItemId];

    /// <summary>Flexible bonus slots from rolled ModifierLimit stats (the "Expanded" prefix);
    /// usable by either affix type, on top of the per-side caps.</summary>
    public int ModifierLimitBonus(GameData data)
    {
        float bonus = 0;
        foreach (var roll in Modifiers)
            if (data.Modifiers.TryGetValue(roll.ModifierId, out var def) && def.StatAffected == StatType.ModifierLimit)
                bonus += roll.Value;
        return (int)bonus;
    }

    /// <summary>Prefix slots + suffix slots + flexible bonuses = current total Modifier Limit.</summary>
    public int CurrentModifierLimit(GameData data) => MaxPrefixes + MaxSuffixes + ModifierLimitBonus(data);

    /// <summary>Whether another affix of the given type fits: the side cap (+ flex bonus) and
    /// the total cap must both allow it, and the item must not be Sealed.</summary>
    public bool CanAddAffix(GameData data, AffixType type)
    {
        if (Locked) return false;
        int bonus = ModifierLimitBonus(data);
        if (Modifiers.Count >= CurrentModifierLimit(data)) return false;
        int sideCount = CountAffixes(data, type);
        int sideMax = (type == AffixType.Prefix ? MaxPrefixes : MaxSuffixes) + bonus;
        return sideCount < sideMax;
    }

    /// <summary>Old saves have no per-side slots; derive them from the legacy total once.</summary>
    public void EnsureSlotData()
    {
        if (MaxPrefixes + MaxSuffixes <= 0)
        {
            int total = Math.Max(2, BaseModifierLimit);
            MaxPrefixes = (total + 1) / 2;
            MaxSuffixes = total / 2;
        }
        if (StackCount < 1) StackCount = 1;
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
        return Math.Max(1, (int)MathF.Round(value)) * Math.Max(1, StackCount);
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
