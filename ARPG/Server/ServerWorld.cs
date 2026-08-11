using System.Numerics;
using ARPG.Data;
using ARPG.Items;
using ARPG.Sim;
using ARPG.Skills;
using ARPG.Util;
using ARPG.World;

namespace ARPG.Server;

/// <summary>Outbound notifications from the authoritative simulation. GameServer turns these into packets.</summary>
public interface IServerEvents
{
    void EnemySpawned(ServerEnemy e);
    void EnemyHealthChanged(ServerEnemy e);
    void EnemyDied(ServerEnemy e);
    void PlayerHealthChanged(ServerPlayer p);
    void PlayerDied(ServerPlayer p);
    void PlayerRespawned(ServerPlayer p);
    void ProjectileSpawned(ServerProjectile p);
    void ProjectileDespawned(ServerProjectile p, Vector2 at);
    void WorldItemSpawned(WorldItem item);
    void WorldItemRemoved(WorldItem item, int pickedUpByPlayerId);
    void CharacterChanged(ServerPlayer p);
    /// <summary>effectPoint is the server-computed impact/effect location — clients must
    /// render the effect at exactly this point (no client-side recomputation).</summary>
    void SkillUsed(ServerPlayer p, string skillId, Vector2 effectPoint);
    void MessageFor(ServerPlayer p, string text);
    void PlayerDodged(ServerPlayer p, Vector2 direction, float distance, float duration);
    /// <summary>A damage application, for floating combat numbers on all clients.</summary>
    void DamageDealt(bool targetIsPlayer, int targetId, float amount, DamageKind kind, Vector2 position, bool blocked = false);
}

/// <summary>
/// The authoritative game simulation. Runs ONLY on the host (which in single player is a local
/// server with a loopback client). Owns enemies, AI, combat resolution, loot generation,
/// world drops, pickups and all player character mutations.
/// </summary>
public partial class ServerWorld
{
    public readonly GameData Data;
    public readonly GameMap Map;
    public readonly LootGenerator Loot;
    public readonly Dictionary<int, ServerPlayer> Players = new();
    public readonly Dictionary<int, ServerEnemy> Enemies = new();
    public readonly Dictionary<int, ServerProjectile> Projectiles = new();
    public readonly Dictionary<Guid, WorldItem> Drops = new();
    public readonly List<EnemySpawner> Spawners = new();

    private readonly IServerEvents _events;
    private readonly Random _rng;
    private int _nextEnemyId = 1;
    private int _nextProjectileId = 1;
    public float Time { get; private set; }

    private const int MaxEnemies = 16;
    private const float RespawnDelay = 20f;
    private const float PlayerRespawnDelay = 3f;

    public ServerWorld(GameData data, int mapSeed, IServerEvents events)
    {
        Data = data;
        Map = new GameMap(mapSeed);
        _events = events;
        _rng = new Random();
        Loot = new LootGenerator(data, _rng);

        // One spawner per map spawn point, alternating enemy types.
        var types = data.Enemies.Keys.OrderBy(k => k).ToList();
        for (int i = 0; i < Map.EnemySpawns.Count; i++)
            Spawners.Add(new EnemySpawner
            {
                Position = Map.EnemySpawns[i],
                EnemyTypeId = types.Count > 0 ? types[i % types.Count] : null,
            });
    }

    // ------------------------------------------------------------------ players

    public ServerPlayer AddPlayer(int id, string name, CharacterData character)
    {
        character ??= CharacterData.CreateNew(Data, name);
        character.Name = name;
        // Migrate items from older saves: derive per-side slot caps and stack counts.
        foreach (var placed in character.Inventory.Items) placed.Item.EnsureSlotData();
        foreach (var equipped in character.Equipment.Values) equipped?.EnsureSlotData();
        var p = new ServerPlayer
        {
            Id = id,
            Name = name,
            Position = Map.PlayerSpawn,
            Character = character,
        };
        p.RecomputeStats(Data);
        p.Health = p.Stats.MaxHealth;
        p.LastSyncedHealth = p.Health;
        Players[id] = p;
        return p;
    }

    public void RemovePlayer(int id) => Players.Remove(id);

