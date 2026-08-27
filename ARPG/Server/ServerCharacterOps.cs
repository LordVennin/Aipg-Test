using System.Numerics;
using ARPG.Inventory;
using ARPG.Items;
using ARPG.Net;
using ARPG.Sim;
using ARPG.Skills;
using ARPG.World;

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
                if (src.Kind == ItemLocationKind.Stash)
                {
                    if (!StashInReach(p, src.ContainerId)) { error = "You are not at the stash."; return false; }
                    var srcStash = c.GetStash(src.ContainerId);
                    if (!inv.CanPlaceAt(Data, item, dst.X, dst.Y))
                    {
                        if (!inv.TryFindFreeSlot(Data, item, out int fx, out int fy)) { error = "Inventory is full."; return false; }
                        dst.X = fx; dst.Y = fy;
                    }
                    srcStash.Items.Remove(srcStash.FindByInstance(item.InstanceId));
                    inv.Items.Add(new PlacedItem { Item = item, X = dst.X, Y = dst.Y });
                    return true;
                }
                return false;
            }

            case ItemLocationKind.Stash:
            {
                if (!StashInReach(p, dst.ContainerId)) { error = "You are not at the stash."; return false; }
                var stash = c.GetStash(dst.ContainerId);
                if (src.Kind == ItemLocationKind.Stash && src.ContainerId == dst.ContainerId)
                {
                    // Reposition within the container.
                    var placedS = stash.FindByInstance(item.InstanceId);
                    if (stash.CanPlaceAt(Data, item, dst.X, dst.Y, item.InstanceId))
                    {
                        placedS.X = dst.X;
                        placedS.Y = dst.Y;
                        return true;
                    }
                    error = "No room there.";
                    return false;
                }
                if (!stash.CanPlaceAt(Data, item, dst.X, dst.Y))
                {
                    if (!stash.TryFindFreeSlot(Data, item, out int sx, out int sy)) { error = "The stash is full."; return false; }
                    dst.X = sx; dst.Y = sy;
                }
                if (src.Kind == ItemLocationKind.Grid)
                    inv.Items.Remove(inv.FindByInstance(item.InstanceId));
                else if (src.Kind == ItemLocationKind.Equipment)
                    c.Equipment.Remove((EquipSlot)src.EquipSlot);
                else
                {
                    error = "Cannot stash that from there.";
                    return false;
                }
                stash.Items.Add(new PlacedItem { Item = item, X = dst.X, Y = dst.Y });
                return true;
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
                // Attribute requirements check against CURRENT computed attributes
                // (base + gear + passives) — server-authoritative like the level. The
                // item's own "of Ease" rolls lower ITS requirements locally.
                int reqStr = item.EffectiveRequirement(Data, itemBase.RequiredStrength);
                int reqDex = item.EffectiveRequirement(Data, itemBase.RequiredDexterity);
                int reqInt = item.EffectiveRequirement(Data, itemBase.RequiredIntelligence);
                if (reqStr > p.Stats.Strength + 0.01f)
                {
                    error = $"Requires {reqStr} Strength.";
                    return false;
                }
                if (reqDex > p.Stats.Dexterity + 0.01f)
                {
                    error = $"Requires {reqDex} Dexterity.";
                    return false;
                }
                if (reqInt > p.Stats.Intelligence + 0.01f)
                {
                    error = $"Requires {reqInt} Intelligence.";
                    return false;
                }

                // Hand rules: a two-handed weapon occupies both hands — EXCEPT a bow,
                // which shares with (only) a quiver.
                if (slot == EquipSlot.OffHand &&
                    c.Equipment.GetValueOrDefault(EquipSlot.MainHand)?.GetBase(Data) is { IsWeapon: true, TwoHanded: true } mainBase &&
                    !(mainBase.Category == ItemCategory.Bow && itemBase.Category == ItemCategory.Quiver))
                {
                    error = $"Cannot use the off-hand while the two-handed {mainBase.Name} is equipped.";
                    return false;
                }
                if (slot == EquipSlot.MainHand && itemBase.IsWeapon && itemBase.TwoHanded)
                {
                    var offHand = c.Equipment.GetValueOrDefault(EquipSlot.OffHand);
                    bool offHandStays = itemBase.Category == ItemCategory.Bow &&
                                        offHand?.GetBase(Data).Category == ItemCategory.Quiver;
                    if (offHand != null && !offHandStays)
                    {
                        // Auto-unequip the off-hand item to the bag; fail if there is no room.
                        if (!inv.TryFindFreeSlot(Data, offHand, out int ox, out int oy))
                        {
                            error = "No room to unequip the off-hand item.";
                            return false;
                        }
                        c.Equipment.Remove(EquipSlot.OffHand);
                        inv.Items.Add(new PlacedItem { Item = offHand, X = ox, Y = oy });
                    }
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
        ItemLocationKind.Stash => c.GetStash(loc.ContainerId).ItemAtCell(loc.X, loc.Y, Data)?.Item,
        _ => null,
    };

    /// <summary>Whether this player can use the given stash container RIGHT NOW —
    /// storage is tied to a physical object, so moves require standing beside it.
    /// Today there is one container: the hub's stash chest.</summary>
    private bool StashInReach(ServerPlayer p, string containerId) =>
        containerId == GameMap.HubStashId && Map.Kind == MapKind.Hub &&
        Vector2.Distance(p.Position, Map.StashSpot) <= 3.0f;

    /// <summary>
    /// Apply an Enchanting Scroll (by instance id) to a target item (grid or equipped).
    /// Fully server-validated: scroll ownership, target ownership, enchant rules, seal state.
    /// One charge is consumed from the stack only on success.
    /// </summary>
    public void ApplyEnchant(int playerId, Guid scrollId, Guid targetId)
    {
        if (!Players.TryGetValue(playerId, out var p)) return;
        var c = p.Character;

        // The scroll can live in the bag OR the stash (when its chest is in reach) —
        // crafting straight out of storage, no shuffling items into the bag first.
        var scrollHome = c.Inventory;
        var scrollPlaced = c.Inventory.FindByInstance(scrollId);
        if (scrollPlaced == null && StashInReach(p, GameMap.HubStashId))
        {
            scrollHome = c.GetStash(GameMap.HubStashId);
            scrollPlaced = scrollHome.FindByInstance(scrollId);
        }
        var scrollBase = scrollPlaced?.Item.GetBase(Data);
        if (scrollPlaced == null || scrollBase?.Category != ItemCategory.EnchantScroll ||
            scrollBase.EnchantType == EnchantType.None)
        {
            _events.MessageFor(p, "That is not an Enchanting Scroll.");
            return;
        }

        ItemInstance target = c.Inventory.FindByInstance(targetId)?.Item
                              ?? c.Equipment.Values.FirstOrDefault(e => e != null && e.InstanceId == targetId)
                              ?? (StashInReach(p, GameMap.HubStashId)
                                  ? c.GetStash(GameMap.HubStashId).FindByInstance(targetId)?.Item
                                  : null);
        if (target == null || target.InstanceId == scrollId) return;

        if (EnchantSystem.Apply(Data, _rng, Loot, scrollBase.EnchantType, target, out string error))
        {
            // Consume one charge.
            scrollPlaced.Item.StackCount--;
            if (scrollPlaced.Item.StackCount <= 0)
                scrollHome.Items.Remove(scrollPlaced);
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
        var c = p.Character;
        // Bag first; then EQUIPPED gear (dropped straight off the body — stats
        // recompute); then the stash, when its chest is in reach.
        var placed = c.Inventory.FindByInstance(instanceId);
        if (placed != null)
        {
            c.Inventory.Items.Remove(placed);
            SpawnDrop(placed.Item, p.Position);
            _events.CharacterChanged(p);
            return;
        }
        foreach (var (slot, item) in c.Equipment)
        {
            if (item == null || item.InstanceId != instanceId) continue;
            c.Equipment.Remove(slot);
            p.RecomputeStats(Data);
            SpawnDrop(item, p.Position);
            _events.PlayerHealthChanged(p);
            _events.CharacterChanged(p); // also rebroadcasts the held/worn appearance
            return;
        }
        if (StashInReach(p, GameMap.HubStashId))
        {
            var stash = c.GetStash(GameMap.HubStashId);
            var inStash = stash.FindByInstance(instanceId);
            if (inStash != null)
            {
                stash.Items.Remove(inStash);
                SpawnDrop(inStash.Item, p.Position);
                _events.CharacterChanged(p);
            }
        }
    }

    /// <summary>Roll a gambled item: the player picks the exact BASE, fate rolls the
    /// rarity and modifiers. Validated: gambler in reach, base level-eligible, gold,
    /// bag space (checked BEFORE charging — a full bag never eats the fee).</summary>
    public void Gamble(int playerId, string baseItemId)
    {
        if (!Players.TryGetValue(playerId, out var p)) return;
        var b = Data.Items.GetValueOrDefault(baseItemId);
        if (!GambleBalance.Eligible(b, p.Character.Level)) return;
        var gambler = Npcs.FirstOrDefault(n => n.TypeId == "gambler");
        if (gambler == null || Vector2.Distance(p.Position, gambler.Position) > 3.5f)
        {
            _events.MessageFor(p, "The gambler waits in the sanctum.");
            return;
        }
        int price = GambleBalance.Price(b, p.Character.Level);
        if (p.Character.Gold < price)
        {
            _events.MessageFor(p, $"A roll on that costs {price} gold — come back richer.");
            return;
        }
        int total = GambleBalance.WeightNormal + GambleBalance.WeightMagic + GambleBalance.WeightRare;
        int roll = _rng.Next(total);
        var rarity = roll < GambleBalance.WeightNormal ? ItemRarity.Normal
            : roll < GambleBalance.WeightNormal + GambleBalance.WeightMagic ? ItemRarity.Magic
            : ItemRarity.Rare;
        var item = Loot.Generate(b, p.Character.Level, rarity);
        if (!p.Character.Inventory.TryAdd(Data, item))
        {
            _events.MessageFor(p, "Your bag is full — fate waits for no one, but she does need room.");
            return;
        }
        p.Character.Gold -= price;
        _events.MessageFor(p, $"{price} gold to fate... she hands you {item.DisplayName(Data)}.");
        p.RecomputeStats(Data);
        _events.PlayerHealthChanged(p);
        _events.CharacterChanged(p);
    }

    /// <summary>Gold price of learning a skill from the trainer (campaign mode).</summary>
    public const int SkillPrice = 75;

    public void LearnSkill(int playerId, string skillId)
    {
        if (!Players.TryGetValue(playerId, out var p)) return;
        if (!Data.Skills.ContainsKey(skillId) || p.Character.GetSkill(skillId) != null) return;
        // Campaign: skills are BOUGHT from the trainer in the sanctum — 75 gold, in
        // person. (Arena/test worlds have no trainer and keep free learning.)
        if (Campaign)
        {
            var trainer = Npcs.FirstOrDefault(n => n.TypeId == "skill_trainer");
            if (trainer == null ||
                System.Numerics.Vector2.Distance(p.Position, trainer.Position) > 3.5f)
            {
                _events.MessageFor(p, "New skills are taught by the trainer in the sanctum.");
                return;
            }
            if (p.Character.Gold < SkillPrice)
            {
                _events.MessageFor(p, $"Training costs {SkillPrice} gold — you can't afford it yet.");
                return;
            }
            p.Character.Gold -= SkillPrice;
            var learnedDef = Data.Skills[skillId];
            _events.MessageFor(p, $"Learned {learnedDef.Name} for {SkillPrice} gold.");
        }
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
            case "warp_next":
                // Dev shortcut through the campaign loop (skips the ready-door dance).
                if (Campaign) TransitionTo(MapIndex >= 3 ? 0 : MapIndex + 1);
                break;
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
            case "give_bow":
            case "give_shield":
            {
                var category = cmd switch
                {
                    "give_mace" => ItemCategory.Mace,
                    "give_staff" => ItemCategory.Staff,
                    "give_bow" => ItemCategory.Bow,
                    _ => ItemCategory.Shield,
                };
                // Prefer bases the character can actually EQUIP (level + attributes) —
                // a debug convenience that hands out unwearable gear helps nobody.
                var pool = Data.Items.Values.Where(b => b.Category == category).ToList();
                var wearable = pool.Where(b =>
                    b.RequiredLevel <= p.Character.Level &&
                    b.RequiredStrength <= p.Stats.Strength + 0.01f &&
                    b.RequiredDexterity <= p.Stats.Dexterity + 0.01f &&
                    b.RequiredIntelligence <= p.Stats.Intelligence + 0.01f).ToList();
                var itemBase = (wearable.Count > 0 ? wearable : pool)
                    .OrderBy(_ => Guid.NewGuid()).FirstOrDefault();
                if (itemBase != null)
                {
                    var given = Loot.Generate(itemBase, 10, ItemRarity.Rare);
                    if (arg == "equip")
                    {
                        // Straight into the hand (dev/screenshot aid).
                        c.Equipment[EquipSlot.MainHand] = given;
                        if (itemBase.TwoHanded || itemBase.Category == ItemCategory.Bow)
                            c.Equipment.Remove(EquipSlot.OffHand);
                        p.RecomputeStats(Data);
                        changed = true;
                    }
                    else
                        changed = GiveItem(p, given);
                }
                break;
            }
            case "give_rare":
            {
                var item = Loot.GenerateEquipment(Data.GetLootTable("default"), 10, ItemRarity.Rare);
                if (item != null) changed = GiveItem(p, item);
                break;
            }
            case "equip_set":
            {
                // Dev/screenshot aid: wear a full named armor family directly — no
                // requirement checks, straight into the slots (debug only).
                string[] setIds = arg switch
                {
                    "leather" => new[] { "leather_hood", "padded_vest", "worn_gloves", "sturdy_boots", "rope_belt" },
                    "hide" => new[] { "hide_hood", "hide_tunic", "hide_gloves", "hide_boots", "rope_belt" },
                    "cloth" => new[] { "cloth_cowl", "cloth_robe", "cloth_wraps", "cloth_slippers", "rope_belt" },
                    "plate" => new[] { "warplate_helm", "iron_plate", "warplate_gauntlets", "warplate_greaves", "rope_belt" },
                    "archer" => new[] { "short_bow", "leather_quiver", "hunters_hood", "hunters_jerkin", "hunters_gloves", "hunters_treads", "rope_belt" },
                    _ => new[] { "iron_cap", "iron_mail", "iron_gauntlets", "iron_greaves", "rope_belt" },
                };
                foreach (var id in setIds)
                    if (Data.Items.TryGetValue(id, out var b))
                        c.Equipment[ItemBase.CompatibleSlots(b.Category).First()] = new ItemInstance
                        {
                            BaseItemId = id,
                            ItemLevel = b.RequiredLevel,
                            Rarity = ItemRarity.Normal,
                            BaseModifierLimit = b.BaseModifierLimit,
                        };
                changed = true;
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
            case "drop_scrolls":
            {
                // One drop of every crafting (Enchanting) scroll type and every Skill
                // Scroll, scattered around the character for crafting/testing sessions.
                foreach (var b in Data.Items.Values.Where(b =>
                             b.Category is ItemCategory.EnchantScroll or ItemCategory.SkillScroll))
                {
                    var item = new ItemInstance
                    {
                        BaseItemId = b.Id,
                        ItemLevel = 1,
                        Rarity = ItemRarity.Normal,
                        BaseModifierLimit = 0,
                        StackCount = b.Category == ItemCategory.EnchantScroll ? Math.Min(5, b.MaxStack) : 1,
                    };
                    SpawnDrop(item, p.Position);
                }
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
            case "give_gold":
                c.Gold += int.TryParse(arg, out int goldAmt) && goldAmt > 0 ? goldAmt : 500;
                changed = true;
                break;
            case "learn":
                // Dev shortcut: learn any skill by id, bypassing the trainer.
                if (Data.Skills.ContainsKey(arg) && c.GetSkill(arg) == null)
                {
                    c.Skills.Add(new LearnedSkill { SkillId = arg });
                    changed = true;
                }
                break;
            case "give_curio":
            {
                string curioId = string.IsNullOrEmpty(arg) ? "merc_contract" : arg;
                if (Data.Items.TryGetValue(curioId, out var curioBase) &&
                    curioBase.Category == ItemCategory.Curio)
                {
                    GiveItem(p, new ItemInstance
                    {
                        BaseItemId = curioId, ItemLevel = 1, Rarity = ItemRarity.Normal,
                        StackCount = curioBase.MaxStack > 1 ? 3 : 1,
                    });
                    changed = true;
                }
                break;
            }
        }

        if (changed) _events.CharacterChanged(p);
    }

    private bool GiveItem(ServerPlayer p, ItemInstance item)
    {
        if (p.Character.Inventory.TryAdd(Data, item)) return true;
        SpawnDrop(item, p.Position);
        return false;
    }

    // ------------------------------------------------------------------ passive tree

    /// <summary>Server-validated passive allocation: the node must exist, be unallocated,
    /// be a start node or adjacent to an allocated one, and the character must have an
    /// unspent point (one per level past the first).</summary>
    public void AllocatePassive(int playerId, string nodeId)
    {
        if (!Players.TryGetValue(playerId, out var p)) return;
        var c = p.Character;
        var tree = Data.PassiveTree;
        if (!tree.ById.TryGetValue(nodeId, out var node)) return;
        if (c.AllocatedPassives.Contains(nodeId)) return;
        int available = Skills.PassiveTree.PointsForLevel(c.Level) - c.AllocatedPassives.Count;
        if (available <= 0)
        {
            _events.MessageFor(p, "No passive points available.");
            return;
        }
        bool reachable = node.Start ||
                         tree.Neighbors(nodeId).Any(n => c.AllocatedPassives.Contains(n));
        if (!reachable)
        {
            _events.MessageFor(p, "That passive is not connected to your allocated nodes.");
            return;
        }
        c.AllocatedPassives.Add(nodeId);
        p.RecomputeStats(Data);
        // Max health/mana may have grown; keep current values within the new caps and
        // let clients re-read both bars.
        p.Health = MathF.Min(p.Health + 0, p.Stats.MaxHealth);
        p.Mana = MathF.Min(p.Mana, p.Stats.MaxMana);
        _events.PlayerHealthChanged(p);
        _events.CharacterChanged(p);
    }

    // ------------------------------------------------------------------ merchant shop

    public const int ShopSlotCount = 6;
    private const int ShopBuyMarkup = 2;   // buy price = item value x2
    private const float ShopInteractRange = 3f;

    /// <summary>Deterministic string hash (FNV-1a). string.GetHashCode is randomized per
    /// process, which would reroll every shop each session — never use it for seeds.</summary>
    private static int StableHash(string s)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (char ch in s ?? "") { h ^= ch; h *= 16777619; }
            return (int)h;
        }
    }

    private static Guid DeterministicGuid(int seed, int slot)
    {
        var bytes = new byte[16];
        unchecked
        {
            uint a = (uint)seed, b = (uint)(seed * 31 + slot * 101 + 7);
            for (int i = 0; i < 16; i += 4)
            {
                a = a * 1664525 + 1013904223 + b;
                b ^= a >> 13;
                bytes[i] = (byte)a; bytes[i + 1] = (byte)(a >> 8);
                bytes[i + 2] = (byte)(a >> 16); bytes[i + 3] = (byte)(a >> 24);
            }
        }
        return new Guid(bytes);
    }

    /// <summary>
    /// The merchant's stock for THIS player: seeded by (character name, character level),
    /// so every player sees their own shop, it rerolls exactly once per level-up, and
    /// leaving/rejoining a session never rerolls it (no shop-scumming with friends).
    /// </summary>
    public List<ShopEntry> GetShopStock(ServerPlayer p)
    {
        var c = p.Character;
        if (c.ShopLevel != c.Level)
        {
            c.ShopLevel = c.Level;
            c.ShopSoldSlots.Clear();
            _events.CharacterChanged(p);
        }
        int seed = StableHash(c.Name) ^ unchecked(c.Level * (int)0x9E3779B1);
        var gen = new LootGenerator(Data, new Random(seed));
        var table = Data.GetLootTable("default");
        var stock = new List<ShopEntry>();
        for (int slot = 0; slot < ShopSlotCount; slot++)
        {
            // Last slot is always a rare — the "window piece" worth saving for.
            var item = gen.GenerateEquipment(table, c.Level,
                slot == ShopSlotCount - 1 ? ItemRarity.Rare : null);
            if (item == null) continue;
            item.InstanceId = DeterministicGuid(seed, slot); // stable identity per (player, level, slot)
            stock.Add(new ShopEntry
            {
                Slot = slot,
                Item = item,
                Price = Math.Max(1, item.GoldValue(Data) * ShopBuyMarkup),
                Sold = c.ShopSoldSlots.Contains(slot),
            });
        }
        return stock;
    }

    private ServerNpc ShopNpcInRange(ServerPlayer p, int npcId)
    {
        var npc = Npcs.FirstOrDefault(n => n.Id == npcId);
        if (npc == null) return null;
        return Vector2.Distance(npc.Position, p.Position) <= ShopInteractRange ? npc : null;
    }

    public void ShopOpen(int playerId, int npcId)
    {
        if (!Players.TryGetValue(playerId, out var p) || !p.Alive) return;
        var npc = ShopNpcInRange(p, npcId);
        // The trainer sells skills, the sellsword deals in contracts, and the
        // researcher trades in paper — none of them run a gear stall.
        if (npc == null || npc.TypeId is "skill_trainer" or "mercenary" or "researcher") return;
        _events.ShopStockFor(p, npcId, GetShopStock(p));
    }

    public void ShopBuy(int playerId, int npcId, int slot)
    {
        if (!Players.TryGetValue(playerId, out var p) || !p.Alive) return;
        var buyNpc = ShopNpcInRange(p, npcId);
        if (buyNpc == null || buyNpc.TypeId == "skill_trainer") return;
        var entry = GetShopStock(p).FirstOrDefault(e => e.Slot == slot);
        if (entry == null || entry.Sold) return;
        var c = p.Character;
        if (c.Gold < entry.Price)
        {
            _events.MessageFor(p, "Not enough gold.");
            return;
        }
        if (!c.Inventory.TryAdd(Data, entry.Item))
        {
            _events.MessageFor(p, "Your inventory is full.");
            return;
        }
        c.Gold -= entry.Price;
        c.ShopSoldSlots.Add(slot);
        _events.CharacterChanged(p);
        _events.ShopStockFor(p, npcId, GetShopStock(p));
    }

    /// <summary>Sold-by-mistake insurance: how many recent sales the merchant holds.</summary>
    /// <summary>Buy-back keeps EVERYTHING sold during the current character level
    /// (wiped on level-up — see GrantCharacterXp); the cap is only a runaway guard.</summary>
    public const int BuybackSlots = 500;

    public void ShopSell(int playerId, Guid itemInstanceId)
    {
        if (!Players.TryGetValue(playerId, out var p) || !p.Alive) return;
        if (Npcs.All(n => Vector2.Distance(n.Position, p.Position) > ShopInteractRange)) return;
        var c = p.Character;
        var placed = c.Inventory.FindByInstance(itemInstanceId);
        if (placed == null) return;
        c.Inventory.Remove(itemInstanceId);
        int value = Math.Max(1, placed.Item.GoldValue(Data));
        c.Gold += value;
        // The merchant keeps recent sales on the counter — buy-back at the same price.
        p.Buyback.Add(new ShopEntry { Item = placed.Item, Price = value });
        while (p.Buyback.Count > BuybackSlots) p.Buyback.RemoveAt(0);
        for (int i = 0; i < p.Buyback.Count; i++) p.Buyback[i].Slot = i;
        _events.CharacterChanged(p);
        // Refresh the open shop so its buy-back tab shows the item immediately.
        var shopNpc = Npcs.FirstOrDefault(n => n.TypeId != "skill_trainer" &&
            Vector2.Distance(n.Position, p.Position) <= ShopInteractRange);
        if (shopNpc != null) _events.ShopStockFor(p, shopNpc.Id, GetShopStock(p));
    }

    /// <summary>Buy a previously sold item back, at the price it fetched.</summary>
    public void ShopBuyback(int playerId, int npcId, Guid itemInstanceId)
    {
        if (!Players.TryGetValue(playerId, out var p) || !p.Alive) return;
        var npc = ShopNpcInRange(p, npcId);
        if (npc == null || npc.TypeId == "skill_trainer") return;
        var entry = p.Buyback.FirstOrDefault(b => b.Item.InstanceId == itemInstanceId);
        if (entry == null) return;
        var c = p.Character;
        if (c.Gold < entry.Price)
        {
            _events.MessageFor(p, "Not enough gold.");
            return;
        }
        if (!c.Inventory.TryAdd(Data, entry.Item))
        {
            _events.MessageFor(p, "Your inventory is full.");
            return;
        }
        c.Gold -= entry.Price;
        p.Buyback.Remove(entry);
        for (int i = 0; i < p.Buyback.Count; i++) p.Buyback[i].Slot = i;
        _events.CharacterChanged(p);
        _events.ShopStockFor(p, npcId, GetShopStock(p));
    }
}
