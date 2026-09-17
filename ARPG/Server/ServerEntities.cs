using System.Linq;
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

    /// <summary>Skill XP accrued since the last CharacterChanged broadcast — flushed on
    /// a slow clock so per-hit XP grants never flood the wire with character JSON.</summary>
    public bool SkillXpDirty;

    // Dodge: cooldown and invulnerability are server-authoritative.
    public float NextDodgeAt;
    public float InvulnerableUntil;
    /// <summary>While the dash lasts, the body smashes breakables it passes through.</summary>
    public float DodgeUntil;
    /// <summary>Cinderwrap: next time a melee hit may scorch the ground (throttle).</summary>
    public float CinderNextAt;
    /// <summary>Client position updates are ignored until this time (set on map
    /// transitions, while the client's in-flight states still carry old-map coords).</summary>
    public float IgnoreStateUntil;

    /// <summary>Defense-run build currency (DefenseBalance): granted on entering the
    /// arena, earned from kills and cleared waves, worthless anywhere else — it is
    /// deliberately NOT on CharacterData, so it can never leave the run.</summary>
    public int Supplies;

    /// <summary>Items sold to merchants THIS SESSION (most recent last): the buy-back
    /// tab's stock, offered back at exactly what they fetched. Slot = list index.</summary>
    public readonly List<ShopEntry> Buyback = new();

    // Campaign revive channel (teammates hold the interact key beside this corpse).
    // The SERVER does the timekeeping — pulses only mark that someone is channeling.
    public float ReviveProgress;
    public float LastRevivePulseAt = -10f;

    /// <summary>Blocking goes on cooldown after each successful block (server-authoritative).</summary>
    public float NextBlockReadyAt;

    // Flask drinking in progress: restore-over-time windows fed by the equipped
    // flask ITEMS' stats (charges live on the items themselves).
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
    Brutish = 1,       // much more life and damage
    Swift = 2,         // faster movement and attacks
    Warded = 4,        // elemental resistance shell + extra life
    Boss = 8,          // miniboss: slam attack, stun resistance, guaranteed loot
    Vampiric = 16,     // heals from the damage it deals
    Thorny = 32,       // melee blows against it cut the attacker back
    Regenerating = 64, // knits itself back together when left alone
    Minion = 128,      // a RARE leader's follower: tougher, no affix of its own
}

/// <summary>
/// Elite TIERS, Diablo-style: a MAGIC monster carries exactly one affix (blue), a
/// RARE one two or three plus a name of its own (gold) and a pack of tougher minions.
/// Shared by server (rolling, scaling) and client (names, colours, the hover panel's
/// affix descriptions) so both agree on what a bitmask means.
/// </summary>
public static class EliteAffixInfo
{
    public const EliteAffix RollableMask = EliteAffix.Brutish | EliteAffix.Swift | EliteAffix.Warded |
                                           EliteAffix.Vampiric | EliteAffix.Thorny | EliteAffix.Regenerating;
    public static readonly EliteAffix[] Rollable =
    {
        EliteAffix.Brutish, EliteAffix.Swift, EliteAffix.Warded,
        EliteAffix.Vampiric, EliteAffix.Thorny, EliteAffix.Regenerating,
    };

    /// <summary>Number of rollable (non-boss, non-minion) affixes in the mask.</summary>
    public static int AffixCount(EliteAffix a) => System.Numerics.BitOperations.PopCount((uint)(a & RollableMask));
    public static bool IsBoss(EliteAffix a) => (a & EliteAffix.Boss) != 0;
    public static bool IsMinion(EliteAffix a) => (a & EliteAffix.Minion) != 0;
    /// <summary>Two or more affixes: a named RARE.</summary>
    public static bool IsRare(EliteAffix a) => !IsBoss(a) && AffixCount(a) >= 2;
    /// <summary>Exactly one affix: a MAGIC monster.</summary>
    public static bool IsMagic(EliteAffix a) => !IsBoss(a) && AffixCount(a) == 1;

    public static string Name(EliteAffix a) => a switch
    {
        EliteAffix.Brutish => "Brutish",
        EliteAffix.Swift => "Swift",
        EliteAffix.Warded => "Warded",
        EliteAffix.Vampiric => "Vampiric",
        EliteAffix.Thorny => "Thorny",
        EliteAffix.Regenerating => "Regenerating",
        EliteAffix.Boss => "Boss",
        EliteAffix.Minion => "Minion",
        _ => "",
    };

