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
    /// render the effect at exactly this point (no client-side recomputation). phase:
    /// 0 = instant cast (animation + effects), 1 = wind-up started (animation only),
    /// 2 = wind-up landed (impact effects at the REAL point, no animation restart).</summary>
    void SkillUsed(ServerPlayer p, string skillId, Vector2 effectPoint, byte phase = 0);
    /// <summary>A chain-lightning path (caster, then each victim in hit order) so clients
    /// can draw the bolt between the exact chain points.</summary>
    void ChainEffect(string skillId, List<Vector2> points, float height);
    void MessageFor(ServerPlayer p, string text);
    void PlayerDodged(ServerPlayer p, Vector2 direction, float distance, float duration);
    /// <summary>A damage application, for floating combat numbers on all clients.</summary>
    void DamageDealt(bool targetIsPlayer, int targetId, float amount, DamageKind kind, Vector2 position, bool blocked = false);
    /// <summary>A boss ground-slam burst, for the AoE visual on all clients.</summary>
    void EnemySlammed(ServerEnemy e, float radius);
    /// <summary>This player's current merchant stock (sent on shop open and after a buy).</summary>
    void ShopStockFor(ServerPlayer p, int npcId, IReadOnlyList<ShopEntry> stock);
    /// <summary>A one-shot or timed world visual ("zap" bursts, "firepatch" ground fire).</summary>
    void WorldEffect(string kind, Vector2 position, float radius, float duration, float height);
    void SummonSpawned(ServerSummon s);
    void SummonDespawned(ServerSummon s);
}

/// <summary>One merchant stock slot as offered to a specific player.</summary>
public class ShopEntry
{
    public int Slot;
    public ItemInstance Item;
    public int Price;
    public bool Sold;
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
    public readonly List<PackSpawner> Packs = new();

    private readonly IServerEvents _events;
    private readonly Random _rng;
    private int _nextEnemyId = 1;
    private int _nextProjectileId = 1;
    public float Time { get; private set; }

    private const int MaxEnemies = 16;
    private const float RespawnDelay = 20f;
    private const float PlayerRespawnDelay = 3f;

    public ServerWorld(GameData data, int mapSeed, IServerEvents events, string zoneThemeId = null)
    {
        Data = data;
        var theme = data.ZoneThemes.FirstOrDefault(t => t.Id == zoneThemeId)
                    ?? data.ZoneThemes.FirstOrDefault();
        Map = new GameMap(mapSeed, theme);
        _events = events;
        _rng = new Random();
        Loot = new LootGenerator(data, _rng);

        // A few roaming spawners on the open ground for ambient danger...
        var types = new[] { "grunt", "spitter" };
        for (int i = 0; i < Math.Min(4, Map.EnemySpawns.Count); i++)
            Spawners.Add(new EnemySpawner
            {
                Position = Map.EnemySpawns[i],
                EnemyTypeId = types[i % types.Length],
            });

        // ...plus the AUTHORED encounters of the graveyard slice, placed on the
        // deterministic demo terrain: packs guard the lower path and the upper ruins,
        // overlook spitters hold the plateau edges, and the Gravelord waits in the
        // wide arena on the south plateau. Packs share aggro and respawn as groups.
        Packs.Add(new PackSpawner // lower-path picket, east corridor
        {
            Position = new Vector2(14.2f, 17.5f),
            Entries = new[] { ("grunt", 3) },
        });
        Packs.Add(new PackSpawner // under-bridge ambush
        {
            Position = new Vector2(10.6f, 18.2f),
            Entries = new[] { ("grunt", 2), ("spitter", 1) },
        });
        Packs.Add(new PackSpawner // upper ruins, north court
        {
            Position = new Vector2(11.5f, 9.5f),
            Entries = new[] { ("grunt", 3) },
            LeaderAffixes = EliteAffix.Brutish,
        });
        Packs.Add(new PackSpawner // upper ruins, west hall
        {
            Position = new Vector2(8.2f, 12.5f),
            Entries = new[] { ("spitter", 1), ("grunt", 2) },
            LeaderAffixes = EliteAffix.Swift,
        });
        Packs.Add(new PackSpawner // overlook: spitters on plateau A's south rim
        {
            Position = new Vector2(12.2f, 15.3f),
            Entries = new[] { ("spitter", 2) },
            ScatterRadius = 0.9f,
        });
        Packs.Add(new PackSpawner // crown overlook, warded sentinel
        {
            Position = new Vector2(7.5f, 7.5f),
            Entries = new[] { ("spitter", 1) },
            LeaderAffixes = EliteAffix.Warded,
            ScatterRadius = 0.4f,
        });
        Packs.Add(new PackSpawner // miniboss arena on plateau B
        {
            Position = new Vector2(10.5f, 25.0f),
            Entries = new[] { ("gravelord", 1), ("grunt", 2) },
            LeaderAffixes = EliteAffix.Boss,
            ScatterRadius = 1.8f,
            RespawnDelay = 90f,
        });

        // The test merchant sets up near the player spawn, on the first walkable spot
        // outside the spawn-clear zone (so players never spawn inside them).
        if (data.Npcs.ContainsKey("merchant"))
        {
            var spawn = Map.PlayerSpawn;
            foreach (var off in new[]
                     {
                         new Vector2(2.6f, -1.2f), new Vector2(-2.6f, 1.2f), new Vector2(2.6f, 1.6f),
                         new Vector2(-1.4f, -2.6f), new Vector2(3.2f, 0f), new Vector2(0f, 3.2f),
                     })
            {
                var pos = spawn + off;
                if (Map.CircleHitsWall(pos, 0.4f)) continue;
                Npcs.Add(new ServerNpc
                {
                    Id = 1, TypeId = "merchant", Position = pos,
                    Height = Map.GroundHeightAt(pos),
                });
                break;
            }
        }
    }

    /// <summary>Friendly NPCs (the test merchant). Not combat entities.</summary>
    public readonly List<ServerNpc> Npcs = new();

