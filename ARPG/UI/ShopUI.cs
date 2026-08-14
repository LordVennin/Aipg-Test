using FontStashSharp;
using ARPG.Core;
using ARPG.Data;
using ARPG.Items;
using ARPG.Net;
using ARPG.Render;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ARPG.UI;

/// <summary>
/// The merchant shop (opened with the pickup key near an NPC). Left: the merchant's
/// stock for THIS player — seeded per (character, level) server-side, so it rerolls on
/// level-up and never by rejoining. Right: your inventory, click to sell. Hover either
/// side for full item tooltips.
/// </summary>
public class ShopUI
{
    public bool Open;
    private readonly GameData _data;
    private readonly GameClient _client;
    private Rectangle _panelRect;
    private int _npcId;
    private List<ClientShopEntry> _stock = new();
    private string _dialogue = "";
    private string _npcName = "Merchant";
    private readonly Random _rng = new();
    private Point _lastMouse;

    // Row rects rebuilt each Draw, hit-tested in Update the following frame (UI runs
    // before Draw, so clicks land on last frame's layout — fine at 60 fps).
    private readonly List<(Rectangle rect, int slot)> _buyRows = new();
    private readonly List<(Rectangle rect, Guid instanceId)> _sellRows = new();

    public ShopUI(GameData data, GameClient client)
    {
        _data = data;
        _client = client;
        _client.ShopStockReceived += (npcId, stock) =>
        {
            _npcId = npcId;
            _stock = stock;
            if (!Open)
            {
                Open = true;
                var npc = _client.World.Npcs.GetValueOrDefault(npcId);
                var def = npc != null ? _data.Npcs.GetValueOrDefault(npc.TypeId) : null;
                _npcName = def?.Name ?? npc?.Name ?? "Merchant";
                _dialogue = def is { Dialogue.Count: > 0 }
                    ? def.Dialogue[_rng.Next(def.Dialogue.Count)]
                    : "";
            }
        };
    }

    public void Layout(Point screen)
    {
        _panelRect = new Rectangle(screen.X / 2 - 340, 40, 680, Math.Min(600, screen.Y - 70));
    }

    public bool Contains(Point p) => Open && _panelRect.Contains(p);

    public void Update(InputManager input)
    {
        if (!Open) return;
        _lastMouse = input.MousePosition;
        if (CloseButton.Handle(input, _panelRect))
        {
            Open = false;
            return;
        }
        if (input.MouseLeftPressed)
        {
            var gold = _client.World.MyCharacter?.Gold ?? 0;
            foreach (var (rect, slot) in _buyRows)
                if (rect.Contains(input.MousePosition))
                {
                    var entry = _stock.FirstOrDefault(e => e.Slot == slot);
                    if (entry is { Sold: false } && gold >= entry.Price)
                        _client.RequestShopBuy(_npcId, slot);
                    break;
                }
            foreach (var (rect, instanceId) in _sellRows)
                if (rect.Contains(input.MousePosition))
                {
                    _client.RequestShopSell(instanceId);
                    break;
                }
        }
        if (_panelRect.Contains(input.MousePosition))
            input.MouseCapturedByUI = true;
    }

