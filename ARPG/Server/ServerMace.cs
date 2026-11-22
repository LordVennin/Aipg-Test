using System.Numerics;
using ARPG.Skills;
using ARPG.Util;

namespace ARPG.Server;

/// <summary>
/// The mace's mechanical identity (ServerWorld partial).
///
/// CRASH: when a MACE skill's knockback drives an enemy into a wall, a structure or
/// another enemy, the thrown enemy takes bonus damage (a % of the hit that threw it)
/// and a burst of stun buildup; an enemy it was slammed INTO takes a smaller share.
/// All numbers live in Data/Config/mace_crash.json; the "of Impact" mace suffix scales
/// both the knockback distance and the Crash damage.
///
/// GROUND BURSTS: delayed area hits on a timer — Fault Line's three eruptions along
/// the aim and the Aftershock scroll's repeat of any Slam.
/// </summary>
public partial class ServerWorld
{
    /// <summary>Crashes resolved this session (diagnostics, tests).</summary>
    public int CrashCount { get; private set; }
    /// <summary>The bonus damage of the most recent Crash (before mitigation), for tests.</summary>
    public float LastCrashDamage { get; private set; }
    private bool _crashHitInProgress;

    private static bool Helpless(ServerEnemy e, float time) => time < e.StunnedUntil || time < e.FrozenUntil;

    /// <summary>Stun buildup from a player's hit: scaled by their "Concussive" gear.</summary>
    private void ApplyStunBuildupBy(ServerPlayer p, ServerEnemy e, float buildup, float stunDuration) =>
        ApplyStunBuildup(e, buildup * (1f + (p?.Stats.StunBuildupIncrease ?? 0f) / 100f), stunDuration);

    /// <summary>Knock an enemy straight away from <paramref name="from"/> (walls stop
    /// it) and resolve Crash for mace skills. <paramref name="hitDamage"/> is the hit
    /// that threw it — the Crash bonus is a share of that.</summary>
    private void KnockbackEnemy(ServerPlayer p, ServerEnemy e, Vector2 from, float distance, float hitDamage,
        SkillDefinition def, string skillId)
    {
        if (distance <= 0f || e.Dead) return;
        float impact = 1f + p.Stats.ImpactIncrease / 100f;
        distance *= impact;
        var dir = (e.Position - from).NormalizedOrZero();
        if (dir == Vector2.Zero) dir = p.Facing;
        var start = e.Position;
        e.Position = Map.MoveWithCollision(start, dir * distance, e.Def.Radius, ref e.Height);
        bool hitStructure = false;
        if (Structures.Count > 0)
        {
            var beforeStructures = e.Position;
            CollideWithStructureTiles(e);
            hitStructure = Vector2.DistanceSquared(beforeStructures, e.Position) > 0.0004f ||
                           Structures.Values.Any(st => !st.Destroyed && MathF.Abs(st.Height - e.Height) <= 0.75f &&
                               Vector2.Distance(st.Position, e.Position) <= st.Radius + e.Def.Radius + 0.1f);
        }

        var cfg = Data.MaceCrash;
        if (!def.HasTag(SkillTags.Mace) || distance < cfg.MinKnockback) return;
        float moved = Vector2.Distance(start, e.Position);
        bool hitWall = moved < distance - 0.25f; // the wall cut the flight short
        // Slammed into another body: one that lay AHEAD along the throw and now overlaps.
        ServerEnemy anvil = null;
        float best = float.MaxValue;
        if (moved > 0.2f)
            foreach (var o in Enemies.Values)
            {
                if (o == e || o.Dead || MathF.Abs(o.Height - e.Height) > 0.75f) continue;
                float gap = Vector2.Distance(o.Position, e.Position);
                if (gap > e.Def.Radius + o.Def.Radius + 0.12f) continue;
                if (Vector2.Dot(o.Position - start, dir) < 0.25f) continue;
                if (gap < best) { best = gap; anvil = o; }
            }
        if (!hitWall && !hitStructure && anvil == null) return;

        CrashCount++;
        LastCrashDamage = hitDamage * cfg.DamagePct / 100f * impact;
        // Dust where it hit, and the bonus damage in its own "Crash" tint.
        _events.WorldEffect("dirtburst", e.Position, 0.9f, 0.45f, e.Height);
        _crashHitInProgress = true;
        try
        {
            HitEnemy(e, LastCrashDamage, p.Id, skillId, DamageKind.Blunt, dir);
            if (!e.Dead) ApplyStunBuildupBy(p, e, cfg.StunBuildup, cfg.StunDuration);
            if (anvil != null && !anvil.Dead)
            {
                HitEnemy(anvil, hitDamage * cfg.SecondaryDamagePct / 100f * impact, p.Id, skillId, DamageKind.Blunt, dir);
                if (!anvil.Dead) ApplyStunBuildupBy(p, anvil, cfg.SecondaryStunBuildup, cfg.StunDuration);
            }
        }
        finally { _crashHitInProgress = false; }
    }

