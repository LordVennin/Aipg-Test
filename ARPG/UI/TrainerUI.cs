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
/// The skill trainer's panel. Top: the skill BOOKS in your bag, each with a "Hand
/// over" button (the trainer reads it and unlocks its skills for you alone). Below:
/// every skill — known ones greyed, unlocked ones bought for a flat gold price, and
/// locked ones greyed with the book that would unlock them. Every button is just a
/// request; the server enforces reach, price and unlocks.
/// </summary>
public class TrainerUI
{
    public const int SkillPrice = Server.ServerWorld.SkillPrice;
    private const int RowH = 40;

    public bool Open;
    private readonly GameData _data;
    private readonly GameClient _client;
    private Rectangle _panelRect;
    private Point _lastMouse;
    private int _scroll;
    private int _rowCount;
    private readonly List<(Rectangle rect, string skillId)> _buyButtons = new();
    private readonly List<(Rectangle rect, Guid itemId)> _bookButtons = new();
    public readonly WindowDrag Window = new();

    public TrainerUI(GameData data, GameClient client)
    {
        _data = data;
        _client = client;
    }

    public void Layout(Point screen)
    {
        int h = Math.Min(130 + (_data.Skills.Count + 3) * RowH, screen.Y - 80);
        _panelRect = Window.Place(new Rectangle(screen.X / 2 - 250, 40, 500, h), screen);
    }

    public bool Contains(Point p) => Open && _panelRect.Contains(p);

    /// <summary>The skill books in the bag, in bag order.</summary>
    public static List<Items.ItemInstance> BooksInBag(GameData data, Sim.CharacterData c) =>
        c.Inventory.Items.Select(pl => pl.Item).Where(it => data.Items.TryGetValue(it.BaseItemId, out var b) && b.IsSkillBook).ToList();

