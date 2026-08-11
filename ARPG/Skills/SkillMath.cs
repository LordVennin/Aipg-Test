using ARPG.Data;
using ARPG.Stats;

namespace ARPG.Skills;

/// <summary>One typed portion of a hit (e.g. "+3-5 Fire" from a Searing weapon roll).</summary>
public struct DamageComponent
{
    public DamageKind Kind;
    public float Min;
    public float Max;
}

/// <summary>Fully resolved runtime numbers for one use/display of a skill.</summary>
public struct EffectiveSkillStats
{
    public float MinDamage;
    public float MaxDamage;
    /// <summary>Added elemental damage on attacks (empty for spells). Each component is
    /// mitigated by its own resistance when it hits a player.</summary>
    public List<DamageComponent> Added;
    public float Cooldown;        // seconds between uses (already includes attack/cast speed)
    public float Range;
    public float Radius;
    public float ProjectileSpeed;
    public int ProjectileCount;
    public float IgniteChance;    // 0..1
    public DamageKind DamageKind;

    public float AverageDamage => (MinDamage + MaxDamage) * 0.5f;
}

/// <summary>
/// Central skill number computation: definition + skill level + attached scrolls +
/// player stats + weapon. Both server (authoritative combat) and client (UI display)
/// call this same code, so displayed numbers match simulated ones.
/// </summary>
public static class SkillMath
{
    public const int MaxSkillLevel = 10;

    /// <summary>Skill XP needed to advance from `level` to the next level.</summary>
    public static float XpToNextLevel(int level) => 60f * level;

    /// <summary>
    /// Scroll slots unlocked at a given skill level. Data-driven via Data/Config/scroll_slots.json
    /// (a map of "skill level reached" -> "total slots"), NOT hard-coded in UI code.
    /// </summary>
    public static int ScrollSlotsAtLevel(GameData data, int skillLevel)
    {
        int slots = 0;
        foreach (var (levelReq, slotCount) in data.ScrollSlotProgression)
            if (skillLevel >= levelReq && slotCount > slots)
                slots = slotCount;
        return slots;
    }

    public static int MaxScrollSlots(GameData data) =>
        data.ScrollSlotProgression.Count == 0 ? 0 : data.ScrollSlotProgression.Values.Max();

    /// <summary>
    /// The impact point of a caster-relative melee strike: always projected along the aim
    /// direction FROM THE CASTER, clamped into [minReach, range]. The server computes this
    /// point for hit detection and broadcasts it, so client visuals use the exact same point.
    /// </summary>
    public static System.Numerics.Vector2 MeleeImpactPoint(
        System.Numerics.Vector2 caster, System.Numerics.Vector2 aim,
        System.Numerics.Vector2 facing, float range)
    {
        var toAim = aim - caster;
        float len = toAim.Length();
        var dir = len > 0.001f ? toAim / len : facing;
        if (dir.LengthSquared() < 0.001f) dir = new System.Numerics.Vector2(1, 0);
        float minReach = MathF.Min(0.6f, range);
        float dist = Math.Clamp(len, minReach, range);
        return caster + dir * dist;
    }

