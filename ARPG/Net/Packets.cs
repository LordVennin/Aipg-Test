using System.Numerics;
using LiteNetLib.Utils;

namespace ARPG.Net;

/// <summary>
/// All network message types. Complex payloads (characters, items) travel as JSON strings —
/// the same serialization used for save files — so generated items are never rerolled or
/// re-interpreted on the receiving side. Hot-path state updates use compact binary fields.
/// </summary>
public enum PacketType : byte
{
    // Client -> Server
    JoinRequest,
    PlayerState,
    UseSkill,
    PickupRequest,
    MoveItemRequest,
    DropItemRequest,
    LearnSkillRequest,
    AssignHotbarRequest,
    DebugCommand,
    DodgeRequest,
    ApplyEnchantRequest,
    /// <summary>Open a merchant's shop (npc id) — the server answers with ShopStock.</summary>
    ShopOpenRequest,
    /// <summary>Buy the item in a stock slot (npc id, slot index).</summary>
    ShopBuyRequest,
    /// <summary>Sell an inventory item to the merchant (item instance id).</summary>
    ShopSellRequest,
    /// <summary>Allocate a passive tree node (node id). Server-validated.</summary>
    AllocatePassiveRequest,
    /// <summary>Spend banked skill XP to level a skill up (skill id). Server-validated.</summary>
    LevelSkillRequest,
    /// <summary>Adjust a summon skill's minion count from the Skill Menu (skill id, +1/-1).</summary>
    SummonAdjustRequest,
    /// <summary>Command all summons to a world point (hasPoint=false clears the rally).</summary>
    SummonRallyRequest,

    // Server -> Client
    JoinAccept,
    JoinDeny,
    PlayerJoined,
    PlayerLeft,
    PlayerStates,
    PlayerHealth,
    PlayerDeath,
    PlayerRespawn,
    EnemySpawn,
    EnemyStates,
    EnemyHealth,
    EnemyDeath,
    ProjectileSpawn,
    ProjectileDespawn,
    WorldItemSpawn,
    WorldItemRemove,
    CharacterState,
    SkillEffect,
    ServerMessage,
    DodgeEvent,
    DamageEvent,
    /// <summary>Visual equipment state of a player (currently: main-hand weapon base id),
    /// so all clients can render what each player is holding.</summary>
    PlayerAppearance,
    /// <summary>Periodic per-player round-trip pings, for the HUD player list.</summary>
    PlayerPings,
    /// <summary>Chain-lightning path: the exact points the bolt leaps between.</summary>
    ChainEffect,
    /// <summary>A boss ground-slam AoE burst (position, radius, height) for the visual.</summary>
    EnemySlam,
    EnemyAttack,
    SummonAttack,
    /// <summary>A friendly NPC in the world (id, type id, position, height).</summary>
    NpcInfo,
    /// <summary>A merchant's stock for THIS player: per slot the item, price and sold flag.</summary>
    ShopStock,
    /// <summary>A world visual effect: kind ("zap", "firepatch"), position, radius, duration, height.</summary>
    WorldEffect,
    /// <summary>A summon spawned (id, owner, skill id, position, height, max/current hp).</summary>
    SummonSpawn,
    /// <summary>Summon movement/health snapshots (10 Hz).</summary>
    SummonStates,
    /// <summary>A summon died or was dismissed (id).</summary>
    SummonDespawn,

    // Campaign loop (batch 15)
    /// <summary>Client -> server: toggle READY at the exit door (transition when all living players are ready).</summary>
    DoorReadyRequest,
    /// <summary>Client -> server: open a hub chest (chest id).</summary>
    ChestOpenRequest,
    /// <summary>Server -> client: the world moved to another map — rebuild it locally and wipe replicated state.
    /// Payload: seed, theme id, kind, loop, map index, enemy level, exit locked, YOUR new position + height.</summary>
    MapChange,
    /// <summary>Server -> client: campaign zone state (loop, map index, enemy level, ready count, living players, exit locked).</summary>
    ZoneState,
    /// <summary>Server -> client: one chest's state (id, position, height, opened).</summary>
    ChestInfo,

    // Potions + boss dash (batch 18)
    /// <summary>Client -> server: drink a potion flask (byte kind: 0 = health, 1 = mana).</summary>
    PotionRequest,
    /// <summary>Server -> client: a telegraphed enemy dash (id, phase 1 line-telegraph / 2 launch,
    /// position, direction, range, windup, height).</summary>
    EnemyDash,
}

/// <summary>Where an item sits, for inventory move requests: grid cell, equip slot, or a skill's scroll slot.</summary>
public enum ItemLocationKind : byte
{
    Grid,
    Equipment,
    ScrollSlot,
}

public struct ItemLocation
{
    public ItemLocationKind Kind;
    public int X;              // grid cell x
    public int Y;              // grid cell y
    public byte EquipSlot;     // (EquipSlot) when Kind == Equipment
    public string SkillId;     // when Kind == ScrollSlot
    public int ScrollIndex;    // when Kind == ScrollSlot

    public static ItemLocation AtGrid(int x, int y) => new() { Kind = ItemLocationKind.Grid, X = x, Y = y, SkillId = "" };
    public static ItemLocation AtEquip(Items.EquipSlot slot) => new() { Kind = ItemLocationKind.Equipment, EquipSlot = (byte)slot, SkillId = "" };
    public static ItemLocation AtScroll(string skillId, int index) => new() { Kind = ItemLocationKind.ScrollSlot, SkillId = skillId, ScrollIndex = index };

    public void Write(NetDataWriter w)
    {
        w.Put((byte)Kind);
        w.Put(X);
        w.Put(Y);
        w.Put(EquipSlot);
        w.Put(SkillId ?? "");
        w.Put(ScrollIndex);
    }

    public static ItemLocation Read(NetDataReader r) => new()
    {
        Kind = (ItemLocationKind)r.GetByte(),
        X = r.GetInt(),
        Y = r.GetInt(),
        EquipSlot = r.GetByte(),
        SkillId = r.GetString(),
        ScrollIndex = r.GetInt(),
    };
}

/// <summary>Helpers for composing packets. Every writer starts with the PacketType byte.</summary>
public static class Packets
{
    public static NetDataWriter Make(PacketType type)
    {
        var w = new NetDataWriter();
        w.Put((byte)type);
        return w;
    }

    public static void PutVec2(this NetDataWriter w, Vector2 v)
    {
        w.Put(v.X);
        w.Put(v.Y);
    }

    public static Vector2 GetVec2(this NetDataReader r) => new(r.GetFloat(), r.GetFloat());

    public static void PutGuid(this NetDataWriter w, Guid g) => w.Put(g.ToString("N"));
    public static Guid GetGuid(this NetDataReader r) => Guid.ParseExact(r.GetString(), "N");
}