    /// <summary>Movement is client-computed for responsiveness; the server sanity-clamps it.
    /// All important results (damage, loot, items) remain host-authoritative.</summary>
    public void UpdatePlayerState(int id, Vector2 pos, Vector2 facing)
    {
        if (!Players.TryGetValue(id, out var p) || !p.Alive) return;
        pos.X = Math.Clamp(pos.X, 0, Map.Width);
        pos.Y = Math.Clamp(pos.Y, 0, Map.Height);
        if (!Map.CircleHitsWall(pos, ServerPlayer.Radius * 0.7f))
            p.Position = pos;
        p.Facing = facing.NormalizedOrZero();
    }

    // ------------------------------------------------------------------ tick

    public void Tick(float dt)
    {
        Time += dt;
        TickEnemies(dt);
        TickProjectiles(dt);
        TickSpawners();
        TickPlayers(dt);
    }

    private void TickPlayers(float dt)
    {
        foreach (var p in Players.Values)
        {
            if (!p.Alive)
            {
                p.RespawnTimer -= dt;
                if (p.RespawnTimer <= 0)
                {
                    p.Alive = true;
                    p.Position = Map.PlayerSpawn;
                    p.Health = p.Stats.MaxHealth;
                    p.LastSyncedHealth = p.Health;
                    _events.PlayerRespawned(p);
                }
                continue;
            }

            // Life regeneration (from the LifeRegeneration stat, e.g. the Mending prefix).
            // Health changes are broadcast in whole-point steps to avoid packet spam.
            if (p.Stats.LifeRegeneration > 0 && p.Health < p.Stats.MaxHealth)
            {
                p.Health = MathF.Min(p.Stats.MaxHealth, p.Health + p.Stats.LifeRegeneration * dt);
                if (MathF.Abs(p.Health - p.LastSyncedHealth) >= 1f || p.Health >= p.Stats.MaxHealth)
                {
                    p.LastSyncedHealth = p.Health;
                    _events.PlayerHealthChanged(p);
                }
            }
        }
    }

    private void TickSpawners()
    {
        int alive = Enemies.Values.Count(e => !e.Dead);
        foreach (var spawner in Spawners)
        {
            if (spawner.EnemyTypeId == null) continue;
            bool occupied = spawner.AliveEnemyId >= 0 &&
                            Enemies.TryGetValue(spawner.AliveEnemyId, out var e) && !e.Dead;
            if (occupied) continue;
            if (spawner.RespawnAt <= 0)
            {
                spawner.RespawnAt = Time + (spawner.AliveEnemyId == -1 ? 0 : RespawnDelay);
            }
            if (Time >= spawner.RespawnAt && alive < MaxEnemies)
            {
                var enemy = SpawnEnemy(spawner.EnemyTypeId, spawner.Position);
                spawner.AliveEnemyId = enemy.Id;
                spawner.RespawnAt = 0;
                alive++;
            }
        }
    }

    public ServerEnemy SpawnEnemy(string typeId, Vector2 pos)
    {
        var def = Data.Enemies.GetValueOrDefault(typeId) ?? Data.Enemies.Values.First();
        var e = new ServerEnemy
        {
            Id = _nextEnemyId++,
            Def = def,
            Position = pos,
            Health = def.MaxHealth,
        };
        Enemies[e.Id] = e;
        _events.EnemySpawned(e);
        return e;
    }

