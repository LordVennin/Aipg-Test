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
/// The skill trainer's panel: every teachable skill in one list — known ones greyed,
/// the rest bought for a flat gold price. The BUY is just a LearnSkill request; the
/// server enforces the price and that the buyer is standing at the trainer.
/// </summary>
public class TrainerUI
{
    public const int SkillPrice = Server.ServerWorld.SkillPrice;

    public bool Open;
    private readonly GameData _data;
    private readonly GameClient _client;
    private Rectangle _panelRect;
    private Point _lastMouse;
    private readonly List<(Rectangle rect, string skillId)> _buyButtons = new();
    public readonly WindowDrag Window = new();

    public TrainerUI(GameData data, GameClient client)
    {
        _data = data;
        _client = client;
    }

    public void Layout(Point screen)
    {
        int h = Math.Min(120 + _data.Skills.Count * 40, screen.Y - 80);
        _panelRect = Window.Place(new Rectangle(screen.X / 2 - 230, 40, 460, h), screen);
    }

    public bool Contains(Point p) => Open && _panelRect.Contains(p);

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
        if (input.MouseLeftPressed)
            foreach (var (rect, skillId) in _buyButtons)
                if (rect.Contains(input.MousePosition))
                {
                    input.MouseCapturedByUI = true;
                    _client.RequestLearnSkill(skillId); // server charges + validates
                    break;
                }
        if (_panelRect.Contains(input.MousePosition))
            input.MouseCapturedByUI = true;
    }

    public void Draw(SpriteBatch sb)
    {
        if (!Open) return;
        var character = _client.World.MyCharacter;
        if (character == null) return;

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
        sb.DrawString(small, $"Each technique costs {SkillPrice} gold, taught on the spot.",
            new Vector2(x, y), new Color(160, 152, 170));
        y += 24;

        _buyButtons.Clear();
        var font = FontManager.Get(15);
        foreach (var def in _data.Skills.Values.OrderBy(d => d.Name))
        {
            if (y > _panelRect.Bottom - 40) break;
            bool known = character.GetSkill(def.Id) != null;
            var row = new Rectangle(x, y, _panelRect.Width - 28, 34);
            sb.Draw(TextureGen.Pixel, row, new Color(30, 30, 40));
            SkillMenuUI.DrawSkillIcon(sb, new Rectangle(row.X + 4, row.Y + 5, 24, 24), def);
            sb.DrawString(font, def.Name, new Vector2(row.X + 36, row.Y + 7),
                known ? new Color(130, 126, 116) : Color.White);
            if (known)
            {
                sb.DrawString(small, "Known", new Vector2(row.Right - 62, row.Y + 9), new Color(120, 150, 120));
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
            y += 40;
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
