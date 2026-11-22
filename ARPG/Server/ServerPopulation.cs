using ARPG.Data;

namespace ARPG.Server;

/// <summary>
/// Who lives where (ServerWorld partial). Pack compositions, overlook pickets, promised
/// patrols, boss packs and survival-wave enemies come from the map theme's population
/// fields in Data/Zones/themes.json. A theme without a PackPool — and every map that
/// is not a portal run — uses the built-in undead lists below, which are exactly the
/// lists SetupForest used to hard-code.
/// </summary>
public partial class ServerWorld
{
    /// <summary>The pre-batch-85 compositions, verbatim. Knights (Barrow Knights and
    /// Grave Callers) join from campaign loop 2 or zone enemy level 4.</summary>
    public static readonly ZoneTheme BuiltInUndead = new()
    {
        Id = "(built-in undead)",
        PackPool =
        {
            PackPoolEntry.Of(1, 1, ("grunt", 3)),
            PackPoolEntry.Of(1, 1, ("grunt", 2), ("spitter", 1)),
            PackPoolEntry.Of(1, 1, ("grunt", 1), ("spitter", 2)),
            PackPoolEntry.Of(1, 1, ("crypt_leaper", 2), ("grunt", 1)),
            PackPoolEntry.Of(2, 4, ("bone_knight", 2), ("grunt", 1)),
            PackPoolEntry.Of(2, 4, ("grave_caller", 1), ("crypt_leaper", 1), ("grunt", 1)),
        },
        OverlookPool =
        {
            PackPoolEntry.Of(1, 1, ("spitter", 2)),
            PackPoolEntry.Of(2, 4, ("spitter", 2), ("bone_knight", 1)),
        },
        PromisedPacks = { PromisedKnights() },
        BossPacks =
        {
            BossPackDef.Of("gravelord", 1, 1, ("grunt", 2)),
            BossPackDef.Of("gravelord", 2, 4, ("bone_knight", 2)),
        },
    };

    private static PackPoolEntry PromisedKnights()
    {
        var pack = PackPoolEntry.Of(2, 4, ("bone_knight", 2));
        pack.RequireEnemy = "bone_knight";
        return pack;
    }

    /// <summary>The population rules for the current map: the theme's own on a portal
    /// run when it defines a PackPool, the built-in undead otherwise.</summary>
    public ZoneTheme PopulationTheme =>
        MapIndex == ScrollMapIndex && Map.Theme?.PackPool is { Count: > 0 } ? Map.Theme : BuiltInUndead;

    /// <summary>The sealing boss's name for use inside a sentence ("the Gravelord").</summary>
    private string BossNameMidSentence =>
        LowerThe(Enemies.TryGetValue(_bossEnemyId, out var boss) ? boss.Def.Name : "the boss");

    /// <summary>"The Gravelord" -> "the Gravelord" for use inside a sentence.</summary>
    private static string LowerThe(string name) =>
        name != null && name.StartsWith("The ") ? "the " + name[4..] : name;
}
