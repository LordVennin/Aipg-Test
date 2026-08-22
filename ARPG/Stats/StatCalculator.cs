using ARPG.Data;
using ARPG.Items;
using ARPG.Sim;

namespace ARPG.Stats;

/// <summary>Fully resolved character stats, computed in one place from all sources.</summary>
public struct ComputedStats
{
    // Primary attributes (base + gear + passives + effects), pre-derived.
    public float Strength;
    public float Dexterity;
    public float Intelligence;
    public float MaxHealth;
    public float LifeRegeneration;       // health per second
    public float MaxMana;                // level-based pool + flat modifiers
    public float ManaRegeneration;       // mana per second (level-based, scaled by % mods)
    public float MovementSpeed;          // tiles per second
    public float Armor;
    /// <summary>Aggregated DEX defense rating from ALL equipped pieces + dexterity.</summary>
    public float DeflectionRating;
    /// <summary>Initial Deflection Chance derived from the rating (level-scaled, capped);
    /// descending layers are generated from it per incoming Attack.</summary>
    public float DeflectionChance;
    /// <summary>Maximum Energy Shield (flat gear ES scaled by Intelligence).</summary>
    public float MaxEnergyShield;
    public float FireResistance;         // percent, capped
    public float ColdResistance;
    public float LightningResistance;
    public float AcidResistance;
    public float DarkResistance;
    public float LightResistance;
    public float ArcaneResistance;
    public float PhysicalDamageIncrease; // percent
    public float SpellDamageIncrease;    // percent
    public float AttackSpeedIncrease;    // percent
    public float CastSpeedIncrease;      // percent
    public float CritChance;             // percent chance a skill hit crits (base 5)
    public float CritDamage;             // crit damage multiplier percent (base 150)

    // Percent-increased ailment magnitudes (0 = baseline 100%)
    public float IgniteMagnitudeIncrease;
    public float ChillMagnitudeIncrease;
    public float PoisonMagnitudeIncrease;
    public float BleedMagnitudeIncrease;

    // Minions
    public float SummonDamageIncrease;   // percent
    public float SummonHealthIncrease;   // percent
    public int SummonLimitBonus;         // flat extra minions
    public float LightRadiusIncrease;    // percent — personal torchglow in dark zones

    /// <summary>Equipped items whose requirements are no longer met — still worn but
    /// contributing nothing (the UI paints them red). Null when constructed bare
    /// (tests/defaults), so consumers must null-check.</summary>
    public HashSet<Guid> InactiveItems;

    /// <summary>Base personal light radius in screen pixels (dark-zone torchglow).</summary>
    public const float BaseLightRadius = 235f;
    public float LightRadius => BaseLightRadius * (1f + LightRadiusIncrease / 100f);

    // Blocking (requires shields only in practice: BlockChance rolls solely on shields)
    public float BlockChance;            // percent chance to fully avoid one hit
    public float BlockCooldown;          // seconds between successful blocks
    public bool HasShield;               // a shield is equipped in either hand
    /// <summary>Total Armor on equipped shields only (fuels Shield Bash damage scaling).</summary>
    public float ShieldArmor;

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
    public float AddedArcane;

    // Flat added elemental damage on SPELLS (from caster-weapon "Added X Spell Damage" rolls).
    public float SpellAddedFire;
    public float SpellAddedCold;
    public float SpellAddedLightning;
    public float SpellAddedAcid;
    public float SpellAddedDark;
    public float SpellAddedLight;
    public float SpellAddedArcane;

    public const float ResistanceCap = 75f;
    public const float BlockChanceCap = 75f;

    /// <summary>Armor mitigation applied to physical hits (Thrust/Blunt/Slash alike).
    /// Computed in StatCalculator via ArmorBalance — the soft cap scales with level.</summary>
    public float PhysicalReduction;

