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

    public float Cooldown { get; set; } = 0.5f;
    public float Range { get; set; } = 1.5f;
    public float Radius { get; set; }
    public float RadiusPerLevel { get; set; }
    public float ProjectileSpeed { get; set; } = 10f;
    public int ProjectileCount { get; set; } = 1;

    /// <summary>Tiles the target is pushed away from the caster on hit (melee skills).</summary>
    public float Knockback { get; set; }

    /// <summary>Seconds hit enemies are stunned (no movement, no attacks).</summary>
    public float StunDuration { get; set; }

    /// <summary>Chance (0..1) that a hit actually applies StunDuration. Defaults to always.</summary>
    public float StunChance { get; set; } = 1f;

    /// <summary>Flat damage added per point of Armor on equipped SHIELDS (Shield Bash:
    /// heavier shields hit harder). 0 for skills that don't scale with shields.</summary>
    public float ShieldArmorScaling { get; set; }

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