    /// <summary>Player minions (skeleton archers), by id.</summary>
    public readonly Dictionary<int, ServerSummon> Summons = new();
    private int _nextSummonId = 1;
    private class SummonRespawn { public int OwnerId; public string SkillId; public float At; }
    private readonly List<SummonRespawn> _summonRespawns = new();

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
        p.Height = Map.GroundHeightAt(p.Position);
        p.RecomputeStats(Data);
        p.Health = p.Stats.MaxHealth;
        p.Mana = p.Stats.MaxMana;
        p.LastSyncedHealth = p.Health;
        p.LastSyncedMana = p.Mana;
        Players[id] = p;
        return p;
    }

    public void RemovePlayer(int id)
    {
        Players.Remove(id);
        _flow.Remove(id);
    }

    /// <summary>Movement is client-computed for responsiveness; the server sanity-clamps it.
    /// All important results (damage, loot, items) remain host-authoritative.</summary>
    public void UpdatePlayerState(int id, Vector2 pos, Vector2 facing, float height)
    {
        if (!Players.TryGetValue(id, out var p) || !p.Alive) return;
        pos.X = Math.Clamp(pos.X, 0, Map.Width);
        pos.Y = Math.Clamp(pos.Y, 0, Map.Height);
        // Sanity: accept the client's position/height only if a surface actually exists
        // there near the claimed height — the SERVER's sampled value becomes canonical.
        if (Time < p.FrozenUntil)
        {
            p.Facing = facing.NormalizedOrZero() == Vector2.Zero ? p.Facing : facing;
            p.Position = p.FrozenAt; // frozen in place: movement is rejected
            return;
        }
        if (Map.SampleHeight(pos, height) is { } surface &&
            !Map.CircleBlocked(pos, ServerPlayer.Radius * 0.7f, surface))
        {
            p.Position = pos;
            p.Height = surface;
        }
        p.Facing = facing.NormalizedOrZero();
    }

    // ------------------------------------------------------------------ tick

    public void Tick(float dt)
    {
        Time += dt;
        UpdateWindups();
        TickFirePatches();
        TickSummons(dt);
        TickEnemies(dt);
        TickProjectiles(dt);
        TickSpawners();
        TickPlayers(dt);
    }

    private void TickPlayers(float dt)
    {
        foreach (var p in Players.Values)
        {
            // Electrocuted players roll the same periodic freeze-in-place as enemies.
            if (p.Alive && Time < p.ElectrocutedUntil && Time >= p.NextShockRollAt)
            {
                p.NextShockRollAt = Time + ShockRollInterval;
                if (_rng.NextDouble() < ShockFreezeChance)
                {
                    p.FrozenUntil = MathF.Max(p.FrozenUntil, Time + ShockFreezeDuration);
                    p.FrozenAt = p.Position;
                    _events.WorldEffect("zap", p.Position, 0.7f, 0.45f, p.Height);
                }
            }
            if (!p.Alive)
            {
                p.RespawnTimer -= dt;
                if (p.RespawnTimer <= 0)
                {
                    p.Alive = true;
                    p.Position = Map.PlayerSpawn;
                    p.Height = Map.GroundHeightAt(Map.PlayerSpawn);
                    p.Health = p.Stats.MaxHealth;
                    p.Mana = p.Stats.MaxMana;
                    p.LastSyncedHealth = p.Health;
                    _events.PlayerRespawned(p);
                }
                continue;
            }

            // Gold is picked up automatically by walking over it.
            foreach (var drop in Drops.Values.ToList())
            {
                if (!drop.IsGold || Vector2.Distance(drop.Position, p.Position) > 1.1f ||
                    MathF.Abs(drop.Height - p.Height) > 0.75f) continue;
                p.Character.Gold += drop.GoldAmount;
                Drops.Remove(drop.DropId);
                _events.WorldItemRemoved(drop, p.Id);
                _events.CharacterChanged(p);
            }

            // Life regeneration (from the LifeRegeneration stat, e.g. the Mending prefix)
            // and level-based mana regeneration. Changes are broadcast in whole-point
            // steps to avoid packet spam.
            bool resourceChanged = false;
            if (p.Stats.LifeRegeneration > 0 && p.Health < p.Stats.MaxHealth)
            {
                p.Health = MathF.Min(p.Stats.MaxHealth, p.Health + p.Stats.LifeRegeneration * dt);
                if (MathF.Abs(p.Health - p.LastSyncedHealth) >= 1f || p.Health >= p.Stats.MaxHealth)
                    resourceChanged = true;
            }
            if (p.Stats.ManaRegeneration > 0 && p.Mana < p.Stats.MaxMana)
            {
                p.Mana = MathF.Min(p.Stats.MaxMana, p.Mana + p.Stats.ManaRegeneration * dt);
                if (MathF.Abs(p.Mana - p.LastSyncedMana) >= 1f || p.Mana >= p.Stats.MaxMana)
                    resourceChanged = true;
            }
            if (resourceChanged)
            {
                p.LastSyncedHealth = p.Health;
                p.LastSyncedMana = p.Mana;
                _events.PlayerHealthChanged(p);
            }
        }
    }

    private void TickSpawners()
    {
        // Authored packs: spawn the whole group when empty (immediately on world start,
        // after RespawnDelay once wiped). Members scatter around the anchor on walkable
        // ground; the first member spawned carries the pack's elite affixes.
        for (int pi = 0; pi < Packs.Count; pi++)
        {
            var pack = Packs[pi];
            pack.AliveIds.RemoveAll(id => !Enemies.TryGetValue(id, out var m) || m.Dead);
            if (pack.AliveIds.Count > 0) continue;
            if (pack.RespawnAt <= 0)
            {
                pack.RespawnAt = Time <= 0.1f ? Time : Time + pack.RespawnDelay;
            }
            if (Time < pack.RespawnAt) continue;
            pack.RespawnAt = 0;
            bool leaderPlaced = false;
            foreach (var (typeId, count) in pack.Entries)
                for (int i = 0; i < count; i++)
                {
                    var offset = new Vector2(
                        (float)(_rng.NextDouble() * 2 - 1) * pack.ScatterRadius,
                        (float)(_rng.NextDouble() * 2 - 1) * pack.ScatterRadius);
                    var pos = pack.Position + offset;
                    if (Map.CircleHitsWall(pos, 0.4f)) pos = pack.Position;
                    var affixes = leaderPlaced ? EliteAffix.None : pack.LeaderAffixes;
                    leaderPlaced = true;
                    var member = SpawnEnemy(typeId, pos, affixes, pi);
                    pack.AliveIds.Add(member.Id);
                }
        }

        int alive = Enemies.Values.Count(e => !e.Dead && e.PackId < 0);
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

    public ServerEnemy SpawnEnemy(string typeId, Vector2 pos, EliteAffix affixes = EliteAffix.None, int packId = -1)
    {
        var def = Data.Enemies.GetValueOrDefault(typeId) ?? Data.Enemies.Values.First();
        var e = new ServerEnemy
        {
            Id = _nextEnemyId++,
            Def = def,
            Position = pos,
            MaxHealth = def.MaxHealth,
            Affixes = affixes,
            PackId = packId,
        };
        if (affixes.HasFlag(EliteAffix.Brutish))
        {
            e.MaxHealth *= 2.5f;
            e.DamageScale *= 1.5f;
            e.XpScale *= 3f;
        }
        if (affixes.HasFlag(EliteAffix.Swift))
        {
            e.SpeedScale *= 1.35f;
            e.CooldownScale *= 0.7f;
            e.MaxHealth *= 1.4f;
            e.XpScale *= 2.5f;
        }
        if (affixes.HasFlag(EliteAffix.Warded))
        {
            e.BonusResist = 40f;
            e.MaxHealth *= 1.8f;
            e.XpScale *= 2.5f;
        }
        e.Health = e.MaxHealth;
        e.Height = Map.GroundHeightAt(pos);
        Enemies[e.Id] = e;
        _events.EnemySpawned(e);
        return e;
    }

    /// <summary>Soft enemy-vs-enemy collision: overlapping enemies push each other apart so
    /// packs spread out instead of stacking into one sprite. Runs after AI movement.</summary>
    private void SeparateEnemies(float dt)
    {
        var list = Enemies.Values.Where(e => !e.Dead).ToList();
        for (int i = 0; i < list.Count; i++)
        {
            for (int j = i + 1; j < list.Count; j++)
            {
                var a = list[i];
                var b = list[j];
                if (MathF.Abs(a.Height - b.Height) > 0.75f) continue; // different surfaces don't collide
                float minDist = (a.Def.Radius + b.Def.Radius) * 0.9f;
                var delta = b.Position - a.Position;
                float distSq = delta.LengthSquared();
                if (distSq >= minDist * minDist) continue;
                // Perfectly stacked enemies get a deterministic split direction.
                var dir = distSq > 0.0001f
                    ? delta / MathF.Sqrt(distSq)
                    : new Vector2(MathF.Cos(a.Id * 2.4f), MathF.Sin(a.Id * 2.4f));
                float overlap = minDist - MathF.Sqrt(distSq);
                var push = dir * MathF.Min(overlap * 0.5f, 3f * dt); // gentle, framerate-safe
                a.Position = Map.MoveWithCollision(a.Position, -push, a.Def.Radius, ref a.Height);
                b.Position = Map.MoveWithCollision(b.Position, push, b.Def.Radius, ref b.Height);
            }
        }
    }

    private void TickEnemies(float dt)
    {
        UpdateFlowFields();
        SeparateEnemies(dt);
        foreach (var e in Enemies.Values.ToList())
        {
            if (e.Dead) { Enemies.Remove(e.Id); continue; }

            // Damage-over-time ailments (ignite/poison/bleed). Per-frame ticks apply
            // silently and batch into one damage event / health update every half second.
            bool TickDot(ref float timeLeft, ref float dps, ref float accum, ref float emit, DamageKind kind)
            {
                if (timeLeft <= 0) { dps = 0; return false; }
                timeLeft -= dt;
                float tick = dps * dt;
                accum += tick;
                emit += dt;
                DamageEnemy(e, tick, e.LastHitByPlayer, e.LastHitSkillId, kind, emitEvents: false);
                if (e.Dead) return true;
                if (emit >= 0.5f && accum >= 1f)
                {
                    _events.DamageDealt(false, e.Id, accum, kind, e.Position);
                    _events.EnemyHealthChanged(e);
                    accum = 0;
                    emit = 0;
                }
                return false;
            }
            if (TickDot(ref e.BurnTimeLeft, ref e.BurnDps, ref e.BurnAccum, ref e.BurnEmitTimer, DamageKind.Fire)) continue;
            if (TickDot(ref e.PoisonTimeLeft, ref e.PoisonDps, ref e.PoisonAccum, ref e.PoisonEmitTimer, DamageKind.Acid)) continue;
            if (TickDot(ref e.BleedTimeLeft, ref e.BleedDps, ref e.BleedAccum, ref e.BleedEmitTimer, DamageKind.Blunt)) continue;

            // Chill decays constantly; electrocute rolls a freeze-in-place every 2s.
            if (e.ChillMagnitude > 0)
                e.ChillMagnitude = MathF.Max(0f, e.ChillMagnitude - ChillDecayPerSecond * dt);
            if (Time < e.ElectrocutedUntil && Time >= e.NextShockRollAt)
            {
                e.NextShockRollAt = Time + ShockRollInterval;
                if (_rng.NextDouble() < ShockFreezeChance)
                {
                    e.FrozenUntil = MathF.Max(e.FrozenUntil, Time + ShockFreezeDuration);
                    _events.WorldEffect("zap", e.Position, 0.7f, 0.45f, e.Height);
                }
            }

            if (Time < e.StunnedUntil || Time < e.FrozenUntil) continue; // no movement, no attacks

            // Summons soak aggression: an angry enemy with a minion in reach hits IT.
            if (e.State is EnemyState.Chase or EnemyState.Attack)
            {
                var meat = NearestSummonNear(e.Position, e.Def.AttackRange, e.Height);
                if (meat != null && Time >= e.AttackReadyAt)
                {
                    e.AttackReadyAt = Time + e.Def.AttackCooldown * e.CooldownScale;
                    if (e.Def.Ranged)
                        SpawnEnemyProjectileAt(e, meat.Position, meat.Height);
                    else
                        DamageSummon(meat, RollEnemyDamage(e).Sum(c => c.amount));
                    continue;
                }
            }

            // Aggro/chase run on PATH distance (flow field), so climbing a ramp no longer
            // drops aggro — enemies path to the ramp and follow. Attacks stay strictly
            // same-surface: nothing hits through a cliff face or a bridge deck.
            var (target, pathDist) = FindTarget(e);
            float dist = target != null ? Vector2.Distance(e.Position, target.Position) : float.MaxValue;
            bool sameSurface = target != null && MathF.Abs(target.Height - e.Height) <= 0.75f;
            switch (e.State)
            {
                case EnemyState.Idle:
                    if (target != null && pathDist <= e.Def.AggroRange)
                    {
                        e.State = EnemyState.Chase;
                        e.TargetPlayerId = target.Id;
                        AlertPack(e, target);
                    }
                    break;

                case EnemyState.Chase:
                    if (target == null || pathDist > e.Def.AggroRange * 1.5f)
                    {
                        e.State = EnemyState.Idle;
                        e.TargetPlayerId = -1;
                        break;
                    }
                    e.TargetPlayerId = target.Id;
                    if (dist <= e.Def.AttackRange && CanAttack(e, target, sameSurface))
                    {
                        e.State = EnemyState.Attack;
                        break;
                    }
                    MoveEnemyToward(e, ChaseWaypoint(e, target), dt);
                    break;

                case EnemyState.Attack:
                    if (target == null || dist > e.Def.AttackRange * 1.15f || !CanAttack(e, target, sameSurface))
                    {
                        e.State = target == null ? EnemyState.Idle : EnemyState.Chase;
                        break;
                    }
                    // Boss ground slam: an AoE burst around the boss on its own timer,
                    // hitting every same-surface player in the radius with knockback.
                    if (e.Def.SlamRadius > 0 && Time >= e.SlamReadyAt && dist <= e.Def.SlamRadius * 0.85f)
                    {
                        e.SlamReadyAt = Time + e.Def.SlamCooldown;
                        _events.EnemySlammed(e, e.Def.SlamRadius);
                        foreach (var victim in Players.Values)
                        {
                            if (!victim.Alive || Time < victim.InvulnerableUntil) continue;
                            if (MathF.Abs(victim.Height - e.Height) > 0.75f) continue;
                            if (Vector2.Distance(victim.Position, e.Position) > e.Def.SlamRadius) continue;
                            DamagePlayerTyped(victim, new List<(DamageKind, float)>
                                { (DamageKind.Blunt, e.Def.SlamDamage * e.DamageScale) });
                            var away = (victim.Position - e.Position).NormalizedOrZero();
                            float kh = victim.Height;
                            victim.Position = Map.MoveWithCollision(victim.Position, away * 1.6f, ServerPlayer.Radius, ref kh);
                            victim.Height = kh;
                        }
                        break;
                    }
                    if (Time >= e.AttackReadyAt)
                    {
                        e.AttackReadyAt = Time + e.Def.AttackCooldown * e.CooldownScale;
                        if (e.Def.Ranged)
                            SpawnEnemyProjectile(e, target);
                        else
                            DamagePlayerTyped(target, RollEnemyDamage(e));
                    }
                    break;
            }
        }
    }

    private void MoveEnemyToward(ServerEnemy e, Vector2 target, float dt)
    {
        var dir = (target - e.Position).NormalizedOrZero();
        float slowMult = Time < e.SlowedUntil ? 0.5f : 1f;
        // Chill slows movement proportionally to its magnitude (up to 50% at the cap).
        slowMult *= 1f - 0.5f * (e.ChillMagnitude / ChillMaxMagnitude);
        var delta = dir * e.Def.MoveSpeed * e.SpeedScale * slowMult * dt;
        e.Position = Map.MoveWithCollision(e.Position, delta, e.Def.Radius, ref e.Height);
    }

    /// <summary>Shared pack aggro: when one member spots a player, the whole pack
    /// joins the fight (that's what makes an authored pack feel like one encounter).</summary>
    private void AlertPack(ServerEnemy source, ServerPlayer target)
    {
        if (source.PackId < 0) return;
        foreach (var other in Enemies.Values)
        {
            if (other.PackId != source.PackId || other.Dead || other.Id == source.Id) continue;
            if (other.State != EnemyState.Idle) continue;
            other.State = EnemyState.Chase;
            other.TargetPlayerId = target.Id;
        }
    }

    // ------------------------------------------------------------------ pathfinding
    //
    // A breadth-first flow field per player over the walkable-surface graph. Nodes are
    // (tile, surface) pairs — bridge tiles contribute TWO nodes (ground + deck) — and
    // edges connect surfaces whose heights meet within the step tolerance, so the graph
    // natively understands ramps, cliffs and walking under bridges. Each node stores its
    // hop distance to the player and the next node toward them; enemies follow that
    // chain when they can't walk a straight same-surface line. Cheap (44*44*2 nodes,
    // recomputed a few times a second per player) and ready for bigger generated maps.

    private const float FlowRecomputeInterval = 0.3f;
    private const int FlowMaxRadius = 28; // BFS depth cap in tiles (aggro leash ceiling)

    private sealed class FlowField
    {
        public float NextComputeAt;
        public ushort[] Dist; // hop count from the player's node; ushort.MaxValue = unreachable
        public int[] Next;    // neighbor node one hop closer to the player; -1 = none/at player
    }

    private readonly Dictionary<int, FlowField> _flow = new();
    private readonly Queue<int> _flowQueue = new();

    private int NodeCount => Map.Width * Map.Height * 2;

    /// <summary>The graph node an entity at (pos, height) occupies, or -1 on solid tiles.</summary>
    private int NodeOf(Vector2 pos, float height)
    {
        int x = (int)MathF.Floor(pos.X), y = (int)MathF.Floor(pos.Y);
        if (x < 0 || y < 0 || x >= Map.Width || y >= Map.Height || Map.IsSolid(x, y)) return -1;
        int bridge = Map.BridgeLevel(x, y);
        bool onDeck = bridge > 0 && MathF.Abs(height - bridge) < 0.5f;
        if (Map.IsWater(x, y) && !onDeck) return -1; // water has no ground surface
        return (y * Map.Width + x) * 2 + (onDeck ? 1 : 0);
    }

    /// <summary>Can an entity walk from surface A across the shared edge toward
    /// (dx, dy) onto surface B? Sampled at three points along the edge — ramp SIDE
    /// edges vary in height along the edge, and a midpoint-only test would connect
    /// edges that are only crossable at one spot (enemies then grind on the cliff).</summary>
    private bool SurfacesConnect(int ax, int ay, bool aDeck, int bx, int by, bool bDeck, int dx, int dy)
    {
        Span<float> laterals = stackalloc float[] { 0.2f, 0.5f, 0.8f };
        foreach (float t in laterals)
        {
            var pa = dx != 0
                ? new Vector2(ax + 0.5f + dx * 0.49f, ay + t)
                : new Vector2(ax + t, ay + 0.5f + dy * 0.49f);
            var pb = dx != 0
                ? new Vector2(bx + 0.5f - dx * 0.49f, by + t)
                : new Vector2(bx + t, by + 0.5f - dy * 0.49f);
            float ha = aDeck ? Map.BridgeLevel(ax, ay) : Map.GroundHeightAt(pa);
            float hb = bDeck ? Map.BridgeLevel(bx, by) : Map.GroundHeightAt(pb);
            if (MathF.Abs(ha - hb) > GameMap.StepTolerance) return false;
        }
        return true;
    }

    private void RecomputeFlow(ServerPlayer p, FlowField f)
    {
        int w = Map.Width, n = NodeCount;
        f.Dist ??= new ushort[n];
        f.Next ??= new int[n];
        Array.Fill(f.Dist, ushort.MaxValue);
        Array.Fill(f.Next, -1);
        int start = NodeOf(p.Position, p.Height);
        if (start < 0) return;
        _flowQueue.Clear();
        f.Dist[start] = 0;
        _flowQueue.Enqueue(start);
        Span<(int dx, int dy)> dirs = stackalloc (int, int)[] { (1, 0), (-1, 0), (0, 1), (0, -1) };
        while (_flowQueue.Count > 0)
        {
            int node = _flowQueue.Dequeue();
            int d = f.Dist[node];
            if (d >= FlowMaxRadius) continue;
            int tile = node / 2;
            bool deck = (node & 1) == 1;
            int x = tile % w, y = tile / w;
            foreach (var (dx, dy) in dirs)
            {
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= Map.Width || ny >= Map.Height || Map.IsSolid(nx, ny))
                    continue;
                for (int s = 0; s < 2; s++)
                {
                    if (s == 1 && Map.BridgeLevel(nx, ny) == 0) continue;
                    if (s == 0 && Map.IsWater(nx, ny)) continue; // no walking on water
                    int nn = (ny * w + nx) * 2 + s;
                    if (f.Dist[nn] != ushort.MaxValue) continue;
                    if (!SurfacesConnect(x, y, deck, nx, ny, s == 1, dx, dy)) continue;
                    f.Dist[nn] = (ushort)(d + 1);
                    f.Next[nn] = node; // one hop closer to the player
                    _flowQueue.Enqueue(nn);
                }
            }
        }
    }

    private void UpdateFlowFields()
    {
        foreach (var p in Players.Values)
        {
            if (!p.Alive) continue;
            if (!_flow.TryGetValue(p.Id, out var f))
                _flow[p.Id] = f = new FlowField();
            if (Time < f.NextComputeAt) continue;
            f.NextComputeAt = Time + FlowRecomputeInterval;
            RecomputeFlow(p, f);
        }
    }

    /// <summary>The alive player with the shortest PATH to this enemy (in tiles), so
    /// unreachable players (other surface, walled off) are never chosen. Ranged enemies
    /// also consider straight LINE OF FIRE across elevations — an overlook spitter on
    /// the rim aggros the player below even though the walking path winds down a ramp.</summary>
    private (ServerPlayer target, float pathDist) FindTarget(ServerEnemy e)
    {
        int node = NodeOf(e.Position, e.Height);
        if (node < 0) return (null, float.MaxValue);
        ServerPlayer best = null;
        float bestD = float.MaxValue;
        foreach (var p in Players.Values)
        {
            if (!p.Alive || !_flow.TryGetValue(p.Id, out var f) || f.Dist == null) continue;
            float d = f.Dist[node];
            if (e.Def.Ranged && d > e.Def.AggroRange)
            {
                float euclid = Vector2.Distance(e.Position, p.Position);
                if (euclid < d && euclid <= e.Def.AggroRange * 1.5f &&
                    !Map.ShotBlocked(e.Position, e.Height + 0.5f, p.Position, p.Height + 0.5f))
                    d = euclid;
            }
            if (d < bestD) { bestD = d; best = p; }
        }
        return (best, bestD);
    }

    /// <summary>Where a chasing enemy should move: straight at a same-surface visible
    /// target, else the center of the next flow-field tile toward the player.</summary>
    private Vector2 ChaseWaypoint(ServerEnemy e, ServerPlayer target)
    {
        if (MathF.Abs(target.Height - e.Height) <= 0.75f &&
            !Map.SegmentBlocked(e.Position, target.Position, e.Height + 0.5f))
            return target.Position;
        if (_flow.TryGetValue(target.Id, out var f) && f.Next != null)
        {
            int node = NodeOf(e.Position, e.Height);
            if (node >= 0 && f.Next[node] >= 0)
            {
                int tile = f.Next[node] / 2;
                return new Vector2(tile % Map.Width + 0.5f, tile / Map.Width + 0.5f);
            }
        }
        return target.Position; // no path info — fall back to a direct approach
    }

    // ------------------------------------------------------------------ projectiles

    /// <summary>Melee needs the same surface; ranged enemies may also fire ACROSS
    /// elevations when the (height-interpolated) shot path is clear — that's what makes
    /// overlook spitters rain acid down from the plateau rim.</summary>
    private bool CanAttack(ServerEnemy e, ServerPlayer target, bool sameSurface)
    {
        if (!e.Def.Ranged) return sameSurface;
        return !Map.ShotBlocked(e.Position, e.Height + 0.5f, target.Position, target.Height + 0.5f);
    }

    private void SpawnEnemyProjectile(ServerEnemy e, ServerPlayer target)
        => SpawnEnemyProjectileAt(e, target.Position, target.Height);

    private void SpawnEnemyProjectileAt(ServerEnemy e, Vector2 targetPos, float targetHeight)
    {
        var dir = (targetPos - e.Position).NormalizedOrZero();
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
            Height = e.Height,
            Direction = dir,
            Speed = e.Def.ProjectileSpeed,
            MaxRange = e.Def.AttackRange + 3f,
            HeightStep = (targetHeight - e.Height) /
                         MathF.Max(0.5f, Vector2.Distance(e.Position, targetPos)),
            MinDamage = primary.Value * 0.8f * e.DamageScale,
            MaxDamage = primary.Value * 1.2f * e.DamageScale,
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
            float nextHeight = pr.Height + pr.HeightStep * step;
            pr.Traveled += step;

            if (pr.Traveled >= pr.MaxRange ||
                Map.ShotBlocked(pr.Position, pr.Height + 0.5f, next, nextHeight + 0.5f))
            {
                RemoveProjectile(pr, next);
                continue;
            }
            pr.Position = next;
            pr.Height = nextHeight;

            if (pr.FromPlayer)
            {
                foreach (var e in Enemies.Values)
                {
                    if (e.Dead || MathF.Abs(e.Height - pr.Height) > 0.75f) continue;
                    if (Vector2.Distance(pr.Position, e.Position) <= e.Def.Radius + 0.25f)
                    {
                        var comps = RollComponentList(pr.MinDamage, pr.MaxDamage, pr.DamageKind, pr.Added);
                        ApplyCritRoll(comps, pr.CritChance, pr.CritDamage);
                        var (dmg, hitKind) = MitigateForEnemy(e, comps);
                        HitEnemy(e, dmg, pr.OwnerId, pr.SkillId, hitKind);
                        ApplyAilments(e, comps, dmg, pr.Ailments);
                        // Scroll of Shattering: cold projectiles burst into small shards
                        // that continue BEHIND the struck enemy in a shotgun spread.
                        if (pr.Ailments.ShatterShards > 0)
                            SpawnShatterShards(pr, e);
                        // Scroll of Scorched Earth: fire projectiles scorch the ground.
                        if (pr.Ailments.FirePatch)
                            SpawnFirePatch(e.Position, e.Height, MathF.Max(2f, dmg * 0.2f), pr.OwnerId, pr.SkillId);
                        RemoveProjectile(pr, pr.Position);
                        break;
                    }
                }
            }
            else
            {
                bool consumed = false;
                foreach (var s2 in Summons.Values)
                {
                    if (MathF.Abs(s2.Height - pr.Height) > 0.75f) continue;
                    if (Vector2.Distance(pr.Position, s2.Position) <= ServerSummon.Radius + 0.25f)
                    {
                        DamageSummon(s2, Roll(pr.MinDamage, pr.MaxDamage));
                        RemoveProjectile(pr, pr.Position);
                        consumed = true;
                        break;
                    }
                }
                if (consumed) continue;
                foreach (var p in Players.Values)
                {
                    if (!p.Alive || MathF.Abs(p.Height - pr.Height) > 0.75f) continue;
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

    public void UseSkill(int playerId, string skillId, Vector2 target, int targetEnemyId = -1, float charge = 0f)
    {
        if (!Players.TryGetValue(playerId, out var p) || !p.Alive) return;
        var learned = p.Character.GetSkill(skillId);
        var def = Data.Skills.GetValueOrDefault(skillId);
        if (learned == null || def == null) return;

        // Frozen solid (chill freeze / electrocute paralysis): no actions at all.
        if (Time < p.FrozenUntil) return;

        // Global use-time lockout: you cannot dump the whole hotbar in one frame —
        // every cast locks ALL skills for its UseTime (small tolerance for jitter).
        if (Time < p.GlobalSkillReadyAt - 0.05f) return;

        // Hold-to-charge (Shield Bash): scales damage/knockback (and the client's
        // lunge). Client-timed like movement; the server just clamps it.
        charge = def.Chargeable ? Math.Clamp(charge, 0f, 1f) : 0f;
        float chargeMult = 1f + 0.7f * charge;

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

        // Hover-targeted cast: aim at the chosen enemy's true position and (for
        // projectiles) arc the shot to its elevation — that's how you pick a target
        // below a cliff or up on an overlook instead of firing along your own plane.
        float targetHeight = p.Height;
        if (targetEnemyId >= 0 && Enemies.TryGetValue(targetEnemyId, out var aimed) && !aimed.Dead)
        {
            target = aimed.Position;
            targetHeight = aimed.Height;
        }

        // Mana: validated and spent server-side (no cooldown consumed on failure).
        if (stats.ManaCost > 0)
        {
            if (p.Mana < stats.ManaCost - 0.01f)
            {
                _events.MessageFor(p, "Not enough mana.");
                return;
            }
            p.Mana -= stats.ManaCost;
            p.LastSyncedMana = p.Mana;
            _events.PlayerHealthChanged(p);
        }

        p.SkillReadyAt[skillId] = Time + stats.Cooldown;
        p.GlobalSkillReadyAt = Time + def.UseTime;

        // Lunge skills (Shield Bash) shove the caster forward client-side; cover the
        // collision with brief server-side invulnerability so it can't hurt them.
        if (def.LungeDistance > 0)
            p.InvulnerableUntil = MathF.Max(p.InvulnerableUntil, Time + 0.35f);

        // Wind-up skills (Mace Slam): costs are paid now, but the hit lands after
        // WindupTime. SkillUsed is broadcast immediately so clients start the slam
        // animation; their impact visuals self-delay by the same WindupTime.
        if (def.WindupTime > 0)
        {
            _windups.Add(new PendingStrike
            {
                PlayerId = playerId, SkillId = skillId, Target = target,
                TargetHeight = targetHeight, TargetEnemyId = targetEnemyId,
                ChargeMult = chargeMult, Stats = stats,
                ExecuteAt = Time + def.WindupTime,
            });
            _events.SkillUsed(p, skillId,
                SkillMath.MeleeImpactPoint(p.Position, target, p.Facing, stats.Range), phase: 1);
            return;
        }

        ResolveSkill(p, skillId, def, stats, target, targetHeight, chargeMult, phase: 0);
    }

    /// <summary>A cast whose hit is still winding up (Mace Slam's delayed impact).</summary>
    private class PendingStrike
    {
        public int PlayerId;
        public string SkillId;
        public Vector2 Target;
        public float TargetHeight;
        public int TargetEnemyId;
        public float ChargeMult;
        public EffectiveSkillStats Stats;
        public float ExecuteAt;
    }

    private readonly List<PendingStrike> _windups = new();

    /// <summary>Land queued wind-up strikes whose timers expired (called from the tick).</summary>
    private void UpdateWindups()
    {
        for (int i = _windups.Count - 1; i >= 0; i--)
        {
            var w = _windups[i];
            if (Time < w.ExecuteAt) continue;
            _windups.RemoveAt(i);
            if (!Players.TryGetValue(w.PlayerId, out var caster) || !caster.Alive) continue;
            var def = Data.Skills.GetValueOrDefault(w.SkillId);
            if (def == null) continue;
            // Re-aim at a hover-targeted enemy's current spot so the slam tracks
            // slightly, the way the cast would have if it resolved instantly.
            var target = w.Target;
            if (w.TargetEnemyId >= 0 && Enemies.TryGetValue(w.TargetEnemyId, out var aimed) && !aimed.Dead)
                target = aimed.Position;
            ResolveSkill(caster, w.SkillId, def, w.Stats, target, w.TargetHeight, w.ChargeMult,
                phase: 2);
        }
    }

    /// <summary>The actual hit resolution for a cast (immediately for normal skills, after
    /// the wind-up for queued ones). `phase` rides the SkillUsed broadcast: 0 = instant
    /// cast, 2 = a wind-up landing (the animation already played from the phase-1 cast).</summary>
    private void ResolveSkill(ServerPlayer p, string skillId, SkillDefinition def,
        EffectiveSkillStats stats, Vector2 target, float targetHeight, float chargeMult,
        byte phase)
    {
        int playerId = p.Id;

        // The effect point is computed ONCE here and broadcast; hit detection below and
        // client visuals both use this exact point.
        Vector2 effectPoint = target;

        switch (def.Archetype)
        {
            case SkillArchetype.MeleeStrike:
            {
                // Caster-relative: always projected in front of the player along the aim
                // direction, never behind, clamped to weapon/skill range. Hits EVERY
                // enemy in the arc — the mace visibly swings through all of them.
                effectPoint = SkillMath.MeleeImpactPoint(p.Position, target, p.Facing, stats.Range);
                foreach (var e in EnemiesNear(effectPoint, stats.Radius, p.Height))
                {
                    var (dmg, kind) = RollSkillHit(e, stats, out var comps);
                    HitEnemy(e, dmg * chargeMult, playerId, skillId, kind);
                    ApplyAilments(e, comps, dmg * chargeMult, stats);
                    if (e.Dead) continue;
                    if (def.Knockback > 0)
                    {
                        var push = (e.Position - p.Position).NormalizedOrZero();
                        if (push == Vector2.Zero) push = p.Facing;
                        e.Position = Map.MoveWithCollision(e.Position, push * def.Knockback * chargeMult, e.Def.Radius, ref e.Height);
                    }
                    if (RollStun(def))
                        e.StunnedUntil = Time + def.StunDuration *
                            (e.Affixes.HasFlag(EliteAffix.Boss) ? 0.3f : 1f);
                    if (def.SlowChance > 0 && _rng.NextDouble() < def.SlowChance)
                        e.SlowedUntil = Time + def.SlowDuration;
                }
                break;
            }
            case SkillArchetype.MeleeSingle:
            {
                // Single target: hit only the enemy closest to the impact point, then
                // knock it back away from the caster (with wall collision).
                effectPoint = SkillMath.MeleeImpactPoint(p.Position, target, p.Facing, stats.Range);
                var victim = EnemiesNear(effectPoint, stats.Radius, p.Height)
                    .OrderBy(e => Vector2.Distance(e.Position, effectPoint))
                    .FirstOrDefault();
                if (victim != null)
                {
                    var (vDmg, vKind) = RollSkillHit(victim, stats, out var vComps);
                    HitEnemy(victim, vDmg * chargeMult, playerId, skillId, vKind);
                    ApplyAilments(victim, vComps, vDmg * chargeMult, stats);
                    if (!victim.Dead && def.Knockback > 0)
                    {
                        var push = (victim.Position - p.Position).NormalizedOrZero();
                        if (push == Vector2.Zero) push = p.Facing;
                        victim.Position = Map.MoveWithCollision(victim.Position, push * def.Knockback * chargeMult, victim.Def.Radius, ref victim.Height);
                    }
                    if (!victim.Dead && RollStun(def))
                        victim.StunnedUntil = Time + def.StunDuration *
                            (victim.Affixes.HasFlag(EliteAffix.Boss) ? 0.3f : 1f);
                }
                break;
            }
            case SkillArchetype.MeleeArea:
            {
                effectPoint = p.Position;
                foreach (var e in EnemiesNear(p.Position, stats.Radius, p.Height))
                {
                    { var (dmg, kind) = RollSkillHit(e, stats, out var comps); HitEnemy(e, dmg, playerId, skillId, kind); ApplyAilments(e, comps, dmg, stats); }
                    if (!e.Dead && RollStun(def))
                        e.StunnedUntil = Time + def.StunDuration *
                            (e.Affixes.HasFlag(EliteAffix.Boss) ? 0.3f : 1f);
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
                        Height = p.Height,
                        Direction = dir,
                        Speed = stats.ProjectileSpeed,
                        MaxRange = stats.Range,
                        HeightStep = MathF.Abs(targetHeight - p.Height) > 0.05f
                            ? (targetHeight - p.Height) / MathF.Max(0.5f, Vector2.Distance(p.Position, target))
                            : 0f,
                        MinDamage = stats.MinDamage,
                        MaxDamage = stats.MaxDamage,
                        DamageKind = stats.DamageKind,
                        IgniteChance = stats.IgniteChance,
                        CritChance = stats.CritChance,
                        CritDamage = stats.CritDamage,
                        Added = stats.Added,
                        Ailments = stats,
                    };
                    Projectiles[pr.Id] = pr;
                    _events.ProjectileSpawned(pr);
                }
                break;
            }
            case SkillArchetype.AreaBurst:
            {
                effectPoint = ClampToRange(p.Position, target, stats.Range);
                foreach (var e in EnemiesNear(effectPoint, stats.Radius, p.Height))
                    { var (dmg, kind) = RollSkillHit(e, stats, out var comps); HitEnemy(e, dmg, playerId, skillId, kind); ApplyAilments(e, comps, dmg, stats); }
                break;
            }
            case SkillArchetype.ChainLightning:
            {
                // Instant-hit chain: strike the enemy nearest the aim, then leap to the
                // closest unhit enemy within Radius, up to ProjectileCount total hits
                // (Multishot scrolls therefore add extra jumps). The full chain path is
                // broadcast so clients render the bolt between the exact victims.
                effectPoint = ClampToRange(p.Position, target, stats.Range);
                var chainPoints = new List<Vector2> { p.Position };
                var hitIds = new HashSet<int>();
                var current = Enemies.Values
                    .Where(e => !e.Dead && MathF.Abs(e.Height - p.Height) <= 0.75f &&
                                Vector2.Distance(e.Position, effectPoint) <= 2.2f)
                    .OrderBy(e => Vector2.Distance(e.Position, effectPoint))
                    .FirstOrDefault();
                int maxHits = Math.Max(1, stats.ProjectileCount);
                while (current != null && hitIds.Count < maxHits)
                {
                    hitIds.Add(current.Id);
                    chainPoints.Add(current.Position);
                    var (dmg, kind) = RollSkillHit(current, stats, out var comps);
                    HitEnemy(current, dmg, playerId, skillId, kind);
                    ApplyAilments(current, comps, dmg, stats);
                    var from = current.Position;
                    current = Enemies.Values
                        .Where(e => !e.Dead && !hitIds.Contains(e.Id) &&
                                    MathF.Abs(e.Height - p.Height) <= 0.75f &&
                                    Vector2.Distance(e.Position, from) <= stats.Radius)
                        .OrderBy(e => Vector2.Distance(e.Position, from))
                        .FirstOrDefault();
                }
                if (chainPoints.Count > 1)
                {
                    effectPoint = chainPoints[^1];
                    _events.ChainEffect(skillId, chainPoints, p.Height);
                }
                break;
            }
        }

        _events.SkillUsed(p, skillId, effectPoint, phase);
    }

    /// <summary>Server-authoritative dodge: validates the cooldown and applies i-frames.
    /// Movement itself is client-predicted (like normal movement) for responsiveness.</summary>
    public void RequestDodge(int playerId, Vector2 direction)
    {
        if (!Players.TryGetValue(playerId, out var p) || !p.Alive) return;
        if (Time < p.FrozenUntil) return;         // frozen in place
        if (Time < p.NextDodgeAt - 0.05f) return; // still on cooldown — reject silently
        var dir = direction.NormalizedOrZero();
        if (dir == Vector2.Zero) dir = p.Facing;
        p.NextDodgeAt = Time + p.Stats.DodgeCooldown;
        p.InvulnerableUntil = Time + p.Stats.DodgeInvulnerability;
        _events.PlayerDodged(p, dir, p.Stats.DodgeDistance, p.Stats.DodgeDuration);
    }

    // ------------------------------------------------------------------ summons

    /// <summary>Minion cap for a summon skill: base + gear bonus (+X to summon limit).</summary>
    public int SummonLimitFor(ServerPlayer p, SkillDefinition def) =>
        def.SummonLimit + p.Stats.SummonLimitBonus;

    /// <summary>Mana price of ONE summon: flat (grows per skill level) + a fraction of
    /// the caster's max mana. Respawns after death are free.</summary>
    public float SummonManaCost(ServerPlayer p, SkillDefinition def, int level) =>
        def.ManaCost + def.ManaCostPerLevel * (level - 1) + def.ManaCostPctMax * p.Stats.MaxMana;

    private int LivingSummons(int ownerId, string skillId) =>
        Summons.Values.Count(s => s.OwnerId == ownerId && s.SkillId == skillId) +
        _summonRespawns.Count(r => r.OwnerId == ownerId && r.SkillId == skillId);

    /// <summary>Skill Menu +/- : raise or lower a summon skill's minion count.</summary>
    public void SummonAdjust(int playerId, string skillId, int delta)
    {
        if (!Players.TryGetValue(playerId, out var p) || !p.Alive) return;
        var learned = p.Character.GetSkill(skillId);
        var def = Data.Skills.GetValueOrDefault(skillId);
        if (learned == null || def == null || def.Archetype != SkillArchetype.Summon) return;

        if (delta > 0)
        {
            if (LivingSummons(playerId, skillId) >= SummonLimitFor(p, def))
            {
                _events.MessageFor(p, "Summon limit reached.");
                return;
            }
            float cost = SummonManaCost(p, def, learned.Level);
            if (p.Mana < cost - 0.01f)
            {
                _events.MessageFor(p, "Not enough mana.");
                return;
            }
            p.Mana -= cost;
            p.LastSyncedMana = p.Mana;
            _events.PlayerHealthChanged(p);
            SpawnSummon(p, learned, def);
            p.DesiredSummons[skillId] = LivingSummons(playerId, skillId);
        }
        else if (delta < 0)
        {
            // Prefer cancelling a pending respawn; otherwise dismiss a living minion.
            int pending = _summonRespawns.FindIndex(r => r.OwnerId == playerId && r.SkillId == skillId);
            if (pending >= 0)
            {
                _summonRespawns.RemoveAt(pending);
            }
            else
            {
                var victim = Summons.Values.FirstOrDefault(s => s.OwnerId == playerId && s.SkillId == skillId);
                if (victim == null) return;
                Summons.Remove(victim.Id);
                _events.SummonDespawned(victim);
            }
            p.DesiredSummons[skillId] = LivingSummons(playerId, skillId);
        }
    }

    /// <summary>Rally command (backquote): send ONE summon skill's pack to a point, or
    /// clear its rally so it falls back to following the summoner. An empty skillId
    /// applies to every summon skill the player knows.</summary>
    public void SummonRally(int playerId, string skillId, bool hasPoint, Vector2 point)
    {
        if (!Players.TryGetValue(playerId, out var p)) return;
        var targets = string.IsNullOrEmpty(skillId)
            ? p.Character.Skills.Select(s => s.SkillId)
                .Where(id => Data.Skills.GetValueOrDefault(id)?.Archetype == SkillArchetype.Summon)
                .ToList()
            : new List<string> { skillId };
        foreach (var id in targets)
        {
            if (hasPoint) p.SummonRallies[id] = (point, Map.GroundHeightAt(point));
            else p.SummonRallies.Remove(id);
        }
    }

    private void SpawnSummon(ServerPlayer p, LearnedSkill learned, SkillDefinition def)
    {
        int level = learned.Level;
        float hp = (def.SummonHealth + def.SummonHealthPerLevel * (level - 1)) *
                   (1f + p.Stats.SummonHealthIncrease / 100f);
        float dmg = (def.SummonDamage + def.SummonDamagePerLevel * (level - 1)) *
                    (1f + p.Stats.SummonDamageIncrease / 100f);
        var pos = p.Position;
        foreach (var off in new[]
                 {
                     new Vector2(0.9f, 0.4f), new Vector2(-0.9f, 0.4f), new Vector2(0.4f, -0.9f),
                     new Vector2(-0.4f, 0.9f), new Vector2(1.2f, -0.6f), new Vector2(-1.2f, -0.6f),
                 })
        {
            if (!Map.CircleHitsWall(p.Position + off, ServerSummon.Radius)) { pos = p.Position + off; break; }
        }
        var s = new ServerSummon
        {
            Id = _nextSummonId++,
            OwnerId = p.Id,
            SkillId = learned.SkillId,
            Position = pos,
            Height = Map.GroundHeightAt(pos),
            Health = hp,
            MaxHealth = hp,
            Damage = dmg,
            Melee = def.SummonMelee,
            Reach = def.SummonMelee ? 1.1f : ServerSummon.AttackRange,
            SwingTime = def.SummonMelee ? 1.0f : ServerSummon.AttackCooldown,
        };
        Summons[s.Id] = s;
        _events.SummonSpawned(s);
    }

    /// <summary>Damage a minion (enemy melee/projectiles). Death queues a FREE respawn
    /// near the summoner after the skill's respawn time.</summary>
    public void DamageSummon(ServerSummon s, float damage)
    {
        if (s.Dead || damage <= 0) return;
        s.Health -= damage;
        _events.DamageDealt(false, -s.Id, damage, DamageKind.Blunt, s.Position);
        if (s.Health > 0) return;
        s.Health = 0;
        Summons.Remove(s.Id);
        _events.SummonDespawned(s);
        var def = Data.Skills.GetValueOrDefault(s.SkillId);
        _summonRespawns.Add(new SummonRespawn
        {
            OwnerId = s.OwnerId, SkillId = s.SkillId,
            At = Time + (def?.SummonRespawnTime ?? 6f),
        });
    }

    private void TickSummons(float dt)
    {
        // Pending respawns: free, near the (living) summoner.
        for (int i = _summonRespawns.Count - 1; i >= 0; i--)
        {
            var r = _summonRespawns[i];
            if (Time < r.At) continue;
            if (!Players.TryGetValue(r.OwnerId, out var owner)) { _summonRespawns.RemoveAt(i); continue; }
            if (!owner.Alive) { r.At = Time + 1f; continue; } // wait for the summoner to respawn
            var learned = owner.Character.GetSkill(r.SkillId);
            var def = Data.Skills.GetValueOrDefault(r.SkillId);
            _summonRespawns.RemoveAt(i);
            if (learned == null || def == null) continue;
            SpawnSummon(owner, learned, def);
        }

        SeparateSummons(dt);
        foreach (var s in Summons.Values.ToList())
        {
            if (!Players.TryGetValue(s.OwnerId, out var owner))
            {
                Summons.Remove(s.Id);
                _events.SummonDespawned(s);
                continue;
            }

            // Goal: this SKILL's rally point when set, otherwise loosely follow the summoner.
            bool rallied = owner.SummonRallies.TryGetValue(s.SkillId, out var rally);
            var goal = rallied ? rally.Point : owner.Position;

            // Nearest living enemy in aggro range with a clear line of fire.
            ServerEnemy prey = null;
            float bestDist = ServerSummon.AggroRange;
            foreach (var e in Enemies.Values)
            {
                if (e.Dead || MathF.Abs(e.Height - s.Height) > 0.75f) continue;
                float d = Vector2.Distance(e.Position, s.Position);
                if (d < bestDist && !Map.ShotBlocked(s.Position, s.Height + 0.5f, e.Position, e.Height + 0.5f))
                {
                    bestDist = d;
                    prey = e;
                }
            }

            // A rally is an explicit order: march to the point before picking fights,
            // then fight whatever comes in range from the held position.
            bool marchingToRally = rallied && Vector2.Distance(s.Position, goal) > 1.2f;

            if (!marchingToRally && prey != null && bestDist <= s.Reach)
            {
                if (Time >= s.AttackReadyAt)
                {
                    s.AttackReadyAt = Time + s.SwingTime;
                    var dir = (prey.Position - s.Position).NormalizedOrZero();
                    if (s.Melee)
                    {
                        // Skeleton warrior swing: the full player-hit pipeline (mitigation,
                        // kill credit/XP to the summoner), slashing damage.
                        var comps = RollComponentList(s.Damage * 0.85f, s.Damage * 1.15f, DamageKind.Slash, null);
                        var (dmg, kind) = MitigateForEnemy(prey, comps);
                        HitEnemy(prey, dmg, s.OwnerId, s.SkillId, kind);
                    }
                    else
                    {
                        var arrow = new ServerProjectile
                        {
                            Id = _nextProjectileId++,
                            FromPlayer = true,
                            OwnerId = s.OwnerId,   // kill credit and XP go to the summoner
                            SkillId = s.SkillId,
                            SpriteOverride = "Arrow",
                            Position = s.Position + dir * 0.4f,
                            Height = s.Height,
                            Direction = dir,
                            Speed = 11f,
                            MaxRange = ServerSummon.AttackRange + 1.5f,
                            MinDamage = s.Damage * 0.85f,
                            MaxDamage = s.Damage * 1.15f,
                            DamageKind = DamageKind.Thrust,
                        };
                        Projectiles[arrow.Id] = arrow;
                        _events.ProjectileSpawned(arrow);
                    }
                }
                continue; // in fighting position: hold
            }

            // March: toward the prey if hunting near home, else toward the goal.
            // Rallied MELEE summons still chase prey near their post — a warrior with a
            // 1.1 reach that refused to leave the mark could never fight at all — while
            // rallied ranged summons hold the point and let the bow do the walking.
            var moveTarget = prey?.Position ?? goal;
            if (rallied && (!s.Melee || prey == null)) moveTarget = goal;
            float distToTarget = Vector2.Distance(s.Position, moveTarget);
            float stop = rallied || prey != null ? 0.6f : 1.8f;
            if (distToTarget > stop)
            {
                var dir = (moveTarget - s.Position).NormalizedOrZero();
                float h = s.Height;
                s.Position = Map.MoveWithCollision(s.Position, dir * ServerSummon.MoveSpeed * dt,
                    ServerSummon.Radius, ref h);
                s.Height = h;
            }
        }
    }

    /// <summary>Soft push-apart between overlapping summons (same recipe as enemies),
    /// so a pack fans out around its summoner instead of stacking into one sprite.</summary>
    private void SeparateSummons(float dt)
    {
        var list = Summons.Values.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            for (int j = i + 1; j < list.Count; j++)
            {
                var a = list[i];
                var b = list[j];
                if (MathF.Abs(a.Height - b.Height) > 0.75f) continue;
                float minDist = ServerSummon.Radius * 2.2f;
                var delta = b.Position - a.Position;
                float distSq = delta.LengthSquared();
                if (distSq >= minDist * minDist) continue;
                var dir = distSq > 0.0001f
                    ? delta / MathF.Sqrt(distSq)
                    : new Vector2(MathF.Cos(a.Id * 2.4f), MathF.Sin(a.Id * 2.4f));
                float overlap = minDist - MathF.Sqrt(distSq);
                var push = dir * MathF.Min(overlap * 0.5f, 3f * dt);
                float ha = a.Height, hb = b.Height;
                a.Position = Map.MoveWithCollision(a.Position, -push, ServerSummon.Radius, ref ha);
                b.Position = Map.MoveWithCollision(b.Position, push, ServerSummon.Radius, ref hb);
                a.Height = ha;
                b.Height = hb;
            }
        }
    }

    /// <summary>Nearest living summon within range of a point (enemy target picking).</summary>
    public ServerSummon NearestSummonNear(Vector2 point, float range, float height)
    {
        ServerSummon best = null;
        float bestD = range;
        foreach (var s in Summons.Values)
        {
            if (MathF.Abs(s.Height - height) > 0.75f) continue;
            float d = Vector2.Distance(s.Position, point);
            if (d < bestD) { bestD = d; best = s; }
        }
        return best;
    }

    // ------------------------------------------------------------------ ailments

    public const float ChillMaxMagnitude = 100f;   // modifiers can raise this later
    private const float ChillDecayPerSecond = 12f;
    private const float FreezeChanceAtCap = 0.35f;
    private const float FreezeDuration = 1.4f;
    private const float ElectrocuteDuration = 6f;  // base "shock" duration
    private const float ShockRollInterval = 2f;
    private const float ShockFreezeChance = 0.45f;
    private const float ShockFreezeDuration = 0.8f;

    /// <summary>Roll every ailment of a landed hit. `comps` are the hit's typed components
    /// AFTER the crit roll but BEFORE mitigation (poison/bleed scale off what was swung,
    /// per type); `dealtTotal` is the post-mitigation damage (ignite/chill scale off what
    /// actually landed).</summary>
    private void ApplyAilments(ServerEnemy e, List<(DamageKind kind, float amount)> comps,
        float dealtTotal, in EffectiveSkillStats stats)
    {
        if (e.Dead || dealtTotal <= 0) return;

        // Ignite: fire DoT, 80% of the hit over 4 seconds, scaled by magnitudes.
        if (stats.IgniteChance > 0 && _rng.NextDouble() < stats.IgniteChance)
        {
            e.BurnDps = MathF.Max(e.BurnDps, dealtTotal * 0.8f / 4f * stats.IgniteMagnitude);
            e.BurnTimeLeft = 4f;
        }

        // Chill: buildup proportional to the hit's share of the enemy's max life. At the
        // cap, every further chilling hit can freeze outright (blue tint, no actions).
        if (stats.ChillChance > 0 && _rng.NextDouble() < stats.ChillChance)
        {
            float gain = 100f * (dealtTotal / MathF.Max(1f, e.MaxHealth)) * 2.5f * stats.ChillMagnitude;
            e.ChillMagnitude = MathF.Min(ChillMaxMagnitude, e.ChillMagnitude + gain);
            if (e.ChillMagnitude >= ChillMaxMagnitude - 0.01f &&
                _rng.NextDouble() < FreezeChanceAtCap)
            {
                float dur = FreezeDuration * (e.Affixes.HasFlag(EliteAffix.Boss) ? 0.4f : 1f);
                e.FrozenUntil = MathF.Max(e.FrozenUntil, Time + dur);
            }
        }

        // Electrocute: for the next 6s, a roll every 2s can freeze the target in place
        // with a crackle of electricity.
        if (stats.ElectrocuteChance > 0 && _rng.NextDouble() < stats.ElectrocuteChance)
        {
            if (Time >= e.ElectrocutedUntil) e.NextShockRollAt = Time + ShockRollInterval;
            e.ElectrocutedUntil = MathF.Max(e.ElectrocutedUntil, Time + ElectrocuteDuration);
        }

        // Poison: DoT off the physical + dark + acid portions swung, 60% over 4s.
        if (stats.PoisonChance > 0 && _rng.NextDouble() < stats.PoisonChance)
        {
            float basis = comps.Where(c => DamageKinds.IsPhysical(c.kind) ||
                                           c.kind is DamageKind.Dark or DamageKind.Acid)
                               .Sum(c => c.amount);
            if (basis > 0)
            {
                e.PoisonDps = MathF.Max(e.PoisonDps, basis * 0.6f / 4f * stats.PoisonMagnitude);
                e.PoisonTimeLeft = 4f;
            }
        }

        // Bleed: physical only, but scales better (90% over 4s).
        if (stats.BleedChance > 0 && _rng.NextDouble() < stats.BleedChance)
        {
            float basis = comps.Where(c => DamageKinds.IsPhysical(c.kind)).Sum(c => c.amount);
            if (basis > 0)
            {
                e.BleedDps = MathF.Max(e.BleedDps, basis * 0.9f / 4f * stats.BleedMagnitude);
                e.BleedTimeLeft = 4f;
            }
        }
    }

    /// <summary>Active Scorched Earth fire-resistance shred stacks (expired ones pruned).</summary>
    public int FireExposureStacks(ServerEnemy e)
    {
        e.FireExposure.RemoveAll(t => t <= Time);
        return e.FireExposure.Count;
    }

    /// <summary>A burning ground circle left by Scorched Earth fire projectiles: ticks
    /// fire damage and stacks -1% fire resistance per second (5s per stack, max 25).</summary>
    private class FirePatchArea
    {
        public Vector2 Position;
        public float Height;
        public float ExpiresAt;
        public float Dps;
        public int OwnerId;
        public string SkillId;
        public float NextTickAt;
        public float NextStackAt;
    }

    private readonly List<FirePatchArea> _firePatches = new();
    public int ActiveFirePatches => _firePatches.Count;
    public const float FirePatchRadius = 1.2f;
    public const float FirePatchDuration = 3f;
    public const int FireExposureMaxStacks = 25;
    private const float FireExposureStackLife = 5f;

    private void SpawnFirePatch(Vector2 pos, float height, float dps, int ownerId, string skillId)
    {
        _firePatches.Add(new FirePatchArea
        {
            Position = pos, Height = height, ExpiresAt = Time + FirePatchDuration,
            Dps = dps, OwnerId = ownerId, SkillId = skillId,
            NextTickAt = Time + 0.5f, NextStackAt = Time + 1f,
        });
        _events.WorldEffect("firepatch", pos, FirePatchRadius, FirePatchDuration, height);
    }

    /// <summary>Scroll of Shattering: 5 small ice shards continue behind the struck enemy
    /// in a random shotgun spread at 20% of the parent's damage. Shards never re-shatter
    /// (added-projectile scrolls deliberately cannot raise the count).</summary>
    private void SpawnShatterShards(ServerProjectile parent, ServerEnemy hit)
    {
        var childStats = parent.Ailments;
        childStats.ShatterShards = 0;
        childStats.FirePatch = false;
        int count = 5;
        for (int i = 0; i < count; i++)
        {
            float spread = ((float)_rng.NextDouble() - 0.5f) * 1.2f; // ~±34 degrees
            var dir = Rotate(parent.Direction, spread);
            var shard = new ServerProjectile
            {
                Id = _nextProjectileId++,
                FromPlayer = true,
                OwnerId = parent.OwnerId,
                SkillId = parent.SkillId,
                SpriteOverride = "IceShard",
                Position = hit.Position + dir * (hit.Def.Radius + 0.25f),
                Height = parent.Height,
                Direction = dir,
                Speed = 9f,
                MaxRange = 3.5f,
                HeightStep = 0f,
                MinDamage = parent.MinDamage * 0.2f,
                MaxDamage = parent.MaxDamage * 0.2f,
                DamageKind = DamageKind.Cold,
                CritChance = parent.CritChance,
                CritDamage = parent.CritDamage,
                Ailments = childStats,
            };
            Projectiles[shard.Id] = shard;
            _events.ProjectileSpawned(shard);
        }
    }

    private void TickFirePatches()
    {
        for (int i = _firePatches.Count - 1; i >= 0; i--)
        {
            var fp = _firePatches[i];
            if (Time >= fp.ExpiresAt) { _firePatches.RemoveAt(i); continue; }
            bool tick = Time >= fp.NextTickAt;
            bool stack = Time >= fp.NextStackAt;
            if (!tick && !stack) continue;
            if (tick) fp.NextTickAt = Time + 0.5f;
            if (stack) fp.NextStackAt = Time + 1f;
            foreach (var e in EnemiesNear(fp.Position, FirePatchRadius, fp.Height))
            {
                if (tick)
                {
                    var (dmg, kind) = MitigateForEnemy(e, new List<(DamageKind, float)> { (DamageKind.Fire, fp.Dps * 0.5f) });
                    DamageEnemy(e, dmg, fp.OwnerId, fp.SkillId, kind);
                }
                if (stack && !e.Dead && FireExposureStacks(e) < FireExposureMaxStacks)
                    e.FireExposure.Add(Time + FireExposureStackLife);
            }
        }
    }

    private bool RollStun(SkillDefinition def) =>
        def.StunDuration > 0 && _rng.NextDouble() < def.StunChance;

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

    /// <summary>Living enemies within radius of a point ON the given surface height —
    /// area skills never reach through a bridge deck or across a cliff level.</summary>
    private IEnumerable<ServerEnemy> EnemiesNear(Vector2 point, float radius, float height) =>
        Enemies.Values.Where(e => !e.Dead && MathF.Abs(e.Height - height) <= 0.75f &&
                                  Vector2.Distance(e.Position, point) <= radius + e.Def.Radius).ToList();

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

    /// <summary>A full skill hit against one enemy: roll components, roll the critical
    /// strike, then apply the enemy's per-type resistances/weaknesses (negative
    /// resistance = extra damage).</summary>
    private (float total, DamageKind dominant) RollSkillHit(ServerEnemy target, in EffectiveSkillStats stats)
        => RollSkillHit(target, stats, out _);

    private (float total, DamageKind dominant) RollSkillHit(ServerEnemy target, in EffectiveSkillStats stats,
        out List<(DamageKind kind, float amount)> comps)
    {
        comps = RollComponentList(stats.MinDamage, stats.MaxDamage, stats.DamageKind, stats.Added);
        ApplyCritRoll(comps, stats.CritChance, stats.CritDamage);
        return MitigateForEnemy(target, comps);
    }

    /// <summary>Roll a critical strike: on success every component of the hit is multiplied
    /// by the crit damage multiplier (percent; 150 = 1.5x).</summary>
    private void ApplyCritRoll(List<(DamageKind kind, float amount)> comps, float chance, float damagePct)
    {
        if (chance <= 0 || _rng.NextDouble() * 100 >= chance) return;
        float mult = MathF.Max(1f, damagePct / 100f);
        for (int i = 0; i < comps.Count; i++)
            comps[i] = (comps[i].kind, comps[i].amount * mult);
    }

    private (float total, DamageKind dominant) MitigateForEnemy(ServerEnemy e,
        List<(DamageKind kind, float amount)> components)
    {
        float total = 0, dominantAmount = -1;
        var dominant = DamageKind.Blunt;
        foreach (var (kind, amount) in components)
        {
            float resist = Math.Clamp((e.Def.Resistances?.GetValueOrDefault(kind) ?? 0f) + e.BonusResist, -300f, 100f);
            if (kind == DamageKind.Fire) resist -= FireExposureStacks(e); // Scorched Earth shred
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
    private List<(DamageKind kind, float amount)> RollEnemyDamage(ServerEnemy e)
    {
        var def = e.Def;
        var list = new List<(DamageKind, float)>();
        if (def.DamageTypes is { Count: > 0 })
            foreach (var (kind, avg) in def.DamageTypes)
                list.Add((kind, Roll(avg * 0.8f, avg * 1.2f) * e.DamageScale));
        else
            list.Add((def.Ranged ? DamageKind.Fire : DamageKind.Blunt, def.Damage * e.DamageScale));
        return list;
    }

    private void HitEnemy(ServerEnemy e, float damage, int byPlayer, string skillId, DamageKind kind)
        => DamageEnemy(e, damage, byPlayer, skillId, kind);

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
            bool changed = GrantCharacterXp(killer, e.Def.XpReward * e.XpScale);
            var skill = e.LastHitSkillId != null ? killer.Character.GetSkill(e.LastHitSkillId) : null;
            if (skill != null)
                changed |= GrantSkillXp(killer, skill, e.Def.XpReward * e.XpScale);
            if (changed) _events.CharacterChanged(killer);
        }

        // Elites roll the loot table twice; the boss's own table already guarantees
        // an item + both scroll types per roll, so its double roll is the reward burst.
        int lootRolls = e.Affixes == EliteAffix.None ? 1 : 2;
        for (int roll = 0; roll < lootRolls; roll++)
            foreach (var item in Loot.RollDrops(e.Def.LootTableId, e.Def.Level))
                SpawnDrop(item, e.Position, e.Height);

        // Gold drop, scaled by enemy level.
        var table = Data.GetLootTable(e.Def.LootTableId);
        if (_rng.NextDouble() < table.GoldDropChance)
        {
            int amount = _rng.Next(table.GoldMin, table.GoldMax + 1);
            amount = Math.Max(1, (int)(amount * (1f + 0.25f * (e.Def.Level - 1))));
            SpawnGoldDrop(amount, e.Position, e.Height);
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
        // XP only ACCRUES here — leveling is a deliberate Skill Menu action
        // (LevelSkill below), so skills can never over-level by accident.
        if (skill.Level >= SkillMath.MaxSkillLevel) return false;
        skill.Experience += xp;
        return true;
    }

    /// <summary>Spend banked skill XP on a level (the Skill Menu's Level Up button).</summary>
    public void LevelSkill(int playerId, string skillId)
    {
        if (!Players.TryGetValue(playerId, out var p)) return;
        var skill = p.Character.GetSkill(skillId);
        if (skill == null || skill.Level >= SkillMath.MaxSkillLevel) return;
        float need = SkillMath.XpToNextLevel(skill.Level);
        if (skill.Experience < need) return;
        skill.Experience -= need;
        skill.Level++;
        _events.CharacterChanged(p);
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

    public void SpawnDrop(ItemInstance item, Vector2 pos, float height = 0f)
    {
        var drop = new WorldItem { Position = Jitter(pos), Item = item, Height = height };
        Drops[drop.DropId] = drop;
        _events.WorldItemSpawned(drop);
    }

    public void SpawnGoldDrop(int amount, Vector2 pos, float height = 0f)
    {
        var drop = new WorldItem { Position = Jitter(pos), GoldAmount = amount, Height = height };
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
        if (Vector2.Distance(p.Position, drop.Position) > ServerPlayer.PickupRange ||
            MathF.Abs(drop.Height - p.Height) > 0.75f)
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
