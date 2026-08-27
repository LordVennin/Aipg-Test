using FontStashSharp;
using ARPG.Core;
using ARPG.Data;
using ARPG.Inventory;
using ARPG.Items;
using ARPG.Net;
using ARPG.Render;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ARPG.UI;

/// <summary>An in-flight drag operation, shared between inventory and skill menu.</summary>
public class DragState
{
    public ItemInstance Item;
    public ItemLocation Source;
    public bool Active => Item != null;

    public void Clear() => Item = null;
}

/// <summary>
/// Grid inventory + equipment panel with drag & drop. All mutations are requests to the
/// server; the panel renders whatever authoritative CharacterData the client last received.
/// </summary>
public class InventoryUI
{
    public bool Open;
    private readonly GameData _data;
    private readonly GameClient _client;
    private readonly DragState _drag;

    private Rectangle _panelRect;
    private Rectangle _gridRect;
    private readonly Dictionary<EquipSlot, Rectangle> _slotRects = new();
    public const int Cell = 42;

    /// <summary>Item under the mouse this frame (for the tooltip), grid or equipment.</summary>
    public ItemInstance HoveredItem { get; private set; }

    /// <summary>Armed Enchanting Scroll (right-clicked); the next left-click applies it.
    /// Shared with the stash panel — scrolls arm and apply from either container.</summary>
    private Guid? _pendingScrollId;
    public Guid? PendingScrollId { get => _pendingScrollId; set => _pendingScrollId = value; }
    public bool EnchantModeActive => _pendingScrollId != null;
    public void CancelEnchantMode() => _pendingScrollId = null;

    /// <summary>Find an item by instance id in the bag OR any stash (armed scrolls can
    /// live in either).</summary>
    public Inventory.PlacedItem FindAnywhere(Sim.CharacterData character, Guid id) =>
        character.Inventory.FindByInstance(id)
        ?? character.Stashes.Values.Select(s => s.FindByInstance(id)).FirstOrDefault(x => x != null);

    /// <summary>Sell mode (set while a merchant shop is open): CTRL+clicking a BAG item
    /// sells it (or drag it onto the shop window). Cleared when the shop closes.</summary>
    public Action<ItemInstance> SellClickHandler;

    /// <summary>Quick-move (set while the stash is open, shop closed): CTRL+clicking a
    /// BAG item sends it straight to the stash. Cleared by PlayScreen otherwise.</summary>
    public Action<ItemInstance> QuickMoveHandler;

    public InventoryUI(GameData data, GameClient client, DragState drag)
    {
        _data = data;
        _client = client;
        _drag = drag;
    }

    public readonly WindowDrag Window = new();

    public void Layout(Point screen)
    {
        int w = 20 + 10 * Cell;
        int h = 640;
        _panelRect = Window.Place(new Rectangle(screen.X - w - 12, 36, w, h), screen);
        int x = _panelRect.X, y = _panelRect.Y;
        _gridRect = new Rectangle(x + 10, y + 372, 10 * Cell, 6 * Cell);

        _slotRects[EquipSlot.Helmet] = new Rectangle(x + 190, y + 40, 60, 60);
        _slotRects[EquipSlot.Amulet] = new Rectangle(x + 262, y + 56, 44, 44);
        _slotRects[EquipSlot.MainHand] = new Rectangle(x + 46, y + 110, 76, 130);
        _slotRects[EquipSlot.BodyArmor] = new Rectangle(x + 172, y + 110, 96, 130);
        _slotRects[EquipSlot.OffHand] = new Rectangle(x + 318, y + 110, 76, 130);
        _slotRects[EquipSlot.Gloves] = new Rectangle(x + 46, y + 252, 60, 60);
        _slotRects[EquipSlot.Ring1] = new Rectangle(x + 128, y + 262, 40, 40);
        _slotRects[EquipSlot.Belt] = new Rectangle(x + 178, y + 262, 84, 40);
        _slotRects[EquipSlot.Ring2] = new Rectangle(x + 272, y + 262, 40, 40);
        _slotRects[EquipSlot.Boots] = new Rectangle(x + 334, y + 252, 60, 60);
        // Flask strip between the belt row and the bag grid.
        _slotRects[EquipSlot.Flask1] = new Rectangle(x + 128, y + 316, 40, 48);
        _slotRects[EquipSlot.Flask2] = new Rectangle(x + 272, y + 316, 40, 48);
    }

