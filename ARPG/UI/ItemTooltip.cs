using FontStashSharp;
using ARPG.Data;
using ARPG.Items;
using ARPG.Render;
using ARPG.Stats;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ARPG.UI;

/// <summary>
/// Renders item tooltips: name (rarity colored), base properties, prefixes/suffixes,
/// modifier limit and requirements — clearly separated sections.
/// </summary>
public static class ItemTooltip
{
    private record Line(string Text, Color Color, bool Bold = false, bool Separator = false);

    /// <summary>The generic attack added-damage stats and their damage types — one
    /// affix family that works identically on maces, bows and quivers.</summary>
    private static readonly (StatType Stat, Skills.DamageKind Kind)[] AttackAddStats =
    {
        (StatType.AddedFireDamage, Skills.DamageKind.Fire),
        (StatType.AddedColdDamage, Skills.DamageKind.Cold),
        (StatType.AddedLightningDamage, Skills.DamageKind.Lightning),
        (StatType.AddedAcidDamage, Skills.DamageKind.Acid),
        (StatType.AddedDarkDamage, Skills.DamageKind.Dark),
        (StatType.AddedLightDamage, Skills.DamageKind.Light),
        (StatType.AddedArcaneDamage, Skills.DamageKind.Arcane),
    };

    /// <summary>Red warning color for gear whose requirements are no longer met.</summary>
    public static readonly Color UnmetColor = new(255, 95, 85);

    public static void Draw(SpriteBatch sb, GameData data, ItemInstance item, Point mouse, Point screenSize,
        bool requirementsNotMet = false)
    {
        var lines = BuildLines(data, item, requirementsNotMet);

        var font = FontManager.Get(15);
        var boldFont = FontManager.GetBold(16);
        float width = 180;
        float height = 10;
        foreach (var line in lines)
        {
            if (line.Separator) { height += 7; continue; }
            var f = line.Bold ? boldFont : font;
            var size = f.MeasureString(line.Text);
            width = Math.Max(width, size.X + 24);
            height += size.Y + 3;
        }
        height += 8;

        var pos = new Point(mouse.X + 18, mouse.Y + 12);
        if (pos.X + width > screenSize.X) pos.X = (int)(mouse.X - width - 8);
        if (pos.Y + height > screenSize.Y) pos.Y = (int)Math.Max(0, screenSize.Y - height - 4);

        var rect = new Rectangle(pos.X, pos.Y, (int)width, (int)height);
        sb.Draw(TextureGen.Pixel, rect, new Color(10, 10, 16, 245));
        var borderColor = WorldRenderer.RarityColor(item.Rarity) * 0.8f;
        sb.Draw(TextureGen.Pixel, new Rectangle(rect.X, rect.Y, rect.Width, 2), borderColor);
        sb.Draw(TextureGen.Pixel, new Rectangle(rect.X, rect.Bottom - 2, rect.Width, 2), borderColor);
        sb.Draw(TextureGen.Pixel, new Rectangle(rect.X, rect.Y, 2, rect.Height), borderColor);
        sb.Draw(TextureGen.Pixel, new Rectangle(rect.Right - 2, rect.Y, 2, rect.Height), borderColor);

        float y = pos.Y + 6;
        foreach (var line in lines)
        {
            if (line.Separator)
            {
                sb.Draw(TextureGen.Pixel, new Rectangle(pos.X + 8, (int)y + 2, (int)width - 16, 1), new Color(90, 85, 70));
                y += 7;
                continue;
            }
            var f = line.Bold ? boldFont : font;
            var size = f.MeasureString(line.Text);
            sb.DrawString(f, line.Text, new Vector2(pos.X + width / 2 - size.X / 2, y), line.Color);
            y += size.Y + 3;
        }
    }

