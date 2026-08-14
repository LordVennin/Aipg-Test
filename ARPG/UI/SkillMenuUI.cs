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
/// Dedicated Skill Menu (not the inventory window): learned skills with level/XP, computed
/// stats, tags, Skill Scroll slots (locked / empty / occupied), hotkey assignment and
/// learning new skills. Scrolls attach by dragging a scroll item from the inventory
/// onto an unlocked slot; click an occupied slot to detach it back to the bag.
/// </summary>
public class SkillMenuUI
{
    public bool Open;
    private readonly GameData _data;
    private readonly GameClient _client;
    private readonly DragState _drag;

    private Rectangle _panelRect;
    private string _selectedSkillId;
    private readonly List<(Rectangle rect, string skillId, bool learned)> _listRows = new();
    private readonly List<(Rectangle rect, int index, bool unlocked, ItemInstance scroll)> _scrollSlots = new();
    private readonly List<(Rectangle rect, int hotbarSlot)> _hotbarButtons = new();

    public ItemInstance HoveredScrollItem { get; private set; }

    public SkillMenuUI(GameData data, GameClient client, DragState drag)
    {
        _data = data;
        _client = client;
        _drag = drag;
    }

    public void Layout(Point screen)
    {
        _panelRect = new Rectangle(12, 36, 480, Math.Min(660, screen.Y - 60));
    }

    public bool Contains(Point p) => Open && _panelRect.Contains(p);

    public void Update(InputManager input)
    {
        HoveredScrollItem = null;
        if (!Open) return;
        var character = _client.World.MyCharacter;
        if (character == null) return;
        var mouse = input.MousePosition;
        if (CloseButton.Handle(input, _panelRect))
        {
            Open = false;
            return;
        }
        if (_panelRect.Contains(mouse)) input.MouseCapturedByUI = true;

        _selectedSkillId ??= character.Skills.FirstOrDefault()?.SkillId;

        foreach (var (rect, skillId, learned) in _listRows)
        {
            if (input.MouseLeftPressed && rect.Contains(mouse))
            {
                if (learned) _selectedSkillId = skillId;
                else _client.RequestLearnSkill(skillId);
            }
        }

        foreach (var (rect, index, unlocked, scroll) in _scrollSlots)
        {
            if (!rect.Contains(mouse)) continue;
            if (scroll != null) HoveredScrollItem = scroll;
            if (input.MouseLeftPressed && unlocked && scroll != null && !_drag.Active)
            {
                // Detach: server places it into any free inventory spot.
                _client.RequestMoveItem(ItemLocation.AtScroll(_selectedSkillId, index), ItemLocation.AtGrid(0, 0));
            }
        }

        foreach (var (rect, hotbarSlot) in _hotbarButtons)
        {
            if (input.MouseLeftPressed && rect.Contains(mouse) && _selectedSkillId != null)
                _client.RequestAssignHotbar(hotbarSlot, _selectedSkillId);
        }
    }

    /// <summary>Complete a drag released over the panel (attach a Skill Scroll). Returns true if handled.</summary>
    public bool TryDropAt(Point mouse)
    {
        if (!Open || !_drag.Active || !_panelRect.Contains(mouse)) return false;
        foreach (var (rect, index, unlocked, _) in _scrollSlots)
        {
            if (rect.Contains(mouse) && unlocked && _selectedSkillId != null)
            {
                _client.RequestMoveItem(_drag.Source, ItemLocation.AtScroll(_selectedSkillId, index));
                return true;
            }
        }
        return true; // released on panel, no valid slot: cancel
    }