    public bool Contains(Point p) => Open && _panelRect.Contains(p);

    public void Update(InputManager input, bool mouseBlocked = false)
    {
        HoveredItem = null;
        if (!Open) return;
        var character = _client.World.MyCharacter;
        if (character == null) return;
        if (mouseBlocked) return; // a window above this one holds the mouse
        var mouse = input.MousePosition;

        if (CloseButton.Handle(input, _panelRect))
        {
            Open = false;
            CancelEnchantMode();
            return;
        }
        if (Window.HandleBar(input, WindowDrag.BarFor(_panelRect))) return;

        if (_panelRect.Contains(mouse))
            input.MouseCapturedByUI = true;

        // --- hover detection ---
        var hoveredPlaced = HitGridItem(character, mouse);
        EquipSlot? hoveredSlot = HitEquipSlot(mouse);
        if (!_drag.Active)
        {
            if (hoveredPlaced != null) HoveredItem = hoveredPlaced.Item;
            else if (hoveredSlot.HasValue)
                HoveredItem = character.Equipment.GetValueOrDefault(hoveredSlot.Value);
        }

        // --- enchant apply mode: right-click armed a scroll; left-click applies it ---
        if (_pendingScrollId.HasValue)
        {
            if (FindAnywhere(character, _pendingScrollId.Value) == null)
            {
                _pendingScrollId = null; // scroll consumed/moved elsewhere
            }
            else if (input.MouseRightPressed || input.WasKeyPressed(Microsoft.Xna.Framework.Input.Keys.Escape))
            {
                _pendingScrollId = null;
                input.MouseCapturedByUI = true;
                return;
            }
            else if (input.MouseLeftPressed)
            {
                input.MouseCapturedByUI = true; // this click belongs to the enchant flow
                ItemInstance target = hoveredPlaced?.Item;
                if (target == null && hoveredSlot.HasValue)
                    target = character.Equipment.GetValueOrDefault(hoveredSlot.Value);
                if (target != null && target.InstanceId != _pendingScrollId.Value &&
                    target.GetBase(_data).Category != ItemCategory.EnchantScroll)
                {
                    _client.RequestApplyEnchant(_pendingScrollId.Value, target.InstanceId);
                }
                _pendingScrollId = null; // one application per arm; clicking elsewhere cancels
                return;
            }
        }

        // --- ctrl+click quick actions: sell to the open shop, or shuttle to the open
        // stash. A PLAIN click never sells any more — it just starts a drag.
        if (!_drag.Active && !_pendingScrollId.HasValue && input.MouseLeftPressed &&
            input.CtrlDown && hoveredPlaced != null)
        {
            if (SellClickHandler != null)
            {
                input.MouseCapturedByUI = true;
                SellClickHandler(hoveredPlaced.Item);
                return;
            }
            if (QuickMoveHandler != null)
            {
                input.MouseCapturedByUI = true;
                QuickMoveHandler(hoveredPlaced.Item);
                return;
            }
        }

        // --- start drag ---
        if (!_drag.Active && !_pendingScrollId.HasValue && input.MouseLeftPressed && _panelRect.Contains(mouse))
        {
            if (hoveredPlaced != null)
            {
                _drag.Item = hoveredPlaced.Item;
                _drag.Source = ItemLocation.AtGrid(hoveredPlaced.X, hoveredPlaced.Y);
            }
            else if (hoveredSlot.HasValue && character.Equipment.GetValueOrDefault(hoveredSlot.Value) != null)
            {
                _drag.Item = character.Equipment[hoveredSlot.Value];
                _drag.Source = ItemLocation.AtEquip(hoveredSlot.Value);
            }
        }

        // --- quick actions (right click): arm an Enchanting Scroll / equip / unequip ---
        if (!_drag.Active && input.MouseRightPressed)
        {
            if (hoveredPlaced != null && hoveredPlaced.Item.GetBase(_data).Category == ItemCategory.EnchantScroll)
            {
                _pendingScrollId = hoveredPlaced.Item.InstanceId;
            }
            else if (hoveredPlaced != null)
            {
                var slots = ItemBase.CompatibleSlots(hoveredPlaced.Item.GetBase(_data).Category);
                if (slots.Count > 0)
                {
                    // Prefer an empty compatible slot, otherwise swap into the first.
                    var target = slots.FirstOrDefault(s => character.Equipment.GetValueOrDefault(s) == null);
                    if (!slots.Contains(target)) target = slots[0];
                    _client.RequestMoveItem(ItemLocation.AtGrid(hoveredPlaced.X, hoveredPlaced.Y), ItemLocation.AtEquip(target));
                }
            }
            else if (hoveredSlot.HasValue && character.Equipment.GetValueOrDefault(hoveredSlot.Value) != null)
            {
                _client.RequestMoveItem(ItemLocation.AtEquip(hoveredSlot.Value), ItemLocation.AtGrid(0, 0));
            }
        }
    }