    private void TickEnemies(float dt)
    {
        foreach (var e in Enemies.Values.ToList())
        {
            if (e.Dead) { Enemies.Remove(e.Id); continue; }

            // Burning (ignite) damage over time. Per-frame ticks are applied silently and
            // batched into one damage event / health update every half second.
            if (e.BurnTimeLeft > 0)
            {
                e.BurnTimeLeft -= dt;
                float tick = e.BurnDps * dt;
                e.BurnAccum += tick;
                e.BurnEmitTimer += dt;
                DamageEnemy(e, tick, e.LastHitByPlayer, e.LastHitSkillId, DamageKind.Fire, emitEvents: false);
                if (e.Dead) continue;
                if (e.BurnEmitTimer >= 0.5f && e.BurnAccum >= 1f)
                {
                    _events.DamageDealt(false, e.Id, e.BurnAccum, DamageKind.Fire, e.Position);
                    _events.EnemyHealthChanged(e);
                    e.BurnAccum = 0;
                    e.BurnEmitTimer = 0;
                }
            }

            if (Time < e.StunnedUntil) continue; // stunned: no movement, no attacks

            var target = NearestAlivePlayer(e.Position, out float dist);
            switch (e.State)
            {
                case EnemyState.Idle:
                    if (target != null && dist <= e.Def.AggroRange)
                    {
                        e.State = EnemyState.Chase;
                        e.TargetPlayerId = target.Id;
                    }
                    break;

                case EnemyState.Chase:
                    if (target == null || dist > e.Def.AggroRange * 1.5f)
                    {
                        e.State = EnemyState.Idle;
                        e.TargetPlayerId = -1;
                        break;
                    }
                    e.TargetPlayerId = target.Id;
                    if (dist <= e.Def.AttackRange && (!e.Def.Ranged || !Map.SegmentHitsWall(e.Position, target.Position)))
                    {
                        e.State = EnemyState.Attack;
                        break;
                    }
                    MoveEnemyToward(e, target.Position, dt);
                    break;

                case EnemyState.Attack:
                    if (target == null || dist > e.Def.AttackRange * 1.15f)
                    {
                        e.State = target == null ? EnemyState.Idle : EnemyState.Chase;
                        break;
                    }
                    if (Time >= e.AttackReadyAt)
                    {
                        e.AttackReadyAt = Time + e.Def.AttackCooldown;
                        if (e.Def.Ranged)
                            SpawnEnemyProjectile(e, target);
                        else
                            DamagePlayerTyped(target, RollEnemyDamage(e.Def));
                    }
                    break;
            }
        }
    }

    private void MoveEnemyToward(ServerEnemy e, Vector2 target, float dt)
    {
        var dir = (target - e.Position).NormalizedOrZero();
        var delta = dir * e.Def.MoveSpeed * dt;
        e.Position = Map.MoveWithCollision(e.Position, delta, e.Def.Radius);
    }

    private ServerPlayer NearestAlivePlayer(Vector2 from, out float distance)
    {
        ServerPlayer best = null;
        distance = float.MaxValue;
        foreach (var p in Players.Values)
        {
            if (!p.Alive) continue;
            float d = Vector2.Distance(from, p.Position);
            if (d < distance) { distance = d; best = p; }
        }
        return best;
    }

    // ------------------------------------------------------------------ projectiles

    private void SpawnEnemyProjectile(ServerEnemy e, ServerPlayer target)
    {
        var dir = (target.Position - e.Position).NormalizedOrZero();
        var damageTypes = e.Def.DamageTypes is { Count: > 0 }
            ? e.Def.DamageTypes
            : new Dictionary<DamageKind, float> { [DamageKind.Fire] = e.Def.Damage };
        var primary = damageTypes.First();
        var extra = damageTypes.Skip(1)
            .Select(kv => new DamageComponent { Kind = kv.Key, Min = kv.Value * 0.8f, Max = kv.Value * 1.2f })
            .ToList();
        var pr = new ServerProjectile
        {
            Id = _nextProjectileId++,
            FromPlayer = false,
            OwnerId = e.Id,
            Position = e.Position,
            Direction = dir,
            Speed = e.Def.ProjectileSpeed,
            MaxRange = e.Def.AttackRange + 3f,
            MinDamage = primary.Value * 0.8f,
            MaxDamage = primary.Value * 1.2f,
            DamageKind = primary.Key,
            Added = extra.Count > 0 ? extra : null,
        };
        Projectiles[pr.Id] = pr;
        _events.ProjectileSpawned(pr);
    }

    private void TickProjectiles(float dt)
    {
        foreach (var pr in Projectiles.Values.ToList())
        {
            float step = pr.Speed * dt;
            var next = pr.Position + pr.Direction * step;
            pr.Traveled += step;

            if (pr.Traveled >= pr.MaxRange || Map.SegmentHitsWall(pr.Position, next))
            {
                RemoveProjectile(pr, next);
                continue;
            }
            pr.Position = next;

            if (pr.FromPlayer)
            {
                foreach (var e in Enemies.Values)
                {
                    if (e.Dead) continue;
                    if (Vector2.Distance(pr.Position, e.Position) <= e.Def.Radius + 0.25f)
                    {
                        var (dmg, hitKind) = MitigateForEnemy(e, RollComponentList(pr.MinDamage, pr.MaxDamage, pr.DamageKind, pr.Added));
                        bool ignite = pr.IgniteChance > 0 && _rng.NextDouble() < pr.IgniteChance;
                        HitEnemy(e, dmg, pr.OwnerId, pr.SkillId, hitKind, ignite);
                        RemoveProjectile(pr, pr.Position);
                        break;
                    }
                }
            }
            else
            {
                foreach (var p in Players.Values)
                {
                    if (!p.Alive) continue;
                    if (Time < p.InvulnerableUntil) continue; // dodging players are passed through
                    if (Vector2.Distance(pr.Position, p.Position) <= ServerPlayer.Radius + 0.25f)
                    {
                        DamagePlayerTyped(p, RollComponentList(pr.MinDamage, pr.MaxDamage, pr.DamageKind, pr.Added));
                        RemoveProjectile(pr, pr.Position);
                        break;
                    }
                }
            }
        }
    }

