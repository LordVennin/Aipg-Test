using ARPG.Data;

namespace ARPG.Items;

/// <summary>
/// The gambler's rules, shared verbatim by server (validation) and client (the price
/// list is local knowledge — no stock roundtrip): which bases can be gambled, what a
/// roll costs, and the rarity odds. You pick the exact BASE; what's rolled on it is
/// fate's business — that is the whole game.
/// </summary>
public static class GambleBalance
{
    /// <summary>Rarity odds per roll (never Normal-heavy — you paid for a chance).</summary>
    public const int WeightNormal = 30;
    public const int WeightMagic = 50;
    public const int WeightRare = 20;

    /// <summary>Gear the gambler will roll: real equipment only — no flasks, no
    /// scrolls — and only bases the character could level-wise wear TODAY.</summary>
    public static bool Eligible(ItemBase b, int playerLevel) =>
        b != null && b.RequiredLevel <= playerLevel &&
        b.Category is ItemCategory.Mace or ItemCategory.Staff or ItemCategory.Bow
            or ItemCategory.Shield or ItemCategory.Quiver
            or ItemCategory.Helmet or ItemCategory.BodyArmor or ItemCategory.Gloves
            or ItemCategory.Boots or ItemCategory.Belt or ItemCategory.Amulet
            or ItemCategory.Ring;

    public static IEnumerable<ItemBase> EligibleBases(GameData data, int playerLevel) =>
        data.Items.Values.Where(b => Eligible(b, playerLevel))
            .OrderBy(b => b.Category).ThenBy(b => b.RequiredLevel).ThenBy(b => b.Name);

    /// <summary>A roll costs REAL money: it scales with the character level the item
    /// will roll at, and jewelry (the strongest mod carriers) costs half again more.</summary>
    public static int Price(ItemBase b, int playerLevel)
    {
        int price = 45 + playerLevel * 12;
        if (b.Category is ItemCategory.Amulet or ItemCategory.Ring)
            price = price * 3 / 2;
        return price;
    }
}
