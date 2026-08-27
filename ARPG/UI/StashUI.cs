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
/// A stash CONTAINER's grid — storage lives on the character keyed by container id
/// (the hub's chest today; player-room furniture later), so this panel is pointed at
/// whichever container the player opened. Items drag between here and the bag; every
/// move is a server-validated MoveItem with a reach check against the container.
/// </summary>
public class StashUI
{
    public bool Open;
    /// <summary>Which container this panel is showing (set when the player opens one).</summary>
    public string ContainerId = World.GameMap.HubStashId;

    private readonly GameData _data;
    private readonly GameClient _client;
    private readonly InventoryUI _inventory; // reused for item-box rendering
    private readonly DragState _drag;
    private Rectangle _panelRect, _gridRect;
    private Point _lastMouse;
    private Point _screenSize;

    public ItemInstance HoveredItem { get; private set; }
    public readonly WindowDrag Window = new();
    private const int Cell = InventoryUI.Cell;
    private const int GridW = Sim.CharacterData.StashWidth, GridH = Sim.CharacterData.StashHeight;

    public StashUI(GameData data, GameClient client, InventoryUI inventory, DragState drag)
    {
        _data = data;
        _client = client;
        _inventory = inventory;
        _drag = drag;
    }

    public void Layout(Point screen)
    {
        _screenSize = screen;
        // Left side, like the shop — the bag opens on the right to move things across.
        int w = 20 + GridW * Cell;
        int h = 84 + GridH * Cell;
        _panelRect = Window.Place(new Rectangle(16, 36, w, h), screen);
        _gridRect = new Rectangle(_panelRect.X + 10, _panelRect.Y + 44, GridW * Cell, GridH * Cell);
    }

    public bool Contains(Point p) => Open && _panelRect.Contains(p);

    private Inventory.InventoryGrid Grid =>
        _client.World.MyCharacter?.GetStash(ContainerId);

    public void Update(InputManager input, bool mouseBlocked = false)
    {
        HoveredItem = null;
        if (!Open) return;
        var grid = Grid;
        if (grid == null) return;
        if (mouseBlocked) return; // a window above this one holds the mouse
        _lastMouse = input.MousePosition;
        var mouse = input.MousePosition;

        if (CloseButton.Handle(input, _panelRect)) { Open = false; return; }
        if (Window.HandleBar(input, WindowDrag.BarFor(_panelRect))) return;
        if (_panelRect.Contains(mouse))
            input.MouseCapturedByUI = true;

        Inventory.PlacedItem hovered = null;
        if (_gridRect.Contains(mouse))
        {
            int cx = (mouse.X - _gridRect.X) / Cell;
            int cy = (mouse.Y - _gridRect.Y) / Cell;
            hovered = grid.ItemAtCell(cx, cy, _data);
        }
        if (!_drag.Active && hovered != null) HoveredItem = hovered.Item;

        // --- enchant flow, shared with the bag panel: an armed scroll (from either
        // container) applies to stash items with a left-click; right-click arms a
        // crafting scroll straight FROM the stash — no shuffling into the bag first.
        if (_inventory.PendingScrollId is { } armed)
        {
            if (input.MouseRightPressed && _panelRect.Contains(mouse))
            {
                _inventory.PendingScrollId = null;
                input.MouseCapturedByUI = true;
                return;
            }
            if (input.MouseLeftPressed && hovered != null)
            {
                input.MouseCapturedByUI = true;
                if (hovered.Item.InstanceId != armed &&
                    hovered.Item.GetBase(_data).Category != ItemCategory.EnchantScroll)
                    _client.RequestApplyEnchant(armed, hovered.Item.InstanceId);
                _inventory.PendingScrollId = null;
                return;
            }
        }
        else if (!_drag.Active && input.MouseRightPressed && hovered != null &&
                 hovered.Item.GetBase(_data).Category == ItemCategory.EnchantScroll)
        {
            _inventory.PendingScrollId = hovered.Item.InstanceId;
            input.MouseCapturedByUI = true;
            return;
        }

        if (!_drag.Active && !_inventory.PendingScrollId.HasValue &&
            input.MouseLeftPressed && hovered != null)
        {
            // Ctrl+click shuttles the item straight to the bag (server auto-places).
            if (input.CtrlDown)
            {
                input.MouseCapturedByUI = true;
                _client.RequestMoveItem(ItemLocation.AtStash(ContainerId, hovered.X, hovered.Y),
                    ItemLocation.AtGrid(0, 0));
                return;
            }
            _drag.Item = hovered.Item;
            _drag.Source = ItemLocation.AtStash(ContainerId, hovered.X, hovered.Y);
        }
    }

