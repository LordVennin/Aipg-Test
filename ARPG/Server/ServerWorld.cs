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
    void SkillUsed(ServerPlayer p, string skillId, Vector2 target);
    void MessageFor(ServerPlayer p, string text);
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
        var p = new ServerPlayer
        {
            Id = id,
            Name = name,
            Position = Map.PlayerSpawn,
            Character = character,
        };
        p.RecomputeStats(Data);
        p.Health = p.Stats.MaxHealth;
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
            if (p.Alive) continue;
            p.RespawnTimer -= dt;
            if (p.RespawnTimer <= 0)
            {
                p.Alive = true;
                p.Position = Map.PlayerSpawn;
                p.Health = p.Stats.MaxHealth;
                _events.PlayerRespawned(p);
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

            // Burning (ignite) damage over time.
            if (e.BurnTimeLeft > 0)
            {
                e.BurnTimeLeft -= dt;
                DamageEnemy(e, e.BurnDps * dt, e.LastHitByPlayer, e.LastHitSkillId, silentBelow: 1f);
                if (e.Dead) continue;
            }

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
                            DamagePlayer(target, e.Def.Damage, DamageKind.Physical);
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
        var pr = new ServerProjectile
        {
            Id = _nextProjectileId++,
            FromPlayer = false,
            OwnerId = e.Id,
            Position = e.Position,
            Direction = dir,
            Speed = e.Def.ProjectileSpeed,
            MaxRange = e.Def.AttackRange + 3f,
            MinDamage = e.Def.Damage,
            MaxDamage = e.Def.Damage,
            DamageKind = DamageKind.Fire,
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
                        float dmg = Roll(pr.MinDamage, pr.MaxDamage);
                        bool ignite = pr.IgniteChance > 0 && _rng.NextDouble() < pr.IgniteChance;
                        HitEnemy(e, dmg, pr.OwnerId, pr.SkillId, ignite);
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
                    if (Vector2.Distance(pr.Position, p.Position) <= ServerPlayer.Radius + 0.25f)
                    {
                        DamagePlayer(p, Roll(pr.MinDamage, pr.MaxDamage), pr.DamageKind);
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

        // Cooldown check (small tolerance for network jitter).
        if (p.SkillReadyAt.TryGetValue(skillId, out float readyAt) && Time < readyAt - 0.05f)
            return;

        var stats = SkillMath.Compute(Data, def, learned.Level, learned.ScrollDefinitions(Data), p.Stats);
        p.SkillReadyAt[skillId] = Time + stats.Cooldown;
        _events.SkillUsed(p, skillId, target);

        switch (def.Archetype)
        {
            case SkillArchetype.MeleeStrike:
            {
                var point = ClampToRange(p.Position, target, stats.Range);
                foreach (var e in EnemiesNear(point, stats.Radius))
                    HitEnemy(e, Roll(stats.MinDamage, stats.MaxDamage), playerId, skillId, RollIgnite(stats));
                break;
            }
            case SkillArchetype.MeleeArea:
            {
                foreach (var e in EnemiesNear(p.Position, stats.Radius))
                    HitEnemy(e, Roll(stats.MinDamage, stats.MaxDamage), playerId, skillId, RollIgnite(stats));
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
                    };
                    Projectiles[pr.Id] = pr;
                    _events.ProjectileSpawned(pr);
                }
                break;
            }
            case SkillArchetype.AreaBurst:
            {
                var point = ClampToRange(p.Position, target, stats.Range);
                foreach (var e in EnemiesNear(point, stats.Radius))
                    HitEnemy(e, Roll(stats.MinDamage, stats.MaxDamage), playerId, skillId, RollIgnite(stats));
                break;
            }
        }
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

    private void HitEnemy(ServerEnemy e, float damage, int byPlayer, string skillId, bool ignite)
    {
        if (ignite)
        {
            e.BurnDps = MathF.Max(e.BurnDps, damage * 0.4f / 3f);
            e.BurnTimeLeft = 3f;
        }
        DamageEnemy(e, damage, byPlayer, skillId);
    }

    private void DamageEnemy(ServerEnemy e, float damage, int byPlayer, string skillId, float silentBelow = 0f)
    {
        if (e.Dead || damage <= 0) return;
        e.Health -= damage;
        e.LastHitByPlayer = byPlayer;
        e.LastHitSkillId = skillId;
        if (e.Health <= 0)
        {
            e.Health = 0;
            e.State = EnemyState.Dead;
            _events.EnemyDied(e);
            OnEnemyKilled(e);
        }
        else if (damage >= silentBelow)
        {
            e.State = e.State == EnemyState.Idle ? EnemyState.Chase : e.State;
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

    private void DamagePlayer(ServerPlayer p, float rawDamage, DamageKind kind)
    {
        if (!p.Alive) return;
        float damage = rawDamage;
        if (kind == DamageKind.Physical)
            damage *= 1f - p.Stats.PhysicalReduction;
        else
            damage *= 1f - p.Stats.ResistanceFor(kind) / 100f;

        p.Health -= MathF.Max(0.5f, damage);
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
        var jitter = new Vector2((float)(_rng.NextDouble() - 0.5), (float)(_rng.NextDouble() - 0.5)) * 0.8f;
        var target = pos + jitter;
        if (Map.IsWallAt(target)) target = pos;
        var drop = new WorldItem { Position = target, Item = item };
        Drops[drop.DropId] = drop;
        _events.WorldItemSpawned(drop);
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
        if (!p.Character.Inventory.TryAdd(Data, drop.Item))
        {
            _events.MessageFor(p, "Inventory is full.");
            return;
        }
        Drops.Remove(dropId);
        _events.WorldItemRemoved(drop, playerId);
        _events.CharacterChanged(p);
    }
}
