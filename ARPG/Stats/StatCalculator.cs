using ARPG.Data;
using ARPG.Items;
using ARPG.Sim;

namespace ARPG.Stats;

/// <summary>Fully resolved character stats, computed in one place from all sources.</summary>
public struct ComputedStats
{
    public float MaxHealth;
    public float LifeRegeneration;       // health per second
    public float MovementSpeed;          // tiles per second
    public float Armor;
    public float FireResistance;         // percent, capped
    public float ColdResistance;
    public float LightningResistance;
    public float AcidResistance;
    public float DarkResistance;
    public float LightResistance;
    public float PhysicalDamageIncrease; // percent
    public float SpellDamageIncrease;    // percent
    public float AttackSpeedIncrease;    // percent
    public float CastSpeedIncrease;      // percent

    // Blocking (requires shields only in practice: BlockChance rolls solely on shields)
    public float BlockChance;            // percent chance to fully avoid one hit
    public float BlockCooldown;          // seconds between successful blocks
    public bool HasShield;               // a shield is equipped in either hand

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
    /// <summary>Which physical damage type this weapon's hits deal (maces = Blunt;
    /// future swords = Slash, spears = Thrust).</summary>
    public Skills.DamageKind PhysicalSubtype;

    // Flat added elemental damage on MELEE attacks (from melee-weapon "Added X Damage" rolls).
    public float AddedFire;
    public float AddedCold;
    public float AddedLightning;
    public float AddedAcid;
    public float AddedDark;
    public float AddedLight;

    // Flat added elemental damage on SPELLS (from caster-weapon "Added X Spell Damage" rolls).
    public float SpellAddedFire;
    public float SpellAddedCold;
    public float SpellAddedLightning;
    public float SpellAddedAcid;
    public float SpellAddedDark;
    public float SpellAddedLight;

    public const float ResistanceCap = 75f;
    public const float BlockChanceCap = 75f;

    /// <summary>Standard armor mitigation: armor / (armor + 60). Applied to physical hits
    /// (Thrust, Blunt and Slash alike).</summary>
    public readonly float PhysicalReduction => Armor / (Armor + 60f);

    public readonly float ResistanceFor(Skills.DamageKind kind) => kind switch
    {
        Skills.DamageKind.Fire => FireResistance,
        Skills.DamageKind.Cold => ColdResistance,
        Skills.DamageKind.Lightning => LightningResistance,
        Skills.DamageKind.Acid => AcidResistance,
        Skills.DamageKind.Dark => DarkResistance,
        Skills.DamageKind.Light => LightResistance,
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
    /// <summary>Base seconds between successful blocks, before BlockCooldownRecovery.</summary>
    public const float BaseBlockCooldown = 2f;

    public static ComputedStats Compute(GameData data, CharacterData character, StatCollection temporaryEffects = null)
    {
        // 1) Aggregate flat/percent contributions from all equipped items.
        var total = new StatCollection();
        ItemInstance weapon = null;
        bool hasShield = false;
        foreach (var (slot, item) in character.Equipment)
        {
            if (item == null) continue;
            total.AddAll(item.TotalStats(data));
            if (slot == EquipSlot.MainHand) weapon = item;
            if (item.GetBase(data)?.Category == ItemCategory.Shield) hasShield = true;
        }

        // 2) Temporary effects (buffs/debuffs) merge into the same pool.
        if (temporaryEffects != null) total.AddAll(temporaryEffects);

        var s = new ComputedStats
        {
            MaxHealth = BaseMaxHealth + HealthPerCharLevel * (character.Level - 1) + total.Get(StatType.MaxHealth),
            LifeRegeneration = total.Get(StatType.LifeRegeneration),
            MovementSpeed = BaseMoveSpeed * (1f + total.Get(StatType.MovementSpeed) / 100f),
            Armor = total.Get(StatType.Armor),
            FireResistance = MathF.Min(ComputedStats.ResistanceCap, total.Get(StatType.FireResistance)),
            ColdResistance = MathF.Min(ComputedStats.ResistanceCap, total.Get(StatType.ColdResistance)),
            LightningResistance = MathF.Min(ComputedStats.ResistanceCap, total.Get(StatType.LightningResistance)),
            AcidResistance = MathF.Min(ComputedStats.ResistanceCap, total.Get(StatType.AcidResistance)),
            DarkResistance = MathF.Min(ComputedStats.ResistanceCap, total.Get(StatType.DarkResistance)),
            LightResistance = MathF.Min(ComputedStats.ResistanceCap, total.Get(StatType.LightResistance)),
            AddedFire = total.Get(StatType.AddedFireDamage),
            AddedCold = total.Get(StatType.AddedColdDamage),
            AddedLightning = total.Get(StatType.AddedLightningDamage),
            AddedAcid = total.Get(StatType.AddedAcidDamage),
            AddedDark = total.Get(StatType.AddedDarkDamage),
            AddedLight = total.Get(StatType.AddedLightDamage),
            SpellAddedFire = total.Get(StatType.AddedFireSpellDamage),
            SpellAddedCold = total.Get(StatType.AddedColdSpellDamage),
            SpellAddedLightning = total.Get(StatType.AddedLightningSpellDamage),
            SpellAddedAcid = total.Get(StatType.AddedAcidSpellDamage),
            SpellAddedDark = total.Get(StatType.AddedDarkSpellDamage),
            SpellAddedLight = total.Get(StatType.AddedLightSpellDamage),
            PhysicalDamageIncrease = total.Get(StatType.PhysicalDamage),
            SpellDamageIncrease = total.Get(StatType.SpellDamage),
            AttackSpeedIncrease = total.Get(StatType.AttackSpeed),
            CastSpeedIncrease = total.Get(StatType.CastSpeed),
        };

        // Blocking: chance comes entirely from gear (shields and their modifiers); a block
        // avoids one hit completely, then waits out a cooldown that recovery stats shorten.
        s.HasShield = hasShield;
        s.BlockChance = MathF.Min(ComputedStats.BlockChanceCap, total.Get(StatType.BlockChance));
        s.BlockCooldown = MathF.Max(0.25f, BaseBlockCooldown / (1f + total.Get(StatType.BlockCooldownRecovery) / 100f));

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

        // Physical subtype by weapon category (unarmed and maces/staffs strike Blunt;
        // future categories map here: swords -> Slash, spears -> Thrust).
        s.PhysicalSubtype = s.WeaponCategory switch
        {
            _ => Skills.DamageKind.Blunt,
        };
        return s;
    }
}
