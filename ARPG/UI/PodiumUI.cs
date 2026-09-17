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
/// The scroll podium's picker: every sealed warp scroll in the bag, with its level and
/// seals. Pick one and it goes on the podium — the server consumes it and opens the
/// portal to the zone it describes.
/// </summary>
public class PodiumUI
{
    public bool Open;
    private readonly GameData _data;
    private readonly GameClient _client;
    private Rectangle _panelRect, _listRect;
    private Point _lastMouse;
    private int _scroll;
    private const int RowH = 44;

    public readonly WindowDrag Window = new();

    public PodiumUI(GameData data, GameClient client)
    {
        _data = data;
        _client = client;
    }

    public void Layout(Point screen)
    {
        _panelRect = Window.Place(new Rectangle(screen.X / 2 - 230, 60, 460, 420), screen);
        _listRect = new Rectangle(_panelRect.X + 10, _panelRect.Y + 66, _panelRect.Width - 20, _panelRect.Height - 104);
    }

    public bool Contains(Point p) => Open && _panelRect.Contains(p);

    private List<ItemInstance> Scrolls()
    {
        var c = _client.World.MyCharacter;
        if (c == null) return new List<ItemInstance>();
        return c.Inventory.Items.Select(pi => pi.Item)
            .Where(it => it.GetBase(_data).Category == ItemCategory.WarpScroll)
            .OrderByDescending(it => it.ItemLevel).ToList();
    }

    public void Update(InputManager input, bool mouseBlocked = false)
    {
        if (!Open || mouseBlocked) return;
        _lastMouse = input.MousePosition;
        if (CloseButton.Handle(input, _panelRect)) { Open = false; return; }
        if (Window.HandleBar(input, WindowDrag.BarFor(_panelRect))) return;
        if (!_panelRect.Contains(_lastMouse)) return;
        input.MouseCapturedByUI = true;
        var scrolls = Scrolls();
        int maxScroll = Math.Max(0, scrolls.Count - _listRect.Height / RowH);
        if (input.ScrollDelta != 0) _scroll = Math.Clamp(_scroll - Math.Sign(input.ScrollDelta), 0, maxScroll);
        _scroll = Math.Clamp(_scroll, 0, maxScroll);
        if (input.MouseLeftPressed && _listRect.Contains(_lastMouse))
        {
            int row = (_lastMouse.Y - _listRect.Y) / RowH + _scroll;
            if (row >= 0 && row < scrolls.Count)
            {
                _client.RequestUsePodium(scrolls[row].InstanceId);
                Open = false;
            }
        }
    }

    public void Draw(SpriteBatch sb)
    {
        if (!Open) return;
        sb.Draw(TextureGen.Pixel, _panelRect, new Color(20, 16, 30, 244));
        Border(sb, _panelRect, new Color(140, 100, 200));
        WindowDrag.DrawBar(sb, _panelRect, _lastMouse);
        CloseButton.Draw(sb, _panelRect, _lastMouse);
        int x = _panelRect.X + 12;
        sb.DrawString(FontManager.GetBold(19), "The Podium", new Vector2(x, _panelRect.Y + 8), new Color(200, 170, 255));
        sb.DrawString(FontManager.Get(13), "place a sealed warp scroll — the portal opens onto the zone its seals describe",
            new Vector2(x, _panelRect.Y + 36), new Color(170, 160, 180));
        sb.Draw(TextureGen.Pixel, _listRect, new Color(12, 10, 18, 235));
        var scrolls = Scrolls();
        var rowFont = FontManager.Get(14);
        var subFont = FontManager.Get(11);
        if (scrolls.Count == 0)
            sb.DrawString(rowFont, "No sealed warp scrolls in your bag. The dead and their chests drop them.",
                new Vector2(_listRect.X + 8, _listRect.Y + 8), new Color(150, 140, 160));
        int visible = _listRect.Height / RowH;
        for (int i = 0; i < visible; i++)
        {
            int idx = i + _scroll;
            if (idx >= scrolls.Count) break;
            var it = scrolls[idx];
            var row = new Rectangle(_listRect.X, _listRect.Y + i * RowH, _listRect.Width, RowH);
            if (row.Contains(_lastMouse)) sb.Draw(TextureGen.Pixel, row, new Color(60, 44, 84, 200));
            var col = WorldRenderer.RarityColor(it.Rarity);
            sb.DrawString(rowFont, $"Sealed Warp Scroll  ·  zone level {it.ItemLevel}", new Vector2(row.X + 8, row.Y + 5), col);
            var seals = string.Join("  ·  ", it.Modifiers
                .Select(m => _data.Modifiers.GetValueOrDefault(m.ModifierId))
                .Where(d => d != null).Select(d => d.Name));
            if (seals.Length > 70) seals = seals[..67] + "...";
            sb.DrawString(subFont, seals.Length > 0 ? seals : "unsealed", new Vector2(row.X + 8, row.Y + 25), new Color(190, 178, 205));
        }
        sb.DrawString(FontManager.Get(12), "click a scroll to place it · the scroll is spent when the portal opens",
            new Vector2(x, _panelRect.Bottom - 22), new Color(150, 140, 160));
    }

    private static void Border(SpriteBatch sb, Rectangle r, Color c)
    {
        sb.Draw(TextureGen.Pixel, new Rectangle(r.X, r.Y, r.Width, 2), c);
        sb.Draw(TextureGen.Pixel, new Rectangle(r.X, r.Bottom - 2, r.Width, 2), c);
        sb.Draw(TextureGen.Pixel, new Rectangle(r.X, r.Y, 2, r.Height), c);
        sb.Draw(TextureGen.Pixel, new Rectangle(r.Right - 2, r.Y, 2, r.Height), c);
    }
}
