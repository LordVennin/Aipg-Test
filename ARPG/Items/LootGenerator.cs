using ARPG.Data;
using ARPG.Stats;

namespace ARPG.Items;

/// <summary>
/// Authoritative loot generation (runs ONLY on the server/host). Pipeline:
/// loot table -> drop roll -> base selection -> item level -> rarity -> desired modifier
/// count -> clamp against the item's own ModifierLimit -> affix selection -> value rolls.
/// Once generated, rolls are final; clients receive the serialized result and never reroll.
/// </summary>
public class LootGenerator
{
    private readonly GameData _data;
    private readonly Random _rng;

    public LootGenerator(GameData data, Random rng = null)
    {
        _data = data;
        _rng = rng ?? new Random();
    }

    /// <summary>Roll the drops for one enemy kill. May return zero, one or two items.</summary>
    public List<ItemInstance> RollDrops(string lootTableId, int itemLevel)
    {
        var table = _data.GetLootTable(lootTableId);
        var drops = new List<ItemInstance>();

        if (_rng.NextDouble() < table.DropChance)
        {
            var item = GenerateEquipment(table, itemLevel);
            if (item != null) drops.Add(item);
        }
        if (_rng.NextDouble() < table.ScrollDropChance)
        {
            var scroll = GenerateScrollItem();
            if (scroll != null) drops.Add(scroll);
        }
        return drops;
    }

    public ItemInstance GenerateEquipment(LootTable table, int itemLevel, ItemRarity? forcedRarity = null)
    {
        var itemBase = PickEquipmentBase(table, itemLevel);
        if (itemBase == null) return null;
        var rarity = forcedRarity ?? RollRarity(table);
        return Generate(itemBase, itemLevel, rarity);
    }

    /// <summary>Generate a concrete item from a base with rolled affixes.</summary>
    public ItemInstance Generate(ItemBase itemBase, int itemLevel, ItemRarity rarity, int? forcedModifierCount = null)
    {
        var item = new ItemInstance
        {
            BaseItemId = itemBase.Id,
            ItemLevel = itemLevel,
            Rarity = rarity,
            BaseModifierLimit = itemBase.BaseModifierLimit,
        };

        int desired = forcedModifierCount ?? rarity switch
        {
            ItemRarity.Normal => 0,
            ItemRarity.Magic => _rng.Next(1, 3),   // 1-2
            ItemRarity.Rare => _rng.Next(3, 6),    // 3-5
            _ => 0,
        };

        RollModifiers(item, itemBase, desired);
        return item;
    }

    /// <summary>
    /// Roll up to `desired` additional modifiers onto an existing item, clamped against the
    /// item's CURRENT modifier limit (which itself may grow if an "Expanded"-style affix that
    /// raises ModifierLimit is rolled mid-generation — by design, no universal cap exists).
    /// </summary>
    public void RollModifiers(ItemInstance item, ItemBase itemBase, int desired)
    {
        for (int i = 0; i < desired; i++)
        {
            if (item.Modifiers.Count >= item.CurrentModifierLimit(_data)) break;

            var usedGroups = item.Modifiers
                .Select(r => _data.Modifiers.GetValueOrDefault(r.ModifierId)?.ModifierGroup)
                .Where(g => g != null)
                .ToHashSet();

            var candidates = _data.Modifiers.Values.Where(m =>
                m.CompatibleWith(itemBase.Category) &&
                m.MinimumItemLevel <= item.ItemLevel &&
                !usedGroups.Contains(m.ModifierGroup)).ToList();

            var pick = WeightedPick(candidates, m => m.Weight);
            if (pick == null) break;

            float value = pick.MinimumValue + (float)_rng.NextDouble() * (pick.MaximumValue - pick.MinimumValue);
            item.Modifiers.Add(new ItemModifierRoll { ModifierId = pick.Id, Value = MathF.Round(value) });
        }
    }

    public ItemInstance GenerateScrollItem()
    {
        var scrollBases = _data.Items.Values.Where(b => b.Category == ItemCategory.SkillScroll).ToList();
        var pick = WeightedPick(scrollBases, b => b.DropWeight);
        if (pick == null) return null;
        return new ItemInstance
        {
            BaseItemId = pick.Id,
            ItemLevel = 1,
            Rarity = ItemRarity.Normal,
            BaseModifierLimit = 0,
        };
    }

    private ItemBase PickEquipmentBase(LootTable table, int itemLevel)
    {
        var candidates = _data.Items.Values.Where(b =>
            b.Category != ItemCategory.SkillScroll && b.RequiredLevel <= Math.Max(1, itemLevel)).ToList();
        return WeightedPick(candidates, b =>
        {
            int catWeight = table.CategoryWeights.GetValueOrDefault(b.Category, 100);
            return Math.Max(0, b.DropWeight * catWeight / 100);
        });
    }

    private ItemRarity RollRarity(LootTable table)
    {
        int total = table.RarityWeightNormal + table.RarityWeightMagic + table.RarityWeightRare;
        if (total <= 0) return ItemRarity.Normal;
        int roll = _rng.Next(total);
        if (roll < table.RarityWeightNormal) return ItemRarity.Normal;
        if (roll < table.RarityWeightNormal + table.RarityWeightMagic) return ItemRarity.Magic;
        return ItemRarity.Rare;
    }

    private T WeightedPick<T>(IReadOnlyList<T> items, Func<T, int> weight) where T : class
    {
        int total = 0;
        foreach (var item in items) total += Math.Max(0, weight(item));
        if (total <= 0) return null;
        int roll = _rng.Next(total);
        foreach (var item in items)
        {
            roll -= Math.Max(0, weight(item));
            if (roll < 0) return item;
        }
        return items[^1];
    }
}
