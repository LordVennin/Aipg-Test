using System.Numerics;
using ARPG.Data;
using ARPG.Items;
using ARPG.Sim;
using ARPG.Skills;
using ARPG.Stats;

namespace ARPG.Server;

public enum EnemyState : byte
{
    Idle,
    Chase,
    Attack,
    Dead,
}

/// <summary>Authoritative player state held by the server (host).</summary>
public class ServerPlayer
{
    public int Id;
    public string Name;
    public Vector2 Position;
    public Vector2 Facing = new(1, 0);
    public float Health;
    public bool Alive = true;
    public float RespawnTimer;
    public CharacterData Character;
    public ComputedStats Stats;
    /// <summary>Next allowed use time per skill id (server clock seconds).</summary>
    public readonly Dictionary<string, float> SkillReadyAt = new();

    // Dodge: cooldown and invulnerability are server-authoritative.
    public float NextDodgeAt;
    public float InvulnerableUntil;

    public const float Radius = 0.35f;
    public const float PickupRange = 2.0f;

    public void RecomputeStats(GameData data)
    {
        Stats = StatCalculator.Compute(data, Character);
        if (Health > Stats.MaxHealth) Health = Stats.MaxHealth;
    }
}

/// <summary>Authoritative enemy with a simple Idle/Chase/Attack/Dead state machine.</summary>
public class ServerEnemy
{
    public int Id;
    public EnemyDefinition Def;
    public Vector2 Position;
    public float Health;
    public EnemyState State = EnemyState.Idle;
    public float AttackReadyAt;
    public int TargetPlayerId = -1;

    // Ignite (burning damage over time) bookkeeping. Burn ticks apply every frame but
    // damage events/health updates are batched via the accumulator to avoid packet spam.
    public float BurnDps;
    public float BurnTimeLeft;
    public float BurnAccum;
    public float BurnEmitTimer;

    // Kill credit for XP and skill XP.
    public int LastHitByPlayer = -1;
    public string LastHitSkillId;

    public bool Dead => State == EnemyState.Dead;
}

public class ServerProjectile
{
    public int Id;
    public bool FromPlayer;
    public int OwnerId;          // player id or enemy id
    public string SkillId;       // player projectiles: which skill fired it (for credit + display)
    public Vector2 Position;
    public Vector2 Direction;
    public float Speed;
    public float MaxRange;
    public float Traveled;
    public float MinDamage;
    public float MaxDamage;
    public DamageKind DamageKind;
    public float IgniteChance;
}

/// <summary>An ItemInstance lying in the world. Generated once by the host; never rerolled.</summary>
public class WorldItem
{
    public Guid DropId = Guid.NewGuid();
    public Vector2 Position;
    public ItemInstance Item;
}

/// <summary>Respawning enemy spawn point.</summary>
public class EnemySpawner
{
    public Vector2 Position;
    public string EnemyTypeId;
    public int AliveEnemyId = -1;
    public float RespawnAt;
}
