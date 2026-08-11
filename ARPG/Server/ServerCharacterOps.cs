using ARPG.Inventory;
using ARPG.Items;
using ARPG.Net;
using ARPG.Sim;
using ARPG.Skills;

namespace ARPG.Server;

/// <summary>
/// Server-side (authoritative) character mutations: inventory moves, equipping, Skill Scroll
/// attachment, dropping, learning skills, hotbar assignment and debug commands.
/// Every successful change ends with a CharacterChanged event so the owning client re-syncs.
/// </summary>
public partial class ServerWorld
{
    public void MoveItem(int playerId, ItemLocation src, ItemLocation dst)
    {
        if (!Players.TryGetValue(playerId, out var p)) return;
        bool ok = TryMoveItem(p, src, dst, out string error);
        if (!ok && error != null) _events.MessageFor(p, error);
        // Re-sync the client either way: on failure this reverts any optimistic UI state.
        p.RecomputeStats(Data);
        _events.PlayerHealthChanged(p);
        _events.CharacterChanged(p);
    }

    private bool TryMoveItem(ServerPlayer p, ItemLocation src, ItemLocation dst, out string error)
    {
        error = null;
        var c = p.Character;
        var inv = c.Inventory;

        ItemInstance item = GetItemAt(c, src);
        if (item == null) return false;

        switch (dst.Kind)
        {
            case ItemLocationKind.Grid:
            {
                if (src.Kind == ItemLocationKind.Grid)
                {
                    var placed = inv.FindByInstance(item.InstanceId);
                    if (inv.CanPlaceAt(Data, item, dst.X, dst.Y, item.InstanceId))
                    {
                        placed.X = dst.X; placed.Y = dst.Y;
                        return true;
                    }
                    // Dropping a stackable onto a same-base stack merges them.
                    var under = inv.ItemAtCell(dst.X, dst.Y, Data);
                    var itemBaseDef = item.GetBase(Data);
                    if (under != null && under.Item.InstanceId != item.InstanceId &&
                        itemBaseDef.MaxStack > 1 && under.Item.BaseItemId == item.BaseItemId)
                    {
                        int space = itemBaseDef.MaxStack - under.Item.StackCount;
                        int moved = Math.Min(space, item.StackCount);
                        if (moved <= 0) { error = "That stack is full."; return false; }
                        under.Item.StackCount += moved;
                        item.StackCount -= moved;
                        if (item.StackCount <= 0) inv.Items.Remove(placed);
                        return true;
                    }
                    // Swap with a single blocking item if both fit afterwards.
                    var blocking = inv.SingleOverlap(Data, item, dst.X, dst.Y);
                    if (blocking == null) { error = "No room there."; return false; }
                    int oldX = placed.X, oldY = placed.Y;
                    inv.Items.Remove(placed);
                    inv.Items.Remove(blocking);
                    if (inv.CanPlaceAt(Data, item, dst.X, dst.Y) && inv.CanPlaceAt(Data, blocking.Item, oldX, oldY))
                    {
                        inv.Items.Add(new PlacedItem { Item = item, X = dst.X, Y = dst.Y });
                        inv.Items.Add(new PlacedItem { Item = blocking.Item, X = oldX, Y = oldY });
                        return true;
                    }
                    inv.Items.Add(placed);
                    inv.Items.Add(blocking);
                    error = "No room to swap.";
                    return false;
                }
                if (src.Kind == ItemLocationKind.Equipment)
                {
                    var slot = (EquipSlot)src.EquipSlot;
                    if (!inv.CanPlaceAt(Data, item, dst.X, dst.Y))
                    {
                        if (!inv.TryFindFreeSlot(Data, item, out int fx, out int fy)) { error = "Inventory is full."; return false; }
                        dst.X = fx; dst.Y = fy;
                    }
                    c.Equipment.Remove(slot);
                    inv.Items.Add(new PlacedItem { Item = item, X = dst.X, Y = dst.Y });
                    return true;
                }
                if (src.Kind == ItemLocationKind.ScrollSlot)
                {
                    var skill = c.GetSkill(src.SkillId);
                    if (skill == null) return false;
                    if (!inv.CanPlaceAt(Data, item, dst.X, dst.Y))
                    {
                        if (!inv.TryFindFreeSlot(Data, item, out int fx, out int fy)) { error = "Inventory is full."; return false; }
                        dst.X = fx; dst.Y = fy;
                    }
                    skill.Scrolls[src.ScrollIndex] = null;
                    inv.Items.Add(new PlacedItem { Item = item, X = dst.X, Y = dst.Y });
                    return true;
                }
                return false;
            }

            case ItemLocationKind.Equipment:
            {
                var slot = (EquipSlot)dst.EquipSlot;
                var itemBase = item.GetBase(Data);
                if (!itemBase.IsEquippable || !ItemBase.CompatibleSlots(itemBase.Category).Contains(slot))
                {
                    error = $"{itemBase.Name} cannot be equipped there.";
                    return false;
                }
                if (itemBase.RequiredLevel > c.Level)
                {
                    error = $"Requires character level {itemBase.RequiredLevel}.";
                    return false;
                }

                var previous = c.Equipment.GetValueOrDefault(slot);

                if (src.Kind == ItemLocationKind.Grid)
                {
                    var placed = inv.FindByInstance(item.InstanceId);
                    int oldX = placed.X, oldY = placed.Y;
                    inv.Items.Remove(placed);
                    if (previous != null)
                    {
                        // Swap: previous item goes back where the new one came from, or any free spot.
                        if (inv.CanPlaceAt(Data, previous, oldX, oldY))
                            inv.Items.Add(new PlacedItem { Item = previous, X = oldX, Y = oldY });
                        else if (inv.TryFindFreeSlot(Data, previous, out int fx, out int fy))
                            inv.Items.Add(new PlacedItem { Item = previous, X = fx, Y = fy });
                        else
                        {
                            inv.Items.Add(placed);
                            error = "No room to unequip the current item.";
                            return false;
                        }
                    }
                    c.Equipment[slot] = item;
                    return true;
                }
                if (src.Kind == ItemLocationKind.Equipment)
                {
                    var srcSlot = (EquipSlot)src.EquipSlot;
                    if (previous != null && !ItemBase.CompatibleSlots(previous.GetBase(Data).Category).Contains(srcSlot))
                    {
                        error = "Cannot swap those slots.";
                        return false;
                    }
                    c.Equipment.Remove(srcSlot);
                    if (previous != null) c.Equipment[srcSlot] = previous;
                    c.Equipment[slot] = item;
                    return true;
                }
                error = "Cannot equip that.";
                return false;
            }

            case ItemLocationKind.ScrollSlot:
            {
                var skill = c.GetSkill(dst.SkillId);
                var skillDef = skill?.GetDefinition(Data);
                if (skill == null || skillDef == null) return false;

                var itemBase = item.GetBase(Data);
                if (itemBase.Category != ItemCategory.SkillScroll || itemBase.ScrollId == null)
                {
                    error = "Only Skill Scrolls can go there.";
                    return false;
                }
                var scrollDef = Data.Scrolls.GetValueOrDefault(itemBase.ScrollId);
                if (scrollDef == null) return false;
                if (!scrollDef.CompatibleWith(skillDef))
                {
                    error = $"{scrollDef.Name} requires a skill with the '{scrollDef.RequiredTag}' tag.";
                    return false;
                }
                int unlocked = SkillMath.ScrollSlotsAtLevel(Data, skill.Level);
                if (dst.ScrollIndex < 0 || dst.ScrollIndex >= unlocked)
                {
                    error = "That Scroll slot is locked.";
                    return false;
                }
                while (skill.Scrolls.Count <= dst.ScrollIndex) skill.Scrolls.Add(null);
                var existing = skill.Scrolls[dst.ScrollIndex];

                // Remove the scroll from its source location.
                if (src.Kind == ItemLocationKind.Grid)
                {
                    var placed = inv.FindByInstance(item.InstanceId);
                    int oldX = placed.X, oldY = placed.Y;
                    inv.Items.Remove(placed);
                    if (existing != null) // replace: old scroll goes back to the bag
                    {
                        if (inv.CanPlaceAt(Data, existing, oldX, oldY))
                            inv.Items.Add(new PlacedItem { Item = existing, X = oldX, Y = oldY });
                        else if (inv.TryFindFreeSlot(Data, existing, out int fx, out int fy))
                            inv.Items.Add(new PlacedItem { Item = existing, X = fx, Y = fy });
                        else
                        {
                            inv.Items.Add(placed);
                            error = "No room to remove the attached Scroll.";
                            return false;
                        }
                    }
                }
                else if (src.Kind == ItemLocationKind.ScrollSlot)
                {
                    var srcSkill = c.GetSkill(src.SkillId);
                    if (srcSkill == null) return false;
                    srcSkill.Scrolls[src.ScrollIndex] = existing; // may be null or a swap
                }
                else return false;

                skill.Scrolls[dst.ScrollIndex] = item;
                return true;
            }
        }
        return false;
    }

