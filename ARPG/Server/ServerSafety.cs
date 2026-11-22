using ARPG.Items;
using ARPG.World;

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
            // The caravan stand's workbench: "ready" there means "call the wave", wherever
            // the builders have walked to since.
            if (Map.Kind == MapKind.Defense && ReferenceEquals(set, _readyAtDefenseDoor))
            {
                if (DefPhase == DefensePhase.Build) StartWave();
                return;
            }
            // Doors: ready marks are sticky, so a waiting player may have wandered off —
            // even over to a DIFFERENT door. Only complete the move if one of them still
            // stands at this set's own door and it is the door their key would answer;
            // otherwise leave the marks as they are (they re-press when they come back).
            foreach (int waiting in set.ToList())
            {
                if (!Players.TryGetValue(waiting, out var wp) || !StandsAtDoorOf(wp, set)) continue;
                set.Remove(waiting);
                DoorReady(waiting); // re-adds them and completes the move with the usual checks
                return;
            }
        }
        _events.ZoneStateChanged(this); // the ready count on everyone's HUD changed
    }

    /// <summary>Is this player within reach of a door that <paramref name="set"/> belongs
    /// to, with no OTHER door nearer (DoorReady answers the nearest door in reach)?</summary>
    private bool StandsAtDoorOf(ServerPlayer p, HashSet<int> set)
    {
        var doors = new List<(System.Numerics.Vector2 pos, HashSet<int> ready)>();
        if (Map.IsRoad)
        {
            doors.Add((Map.ExitDoor, _readyAtDoor));
            doors.Add((Map.EntryDoor, _readyAtDoor));
        }
        else if (Map.IsHub)
        {
            doors.Add((Map.ExitDoor, _readyAtDoor));
            doors.Add((Map.DefenseDoor, _readyAtDefenseDoor));
            doors.Add((Map.TutorialDoor, _readyAtTutorialDoor));
            if (Map.Kind == MapKind.RuinsHub) doors.Add((Map.EntryDoor, _readyAtRoad));
            if (PortalOpen) doors.Add((Map.PortalSpot, _readyAtPortal));
        }
        else doors.Add((Map.ExitDoor, _readyAtDoor));
        var inReach = doors
            .Where(d => d.pos != System.Numerics.Vector2.Zero && System.Numerics.Vector2.Distance(p.Position, d.pos) <= 2.6f)
            .OrderBy(d => System.Numerics.Vector2.Distance(p.Position, d.pos)).ToList();
        return inReach.Count > 0 && ReferenceEquals(inReach[0].ready, set);
    }
}
