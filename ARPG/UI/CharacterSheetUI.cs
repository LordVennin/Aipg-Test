using FontStashSharp;
using ARPG.Core;
using ARPG.Data;
using ARPG.Items;
using ARPG.Net;
using ARPG.Render;
using ARPG.Skills;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ARPG.UI;

/// <summary>
/// Character sheet (default C): defensive totals (armor, all resistances, life regen)
/// and every learned skill's DPS with currently equipped gear, broken down by damage
/// type — so a Searing mace shows its Blunt and Fire portions separately.
/// </summary>
public class CharacterSheetUI
{
    public bool Open;
    private readonly GameData _data;
    private readonly GameClient _client;
    private Rectangle _panelRect;

    public CharacterSheetUI(GameData data, GameClient client)
    {
        _data = data;
        _client = client;
    }

    public readonly WindowDrag Window = new();

    public void Layout(Point screen)
    {
        _panelRect = Window.Place(new Rectangle(screen.X / 2 - 250, 36, 500, Math.Min(650, screen.Y - 60)), screen);
    }

    public bool Contains(Point p) => Open && _panelRect.Contains(p);

    private Point _lastMouse;

    public void Update(InputManager input, bool mouseBlocked = false)
    {
        if (!Open) return;
        if (mouseBlocked) return; // a window above this one holds the mouse
        _lastMouse = input.MousePosition;
        if (CloseButton.Handle(input, _panelRect))
        {
            Open = false;
            return;
        }
        if (Window.HandleBar(input, WindowDrag.BarFor(_panelRect))) return;
        if (_panelRect.Contains(input.MousePosition))
            input.MouseCapturedByUI = true;
    }

