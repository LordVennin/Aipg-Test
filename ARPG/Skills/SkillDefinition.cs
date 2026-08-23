using ARPG.Items;

namespace ARPG.Skills;

/// <summary>Lightweight skill tag system. Scroll compatibility is tag-based, not name-based.</summary>
public static class SkillTags
{
    public const string Attack = "Attack";
    public const string Spell = "Spell";
    public const string Melee = "Melee";
    public const string Projectile = "Projectile";
    public const string Area = "Area";
    public const string Physical = "Physical";
    public const string Fire = "Fire";
    public const string Arcane = "Arcane";
    public const string Mace = "Mace";
    public const string Staff = "Staff";
    public const string Shield = "Shield";
}

/// <summary>How the server executes a skill. New archetypes extend the combat switch only.</summary>
public enum SkillArchetype
{
    MeleeStrike,   // hits enemies near the aimed point, within weapon/skill range
    MeleeSingle,   // hits ONE enemy in front of the caster (swipe + knockback capable)
    MeleeArea,     // hits all enemies in a radius around the caster
    Projectile,    // launches one or more projectiles toward the cursor
    AreaBurst,     // damages enemies in a radius around the aimed point (range-limited)
    ChainLightning, // instantly hits a target near the aim, then leaps between nearby enemies
    Summon,        // maintains persistent minions; managed from the Skill Menu, not the hotbar
}

/// <summary>
/// All damage types. Physical damage is split into Thrust, Blunt and Slash (mitigated by
/// armor); the six elemental types are each mitigated by their own resistance; Arcane is
/// unresisted.
/// </summary>
public enum DamageKind
{
    Thrust,
    Blunt,
    Slash,
    Fire,
    Cold,
    Lightning,
    Acid,
    Dark,
    Light,
    Arcane,
}

public static class DamageKinds
{
    public static bool IsPhysical(DamageKind kind) =>
        kind is DamageKind.Thrust or DamageKind.Blunt or DamageKind.Slash;
}