    /// <summary>Try to complete a drag released over this panel. Returns true if handled.</summary>
    public bool TryDropAt(Point mouse)
    {
        if (!Open || !_drag.Active || !_panelRect.Contains(mouse)) return false;
        var character = _client.World.MyCharacter;

        var slot = HitEquipSlot(mouse);
        if (slot.HasValue)
        {
            _client.RequestMoveItem(_drag.Source, ItemLocation.AtEquip(slot.Value));
            return true;
        }
        if (_gridRect.Contains(mouse))
        {
            var b = _drag.Item.GetBase(_data);
            int cellX = (int)MathF.Floor((mouse.X - _gridRect.X) / (float)Cell - (b.InventoryWidth - 1) / 2f);
            int cellY = (int)MathF.Floor((mouse.Y - _gridRect.Y) / (float)Cell - (b.InventoryHeight - 1) / 2f);
            cellX = Math.Clamp(cellX, 0, character.Inventory.Width - b.InventoryWidth);
            cellY = Math.Clamp(cellY, 0, character.Inventory.Height - b.InventoryHeight);
            _client.RequestMoveItem(_drag.Source, ItemLocation.AtGrid(cellX, cellY));
            return true;
        }
        return true; // released on the panel but not on a target: cancel silently
    }

    private PlacedItem HitGridItem(Sim.CharacterData character, Point mouse)
    {
        if (!_gridRect.Contains(mouse)) return null;
        int cx = (mouse.X - _gridRect.X) / Cell;
        int cy = (mouse.Y - _gridRect.Y) / Cell;
        return character.Inventory.ItemAtCell(cx, cy, _data);
    }

    private EquipSlot? HitEquipSlot(Point mouse)
    {
        foreach (var (slot, rect) in _slotRects)
            if (rect.Contains(mouse))
                return slot;
        return null;
    }

