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
/// Talking to the merchant (pickup key in range) opens DIALOGUE first — flavor line plus
/// options — with the shop as one of the options. The shop shows the merchant's stock as
/// an inventory-style grid (same cells, sprites and tooltips as the player's bag); the
/// player's own inventory panel opens alongside in sell mode, where clicking a bag item
/// sells it. Stock is per player, seeded by (character, level) server-side.
/// </summary>
public class ShopUI
{
    public enum ShopMode { Closed, Dialogue, Shop }
    public ShopMode Mode { get; private set; } = ShopMode.Closed;
    public bool Open => Mode != ShopMode.Closed;

    /// <summary>Fired when the mode changes (PlayScreen syncs the inventory/sell mode).</summary>
    public event Action<ShopMode> ModeChanged;

    private readonly GameData _data;
    private readonly GameClient _client;
    private readonly InventoryUI _inventory; // reused for item-box rendering
    private Rectangle _panelRect;      // shop grid panel
    private Rectangle _dialogueRect;   // dialogue panel
    private int _npcId;
    private List<ClientShopEntry> _stock = new();
    private string _dialogue = "";
    private string _npcName = "Merchant";
    private NpcDefinition _npcDef;
    private readonly Random _rng = new();
    private Point _lastMouse;
    private Point _screenSize;

    private const int Cell = InventoryUI.Cell;
    private const int GridW = 8, GridH = 5;
    /// <summary>Stock slot -> grid cell placement, rebuilt when stock changes.</summary>
    private readonly List<(ClientShopEntry entry, Rectangle rect)> _stockBoxes = new();
    private readonly List<(Rectangle rect, Action onClick, string label)> _dialogueOptions = new();

    /// <summary>Dev/automation: jump straight to the grid when stock arrives (ARPG_DEVUI=shopgrid).</summary>
    public bool DevAutoGrid;

    public ShopUI(GameData data, GameClient client, InventoryUI inventory)
    {
        _data = data;
        _client = client;
        _inventory = inventory;
        _client.ShopStockReceived += (npcId, stock) =>
        {
            _npcId = npcId;
            _stock = stock;
            if (Mode == ShopMode.Closed)
            {
                var npc = _client.World.Npcs.GetValueOrDefault(npcId);
                _npcDef = npc != null ? _data.Npcs.GetValueOrDefault(npc.TypeId) : null;
                _npcName = _npcDef?.Name ?? npc?.Name ?? "Merchant";
                RollDialogue();
                SetMode(ShopMode.Dialogue);
                if (DevAutoGrid) SetMode(ShopMode.Shop);
            }
        };
    }

    private void RollDialogue() =>
        _dialogue = _npcDef is { Dialogue.Count: > 0 }
            ? _npcDef.Dialogue[_rng.Next(_npcDef.Dialogue.Count)]
            : "";

    private void SetMode(ShopMode mode)
    {
        if (Mode == mode) return;
        Mode = mode;
        ModeChanged?.Invoke(mode);
    }

    public void Close() => SetMode(ShopMode.Closed);

    public readonly WindowDrag Window = new();

    public void Layout(Point screen)
    {
        _screenSize = screen;
        // Shop panel sits LEFT so the player's inventory (right side) fits beside it.
        int w = 20 + GridW * Cell;
        _panelRect = Window.Place(new Rectangle(16, 36, w, 250 + GridH * Cell), screen);
        _dialogueRect = new Rectangle(screen.X / 2 - 250, screen.Y / 2 - 170, 500, 250);
    }

    public bool Contains(Point p) => Mode switch
    {
        ShopMode.Dialogue => _dialogueRect.Contains(p),
        ShopMode.Shop => _panelRect.Contains(p),
        _ => false,
    };

