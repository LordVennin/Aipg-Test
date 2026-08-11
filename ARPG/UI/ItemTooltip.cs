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

    public static void Draw(SpriteBatch sb, GameData data, ItemInstance item, Point mouse, Point screenSize)
    {
        var lines = BuildLines(data, item);

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

    private static List<Line> BuildLines(GameData data, ItemInstance item)
    {
        var lines = new List<Line>();
        var itemBase = item.GetBase(data);
        var gray = new Color(170, 165, 150);
        var white = Color.White;
        var modColor = new Color(120, 145, 255);

        lines.Add(new Line(item.DisplayName(data), WorldRenderer.RarityColor(item.Rarity), Bold: true));
        lines.Add(new Line($"{item.Rarity} {CategoryName(itemBase.Category)}", gray));

        // --- base properties ---
        var bs = itemBase.BaseStats;
        var baseLines = new List<Line>();
        float minD = bs.GetValueOrDefault(StatType.MinPhysicalDamage);
        float maxD = bs.GetValueOrDefault(StatType.MaxPhysicalDamage);
        if (maxD > 0)
        {
            // Local added phys from this item's own modifiers affects the shown weapon damage.
            float added = 0;
            foreach (var roll in item.Modifiers)
                if (data.Modifiers.TryGetValue(roll.ModifierId, out var d) && d.StatAffected == StatType.AddedPhysicalDamage)
                    added += roll.Value;
            baseLines.Add(new Line($"Physical Damage: {minD + added:0}-{maxD + added:0}", white));
        }
        if (bs.TryGetValue(StatType.BaseAttackSpeed, out float aps))
            baseLines.Add(new Line($"Attack Speed: {aps:0.0#}", white));
        if (bs.TryGetValue(StatType.WeaponRange, out float range))
            baseLines.Add(new Line($"Weapon Range: {range:0.0}", white));
        foreach (var (stat, value) in bs)
        {
            if (stat is StatType.MinPhysicalDamage or StatType.MaxPhysicalDamage
                or StatType.BaseAttackSpeed or StatType.WeaponRange) continue;
            baseLines.Add(new Line(DescribeBaseStat(stat, value), white));
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
            lines.Add(new Line("Drag onto an item to use.", gray));
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
        if (itemBase.RequiredLevel > 1)
            lines.Add(new Line($"Required Level: {itemBase.RequiredLevel}", gray));
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
        StatType.SpellDamage => $"+{value:0}% Spell Damage",
        StatType.CastSpeed => $"+{value:0}% Cast Speed",
        StatType.MaxHealth => $"+{value:0} Maximum Health",
        StatType.FireResistance => $"+{value:0}% Fire Resistance",
        StatType.ColdResistance => $"+{value:0}% Cold Resistance",
        StatType.LightningResistance => $"+{value:0}% Lightning Resistance",
        StatType.MovementSpeed => $"+{value:0}% Movement Speed",
        _ => $"+{value:0} {stat}",
    };
}