    public void Draw(SpriteBatch sb, InputManager input)
    {
        if (!Open) return;
        var character = _client.World.MyCharacter;
        if (character == null) return;

        sb.Draw(TextureGen.Pixel, _panelRect, new Color(22, 22, 30, 240));
        DrawBorder(sb, _panelRect, new Color(95, 88, 62));
        WindowDrag.DrawBar(sb, _panelRect, input.MousePosition);
        var title = FontManager.GetBold(19);
        sb.DrawString(title, "Equipment & Inventory", new Vector2(_panelRect.X + 12, _panelRect.Y + 8), new Color(230, 215, 165));
        CloseButton.Draw(sb, _panelRect, input.MousePosition);

        // --- gold display (below the drag bar so it never overlaps the grip dots) ---
        var goldFont = FontManager.GetBold(16);
        string goldText = $"{character.Gold:n0}";
        var goldSize = goldFont.MeasureString(goldText);
        var pile = SpriteGen.GetGoldPile();
        float goldX = _panelRect.Right - 12 - goldSize.X;
        int goldY = _panelRect.Y + 32;
        if (pile != null)
            sb.Draw(pile, new Rectangle((int)goldX - pile.Width * 2 - 6, goldY + 2, pile.Width * 2, pile.Height * 2), Color.White);
        sb.DrawString(goldFont, goldText, new Vector2(goldX, goldY), new Color(240, 200, 90));

        // --- equipment slots ---
        var inactiveGear = _client.World.MyStats.InactiveItems;
        foreach (var (slot, rect) in _slotRects)
        {
            sb.Draw(TextureGen.Pixel, rect, new Color(14, 14, 20));
            DrawBorder(sb, rect, new Color(70, 66, 54));
            var item = character.Equipment.GetValueOrDefault(slot);
            if (item != null && (!_drag.Active || _drag.Item.InstanceId != item.InstanceId))
            {
                DrawItemBox(sb, rect, item);
                // Requirements no longer met: the piece is dead weight — flag it red.
                if (inactiveGear != null && inactiveGear.Contains(item.InstanceId))
                {
                    sb.Draw(TextureGen.Pixel, rect, new Color(190, 35, 25, 80));
                    DrawBorder(sb, rect, ItemTooltip.UnmetColor);
                    var xFont = FontManager.GetBold(14);
                    var xSize = xFont.MeasureString("!");
                    sb.DrawString(xFont, "!",
                        new Vector2(rect.Right - xSize.X - 4, rect.Y + 1), ItemTooltip.UnmetColor);
                }
            }
            else if (item == null)
            {
                var font = FontManager.Get(11);
                string label = SlotLabel(slot);
                var size = font.MeasureString(label);
                sb.DrawString(font, label,
                    new Vector2(rect.Center.X - size.X / 2, rect.Center.Y - size.Y / 2), new Color(90, 86, 76));
            }
        }

        // --- grid ---
        for (int y = 0; y <= character.Inventory.Height; y++)
            sb.Draw(TextureGen.Pixel, new Rectangle(_gridRect.X, _gridRect.Y + y * Cell, _gridRect.Width, 1), new Color(50, 48, 44));
        for (int x = 0; x <= character.Inventory.Width; x++)
            sb.Draw(TextureGen.Pixel, new Rectangle(_gridRect.X + x * Cell, _gridRect.Y, 1, _gridRect.Height), new Color(50, 48, 44));

        foreach (var placed in character.Inventory.Items)
        {
            if (_drag.Active && _drag.Item.InstanceId == placed.Item.InstanceId) continue;
            var b = placed.Item.GetBase(_data);
            var rect = new Rectangle(_gridRect.X + placed.X * Cell + 1, _gridRect.Y + placed.Y * Cell + 1,
                b.InventoryWidth * Cell - 2, b.InventoryHeight * Cell - 2);
            DrawItemBox(sb, rect, placed.Item);
        }

        // --- drop placement preview while dragging ---
        if (_drag.Active && _gridRect.Contains(input.MousePosition))
        {
            var b = _drag.Item.GetBase(_data);
            int cellX = (int)MathF.Floor((input.MousePosition.X - _gridRect.X) / (float)Cell - (b.InventoryWidth - 1) / 2f);
            int cellY = (int)MathF.Floor((input.MousePosition.Y - _gridRect.Y) / (float)Cell - (b.InventoryHeight - 1) / 2f);
            cellX = Math.Clamp(cellX, 0, character.Inventory.Width - b.InventoryWidth);
            cellY = Math.Clamp(cellY, 0, character.Inventory.Height - b.InventoryHeight);
            bool fits = character.Inventory.CanPlaceAt(_data, _drag.Item, cellX, cellY, _drag.Item.InstanceId);
            var previewRect = new Rectangle(_gridRect.X + cellX * Cell, _gridRect.Y + cellY * Cell,
                b.InventoryWidth * Cell, b.InventoryHeight * Cell);
            sb.Draw(TextureGen.Pixel, previewRect, fits ? new Color(60, 160, 60, 110) : new Color(180, 50, 50, 110));
        }

        var hint = FontManager.Get(12);
        sb.DrawString(hint,
            SellClickHandler != null
                ? "SELLING — ctrl+click a bag item (or drag it onto the shop) to sell it"
                : QuickMoveHandler != null
                    ? "ctrl+click sends an item to the stash · drag to move/equip"
                    : "drag to move/equip · right-click quick equip · right-click a Scroll, then click an item",
            new Vector2(_panelRect.X + 12, _panelRect.Bottom - 22),
            SellClickHandler != null ? new Color(240, 200, 90) : new Color(120, 116, 104));

        // Armed Enchanting Scroll follows the cursor (armed from the bag OR the stash).
        if (_pendingScrollId.HasValue)
        {
            var pending = FindAnywhere(character, _pendingScrollId.Value);
            if (pending != null)
            {
                var tex = SpriteGen.GetEnchantScrollSprite(pending.Item.GetBase(_data));
                var mouse = input.MousePosition;
                if (tex != null)
                    sb.Draw(tex, new Rectangle(mouse.X + 10, mouse.Y + 6, 28, 28), Color.White);
                sb.DrawString(FontManager.Get(13), "click an item to enchant · right-click to cancel",
                    new Vector2(mouse.X + 14, mouse.Y + 36), new Color(230, 200, 255));
            }
        }
    }