    private ItemInstance GetItemAt(CharacterData c, ItemLocation loc) => loc.Kind switch
    {
        ItemLocationKind.Grid => c.Inventory.ItemAtCell(loc.X, loc.Y, Data)?.Item,
        ItemLocationKind.Equipment => c.Equipment.GetValueOrDefault((EquipSlot)loc.EquipSlot),
        ItemLocationKind.ScrollSlot => c.GetSkill(loc.SkillId) is { } s &&
                                       loc.ScrollIndex >= 0 && loc.ScrollIndex < s.Scrolls.Count
            ? s.Scrolls[loc.ScrollIndex]
            : null,
        _ => null,
    };

    /// <summary>
    /// Apply an Enchanting Scroll (by instance id) to a target item (grid or equipped).
    /// Fully server-validated: scroll ownership, target ownership, enchant rules, seal state.
    /// One charge is consumed from the stack only on success.
    /// </summary>
    public void ApplyEnchant(int playerId, Guid scrollId, Guid targetId)
    {
        if (!Players.TryGetValue(playerId, out var p)) return;
        var c = p.Character;

        var scrollPlaced = c.Inventory.FindByInstance(scrollId);
        var scrollBase = scrollPlaced?.Item.GetBase(Data);
        if (scrollPlaced == null || scrollBase?.Category != ItemCategory.EnchantScroll ||
            scrollBase.EnchantType == EnchantType.None)
        {
            _events.MessageFor(p, "That is not an Enchanting Scroll.");
            return;
        }

        ItemInstance target = c.Inventory.FindByInstance(targetId)?.Item
                              ?? c.Equipment.Values.FirstOrDefault(e => e != null && e.InstanceId == targetId);
        if (target == null || target.InstanceId == scrollId) return;

        if (EnchantSystem.Apply(Data, _rng, Loot, scrollBase.EnchantType, target, out string error))
        {
            // Consume one charge.
            scrollPlaced.Item.StackCount--;
            if (scrollPlaced.Item.StackCount <= 0)
                c.Inventory.Items.Remove(scrollPlaced);
            _events.MessageFor(p, $"{scrollBase.Name} applied to {target.DisplayName(Data)}.");
        }
        else if (error != null)
        {
            _events.MessageFor(p, error);
        }

        p.RecomputeStats(Data);
        _events.PlayerHealthChanged(p);
        _events.CharacterChanged(p);
    }

