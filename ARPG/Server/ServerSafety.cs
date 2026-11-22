using ARPG.Items;

namespace ARPG.Server;

/// <summary>
/// Soft-lock safety nets (ServerWorld partial) from the batch 85 audit: situations
/// where a character could be left unable to progress, each closed here.
/// </summary>
public partial class ServerWorld
{
    /// <summary>Every weapon the character still owns: equipped, bagged or stashed.</summary>
    private IEnumerable<ItemInstance> OwnedWeapons(Sim.CharacterData c) =>
        c.Equipment.Values
            .Concat(c.Inventory.Items.Select(pl => pl.Item))
            .Concat(c.Stashes.Values.SelectMany(g => g.Items.Select(pl => pl.Item)))
            .Where(it => it != null && it.GetBase(Data).IsWeapon);

    /// <summary>
    /// A character with no weapon at all cannot use a weapon skill — and a warrior or
    /// archer whose only skills need one cannot fight at all. If that ever happens
    /// (sold it, dropped it and left the map, a save from another build lost it), the
    /// camp hands them their class's plain starting weapon. Called on join and whenever
    /// the party arrives home.
    /// </summary>
    public bool EnsureHasWeapon(ServerPlayer p)
    {
        var c = p.Character;
        if (OwnedWeapons(c).Any()) return false;
        var cls = Data.Classes.FirstOrDefault(cl => cl.Id == c.ClassId) ?? Data.Classes.FirstOrDefault();
        if (cls?.StartWeapon == null || !Data.Items.TryGetValue(cls.StartWeapon, out var weaponBase)) return false;
        var weapon = new ItemInstance
        {
            BaseItemId = weaponBase.Id, ItemLevel = 1, Rarity = ItemRarity.Normal,
            BaseModifierLimit = weaponBase.BaseModifierLimit,
        };
        weapon.EnsureSlotData();
        if (!c.Equipment.ContainsKey(EquipSlot.MainHand))
        {
            c.Equipment[EquipSlot.MainHand] = weapon;
            p.RecomputeStats(Data);
            _events.PlayerHealthChanged(p);
        }
        else if (!c.Inventory.TryAdd(Data, weapon)) SpawnDrop(weapon, p.Position, p.Height);
        _events.MessageFor(p, $"You have no weapon — the camp finds you a spare {weaponBase.Name}.");
        MarkCharacterDirty(p);
        return true;
    }

    /// <summary>
    /// A player left (disconnect): they no longer hold up — or count toward — any door.
    /// If everyone STILL here was already ready, the door they were waiting at opens now
    /// instead of leaving them standing at "1/1 ready" until someone re-presses the key.
    /// </summary>
    private void ReleaseDoorsAfterLeave(int leftPlayerId)
    {
        var sets = new[] { _readyAtDoor, _readyAtDefenseDoor, _readyAtTutorialDoor, _readyAtPortal, _readyAtRoad };
        foreach (var set in sets) set.Remove(leftPlayerId);
        if (!Campaign) return;
        int alive = Players.Values.Count(pl => pl.Alive);
        if (alive == 0) return;
        foreach (var set in sets)
        {
            // Stale ids (dead or gone) never count.
            set.RemoveWhere(id => !Players.TryGetValue(id, out var pl) || !pl.Alive);
            if (set.Count == 0 || set.Count < alive) continue;
            // Re-press the key for one of the waiting players: DoorReady works out which
            // door they stand at and completes the move with the usual checks.
            int waiting = set.First();
            set.Remove(waiting);
            DoorReady(waiting);
            return;
        }
        _events.ZoneStateChanged(this); // the ready count on everyone's HUD changed
    }
}
