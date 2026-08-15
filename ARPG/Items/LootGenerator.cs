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
        if (_rng.NextDouble() < table.EnchantScrollDropChance)
        {
            var enchant = GenerateEnchantScrollItem();
            if (enchant != null) drops.Add(enchant);
        }
        return drops;
    }

    /// <summary>
    /// Roll an item's prefix/suffix slot caps. Total is weighted: 5 is the standard,
    /// below 5 is uncommon, 8 is extremely rare. The total splits roughly evenly
    /// between prefixes and suffixes with occasional lopsided items.
    /// </summary>
    public (int maxPrefixes, int maxSuffixes) RollSlots()
    {
        // total:  3    4    5    6    7    8
        // weight: 2    8   55   22   10    3
        int roll = _rng.Next(100);
        int total = roll switch
        {
            < 2 => 3,
            < 10 => 4,
            < 65 => 5,
            < 87 => 6,
            < 97 => 7,
            _ => 8,
        };
        int prefixes = total / 2;
        int suffixes = total - prefixes;
        if ((total & 1) == 1 && _rng.Next(2) == 0) (prefixes, suffixes) = (suffixes, prefixes);
        // Occasional lopsided split, always leaving at least one slot per side.
        if (_rng.Next(6) == 0)
        {
            if (_rng.Next(2) == 0 && prefixes > 1) { prefixes--; suffixes++; }
            else if (suffixes > 1) { suffixes--; prefixes++; }
        }
        return (prefixes, suffixes);
    }

    public ItemInstance GenerateEquipment(LootTable table, int itemLevel, ItemRarity? forcedRarity = null)
    {
        var itemBase = PickEquipmentBase(table, itemLevel);
        if (itemBase == null) return null;
        var rarity = forcedRarity ?? RollRarity(table);
        if (itemBase.Category == ItemCategory.Flask) rarity = ItemRarity.Normal; // no mods on flasks
        return Generate(itemBase, itemLevel, rarity);
    }

    /// <summary>Generate a concrete item from a base: rolled slot caps, then rolled affixes.
    /// Blue (magic) items hold at most 2 modifiers; gold (rare) items are limited only by
    /// their rolled slots.</summary>
    public ItemInstance Generate(ItemBase itemBase, int itemLevel, ItemRarity rarity, int? forcedModifierCount = null)
    {
        var (maxPrefixes, maxSuffixes) = RollSlots();
        var item = new ItemInstance
        {
            BaseItemId = itemBase.Id,
            ItemLevel = itemLevel,
            Rarity = rarity,
            BaseModifierLimit = itemBase.BaseModifierLimit,
            MaxPrefixes = maxPrefixes,
            MaxSuffixes = maxSuffixes,
            FlaskCharges = itemBase.FlaskChargesMax, // dropped flasks come full
        };

        int desired = forcedModifierCount ?? rarity switch
        {
            ItemRarity.Normal => 0,
            ItemRarity.Magic => _rng.Next(1, EnchantSystem.MagicModifierCap + 1),
            ItemRarity.Rare => _rng.Next(3, 6),    // 3-5, clamped by the item's slots
            _ => 0,
        };
        if (rarity == ItemRarity.Magic) desired = Math.Min(desired, EnchantSystem.MagicModifierCap);

        RollModifiers(item, itemBase, desired);
        return item;
    }

    /// <summary>
    /// Roll up to `desired` additional modifiers onto an existing item, respecting the
    /// item's own prefix/suffix slot caps (plus any flexible "Expanded" bonus slots —
    /// by design there is no universal cap, only per-item capacity).
    /// </summary>
    public void RollModifiers(ItemInstance item, ItemBase itemBase, int desired)
    {
        for (int i = 0; i < desired; i++)
        {
            bool prefixOpen = item.CanAddAffix(_data, AffixType.Prefix);
            bool suffixOpen = item.CanAddAffix(_data, AffixType.Suffix);
            if (!prefixOpen && !suffixOpen) break;
            AffixType affix = prefixOpen && suffixOpen
                ? (_rng.Next(2) == 0 ? AffixType.Prefix : AffixType.Suffix)
                : (prefixOpen ? AffixType.Prefix : AffixType.Suffix);
            if (!TryRollAffix(item, itemBase, affix))
            {
                // One side ran out of compatible modifier groups — fall back to the
                // other side before giving up entirely.
                var other = affix == AffixType.Prefix ? AffixType.Suffix : AffixType.Prefix;
                if (!item.CanAddAffix(_data, other) || !TryRollAffix(item, itemBase, other)) break;
            }
        }
    }

    /// <summary>Roll one modifier of the given affix type onto the item (group-exclusive,
    /// item-level gated). Returns false when no compatible modifier exists.
    /// The caller is responsible for slot checks.</summary>
    public bool TryRollAffix(ItemInstance item, ItemBase itemBase, AffixType affix)
    {
        var usedGroups = item.Modifiers
            .Select(r => _data.Modifiers.GetValueOrDefault(r.ModifierId)?.ModifierGroup)
            .Where(g => g != null)
            .ToHashSet();

        var candidates = _data.Modifiers.Values.Where(m =>
            m.AffixType == affix &&
            m.CompatibleWith(itemBase.Category) &&
            m.MinimumItemLevel <= item.ItemLevel &&
            !usedGroups.Contains(m.ModifierGroup)).ToList();

        var pick = WeightedPick(candidates, m => m.Weight);
        if (pick == null) return false;

        float value = pick.MinimumValue + (float)_rng.NextDouble() * (pick.MaximumValue - pick.MinimumValue);
        item.Modifiers.Add(new ItemModifierRoll { ModifierId = pick.Id, Value = MathF.Round(value) });
        return true;
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

    public ItemInstance GenerateEnchantScrollItem(string baseId = null)
    {
        ItemBase pick;
        if (baseId != null)
            pick = _data.Items.GetValueOrDefault(baseId);
        else
        {
            var bases = _data.Items.Values.Where(b => b.Category == ItemCategory.EnchantScroll).ToList();
            pick = WeightedPick(bases, b => b.DropWeight);
        }
        if (pick == null) return null;
        return new ItemInstance
        {
            BaseItemId = pick.Id,
            ItemLevel = 1,
            Rarity = ItemRarity.Normal,
            BaseModifierLimit = 0,
            StackCount = 1,
        };
    }

    /// <summary>Trash filter: bases whose required level sits more than this many levels
    /// below the drop's item level stop appearing — late zones drop CURRENT gear, not
    /// leather hoods. (Falls back to the full pool if the window would empty it.)</summary>
    public const int BaseLevelWindow = 25;

    private ItemBase PickEquipmentBase(LootTable table, int itemLevel)
    {
        int ilvl = Math.Max(1, itemLevel);
        var candidates = _data.Items.Values.Where(b =>
            b.IsEquippable && b.RequiredLevel <= ilvl).ToList();
        var current = candidates.Where(b => b.RequiredLevel >= ilvl - BaseLevelWindow).ToList();
        if (current.Count > 0) candidates = current;
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