    public static EffectiveSkillStats Compute(
        GameData data,
        SkillDefinition def,
        int level,
        IEnumerable<ScrollDefinition> scrolls,
        in ComputedStats playerStats)
    {
        var s = new EffectiveSkillStats
        {
            DamageKind = def.DamageKind,
            Range = def.Range,
            Radius = def.Radius + def.RadiusPerLevel * (level - 1),
            ProjectileSpeed = def.ProjectileSpeed,
            ProjectileCount = def.ProjectileCount,
            IgniteChance = 0f,
        };

        // Base damage: weapon-driven for attacks, skill-progression-driven for spells.
        if (def.UsesWeaponDamage)
        {
            float mult = def.WeaponDamageMultiplier + def.WeaponDamageMultiplierPerLevel * (level - 1);
            s.MinDamage = playerStats.WeaponMinDamage * mult;
            s.MaxDamage = playerStats.WeaponMaxDamage * mult;
            // Attacks deal the weapon's physical subtype (Blunt for maces). Added ATTACK
            // damage rolls apply to MELEE skills only, as separately-typed components.
            s.DamageKind = playerStats.PhysicalSubtype;
            if (def.HasTag(SkillTags.Melee))
            {
                var added = new List<DamageComponent>();
                void AddComp(DamageKind kind, float value)
                {
                    if (value > 0)
                        added.Add(new DamageComponent { Kind = kind, Min = value * 0.8f, Max = value * 1.2f });
                }
                AddComp(DamageKind.Fire, playerStats.AddedFire);
                AddComp(DamageKind.Cold, playerStats.AddedCold);
                AddComp(DamageKind.Lightning, playerStats.AddedLightning);
                AddComp(DamageKind.Acid, playerStats.AddedAcid);
                AddComp(DamageKind.Dark, playerStats.AddedDark);
                AddComp(DamageKind.Light, playerStats.AddedLight);
                if (added.Count > 0) s.Added = added;
            }
        }
        else
        {
            float baseDmg = def.BaseDamage + def.DamagePerLevel * (level - 1);
            s.MinDamage = baseDmg * 0.85f;
            s.MaxDamage = baseDmg * 1.15f;

            // Added SPELL damage rolls (caster weapons) attach typed components to spells,
            // scaled by the same Spell Damage % as the spell's base damage.
            float spellScale = 1f + playerStats.SpellDamageIncrease / 100f;
            var added = new List<DamageComponent>();
            void AddComp(DamageKind kind, float value)
            {
                if (value > 0)
                    added.Add(new DamageComponent
                    {
                        Kind = kind,
                        Min = value * 0.8f * spellScale,
                        Max = value * 1.2f * spellScale,
                    });
            }
            AddComp(DamageKind.Fire, playerStats.SpellAddedFire);
            AddComp(DamageKind.Cold, playerStats.SpellAddedCold);
            AddComp(DamageKind.Lightning, playerStats.SpellAddedLightning);
            AddComp(DamageKind.Acid, playerStats.SpellAddedAcid);
            AddComp(DamageKind.Dark, playerStats.SpellAddedDark);
            AddComp(DamageKind.Light, playerStats.SpellAddedLight);
            if (added.Count > 0) s.Added = added;
        }

        // Global damage scaling from character stats.
        float damageInc = def.IsAttack ? playerStats.PhysicalDamageIncrease : playerStats.SpellDamageIncrease;
        s.MinDamage *= 1f + damageInc / 100f;
        s.MaxDamage *= 1f + damageInc / 100f;

        // Use speed: attacks scale with weapon speed + attack speed, spells with cast speed.
        float useTime;
        if (def.IsAttack)
        {
            float aps = MathF.Max(0.1f, playerStats.WeaponAttackSpeed * (1f + playerStats.AttackSpeedIncrease / 100f));
            useTime = MathF.Max(def.Cooldown, 1f / aps);
            if (def.RequiredWeapon == null || playerStats.WeaponCategory == def.RequiredWeapon)
                s.Range = MathF.Max(s.Range, playerStats.WeaponRange);
        }
        else
        {
            useTime = def.Cooldown / MathF.Max(0.1f, 1f + playerStats.CastSpeedIncrease / 100f);
        }

        // Apply attached Skill Scrolls.
        float speedMult = 1f, cooldownMult = 1f, componentMult = 1f;
        foreach (var scroll in scrolls)
        {
            foreach (var fx in scroll.Effects)
            {
                switch (fx.Stat)
                {
                    case ScrollStat.DamageMultiplier:
                        s.MinDamage = s.MinDamage * fx.Mult + fx.Add;
                        s.MaxDamage = s.MaxDamage * fx.Mult + fx.Add;
                        componentMult *= fx.Mult;
                        break;
                    case ScrollStat.AddedProjectiles:
                        s.ProjectileCount += (int)fx.Add;
                        break;
                    case ScrollStat.AreaMultiplier:
                        s.Radius = s.Radius * fx.Mult + fx.Add;
                        break;
                    case ScrollStat.SpeedMultiplier:
                        speedMult *= fx.Mult;
                        break;
                    case ScrollStat.CooldownMultiplier:
                        cooldownMult *= fx.Mult;
                        break;
                    case ScrollStat.ProjectileSpeedMultiplier:
                        s.ProjectileSpeed = s.ProjectileSpeed * fx.Mult + fx.Add;
                        break;
                    case ScrollStat.RangeMultiplier:
                        s.Range = s.Range * fx.Mult + fx.Add;
                        break;
                    case ScrollStat.IgniteChance:
                        s.IgniteChance = MathF.Min(1f, s.IgniteChance + fx.Add);
                        break;
                }
            }
        }

        if (s.Added != null && MathF.Abs(componentMult - 1f) > 0.001f)
            for (int i = 0; i < s.Added.Count; i++)
                s.Added[i] = new DamageComponent
                {
                    Kind = s.Added[i].Kind,
                    Min = s.Added[i].Min * componentMult,
                    Max = s.Added[i].Max * componentMult,
                };

        s.Cooldown = MathF.Max(0.1f, useTime / MathF.Max(0.1f, speedMult) * cooldownMult);
        return s;
    }
}