    public void Draw(SpriteBatch sb, Point screenSize)
    {
        if (!Open) return;
        var character = _client.World.MyCharacter;
        if (character == null) return;

        sb.Draw(TextureGen.Pixel, _panelRect, new Color(22, 22, 30, 240));
        Border(sb, _panelRect, new Color(120, 100, 60));
        CloseButton.Draw(sb, _panelRect, _lastMouse);

        var title = FontManager.GetBold(19);
        var font = FontManager.Get(15);
        var small = FontManager.Get(13);
        int x = _panelRect.X + 14, y = _panelRect.Y + 8;

        sb.DrawString(title, _npcName, new Vector2(x, y), new Color(240, 210, 130));
        y += 26;
        if (!string.IsNullOrEmpty(_dialogue))
        {
            sb.DrawString(small, $"\"{_dialogue}\"", new Vector2(x, y), new Color(180, 172, 150));
            y += 22;
        }
        sb.DrawString(font, $"Your gold: {character.Gold}", new Vector2(x, y), new Color(240, 200, 90));
        y += 26;

        int colW = (_panelRect.Width - 42) / 2;
        int leftX = x, rightX = _panelRect.X + 28 + colW;
        int listTop = y;
        ItemInstance hoverItem = null;

        // ---------------- left: merchant stock (click to buy)
        sb.DrawString(FontManager.GetBold(15), "For sale  (click to buy)", new Vector2(leftX, listTop),
            new Color(150, 200, 150));
        _buyRows.Clear();
        int rowY = listTop + 24;
        foreach (var entry in _stock)
        {
            var rect = new Rectangle(leftX, rowY, colW, 34);
            bool hovered = rect.Contains(_lastMouse);
            bool affordable = character.Gold >= entry.Price;
            sb.Draw(TextureGen.Pixel, rect,
                entry.Sold ? new Color(28, 28, 32, 200) :
                hovered ? new Color(52, 48, 38, 230) : new Color(34, 34, 42, 220));
            var nameColor = entry.Sold ? new Color(110, 110, 110) : RarityColor(entry.Item.Rarity);
            sb.DrawString(font, entry.Item.DisplayName(_data), new Vector2(rect.X + 6, rect.Y + 2), nameColor);
            string priceText = entry.Sold ? "SOLD" : $"{entry.Price} gold";
            var priceColor = entry.Sold ? new Color(120, 90, 90)
                : affordable ? new Color(240, 200, 90) : new Color(200, 110, 90);
            sb.DrawString(small, priceText, new Vector2(rect.X + 6, rect.Y + 18), priceColor);
            if (!entry.Sold)
            {
                _buyRows.Add((rect, entry.Slot));
                if (hovered) hoverItem = entry.Item;
            }
            rowY += 38;
        }

        // ---------------- right: your inventory (click to sell)
        sb.DrawString(FontManager.GetBold(15), "Your items  (click to sell)", new Vector2(rightX, listTop),
            new Color(200, 160, 140));
        _sellRows.Clear();
        rowY = listTop + 24;
        int maxRows = (_panelRect.Bottom - rowY - 10) / 24;
        var items = character.Inventory.Items;
        for (int i = 0; i < items.Count && i < maxRows; i++)
        {
            var placed = items[i];
            var rect = new Rectangle(rightX, rowY, colW, 22);
            bool hovered = rect.Contains(_lastMouse);
            if (hovered) sb.Draw(TextureGen.Pixel, rect, new Color(52, 44, 38, 230));
            int sellPrice = Math.Max(1, placed.Item.GoldValue(_data));
            string label = placed.Item.DisplayName(_data);
            if (placed.Item.StackCount > 1) label += $" x{placed.Item.StackCount}";
            sb.DrawString(small, label, new Vector2(rect.X + 4, rect.Y + 3), RarityColor(placed.Item.Rarity));
            string priceText = $"{sellPrice}g";
            var ps = small.MeasureString(priceText);
            sb.DrawString(small, priceText, new Vector2(rect.Right - ps.X - 6, rect.Y + 3), new Color(240, 200, 90));
            _sellRows.Add((rect, placed.Item.InstanceId));
            if (hovered) hoverItem = placed.Item;
            rowY += 24;
        }
        if (items.Count > maxRows)
            sb.DrawString(small, $"...and {items.Count - maxRows} more", new Vector2(rightX, rowY + 2),
                new Color(140, 135, 120));

        if (hoverItem != null)
            ItemTooltip.Draw(sb, _data, hoverItem, _lastMouse, screenSize);
    }

    private static Color RarityColor(ItemRarity rarity) => rarity switch
    {
        ItemRarity.Magic => new Color(120, 150, 255),
        ItemRarity.Rare => new Color(255, 220, 100),
        _ => new Color(220, 220, 220),
    };

    private static void Border(SpriteBatch sb, Rectangle r, Color c)
    {
        sb.Draw(TextureGen.Pixel, new Rectangle(r.X, r.Y, r.Width, 2), c);
        sb.Draw(TextureGen.Pixel, new Rectangle(r.X, r.Bottom - 2, r.Width, 2), c);
        sb.Draw(TextureGen.Pixel, new Rectangle(r.X, r.Y, 2, r.Height), c);
        sb.Draw(TextureGen.Pixel, new Rectangle(r.Right - 2, r.Y, 2, r.Height), c);
    }
}
