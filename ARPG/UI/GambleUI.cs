using ARPG.Core;
using ARPG.Data;
using ARPG.Items;
using ARPG.Net;
using ARPG.Render;
using FontStashSharp;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ARPG.UI;

/// <summary>
/// The gambler's table: a scrollable list of every gear BASE the character could wear
/// at their level, each with a steep price. You choose the exact base; the rarity and
/// every modifier on it are fate's roll (GambleBalance — rules shared with the server,
/// so the list and prices need no stock roundtrip).
/// </summary>
public class GambleUI
{
    public bool Open;
    private readonly GameData _data;
    private readonly GameClient _client;
    private Rectangle _panelRect, _listRect;
    private Point _lastMouse;
    private int _scroll;
    private const int RowH = 26;

    public readonly WindowDrag Window = new();

    public GambleUI(GameData data, GameClient client)
    {
        _data = data;
        _client = client;
    }

    public void Layout(Point screen)
    {
        _panelRect = Window.Place(new Rectangle(screen.X / 2 - 210, 48, 420, 560), screen);
        _listRect = new Rectangle(_panelRect.X + 10, _panelRect.Y + 86, _panelRect.Width - 20,
            _panelRect.Height - 130);
    }

    public bool Contains(Point p) => Open && _panelRect.Contains(p);

    private List<ItemBase> Bases()
    {
        int level = _client.World.MyCharacter?.Level ?? 1;
        return GambleBalance.EligibleBases(_data, level).ToList();
    }

    public void Update(InputManager input, bool mouseBlocked = false)
    {
        if (!Open || mouseBlocked) return;
        _lastMouse = input.MousePosition;
        if (CloseButton.Handle(input, _panelRect)) { Open = false; return; }
        if (Window.HandleBar(input, WindowDrag.BarFor(_panelRect))) return;
        if (!_panelRect.Contains(_lastMouse)) return;
        input.MouseCapturedByUI = true;

        var bases = Bases();
        int maxScroll = Math.Max(0, bases.Count - _listRect.Height / RowH);
        if (input.ScrollDelta != 0)
            _scroll = Math.Clamp(_scroll - Math.Sign(input.ScrollDelta) * 3, 0, maxScroll);
        _scroll = Math.Clamp(_scroll, 0, maxScroll);

        if (input.MouseLeftPressed && _listRect.Contains(_lastMouse))
        {
            int row = (_lastMouse.Y - _listRect.Y) / RowH + _scroll;
            if (row >= 0 && row < bases.Count)
            {
                int level = _client.World.MyCharacter?.Level ?? 1;
                int price = GambleBalance.Price(bases[row], level);
                if ((_client.World.MyCharacter?.Gold ?? 0) >= price)
                    _client.RequestGamble(bases[row].Id);
            }
        }
    }

    public void Draw(SpriteBatch sb)
    {
        if (!Open) return;
        var character = _client.World.MyCharacter;
        if (character == null) return;

        sb.Draw(TextureGen.Pixel, _panelRect, new Color(24, 20, 30, 242));
        Border(sb, _panelRect, new Color(170, 130, 60));
        WindowDrag.DrawBar(sb, _panelRect, _lastMouse);
        CloseButton.Draw(sb, _panelRect, _lastMouse);

        int x = _panelRect.X + 12;
        sb.DrawString(FontManager.GetBold(19), "Sable the Gambler",
            new Vector2(x, _panelRect.Y + 8), new Color(240, 200, 110));
        sb.DrawString(FontManager.Get(13),
            "pick the base — the rarity and every roll on it are fate's",
            new Vector2(x, _panelRect.Y + 34), new Color(175, 165, 150));
        sb.DrawString(FontManager.Get(15), $"Your gold: {character.Gold}",
            new Vector2(x, _panelRect.Y + 56), new Color(240, 200, 90));

        sb.Draw(TextureGen.Pixel, _listRect, new Color(14, 12, 18, 235));
        var bases = Bases();
        int visible = _listRect.Height / RowH;
        var rowFont = FontManager.Get(14);
        var subFont = FontManager.Get(11);
        for (int i = 0; i < visible; i++)
        {
            int idx = i + _scroll;
            if (idx >= bases.Count) break;
            var b = bases[idx];
            var row = new Rectangle(_listRect.X, _listRect.Y + i * RowH, _listRect.Width, RowH);
            bool hover = row.Contains(_lastMouse);
            int price = GambleBalance.Price(b, character.Level);
            bool afford = character.Gold >= price;
            if (hover)
                sb.Draw(TextureGen.Pixel, row, new Color(60, 50, 36, 200));
            sb.DrawString(rowFont, b.Name, new Vector2(row.X + 6, row.Y + 4),
                afford ? new Color(230, 224, 210) : new Color(130, 124, 112));
            sb.DrawString(subFont, b.Category.ToString(),
                new Vector2(row.X + 200, row.Y + 7), new Color(140, 134, 122));
            string priceText = $"{price} g";
            var pSize = rowFont.MeasureString(priceText);
            sb.DrawString(rowFont, priceText,
                new Vector2(row.Right - pSize.X - 8, row.Y + 4),
                afford ? new Color(240, 200, 90) : new Color(150, 110, 80));
        }
        if (bases.Count > visible)
            sb.DrawString(subFont, $"scroll · {_scroll + 1}-{Math.Min(bases.Count, _scroll + visible)} of {bases.Count}",
                new Vector2(_listRect.X, _listRect.Bottom + 4), new Color(140, 132, 120));
        sb.DrawString(FontManager.Get(12), "click a base to roll it · no refunds",
            new Vector2(x, _panelRect.Bottom - 20), new Color(150, 140, 120));
    }

    private static void Border(SpriteBatch sb, Rectangle r, Color c)
    {
        sb.Draw(TextureGen.Pixel, new Rectangle(r.X, r.Y, r.Width, 2), c);
        sb.Draw(TextureGen.Pixel, new Rectangle(r.X, r.Bottom - 2, r.Width, 2), c);
        sb.Draw(TextureGen.Pixel, new Rectangle(r.X, r.Y, 2, r.Height), c);
        sb.Draw(TextureGen.Pixel, new Rectangle(r.Right - 2, r.Y, 2, r.Height), c);
    }
}