    /// <summary>What the affix does, for the hover panel.</summary>
    public static string Describe(EliteAffix a) => a switch
    {
        EliteAffix.Brutish => "2.5x life, 1.5x damage",
        EliteAffix.Swift => "moves 35% faster, attacks 30% sooner",
        EliteAffix.Warded => "+40% to all resistances",
        EliteAffix.Vampiric => "heals 30% of the damage it deals",
        EliteAffix.Thorny => "melee hits cut the attacker for 15%",
        EliteAffix.Regenerating => "regrows 3% life a second when unhurt for 2s",
        EliteAffix.Boss => "slams, shrugs off stuns, always drops loot",
        EliteAffix.Minion => "a rare's follower: +50% life",
        _ => "",
    };

    /// <summary>The epithet a rare earns from its FIRST affix ("Gorrak the Barbed").</summary>
    public static string Epithet(EliteAffix a) => a switch
    {
        EliteAffix.Brutish => "the Brute",
        EliteAffix.Swift => "the Fleet",
        EliteAffix.Warded => "the Shielded",
        EliteAffix.Vampiric => "the Thirsting",
        EliteAffix.Thorny => "the Barbed",
        EliteAffix.Regenerating => "the Unending",
        _ => "the Cursed",
    };

    private static readonly string[] NameHeads =
        { "Gor", "Mal", "Vex", "Thar", "Ulg", "Kra", "Zul", "Bram", "Hex", "Mor", "Skar", "Dru" };
    private static readonly string[] NameTails =
        { "rak", "goth", "mir", "vash", "dun", "zek", "gul", "thar", "mok", "ath" };

    /// <summary>A rare's name: two syllables plus the epithet of its first affix.</summary>
    public static string RareName(Random rng, EliteAffix affixes)
    {
        var first = EliteAffix.None;
        foreach (var a in Rollable) if ((affixes & a) != 0) { first = a; break; }
        return NameHeads[rng.Next(NameHeads.Length)] + NameTails[rng.Next(NameTails.Length)] + " " + Epithet(first);
    }

    /// <summary>The pack-leader roll: 50% plain, 32% magic (one affix), 18% rare (two or
    /// three distinct affixes and a name). Returns None with an empty name for plain.</summary>
    public static EliteAffix RollPackLeader(Random rng, out string name)
    {
        name = "";
        int roll = rng.Next(100);
        if (roll < 50) return EliteAffix.None;
        if (roll < 82) return Rollable[rng.Next(Rollable.Length)];
        int count = rng.Next(100) < 35 ? 3 : 2;
        var pool = Rollable.OrderBy(_ => rng.Next()).Take(count);
        var affixes = EliteAffix.None;
        foreach (var a in pool) affixes |= a;
        name = RareName(rng, affixes);
        return affixes;
    }
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
    /// <summary>World direction of the blow currently being applied (set around the
    /// damage call by HitEnemy); rides the damage event for directional blood.</summary>
    public Vector2 LastHitDir;
    /// <summary>While the server clock is below this, the enemy moves at reduced speed.</summary>
    public float SlowedUntil;
    /// <summary>Standing in a Ground Slam tremor: a lighter slow (TremorSlow fraction)
    /// refreshed every tick the enemy stays inside the shaking circle.</summary>
    public float TremorSlowUntil;
    public float TremorSlow;

    // Elite/pack state. Multipliers default to 1 so normal enemies are unaffected.
    public EliteAffix Affixes;
    /// <summary>A RARE's own name ("Gorrak the Barbed"); empty for everything else.</summary>
    public string EliteName = "";
    /// <summary>Spawned buried: clients keep it unseen until a player nears, then play
    /// the rise from the earth. Cosmetic — the server simulates it like any other.</summary>
    public bool Buried;
    /// <summary>A Last Stand hunter: it has the party's scent and never loses it —
    /// aggro range and leash don't apply, it closes on the nearest player from anywhere.</summary>
    public bool Hunting;
    /// <summary>Server time of the last damage taken (Regenerating waits 2s after it).</summary>
    public float LastDamagedAt = -100f;
    /// <summary>Regenerating: seconds since the last health sync while regrowing.</summary>
    public float RegenAccum;
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
    // Ranged telegraphed AoE cast (casters): the circle LOCKS at cast start.
    public float CastResolveAt;
    public Vector2 CastTarget;
    public float CastReadyAt;