    private void RemoveProjectile(ServerProjectile pr, Vector2 at)
    {
        if (Projectiles.Remove(pr.Id))
            _events.ProjectileDespawned(pr, at);
    }

    // ------------------------------------------------------------------ combat

    public void UseSkill(int playerId, string skillId, Vector2 target)
    {
        if (!Players.TryGetValue(playerId, out var p) || !p.Alive) return;
        var learned = p.Character.GetSkill(skillId);
        var def = Data.Skills.GetValueOrDefault(skillId);
        if (learned == null || def == null) return;

        // Weapon requirement: category check, never a specific item check.
        if (def.RequiredWeapon.HasValue)
        {
            var weapon = p.Character.MainHand;
            if (weapon == null || weapon.GetBase(Data).Category != def.RequiredWeapon.Value)
            {
                _events.MessageFor(p, $"{def.Name} requires a {def.RequiredWeapon} weapon.");
                return;
            }
        }
        if (def.RequiresShield && !p.Stats.HasShield)
        {
            _events.MessageFor(p, $"{def.Name} requires a shield equipped.");
            return;
        }

        // Cooldown check (small tolerance for network jitter).
        if (p.SkillReadyAt.TryGetValue(skillId, out float readyAt) && Time < readyAt - 0.05f)
            return;

        var stats = SkillMath.Compute(Data, def, learned.Level, learned.ScrollDefinitions(Data), p.Stats);
        p.SkillReadyAt[skillId] = Time + stats.Cooldown;

        // The effect point is computed ONCE here and broadcast; hit detection below and
        // client visuals both use this exact point.
        Vector2 effectPoint = target;

        switch (def.Archetype)
        {
            case SkillArchetype.MeleeStrike:
            {
                // Caster-relative: always projected in front of the player along the aim
                // direction, never behind, clamped to weapon/skill range.
                effectPoint = SkillMath.MeleeImpactPoint(p.Position, target, p.Facing, stats.Range);
                foreach (var e in EnemiesNear(effectPoint, stats.Radius))
                    { var (dmg, kind) = RollSkillHit(e, stats); HitEnemy(e, dmg, playerId, skillId, kind, RollIgnite(stats)); }
                break;
            }
            case SkillArchetype.MeleeSingle:
            {
                // Single target: hit only the enemy closest to the impact point, then
                // knock it back away from the caster (with wall collision).
                effectPoint = SkillMath.MeleeImpactPoint(p.Position, target, p.Facing, stats.Range);
                var victim = EnemiesNear(effectPoint, stats.Radius)
                    .OrderBy(e => Vector2.Distance(e.Position, effectPoint))
                    .FirstOrDefault();
                if (victim != null)
                {
                    var (vDmg, vKind) = RollSkillHit(victim, stats);
                    HitEnemy(victim, vDmg, playerId, skillId, vKind, RollIgnite(stats));
                    if (!victim.Dead && def.Knockback > 0)
                    {
                        var push = (victim.Position - p.Position).NormalizedOrZero();
                        if (push == Vector2.Zero) push = p.Facing;
                        victim.Position = Map.MoveWithCollision(victim.Position, push * def.Knockback, victim.Def.Radius);
                    }
                }
                break;
            }
            case SkillArchetype.MeleeArea:
            {
                effectPoint = p.Position;
                foreach (var e in EnemiesNear(p.Position, stats.Radius))
                {
                    { var (dmg, kind) = RollSkillHit(e, stats); HitEnemy(e, dmg, playerId, skillId, kind, RollIgnite(stats)); }
                    if (!e.Dead && def.StunDuration > 0)
                        e.StunnedUntil = Time + def.StunDuration;
                }
                break;
            }
            case SkillArchetype.Projectile:
            {
                var baseDir = (target - p.Position).NormalizedOrZero();
                if (baseDir == Vector2.Zero) baseDir = p.Facing;
                int count = Math.Max(1, stats.ProjectileCount);
                float spread = 0.21f; // ~12 degrees between projectiles
                for (int i = 0; i < count; i++)
                {
                    float offset = (i - (count - 1) / 2f) * spread;
                    var dir = Rotate(baseDir, offset);
                    var pr = new ServerProjectile
                    {
                        Id = _nextProjectileId++,
                        FromPlayer = true,
                        OwnerId = playerId,
                        SkillId = skillId,
                        Position = p.Position + dir * 0.4f,
                        Direction = dir,
                        Speed = stats.ProjectileSpeed,
                        MaxRange = stats.Range,
                        MinDamage = stats.MinDamage,
                        MaxDamage = stats.MaxDamage,
                        DamageKind = stats.DamageKind,
                        IgniteChance = stats.IgniteChance,
                        Added = stats.Added,
                    };
                    Projectiles[pr.Id] = pr;
                    _events.ProjectileSpawned(pr);
                }
                break;
            }
            case SkillArchetype.AreaBurst:
            {
                effectPoint = ClampToRange(p.Position, target, stats.Range);
                foreach (var e in EnemiesNear(effectPoint, stats.Radius))
                    { var (dmg, kind) = RollSkillHit(e, stats); HitEnemy(e, dmg, playerId, skillId, kind, RollIgnite(stats)); }
                break;
            }
        }

        _events.SkillUsed(p, skillId, effectPoint);
    }