    public void DrawItemBox(SpriteBatch sb, Rectangle rect, ItemInstance item)
    {
        var b = item.GetBase(_data);
        var rarity = WorldRenderer.RarityColor(item.Rarity);
        sb.Draw(TextureGen.Pixel, rect, CategoryFill(b.Category));
        DrawBorder(sb, rect, rarity);

        // Enchanting Scrolls show their type sprite plus the stack count.
        var enchantTex = SpriteGen.GetEnchantScrollSprite(b);
        if (enchantTex != null)
        {
            int iconSize = Math.Min(rect.Width, rect.Height) - 6;
            sb.Draw(enchantTex, new Rectangle(rect.Center.X - iconSize / 2, rect.Center.Y - iconSize / 2, iconSize, iconSize), Color.White);
            if (item.StackCount > 1)
            {
                var stackFont = FontManager.GetBold(12);
                string count = item.StackCount.ToString();
                var cSize = stackFont.MeasureString(count);
                sb.DrawString(stackFont, count,
                    new Vector2(rect.Right - cSize.X - 3, rect.Bottom - cSize.Y - 1), new Color(255, 240, 190));
            }
            return;
        }

        // Curios (contracts, blueprints) show their trinket sprite + stack count.
        var curioTex = SpriteGen.GetCurioSprite(b);
        if (curioTex != null)
        {
            float cScale = MathF.Min((rect.Width - 6f) / curioTex.Width, (rect.Height - 6f) / curioTex.Height);
            int cW = (int)(curioTex.Width * cScale), cH = (int)(curioTex.Height * cScale);
            sb.Draw(curioTex, new Rectangle(rect.Center.X - cW / 2, rect.Center.Y - cH / 2, cW, cH), Color.White);
            if (item.StackCount > 1)
            {
                var stackFont = FontManager.GetBold(12);
                string count = item.StackCount.ToString();
                var cSize = stackFont.MeasureString(count);
                sb.DrawString(stackFont, count,
                    new Vector2(rect.Right - cSize.X - 3, rect.Bottom - cSize.Y - 1), new Color(255, 240, 190));
            }
            return;
        }

        // Skill Scrolls hang unrolled — a different silhouette from crafting scrolls.
        var skillScrollTex = SpriteGen.GetSkillScrollSprite(b);
        if (skillScrollTex != null)
        {
            int kH = rect.Height - 6;
            int kW = kH * skillScrollTex.Width / skillScrollTex.Height;
            sb.Draw(skillScrollTex, new Rectangle(rect.Center.X - kW / 2, rect.Center.Y - kH / 2, kW, kH), Color.White);
            return;
        }

        // Flasks show the bottle with the item's remaining charges.
        var flaskTex = SpriteGen.GetFlaskSprite(b);
        if (flaskTex != null)
        {
            int fH = rect.Height - 8;
            int fW = fH * flaskTex.Width / flaskTex.Height;
            sb.Draw(flaskTex, new Rectangle(rect.Center.X - fW / 2, rect.Center.Y - fH / 2, fW, fH), Color.White);
            var chargeFont = FontManager.GetBold(12);
            string ch = $"{item.FlaskCharges}/{b.FlaskChargesMax}";
            var chSize = chargeFont.MeasureString(ch);
            sb.DrawString(chargeFont, ch,
                new Vector2(rect.Right - chSize.X - 3, rect.Bottom - chSize.Y - 1), new Color(255, 240, 190));
            return;
        }

        // Quivers show their tube-of-arrows sprite.
        var quiverTex = SpriteGen.GetQuiverSprite(b);
        if (quiverTex != null)
        {
            int qH = rect.Height - 8;
            int qW = qH * quiverTex.Width / quiverTex.Height;
            sb.Draw(quiverTex, new Rectangle(rect.Center.X - qW / 2, rect.Center.Y - qH / 2, qW, qH), Color.White);
            return;
        }

        // Worn armor and jewelry show a shaped, tinted glyph per category.
        var armorTex = SpriteGen.GetArmorSprite(b);
        if (armorTex != null)
        {
            float aScale = MathF.Min((rect.Width - 8f) / armorTex.Width, (rect.Height - 8f) / armorTex.Height);
            int aW = (int)(armorTex.Width * aScale), aH = (int)(armorTex.Height * aScale);
            sb.Draw(armorTex, new Rectangle(rect.Center.X - aW / 2, rect.Center.Y - aH / 2, aW, aH), Color.White);
            return;
        }

        // Weapons show their actual sprite (upright); everything else keeps initials.
        var weaponTex = SpriteGen.GetWeaponSprite(b);
        if (weaponTex != null)
        {
            // The strip is horizontal (grip left); rotate -90° so it stands upright and
            // scale it to fit the box height with a small margin.
            float scale = MathF.Min((rect.Height - 8) / (float)weaponTex.Width,
                                    (rect.Width - 8) / (float)weaponTex.Height);
            var center = new Vector2(rect.Center.X, rect.Center.Y);
            sb.Draw(weaponTex, center, null, Color.White, -MathF.PI / 2f,
                new Vector2(weaponTex.Width / 2f, weaponTex.Height / 2f), scale, SpriteEffects.None, 0f);
            return;
        }

        var font = FontManager.GetBold(13);
        string initials = Initials(b.Name);
        var size = font.MeasureString(initials);
        sb.DrawString(font, initials, new Vector2(rect.Center.X - size.X / 2, rect.Center.Y - size.Y / 2), rarity);
    }

