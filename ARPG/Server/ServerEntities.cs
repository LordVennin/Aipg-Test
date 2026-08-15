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

/// <summary>Bitmask of enemy debuffs replicated in enemy snapshots. Each flag gets its
/// own tiny indicator icon above the enemy's head; new debuffs claim the next bit.</summary>
public static class EnemyDebuffs
{
    public const byte Stunned = 1 << 0;
    public const byte Burning = 1 << 1;
    public const byte Slowed = 1 << 2;
    public const byte Chilled = 1 << 3;
    public const byte Frozen = 1 << 4;   // blue tint, no movement or attacks
    public const byte Shocked = 1 << 5;  // electrocuted: periodic freeze rolls + sparks
    public const byte Poisoned = 1 << 6;
    public const byte Bleeding = 1 << 7;
}

/// <summary>Player debuff bitmask replicated in PlayerStates (ailments affect players too).</summary>
public static class PlayerDebuffs
{
    public const byte Frozen = 1 << 0;
    public const byte Shocked = 1 << 1;
}

/// <summary>Authoritative player state held by the server (host).</summary>
public class ServerPlayer
{
    public int Id;
    public string Name;
    public Vector2 Position;
    /// <summary>Surface height in elevation levels (0 = ground floor); see GameMap.</summary>
    public float Height;
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
    /// <summary>Client position updates are ignored until this time (set on map
    /// transitions, while the client's in-flight states still carry old-map coords).</summary>
    public float IgnoreStateUntil;

    /// <summary>Blocking goes on cooldown after each successful block (server-authoritative).</summary>
    public float NextBlockReadyAt;

    // Potion flasks (PotionBalance): restore-over-time, charges refill from kills.
    public int HealthPotionCharges = ARPG.Stats.PotionBalance.StartCharges;
    public int ManaPotionCharges = ARPG.Stats.PotionBalance.StartCharges;
    public float PotionHealUntil;
    public float PotionHealPerSec;
    public float PotionManaUntil;
    public float PotionManaPerSec;

    /// <summary>Global skill lockout: no skill may be used before this server time
    /// (set to now + the last-used skill's UseTime).</summary>
    public float GlobalSkillReadyAt;

    // Ailments on players (electrocute's periodic freeze affects players too).
    public float FrozenUntil;
    public float ElectrocutedUntil;
    public float NextShockRollAt;
    /// <summary>Position pinned while frozen — movement updates are rejected against it.</summary>
    public Vector2 FrozenAt;

    /// <summary>Desired minion count per summon skill id (managed via the Skill Menu).</summary>
    public readonly Dictionary<string, int> DesiredSummons = new();
    /// <summary>Rally points PER SUMMON SKILL (absent = that pack follows the summoner).
    /// Separate marks per skill let archers hold a ridge while warriors hold a door.</summary>
    public readonly Dictionary<string, (Vector2 Point, float Height)> SummonRallies = new();

    /// <summary>Last health value broadcast to clients (throttles regen sync spam).</summary>
    public float LastSyncedHealth;

    /// <summary>Current mana (server-authoritative; skills spend it, level-based regen refills it).</summary>
    public float Mana;
    public float LastSyncedMana;

    /// <summary>Total maximum mana RESERVED by living/awaiting-respawn summons —
    /// the usable pool is Stats.MaxMana - ManaReserved (recomputed by the world
    /// whenever summons change).</summary>
    public float ManaReserved;

    /// <summary>Current Energy Shield: absorbs damage before life, recharges after
    /// EnergyShieldBalance.RechargeDelay seconds without taking damage.</summary>
    public float EnergyShield;
    public float LastSyncedEnergyShield;
    /// <summary>Server time of the last damage taken (any hit resets the ES recharge delay).</summary>
    public float LastDamagedAt = -999f;

    public const float Radius = 0.35f;
    public const float PickupRange = 2.0f;

    public void RecomputeStats(GameData data)
    {
        Stats = StatCalculator.Compute(data, Character);
        if (Health > Stats.MaxHealth) Health = Stats.MaxHealth;
        if (Mana > Stats.MaxMana) Mana = Stats.MaxMana;
        if (EnergyShield > Stats.MaxEnergyShield) EnergyShield = Stats.MaxEnergyShield;
    }
}

/// <summary>Authoritative enemy with a simple Idle/Chase/Attack/Dead state machine.</summary>
/// <summary>Elite modifiers rolled onto pack leaders and bosses. Flags so they can
/// stack; replicated in EnemySpawn packets for tinting and name display.</summary>
[Flags]
public enum EliteAffix : byte
{
    None = 0,
    Brutish = 1,  // much more life and damage
    Swift = 2,    // faster movement and attacks
    Warded = 4,   // elemental resistance shell + extra life
    Boss = 8,     // miniboss: slam attack, stun resistance, guaranteed loot
}

