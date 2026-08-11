namespace ARPG.Stats;

/// <summary>
/// Every numeric character/item statistic in the game. Semantics per stat:
///  - Flat stats add directly (MaxHealth, Armor, resistances, AddedPhysicalDamage, ModifierLimit).
///  - Percent stats are "increased %" values combined additively then applied multiplicatively
///    (MovementSpeed, PhysicalDamage, SpellDamage, AttackSpeed, CastSpeed).
///  - Weapon-local base stats live in an ItemBase's BaseStats
///    (MinPhysicalDamage, MaxPhysicalDamage, BaseAttackSpeed, WeaponRange).
/// </summary>
public enum StatType
{
    // Flat character stats
    MaxHealth,
    LifeRegeneration, // health per second
    MaximumMana,      // flat added to the level-based mana pool
    ManaRegeneration, // percent-increased over the level-based base regen
    Armor,
    FireResistance,
    ColdResistance,
    LightningResistance,
    AcidResistance,
    DarkResistance,
    LightResistance,
    ArcaneResistance,

    // Percent-increased character stats
    MovementSpeed,
    PhysicalDamage,
    SpellDamage,
    AttackSpeed,
    CastSpeed,

    // Weapon-local stats
    MinPhysicalDamage,
    MaxPhysicalDamage,
    BaseAttackSpeed,     // attacks per second
    WeaponRange,         // tiles
    AddedPhysicalDamage, // flat added to both min and max (local to weapon)

    // Flat added elemental damage on MELEE attacks (rolls on melee weapons only)
    AddedFireDamage,
    AddedColdDamage,
    AddedLightningDamage,
    AddedAcidDamage,
    AddedDarkDamage,
    AddedLightDamage,
    AddedArcaneDamage,

    // Flat added elemental damage on SPELLS (rolls on caster weapons only)
    AddedFireSpellDamage,
    AddedColdSpellDamage,
    AddedLightningSpellDamage,
    AddedAcidSpellDamage,
    AddedDarkSpellDamage,
    AddedLightSpellDamage,
    AddedArcaneSpellDamage,

    // Blocking (shields grant base BlockChance; a successful block avoids ALL damage from
    // one hit, then blocking goes on cooldown — base 2s, sped up by BlockCooldownRecovery)
    BlockChance,           // flat percent chance
    BlockCooldownRecovery, // % faster block cooldown recovery

    // Item-local meta stat: raises the item's own modifier capacity ("Expanded" prefix)
    ModifierLimit,

    // Dodge stats (percent-increased over the Data/Config/dodge.json base values,
    // so equipment and modifiers can scale them)
    DodgeDistance,
    DodgeDuration,
    DodgeCooldownRecovery, // % faster cooldown recovery (reduces effective cooldown)
    DodgeInvulnerability,
}

/// <summary>Sum of stat contributions from one source group (equipment, temporary effects, ...).</summary>
public class StatCollection
{
    private readonly Dictionary<StatType, float> _values = new();

    public float Get(StatType type) => _values.GetValueOrDefault(type, 0f);
    public void Add(StatType type, float value) => _values[type] = Get(type) + value;
    public void Clear() => _values.Clear();

    public void AddAll(StatCollection other)
    {
        foreach (var (k, v) in other._values) Add(k, v);
    }

    public void AddAll(IReadOnlyDictionary<StatType, float> values)
    {
        if (values == null) return;
        foreach (var (k, v) in values) Add(k, v);
    }

    public IEnumerable<KeyValuePair<StatType, float>> All => _values;
}
