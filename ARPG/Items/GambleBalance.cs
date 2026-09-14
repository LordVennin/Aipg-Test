using ARPG.Data;
using ARPG.Stats;

namespace ARPG.Items;

/// <summary>The gambler's kind of offer: a category of gear, optionally narrowed by
/// what defends it (armour, energy shield, or deflection for light cloth/hide).</summary>
public enum GambleDefense { Any, Armor, EnergyShield, Deflection }

public record GambleOffer(string Token, string Label, ItemCategory Category, GambleDefense Defense);

/// <summary>
/// Gambling rules shared by client (list, prices) and server (the roll). You buy a KIND
/// of gear — "a random bow", "a random energy-shield helmet" — never a named base or a
/// tier: fate picks a base near your level (or below), then the rarity and every roll.
/// </summary>
public static class GambleBalance
{
    /// <summary>Rarity odds per roll (never Normal-heavy — you paid for a chance).</summary>
    public const int WeightNormal = 30;
    public const int WeightMagic = 50;
    public const int WeightRare = 20;

    /// <summary>How far below the character's level a base may sit and still be the
    /// "around your level" pick; older bases only fill in when nothing newer exists.</summary>
    public const int PreferredLevelSpan = 6;

    public static readonly GambleOffer[] Offers =
    {
        new("mace", "Random Mace", ItemCategory.Mace, GambleDefense.Any),
        new("staff", "Random Staff", ItemCategory.Staff, GambleDefense.Any),
        new("bow", "Random Bow", ItemCategory.Bow, GambleDefense.Any),
        new("quiver", "Random Quiver", ItemCategory.Quiver, GambleDefense.Any),
        new("shield", "Random Shield", ItemCategory.Shield, GambleDefense.Any),
        new("helmet:armor", "Random Armor Helmet", ItemCategory.Helmet, GambleDefense.Armor),
        new("helmet:es", "Random Energy Shield Helmet", ItemCategory.Helmet, GambleDefense.EnergyShield),
        new("helmet:light", "Random Deflection Helmet", ItemCategory.Helmet, GambleDefense.Deflection),
        new("body:armor", "Random Armor Body", ItemCategory.BodyArmor, GambleDefense.Armor),
        new("body:es", "Random Energy Shield Body", ItemCategory.BodyArmor, GambleDefense.EnergyShield),
        new("body:light", "Random Deflection Body", ItemCategory.BodyArmor, GambleDefense.Deflection),
        new("gloves:armor", "Random Armor Gloves", ItemCategory.Gloves, GambleDefense.Armor),
        new("gloves:es", "Random Energy Shield Gloves", ItemCategory.Gloves, GambleDefense.EnergyShield),
        new("gloves:light", "Random Deflection Gloves", ItemCategory.Gloves, GambleDefense.Deflection),
        new("boots:armor", "Random Armor Boots", ItemCategory.Boots, GambleDefense.Armor),
        new("boots:es", "Random Energy Shield Boots", ItemCategory.Boots, GambleDefense.EnergyShield),
        new("boots:light", "Random Deflection Boots", ItemCategory.Boots, GambleDefense.Deflection),
        new("belt", "Random Belt", ItemCategory.Belt, GambleDefense.Any),
        new("amulet", "Random Amulet", ItemCategory.Amulet, GambleDefense.Any),
        new("ring", "Random Ring", ItemCategory.Ring, GambleDefense.Any),
    };

    public static GambleOffer FindOffer(string token) =>
        Offers.FirstOrDefault(o => o.Token == token);

    /// <summary>Does a base belong to an offer: right category, never a unique, and the
    /// defence flavour the offer names (a piece with no armour or shield counts as
    /// "light" only when it carries deflection).</summary>
    public static bool Matches(ItemBase b, GambleOffer o)
    {
        if (b == null || b.Unique || b.Category != o.Category) return false;
        float armor = b.BaseStats.GetValueOrDefault(StatType.Armor);
        float es = b.BaseStats.GetValueOrDefault(StatType.EnergyShield);
        float deflect = b.BaseStats.GetValueOrDefault(StatType.DeflectionRating);
        return o.Defense switch
        {
            GambleDefense.Armor => armor > 0,
            GambleDefense.EnergyShield => es > 0,
            GambleDefense.Deflection => deflect > 0 && armor <= 0 && es <= 0,
            _ => true,
        };
    }

    /// <summary>The bases fate may hand over for an offer at this level: those the
    /// character can wear now, preferring ones within PreferredLevelSpan below their
    /// level; when none are that fresh, whatever wearable bases exist.</summary>
    public static List<ItemBase> Candidates(GameData data, GambleOffer o, int playerLevel)
    {
        var wearable = data.Items.Values
            .Where(b => Matches(b, o) && b.RequiredLevel <= playerLevel)
            .OrderBy(b => b.RequiredLevel).ThenBy(b => b.Name).ToList();
        var fresh = wearable.Where(b => b.RequiredLevel >= playerLevel - PreferredLevelSpan).ToList();
        return fresh.Count > 0 ? fresh : wearable;
    }

    /// <summary>Offers the table shows at this level: only kinds fate can actually fill.</summary>
    public static IEnumerable<GambleOffer> Available(GameData data, int playerLevel) =>
        Offers.Where(o => Candidates(data, o, playerLevel).Count > 0);

    /// <summary>A roll costs REAL money: it scales with the character level the item
    /// will roll at, and jewelry (the strongest mod carriers) costs half again more.</summary>
    public static int Price(GambleOffer o, int playerLevel)
    {
        int price = 45 + playerLevel * 12;
        if (o.Category is ItemCategory.Amulet or ItemCategory.Ring)
            price = price * 3 / 2;
        return price;
    }
}