    public void Draw(SpriteBatch sb, InputManager input)
    {
        if (!Open) return;
        var character = _client.World.MyCharacter;
        if (character == null) return;

        sb.Draw(TextureGen.Pixel, _panelRect, new Color(22, 22, 30, 240));
        Border(sb, _panelRect, new Color(95, 88, 62));
        CloseButton.Draw(sb, _panelRect, input.MousePosition);
        int x = _panelRect.X + 12, y = _panelRect.Y + 8;
        sb.DrawString(FontManager.GetBold(19), "Skills", new Vector2(x, y), new Color(230, 215, 165));
        y += 34;

        // --- learned skill list + learnable skills ---
        _listRows.Clear();
        var font = FontManager.Get(15);
        var small = FontManager.Get(12);
        foreach (var learned in character.Skills)
        {
            var def = learned.GetDefinition(_data);
            if (def == null) continue;
            var rect = new Rectangle(x, y, 300, 26);
            bool selected = learned.SkillId == _selectedSkillId;
            sb.Draw(TextureGen.Pixel, rect, selected ? new Color(70, 62, 40) : new Color(34, 34, 44));
            DrawSkillIcon(sb, new Rectangle(rect.X + 2, rect.Y + 2, 22, 22), def);
            sb.DrawString(font, $"{def.Name}", new Vector2(rect.X + 30, rect.Y + 4), Color.White);
            sb.DrawString(font, $"Lv {learned.Level}", new Vector2(rect.X + 240, rect.Y + 4), new Color(180, 220, 160));
            _listRows.Add((rect, learned.SkillId, true));

            // Hotbar badge
            int slot = Array.IndexOf(character.Hotbar, learned.SkillId);
            if (slot >= 0)
                sb.DrawString(small, HotbarKeyLabel(slot), new Vector2(rect.Right + 8, rect.Y + 6), new Color(150, 190, 255));
            y += 30;
        }
        foreach (var def in _data.Skills.Values.OrderBy(d => d.Name))
        {
            if (character.GetSkill(def.Id) != null) continue;
            var rect = new Rectangle(x, y, 300, 26);
            sb.Draw(TextureGen.Pixel, rect, new Color(28, 28, 34));
            sb.DrawString(font, def.Name, new Vector2(rect.X + 30, rect.Y + 4), new Color(120, 116, 108));
            var learnRect = new Rectangle(rect.Right + 8, rect.Y, 64, 26);
            sb.Draw(TextureGen.Pixel, learnRect, new Color(46, 66, 46));
            Border(sb, learnRect, new Color(90, 130, 90));
            sb.DrawString(small, "Learn", new Vector2(learnRect.X + 14, learnRect.Y + 5), new Color(180, 230, 180));
            _listRows.Add((learnRect, def.Id, false));
            y += 30;
        }
        y += 8;

        // --- selected skill details ---
        _scrollSlots.Clear();
        _hotbarButtons.Clear();
        var sel = _selectedSkillId != null ? character.GetSkill(_selectedSkillId) : null;
        var selDef = sel?.GetDefinition(_data);
        if (sel == null || selDef == null) return;

        sb.Draw(TextureGen.Pixel, new Rectangle(x, y, _panelRect.Width - 24, 2), new Color(90, 85, 70));
        y += 10;
        sb.DrawString(FontManager.GetBold(18), selDef.Name, new Vector2(x, y), WorldRenderer.RarityColor(ItemRarity.Rare));
        sb.DrawString(small, $"Tags: {string.Join(", ", selDef.Tags)}", new Vector2(x + 200, y + 4), new Color(170, 150, 200));
        y += 26;
        // Wrap the description to the panel width — long texts (e.g. Shield Bash) must
        // not run past the panel edge.
        foreach (var descLine in TextUtil.WrapToWidth(selDef.Description ?? "", small, _panelRect.Width - 30))
        {
            sb.DrawString(small, descLine, new Vector2(x, y), new Color(170, 165, 150));
            y += 17;
        }
        y += 3;

        // XP bar
        float xpNeed = SkillMath.XpToNextLevel(sel.Level);
        float xpFrac = sel.Level >= SkillMath.MaxSkillLevel ? 1f : Math.Clamp(sel.Experience / xpNeed, 0, 1);
        var xpRect = new Rectangle(x, y, 300, 10);
        sb.Draw(TextureGen.Pixel, xpRect, new Color(30, 30, 36));
        sb.Draw(TextureGen.Pixel, new Rectangle(xpRect.X, xpRect.Y, (int)(xpRect.Width * xpFrac), xpRect.Height), new Color(120, 200, 120));
        Border(sb, xpRect, new Color(70, 70, 60));
        string xpText = sel.Level >= SkillMath.MaxSkillLevel ? "MAX" : $"Level {sel.Level}  ·  XP {sel.Experience:0}/{xpNeed:0}";
        sb.DrawString(small, xpText, new Vector2(xpRect.Right + 10, y - 2), Color.White);
        y += 20;

        // Computed stats (same math the server uses)
        var stats = SkillMath.Compute(_data, selDef, sel.Level, sel.ScrollDefinitions(_data), _client.World.MyStats);
        // Attacks are pure weapon scaling (PoE-style): show the % so the source is obvious.
        string weaponPct = selDef.UsesWeaponDamage
            ? $"  ({(selDef.WeaponDamageMultiplier + selDef.WeaponDamageMultiplierPerLevel * (sel.Level - 1)) * 100:0}% of weapon)"
            : "";
        string statLine1 = $"Damage {stats.MinDamage:0}-{stats.MaxDamage:0} ({stats.DamageKind}){weaponPct}   Cooldown {stats.Cooldown:0.00}s";
        string statLine2 = $"Range {stats.Range:0.0}" +
                           (stats.Radius > 0 ? $"   Radius {stats.Radius:0.0}" : "") +
                           (selDef.Archetype == SkillArchetype.Projectile ? $"   Projectiles {stats.ProjectileCount}" : "") +
                           (stats.ManaCost > 0 ? $"   Mana {stats.ManaCost:0}" : "") +
                           $"   Crit {stats.CritChance:0}%" +
                           (stats.IgniteChance > 0 ? $"   Ignite {stats.IgniteChance:P0}" : "") +
                           (selDef.StunDuration > 0
                               ? $"   Stun {selDef.StunDuration:0.0}s" +
                                 (selDef.StunChance < 1f ? $" ({selDef.StunChance:P0})" : "")
                               : "") +
                           (selDef.Knockback > 0 ? $"   Knockback {selDef.Knockback:0.0}" : "");
        sb.DrawString(font, statLine1, new Vector2(x, y), new Color(235, 225, 200));
        y += 20;
        sb.DrawString(font, statLine2, new Vector2(x, y), new Color(235, 225, 200));
        y += 26;
        if (selDef.RequiredWeapon.HasValue)
        {
            sb.DrawString(small, $"Requires weapon: {selDef.RequiredWeapon}", new Vector2(x, y), new Color(220, 170, 130));
            y += 18;
        }
        if (selDef.RequiresShield)
        {
            sb.DrawString(small, "Requires a shield equipped", new Vector2(x, y), new Color(220, 170, 130));
            y += 18;
        }

        // --- Skill Scroll slots ---
        int unlockedSlots = SkillMath.ScrollSlotsAtLevel(_data, sel.Level);
        int maxSlots = Math.Max(1, SkillMath.MaxScrollSlots(_data));
        sb.DrawString(font, $"Skill Scrolls ({unlockedSlots}/{maxSlots} slots unlocked)", new Vector2(x, y), new Color(200, 170, 255));
        y += 22;
        for (int i = 0; i < maxSlots; i++)
        {
            var rect = new Rectangle(x + i * 54, y, 46, 46);
            bool unlocked = i < unlockedSlots;
            ItemInstance scroll = (sel.Scrolls != null && i < sel.Scrolls.Count) ? sel.Scrolls[i] : null;

            if (!unlocked)
            {
                sb.Draw(TextureGen.Pixel, rect, new Color(16, 16, 18));
                Border(sb, rect, new Color(50, 48, 44));
                var lockFont = FontManager.Get(11);
                var lockSize = lockFont.MeasureString("LOCK");
                sb.DrawString(lockFont, "LOCK", new Vector2(rect.Center.X - lockSize.X / 2, rect.Center.Y - lockSize.Y / 2), new Color(85, 80, 72));
            }
            else if (scroll == null)
            {
                sb.Draw(TextureGen.Pixel, rect, new Color(30, 26, 40));
                Border(sb, rect, new Color(120, 90, 160));
            }
            else
            {
                sb.Draw(TextureGen.Pixel, rect, new Color(70, 44, 74));
                Border(sb, rect, new Color(200, 150, 255));
                var b = scroll.GetBase(_data);
                var scrollFont = FontManager.GetBold(13);
                string initial = b.Name.Length > 0 ? b.Name[..1] : "?";
                var iSize = scrollFont.MeasureString(initial);
                sb.DrawString(scrollFont, initial, new Vector2(rect.Center.X - iSize.X / 2, rect.Center.Y - iSize.Y / 2), Color.White);
            }
            _scrollSlots.Add((rect, i, unlocked, scroll));
        }
        y += 54;
        sb.DrawString(small, "drag a Scroll from your inventory onto a slot · click a Scroll to detach",
            new Vector2(x, y), new Color(130, 124, 112));
        y += 24;

        // --- hotkey assignment ---
        sb.DrawString(font, "Assign to hotbar:", new Vector2(x, y), Color.White);
        y += 22;
        for (int slot = 0; slot < character.Hotbar.Length; slot++)
        {
            var rect = new Rectangle(x + slot * 74, y, 66, 30);
            bool assignedHere = character.Hotbar[slot] == sel.SkillId;
            sb.Draw(TextureGen.Pixel, rect, assignedHere ? new Color(64, 84, 56) : new Color(40, 40, 48));
            Border(sb, rect, assignedHere ? new Color(140, 200, 120) : new Color(80, 78, 66));
            var keyLabel = HotbarKeyLabel(slot);
            var kSize = font.MeasureString(keyLabel);
            sb.DrawString(font, keyLabel, new Vector2(rect.Center.X - kSize.X / 2, rect.Center.Y - kSize.Y / 2), Color.White);
            _hotbarButtons.Add((rect, slot));
        }
    }