    private static List<Line> BuildLines(GameData data, ItemInstance item, bool requirementsNotMet)
    {
        var lines = new List<Line>();
        var itemBase = item.GetBase(data);
        var gray = new Color(170, 165, 150);
        var white = Color.White;
        var modColor = new Color(120, 145, 255);

        lines.Add(new Line(item.DisplayName(data), WorldRenderer.RarityColor(item.Rarity), Bold: true));
        string handedness = itemBase.IsWeapon ? (itemBase.TwoHanded ? "Two-Handed " : "One-Handed ") : "";
        lines.Add(new Line($"{item.Rarity} {handedness}{CategoryName(itemBase.Category)}", gray));
        if (requirementsNotMet)
        {
            lines.Add(new Line("REQUIREMENTS NOT MET", UnmetColor, Bold: true));
            lines.Add(new Line("This item grants no benefits.", UnmetColor));
        }

        // --- base properties ---
        var bs = itemBase.BaseStats;
        var baseLines = new List<Line>();
        float minD = bs.GetValueOrDefault(StatType.MinPhysicalDamage);
        float maxD = bs.GetValueOrDefault(StatType.MaxPhysicalDamage);
        if (maxD > 0)
        {
            // This item's own flat AND %Physical rolls fold into the shown range —
            // (base + flat) x local% is the total you actually swing with, mirroring
            // the armor totals below. Global %phys from other gear layers on top.
            float added = ModTotal(StatType.AddedPhysicalDamage);
            float localScale = 1f + ModTotal(StatType.PhysicalDamage) / 100f;
            baseLines.Add(new Line(
                $"Physical Damage: {(minD + added) * localScale:0}-{(maxD + added) * localScale:0}", white));
        }
        // Elemental ATTACK adds rolled on this item show as computed damage lines up
        // top, colored by type — the same 0.8x-1.2x spread the combat math rolls onto
        // every weapon attack, melee and ranged alike. (Staff SPELL adds are different
        // stats entirely and stay in the modifier list: they scale spells, not swings.)
        foreach (var (addStat, addKind) in AttackAddStats)
        {
            float v = ModTotal(addStat);
            if (v > 0)
                baseLines.Add(new Line($"{addKind} Damage: {v * 0.8f:0}-{v * 1.2f:0}",
                    WorldRenderer.DamageKindColor(addKind)));
        }
        if (bs.TryGetValue(StatType.BaseAttackSpeed, out float aps))
            baseLines.Add(new Line($"Attack Speed: {aps:0.0#}", white));
        if (bs.TryGetValue(StatType.WeaponRange, out float range))
            baseLines.Add(new Line($"Weapon Range: {range:0.0}", white));

        // Defensive totals are shown pre-calculated: base + all matching modifiers rolled
        // on the item, so players never sum them (mirrors the weapon damage line).
        float ModTotal(StatType t) => item.Modifiers.Sum(roll =>
            data.Modifiers.GetValueOrDefault(roll.ModifierId)?.StatAffected == t ? roll.Value : 0f);
        float totalArmor = bs.GetValueOrDefault(StatType.Armor) + ModTotal(StatType.Armor);
        if (totalArmor > 0)
            baseLines.Add(new Line($"Armor: {totalArmor:0}", white));
        float totalDeflection = bs.GetValueOrDefault(StatType.DeflectionRating) + ModTotal(StatType.DeflectionRating);
        if (totalDeflection > 0)
            baseLines.Add(new Line($"Deflection Rating: {totalDeflection:0}", new Color(150, 220, 150)));
        float totalEs = bs.GetValueOrDefault(StatType.EnergyShield) + ModTotal(StatType.EnergyShield);
        if (totalEs > 0)
            baseLines.Add(new Line($"Energy Shield: {totalEs:0}", new Color(140, 200, 240)));

        foreach (var (stat, value) in bs)
        {
            if (stat is StatType.MinPhysicalDamage or StatType.MaxPhysicalDamage
                or StatType.BaseAttackSpeed or StatType.WeaponRange or StatType.Armor
                or StatType.DeflectionRating or StatType.EnergyShield) continue;
            baseLines.Add(new Line(DescribeBaseStat(stat, value), white));
        }

        // Flask stats: what a sip restores and how it recharges.
        if (itemBase.Category == ItemCategory.Flask)
        {
            if (itemBase.FlaskHeal > 0)
                baseLines.Add(new Line(
                    $"Restores {itemBase.FlaskHeal:0} Life over {itemBase.FlaskDuration:0.#}s",
                    new Color(235, 130, 120)));
            if (itemBase.FlaskMana > 0)
                baseLines.Add(new Line(
                    $"Restores {itemBase.FlaskMana:0} Mana over {itemBase.FlaskDuration:0.#}s",
                    new Color(140, 170, 245)));
            baseLines.Add(new Line($"Charges: {item.FlaskCharges}/{itemBase.FlaskChargesMax}", white));
            baseLines.Add(new Line("Charges never regenerate — refill at the sanctum fountain.", gray));
        }
        if (baseLines.Count > 0)
        {
            lines.Add(new Line("", gray, Separator: true));
            lines.AddRange(baseLines);
        }

        // --- prefixes, then suffixes ---
        foreach (var affixType in new[] { AffixType.Prefix, AffixType.Suffix })
        {
            var rolls = item.Modifiers
                .Select(r => (roll: r, def: data.Modifiers.GetValueOrDefault(r.ModifierId)))
                .Where(x => x.def != null && x.def.AffixType == affixType)
                .ToList();
            if (rolls.Count == 0) continue;
            lines.Add(new Line("", gray, Separator: true));
            foreach (var (roll, def) in rolls)
                lines.Add(new Line($"{def.DescribeRoll(roll.Value)}  [{(affixType == AffixType.Prefix ? "P" : "S")}] {def.Name}", modColor));
        }

        // --- meta ---
        lines.Add(new Line("", gray, Separator: true));
        if (itemBase.Category == ItemCategory.SkillScroll && itemBase.ScrollId != null &&
            data.Scrolls.TryGetValue(itemBase.ScrollId, out var scrollDef))
        {
            lines.Add(new Line($"Requires skill tag: {scrollDef.RequiredTag}", new Color(200, 160, 255)));
            lines.Add(new Line(scrollDef.Description ?? "", gray));
        }
        else if (itemBase.Category == ItemCategory.EnchantScroll)
        {
            foreach (var descLine in WrapText(itemBase.Description ?? "", 44))
                lines.Add(new Line(descLine, new Color(190, 175, 220)));
            lines.Add(new Line($"Stack: {item.StackCount}/{itemBase.MaxStack}", gray));
            lines.Add(new Line("Right-click, then click an item to use.", gray));
        }
        else
        {
            item.EnsureSlotData();
            int bonus = item.ModifierLimitBonus(data);
            string flex = bonus > 0 ? $"  (+{bonus} flexible)" : "";
            lines.Add(new Line(
                $"Prefixes: {item.CountAffixes(data, AffixType.Prefix)}/{item.MaxPrefixes} · " +
                $"Suffixes: {item.CountAffixes(data, AffixType.Suffix)}/{item.MaxSuffixes}{flex}", gray));
            lines.Add(new Line($"Modifiers: {item.Modifiers.Count} / {item.CurrentModifierLimit(data)} (item limit)", gray));
            if (item.Locked)
                lines.Add(new Line("SEALED — cannot be modified", new Color(230, 110, 200)));
        }
        // Requirements: level plus any attribute demands (after the item's own "of
        // Ease" reductions), on one clear line.
        var reqs = new List<string>();
        if (itemBase.RequiredLevel > 1) reqs.Add($"Level {itemBase.RequiredLevel}");
        int rStr = item.EffectiveRequirement(data, itemBase.RequiredStrength);
        int rDex = item.EffectiveRequirement(data, itemBase.RequiredDexterity);
        int rInt = item.EffectiveRequirement(data, itemBase.RequiredIntelligence);
        bool reduced = rStr < itemBase.RequiredStrength || rDex < itemBase.RequiredDexterity ||
                       rInt < itemBase.RequiredIntelligence;
        if (rStr > 0) reqs.Add($"{rStr} Str");
        if (rDex > 0) reqs.Add($"{rDex} Dex");
        if (rInt > 0) reqs.Add($"{rInt} Int");
        if (reqs.Count > 0)
            lines.Add(new Line($"Requires: {string.Join(", ", reqs)}{(reduced ? "  (reduced)" : "")}",
                requirementsNotMet ? UnmetColor : new Color(220, 170, 130)));
        if (totalDeflection > 0)
        {
            foreach (var dLine in WrapText(
                "Deflection: incoming Attacks run repeated checks at descending chances; " +
                "each success deflects 20% of the remaining damage.", 46))
                lines.Add(new Line(dLine, new Color(130, 160, 130)));
        }
        lines.Add(new Line($"Item Level: {item.ItemLevel}", gray));
        if (itemBase.Category != ItemCategory.SkillScroll)
            lines.Add(new Line($"Value: {item.GoldValue(data)} gold", new Color(240, 200, 90)));
        return lines;
    }