    public void Draw(SpriteBatch sb)
    {
        if (!Open) return;
        var character = _client.World.MyCharacter;
        if (character == null) return;
        var stats = _client.World.MyStats;

        sb.Draw(TextureGen.Pixel, _panelRect, new Color(22, 22, 30, 240));
        Border(sb, _panelRect, new Color(95, 88, 62));
        WindowDrag.DrawBar(sb, _panelRect, _lastMouse);
        CloseButton.Draw(sb, _panelRect, _lastMouse);
        int x = _panelRect.X + 14, y = _panelRect.Y + 8;
        var title = FontManager.GetBold(19);
        var font = FontManager.Get(15);
        var small = FontManager.Get(13);
        var header = new Color(230, 215, 165);
        var label = new Color(170, 165, 150);
        var value = Color.White;

        sb.DrawString(title, $"Character — {character.Name}  (Level {character.Level})", new Vector2(x, y), header);
        y += 34;

        // ------------------------------------------------ attributes
        sb.DrawString(FontManager.GetBold(16), "Attributes", new Vector2(x, y), new Color(220, 190, 140));
        y += 24;
        var attrs = new (string name, float val, Color c)[]
        {
            ("Strength", stats.Strength, new Color(230, 140, 120)),
            ("Dexterity", stats.Dexterity, new Color(140, 220, 140)),
            ("Intelligence", stats.Intelligence, new Color(140, 170, 240)),
        };
        for (int i = 0; i < attrs.Length; i++)
        {
            var pos = new Vector2(x + i * 155, y);
            sb.DrawString(font, attrs[i].name, pos, attrs[i].c);
            sb.DrawString(FontManager.GetBold(15), $"{attrs[i].val:0}", pos + new Vector2(100, 0), value);
        }
        y += 27;

        // ------------------------------------------------ defenses
        sb.DrawString(FontManager.GetBold(16), "Defenses", new Vector2(x, y), new Color(150, 200, 150));
        y += 24;
        void StatLine(string name, string val)
        {
            sb.DrawString(font, name, new Vector2(x, y), label);
            sb.DrawString(font, val, new Vector2(x + 210, y), value);
            y += 21;
        }
        var me = _client.World.Me;
        StatLine("Maximum Health", $"{me?.Health ?? 0:0} / {stats.MaxHealth:0}");
        StatLine("Life Regeneration", stats.LifeRegeneration > 0 ? $"+{stats.LifeRegeneration:0.#}/s" : "—");
        StatLine("Maximum Mana", $"{me?.Mana ?? 0:0} / {stats.MaxMana:0}");
        StatLine("Mana Regeneration", $"+{stats.ManaRegeneration:0.#}/s");
        StatLine("Armor", $"{stats.Armor:0}  ({stats.PhysicalReduction:P0} physical reduction)");
        StatLine("Deflection", stats.DeflectionRating > 0
            ? $"{stats.DeflectionRating:0} rating  ({stats.DeflectionChance:0}% initial chance)"
            : "—");
        StatLine("Energy Shield", stats.MaxEnergyShield > 0
            ? $"{me?.EnergyShield ?? 0:0} / {stats.MaxEnergyShield:0}"
            : "—");
        StatLine("Block Chance", stats.BlockChance > 0
            ? $"{stats.BlockChance:0}%  (recovers in {stats.BlockCooldown:0.0}s)"
            : "—");
        StatLine("Movement Speed", $"{stats.MovementSpeed:0.0} tiles/s");
        if (stats.DeflectionRating > 0)
        {
            sb.DrawString(FontManager.Get(12),
                "Deflection: each incoming Attack runs repeated checks at descending chances" +
                $" (-{Stats.Deflection.ChanceStepPercent:0}% per check);",
                new Vector2(x, y), new Color(130, 150, 130));
            y += 16;
            sb.DrawString(FontManager.Get(12),
                $"every success deflects {Stats.Deflection.ReductionPerLayer:P0} of the remaining damage.",
                new Vector2(x, y), new Color(130, 150, 130));
            y += 18;
        }
        y += 4;

        // Resistances in two columns, colored per type.
        var resists = new (string name, float val, DamageKind kind)[]
        {
            ("Fire", stats.FireResistance, DamageKind.Fire),
            ("Cold", stats.ColdResistance, DamageKind.Cold),
            ("Lightning", stats.LightningResistance, DamageKind.Lightning),
            ("Acid", stats.AcidResistance, DamageKind.Acid),
            ("Dark", stats.DarkResistance, DamageKind.Dark),
            ("Light", stats.LightResistance, DamageKind.Light),
            ("Arcane", stats.ArcaneResistance, DamageKind.Arcane),
        };
        for (int i = 0; i < resists.Length; i++)
        {
            int col = i % 2, row = i / 2;
            var pos = new Vector2(x + col * 240, y + row * 21);
            sb.DrawString(font, $"{resists[i].name} Resistance", pos, WorldRenderer.DamageKindColor(resists[i].kind));
            sb.DrawString(font, $"{resists[i].val:0}%", pos + new Vector2(160, 0), value);
        }
        y += (resists.Length + 1) / 2 * 21 + 10;

        // ------------------------------------------------ skill DPS
        sb.Draw(TextureGen.Pixel, new Rectangle(x, y, _panelRect.Width - 28, 2), new Color(90, 85, 70));
        y += 10;
        sb.DrawString(FontManager.GetBold(16), "Skills — DPS with equipped gear", new Vector2(x, y), new Color(150, 180, 220));
        y += 26;

        foreach (var learned in character.Skills)
        {
            var def = learned.GetDefinition(_data);
            if (def == null) continue;
            var skillStats = SkillMath.Compute(_data, def, learned.Level, learned.ScrollDefinitions(_data), stats);
            var dps = SkillMath.DpsBreakdown(skillStats);
            float totalDps = dps.Values.Sum();

            bool weaponOk = (!def.RequiredWeapon.HasValue || stats.WeaponCategory == def.RequiredWeapon)
                            && (!def.RequiresShield || stats.HasShield);
            SkillMenuUI.DrawSkillIcon(sb, new Rectangle(x, y, 20, 20), def);
            sb.DrawString(font, $"{def.Name}  (Lv {learned.Level})", new Vector2(x + 26, y + 1),
                weaponOk ? value : new Color(140, 135, 125));
            string totalText = $"{totalDps:0.0} DPS";
            var totalSize = FontManager.GetBold(15).MeasureString(totalText);
            sb.DrawString(FontManager.GetBold(15), totalText,
                new Vector2(_panelRect.Right - 20 - totalSize.X, y + 1), weaponOk ? header : new Color(140, 135, 125));
            y += 21;

            // Per-type breakdown, colored: "Blunt 24.3  ·  Fire 8.1"
            float bx = x + 26;
            foreach (var (kind, amount) in dps.OrderByDescending(kv => kv.Value))
            {
                if (amount < 0.05f) continue;
                string part = $"{kind} {amount:0.0}";
                sb.DrawString(small, part, new Vector2(bx, y), WorldRenderer.DamageKindColor(kind));
                bx += small.MeasureString(part).X + 16;
            }
            sb.DrawString(small, $"{1f / MathF.Max(0.05f, skillStats.Cooldown):0.0}/s", new Vector2(_panelRect.Right - 60, y), label);
            if (!weaponOk)
            {
                var reqs = new List<string>();
                if (def.RequiredWeapon.HasValue && stats.WeaponCategory != def.RequiredWeapon)
                    reqs.Add(def.RequiredWeapon.ToString());
                if (def.RequiresShield && !stats.HasShield) reqs.Add("Shield");
                string req = $"requires {string.Join(" + ", reqs)}";
                sb.DrawString(small, req, new Vector2(bx + 6, y), new Color(220, 150, 120));
            }
            y += 22;
        }
    }

    private static void Border(SpriteBatch sb, Rectangle r, Color c)
    {
        sb.Draw(TextureGen.Pixel, new Rectangle(r.X, r.Y, r.Width, 2), c);
        sb.Draw(TextureGen.Pixel, new Rectangle(r.X, r.Bottom - 2, r.Width, 2), c);
        sb.Draw(TextureGen.Pixel, new Rectangle(r.X, r.Y, 2, r.Height), c);
        sb.Draw(TextureGen.Pixel, new Rectangle(r.Right - 2, r.Y, 2, r.Height), c);
    }
}