    /// <summary>Server-authoritative dodge: validates the cooldown and applies i-frames.
    /// Movement itself is client-predicted (like normal movement) for responsiveness.</summary>
    public void RequestDodge(int playerId, Vector2 direction)
    {
        if (!Players.TryGetValue(playerId, out var p) || !p.Alive) return;
        if (Time < p.NextDodgeAt - 0.05f) return; // still on cooldown — reject silently
        var dir = direction.NormalizedOrZero();
        if (dir == Vector2.Zero) dir = p.Facing;
        p.NextDodgeAt = Time + p.Stats.DodgeCooldown;
        p.InvulnerableUntil = Time + p.Stats.DodgeInvulnerability;
        _events.PlayerDodged(p, dir, p.Stats.DodgeDistance, p.Stats.DodgeDuration);
    }

    private bool RollIgnite(in EffectiveSkillStats stats) =>
        stats.IgniteChance > 0 && _rng.NextDouble() < stats.IgniteChance;

    private static Vector2 ClampToRange(Vector2 from, Vector2 target, float range)
    {
        var d = target - from;
        float len = d.Length();
        return (range > 0 && len > range) ? from + d / len * range : target;
    }

    private static Vector2 Rotate(Vector2 v, float radians)
    {
        float c = MathF.Cos(radians), s = MathF.Sin(radians);
        return new Vector2(v.X * c - v.Y * s, v.X * s + v.Y * c);
    }

    private IEnumerable<ServerEnemy> EnemiesNear(Vector2 point, float radius) =>
        Enemies.Values.Where(e => !e.Dead && Vector2.Distance(e.Position, point) <= radius + e.Def.Radius).ToList();

    private float Roll(float min, float max) => min + (float)_rng.NextDouble() * (max - min);

    /// <summary>Roll a full skill hit: main damage plus any typed added components.
    /// Returns the total and the dominant damage kind (largest component) for the
    /// damage event / floating number.</summary>
    /// <summary>Roll every typed portion of a hit into (kind, amount) components.</summary>
    private List<(DamageKind kind, float amount)> RollComponentList(float min, float max,
        DamageKind mainKind, List<DamageComponent> added)
    {
        var list = new List<(DamageKind, float)> { (mainKind, Roll(min, max)) };
        if (added != null)
            foreach (var comp in added)
                list.Add((comp.Kind, Roll(comp.Min, comp.Max)));
        return list;
    }

    /// <summary>A full skill hit against one enemy: roll components, then apply the enemy's
    /// per-type resistances/weaknesses (negative resistance = extra damage).</summary>
    private (float total, DamageKind dominant) RollSkillHit(ServerEnemy target, in EffectiveSkillStats stats) =>
        MitigateForEnemy(target, RollComponentList(stats.MinDamage, stats.MaxDamage, stats.DamageKind, stats.Added));

