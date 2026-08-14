namespace ARPG.Stats;

/// <summary>
/// THE central place for attribute scaling: how Strength/Dexterity/Intelligence turn
/// into derived stats. Every value here is a deliberate placeholder for the balance
/// pass — gameplay code never hardcodes an attribute conversion anywhere else.
/// </summary>
public static class AttributeBalance
{
    /// <summary>Every character's untrained baseline in each attribute.</summary>
    public const float BaseAttribute = 10f;

    // Strength: life and physical/melee power.
    public const float LifePerStrength = 0.5f;
    public const float PhysicalPctPer10Strength = 2f;

    // Dexterity: light-armor defense and mobility.
    public const float DeflectionRatingPerDexterity = 2f;
    public const float MovementPctPer10Dexterity = 0.5f;

    // Intelligence: mana and Energy Shield.
    public const float ManaPerIntelligence = 0.5f;
    public const float EnergyShieldPctPer10Intelligence = 2f;
}

/// <summary>
/// Deflection (the DEX defense): equipped gear aggregates into one character
/// DeflectionRating, converted to an INITIAL chance here. An eligible incoming
/// Attack hit then runs MULTIPLE INDEPENDENT checks at descending chances
/// (initial, initial-step, initial-2*step, ... while &gt; 0). Every SUCCESS deflects
/// away a fraction of the REMAINING damage (multiplicative); failures never stop
/// later checks. Spells, DoTs, ground effects and other non-Attack damage are
/// never deflected. All knobs live here for the balance pass.
/// </summary>
public static class Deflection
{
    /// <summary>Damage removed by each successful check (0.20 = 20% of remaining).</summary>
    public const float ReductionPerLayer = 0.20f;
    /// <summary>Chance drop between consecutive checks, in percentage points.</summary>
    public const float ChanceStepPercent = 15f;
    /// <summary>Cap on the INITIAL chance (later layers descend from it).</summary>
    public const float InitialChanceCap = 75f;

    /// <summary>Rating → initial chance %, with diminishing returns that scale by
    /// character level so the same rating is worth less on a higher-level character
    /// (mirrors how armor denominators usually grow).</summary>
    public static float ChanceFromRating(float rating, int level)
    {
        if (rating <= 0f) return 0f;
        float denom = rating + 20f + 8f * level;
        return MathF.Min(InitialChanceCap, 100f * rating / denom);
    }

    /// <summary>The descending chance layers generated for one incoming Attack:
    /// stops only once the next chance would be zero or lower.</summary>
    public static IEnumerable<float> Layers(float initialChance)
    {
        for (float c = MathF.Min(initialChance, InitialChanceCap); c > 0f; c -= ChanceStepPercent)
            yield return c;
    }

    /// <summary>Run every layer as an INDEPENDENT roll (roll() in [0,1)) and return the
    /// multiplier for the damage that still gets through. A failed layer does not stop
    /// later layers.</summary>
    public static float RollDamageMultiplier(float initialChance, Func<double> roll)
    {
        float mult = 1f;
        foreach (float chance in Layers(initialChance))
            if (roll() * 100.0 < chance)
                mult *= 1f - ReductionPerLayer;
        return mult;
    }
}

/// <summary>Energy Shield (the INT defense): absorbed before life; recharges after not
/// taking damage for the delay, at a rate proportional to maximum. All knobs central.</summary>
public static class EnergyShieldBalance
{
    /// <summary>Seconds without taking damage before recharge starts. Taking any damage
    /// (even fully absorbed) resets this.</summary>
    public const float RechargeDelay = 4f;
    /// <summary>Recharge rate as % of maximum Energy Shield per second.</summary>
    public const float RechargePctPerSecond = 25f;
}
