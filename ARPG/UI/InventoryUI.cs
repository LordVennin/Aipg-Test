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

    public InventoryUI(GameData data, GameClient client, DragState drag)
    {
        _data = data;
        _client = client;
        _drag = drag;
    }

    public void Layout(Point screen)
    {
        int w = 20 + 10 * Cell;
        int h = 640;
        _panelRect = new Rectangle(screen.X - w - 12, 36, w, h);
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
    }

    public bool Contains(Point p) => Open && _panelRect.Contains(p);

    public void Update(InputManager input)
    {
        HoveredItem = null;
        if (!Open) return;
        var character = _client.World.MyCharacter;
        if (character == null) return;
        var mouse = input.MousePosition;

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

        // --- start drag ---
        if (!_drag.Active && input.MouseLeftPressed && _panelRect.Contains(mouse))
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

        // --- quick actions (right click): equip from grid / unequip to grid ---
        if (!_drag.Active && input.MouseRightPressed)
        {
            if (hoveredPlaced != null)
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
            var character = _client.World.MyCharacter;
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
        var title = FontManager.GetBold(19);
        sb.DrawString(title, "Equipment & Inventory", new Vector2(_panelRect.X + 12, _panelRect.Y + 8), new Color(230, 215, 165));

        // --- gold display ---
        var goldFont = FontManager.GetBold(16);
        string goldText = $"{character.Gold:n0}";
        var goldSize = goldFont.MeasureString(goldText);
        var pile = SpriteGen.GetGoldPile();
        float goldX = _panelRect.Right - 16 - goldSize.X;
        if (pile != null)
            sb.Draw(pile, new Rectangle((int)goldX - pile.Width * 2 - 6, _panelRect.Y + 12, pile.Width * 2, pile.Height * 2), Color.White);
        sb.DrawString(goldFont, goldText, new Vector2(goldX, _panelRect.Y + 10), new Color(240, 200, 90));

        // --- equipment slots ---
        foreach (var (slot, rect) in _slotRects)
        {
            sb.Draw(TextureGen.Pixel, rect, new Color(14, 14, 20));
            DrawBorder(sb, rect, new Color(70, 66, 54));
            var item = character.Equipment.GetValueOrDefault(slot);
            if (item != null && (!_drag.Active || _drag.Item.InstanceId != item.InstanceId))
                DrawItemBox(sb, rect, item);
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
        sb.DrawString(hint, "drag to move / equip · right-click to quick equip · drag outside to drop",
            new Vector2(_panelRect.X + 12, _panelRect.Bottom - 22), new Color(120, 116, 104));
    }

    public void DrawItemBox(SpriteBatch sb, Rectangle rect, ItemInstance item)
    {
        var b = item.GetBase(_data);
        var rarity = WorldRenderer.RarityColor(item.Rarity);
        sb.Draw(TextureGen.Pixel, rect, CategoryFill(b.Category));
        DrawBorder(sb, rect, rarity);

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
        ItemCategory.Ring or ItemCategory.Amulet => new Color(70, 64, 36),
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