    public void Update(InputManager input, bool mouseBlocked = false)
    {
        if (Mode == ShopMode.Closed) return;
        if (mouseBlocked) return; // a window above this one holds the mouse
        _lastMouse = input.MousePosition;

        if (Mode == ShopMode.Dialogue)
        {
            if (CloseButton.Handle(input, _dialogueRect)) { Close(); return; }
            if (input.MouseLeftPressed)
                foreach (var (rect, onClick, _) in _dialogueOptions)
                    if (rect.Contains(input.MousePosition))
                    {
                        input.MouseCapturedByUI = true;
                        onClick();
                        break;
                    }
            if (_dialogueRect.Contains(input.MousePosition))
                input.MouseCapturedByUI = true;
            return;
        }

        // --- shop grid ---
        if (CloseButton.Handle(input, _panelRect)) { Close(); return; }
        if (Window.HandleBar(input, WindowDrag.BarFor(_panelRect))) return;
        if (input.MouseLeftPressed)
        {
            var gold = _client.World.MyCharacter?.Gold ?? 0;
            foreach (var (entry, rect) in _stockBoxes)
                if (rect.Contains(input.MousePosition))
                {
                    input.MouseCapturedByUI = true;
                    if (!entry.Sold && gold >= entry.Price)
                        _client.RequestShopBuy(_npcId, entry.Slot);
                    break;
                }
        }
        if (_panelRect.Contains(input.MousePosition))
            input.MouseCapturedByUI = true;
    }

    public void Draw(SpriteBatch sb, Point screenSize)
    {
        if (Mode == ShopMode.Closed) return;
        var character = _client.World.MyCharacter;
        if (character == null) return;

        if (Mode == ShopMode.Dialogue)
        {
            DrawDialogue(sb);
            return;
        }

        // ---------------- shop grid panel
        sb.Draw(TextureGen.Pixel, _panelRect, new Color(22, 22, 30, 240));
        Border(sb, _panelRect, new Color(120, 100, 60));
        WindowDrag.DrawBar(sb, _panelRect, _lastMouse);
        CloseButton.Draw(sb, _panelRect, _lastMouse);

        int x = _panelRect.X + 10, y = _panelRect.Y + 8;
        sb.DrawString(FontManager.GetBold(19), _npcName, new Vector2(x, y), new Color(240, 210, 130));
        y += 26;
        if (!string.IsNullOrEmpty(_dialogue))
        {
            sb.DrawString(FontManager.Get(13), $"\"{Truncate(_dialogue, 52)}\"", new Vector2(x, y),
                new Color(180, 172, 150));
            y += 22;
        }
        sb.DrawString(FontManager.Get(15), $"Your gold: {character.Gold}", new Vector2(x, y),
            new Color(240, 200, 90));
        y += 26;

        // The stock grid — same cell size and item boxes as the player's bag.
        var gridRect = new Rectangle(x, y, GridW * Cell, GridH * Cell);
        sb.Draw(TextureGen.Pixel, gridRect, new Color(14, 14, 18, 235));
        for (int gy = 0; gy <= GridH; gy++)
            sb.Draw(TextureGen.Pixel, new Rectangle(gridRect.X, gridRect.Y + gy * Cell, gridRect.Width, 1), new Color(50, 48, 44));
        for (int gx = 0; gx <= GridW; gx++)
            sb.Draw(TextureGen.Pixel, new Rectangle(gridRect.X + gx * Cell, gridRect.Y, 1, gridRect.Height), new Color(50, 48, 44));

        LayoutStock(gridRect);
        ItemInstance hoverItem = null;
        int hoverPrice = 0;
        bool hoverSold = false;
        foreach (var (entry, rect) in _stockBoxes)
        {
            _inventory.DrawItemBox(sb, rect, entry.Item);
            if (entry.Sold)
            {
                sb.Draw(TextureGen.Pixel, rect, new Color(10, 10, 12, 190));
                var soldFont = FontManager.GetBold(13);
                var ss = soldFont.MeasureString("SOLD");
                sb.DrawString(soldFont, "SOLD",
                    new Vector2(rect.Center.X - ss.X / 2, rect.Center.Y - ss.Y / 2), new Color(200, 120, 110));
            }
            if (rect.Contains(_lastMouse))
            {
                hoverItem = entry.Item;
                hoverPrice = entry.Price;
                hoverSold = entry.Sold;
                sb.Draw(TextureGen.Pixel, rect, new Color(255, 255, 255, 26));
            }
        }

        // Price strip under the grid for the hovered item (tooltip shows the details).
        int stripY = gridRect.Bottom + 8;
        if (hoverItem != null)
        {
            bool affordable = character.Gold >= hoverPrice;
            string line = hoverSold ? "Sold out." :
                $"{hoverItem.DisplayName(_data)} — {hoverPrice} gold" + (affordable ? "  (click to buy)" : "  (not enough gold)");
            sb.DrawString(FontManager.Get(14), Truncate(line, 56), new Vector2(x, stripY),
                hoverSold ? new Color(150, 120, 120) : affordable ? new Color(240, 200, 90) : new Color(210, 120, 100));
        }
        else
        {
            sb.DrawString(FontManager.Get(13), "Hover an item for details · click to buy",
                new Vector2(x, stripY), new Color(140, 135, 120));
            sb.DrawString(FontManager.Get(13), "Sell by clicking items in YOUR bag (right panel)",
                new Vector2(x, stripY + 18), new Color(140, 135, 120));
        }

        if (hoverItem != null)
            ItemTooltip.Draw(sb, _data, hoverItem, _lastMouse, screenSize);
    }