    /// <summary>Complete a drag released over this panel (bag/equipment -> stash, or a
    /// reposition inside it). Returns true when the drop belongs to this panel.</summary>
    public bool TryDropAt(Point mouse)
    {
        if (!Open || !_drag.Active || !_panelRect.Contains(mouse)) return false;
        if (_gridRect.Contains(mouse))
        {
            var b = _drag.Item.GetBase(_data);
            int cellX = (int)MathF.Floor((mouse.X - _gridRect.X) / (float)Cell - (b.InventoryWidth - 1) / 2f);
            int cellY = (int)MathF.Floor((mouse.Y - _gridRect.Y) / (float)Cell - (b.InventoryHeight - 1) / 2f);
            cellX = Math.Clamp(cellX, 0, GridW - b.InventoryWidth);
            cellY = Math.Clamp(cellY, 0, GridH - b.InventoryHeight);
            _client.RequestMoveItem(_drag.Source, ItemLocation.AtStash(ContainerId, cellX, cellY));
            return true;
        }
        return true; // released on the panel frame: cancel silently
    }

    public void Draw(SpriteBatch sb)
    {
        if (!Open) return;
        var grid = Grid;
        if (grid == null) return;

        sb.Draw(TextureGen.Pixel, _panelRect, new Color(22, 22, 30, 240));
        DrawBorder(sb, _panelRect, new Color(120, 100, 150));
        WindowDrag.DrawBar(sb, _panelRect, _lastMouse);
        sb.DrawString(FontManager.GetBold(19), "Stash",
            new Vector2(_panelRect.X + 12, _panelRect.Y + 8), new Color(210, 180, 250));
        CloseButton.Draw(sb, _panelRect, _lastMouse);

        for (int y = 0; y <= GridH; y++)
            sb.Draw(TextureGen.Pixel, new Rectangle(_gridRect.X, _gridRect.Y + y * Cell, _gridRect.Width, 1), new Color(52, 48, 58));
        for (int x = 0; x <= GridW; x++)
            sb.Draw(TextureGen.Pixel, new Rectangle(_gridRect.X + x * Cell, _gridRect.Y, 1, _gridRect.Height), new Color(52, 48, 58));

        foreach (var placed in grid.Items)
        {
            if (_drag.Active && _drag.Item.InstanceId == placed.Item.InstanceId) continue;
            var b = placed.Item.GetBase(_data);
            var rect = new Rectangle(_gridRect.X + placed.X * Cell + 1, _gridRect.Y + placed.Y * Cell + 1,
                b.InventoryWidth * Cell - 2, b.InventoryHeight * Cell - 2);
            _inventory.DrawItemBox(sb, rect, placed.Item);
        }

        // Drop placement preview while dragging over the grid.
        if (_drag.Active && _gridRect.Contains(_lastMouse))
        {
            var b = _drag.Item.GetBase(_data);
            int cellX = (int)MathF.Floor((_lastMouse.X - _gridRect.X) / (float)Cell - (b.InventoryWidth - 1) / 2f);
            int cellY = (int)MathF.Floor((_lastMouse.Y - _gridRect.Y) / (float)Cell - (b.InventoryHeight - 1) / 2f);
            cellX = Math.Clamp(cellX, 0, GridW - b.InventoryWidth);
            cellY = Math.Clamp(cellY, 0, GridH - b.InventoryHeight);
            bool fits = grid.CanPlaceAt(_data, _drag.Item, cellX, cellY, _drag.Item.InstanceId);
            sb.Draw(TextureGen.Pixel,
                new Rectangle(_gridRect.X + cellX * Cell, _gridRect.Y + cellY * Cell,
                    b.InventoryWidth * Cell, b.InventoryHeight * Cell),
                fits ? new Color(60, 160, 60, 110) : new Color(180, 50, 50, 110));
        }

        sb.DrawString(FontManager.Get(12), "drag between the stash and your bag · stored per container",
            new Vector2(_panelRect.X + 12, _panelRect.Bottom - 22), new Color(130, 122, 142));

        if (HoveredItem != null)
            ItemTooltip.Draw(sb, _data, HoveredItem, _lastMouse, _screenSize);
    }

    private static void DrawBorder(SpriteBatch sb, Rectangle r, Color c)
    {
        sb.Draw(TextureGen.Pixel, new Rectangle(r.X, r.Y, r.Width, 2), c);
        sb.Draw(TextureGen.Pixel, new Rectangle(r.X, r.Bottom - 2, r.Width, 2), c);
        sb.Draw(TextureGen.Pixel, new Rectangle(r.X, r.Y, 2, r.Height), c);
        sb.Draw(TextureGen.Pixel, new Rectangle(r.Right - 2, r.Y, 2, r.Height), c);
    }
}