    private string HotbarKeyLabel(int slot)
    {
        var input = GameMain.Instance.Input;
        return slot switch
        {
            0 => input.Bindings[InputAction.PrimaryAttack].Display(),
            1 => input.Bindings[InputAction.Skill1].Display(),
            2 => input.Bindings[InputAction.Skill2].Display(),
            3 => input.Bindings[InputAction.Skill3].Display(),
            4 => input.Bindings[InputAction.Skill4].Display(),
            _ => "?",
        };
    }

    public static void DrawSkillIcon(SpriteBatch sb, Rectangle rect, SkillDefinition def)
    {
        var color = def.DamageKind switch
        {
            DamageKind.Fire => new Color(230, 120, 50),
            DamageKind.Arcane => new Color(170, 100, 240),
            DamageKind.Cold => new Color(110, 180, 240),
            DamageKind.Lightning => new Color(240, 230, 110),
            _ => new Color(200, 190, 170),
        };
        sb.Draw(TextureGen.Circle32, rect, color);
    }

    private static void Border(SpriteBatch sb, Rectangle r, Color c)
    {
        sb.Draw(TextureGen.Pixel, new Rectangle(r.X, r.Y, r.Width, 2), c);
        sb.Draw(TextureGen.Pixel, new Rectangle(r.X, r.Bottom - 2, r.Width, 2), c);
        sb.Draw(TextureGen.Pixel, new Rectangle(r.X, r.Y, 2, r.Height), c);
        sb.Draw(TextureGen.Pixel, new Rectangle(r.Right - 2, r.Y, 2, r.Height), c);
    }
}
