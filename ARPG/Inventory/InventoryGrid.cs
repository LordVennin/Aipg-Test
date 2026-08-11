using ARPG.Data;
using ARPG.Items;

namespace ARPG.Inventory;

/// <summary>An item placed in the grid at cell (X, Y) = top-left corner.</summary>
public class PlacedItem
{
    public ItemInstance Item { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
}

/// <summary>
/// Grid-based inventory. Items occupy Width x Height cells from their ItemBase.
/// Dimensions are configurable per inventory instance.
/// </summary>
public class InventoryGrid
{
    public int Width { get; set; } = 10;
    public int Height { get; set; } = 6;
    public List<PlacedItem> Items { get; set; } = new();

    public PlacedItem FindByInstance(Guid instanceId) =>
        Items.FirstOrDefault(p => p.Item.InstanceId == instanceId);

    public PlacedItem ItemAtCell(int cellX, int cellY, GameData data)
    {
        foreach (var placed in Items)
        {
            var b = placed.Item.GetBase(data);
            if (cellX >= placed.X && cellX < placed.X + b.InventoryWidth &&
                cellY >= placed.Y && cellY < placed.Y + b.InventoryHeight)
                return placed;
        }
        return null;
    }

    /// <summary>Can `item` be placed with its top-left at (x, y)? `ignore` excludes an instance
    /// (used when moving an item within the grid).</summary>
    public bool CanPlaceAt(GameData data, ItemInstance item, int x, int y, Guid? ignore = null)
    {
        var b = item.GetBase(data);
        if (x < 0 || y < 0 || x + b.InventoryWidth > Width || y + b.InventoryHeight > Height)
            return false;
        foreach (var placed in Items)
        {
            if (ignore.HasValue && placed.Item.InstanceId == ignore.Value) continue;
            var pb = placed.Item.GetBase(data);
            bool overlap = x < placed.X + pb.InventoryWidth && x + b.InventoryWidth > placed.X &&
                           y < placed.Y + pb.InventoryHeight && y + b.InventoryHeight > placed.Y;
            if (overlap) return false;
        }
        return true;
    }

    /// <summary>The single item overlapping the target region, or null. Used for swap-on-drop.</summary>
    public PlacedItem SingleOverlap(GameData data, ItemInstance item, int x, int y)
    {
        var b = item.GetBase(data);
        PlacedItem found = null;
        foreach (var placed in Items)
        {
            if (placed.Item.InstanceId == item.InstanceId) continue;
            var pb = placed.Item.GetBase(data);
            bool overlap = x < placed.X + pb.InventoryWidth && x + b.InventoryWidth > placed.X &&
                           y < placed.Y + pb.InventoryHeight && y + b.InventoryHeight > placed.Y;
            if (!overlap) continue;
            if (found != null) return null; // more than one item in the way
            found = placed;
        }
        return found;
    }

    public bool TryFindFreeSlot(GameData data, ItemInstance item, out int x, out int y)
    {
        var b = item.GetBase(data);
        for (int cy = 0; cy <= Height - b.InventoryHeight; cy++)
            for (int cx = 0; cx <= Width - b.InventoryWidth; cx++)
                if (CanPlaceAt(data, item, cx, cy))
                {
                    x = cx; y = cy;
                    return true;
                }
        x = y = -1;
        return false;
    }

    public bool TryAdd(GameData data, ItemInstance item)
    {
        if (!TryFindFreeSlot(data, item, out int x, out int y)) return false;
        Items.Add(new PlacedItem { Item = item, X = x, Y = y });
        return true;
    }

    public bool Remove(Guid instanceId)
    {
        var placed = FindByInstance(instanceId);
        if (placed == null) return false;
        Items.Remove(placed);
        return true;
    }
}
