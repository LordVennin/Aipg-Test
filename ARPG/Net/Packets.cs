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

    /// <summary>Client -> server: refill equipped flasks at the sanctum fountain (batch 19).</summary>
    UseFountainRequest,

    /// <summary>Client -> server: buy a previously sold item back (npc id + item instance guid).</summary>
    ShopBuybackRequest,

    /// <summary>Client -> server: a revive channel pulse (target player id) — sent while
    /// holding the interact key beside a dead teammate; the server does the timekeeping.</summary>
    ReviveRequest,
    /// <summary>Server -> client: a caster's ranged AoE (at, radius, windup, height, phase).</summary>
    EnemyCastAoe,

    /// <summary>Client -> server: gamble a specific gear base from the gambler NPC
    /// (base item id). Price/eligibility are shared rules (GambleBalance); the server
    /// re-validates gold, level and gambler proximity, then rolls the rarity.</summary>
    GambleRequest,

    // Corpses (batch 41)
    /// <summary>Server -> client: an enemy corpse now exists (id, enemy type id, position,
    /// height). Corpses are authoritative server records — future skills can target them.</summary>
    CorpseSpawn,
    /// <summary>Server -> client: a corpse was consumed/expired (id).</summary>
    CorpseRemove,

    // Wagon defense (batch 47)
    /// <summary>Server -> client: defense-run state (phase byte 0 build/1 wave/2 won/3 lost,
    /// wave index, waves total, wagon hp, wagon max hp, build seconds left).</summary>
    DefenseState,
    /// <summary>Server -> client: a structure exists (id, kind byte, position, height,
    /// hp, max hp). Kinds: 0 crossbow turret, 1 spiked barrier, 2 flame turret,
    /// 3 wagon, 4 workbench.</summary>
    StructureSpawn,
    /// <summary>Server -> client: a structure's health changed (id, hp).</summary>
    StructureHealth,
    /// <summary>Server -> client: a structure was destroyed/removed (id).</summary>
    StructureRemove,
    /// <summary>Server -> client: an NPC left the world (id) — defense NPCs step away
    /// when the wave starts.</summary>
    NpcRemove,
    /// <summary>Client -> server: build a structure (kind byte, position). The server
    /// re-validates phase, gold, terrain and spacing.</summary>
    BuildRequest,

    // Researcher + mercenaries (batch 48)
    /// <summary>Client -> server: deal with the researcher (action byte: 0 = spend one
    /// Mercenary Contract on a randomized hire, 1 = hand over the Flamethrower
    /// Blueprint). Server validates proximity and the items.</summary>
    ResearchRequest,
    /// <summary>Client -> server: deploy a hired mercenary onto the defense map during
    /// a build phase (merc id, position). One deployment per merc per run.</summary>
    DeployMercRequest,
    /// <summary>Client -> server: repair every damaged structure at the workbench
    /// (build phases only; cost = DefenseBalance.RepairCost of the missing hp).</summary>
    RepairRequest,
    /// <summary>Server -> client: play the scripted scene with this id (tutorial beats).
    /// Clients run the cutscene locally — letterbox, camera focus, dialogue.</summary>
    CutsceneEvent,
}

/// <summary>Where an item sits, for inventory move requests: grid cell, equip slot, or a skill's scroll slot.</summary>
public enum ItemLocationKind : byte
{
    Grid,
    Equipment,
    ScrollSlot,
    /// <summary>A cell in a stash CONTAINER (the container id rides the SkillId field —
    /// same wire shape). Server-validated against the container's reach.</summary>
    Stash,
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
    /// <summary>The SkillId field carries the stash CONTAINER id (identical wire shape).</summary>
    public static ItemLocation AtStash(string containerId, int x, int y) => new() { Kind = ItemLocationKind.Stash, SkillId = containerId, X = x, Y = y };
    public string ContainerId => SkillId;

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