    /// <summary>What the panel shows for a skill: "known", "learn", or "locked" (with the book).</summary>
    public static string SkillState(GameData data, Sim.CharacterData c, bool gated, string skillId, out Items.ItemBase requiredBook)
    {
        requiredBook = null;
        if (c.GetSkill(skillId) != null) return "known";
        if (!gated || c.IsSkillUnlocked(data, skillId)) return "learn";
        requiredBook = Server.ServerWorld.BookFor(data, skillId);
        return "locked";
    }

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
        if (_panelRect.Contains(input.MousePosition) && input.ScrollDelta != 0)
        {
            int visible = Math.Max(1, (_panelRect.Height - 110) / RowH);
            _scroll = Math.Clamp(_scroll - Math.Sign(input.ScrollDelta) * 2, 0, Math.Max(0, _rowCount - visible));
        }
        if (input.MouseLeftPressed)
        {
            foreach (var (rect, itemId) in _bookButtons)
                if (rect.Contains(input.MousePosition))
                {
                    input.MouseCapturedByUI = true;
                    _client.RequestTurnInSkillBook(itemId); // server checks reach + consumes
                    return;
                }
            foreach (var (rect, skillId) in _buyButtons)
                if (rect.Contains(input.MousePosition))
                {
                    input.MouseCapturedByUI = true;
                    _client.RequestLearnSkill(skillId); // server charges + validates
                    break;
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
        bool gated = _client.World.SkillsGated;

        sb.Draw(TextureGen.Pixel, _panelRect, new Color(22, 22, 30, 240));
        Border(sb, _panelRect, new Color(120, 100, 160));
        WindowDrag.DrawBar(sb, _panelRect, _lastMouse);
        CloseButton.Draw(sb, _panelRect, _lastMouse);

        int x = _panelRect.X + 14, y = _panelRect.Y + 8;
        sb.DrawString(FontManager.GetBold(19), "Skill Training", new Vector2(x, y), new Color(210, 190, 255));
        var goldFont = FontManager.GetBold(15);
        string goldText = $"{character.Gold:n0} gold";
        var gSize = goldFont.MeasureString(goldText);
        sb.DrawString(goldFont, goldText,
            new Vector2(_panelRect.Right - 44 - gSize.X, y + 2), new Color(240, 200, 90));
        y += 34;
        var small = FontManager.Get(12);
        sb.DrawString(small, gated
                ? $"Skill books unlock techniques; each one then costs {SkillPrice} gold to learn."
                : $"Each technique costs {SkillPrice} gold, taught on the spot.",
            new Vector2(x, y), new Color(160, 152, 170));
        y += 24;

        _buyButtons.Clear();
        _bookButtons.Clear();
        var font = FontManager.Get(15);
        int rowW = _panelRect.Width - 28;
        int bottom = _panelRect.Bottom - 16;

        // Row model: books first, then skills (learnable, then locked, then known).
        var books = BooksInBag(_data, character);
        var skills = _data.Skills.Values
            .Select(def => (def, state: SkillState(_data, character, gated, def.Id, out var book), book))
            .OrderBy(r => r.state == "learn" ? 0 : r.state == "locked" ? 1 : 2).ThenBy(r => r.def.Name).ToList();
        _rowCount = books.Count + skills.Count;
        int index = 0;
        bool Visible() => index++ >= _scroll && y + RowH - 6 <= bottom;

        foreach (var bookItem in books)
        {
            if (!Visible()) continue;
            var bookBase = bookItem.GetBase(_data);
            var fresh = bookBase.UnlocksSkills.Where(id => _data.Skills.ContainsKey(id) && !character.IsSkillUnlocked(_data, id)).ToList();
            var row = new Rectangle(x, y, rowW, RowH - 6);
            sb.Draw(TextureGen.Pixel, row, new Color(26, 44, 44));
            Border(sb, row, WorldRenderer.SkillBookColor * 0.6f);
            var icon = SpriteGen.GetCurioSprite(bookBase);
            if (icon != null) sb.Draw(icon, new Rectangle(row.X + 4, row.Y + 5, 24, 24), Color.White);
            sb.DrawString(font, bookBase.Name, new Vector2(row.X + 36, row.Y + 2), WorldRenderer.SkillBookColor);
            string teaches = fresh.Count > 0
                ? "unlocks " + string.Join(", ", fresh.Select(id => _data.Skills[id].Name))
                : "nothing new in it for you";
            sb.DrawString(small, teaches, new Vector2(row.X + 36, row.Y + 19), new Color(170, 190, 186));
            var hand = new Rectangle(row.Right - 96, row.Y + 3, 90, 28);
            bool canHand = fresh.Count > 0;
            bool hover = hand.Contains(_lastMouse);
            sb.Draw(TextureGen.Pixel, hand, canHand ? (hover ? new Color(50, 110, 100) : new Color(36, 78, 72)) : new Color(40, 44, 46));
            Border(sb, hand, canHand ? WorldRenderer.SkillBookColor : new Color(80, 86, 88));
            sb.DrawString(small, "Hand over", new Vector2(hand.X + 14, hand.Y + 6), canHand ? new Color(210, 250, 240) : new Color(130, 136, 138));
            if (canHand) _bookButtons.Add((hand, bookItem.InstanceId));
            y += RowH;
        }

        foreach (var (def, state, book) in skills)
        {
            if (!Visible()) continue;
            bool known = state == "known", locked = state == "locked";
            var row = new Rectangle(x, y, rowW, RowH - 6);
            sb.Draw(TextureGen.Pixel, row, locked ? new Color(26, 26, 32) : new Color(30, 30, 40));
            SkillMenuUI.DrawSkillIcon(sb, new Rectangle(row.X + 4, row.Y + 5, 24, 24), def);
            if (locked) sb.Draw(TextureGen.Pixel, new Rectangle(row.X + 4, row.Y + 5, 24, 24), new Color(10, 10, 14, 150));
            sb.DrawString(font, def.Name, new Vector2(row.X + 36, row.Y + (locked ? 2 : 7)),
                known ? new Color(130, 126, 116) : locked ? new Color(112, 110, 120) : Color.White);
            if (known)
            {
                sb.DrawString(small, "Known", new Vector2(row.Right - 62, row.Y + 9), new Color(120, 150, 120));
            }
            else if (locked)
            {
                sb.DrawString(small, book != null ? $"Requires: {book.Name}" : "Locked",
                    new Vector2(row.X + 36, row.Y + 19), new Color(96, 170, 158));
                sb.DrawString(small, "Locked", new Vector2(row.Right - 62, row.Y + 9), new Color(130, 110, 110));
            }
            else
            {
                bool affordable = character.Gold >= SkillPrice;
                var buy = new Rectangle(row.Right - 96, row.Y + 3, 90, 28);
                bool hover = buy.Contains(_lastMouse);
                sb.Draw(TextureGen.Pixel, buy, affordable
                    ? (hover ? new Color(70, 100, 60) : new Color(46, 66, 46))
                    : new Color(50, 40, 40));
                Border(sb, buy, affordable ? new Color(110, 170, 110) : new Color(90, 70, 70));
                sb.DrawString(small, $"{SkillPrice}g  Learn", new Vector2(buy.X + 12, buy.Y + 6),
                    affordable ? new Color(190, 235, 190) : new Color(150, 120, 120));
                _buyButtons.Add((buy, def.Id));
            }
            y += RowH;
        }
        if (_rowCount > 0 && (_scroll > 0 || index > _scroll + Math.Max(1, (_panelRect.Height - 110) / RowH)))
            sb.DrawString(small, "scroll for more", new Vector2(_panelRect.Right - 110, _panelRect.Bottom - 16), new Color(120, 116, 130));
    }

    private static void Border(SpriteBatch sb, Rectangle r, Color c)
    {
        sb.Draw(TextureGen.Pixel, new Rectangle(r.X, r.Y, r.Width, 2), c);
        sb.Draw(TextureGen.Pixel, new Rectangle(r.X, r.Bottom - 2, r.Width, 2), c);
        sb.Draw(TextureGen.Pixel, new Rectangle(r.X, r.Y, 2, r.Height), c);
        sb.Draw(TextureGen.Pixel, new Rectangle(r.Right - 2, r.Y, 2, r.Height), c);
    }
}