    private (float total, DamageKind dominant) MitigateForEnemy(ServerEnemy e,
        List<(DamageKind kind, float amount)> components)
    {
        float total = 0, dominantAmount = -1;
        var dominant = DamageKind.Blunt;
        foreach (var (kind, amount) in components)
        {
            float resist = Math.Clamp(e.Def.Resistances?.GetValueOrDefault(kind) ?? 0f, -300f, 100f);
            float dmg = amount * (1f - resist / 100f);
            total += dmg;
            if (dmg > dominantAmount) { dominantAmount = dmg; dominant = kind; }
        }
        return (total, dominant);
    }

    private (float total, DamageKind dominant) MitigateForPlayer(ServerPlayer p,
        List<(DamageKind kind, float amount)> components)
    {
        float total = 0, dominantAmount = -1;
        var dominant = DamageKind.Blunt;
        foreach (var (kind, amount) in components)
        {
            float dmg = amount;
            if (DamageKinds.IsPhysical(kind))
                dmg *= 1f - p.Stats.PhysicalReduction;   // armor covers thrust/blunt/slash
            else
                dmg *= 1f - p.Stats.ResistanceFor(kind) / 100f;
            total += dmg;
            if (dmg > dominantAmount) { dominantAmount = dmg; dominant = kind; }
        }
        return (total, dominant);
    }

    /// <summary>Roll an enemy's typed attack damage from its definition (legacy single
    /// Damage value falls back to Blunt for melee, Fire for ranged).</summary>
    private List<(DamageKind kind, float amount)> RollEnemyDamage(EnemyDefinition def)
    {
        var list = new List<(DamageKind, float)>();
        if (def.DamageTypes is { Count: > 0 })
            foreach (var (kind, avg) in def.DamageTypes)
                list.Add((kind, Roll(avg * 0.8f, avg * 1.2f)));
        else
            list.Add((def.Ranged ? DamageKind.Fire : DamageKind.Blunt, def.Damage));
        return list;
    }

    private void HitEnemy(ServerEnemy e, float damage, int byPlayer, string skillId, DamageKind kind, bool ignite)
    {
        if (ignite)
        {
            e.BurnDps = MathF.Max(e.BurnDps, damage * 0.4f / 3f);
            e.BurnTimeLeft = 3f;
        }
        DamageEnemy(e, damage, byPlayer, skillId, kind);
    }

    private void DamageEnemy(ServerEnemy e, float damage, int byPlayer, string skillId,
        DamageKind kind = DamageKind.Blunt, bool emitEvents = true)
    {
        if (e.Dead || damage <= 0) return;
        e.Health -= damage;
        e.LastHitByPlayer = byPlayer;
        e.LastHitSkillId = skillId;
        if (e.Health <= 0)
        {
            e.Health = 0;
            e.State = EnemyState.Dead;
            if (emitEvents) _events.DamageDealt(false, e.Id, damage, kind, e.Position);
            _events.EnemyDied(e);
            OnEnemyKilled(e);
        }
        else if (emitEvents)
        {
            e.State = e.State == EnemyState.Idle ? EnemyState.Chase : e.State;
            _events.DamageDealt(false, e.Id, damage, kind, e.Position);
            _events.EnemyHealthChanged(e);
        }
    }

    /// <summary>Enemy death: award XP (character + skill) and generate loot authoritatively.</summary>
    private void OnEnemyKilled(ServerEnemy e)
    {
        if (Players.TryGetValue(e.LastHitByPlayer, out var killer))
        {
            bool changed = GrantCharacterXp(killer, e.Def.XpReward);
            var skill = e.LastHitSkillId != null ? killer.Character.GetSkill(e.LastHitSkillId) : null;
            if (skill != null)
                changed |= GrantSkillXp(killer, skill, e.Def.XpReward);
            if (changed) _events.CharacterChanged(killer);
        }

        foreach (var item in Loot.RollDrops(e.Def.LootTableId, e.Def.Level))
            SpawnDrop(item, e.Position);

        // Gold drop, scaled by enemy level.
        var table = Data.GetLootTable(e.Def.LootTableId);
        if (_rng.NextDouble() < table.GoldDropChance)
        {
            int amount = _rng.Next(table.GoldMin, table.GoldMax + 1);
            amount = Math.Max(1, (int)(amount * (1f + 0.25f * (e.Def.Level - 1))));
            SpawnGoldDrop(amount, e.Position);
        }
    }

