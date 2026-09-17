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
    private record Line(string Text, Color Color, bool Bold = false, bool Separator = false, string Term = null);

    /// <summary>Where a drawn tooltip landed: its panel and the on-screen rectangles of
    /// the UNDERLINED defense terms (Armor / Energy Shield / Deflection) — hovering one
    /// while the tooltip is held open (Alt) shows its explainer.</summary>
    public sealed class Layout
    {
        public Rectangle Rect;
        public readonly List<(Rectangle Rect, string Term)> Terms = new();
    }

    public const string TermArmor = "Armor", TermEnergyShield = "Energy Shield", TermDeflection = "Deflection";

    /// <summary>The short mechanics explainer behind an underlined term, built from the
    /// live balance constants so it can never drift from the math.</summary>
    public static string Explain(string term) => term switch
    {
        TermArmor =>
            $"Armor reduces PHYSICAL damage taken. Reduction = armor / (armor + {ArmorBalance.SoftCapBase:0} + " +
            $"{ArmorBalance.SoftCapPerLevel:0} x level), so the same armor is worth less as you level - keep it growing. " +
            "Spells, elements and damage over time ignore it.",
        TermEnergyShield =>
            "Energy Shield is a second pool that absorbs EVERY kind of damage before your health. " +
            $"It recharges at {EnergyShieldBalance.RechargePctPerSecond:0}% of its maximum per second once you have gone " +
            $"{EnergyShieldBalance.RechargeDelay:0} seconds without taking damage (even a fully absorbed hit resets that). " +
            $"Intelligence raises it: +{AttributeBalance.EnergyShieldPctPer10Intelligence:0}% per 10 INT.",
        TermDeflection =>
            $"Deflection rating becomes an initial chance (capped at {Deflection.InitialChanceCap:0}%, diminishing with level). " +
            $"Each incoming ATTACK rolls a chain of checks at descending chances (-{Deflection.ChanceStepPercent:0} points each); " +
            $"every success deflects {Deflection.ReductionPerLayer:P0} of the damage still coming. " +
            $"Spells and damage over time are never deflected. Dexterity scales rating by +{AttributeBalance.DeflectionPctPerDexterity:0.#}% per point.",
        _ => "",
    };

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

    /// <summary>Every text line the tooltip would show (tests and tools).</summary>
    public static IReadOnlyList<string> TextLines(GameData data, ItemInstance item) =>
        BuildLines(data, item, false).Where(l => !l.Separator).Select(l => l.Text).ToList();

    public static Layout Draw(SpriteBatch sb, GameData data, ItemInstance item, Point mouse, Point screenSize,
        bool requirementsNotMet = false, bool held = false)
    {
        var lines = BuildLines(data, item, requirementsNotMet);
        var layout = new Layout();
        // The Alt hint only matters when there is something to inspect.
        if (lines.Any(l => l.Term != null))
            lines.Add(new Line(held ? "hover an underlined term" : "hold Alt to inspect", new Color(120, 116, 104)));

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
        layout.Rect = rect;
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
            float lx = pos.X + width / 2 - size.X / 2;
            sb.DrawString(f, line.Text, new Vector2(lx, y), line.Color);
            if (line.Term != null)
            {
                // Underline the term itself (the label before the colon); the rect is
                // what the Alt-held inspector hit-tests.
                int colon = line.Text.IndexOf(':');
                string label = colon > 0 ? line.Text[..colon] : line.Text;
                var lsize = f.MeasureString(label);
                var termRect = new Rectangle((int)lx - 2, (int)y - 1, (int)lsize.X + 4, (int)size.Y + 2);
                sb.Draw(TextureGen.Pixel, new Rectangle((int)lx, (int)(y + size.Y - 1), (int)lsize.X, 1), line.Color * 0.9f);
                layout.Terms.Add((termRect, line.Term));
            }
            y += size.Y + 3;
        }
        return layout;
    }

    /// <summary>The explainer popup for an underlined term, beside the mouse and kept
    /// on screen; drawn OVER the held tooltip.</summary>
    public static void DrawExplainer(SpriteBatch sb, string term, Point mouse, Point screenSize)
    {
        string body = Explain(term);
        if (body.Length == 0) return;
        var titleFont = FontManager.GetBold(15);
        var font = FontManager.Get(14);
        var bodyLines = WrapText(body, 54).ToList();
        float width = titleFont.MeasureString(term).X + 24;
        foreach (var l in bodyLines) width = Math.Max(width, font.MeasureString(l).X + 24);
        float height = 12 + titleFont.MeasureString(term).Y + 6 + bodyLines.Count * (font.MeasureString("X").Y + 2) + 10;
        var pos = new Point(mouse.X + 18, mouse.Y + 12);
        if (pos.X + width > screenSize.X) pos.X = (int)(mouse.X - width - 8);
        if (pos.Y + height > screenSize.Y) pos.Y = (int)Math.Max(0, screenSize.Y - height - 4);
        var rect = new Rectangle(pos.X, pos.Y, (int)width, (int)height);
        sb.Draw(TextureGen.Pixel, rect, new Color(14, 16, 24, 250));
        var edge = new Color(150, 200, 235);
        sb.Draw(TextureGen.Pixel, new Rectangle(rect.X, rect.Y, rect.Width, 2), edge);
        sb.Draw(TextureGen.Pixel, new Rectangle(rect.X, rect.Bottom - 2, rect.Width, 2), edge * 0.6f);
        sb.Draw(TextureGen.Pixel, new Rectangle(rect.X, rect.Y, 2, rect.Height), edge * 0.6f);
        sb.Draw(TextureGen.Pixel, new Rectangle(rect.Right - 2, rect.Y, 2, rect.Height), edge * 0.6f);
        float y = pos.Y + 8;
        sb.DrawString(titleFont, term, new Vector2(pos.X + 12, y), edge);
        y += titleFont.MeasureString(term).Y + 6;
        foreach (var l in bodyLines)
        {
            sb.DrawString(font, l, new Vector2(pos.X + 12, y), new Color(215, 218, 226));
            y += font.MeasureString("X").Y + 2;
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
            baseLines.Add(new Line($"Armor: {totalArmor:0}", white, Term: TermArmor));
        float totalDeflection = bs.GetValueOrDefault(StatType.DeflectionRating) + ModTotal(StatType.DeflectionRating);
        if (totalDeflection > 0)
            baseLines.Add(new Line($"Deflection Rating: {totalDeflection:0}", new Color(150, 220, 150), Term: TermDeflection));
        float totalEs = bs.GetValueOrDefault(StatType.EnergyShield) + ModTotal(StatType.EnergyShield);
        if (totalEs > 0)
            baseLines.Add(new Line($"Energy Shield: {totalEs:0}", new Color(140, 200, 240), Term: TermEnergyShield));

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

        // --- unique rule + flavour ---
        if (itemBase.Unique)
        {
            lines.Add(new Line("", gray, Separator: true));
            var uniqueGold = new Color(255, 206, 120);
            foreach (var rule in itemBase.UniqueLines)
                foreach (var ruleLine in WrapText(rule, 44))
                    lines.Add(new Line(ruleLine, uniqueGold));
            foreach (var descLine in WrapText(itemBase.Description ?? "", 44))
                lines.Add(new Line(descLine, new Color(170, 160, 150)));
        }

        // --- meta ---
        lines.Add(new Line("", gray, Separator: true));
        if (itemBase.Category == ItemCategory.SkillScroll && itemBase.ScrollId != null &&
            data.Scrolls.TryGetValue(itemBase.ScrollId, out var scrollDef))
        {
            lines.Add(new Line($"Requires skill tag: {scrollDef.RequiredTag}", new Color(200, 160, 255)));
            lines.Add(new Line(scrollDef.Description ?? "", gray));
        }
        else if (itemBase.Category == ItemCategory.WarpScroll)
        {
            var violet = new Color(200, 170, 255);
            lines.Add(new Line($"Zone level {item.ItemLevel}  ·  {item.Modifiers.Count} / 6 seals", violet));
            foreach (var descLine in WrapText(itemBase.Description ?? "", 44))
                lines.Add(new Line(descLine, new Color(170, 160, 150)));
            lines.Add(new Line("Place it on the podium in the ruins to open the portal.", gray));
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
        ItemCategory.WarpScroll => "Sealed Warp Scroll",
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