    public void DropItem(int playerId, Guid instanceId)
    {
        if (!Players.TryGetValue(playerId, out var p)) return;
        var placed = p.Character.Inventory.FindByInstance(instanceId);
        if (placed == null) return;
        p.Character.Inventory.Items.Remove(placed);
        SpawnDrop(placed.Item, p.Position);
        _events.CharacterChanged(p);
    }

    public void LearnSkill(int playerId, string skillId)
    {
        if (!Players.TryGetValue(playerId, out var p)) return;
        if (!Data.Skills.ContainsKey(skillId) || p.Character.GetSkill(skillId) != null) return;
        p.Character.Skills.Add(new LearnedSkill { SkillId = skillId });
        _events.CharacterChanged(p);
    }

    public void AssignHotbar(int playerId, int slot, string skillId)
    {
        if (!Players.TryGetValue(playerId, out var p)) return;
        if (slot < 0 || slot >= p.Character.Hotbar.Length) return;
        if (!string.IsNullOrEmpty(skillId))
        {
            if (p.Character.GetSkill(skillId) == null) return;
            // A skill occupies at most one hotbar slot.
            for (int i = 0; i < p.Character.Hotbar.Length; i++)
                if (p.Character.Hotbar[i] == skillId)
                    p.Character.Hotbar[i] = null;
        }
        p.Character.Hotbar[slot] = string.IsNullOrEmpty(skillId) ? null : skillId;
        _events.CharacterChanged(p);
    }