/// <summary>A skill type, loaded from Data/Skills/*.json. Learned skills reference this by Id.</summary>
public class SkillDefinition
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Description { get; set; }
    public List<string> Tags { get; set; } = new();
    public SkillArchetype Archetype { get; set; }
    public DamageKind DamageKind { get; set; } = DamageKind.Blunt;

    /// <summary>True: damage comes from equipped weapon phys damage. False: uses BaseDamage.</summary>
    public bool UsesWeaponDamage { get; set; }
    public float BaseDamage { get; set; }
    public float DamagePerLevel { get; set; }
    /// <summary>Multiplier applied to weapon damage (attack skills), grows with level.</summary>
    public float WeaponDamageMultiplier { get; set; } = 1f;
    public float WeaponDamageMultiplierPerLevel { get; set; } = 0.05f;

    /// <summary>Required weapon category, if any (e.g. Mace for Mace Strike, Staff optional for spells).</summary>
    public ItemCategory? RequiredWeapon { get; set; }

    /// <summary>True: the skill needs a shield equipped in either hand (e.g. Shield Bash).</summary>
    public bool RequiresShield { get; set; }

    /// <summary>Mana spent per use (0 = free, e.g. Basic Strike). Server-validated.</summary>
    public float ManaCost { get; set; }
    /// <summary>Extra flat mana cost per skill level past the first (summons get pricier).</summary>
    public float ManaCostPerLevel { get; set; }
    /// <summary>Extra cost as a fraction of the caster's MAX mana (summons: 0.05 = 5%).</summary>
    public float ManaCostPctMax { get; set; }

    // ------------------------------------------------------------------ summons
    /// <summary>Base number of minions this skill can maintain (gear can add more).</summary>
    public int SummonLimit { get; set; }
    public float SummonHealth { get; set; } = 30f;
    public float SummonHealthPerLevel { get; set; } = 12f;
    public float SummonDamage { get; set; } = 6f;
    public float SummonDamagePerLevel { get; set; } = 2.5f;
    /// <summary>Seconds after a minion dies before it freely respawns near the summoner.</summary>
    public float SummonRespawnTime { get; set; } = 6f;
    /// <summary>True for melee minions (skeleton warriors): they close to arm's reach and
    /// swing instead of holding position and shooting.</summary>
    public bool SummonMelee { get; set; }

    public float Cooldown { get; set; } = 0.5f;
    public float Range { get; set; } = 1.5f;
    public float Radius { get; set; }
    public float RadiusPerLevel { get; set; }
    public float ProjectileSpeed { get; set; } = 10f;
    public int ProjectileCount { get; set; } = 1;

    /// <summary>Named procedural sprite for this skill's projectiles ("IceSpike");
    /// null falls back to the generic glowing orb.</summary>
    public string ProjectileSprite { get; set; }

    /// <summary>Tiles the target is pushed away from the caster on hit (melee skills).</summary>
    public float Knockback { get; set; }

    /// <summary>Seconds hit enemies are stunned (no movement, no attacks).</summary>
    public float StunDuration { get; set; }
    /// <summary>Stun BUILDUP points a hit adds (100 fills the meter and stuns for
    /// StunDuration). Buildup decays constantly, and each triggered stun grants the
    /// enemy a stacking 20% resistance — no chain-stun loops. 0 = never builds.</summary>
    public float StunBuildup { get; set; }

    /// <summary>Chance (0..1) that a hit actually applies StunDuration. Defaults to always.</summary>
    public float StunChance { get; set; } = 1f;

    /// <summary>Chance (0..1) that a hit slows the target's movement (0 = never).</summary>
    public float SlowChance { get; set; }
    /// <summary>How long the slow lasts, in seconds.</summary>
    public float SlowDuration { get; set; } = 2f;

    /// <summary>Global lockout after using ANY skill: no other skill can be used for this
    /// long (prevents dumping the whole hotbar in one frame). Seconds.</summary>
    public float UseTime { get; set; } = 0.35f;

    /// <summary>Hold-to-charge: the client charges up to 1s before releasing; charge
    /// scales damage, knockback and lunge distance.</summary>
    public bool Chargeable { get; set; }

    /// <summary>Seconds between the cast and the hit landing (slam wind-up). The server
    /// queues the strike; the client delays the impact visuals to match. 0 = instant.</summary>
    public float WindupTime { get; set; }

    // ------------------------------------------------------------------ ailments
    /// <summary>Base chance (0..1) a hit ignites: fire DoT scaling off the hit's damage.</summary>
    public float IgniteChance { get; set; }
    /// <summary>Multiplier on ignite DoT strength for this skill (1 = baseline).</summary>
    public float IgniteMagnitude { get; set; } = 1f;
    /// <summary>Base chance (0..1) a hit chills: builds chill magnitude from the hit's damage.</summary>
    public float ChillChance { get; set; }
    /// <summary>Multiplier on chill buildup for this skill (1 = baseline).</summary>
    public float ChillMagnitude { get; set; } = 1f;
    /// <summary>Base chance (0..1) a hit electrocutes: 6s of periodic freeze-in-place rolls.</summary>
    public float ElectrocuteChance { get; set; }
    /// <summary>Multiplier on poison DoT strength for this skill (1 = baseline).</summary>
    public float PoisonMagnitude { get; set; } = 1f;
    /// <summary>Multiplier on bleed DoT strength for this skill (1 = baseline).</summary>
    public float BleedMagnitude { get; set; } = 1f;

    /// <summary>Flat damage added per point of Armor on equipped SHIELDS (Shield Bash:
    /// heavier shields hit harder). 0 for skills that don't scale with shields.</summary>
    public float ShieldArmorScaling { get; set; }

    /// <summary>Tiles the caster dashes toward the aim on use (Shield Bash's forward scoot).
    /// The dash is client-predicted like normal movement; the server grants brief
    /// invulnerability so ramming into an enemy doesn't hurt.</summary>
    public float LungeDistance { get; set; }

    /// <summary>"Attack" skills scale with attack speed; "Spell" with cast speed.</summary>
    public bool IsAttack => Tags.Contains(SkillTags.Attack);

    public bool HasTag(string tag) => Tags.Contains(tag);
}

/// <summary>One numeric effect of a Skill Scroll: additive and/or multiplicative change.</summary>
public class ScrollEffect
{
    public ScrollStat Stat { get; set; }
    public float Add { get; set; }
    public float Mult { get; set; } = 1f;
}

public enum ScrollStat
{
    DamageMultiplier,
    AddedProjectiles,
    AreaMultiplier,
    SpeedMultiplier,      // attack/cast speed multiplier
    CooldownMultiplier,
    ProjectileSpeedMultiplier,
    RangeMultiplier,
    IgniteChance,         // 0..1 chance to ignite (fire damage over time)
    PoisonChance,         // 0..1 chance a melee hit poisons (phys+dark+acid DoT)
    BleedChance,          // 0..1 chance a melee hit bleeds (physical DoT)
    ShatterShards,        // cold projectiles burst into this many shards behind the target
    FirePatch,            // fire projectiles leave a burning ground circle on hit
}

/// <summary>
/// A Skill Scroll definition (Data/SkillScrolls/*.json). Scrolls attach to learned skills through
/// the Skill Menu and alter the skill's computed stats. Compatibility is by required tag.
/// </summary>
public class ScrollDefinition
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Description { get; set; }
    /// <summary>The skill must have this tag for the scroll to attach ("Projectile", "Area", ...).</summary>
    public string RequiredTag { get; set; }
    public List<ScrollEffect> Effects { get; set; } = new();

    public bool CompatibleWith(SkillDefinition skill) =>
        string.IsNullOrEmpty(RequiredTag) || skill.HasTag(RequiredTag);
}
