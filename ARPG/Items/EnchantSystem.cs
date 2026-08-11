using ARPG.Data;

namespace ARPG.Items;

/// <summary>The crafting behavior an Enchanting Scroll applies to a target item.</summary>
public enum EnchantType
{
    None,
    Awaken,          // white item: add 1 random prefix/suffix, item becomes Magic (blue)
    AddPrefixMagic,  // blue item: add a prefix (blue items cap at 2 total modifiers)
    AddSuffixMagic,  // blue item: add a suffix
    AddRandomRare,   // gold item: add a random modifier
    AddPrefixRare,   // gold item: add a prefix
    AddSuffixRare,   // gold item: add a suffix
    SealExpand,      // gold item: +1 prefix and +1 suffix slot, add a random modifier, then SEAL forever
    RemoveRandom,    // any item: remove a random modifier
    Reforge,         // any item: remove a random modifier, then add a random one
    ReforgePrefix,   // remove a random mod and add a prefix; if prefixes are full, the removed mod is a prefix
    ReforgeSuffix,   // remove a random mod and add a suffix; if suffixes are full, the removed mod is a suffix
}

/// <summary>
/// Server-side application of Enchanting Scrolls (the PoE-orb-style crafting currency).
/// All rules are enforced here: rarity requirements, blue items' 2-modifier cap, per-item
/// prefix/suffix slot caps, and Sealed items rejecting all further modification.
/// </summary>
public static class EnchantSystem
{
    public const int MagicModifierCap = 2;

    /// <summary>Try to apply a scroll's effect to the target. Returns false with a reason on failure;
    /// on success the target has been mutated (rolls only ever change through this path).</summary>
    public static bool Apply(GameData data, Random rng, LootGenerator loot, EnchantType type,
        ItemInstance target, out string error)
    {
        error = null;
        var targetBase = target.GetBase(data);
        if (!targetBase.IsEnchantable)
        {
            error = "That item cannot be enchanted.";
            return false;
        }
        target.EnsureSlotData();
        if (target.Locked)
        {
            error = "This item is Sealed and cannot be modified.";
            return false;
        }

        switch (type)
        {
            case EnchantType.Awaken:
                if (target.Rarity != ItemRarity.Normal || target.Modifiers.Count > 0)
                    return Fail(out error, "Requires an unmodified white item.");
                if (!TryAddAffix(data, rng, loot, target, RandomOpenAffix(data, rng, target), out error)) return false;
                target.Rarity = ItemRarity.Magic;
                return true;

            case EnchantType.AddPrefixMagic:
            case EnchantType.AddSuffixMagic:
            {
                if (target.Rarity != ItemRarity.Magic)
                    return Fail(out error, "Requires a blue (magic) item.");
                if (target.Modifiers.Count >= MagicModifierCap)
                    return Fail(out error, $"Blue items can hold at most {MagicModifierCap} modifiers.");
                var affix = type == EnchantType.AddPrefixMagic ? AffixType.Prefix : AffixType.Suffix;
                return TryAddAffix(data, rng, loot, target, affix, out error);
            }

            case EnchantType.AddRandomRare:
            case EnchantType.AddPrefixRare:
            case EnchantType.AddSuffixRare:
            {
                if (target.Rarity != ItemRarity.Rare)
                    return Fail(out error, "Requires a gold (rare) item.");
                AffixType affix = type switch
                {
                    EnchantType.AddPrefixRare => AffixType.Prefix,
                    EnchantType.AddSuffixRare => AffixType.Suffix,
                    _ => RandomOpenAffix(data, rng, target),
                };
                return TryAddAffix(data, rng, loot, target, affix, out error);
            }

            case EnchantType.SealExpand:
            {
                if (target.Rarity != ItemRarity.Rare)
                    return Fail(out error, "Requires a gold (rare) item.");
                target.MaxPrefixes++;
                target.MaxSuffixes++;
                TryAddAffix(data, rng, loot, target, RandomOpenAffix(data, rng, target), out _); // best effort
                target.Locked = true; // sealed: no further enchanting, ever
                return true;
            }

            case EnchantType.RemoveRandom:
            {
                if (target.Modifiers.Count == 0)
                    return Fail(out error, "The item has no modifiers to remove.");
                RemoveRandomModifier(rng, target, null, data);
                if (target.Rarity == ItemRarity.Magic && target.Modifiers.Count == 0)
                    target.Rarity = ItemRarity.Normal; // scoured back to white
                return true;
            }

            case EnchantType.Reforge:
            {
                if (target.Modifiers.Count == 0)
                    return Fail(out error, "The item has no modifiers to reforge.");
                RemoveRandomModifier(rng, target, null, data);
                return TryAddAffix(data, rng, loot, target, RandomOpenAffix(data, rng, target), out error);
            }

            case EnchantType.ReforgePrefix:
            case EnchantType.ReforgeSuffix:
            {
                if (target.Modifiers.Count == 0)
                    return Fail(out error, "The item has no modifiers to reforge.");
                var wanted = type == EnchantType.ReforgePrefix ? AffixType.Prefix : AffixType.Suffix;
                // If that side is full, the removed modifier must be of the same side so the
                // add is guaranteed a slot; otherwise remove anything.
                bool sideFull = !target.CanAddAffix(data, wanted);
                if (sideFull && target.CountAffixes(data, wanted) == 0)
                    return Fail(out error, $"No {wanted.ToString().ToLowerInvariant()} slots on this item.");
                RemoveRandomModifier(rng, target, sideFull ? wanted : null, data);
                return TryAddAffix(data, rng, loot, target, wanted, out error);
            }
        }
        return Fail(out error, "Unknown scroll effect.");
    }

    private static bool Fail(out string error, string message)
    {
        error = message;
        return false;
    }

    /// <summary>Pick an affix side that still has room (random when both do).</summary>
    private static AffixType RandomOpenAffix(GameData data, Random rng, ItemInstance target)
    {
        bool p = target.CanAddAffix(data, AffixType.Prefix);
        bool s = target.CanAddAffix(data, AffixType.Suffix);
        if (p && s) return rng.Next(2) == 0 ? AffixType.Prefix : AffixType.Suffix;
        return p ? AffixType.Prefix : AffixType.Suffix;
    }

    private static bool TryAddAffix(GameData data, Random rng, LootGenerator loot,
        ItemInstance target, AffixType affix, out string error)
    {
        error = null;
        if (!target.CanAddAffix(data, affix))
            return Fail(out error, $"No open {affix.ToString().ToLowerInvariant()} slot on this item.");
        if (!loot.TryRollAffix(target, target.GetBase(data), affix))
            return Fail(out error, "No applicable modifier exists for this item.");
        return true;
    }

    private static void RemoveRandomModifier(Random rng, ItemInstance target, AffixType? ofType, GameData data)
    {
        var candidates = ofType == null
            ? target.Modifiers
            : target.Modifiers.Where(r =>
                data.Modifiers.TryGetValue(r.ModifierId, out var def) && def.AffixType == ofType).ToList();
        if (candidates.Count == 0) candidates = target.Modifiers;
        target.Modifiers.Remove(candidates[rng.Next(candidates.Count)]);
    }
}