    // ------------------------------------------------------------------ debug commands

    public void DebugCommand(int playerId, string cmd, string arg)
    {
        if (!Players.TryGetValue(playerId, out var p)) return;
        var c = p.Character;
        bool changed = false;

        switch (cmd)
        {
            case "spawn_enemy":
            {
                string type = string.IsNullOrEmpty(arg)
                    ? Data.Enemies.Keys.OrderBy(_ => Guid.NewGuid()).FirstOrDefault()
                    : arg;
                if (type != null)
                    SpawnEnemy(type, p.Position + new System.Numerics.Vector2(2, 0));
                break;
            }
            case "give_mace":
            case "give_staff":
            {
                var category = cmd == "give_mace" ? ItemCategory.Mace : ItemCategory.Staff;
                var itemBase = Data.Items.Values.Where(b => b.Category == category)
                    .OrderBy(_ => Guid.NewGuid()).FirstOrDefault();
                if (itemBase != null)
                    changed = GiveItem(p, Loot.Generate(itemBase, 10, ItemRarity.Rare));
                break;
            }
            case "give_rare":
            {
                var item = Loot.GenerateEquipment(Data.GetLootTable("default"), 10, ItemRarity.Rare);
                if (item != null) changed = GiveItem(p, item);
                break;
            }
            case "give_10mod":
            {
                // Demonstrates per-item modifier limits: this item's own slot caps are raised
                // to 6/6, then 10 modifiers are rolled. No universal cap interferes.
                var itemBase = Data.Items.Values.Where(b => b.IsWeapon)
                    .OrderBy(_ => Guid.NewGuid()).FirstOrDefault();
                if (itemBase != null)
                {
                    var item = new ItemInstance
                    {
                        BaseItemId = itemBase.Id,
                        ItemLevel = 10,
                        Rarity = ItemRarity.Rare,
                        BaseModifierLimit = 12,
                        MaxPrefixes = 6,
                        MaxSuffixes = 6,
                    };
                    Loot.RollModifiers(item, itemBase, 10);
                    changed = GiveItem(p, item);
                }
                break;
            }
            case "give_enchant":
            {
                // A stack of 3 of a random (or named) Enchanting Scroll type.
                var scroll = Loot.GenerateEnchantScrollItem(string.IsNullOrEmpty(arg) ? null : arg);
                if (scroll != null)
                {
                    scroll.StackCount = 3;
                    changed = GiveItem(p, scroll);
                }
                break;
            }
            case "give_scroll":
            {
                var scroll = Loot.GenerateScrollItem();
                if (scroll != null) changed = GiveItem(p, scroll);
                break;
            }
            case "skill_xp":
            {
                foreach (var skill in c.Skills)
                    GrantSkillXp(p, skill, 120);
                changed = true;
                break;
            }
            case "char_xp":
                GrantCharacterXp(p, 150);
                changed = true;
                break;
            case "kill_nearby":
            {
                foreach (var e in Enemies.Values.ToList())
                    if (!e.Dead && System.Numerics.Vector2.Distance(e.Position, p.Position) < 7f)
                        DamageEnemy(e, e.Health + 1, playerId, null);
                break;
            }
            case "heal":
                p.Health = p.Stats.MaxHealth;
                _events.PlayerHealthChanged(p);
                break;
        }

        if (changed) _events.CharacterChanged(p);
    }

    private bool GiveItem(ServerPlayer p, ItemInstance item)
    {
        if (p.Character.Inventory.TryAdd(Data, item)) return true;
        SpawnDrop(item, p.Position);
        return false;
    }
}