    private static IEnumerable<string> WrapText(string text, int maxChars)
    {
        var words = text.Split(' ');
        var line = "";
        foreach (var word in words)
        {
            if (line.Length + word.Length + 1 > maxChars && line.Length > 0)
            {
                yield return line;
                line = "";
            }
            line = line.Length == 0 ? word : line + " " + word;
        }
        if (line.Length > 0) yield return line;
    }

    private static string CategoryName(ItemCategory c) => c switch
    {
        ItemCategory.BodyArmor => "Body Armor",
        ItemCategory.SkillScroll => "Skill Scroll",
        _ => c.ToString(),
    };

    private static string DescribeBaseStat(StatType stat, float value) => stat switch
    {
        StatType.Armor => $"Armor: {value:0}",
        StatType.DeflectionRating => $"Deflection Rating: {value:0}",
        StatType.EnergyShield => $"Energy Shield: {value:0}",
        StatType.Strength => $"+{value:0} Strength",
        StatType.Dexterity => $"+{value:0} Dexterity",
        StatType.Intelligence => $"+{value:0} Intelligence",
        StatType.BlockChance => $"Block Chance: {value:0}%",
        StatType.BlockCooldownRecovery => $"+{value:0}% Block Cooldown Recovery",
        StatType.SpellDamage => $"+{value:0}% Spell Damage",
        StatType.CastSpeed => $"+{value:0}% Cast Speed",
        StatType.MaxHealth => $"+{value:0} Maximum Health",
        StatType.MaximumMana => $"+{value:0} Maximum Mana",
        StatType.ManaRegeneration => $"+{value:0}% Mana Regeneration",
        StatType.ArcaneResistance => $"+{value:0}% Arcane Resistance",
        StatType.FireResistance => $"+{value:0}% Fire Resistance",
        StatType.ColdResistance => $"+{value:0}% Cold Resistance",
        StatType.LightningResistance => $"+{value:0}% Lightning Resistance",
        StatType.MovementSpeed => $"+{value:0}% Movement Speed",
        _ => $"+{value:0} {stat}",
    };
}