public class ServerEnemy
{
    public int Id;
    public EnemyDefinition Def;
    /// <summary>Effective level: the def's native level, or a spawner/zone override —
    /// health/damage/XP scale up through EnemyLevelScaling, and XP awards compare
    /// against this for the under-level kill penalty.</summary>
    public int Level;
    public Vector2 Position;
    /// <summary>Surface height in elevation levels (see GameMap).</summary>
    public float Height;
    public float Health;
    /// <summary>Actual max health (definition value scaled by elite affixes).</summary>
    public float MaxHealth;
    public EnemyState State = EnemyState.Idle;
    public float AttackReadyAt;
    public int TargetPlayerId = -1;

    // Telegraphed melee swing: while Winding, the enemy stands committed to a swing
    // along WindupDir that resolves when the clock passes WindupUntil. Sword-style
    // enemies keep re-aiming WindupDir at their victim until just before impact.
    public bool Winding;
    public float WindupUntil;
    public Vector2 WindupDir;
    public int WindupPlayerId = -1;
    public int WindupSummonId = -1;
    /// <summary>Post-swing pause: no movement or new swings until this passes.</summary>
    public float RecoverUntil;
    /// <summary>While the server clock is below this, the enemy neither moves nor attacks.</summary>
    public float StunnedUntil;
    /// <summary>While the server clock is below this, the enemy moves at reduced speed.</summary>
    public float SlowedUntil;

    // Elite/pack state. Multipliers default to 1 so normal enemies are unaffected.
    public EliteAffix Affixes;
    public int PackId = -1;           // index into ServerWorld.Packs, -1 = unaffiliated
    public float DamageScale = 1f;
    public float SpeedScale = 1f;
    public float CooldownScale = 1f;
    public float BonusResist;         // flat % added to every resistance (Warded)
    public float XpScale = 1f;
    public float SlamReadyAt;         // boss ground-slam cooldown gate
    public float SlamResolveAt;       // >0 while a telegraphed slam is winding up
    /// <summary>Next time this enemy may spawn its adds (0 = not armed yet; armed with
    /// the def's first-delay when the enemy first engages, so a boss never opens with
    /// the summon).</summary>
    public float NextAddSpawnAt;
    // Telegraphed dash charge (bosses at DashMinLevel+): stand still through the
    // prepare, then barrel down the locked line for heavy contact damage.
    public float DashPrepareUntil;
    public float DashUntil;
    public Vector2 DashDir;
    public float DashReadyAt;
    /// <summary>Players already struck by the CURRENT dash (one hit per pass).</summary>
    public readonly HashSet<int> DashHitIds = new();

    // Damage-over-time ailments. Ticks apply every frame but damage events/health
    // updates are batched via accumulators to avoid packet spam.
    public float BurnDps;
    public float BurnTimeLeft;
    public float BurnAccum;
    public float BurnEmitTimer;
    public float PoisonDps;
    public float PoisonTimeLeft;
    public float PoisonAccum;
    public float PoisonEmitTimer;
    public float BleedDps;
    public float BleedTimeLeft;
    public float BleedAccum;
    public float BleedEmitTimer;

    // Chill/freeze: magnitude 0..cap builds from chilling hits and decays constantly;
    // at the cap each further hit can freeze outright.
    public float ChillMagnitude;
    public float FrozenUntil;

    // Electrocute: while active, a periodic roll can freeze the enemy in place briefly.
    public float ElectrocutedUntil;
    public float NextShockRollAt;

    /// <summary>Fire exposure stacks (Scorched Earth patches): each entry is the server
    /// time the stack expires; active stacks each shred 1% fire resistance (max 25).</summary>
    public readonly List<float> FireExposure = new();

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
    /// <summary>Flight height in elevation levels (inherited from the caster's surface).</summary>
    public float Height;
    public Vector2 Direction;
    public float Speed;
    public float MaxRange;
    public float Traveled;
    /// <summary>Height change per tile traveled — a shot arcing down from an overlook
    /// (or up at one) descends/climbs linearly toward its target's elevation.</summary>
    public float HeightStep;
    public float MinDamage;
    public float MaxDamage;
    public DamageKind DamageKind;
    /// <summary>True when this projectile is a direct ATTACK (deflectable by the DEX
    /// defense); enemy defs mark spell-like projectiles false. Data-driven.</summary>
    public bool AttackHit = true;
    public float IgniteChance;
    /// <summary>Critical strike stats carried by player projectiles, rolled at impact.</summary>
    public float CritChance;
    public float CritDamage;
    /// <summary>Typed added-damage components carried by the projectile (spell adds).</summary>
    public List<DamageComponent> Added;
    /// <summary>Full skill stats snapshot for ailment rolls on impact (chances and
    /// magnitudes already folded with the caster's increases at cast time).</summary>
    public EffectiveSkillStats Ailments;
    /// <summary>Sprite name override (shatter shards use "IceShard" instead of the
    /// parent skill's sprite). Null = the skill definition's ProjectileSprite.</summary>
    public string SpriteOverride;
}