    // Damage-over-time ailments. Ticks apply every frame but damage events/health
    // updates are batched via accumulators to avoid packet spam.
    public float BurnDps;
    public float BurnTimeLeft;
    public float BurnAccum;
    public float BurnEmitTimer;
    /// <summary>One applied bleed/poison instance. Each (owner, skill) source keeps its
    /// own stacks up to that skill's cap — different sources always coexist.</summary>
    public class DotStack
    {
        public float Dps;
        public float TimeLeft;
        public int OwnerId;
        public string SkillId;
    }
    public readonly List<DotStack> PoisonStacks = new();
    public readonly List<DotStack> BleedStacks = new();
    /// <summary>Combined poison tick rate across every active stack (test/UI view).</summary>
    public float PoisonDps => PoisonStacks.Sum(s => s.Dps);
    public float PoisonTimeLeft => PoisonStacks.Count == 0 ? 0f : PoisonStacks.Max(s => s.TimeLeft);
    public float BleedDps => BleedStacks.Sum(s => s.Dps);
    public float BleedTimeLeft => BleedStacks.Count == 0 ? 0f : BleedStacks.Max(s => s.TimeLeft);
    public float PoisonAccum;
    public float PoisonEmitTimer;
    public float BleedAccum;
    public float BleedEmitTimer;

    // Chill/freeze: magnitude 0..cap builds from chilling hits and decays constantly;
    // at the cap each further hit can freeze outright.
    public float ChillMagnitude;
    public float FrozenUntil;

    // Stun: buildup 0..100 from Stun-building hits (Shield Bash mostly), decaying
    // constantly; at 100 the enemy is briefly stunned, the meter resets, and it gains
    // a stacking 20% resistance to further buildup — chain-stunning falls off fast.
    public float StunBuildup;
    public int StunResistStacks;

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
    /// <summary>Piercing projectiles fly on through every body they strike; each enemy
    /// takes the hit once (tracked here).</summary>
    public bool Pierce;
    public HashSet<int> HitIds;
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
    /// <summary>A rare leader's name (empty otherwise); its pack mates spawn as Minions.</summary>
    public string LeaderName = "";
    /// <summary>Rolled once when the pack first spawns: an all-undead pack may lie
    /// buried (see ServerWorld.BuriedPackChance). Null = not rolled yet.</summary>
    public bool? Buried;
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

/// <summary>A dead enemy's remains: an authoritative world record, not just a visual —
/// future skills (raise dead, corpse explosion, devour) target these. Replicated to
/// every client; cleared on map transitions; capped oldest-first.</summary>
public class ServerCorpse
{
    public int Id;
    /// <summary>The EnemyDefinition id this corpse belonged to (drives the body sprite).</summary>
    public string TypeId;
    /// <summary>The fallen enemy's id — the body keeps the same procedural variant it
    /// wore alive (packs of one type pick per-individual looks by id).</summary>
    public int SourceId;
    public Vector2 Position;
    public float Height;
    public float DiedAt;
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
    /// <summary>Deployed mercenaries hold THIS spot instead of following their owner
    /// (skeleton minions leave it null). Set at deployment, never moved.</summary>
    public Vector2? GuardPoint;
    /// <summary>Attack reach and swing time, set at spawn from the summon's profile.</summary>
    public float Reach = AttackRange;
    public float SwingTime = AttackCooldown;
    /// <summary>Server time this minion crumbles on its own (0 = never): Gravewake thralls.</summary>
    public float ExpiresAt;
    public const float Radius = 0.3f;
    public const float AttackRange = 6.5f;
    public const float AggroRange = 7.5f;
    public const float AttackCooldown = 1.4f;
    public const float MoveSpeed = 3.6f;
    public bool Dead => Health <= 0;
    /// <summary>Equipped-pet companions (SkillId "pet_*"): pure flavor bodies —
    /// never targeted, never damaged, never fighting.</summary>
    public bool IsPet => SkillId != null && SkillId.StartsWith("pet_");
}

/// <summary>A defense-map structure: the wagon, the workbench, or a player-built
/// turret/barrier. Stationary, server-authoritative, replicated as a single entity
/// type. Turrets shoot; barriers just stand in the way; enemies chew through them
/// with plain DPS ticks (no telegraphs — structures don't dodge).</summary>
public class ServerStructure
{
    public int Id;
    public World.StructureKind Kind;
    public Vector2 Position;
    public float Height;
    public float Health;
    public float MaxHealth;
    /// <summary>The player who built it (-1 for server-placed wagon/workbench).</summary>
    public int OwnerId = -1;
    /// <summary>Placement rotation (0 west / 1 north / 2 east / 3 south): a turret's
    /// fire-cone facing, a barrier's wall axis. Chosen at placement, never changes.</summary>
    public byte Rotation;
    /// <summary>Turrets: next allowed shot time (server clock).</summary>
    public float NextShotAt;
    /// <summary>Collision footprint radius (barriers block enemy movement with it).</summary>
    public float Radius = 0.45f;
    public bool Destroyed => Health <= 0;
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