    public bool GrantCharacterXp(ServerPlayer p, float xp)
    {
        var c = p.Character;
        c.Experience += xp;
        bool leveled = false;
        while (c.Experience >= c.XpToNextLevel())
        {
            c.Experience -= c.XpToNextLevel();
            c.Level++;
            leveled = true;
        }
        if (leveled)
        {
            p.RecomputeStats(Data);
            p.Health = p.Stats.MaxHealth;
            _events.PlayerHealthChanged(p);
        }
        return true;
    }

    public bool GrantSkillXp(ServerPlayer p, LearnedSkill skill, float xp)
    {
        if (skill.Level >= SkillMath.MaxSkillLevel) return false;
        skill.Experience += xp;
        while (skill.Level < SkillMath.MaxSkillLevel && skill.Experience >= SkillMath.XpToNextLevel(skill.Level))
        {
            skill.Experience -= SkillMath.XpToNextLevel(skill.Level);
            skill.Level++;
        }
        return true;
    }

    private void DamagePlayerTyped(ServerPlayer p, List<(DamageKind kind, float amount)> components)
    {
        if (!p.Alive) return;
        if (Time < p.InvulnerableUntil) return; // dodge i-frames (server-authoritative)

        // Block: a %-chance to avoid the entire hit (shields grant the chance), then
        // blocking recovers for BlockCooldown seconds before it can trigger again.
        if (p.Stats.BlockChance > 0 && Time >= p.NextBlockReadyAt &&
            _rng.NextDouble() * 100 < p.Stats.BlockChance)
        {
            p.NextBlockReadyAt = Time + p.Stats.BlockCooldown;
            _events.DamageDealt(true, p.Id, 0, DamageKind.Blunt, p.Position, blocked: true);
            return;
        }

        var (damage, kind) = MitigateForPlayer(p, components);

        damage = MathF.Max(0.5f, damage);
        p.Health -= damage;
        p.LastSyncedHealth = p.Health;
        _events.DamageDealt(true, p.Id, damage, kind, p.Position);
        if (p.Health <= 0)
        {
            p.Health = 0;
            p.Alive = false;
            p.RespawnTimer = PlayerRespawnDelay;
            _events.PlayerDied(p);
        }
        else
        {
            _events.PlayerHealthChanged(p);
        }
    }

    // ------------------------------------------------------------------ drops

    public void SpawnDrop(ItemInstance item, Vector2 pos)
    {
        var drop = new WorldItem { Position = Jitter(pos), Item = item };
        Drops[drop.DropId] = drop;
        _events.WorldItemSpawned(drop);
    }

    public void SpawnGoldDrop(int amount, Vector2 pos)
    {
        var drop = new WorldItem { Position = Jitter(pos), GoldAmount = amount };
        Drops[drop.DropId] = drop;
        _events.WorldItemSpawned(drop);
    }

    private Vector2 Jitter(Vector2 pos)
    {
        var target = pos + new Vector2((float)(_rng.NextDouble() - 0.5), (float)(_rng.NextDouble() - 0.5)) * 0.8f;
        return Map.IsWallAt(target) ? pos : target;
    }

    /// <summary>Authoritative pickup: existence, range and inventory space are all checked here,
    /// so two clients can never both take the same drop.</summary>
    public void RequestPickup(int playerId, Guid dropId)
    {
        if (!Players.TryGetValue(playerId, out var p) || !p.Alive) return;
        if (!Drops.TryGetValue(dropId, out var drop)) return;
        if (Vector2.Distance(p.Position, drop.Position) > ServerPlayer.PickupRange)
        {
            _events.MessageFor(p, "Too far away.");
            return;
        }
        if (drop.IsGold)
        {
            p.Character.Gold += drop.GoldAmount;
        }
        else if (!p.Character.Inventory.TryAdd(Data, drop.Item))
        {
            _events.MessageFor(p, "Inventory is full.");
            return;
        }
        Drops.Remove(dropId);
        _events.WorldItemRemoved(drop, playerId);
        _events.CharacterChanged(p);
    }
}