    private void DrawDialogue(SpriteBatch sb)
    {
        sb.Draw(TextureGen.Pixel, _dialogueRect, new Color(22, 22, 30, 245));
        Border(sb, _dialogueRect, new Color(120, 100, 60));
        CloseButton.Draw(sb, _dialogueRect, _lastMouse);

        int x = _dialogueRect.X + 16, y = _dialogueRect.Y + 12;
        sb.DrawString(FontManager.GetBold(19), _npcName, new Vector2(x, y), new Color(240, 210, 130));
        y += 30;

        // Wrapped dialogue line.
        var font = FontManager.Get(15);
        foreach (string line in Wrap(_dialogue, 58))
        {
            sb.DrawString(font, $"{line}", new Vector2(x, y), new Color(200, 192, 170));
            y += 20;
        }
        y += 10;

        _dialogueOptions.Clear();
        void Option(string label, Action onClick)
        {
            var rect = new Rectangle(x, y, _dialogueRect.Width - 32, 30);
            bool hovered = rect.Contains(_lastMouse);
            sb.Draw(TextureGen.Pixel, rect, hovered ? new Color(58, 52, 40, 235) : new Color(36, 36, 44, 220));
            sb.DrawString(FontManager.Get(15), label, new Vector2(rect.X + 10, rect.Y + 5),
                hovered ? new Color(255, 236, 170) : new Color(215, 210, 195));
            _dialogueOptions.Add((rect, onClick, label));
            y += 36;
        }
        Option("Let me see your wares.", () => SetMode(ShopMode.Shop));
        Option("Anything else on your mind?", RollDialogue);
        Option("Farewell.", Close);
    }

    /// <summary>First-fit the stock items into the grid, left-to-right, per row —
    /// deterministic, so boxes never move between frames or reopens.</summary>
    private void LayoutStock(Rectangle gridRect)
    {
        _stockBoxes.Clear();
        var occupied = new bool[GridW, GridH];
        foreach (var entry in _stock.OrderBy(e => e.Slot))
        {
            var b = entry.Item.GetBase(_data);
            int w = Math.Min(b.InventoryWidth, GridW), h = Math.Min(b.InventoryHeight, GridH);
            for (int gy = 0; gy <= GridH - h; gy++)
            {
                bool placed = false;
                for (int gx = 0; gx <= GridW - w; gx++)
                {
                    bool fits = true;
                    for (int dy = 0; dy < h && fits; dy++)
                        for (int dx = 0; dx < w && fits; dx++)
                            fits = !occupied[gx + dx, gy + dy];
                    if (!fits) continue;
                    for (int dy = 0; dy < h; dy++)
                        for (int dx = 0; dx < w; dx++)
                            occupied[gx + dx, gy + dy] = true;
                    _stockBoxes.Add((entry, new Rectangle(
                        gridRect.X + gx * Cell + 1, gridRect.Y + gy * Cell + 1,
                        w * Cell - 2, h * Cell - 2)));
                    placed = true;
                    break;
                }
                if (placed) break;
            }
        }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)] + "…";

    private static IEnumerable<string> Wrap(string text, int width)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        var words = text.Split(' ');
        string line = "";
        foreach (var word in words)
        {
            if (line.Length + word.Length + 1 > width && line.Length > 0)
            {
                yield return line;
                line = word;
            }
            else line = line.Length == 0 ? word : line + " " + word;
        }
        if (line.Length > 0) yield return line;
    }

    private static void Border(SpriteBatch sb, Rectangle r, Color c)
    {
        sb.Draw(TextureGen.Pixel, new Rectangle(r.X, r.Y, r.Width, 2), c);
        sb.Draw(TextureGen.Pixel, new Rectangle(r.X, r.Bottom - 2, r.Width, 2), c);
        sb.Draw(TextureGen.Pixel, new Rectangle(r.X, r.Y, 2, r.Height), c);
        sb.Draw(TextureGen.Pixel, new Rectangle(r.Right - 2, r.Y, 2, r.Height), c);
    }
}