    public readonly float ResistanceFor(Skills.DamageKind kind) => kind switch
    {
        Skills.DamageKind.Fire => FireResistance,
        Skills.DamageKind.Cold => ColdResistance,
        Skills.DamageKind.Lightning => LightningResistance,
        Skills.DamageKind.Acid => AcidResistance,
        Skills.DamageKind.Dark => DarkResistance,
        Skills.DamageKind.Light => LightResistance,
        Skills.DamageKind.Arcane => ArcaneResistance,
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
    // Halved so gear/attributes carry growth — stats should matter more than levels.
    public const float HealthPerCharLevel = 4f;
    public const float UnarmedMinDamage = 2f;
    public const float UnarmedMaxDamage = 4f;
    public const float UnarmedAttackSpeed = 1.2f;
    public const float UnarmedRange = 1.2f;
    /// <summary>Base seconds between successful blocks, before BlockCooldownRecovery.</summary>
    public const float BaseBlockCooldown = 2f;

    // Mana: pool and regen grow with character level (a PoE-style passive skill point
    // system will layer on top later); item modifiers add flat mana / % regen.
    public const float BaseMaxMana = 40f;
    public const float ManaPerCharLevel = 2f; // halved: INT/gear should drive the pool
    public const float BaseManaRegen = 1.2f;       // per second at level 1
    public const float ManaRegenPerCharLevel = 0.15f;

    // Critical hits: every character starts with a small base chance and a 150%
    // damage multiplier; weapon suffixes raise both.
    public const float BaseCritChance = 5f;
    public const float BaseCritDamage = 150f;
    public const float CritChanceCap = 75f;

    public static ComputedStats Compute(GameData data, CharacterData character, StatCollection temporaryEffects = null)
    {
        // 0) ACTIVE-GEAR pass: an equipped piece only counts while its requirements
        // are met by attributes computed from everything EXCEPT itself. This kills the
        // bootstrap exploit (+INT amulet -> equip INT robe -> remove amulet, keep robe):
        // deactivation cascades until the set is stable, so a whole chain of gear that
        // only stood on a removed piece collapses with it. Inactive gear stays worn but
        // contributes NOTHING (the UI flags it in red).
        var wornItems = character.Equipment.Values.Where(it => it != null).ToList();
        var wornStats = wornItems.ToDictionary(it => it.InstanceId, it => it.TotalStats(data));
        var inactive = new HashSet<Guid>();
        float PassiveAttr(StatType t)
        {
            float v = 0f;
            foreach (var nodeId in character.AllocatedPassives)
                if (data.PassiveTree.ById.TryGetValue(nodeId, out var node))
                    foreach (var fx in node.Effects)
                        if (fx.Stat == t) v += fx.Value;
            return v;
        }
        float pStr = PassiveAttr(StatType.Strength);
        float pDex = PassiveAttr(StatType.Dexterity);
        float pInt = PassiveAttr(StatType.Intelligence);
        for (int pass = 0; pass <= wornItems.Count; pass++)
        {
            bool changed = false;
            foreach (var item in wornItems)
            {
                if (inactive.Contains(item.InstanceId)) continue;
                var b = item.GetBase(data);
                if (b == null) continue;
                float aStr = character.BaseStrength + pStr;
                float aDex = character.BaseDexterity + pDex;
                float aInt = character.BaseIntelligence + pInt;
                foreach (var other in wornItems)
                {
                    if (other.InstanceId == item.InstanceId || inactive.Contains(other.InstanceId)) continue;
                    var os = wornStats[other.InstanceId];
                    aStr += os.Get(StatType.Strength);
                    aDex += os.Get(StatType.Dexterity);
                    aInt += os.Get(StatType.Intelligence);
                }
                if (character.Level < b.RequiredLevel ||
                    item.EffectiveRequirement(data, b.RequiredStrength) > aStr + 0.01f ||
                    item.EffectiveRequirement(data, b.RequiredDexterity) > aDex + 0.01f ||
                    item.EffectiveRequirement(data, b.RequiredIntelligence) > aInt + 0.01f)
                {
                    inactive.Add(item.InstanceId);
                    changed = true;
                }
            }
            if (!changed) break;
        }

        // 1) Aggregate flat/percent contributions from all ACTIVE equipped items.
        var total = new StatCollection();
        ItemInstance weapon = null;
        ItemInstance offHand = null;
        bool hasShield = false;
        float shieldArmor = 0f;
        foreach (var (slot, item) in character.Equipment)
        {
            if (item == null || inactive.Contains(item.InstanceId)) continue;
            total.AddAll(item.TotalStats(data));
            if (slot == EquipSlot.MainHand) weapon = item;
            if (slot == EquipSlot.OffHand) offHand = item;
            if (item.GetBase(data)?.Category == ItemCategory.Shield)
            {
                hasShield = true;
                shieldArmor += item.TotalStats(data).Get(StatType.Armor);
            }
        }

        // The weapon's own flat-phys and %Physical rolls are LOCAL: they fold into the
        // weapon's damage line in step 3 (exactly the total its tooltip shows), so pull
        // them back out of the global pool — they must never double-dip the
        // whole-character physical multiplier.
        if (weapon != null)
        {
            var wLocal = weapon.TotalStats(data);
            total.Add(StatType.PhysicalDamage, -wLocal.Get(StatType.PhysicalDamage));
            total.Add(StatType.AddedPhysicalDamage, -wLocal.Get(StatType.AddedPhysicalDamage));
        }

        // 1b) Allocated passive tree nodes contribute through the SAME pool as item
        // modifiers, so every existing stat works in the tree with no extra plumbing.
        foreach (var nodeId in character.AllocatedPassives)
            if (data.PassiveTree.ById.TryGetValue(nodeId, out var node))
                foreach (var fx in node.Effects)
                    total.Add(fx.Stat, fx.Value);

        // 2) Temporary effects (buffs/debuffs) merge into the same pool.
        if (temporaryEffects != null) total.AddAll(temporaryEffects);

        // 2b) Primary attributes resolve FIRST; their derived bonuses (AttributeBalance —
        // the one place those conversions live) then feed the stats below.
        // Base attributes come from the character's CLASS spread (default 10s for
        // pre-class saves); gear and passives stack on top.
        float strength = character.BaseStrength + total.Get(StatType.Strength);
        float dexterity = character.BaseDexterity + total.Get(StatType.Dexterity);
        float intelligence = character.BaseIntelligence + total.Get(StatType.Intelligence);

        var s = new ComputedStats
        {
            Strength = strength,
            Dexterity = dexterity,
            Intelligence = intelligence,
            MaxHealth = BaseMaxHealth + HealthPerCharLevel * (character.Level - 1) + total.Get(StatType.MaxHealth)
                        + strength * AttributeBalance.LifePerStrength,
            LifeRegeneration = total.Get(StatType.LifeRegeneration),
            MaxMana = BaseMaxMana + ManaPerCharLevel * (character.Level - 1) + total.Get(StatType.MaximumMana)
                      + intelligence * AttributeBalance.ManaPerIntelligence,
            ManaRegeneration = (BaseManaRegen + ManaRegenPerCharLevel * (character.Level - 1))
                               * (1f + total.Get(StatType.ManaRegeneration) / 100f)
                               * (1f + intelligence * AttributeBalance.ManaRegenPctPerIntelligence / 100f),
            MovementSpeed = BaseMoveSpeed * (1f + (total.Get(StatType.MovementSpeed)
                            + dexterity / 10f * AttributeBalance.MovementPctPer10Dexterity) / 100f),
            Armor = total.Get(StatType.Armor),
            DeflectionRating = total.Get(StatType.DeflectionRating)
                               * (1f + dexterity * AttributeBalance.DeflectionPctPerDexterity / 100f),
            MaxEnergyShield = total.Get(StatType.EnergyShield)
                              * (1f + intelligence / 10f * AttributeBalance.EnergyShieldPctPer10Intelligence / 100f),
            FireResistance = MathF.Min(ComputedStats.ResistanceCap, total.Get(StatType.FireResistance)),
            ColdResistance = MathF.Min(ComputedStats.ResistanceCap, total.Get(StatType.ColdResistance)),
            LightningResistance = MathF.Min(ComputedStats.ResistanceCap, total.Get(StatType.LightningResistance)),
            AcidResistance = MathF.Min(ComputedStats.ResistanceCap, total.Get(StatType.AcidResistance)),
            DarkResistance = MathF.Min(ComputedStats.ResistanceCap, total.Get(StatType.DarkResistance)),
            LightResistance = MathF.Min(ComputedStats.ResistanceCap, total.Get(StatType.LightResistance)),
            ArcaneResistance = MathF.Min(ComputedStats.ResistanceCap, total.Get(StatType.ArcaneResistance)),
            AddedFire = total.Get(StatType.AddedFireDamage),
            AddedCold = total.Get(StatType.AddedColdDamage),
            AddedLightning = total.Get(StatType.AddedLightningDamage),
            AddedAcid = total.Get(StatType.AddedAcidDamage),
            AddedDark = total.Get(StatType.AddedDarkDamage),
            AddedLight = total.Get(StatType.AddedLightDamage),
            AddedArcane = total.Get(StatType.AddedArcaneDamage),
            SpellAddedFire = total.Get(StatType.AddedFireSpellDamage),
            SpellAddedCold = total.Get(StatType.AddedColdSpellDamage),
            SpellAddedLightning = total.Get(StatType.AddedLightningSpellDamage),
            SpellAddedAcid = total.Get(StatType.AddedAcidSpellDamage),
            SpellAddedDark = total.Get(StatType.AddedDarkSpellDamage),
            SpellAddedLight = total.Get(StatType.AddedLightSpellDamage),
            SpellAddedArcane = total.Get(StatType.AddedArcaneSpellDamage),
            PhysicalDamageIncrease = total.Get(StatType.PhysicalDamage),
            SpellDamageIncrease = total.Get(StatType.SpellDamage),
            AttackSpeedIncrease = total.Get(StatType.AttackSpeed),
            CastSpeedIncrease = total.Get(StatType.CastSpeed),
            CritChance = MathF.Min(CritChanceCap, BaseCritChance + total.Get(StatType.CriticalChance)),
            CritDamage = BaseCritDamage + total.Get(StatType.CriticalDamage),
            IgniteMagnitudeIncrease = total.Get(StatType.IgniteMagnitude),
            ChillMagnitudeIncrease = total.Get(StatType.ChillMagnitude),
            PoisonMagnitudeIncrease = total.Get(StatType.PoisonMagnitude),
            BleedMagnitudeIncrease = total.Get(StatType.BleedMagnitude),
            SummonDamageIncrease = total.Get(StatType.SummonDamage),
            SummonHealthIncrease = total.Get(StatType.SummonHealth),
            SummonLimitBonus = (int)total.Get(StatType.SummonLimit),
            LightRadiusIncrease = total.Get(StatType.LightRadius),
            InactiveItems = inactive,
        };

        // Strength's melee-physical benefit rides the existing percent-increased pool.
        s.PhysicalDamageIncrease += strength / 10f * AttributeBalance.PhysicalPctPer10Strength;
        // Rating → initial chance (level-scaled with a hard cap); the per-hit descending
        // layers are generated from this by the combat code.
        s.DeflectionChance = Deflection.ChanceFromRating(s.DeflectionRating, character.Level);
        // Armor's soft cap grows with level too (ArmorBalance centralizes the curve).
        s.PhysicalReduction = ArmorBalance.PhysicalReduction(s.Armor, character.Level);

        // Blocking: chance comes entirely from gear (shields and their modifiers); a block
        // avoids one hit completely, then waits out a cooldown that recovery stats shorten.
        s.HasShield = hasShield;
        s.ShieldArmor = shieldArmor;
        s.BlockChance = MathF.Min(ComputedStats.BlockChanceCap, total.Get(StatType.BlockChance));
        s.BlockCooldown = MathF.Max(0.25f, BaseBlockCooldown / (1f + total.Get(StatType.BlockCooldownRecovery) / 100f));

        // Dodge: config base values scaled by percent-increased stats, so items and
        // modifiers can grant e.g. "+20% Dodge Distance" or faster recovery.
        var dodge = data.Dodge;
        s.DodgeDistance = dodge.Distance * (1f + total.Get(StatType.DodgeDistance) / 100f);
        s.DodgeDuration = MathF.Max(0.05f, dodge.Duration * (1f + total.Get(StatType.DodgeDuration) / 100f));
        s.DodgeCooldown = MathF.Max(0.2f, dodge.Cooldown / (1f + total.Get(StatType.DodgeCooldownRecovery) / 100f));
        s.DodgeInvulnerability = dodge.InvulnerabilityDuration * (1f + total.Get(StatType.DodgeInvulnerability) / 100f);

        // 3) Weapon-local numbers: base stats, this weapon's own flat added phys, and
        // its own %Physical rolls — (base + flat) x local% — the exact total the item's
        // tooltip shows, mirroring how armor pieces total their defenses. A quiver is
        // ammunition: its flat-phys rolls ride the bow's damage too.
        if (weapon != null)
        {
            var w = weapon.TotalStats(data);
            float added = w.Get(StatType.AddedPhysicalDamage);
            if (offHand?.GetBase(data)?.Category == ItemCategory.Quiver)
                added += offHand.TotalStats(data).Get(StatType.AddedPhysicalDamage);
            float localScale = 1f + w.Get(StatType.PhysicalDamage) / 100f;
            s.WeaponMinDamage = (w.Get(StatType.MinPhysicalDamage) + added) * localScale;
            s.WeaponMaxDamage = (w.Get(StatType.MaxPhysicalDamage) + added) * localScale;
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
            ItemCategory.Bow => Skills.DamageKind.Thrust, // arrows pierce
            _ => Skills.DamageKind.Blunt,
        };
        return s;
    }
}
