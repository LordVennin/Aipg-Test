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