/// <summary>An ItemInstance or gold pile lying in the world. Generated once by the host.</summary>
public class WorldItem
{
    public Guid DropId = Guid.NewGuid();
    public Vector2 Position;
    /// <summary>Surface height the drop rests on (see GameMap).</summary>
    public float Height;
    /// <summary>The dropped item, or null when this drop is a gold pile.</summary>
    public ItemInstance Item;
    /// <summary>Gold amount when Item is null.</summary>
    public int GoldAmount;

    public bool IsGold => Item == null;
}

/// <summary>Respawning enemy spawn point.</summary>
public class EnemySpawner
{
    public Vector2 Position;
    public string EnemyTypeId;
    /// <summary>0 = the def's native level; otherwise spawn at this level (stats/XP
    /// scale through EnemyLevelScaling — reuse any enemy type in late-game zones).</summary>
    public int EnemyLevel;
    public int AliveEnemyId = -1;
    public float RespawnAt;
}

/// <summary>An authored encounter: a group of enemies spawned together around a point,
/// sharing aggro (alert one, alert the pack) and respawning as a group. One member can
/// carry elite affixes (the pack leader); a Boss pack is the zone's miniboss fight.</summary>
public class PackSpawner
{
    public Vector2 Position;
    /// <summary>Enemy type id -> count, spawned scattered around Position.</summary>
    public (string typeId, int count)[] Entries;
    /// <summary>Affixes applied to the FIRST spawned member (the leader). None = no elite.</summary>
    public EliteAffix LeaderAffixes;
    /// <summary>0 = each def's native level; otherwise every member spawns at this level.</summary>
    public int EnemyLevel;
    public float ScatterRadius = 1.4f;
    public float RespawnDelay = 45f;
    /// <summary>Campaign packs spawn ONCE at map build and stay dead when cleared —
    /// run maps are placed encounters, not the test arena's endless respawns.</summary>
    public bool NoRespawn;
    /// <summary>True once the pack has spawned at least once (gates NoRespawn packs).</summary>
    public bool Spawned;
    public readonly List<int> AliveIds = new();
    public float RespawnAt;
}

/// <summary>An openable chest (hub sanctum): first opener pops the lid and the starter
/// gear inside drops on the ground for whoever grabs it.</summary>
public class ServerChest
{
    public int Id;
    public Vector2 Position;
    public float Height;
    public bool Opened;
}

/// <summary>A player's persistent minion (skeleton archer). Follows its summoner (or a
/// rally point), shoots arrows at nearby enemies, takes damage, dies, and freely
/// respawns near the summoner after the skill's respawn time.</summary>
public class ServerSummon
{
    public int Id;
    public int OwnerId;
    public string SkillId;
    public Vector2 Position;
    public float Height;
    public float Health;
    public float MaxHealth;
    public float Damage;
    public float AttackReadyAt;
    /// <summary>Maximum mana this minion RESERVES on its summoner while it exists
    /// (captured at summon time; survives free respawns; released on dismissal).</summary>
    public float ManaReserved;
    /// <summary>Melee summons close to arm's reach and swing; ranged ones hold and shoot.</summary>
    public bool Melee;
    /// <summary>Attack reach and swing time, set at spawn from the summon's profile.</summary>
    public float Reach = AttackRange;
    public float SwingTime = AttackCooldown;
    public const float Radius = 0.3f;
    public const float AttackRange = 6.5f;
    public const float AggroRange = 7.5f;
    public const float AttackCooldown = 1.4f;
    public const float MoveSpeed = 3.6f;
    public bool Dead => Health <= 0;
}

/// <summary>A friendly, stationary NPC (the test merchant). Not a combat entity — enemies
/// ignore it entirely; players interact with the pickup key when in range.</summary>
public class ServerNpc
{
    public int Id;
    /// <summary>NpcDefinition id ("merchant").</summary>
    public string TypeId;
    public Vector2 Position;
    public float Height;
}
