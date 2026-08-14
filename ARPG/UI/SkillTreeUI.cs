using FontStashSharp;
using ARPG.Core;
using ARPG.Data;
using ARPG.Net;
using ARPG.Render;
using ARPG.Skills;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ARPG.UI;

/// <summary>
/// The passive skill tree (default P): a PoE-style web of perks. One point per level past
/// the first; nodes unlock outward from the start node. Allocation is a server request —
/// this panel renders whatever the authoritative character says is allocated.
/// </summary>
public class SkillTreeUI
{
    public bool Open;
    private readonly GameData _data;
    private readonly GameClient _client;
    private Rectangle _panelRect;
    private Point _lastMouse;

    private const float UnitPx = 78f;   // tree layout units -> pixels
    private const int NodeRadius = 19;

    public SkillTreeUI(GameData data, GameClient client)
    {
        _data = data;
        _client = client;
    }

    public void Layout(Point screen)
    {
        int w = Math.Min(860, screen.X - 60);
        int h = Math.Min(560, screen.Y - 80);
        _panelRect = new Rectangle(screen.X / 2 - w / 2, 40, w, h);
    }

    public bool Contains(Point p) => Open && _panelRect.Contains(p);

    private Vector2 NodeScreen(PassiveNode node)
    {
        // The cluster is wider than tall; anchor it slightly below the panel center.
        var center = new Vector2(_panelRect.Center.X, _panelRect.Y + _panelRect.Height * 0.42f);
        return center + new Vector2(node.X * UnitPx, node.Y * UnitPx * 0.85f);
    }

    private bool CanAllocate(Sim.CharacterData c, PassiveNode node)
    {
        if (c.AllocatedPassives.Contains(node.Id)) return false;
        if (PassiveTree.PointsForLevel(c.Level) - c.AllocatedPassives.Count <= 0) return false;
        return node.Start || _data.PassiveTree.Neighbors(node.Id)
            .Any(n => c.AllocatedPassives.Contains(n));
    }

    public void Update(InputManager input)
    {
        if (!Open) return;
        _lastMouse = input.MousePosition;
        var character = _client.World.MyCharacter;
        if (character == null) return;

        if (CloseButton.Handle(input, _panelRect))
        {
            Open = false;
            return;
        }

        if (input.MouseLeftPressed && _panelRect.Contains(input.MousePosition))
        {
            foreach (var node in _data.PassiveTree.Nodes)
            {
                var pos = NodeScreen(node);
                if (Vector2.Distance(pos, input.MousePosition.ToVector2()) <= NodeRadius + 3 &&
                    CanAllocate(character, node))
                {
                    _client.RequestAllocatePassive(node.Id);
                    break;
                }
            }
        }

        if (_panelRect.Contains(input.MousePosition))
            input.MouseCapturedByUI = true;
    }

    public void Draw(SpriteBatch sb)
    {
        if (!Open) return;
        var character = _client.World.MyCharacter;
        if (character == null) return;
        var tree = _data.PassiveTree;

        sb.Draw(TextureGen.Pixel, _panelRect, new Color(18, 18, 26, 245));
        Border(sb, _panelRect, new Color(110, 96, 130));
        CloseButton.Draw(sb, _panelRect, _lastMouse);

        int points = PassiveTree.PointsForLevel(character.Level) - character.AllocatedPassives.Count;
        sb.DrawString(FontManager.GetBold(19), "Passive Skill Tree",
            new Vector2(_panelRect.X + 14, _panelRect.Y + 8), new Color(216, 190, 255));
        sb.DrawString(FontManager.Get(15),
            $"Points available: {Math.Max(0, points)}   (1 per level — you are level {character.Level})",
            new Vector2(_panelRect.X + 14, _panelRect.Y + 34),
            points > 0 ? new Color(160, 240, 160) : new Color(160, 156, 145));

        // Connections underneath the nodes: brighter when both ends are allocated.
        foreach (var pair in tree.Connections)
        {
            if (pair is not { Count: 2 } ||
                !tree.ById.TryGetValue(pair[0], out var a) ||
                !tree.ById.TryGetValue(pair[1], out var b)) continue;
            bool lit = character.AllocatedPassives.Contains(a.Id) &&
                       character.AllocatedPassives.Contains(b.Id);
            bool half = character.AllocatedPassives.Contains(a.Id) ||
                        character.AllocatedPassives.Contains(b.Id);
            DrawLine(sb, NodeScreen(a), NodeScreen(b),
                lit ? new Color(230, 200, 110) : half ? new Color(120, 110, 90) : new Color(64, 62, 70), lit ? 3 : 2);
        }

        PassiveNode hovered = null;
        foreach (var node in tree.Nodes)
        {
            var pos = NodeScreen(node);
            bool allocated = character.AllocatedPassives.Contains(node.Id);
            bool allocatable = CanAllocate(character, node);
            bool hover = Vector2.Distance(pos, _lastMouse.ToVector2()) <= NodeRadius + 3;
            if (hover) hovered = node;

            int r = NodeRadius + (node.Start ? 4 : 0);
            var fill = allocated ? new Color(232, 196, 96)
                : allocatable ? new Color(74, 110, 74)
                : new Color(46, 46, 56);
            var rim = allocated ? new Color(255, 232, 160)
                : allocatable ? new Color(140, 220, 140)
                : new Color(90, 88, 100);
            if (hover) rim = Color.White;

            sb.Draw(TextureGen.Circle32, new Rectangle((int)pos.X - r - 2, (int)pos.Y - r - 2, (r + 2) * 2, (r + 2) * 2), rim);
            sb.Draw(TextureGen.Circle32, new Rectangle((int)pos.X - r, (int)pos.Y - r, r * 2, r * 2), fill);

            // Tiny label under each node.
            var nameFont = FontManager.Get(12);
            var ns = nameFont.MeasureString(node.Name);
            sb.DrawString(nameFont, node.Name, new Vector2(pos.X - ns.X / 2, pos.Y + r + 4),
                allocated ? new Color(255, 232, 160) : new Color(180, 176, 165));
        }

        sb.DrawString(FontManager.Get(13),
            "Click a highlighted node to allocate — perks apply instantly and persist with your character.",
            new Vector2(_panelRect.X + 14, _panelRect.Bottom - 24), new Color(130, 126, 115));

        if (hovered != null) DrawTooltip(sb, hovered, character);
    }