    // ---- test hooks (the nettest drives single steps of the mechanic directly)
    public void DebugKnockback(ServerPlayer p, ServerEnemy e, Vector2 from, float distance, float hitDamage, string skillId) =>
        KnockbackEnemy(p, e, from, distance, hitDamage, Data.Skills[skillId], skillId);
    public void DebugDirectHit(ServerPlayer p, ServerEnemy e, string skillId, float damage) =>
        HitEnemy(e, damage, p.Id, skillId, DamageKind.Blunt, e.Position - p.Position);
    public void DebugStunBuildup(ServerPlayer p, ServerEnemy e, float buildup) => ApplyStunBuildupBy(p, e, buildup, 1f);

    // ------------------------------------------------------------------ ground bursts

    private sealed class GroundBurst
    {
        public float At;
        public Vector2 Position;
        public float Height, Radius, DamageMult, Knockback, StunMult;
        public int OwnerId;
        public string SkillId;
        public SkillDefinition Def;
        public EffectiveSkillStats Stats;
    }

    private readonly List<GroundBurst> _groundBursts = new();
    /// <summary>Queued Fault Line eruptions / Aftershock repeats (tests).</summary>
    public int PendingGroundBursts => _groundBursts.Count;
    /// <summary>Where each queued burst will land (tests).</summary>
    public IEnumerable<Vector2> PendingGroundBurstPositions => _groundBursts.Select(b => b.Position);

    private void QueueGroundBurst(ServerPlayer p, string skillId, SkillDefinition def, in EffectiveSkillStats stats,
        Vector2 at, float radius, float delay, float damageMult, float knockback, float stunMult)
    {
        _groundBursts.Add(new GroundBurst
        {
            At = Time + delay, Position = at, Height = Map.GroundHeightAt(at), Radius = radius,
            DamageMult = damageMult, Knockback = knockback, StunMult = stunMult,
            OwnerId = p.Id, SkillId = skillId, Def = def, Stats = stats,
        });
    }

    private void TickGroundBursts()
    {
        for (int i = _groundBursts.Count - 1; i >= 0; i--)
        {
            var b = _groundBursts[i];
            if (Time < b.At) continue;
            _groundBursts.RemoveAt(i);
            if (!Players.TryGetValue(b.OwnerId, out var p)) continue;
            foreach (var e in EnemiesNear(b.Position, b.Radius, b.Height).ToList())
            {
                var (dmg, kind) = RollSkillHit(e, b.Stats, out var comps);
                dmg *= b.DamageMult;
                HitEnemy(e, dmg, b.OwnerId, b.SkillId, kind, e.Position - b.Position);
                ApplyAilments(e, comps, dmg, b.Stats);
                if (e.Dead) continue;
                KnockbackEnemy(p, e, b.Position, b.Knockback, dmg, b.Def, b.SkillId);
                ApplyStunBuildupBy(p, e, b.Def.StunBuildup * b.StunMult, b.Def.StunDuration);
            }
            BreakBreakablesNear(b.Position, MathF.Max(b.Radius, 0.8f), p);
            _events.WorldEffect("aftershock", b.Position, b.Radius, 0.8f, b.Height);
            _events.WorldEffect("debris", b.Position, b.Radius, 0.9f, b.Height);
        }
    }

    /// <summary>Fault Line: the slam sends a fissure forward along the aim — a row of
    /// delayed eruptions, each its own hit that throws enemies outward.</summary>
    private void QueueFissure(ServerPlayer p, string skillId, SkillDefinition def, in EffectiveSkillStats stats,
        Vector2 origin, Vector2 dir)
    {
        float radius = def.FissureRadius > 0f ? def.FissureRadius * (stats.Radius / MathF.Max(0.01f, def.Radius)) : stats.Radius;
        for (int i = 1; i <= def.FissureBursts; i++)
        {
            var at = origin + dir * def.FissureSpacing * i;
            // The crack stops at a wall: nothing erupts inside or beyond it.
            if (Map.IsWallAt(at) || Map.ShotBlocked(origin, p.Height + 0.5f, at, p.Height + 0.5f)) break;
            QueueGroundBurst(p, skillId, def, stats, at, radius, def.FissureDelay * i, def.FissureDamageMult,
                def.Knockback, 1f);
        }
    }
}
