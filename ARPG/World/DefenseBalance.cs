namespace ARPG.World;

/// <summary>Buildable structure kinds — the byte on the wire (StructureSpawn/BuildRequest).</summary>
public enum StructureKind : byte
{
    CrossbowTurret = 0,
    SpikedBarrier = 1,
    FlameTurret = 2,
    /// <summary>The defense target itself. Not buildable — placed by the server.</summary>
    Wagon = 3,
    /// <summary>Build-phase interaction point beside the wagon. Not buildable, indestructible.</summary>
    Workbench = 4,
}

/// <summary>
/// THE numbers of the wagon-defense loop, shared verbatim by client and server: the
/// build menu shows the same prices the server charges, so building needs no stock
/// roundtrip. Turret gold costs are a deliberate gold SINK — the economy finally has
/// a drain that scales with how much defense you want to buy.
/// </summary>
public static class DefenseBalance
{
    public const int WavesTotal = 5;
    /// <summary>Base enemies in wave 1, before per-wave and per-player growth.</summary>
    public const int WaveBaseCount = 6;
    public const int WavePerIndex = 4;
    public const int WavePerExtraPlayer = 3;
    /// <summary>Seconds between individual spawns while a wave trickles in.</summary>
    public const float SpawnInterval = 1.1f;
    /// <summary>Enemy levels added per wave index on top of the zone's enemy level.</summary>
    public const int WaveLevelStep = 1;

    public const float WagonHealth = 900f;
    /// <summary>Extra wagon health per player beyond the first (more attackers incoming).</summary>
    public const float WagonHealthPerExtraPlayer = 0.25f;

    public const int CrossbowCost = 60;
    public const int BarrierCost = 25;
    public const int FlameCost = 90;

    public const float CrossbowHealth = 140f;
    public const float BarrierHealth = 320f;
    public const float FlameHealth = 140f;

    public const float CrossbowRange = 7.5f;
    public const float CrossbowCooldown = 1.1f;
    public const float CrossbowDamageMin = 6f;
    public const float CrossbowDamageMax = 10f;
    /// <summary>Turret damage grows with the zone's enemy level so late runs stay honest.</summary>
    public const float CrossbowDamagePerLevel = 0.18f;

    public const float FlameRange = 3.4f;
    public const float FlameCooldown = 0.5f;
    public const float FlameDamageMin = 3f;
    public const float FlameDamageMax = 5f;
    public const float FlameDamagePerLevel = 0.18f;

    /// <summary>Turrets are DIRECTIONAL: they only engage targets inside this cone
    /// around their placed facing (rotate at placement to aim it).</summary>
    public const float TurretConeDegrees = 130f;

    /// <summary>Workbench repairs: gold per 100 missing structure hit points — cheap
    /// on purpose (upkeep, not a second purchase).</summary>
    public const float RepairCostPer100Hp = 4f;
    public static int RepairCost(float missingHp) =>
        missingHp < 1f ? 0 : Math.Max(1, (int)MathF.Ceiling(missingHp * RepairCostPer100Hp / 100f));

    /// <summary>The turret cone test, shared by server targeting and the client's
    /// placement preview. Rotation: 0 = west, 1 = north, 2 = east, 3 = south.</summary>
    public static System.Numerics.Vector2 Facing(byte rotation) => rotation switch
    {
        0 => new System.Numerics.Vector2(-1, 0),
        1 => new System.Numerics.Vector2(0, -1),
        2 => new System.Numerics.Vector2(1, 0),
        _ => new System.Numerics.Vector2(0, 1),
    };

    public static bool InCone(System.Numerics.Vector2 from, byte rotation, System.Numerics.Vector2 target)
    {
        var to = target - from;
        float len = to.Length();
        if (len < 0.001f) return true;
        float cosHalf = MathF.Cos(TurretConeDegrees * 0.5f * MathF.PI / 180f);
        return System.Numerics.Vector2.Dot(to / len, Facing(rotation)) >= cosHalf;
    }
    /// <summary>Builds must happen within this range of the player doing the building.</summary>
    public const float BuildReach = 5.0f;
    /// <summary>How far from the wagon/workbench build placement is allowed at all —
    /// generous: most of the arena is legal, only portal mouths are off-limits.</summary>
    public const float PortalExclusion = 4.0f;

    /// <summary>Enemy melee swings against structures: plain damage every this-often
    /// (no telegraph — structures don't dodge).</summary>
    public const float StructureAttackInterval = 1.0f;
    /// <summary>Fraction of the enemy's normal hit dealt to structures per swing.</summary>
    public const float StructureDamageFactor = 1.0f;

    public static int Cost(StructureKind kind) => kind switch
    {
        StructureKind.CrossbowTurret => CrossbowCost,
        StructureKind.SpikedBarrier => BarrierCost,
        StructureKind.FlameTurret => FlameCost,
        _ => int.MaxValue,
    };

    public static float Health(StructureKind kind) => kind switch
    {
        StructureKind.CrossbowTurret => CrossbowHealth,
        StructureKind.SpikedBarrier => BarrierHealth,
        StructureKind.FlameTurret => FlameHealth,
        StructureKind.Wagon => WagonHealth,
        _ => 1f,
    };

    public static bool PlayerBuildable(StructureKind kind) =>
        kind is StructureKind.CrossbowTurret or StructureKind.SpikedBarrier
             or StructureKind.FlameTurret;
}