    private static Color CategoryFill(ItemCategory c) => c switch
    {
        ItemCategory.Mace => new Color(66, 50, 40),
        ItemCategory.Staff => new Color(46, 44, 70),
        ItemCategory.SkillScroll => new Color(70, 44, 74),
        ItemCategory.EnchantScroll => new Color(58, 50, 38),
        ItemCategory.Ring or ItemCategory.Amulet => new Color(70, 64, 36),
        ItemCategory.Flask => new Color(40, 46, 62),
        _ => new Color(44, 52, 56),
    };

    private static string Initials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(parts.Take(3).Select(p => p[0]));
    }

    private static string SlotLabel(EquipSlot slot) => slot switch
    {
        EquipSlot.BodyArmor => "Body",
        EquipSlot.MainHand => "Main Hand",
        EquipSlot.OffHand => "Off Hand",
        EquipSlot.Ring1 => "Ring",
        EquipSlot.Ring2 => "Ring",
        EquipSlot.Flask1 => "Flask",
        EquipSlot.Flask2 => "Flask",
        _ => slot.ToString(),
    };

    private static void DrawBorder(SpriteBatch sb, Rectangle r, Color c)
    {
        sb.Draw(TextureGen.Pixel, new Rectangle(r.X, r.Y, r.Width, 2), c);
        sb.Draw(TextureGen.Pixel, new Rectangle(r.X, r.Bottom - 2, r.Width, 2), c);
        sb.Draw(TextureGen.Pixel, new Rectangle(r.X, r.Y, 2, r.Height), c);
        sb.Draw(TextureGen.Pixel, new Rectangle(r.Right - 2, r.Y, 2, r.Height), c);
    }
}