    private void DrawTooltip(SpriteBatch sb, PassiveNode node, Sim.CharacterData character)
    {
        var lines = new List<(string text, Color color)>
        {
            (node.Name, new Color(240, 220, 150)),
        };
        if (!string.IsNullOrEmpty(node.Description))
            lines.Add((node.Description, new Color(170, 166, 155)));
        foreach (var fx in node.Effects)
            lines.Add((DescribeEffect(fx), new Color(150, 200, 255)));
        lines.Add(character.AllocatedPassives.Contains(node.Id)
            ? ("Allocated", new Color(232, 196, 96))
            : CanAllocate(character, node)
                ? ("Click to allocate", new Color(150, 230, 150))
                : (PassiveTree.PointsForLevel(character.Level) - character.AllocatedPassives.Count <= 0
                    ? "No points available" : "Not connected yet", new Color(200, 120, 110)));

        var font = FontManager.Get(14);
        float w = lines.Max(l => font.MeasureString(l.text).X) + 20;
        float h = lines.Count * 20 + 12;
        var rect = new Rectangle(_lastMouse.X + 16, _lastMouse.Y + 12, (int)w, (int)h);
        if (rect.Right > _panelRect.Right) rect.X = _lastMouse.X - rect.Width - 8;
        sb.Draw(TextureGen.Pixel, rect, new Color(12, 12, 18, 245));
        Border(sb, rect, new Color(110, 96, 130));
        int y = rect.Y + 6;
        foreach (var (text, color) in lines)
        {
            sb.DrawString(font, text, new Vector2(rect.X + 10, y), color);
            y += 20;
        }
    }

    /// <summary>Human-readable effect line from the StatType, matching how item modifier
    /// tooltips phrase the same stats.</summary>
    private static string DescribeEffect(PassiveNodeEffect fx)
    {
        string name = fx.Stat switch
        {
            Stats.StatType.MaxHealth => "Maximum Life",
            Stats.StatType.MaximumMana => "Maximum Mana",
            Stats.StatType.PhysicalDamage => "% Physical Damage",
            Stats.StatType.SpellDamage => "% Spell Damage",
            Stats.StatType.AttackSpeed => "% Attack Speed",
            Stats.StatType.CastSpeed => "% Cast Speed",
            Stats.StatType.MovementSpeed => "% Movement Speed",
            Stats.StatType.Armor => "Armor",
            Stats.StatType.CriticalChance => "% Critical Chance",
            Stats.StatType.CriticalDamage => "% Critical Damage",
            Stats.StatType.ManaRegeneration => "% Mana Regeneration",
            Stats.StatType.LifeRegeneration => "Life per Second",
            _ => fx.Stat.ToString(),
        };
        return $"+{fx.Value:0.#} {name}";
    }

    private static void DrawLine(SpriteBatch sb, Vector2 a, Vector2 b, Color color, int thickness)
    {
        var d = b - a;
        float len = d.Length();
        float ang = MathF.Atan2(d.Y, d.X);
        sb.Draw(TextureGen.Pixel, a, null, color, ang, new Vector2(0, 0.5f),
            new Vector2(len, thickness), SpriteEffects.None, 0f);
    }

    private static void Border(SpriteBatch sb, Rectangle r, Color c)
    {
        sb.Draw(TextureGen.Pixel, new Rectangle(r.X, r.Y, r.Width, 2), c);
        sb.Draw(TextureGen.Pixel, new Rectangle(r.X, r.Bottom - 2, r.Width, 2), c);
        sb.Draw(TextureGen.Pixel, new Rectangle(r.X, r.Y, 2, r.Height), c);
        sb.Draw(TextureGen.Pixel, new Rectangle(r.Right - 2, r.Y, 2, r.Height), c);
    }
}
