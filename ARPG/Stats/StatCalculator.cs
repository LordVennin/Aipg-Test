using ARPG.Data;
using ARPG.Items;
using ARPG.Sim;

namespace ARPG.Stats;

/// <summary>Fully resolved character stats, computed in one place from all sources.</summary>
public struct ComputedStats
{
    public float MaxHealth;
    public float MovementSpeed;          // tiles per second
    public float Armor;
    public float FireResistance;         // percent, capped
    public float ColdResistance;
    public float LightningResistance;
    public float PhysicalDamageIncrease; // percent
    public float SpellDamageIncrease;    // percent
    public float AttackSpeedIncrease;    // percent
    public float CastSpeedIncrease;      // percent

    // Dodge (base values from Data/Config/dodge.json, scaled by Dodge* stats)
    public float DodgeDistance;          // tiles
    public float DodgeDuration;          // seconds the dash lasts
    public float DodgeCooldown;          // seconds between dodges
    public float DodgeInvulnerability;   // i-frame duration in seconds

    // From the equipped weapon (or unarmed defaults)
    public float WeaponMinDamage;
    public float WeaponMaxDamage;
    public float WeaponAttackSpeed;      // attacks per second
    public float WeaponRange;            // tiles
    public ItemCategory? WeaponCategory;

    public const float ResistanceCap = 75f;

    /// <summary>Standard armor mitigation: armor / (armor + 60). Applied to physical hits.</summary>
    public readonly float PhysicalReduction => Armor / (Armor + 60f);

    public readonly float ResistanceFor(Skills.DamageKind kind) => kind switch
    {
        Skills.DamageKind.Fire => FireResistance,
        Skills.DamageKind.Cold => ColdResistance,
        Skills.DamageKind.Lightning => LightningResistance,
        _ => 0f,
    };
}

/// <summary>
/// The single place where final character stats are combined:
/// base character stats + equipment base stats + item modifiers + temporary effects.
/// No gameplay class recalculates stats on its own.
/// </summary>
public static class StatCalculator
{
    public const float BaseMoveSpeed = 4.2f;
    public const float BaseMaxHealth = 60f;
    public const float HealthPerCharLevel = 8f;
    public const float UnarmedMinDamage = 2f;
    public const float UnarmedMaxDamage = 4f;
    public const float UnarmedAttackSpeed = 1.2f;
    public const float UnarmedRange = 1.2f;

    public static ComputedStats Compute(GameData data, CharacterData character, StatCollection temporaryEffects = null)
    {
        // 1) Aggregate flat/percent contributions from all equipped items.
        var total = new StatCollection();
        ItemInstance weapon = null;
        foreach (var (slot, item) in character.Equipment)
        {
            if (item == null) continue;
            total.AddAll(item.TotalStats(data));
            if (slot == EquipSlot.MainHand) weapon = item;
        }

        // 2) Temporary effects (buffs/debuffs) merge into the same pool.
        if (temporaryEffects != null) total.AddAll(temporaryEffects);

        var s = new ComputedStats
        {
            MaxHealth = BaseMaxHealth + HealthPerCharLevel * (character.Level - 1) + total.Get(StatType.MaxHealth),
            MovementSpeed = BaseMoveSpeed * (1f + total.Get(StatType.MovementSpeed) / 100f),
            Armor = total.Get(StatType.Armor),
            FireResistance = MathF.Min(ComputedStats.ResistanceCap, total.Get(StatType.FireResistance)),
            ColdResistance = MathF.Min(ComputedStats.ResistanceCap, total.Get(StatType.ColdResistance)),
            LightningResistance = MathF.Min(ComputedStats.ResistanceCap, total.Get(StatType.LightningResistance)),
            PhysicalDamageIncrease = total.Get(StatType.PhysicalDamage),
            SpellDamageIncrease = total.Get(StatType.SpellDamage),
            AttackSpeedIncrease = total.Get(StatType.AttackSpeed),
            CastSpeedIncrease = total.Get(StatType.CastSpeed),
        };

        // Dodge: config base values scaled by percent-increased stats, so items and
        // modifiers can grant e.g. "+20% Dodge Distance" or faster recovery.
        var dodge = data.Dodge;
        s.DodgeDistance = dodge.Distance * (1f + total.Get(StatType.DodgeDistance) / 100f);
        s.DodgeDuration = MathF.Max(0.05f, dodge.Duration * (1f + total.Get(StatType.DodgeDuration) / 100f));
        s.DodgeCooldown = MathF.Max(0.2f, dodge.Cooldown / (1f + total.Get(StatType.DodgeCooldownRecovery) / 100f));
        s.DodgeInvulnerability = dodge.InvulnerabilityDuration * (1f + total.Get(StatType.DodgeInvulnerability) / 100f);

        // 3) Weapon-local numbers (base stats + local added phys rolled on the weapon itself).
        if (weapon != null)
        {
            var w = weapon.TotalStats(data);
            float added = w.Get(StatType.AddedPhysicalDamage);
            s.WeaponMinDamage = w.Get(StatType.MinPhysicalDamage) + added;
            s.WeaponMaxDamage = w.Get(StatType.MaxPhysicalDamage) + added;
            s.WeaponAttackSpeed = w.Get(StatType.BaseAttackSpeed);
            s.WeaponRange = w.Get(StatType.WeaponRange);
            s.WeaponCategory = weapon.GetBase(data).Category;
        }

        if (s.WeaponMinDamage <= 0) { s.WeaponMinDamage = UnarmedMinDamage; s.WeaponMaxDamage = UnarmedMaxDamage; }
        if (s.WeaponAttackSpeed <= 0) s.WeaponAttackSpeed = UnarmedAttackSpeed;
        if (s.WeaponRange <= 0) s.WeaponRange = UnarmedRange;
        return s;
    }
}
