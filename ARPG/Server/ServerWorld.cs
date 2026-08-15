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
    void EnemySlammed(ServerEnemy e, float radius, byte phase);
    /// <summary>Telegraphed dash charge: phase 1 = ground line telegraph, 2 = launch.</summary>
    void EnemyDashed(ServerEnemy e, byte phase);
    /// <summary>Telegraphed melee swing: phase 1 = wind-up started, 2 = swing resolved.</summary>
    void EnemyAttacked(ServerEnemy e, byte phase, Vector2 dir);
    /// <summary>A summon attacked (arrow loosed / sword swung) — drives client animation.</summary>
    void SummonAttacked(ServerSummon s, Vector2 dir);
    /// <summary>This player's current merchant stock (sent on shop open and after a buy).</summary>
    void ShopStockFor(ServerPlayer p, int npcId, IReadOnlyList<ShopEntry> stock);
    /// <summary>A one-shot or timed world visual ("zap" bursts, "firepatch" ground fire).</summary>
    void WorldEffect(string kind, Vector2 position, float radius, float duration, float height);
    void SummonSpawned(ServerSummon s);
    void SummonDespawned(ServerSummon s);
    /// <summary>The world swapped to a different map (campaign transition). The transport
    /// broadcasts the new map + a fresh snapshot (npcs, chests, summons, zone state).</summary>
    void MapChanged(ServerWorld world);
    /// <summary>Campaign zone state changed (ready count, boss gate, loop/level).</summary>
    void ZoneStateChanged(ServerWorld world);
    /// <summary>A chest opened (replicate the popped lid).</summary>
    void ChestChanged(ServerChest chest);
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
    public GameMap Map { get; private set; }
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

    private const int MaxEnemies = 100;
    private const float RespawnDelay = 20f;
    private const float PlayerRespawnDelay = 3f;

    // ------------------------------------------------------------------ campaign state
    //
    // The playable game loop: players start in the HUB (a small sanctum with the two
    // merchants, starter chests and the run door), then clear THREE generated forest
    // maps in a row — the third ends in the Gravelord, whose death unlocks the door
    // home. Every return trip into the forest generates three NEW maps and raises the
    // enemy level by 3. The old demo slice lives on as Arena mode for tests/debug.

    /// <summary>True when this world runs the hub-and-runs game loop (Arena otherwise).</summary>
    public bool Campaign { get; }
    /// <summary>Which forest excursion this is (1-based). Drives enemy level scaling.</summary>
    public int Loop { get; private set; } = 1;
    /// <summary>0 = hub, 1..3 = the run's forest maps.</summary>
    public int MapIndex { get; private set; }
    public int CampaignEnemyLevel => 1 + 3 * (Loop - 1);
    /// <summary>The final map's exit stays sealed while the boss lives.</summary>
    public bool ExitLocked => Campaign && MapIndex == 3 && BossAlive;
    public bool BossAlive => _bossEnemyId >= 0 &&
                             Enemies.TryGetValue(_bossEnemyId, out var b) && !b.Dead;
    public int ReadyCount => _readyAtDoor.Count;
    /// <summary>Openable starter-gear chests (hub only; opened state persists per run).</summary>
    public readonly List<ServerChest> Chests = new();

    private readonly int _runSeed;
    private readonly ZoneTheme _theme;
    private readonly HashSet<int> _readyAtDoor = new();
    private readonly HashSet<int> _openedChests = new();
    private int _bossEnemyId = -1;
    private int _forestEntries;

    public ServerWorld(GameData data, int mapSeed, IServerEvents events, string zoneThemeId = null,
        bool campaign = false)
    {
        Data = data;
        var theme = data.ZoneThemes.FirstOrDefault(t => t.Id == zoneThemeId)
                    ?? data.ZoneThemes.FirstOrDefault();
        _theme = theme;
        _runSeed = mapSeed;
        _events = events;
        _rng = new Random();
        Loot = new LootGenerator(data, _rng);
        Campaign = campaign;

        if (campaign)
        {
            Map = new GameMap(CampaignMapSeed(0), theme, MapKind.Hub);
            SetupHub();
            return;
        }

        Map = new GameMap(mapSeed, theme);

        // A few roaming spawners on the open ground for ambient danger...
        var types = new[] { "grunt", "spitter", "bone_knight" };
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
        Packs.Add(new PackSpawner // knight patrol in the far southeast, away from the spawn
        {
            Position = new Vector2(36.5f, 36.5f),
            Entries = new[] { ("bone_knight", 2) },
            ScatterRadius = 1.2f,
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

    // ------------------------------------------------------------------ campaign flow

    /// <summary>Deterministic per-map seed (NOT HashCode.Combine — that's randomized per
    /// process, and the same run seed must reproduce the same maps run after run).</summary>
    private int CampaignMapSeed(int mapIndex) =>
        unchecked(_runSeed * 31 + Loop * 7919 + mapIndex * 104729);

    /// <summary>The hub sanctum: two merchants, the starter chests (opened state
    /// persists across returns) and the run door.</summary>
    private void SetupHub()
    {
        Spawners.Clear();
        Packs.Clear();
        Npcs.Clear();
        Chests.Clear();
        _bossEnemyId = -1;
        if (Data.Npcs.ContainsKey("merchant") && Map.NpcSpots.Count > 0)
            Npcs.Add(new ServerNpc
            {
                Id = 1, TypeId = "merchant", Position = Map.NpcSpots[0],
                Height = Map.GroundHeightAt(Map.NpcSpots[0]),
            });
        if (Data.Npcs.ContainsKey("skill_trainer") && Map.NpcSpots.Count > 1)
            Npcs.Add(new ServerNpc
            {
                Id = 2, TypeId = "skill_trainer", Position = Map.NpcSpots[1],
                Height = Map.GroundHeightAt(Map.NpcSpots[1]),
            });
        for (int i = 0; i < Map.ChestSpots.Count; i++)
            Chests.Add(new ServerChest
            {
                Id = i + 1,
                Position = Map.ChestSpots[i],
                Height = Map.GroundHeightAt(Map.ChestSpots[i]),
                Opened = _openedChests.Contains(i + 1),
            });
    }

    /// <summary>A run map: generation-placed packs along the corridor and on the
    /// overlooks, spawned ONCE (no respawning). The third map adds the Gravelord's
    /// arena pack by the exit; its death unlocks the way home. From the second loop
    /// on, Graveguard skeletons (Barrow Knights) join the pack mixes.</summary>
    private void SetupForest()
    {
        Spawners.Clear();
        Packs.Clear();
        Npcs.Clear();
        Chests.Clear();
        _bossEnemyId = -1;
        int level = CampaignEnemyLevel;
        bool knights = Loop >= 2;
        var packRng = new Random(Map.Seed ^ 0x5041434B); // "PACK" — deterministic per map
        foreach (var spot in Map.PackSpots)
        {
            (string, int)[] entries = packRng.Next(knights ? 4 : 3) switch
            {
                0 => new[] { ("grunt", 3) },
                1 => new[] { ("grunt", 2), ("spitter", 1) },
                2 => new[] { ("grunt", 1), ("spitter", 2) },
                _ => new[] { ("bone_knight", 2), ("grunt", 1) },
            };
            var affix = packRng.Next(5) switch
            {
                0 => EliteAffix.Brutish,
                1 => EliteAffix.Swift,
                2 => EliteAffix.Warded,
                _ => EliteAffix.None,
            };
            Packs.Add(new PackSpawner
            {
                Position = spot,
                Entries = entries,
                LeaderAffixes = affix,
                EnemyLevel = level,
                NoRespawn = true,
            });
        }
        foreach (var overlook in Map.OverlookSpots)
            Packs.Add(new PackSpawner
            {
                Position = overlook,
                Entries = knights
                    ? new[] { ("spitter", 2), ("bone_knight", 1) }
                    : new[] { ("spitter", 2) },
                EnemyLevel = level,
                ScatterRadius = 0.9f,
                NoRespawn = true,
            });
        // From the second loop on, Graveguard skeletons are a PROMISE, not a dice
        // roll — if the mixes above happened not to roll any, station a patrol.
        if (knights && Map.PackSpots.Count > 0 &&
            !Packs.Any(pk => pk.Entries.Any(en => en.typeId == "bone_knight")))
            Packs.Add(new PackSpawner
            {
                Position = Map.PackSpots[Map.PackSpots.Count / 2],
                Entries = new[] { ("bone_knight", 2) },
                EnemyLevel = level,
                ScatterRadius = 1.1f,
                NoRespawn = true,
            });
        if (MapIndex == 3)
            Packs.Add(new PackSpawner
            {
                Position = Map.BossSpot,
                Entries = knights
                    ? new[] { ("gravelord", 1), ("bone_knight", 2) }
                    : new[] { ("gravelord", 1), ("grunt", 2) },
                LeaderAffixes = EliteAffix.Boss,
                EnemyLevel = Math.Max(Data.Enemies["gravelord"].Level, level + 3),
                ScatterRadius = 1.8f,
                NoRespawn = true,
            });

        // Spawn everything NOW (after the MapChanged broadcast the transport sends the
        // spawns in order), and remember which enemy is the door-sealing boss.
        for (int pi = 0; pi < Packs.Count; pi++)
            SpawnPackMembers(Packs[pi], pi);
        _bossEnemyId = Enemies.Values
            .FirstOrDefault(e => !e.Dead && e.Def.Id == "gravelord")?.Id ?? -1;
    }

    /// <summary>Swap the world onto another campaign map and move everyone there.</summary>
    private void TransitionTo(int newIndex)
    {
        if (!Campaign) return;
        _readyAtDoor.Clear();
        Enemies.Clear();       // clients wipe on MapChange — no death broadcasts needed
        Projectiles.Clear();
        Drops.Clear();
        _windups.Clear();
        _flow.Clear();
        _rallyFields.Clear();
        foreach (var p in Players.Values) p.SummonRallies.Clear();

        // Re-entering the forest from the hub starts a NEW excursion: three fresh
        // maps, enemy level up 3 per loop.
        if (newIndex == 1) Loop = ++_forestEntries;
        MapIndex = newIndex;
        Map = new GameMap(CampaignMapSeed(newIndex), _theme,
            newIndex == 0 ? MapKind.Hub : MapKind.Forest);

        // Everyone arrives together at the new map's spawn (dead players are pulled
        // through on their feet — the run moves as a group).
        int slot = 0;
        foreach (var p in Players.Values)
        {
            var offset = new Vector2((slot % 3) * 0.9f - 0.9f, (slot / 3) * 0.9f);
            slot++;
            p.Position = Map.PlayerSpawn + offset;
            p.Height = Map.GroundHeightAt(p.Position);
            p.IgnoreStateUntil = Time + 0.5f; // in-flight old-map state packets can't yank us back
            if (!p.Alive)
            {
                p.Health = p.Stats.MaxHealth * 0.5f;
                _events.PlayerRespawned(p);
            }
            _events.PlayerHealthChanged(p);
        }
        foreach (var s in Summons.Values)
        {
            if (!Players.TryGetValue(s.OwnerId, out var owner)) continue;
            s.Position = owner.Position + new Vector2(
                (float)(_rng.NextDouble() - 0.5) * 1.2f, (float)(_rng.NextDouble() - 0.5) * 1.2f);
            s.Height = owner.Height;
        }

        // Clear map furniture BEFORE the broadcast — MapChanged snapshots whatever is
        // in these lists, and a forest must not re-send the hub's merchants.
        Spawners.Clear();
        Packs.Clear();
        Npcs.Clear();
        Chests.Clear();
        if (newIndex == 0) SetupHub();
        _events.MapChanged(this);
        if (newIndex != 0) SetupForest(); // packs spawn AFTER the map broadcast
        _events.ZoneStateChanged(this);
    }

    /// <summary>The exit door: standing near it, the interact key toggles READY. When
    /// every living player is ready the group moves on (hub -> map 1 -> 2 -> 3 -> hub).
    /// The final map's door refuses while the boss lives.</summary>
    public void DoorReady(int playerId)
    {
        if (!Campaign || !Players.TryGetValue(playerId, out var p) || !p.Alive) return;
        if (Vector2.Distance(p.Position, Map.ExitDoor) > 2.6f) return;
        if (ExitLocked)
        {
            _events.MessageFor(p, "The way is sealed — the Gravelord still stands.");
            return;
        }
        bool nowReady = _readyAtDoor.Add(playerId);
        if (!nowReady) _readyAtDoor.Remove(playerId);
        int alive = Players.Values.Count(pl => pl.Alive);
        foreach (var pl in Players.Values)
            _events.MessageFor(pl,
                $"{p.Name} is {(nowReady ? "ready" : "no longer ready")} at the door ({_readyAtDoor.Count}/{alive}).");
        _events.ZoneStateChanged(this);
        if (alive > 0 && _readyAtDoor.Count >= alive)
            TransitionTo(MapIndex >= 3 ? 0 : MapIndex + 1);
    }

    /// <summary>The equipped flask ITEM matching the requested kind (health or mana)
    /// that still has charges. Checks both flask slots so the pair can be arranged
    /// either way round.</summary>
    private (ItemInstance item, ItemBase itemBase) EquippedFlask(ServerPlayer p, bool health)
    {
        foreach (var slot in new[] { EquipSlot.Flask1, EquipSlot.Flask2 })
        {
            var it = p.Character.Equipment.GetValueOrDefault(slot);
            var b = it?.GetBase(Data);
            if (b is not { Category: ItemCategory.Flask }) continue;
            if (health ? b.FlaskHeal <= 0 : b.FlaskMana <= 0) continue;
            if (it.FlaskCharges <= 0) continue;
            return (it, b);
        }
        return (null, null);
    }

    /// <summary>Drink an equipped flask (0 = health, 1 = mana): consumes one of the
    /// ITEM's charges and starts a restore-over-TIME tick — an ARPG sip, not an
    /// instant heal. Charges never regenerate; the sanctum fountain refills them.</summary>
    public void UsePotion(int playerId, byte kind)
    {
        if (!Players.TryGetValue(playerId, out var p) || !p.Alive) return;
        if (kind == 0)
        {
            if (Time < p.PotionHealUntil) return; // already drinking
            var (flask, fb) = EquippedFlask(p, health: true);
            if (flask == null)
            {
                _events.MessageFor(p, "No health flask ready — refill at the sanctum fountain.");
                return;
            }
            if (p.Health >= p.Stats.MaxHealth - 0.5f) return; // nothing to heal
            flask.FlaskCharges--;
            p.PotionHealUntil = Time + fb.FlaskDuration;
            p.PotionHealPerSec = fb.FlaskHeal / fb.FlaskDuration;
        }
        else
        {
            if (Time < p.PotionManaUntil) return;
            var (flask, fb) = EquippedFlask(p, health: false);
            if (flask == null)
            {
                _events.MessageFor(p, "No mana flask ready — refill at the sanctum fountain.");
                return;
            }
            float ceiling = MathF.Max(0f, p.Stats.MaxMana - p.ManaReserved);
            if (p.Mana >= ceiling - 0.5f) return;
            flask.FlaskCharges--;
            p.PotionManaUntil = Time + fb.FlaskDuration;
            p.PotionManaPerSec = fb.FlaskMana / fb.FlaskDuration;
        }
        _events.CharacterChanged(p);
        _events.PlayerHealthChanged(p);
    }

    /// <summary>The sanctum fountain: refills every flask the player carries (equipped
    /// or bagged) back to full. Hub only, and only when standing beside the basin.</summary>
    public void UseFountain(int playerId)
    {
        if (!Players.TryGetValue(playerId, out var p) || !p.Alive) return;
        if (Map.Kind != MapKind.Hub) return;
        if (Vector2.Distance(p.Position, Map.FountainSpot) > 2.6f) return;
        bool refilled = false;
        foreach (var it in p.Character.Equipment.Values
                     .Concat(p.Character.Inventory.Items.Select(pl => pl.Item)))
        {
            if (it?.GetBase(Data) is not { Category: ItemCategory.Flask } fb) continue;
            if (it.FlaskCharges < fb.FlaskChargesMax) { it.FlaskCharges = fb.FlaskChargesMax; refilled = true; }
        }
        if (refilled)
        {
            _events.MessageFor(p, "The fountain's water restores your flasks.");
            _events.CharacterChanged(p);
            _events.WorldEffect("hit", Map.FountainSpot, 0.6f, 0.4f, p.Height);
        }
        else
            _events.MessageFor(p, "Your flasks are already full.");
    }

    /// <summary>Open a hub chest: the lid pops for everyone and the starter gear inside
    /// drops on the ground. Once per chest per RUN SESSION — no farming the sanctum.</summary>
    public void OpenChest(int playerId, int chestId)
    {
        if (!Players.TryGetValue(playerId, out var p) || !p.Alive) return;
        var chest = Chests.FirstOrDefault(c => c.Id == chestId);
        if (chest == null || chest.Opened) return;
        if (Vector2.Distance(p.Position, chest.Position) > 2.4f) return;
        chest.Opened = true;
        _openedChests.Add(chest.Id);
        var table = Data.GetLootTable("default");
        for (int i = 0; i < 2; i++)
        {
            var item = Loot.GenerateEquipment(table, itemLevel: 1, forcedRarity: ItemRarity.Normal);
            if (item != null)
                SpawnDrop(item, chest.Position + new Vector2(0.4f + 0.5f * i, 0.55f), chest.Height);
        }
        _events.ChestChanged(chest);
        _events.WorldEffect("hit", chest.Position, 0.5f, 0.3f, chest.Height);
    }

    /// <summary>Player minions (skeleton archers), by id.</summary>
    public readonly Dictionary<int, ServerSummon> Summons = new();
    private int _nextSummonId = 1;
    private class SummonRespawn
    {
        public int OwnerId;
        public string SkillId;
        public float At;
        /// <summary>The dead minion's mana reservation carries through its respawn wait.</summary>
        public float ManaReserved;
    }
    private readonly List<SummonRespawn> _summonRespawns = new();

    // ------------------------------------------------------------------ players

    public ServerPlayer AddPlayer(int id, string name, CharacterData character)
    {
        character ??= CharacterData.CreateNew(Data, name);
        character.Name = name;
        // Migrate items from older saves: derive per-side slot caps and stack counts.
        foreach (var placed in character.Inventory.Items) placed.Item.EnsureSlotData();
        foreach (var equipped in character.Equipment.Values) equipped?.EnsureSlotData();
        // Flask migration + session top-up: pre-flask saves get the starter pair, and
        // every carried flask starts the session FULL (mid-session refills come only
        // from the sanctum fountain).
        bool hasAnyFlask =
            character.Equipment.Values.Any(it => it?.GetBase(Data)?.Category == ItemCategory.Flask) ||
            character.Inventory.Items.Any(pl => pl.Item.GetBase(Data)?.Category == ItemCategory.Flask);
        if (!hasAnyFlask && Data.Items.ContainsKey("minor_health_flask"))
        {
            character.Equipment[EquipSlot.Flask1] = new ItemInstance
            {
                BaseItemId = "minor_health_flask", ItemLevel = 1, Rarity = ItemRarity.Normal,
            };
            character.Equipment[EquipSlot.Flask2] = new ItemInstance
            {
                BaseItemId = "minor_mana_flask", ItemLevel = 1, Rarity = ItemRarity.Normal,
            };
        }
        foreach (var it in character.Equipment.Values
                     .Concat(character.Inventory.Items.Select(pl => pl.Item)))
            if (it?.GetBase(Data) is { Category: ItemCategory.Flask } fb)
                it.FlaskCharges = fb.FlaskChargesMax;
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
        p.EnergyShield = p.Stats.MaxEnergyShield;
        p.LastSyncedHealth = p.Health;
        p.LastSyncedMana = p.Mana;
        p.LastSyncedEnergyShield = p.EnergyShield;
        Players[id] = p;
        return p;
    }

    public void RemovePlayer(int id)
    {
        Players.Remove(id);
        _flow.Remove(id);
        foreach (var key in _rallyFields.Keys.Where(k => k.playerId == id).ToList())
            _rallyFields.Remove(key);
    }

    /// <summary>Movement is client-computed for responsiveness; the server sanity-clamps it.
    /// All important results (damage, loot, items) remain host-authoritative.</summary>
    public void UpdatePlayerState(int id, Vector2 pos, Vector2 facing, float height)
    {
        if (!Players.TryGetValue(id, out var p) || !p.Alive) return;
        // Right after a map transition the client is still sending OLD-map coordinates
        // until its MapChange arrives; hold the server position until then.
        if (Time < p.IgnoreStateUntil) return;
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
                    p.Mana = MathF.Max(0f, p.Stats.MaxMana - p.ManaReserved);
                    p.EnergyShield = p.Stats.MaxEnergyShield;
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
            // Potion flasks: restore-over-time ticks (never instant). Health caps at
            // max; mana respects the reservation ceiling like everything else.
            if (p.Alive && Time < p.PotionHealUntil && p.Health < p.Stats.MaxHealth)
            {
                p.Health = MathF.Min(p.Stats.MaxHealth, p.Health + p.PotionHealPerSec * dt);
                if (MathF.Abs(p.Health - p.LastSyncedHealth) >= 1f || p.Health >= p.Stats.MaxHealth)
                    resourceChanged = true;
            }
            if (p.Alive && Time < p.PotionManaUntil)
                p.Mana += p.PotionManaPerSec * dt; // clamped by the ceiling just below

            // Mana regenerates only into the UNRESERVED pool (summons hold the rest).
            float manaCeiling = MathF.Max(0f, p.Stats.MaxMana - p.ManaReserved);
            if (p.Mana > manaCeiling)
            {
                p.Mana = manaCeiling; // gear/reservation changes shrink the pool live
                resourceChanged = true;
            }
            else if (p.Stats.ManaRegeneration > 0 && p.Mana < manaCeiling)
            {
                p.Mana = MathF.Min(manaCeiling, p.Mana + p.Stats.ManaRegeneration * dt);
                if (MathF.Abs(p.Mana - p.LastSyncedMana) >= 1f || p.Mana >= manaCeiling)
                    resourceChanged = true;
            }
            // Energy Shield recharge: kicks in after RechargeDelay seconds without taking
            // damage (any hit resets the timer), refilling at a %-of-max rate. All the
            // knobs live in EnergyShieldBalance for the balance pass.
            if (p.Stats.MaxEnergyShield > 0 && p.EnergyShield < p.Stats.MaxEnergyShield &&
                Time - p.LastDamagedAt >= Stats.EnergyShieldBalance.RechargeDelay)
            {
                p.EnergyShield = MathF.Min(p.Stats.MaxEnergyShield,
                    p.EnergyShield + p.Stats.MaxEnergyShield *
                    Stats.EnergyShieldBalance.RechargePctPerSecond / 100f * dt);
                if (MathF.Abs(p.EnergyShield - p.LastSyncedEnergyShield) >= 1f ||
                    p.EnergyShield >= p.Stats.MaxEnergyShield)
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
            if (pack.NoRespawn && pack.Spawned) continue; // placed encounters stay cleared
            if (pack.RespawnAt <= 0)
            {
                pack.RespawnAt = Time <= 0.1f ? Time : Time + pack.RespawnDelay;
            }
            if (Time < pack.RespawnAt) continue;
            pack.RespawnAt = 0;
            SpawnPackMembers(pack, pi);
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
                var enemy = SpawnEnemy(spawner.EnemyTypeId, spawner.Position, level: spawner.EnemyLevel);
                spawner.AliveEnemyId = enemy.Id;
                spawner.RespawnAt = 0;
                alive++;
            }
        }
    }

    /// <summary>Spawn one pack's full roster scattered around its anchor (first member
    /// carries the leader affixes).</summary>
    private void SpawnPackMembers(PackSpawner pack, int pi)
    {
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
                var member = SpawnEnemy(typeId, pos, affixes, pi, pack.EnemyLevel);
                pack.AliveIds.Add(member.Id);
            }
        pack.Spawned = true;
    }

    public ServerEnemy SpawnEnemy(string typeId, Vector2 pos, EliteAffix affixes = EliteAffix.None,
        int packId = -1, int level = 0)
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
            Level = level > 0 ? level : def.Level,
        };
        // Level override: any enemy type can serve a later zone — its numbers scale
        // from the def's native level through the central EnemyLevelScaling curves.
        int levelsAbove = e.Level - def.Level;
        if (levelsAbove > 0)
        {
            e.MaxHealth *= Stats.EnemyLevelScaling.Health(levelsAbove);
            e.DamageScale *= Stats.EnemyLevelScaling.Damage(levelsAbove);
            e.XpScale *= Stats.EnemyLevelScaling.Xp(levelsAbove);
        }
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

            if (Time < e.StunnedUntil || Time < e.FrozenUntil)
            {
                // A stun or freeze mid-wind-up cancels the swing outright (the cooldown
                // was already committed at wind-up start, so interrupts genuinely deny
                // the hit instead of merely delaying it). Same for a telegraphed slam
                // or dash — interrupted charges just stop.
                e.Winding = false;
                e.SlamResolveAt = 0;
                e.DashPrepareUntil = 0;
                e.DashUntil = 0;
                continue; // no movement, no attacks
            }

            // Committed to a telegraphed dash: stand dead still while the ground line
            // burns, then barrel down it — heavy contact damage to anyone still on it.
            if (e.DashPrepareUntil > 0)
            {
                if (Time < e.DashPrepareUntil) continue;
                e.DashPrepareUntil = 0;
                e.DashUntil = Time + e.Def.DashRange / MathF.Max(1f, e.Def.DashSpeed);
                e.DashHitIds.Clear();
                _events.EnemyDashed(e, phase: 2);
            }
            if (e.DashUntil > Time)
            {
                float step = e.Def.DashSpeed * dt;
                var before = e.Position;
                float dh = e.Height;
                e.Position = Map.MoveWithCollision(e.Position, e.DashDir * step, e.Def.Radius, ref dh);
                e.Height = dh;
                if (Vector2.Distance(before, e.Position) < step * 0.35f)
                    e.DashUntil = 0; // slammed into terrain — the charge ends there
                foreach (var victim in Players.Values)
                {
                    if (!victim.Alive || Time < victim.InvulnerableUntil) continue;
                    if (e.DashHitIds.Contains(victim.Id)) continue;
                    if (MathF.Abs(victim.Height - e.Height) > 0.75f) continue;
                    if (Vector2.Distance(victim.Position, e.Position) >
                        e.Def.Radius + ServerPlayer.Radius + 0.3f) continue;
                    e.DashHitIds.Add(victim.Id);
                    // A body-charge, not a swung Attack — never deflectable.
                    DamagePlayerTyped(victim, new List<(DamageKind, float)>
                        { (DamageKind.Blunt, e.Def.DashDamage * e.DamageScale) }, attackHit: false);
                    var aside = (victim.Position - e.Position).NormalizedOrZero();
                    if (aside == Vector2.Zero) aside = new Vector2(-e.DashDir.Y, e.DashDir.X);
                    float kh2 = victim.Height;
                    victim.Position = Map.MoveWithCollision(victim.Position, aside * 1.2f, ServerPlayer.Radius, ref kh2);
                    victim.Height = kh2;
                }
                continue; // dashing: nothing else this tick
            }

            // Committed to a telegraphed ground slam: the boss holds still while the
            // red warning decal fills, then the shockwave hits whoever is still inside
            // the circle — standing in it is a choice.
            if (e.SlamResolveAt > 0)
            {
                if (Time < e.SlamResolveAt) continue;
                e.SlamResolveAt = 0;
                _events.EnemySlammed(e, e.Def.SlamRadius, phase: 2);
                foreach (var victim in Players.Values)
                {
                    if (!victim.Alive || Time < victim.InvulnerableUntil) continue;
                    if (MathF.Abs(victim.Height - e.Height) > 0.75f) continue;
                    if (Vector2.Distance(victim.Position, e.Position) > e.Def.SlamRadius) continue;
                    // A ground slam is an AoE, not a direct Attack — never deflectable.
                    DamagePlayerTyped(victim, new List<(DamageKind, float)>
                        { (DamageKind.Blunt, e.Def.SlamDamage * e.DamageScale) }, attackHit: false);
                    var away = (victim.Position - e.Position).NormalizedOrZero();
                    float kh = victim.Height;
                    victim.Position = Map.MoveWithCollision(victim.Position, away * 2.0f, ServerPlayer.Radius, ref kh);
                    victim.Height = kh;
                }
                continue;
            }

            // Committed to a telegraphed swing: stand still until it resolves. Sword
            // wielders keep re-aiming at their victim until just before impact; lunge
            // attackers locked their direction at wind-up start (strafing beats them).
            if (e.Winding)
            {
                if (e.Def.AttackTracks && Time < e.WindupUntil - 0.08f)
                {
                    Vector2? aim = null;
                    if (e.WindupSummonId >= 0 && Summons.TryGetValue(e.WindupSummonId, out var trackS))
                        aim = trackS.Position;
                    else if (e.WindupPlayerId >= 0 && Players.TryGetValue(e.WindupPlayerId, out var trackP) && trackP.Alive)
                        aim = trackP.Position;
                    if (aim.HasValue)
                    {
                        var newDir = (aim.Value - e.Position).NormalizedOrZero();
                        if (newDir != Vector2.Zero) e.WindupDir = newDir;
                    }
                }
                if (Time >= e.WindupUntil) ResolveEnemySwing(e);
                continue;
            }
            if (Time < e.RecoverUntil) continue; // post-swing opening: punish window

            // Aggro/chase run on PATH distance (flow field) for players, so climbing a
            // ramp no longer drops aggro. SUMMONS are first-class targets with the SAME
            // aggro rules (euclidean, same surface) — a skeleton pack fighting far from
            // its owner gets fought back, exactly like a player would.
            var (target, pathDist) = FindTarget(e);
            float dist = target != null ? Vector2.Distance(e.Position, target.Position) : float.MaxValue;
            bool sameSurface = target != null && MathF.Abs(target.Height - e.Height) <= 0.75f;
            var meat = NearestSummonNear(e.Position, e.Def.AggroRange * 1.5f, e.Height);
            float meatDist = meat != null ? Vector2.Distance(e.Position, meat.Position) : float.MaxValue;
            // Pursue whichever threat is closer once aggroed.
            bool pursueMeat = meat != null && (target == null || meatDist < dist);

            // Telegraphed dash (scaled bosses only — DashMinLevel gates it to the
            // campaign's loop-2+ Gravelord): an engaged MID-RANGE target on a clear
            // line commits the charge. Direction LOCKS at prepare start, so stepping
            // off the burning line dodges the whole thing.
            if (e.Def.DashDamage > 0 && e.Level >= e.Def.DashMinLevel &&
                e.State != EnemyState.Idle && Time >= e.DashReadyAt &&
                target != null && sameSurface &&
                dist >= 2.2f && dist <= e.Def.DashRange &&
                !Map.SegmentBlocked(e.Position, target.Position, e.Height + 0.5f))
            {
                e.DashReadyAt = Time + e.Def.DashCooldown;
                e.DashPrepareUntil = Time + e.Def.DashWindup;
                e.DashDir = (target.Position - e.Position).NormalizedOrZero();
                _events.EnemyDashed(e, phase: 1);
                continue; // rooted in the prepare stance this tick
            }

            // Boss reinforcements: an ENGAGED summoner conjures its adds on a long
            // clock. The first delay arms when it first engages, so the fight never
            // opens with the summon.
            if (e.Def.AddSpawnType.Length > 0 && e.State != EnemyState.Idle &&
                (target != null || meat != null))
            {
                if (e.NextAddSpawnAt <= 0)
                {
                    e.NextAddSpawnAt = Time + e.Def.AddSpawnFirstDelay;
                }
                else if (Time >= e.NextAddSpawnAt)
                {
                    e.NextAddSpawnAt = Time + e.Def.AddSpawnCooldown;
                    _events.WorldEffect("burst", e.Position, 1.6f, 0.4f, e.Height);
                    for (int ai = 0; ai < e.Def.AddSpawnCount; ai++)
                    {
                        float ang = ai * MathF.Tau / e.Def.AddSpawnCount + 0.6f;
                        var apos = e.Position + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * 1.7f;
                        if (Map.CircleHitsWall(apos, 0.4f)) apos = e.Position;
                        var add = SpawnEnemy(e.Def.AddSpawnType, apos, level: e.Level);
                        add.State = EnemyState.Chase; // raised mid-fight: already angry
                        add.TargetPlayerId = e.TargetPlayerId;
                    }
                }
            }

            switch (e.State)
            {
                case EnemyState.Idle:
                    if ((target != null && pathDist <= e.Def.AggroRange) || meatDist <= e.Def.AggroRange)
                    {
                        e.State = EnemyState.Chase;
                        e.TargetPlayerId = target?.Id ?? -1;
                        if (target != null) AlertPack(e, target);
                    }
                    break;

                case EnemyState.Chase:
                    bool playerHeld = target != null && pathDist <= e.Def.AggroRange * 1.5f;
                    bool meatHeld = meatDist <= e.Def.AggroRange * 1.5f;
                    if (!playerHeld && !meatHeld)
                    {
                        e.State = EnemyState.Idle;
                        e.TargetPlayerId = -1;
                        break;
                    }
                    e.TargetPlayerId = target?.Id ?? -1;
                    if (pursueMeat && meatHeld)
                    {
                        if (meatDist <= e.Def.AttackRange)
                        {
                            e.State = EnemyState.Attack;
                            break;
                        }
                        MoveEnemyToward(e, meat.Position, dt);
                        break;
                    }
                    if (dist <= e.Def.AttackRange && CanAttack(e, target, sameSurface))
                    {
                        e.State = EnemyState.Attack;
                        break;
                    }
                    if (playerHeld) MoveEnemyToward(e, ChaseWaypoint(e, target), dt);
                    else if (meatHeld) MoveEnemyToward(e, meat.Position, dt);
                    break;

                case EnemyState.Attack:
                    bool meatEngaged = meatDist <= e.Def.AttackRange * 1.15f;
                    bool playerEngaged = target != null && dist <= e.Def.AttackRange * 1.15f &&
                                         CanAttack(e, target, sameSurface);
                    if (!meatEngaged && !playerEngaged)
                    {
                        e.State = target == null && meat == null ? EnemyState.Idle : EnemyState.Chase;
                        break;
                    }
                    // Boss ground slam: telegraphed — commit the cooldown and start the
                    // wind-up now; the red warning decal broadcasts to every client and
                    // the damage resolves at wind-up end against RESOLVE-time positions
                    // (walk out of the circle and the shockwave misses you).
                    if (e.Def.SlamRadius > 0 && Time >= e.SlamReadyAt && dist <= e.Def.SlamRadius * 0.85f)
                    {
                        e.SlamReadyAt = Time + e.Def.SlamCooldown;
                        e.SlamResolveAt = Time + e.Def.SlamWindup;
                        _events.EnemySlammed(e, e.Def.SlamRadius, phase: 1);
                        break;
                    }
                    if (Time >= e.AttackReadyAt)
                    {
                        // Strike whichever engaged threat is closer — summon or player.
                        bool hitMeat = meatEngaged && (!playerEngaged || meatDist < dist);
                        if (e.Def.Ranged)
                        {
                            e.AttackReadyAt = Time + e.Def.AttackCooldown * e.CooldownScale;
                            if (hitMeat) SpawnEnemyProjectileAt(e, meat.Position, meat.Height);
                            else SpawnEnemyProjectile(e, target);
                        }
                        else if (hitMeat)
                        {
                            StartEnemySwing(e, meat.Position, victimPlayerId: -1, victimSummonId: meat.Id);
                        }
                        else
                        {
                            StartEnemySwing(e, target.Position, victimPlayerId: target.Id, victimSummonId: -1);
                        }
                    }
                    break;
            }
        }
    }

    /// <summary>Commit a melee enemy to a telegraphed swing: the cooldown is spent NOW
    /// (so a stun that cancels the wind-up truly denies the hit), the direction locks
    /// toward the victim, and clients get the phase-1 event to play the wind-up
    /// animation. Cooldowns get ±12% jitter so packs don't swing in unison.</summary>
    private void StartEnemySwing(ServerEnemy e, Vector2 at, int victimPlayerId, int victimSummonId)
    {
        e.AttackReadyAt = Time + e.Def.AttackCooldown * e.CooldownScale *
                          (0.88f + (float)_rng.NextDouble() * 0.24f);
        e.WindupDir = (at - e.Position).NormalizedOrZero();
        if (e.WindupDir == Vector2.Zero) e.WindupDir = new Vector2(1, 0);
        e.WindupPlayerId = victimPlayerId;
        e.WindupSummonId = victimSummonId;
        if (e.Def.AttackWindup <= 0f)
        {
            // Legacy instant hit (data can opt out of the telegraph).
            _events.EnemyAttacked(e, 2, e.WindupDir);
            e.Winding = true; // ResolveEnemySwing clears it and applies recovery
            ResolveEnemySwing(e, announce: false);
            return;
        }
        e.Winding = true;
        e.WindupUntil = Time + e.Def.AttackWindup;
        _events.EnemyAttacked(e, 1, e.WindupDir);
    }

    /// <summary>The wind-up lands: re-check reach and arc NOW. Whoever is nearest inside
    /// the swing (summons soak before players; dodge i-frames pass through) takes the
    /// hit — an empty arc is a clean whiff, which is what makes dodging feel real.</summary>
    private void ResolveEnemySwing(ServerEnemy e, bool announce = true)
    {
        e.Winding = false;
        e.RecoverUntil = Time + e.Def.AttackRecovery;
        if (announce) _events.EnemyAttacked(e, 2, e.WindupDir);

        float reach = e.Def.AttackRange * 1.25f;
        float cosHalfArc = MathF.Cos(e.Def.AttackArc * 0.5f * MathF.PI / 180f);
        bool InArc(Vector2 vpos)
        {
            var to = vpos - e.Position;
            float d = to.Length();
            if (d > reach) return false;
            if (d < 0.2f) return true; // standing inside the enemy: always caught
            return Vector2.Dot(to / d, e.WindupDir) >= cosHalfArc;
        }

        ServerPlayer hitPlayer = null;
        ServerSummon hitSummon = null;
        float bestDist = float.MaxValue;
        foreach (var v in Players.Values)
        {
            if (!v.Alive || Time < v.InvulnerableUntil) continue; // dodge i-frames win
            if (MathF.Abs(v.Height - e.Height) > 0.75f) continue;
            if (!InArc(v.Position)) continue;
            float d = Vector2.Distance(v.Position, e.Position);
            if (d < bestDist) { bestDist = d; hitPlayer = v; }
        }
        foreach (var s in Summons.Values)
        {
            if (MathF.Abs(s.Height - e.Height) > 0.75f) continue;
            if (!InArc(s.Position)) continue;
            float d = Vector2.Distance(s.Position, e.Position);
            if (d < bestDist) { bestDist = d; hitSummon = s; hitPlayer = null; }
        }
        if (hitSummon != null) DamageSummon(hitSummon, RollEnemyDamage(e).Sum(c => c.amount));
        else if (hitPlayer != null) DamagePlayerTyped(hitPlayer, RollEnemyDamage(e));
        // else: whiff — the dodge worked.
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

    private void RecomputeFlow(ServerPlayer p, FlowField f) =>
        ComputeFlowFrom(NodeOf(p.Position, p.Height), f);

    /// <summary>BFS a flow field from ANY start node (player positions each tick; rally
    /// points once when set) — every stored Next points one hop back toward the start.</summary>
    private void ComputeFlowFrom(int start, FlowField f)
    {
        int w = Map.Width, n = NodeCount;
        f.Dist ??= new ushort[n];
        f.Next ??= new int[n];
        Array.Fill(f.Dist, ushort.MaxValue);
        Array.Fill(f.Next, -1);
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
            AttackHit = !e.Def.ProjectileIsSpell,
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
            var prevPos = pr.Position;
            pr.Position = next;
            pr.Height = nextHeight;

            // Hit tests are SWEPT along this tick's segment, not sampled at the endpoint —
            // a fast projectile can otherwise step clean across a small target and
            // visibly "pass through" it.
            static float SegmentDistance(Vector2 a, Vector2 b, Vector2 point)
            {
                var ab = b - a;
                float lenSq = ab.LengthSquared();
                float t = lenSq < 0.000001f ? 0f : Math.Clamp(Vector2.Dot(point - a, ab) / lenSq, 0f, 1f);
                return Vector2.Distance(a + ab * t, point);
            }

            if (pr.FromPlayer)
            {
                foreach (var e in Enemies.Values)
                {
                    if (e.Dead || MathF.Abs(e.Height - pr.Height) > 0.75f) continue;
                    if (SegmentDistance(prevPos, pr.Position, e.Position) <= e.Def.Radius + 0.25f)
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
                    if (SegmentDistance(prevPos, pr.Position, s2.Position) <= ServerSummon.Radius + 0.25f)
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
                    if (SegmentDistance(prevPos, pr.Position, p.Position) <= ServerPlayer.Radius + 0.25f)
                    {
                        DamagePlayerTyped(p, RollComponentList(pr.MinDamage, pr.MaxDamage, pr.DamageKind, pr.Added),
                            attackHit: pr.AttackHit);
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

        // Instant-target skills (Chain Lightning) with nothing in reach fizzle for FREE:
        // no mana spent, no cooldown committed — a whiffed bolt into empty air costing
        // 11 mana felt like a tax on aiming.
        if (def.Archetype == SkillArchetype.ChainLightning)
        {
            var probe = ClampToRange(p.Position, target, stats.Range);
            bool anyTarget = Enemies.Values.Any(en => !en.Dead &&
                MathF.Abs(en.Height - p.Height) <= 0.75f &&
                Vector2.Distance(en.Position, probe) <= 2.2f);
            if (!anyTarget) return;
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
                effectPoint = SkillMath.MeleeImpactPoint(p.Position, target, p.Facing, stats.Range);
                // Aimed area slams (the "Area" tag — Mace Slam) keep the classic circle
                // around the projected impact point: they're ground bursts you place.
                // Plain SWINGS use a player-centered arc instead: an enemy is hit when
                // it's within the skill's RANGE of the CASTER (plus its own body radius)
                // and inside the swing arc around the aim direction, with point-blank
                // enemies always caught. (The old circle-at-impact-point test reached
                // range+radius ahead of the player yet whiffed adjacent off-axis
                // enemies, which read as wildly inconsistent reach.)
                var aimDir = (target - p.Position).NormalizedOrZero();
                if (aimDir == Vector2.Zero) aimDir = p.Facing;
                var struckList = def.Tags?.Contains("Area") == true
                    ? EnemiesNear(effectPoint, stats.Radius, p.Height)
                    : Enemies.Values.Where(e =>
                    {
                        // The hit test mirrors the VISIBLE weapon sweep: the swing
                        // animation carries the mace through the caster's whole front,
                        // so the arc is the full frontal half-circle (a hair past 180°),
                        // with a touch of reach forgiveness for bodies mid-step and a
                        // generous point-blank ring that always connects. Tighter arcs
                        // read as whiffs through enemies the sprite clearly touched.
                        if (e.Dead || MathF.Abs(e.Height - p.Height) > 0.75f) return false;
                        float edist = Vector2.Distance(e.Position, p.Position);
                        if (edist > stats.Range + e.Def.Radius + 0.15f) return false;
                        if (edist <= 0.9f + e.Def.Radius) return true; // point-blank ring
                        var toEnemy = (e.Position - p.Position).NormalizedOrZero();
                        return Vector2.Dot(aimDir, toEnemy) >= -0.05f; // the swept front
                    }).ToList();
                foreach (var e in struckList)
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
                    if (e.Dead) continue;
                    if (def.Knockback > 0)
                    {
                        // The shockwave hurls survivors straight away from the caster.
                        var push = (e.Position - p.Position).NormalizedOrZero();
                        if (push == Vector2.Zero) push = p.Facing;
                        e.Position = Map.MoveWithCollision(e.Position, push * def.Knockback, e.Def.Radius, ref e.Height);
                    }
                    if (RollStun(def))
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

    /// <summary>Maximum mana ONE summon RESERVES while it exists: flat (grows per skill
    /// level) + a fraction of the caster's max mana. Captured at summon time, held
    /// through free respawns, released on dismissal.</summary>
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
            // Summons RESERVE maximum mana while they exist (PoE-style) instead of
            // paying a one-time cost: the reservation must fit in the total pool.
            float reservation = SummonManaCost(p, def, learned.Level);
            if (p.ManaReserved + reservation > p.Stats.MaxMana + 0.01f)
            {
                _events.MessageFor(p, "Not enough maximum mana to sustain another summon.");
                return;
            }
            var raised = SpawnSummon(p, learned, def);
            raised.ManaReserved = reservation;
            p.DesiredSummons[skillId] = LivingSummons(playerId, skillId);
            RecomputeManaReservation(p);
        }
        else if (delta < 0)
        {
            // Prefer cancelling a pending respawn; otherwise dismiss a living minion.
            // Either way the reservation is released.
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
            RecomputeManaReservation(p);
        }
    }

    /// <summary>Re-sum a player's summon mana reservation (living minions + those
    /// awaiting their free respawn), clamp current mana into the unreserved pool, and
    /// sync. THE place reservation bookkeeping happens.</summary>
    public void RecomputeManaReservation(ServerPlayer p)
    {
        p.ManaReserved =
            Summons.Values.Where(s => s.OwnerId == p.Id).Sum(s => s.ManaReserved) +
            _summonRespawns.Where(r => r.OwnerId == p.Id).Sum(r => r.ManaReserved);
        float effectiveMax = MathF.Max(0f, p.Stats.MaxMana - p.ManaReserved);
        if (p.Mana > effectiveMax) p.Mana = effectiveMax;
        p.LastSyncedMana = p.Mana;
        _events.PlayerHealthChanged(p);
    }

    /// <summary>Rally command (backquote): send ONE summon skill's pack to a point, or
    /// clear its rally so it falls back to following the summoner. An empty skillId
    /// applies to every summon skill the player knows.</summary>
    /// <summary>Per-(player, skill) rally flow fields, BFS'd ONCE when the rally is set
    /// (the point never moves) so rallied summons path around walls instead of wedging.</summary>
    private readonly Dictionary<(int playerId, string skillId), FlowField> _rallyFields = new();

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
            if (hasPoint)
            {
                float h = Map.GroundHeightAt(point);
                p.SummonRallies[id] = (point, h);
                var field = new FlowField();
                ComputeFlowFrom(NodeOf(point, h), field);
                _rallyFields[(playerId, id)] = field;
            }
            else
            {
                p.SummonRallies.Remove(id);
                _rallyFields.Remove((playerId, id));
            }
        }
    }

    private ServerSummon SpawnSummon(ServerPlayer p, LearnedSkill learned, SkillDefinition def)
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
        return s;
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
            ManaReserved = s.ManaReserved,
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
            if (learned == null || def == null)
            {
                RecomputeManaReservation(owner); // dropped entry releases its hold
                continue;
            }
            var reborn = SpawnSummon(owner, learned, def);
            reborn.ManaReserved = r.ManaReserved; // the reservation carries through
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
            // then fight whatever comes in range from the held position. But a body
            // physically blocking the march IS the fight — a summon shoving against
            // an enemy at melee distance attacks it rather than pushing forever.
            bool marchingToRally = rallied && Vector2.Distance(s.Position, goal) > 1.2f;
            bool blockedByPrey = marchingToRally && prey != null && bestDist <= 2.2f;

            if ((!marchingToRally || blockedByPrey) && prey != null && bestDist <= s.Reach)
            {
                if (Time >= s.AttackReadyAt)
                {
                    // Attached Skill Scrolls buff summon attacks: the summon skill's
                    // EffectiveSkillStats carries scroll attack speed (via the cooldown
                    // ratio), extra arrow projectiles, and every on-hit ailment chance.
                    var learnedSum = owner.Character.GetSkill(s.SkillId);
                    var defSum = Data.Skills.GetValueOrDefault(s.SkillId);
                    var scrollStats = defSum != null && learnedSum != null
                        ? SkillMath.Compute(Data, defSum, learnedSum.Level,
                            learnedSum.ScrollDefinitions(Data), owner.Stats)
                        : default;
                    float speedMult = defSum is { Cooldown: > 0 } && scrollStats.Cooldown > 0
                        ? scrollStats.Cooldown / defSum.Cooldown
                        : 1f;
                    s.AttackReadyAt = Time + s.SwingTime * MathF.Max(0.3f, speedMult);
                    var dir = (prey.Position - s.Position).NormalizedOrZero();
                    _events.SummonAttacked(s, dir);
                    if (s.Melee)
                    {
                        // Skeleton warrior swing: the full player-hit pipeline (mitigation,
                        // kill credit/XP to the summoner, scroll ailments), slashing damage.
                        var comps = RollComponentList(s.Damage * 0.85f, s.Damage * 1.15f, DamageKind.Slash, null);
                        var (dmg, kind) = MitigateForEnemy(prey, comps);
                        HitEnemy(prey, dmg, s.OwnerId, s.SkillId, kind);
                        if (!prey.Dead) ApplyAilments(prey, comps, dmg, scrollStats);
                    }
                    else
                    {
                        // Multishot scrolls fan extra arrows; every arrow carries the
                        // scroll ailment stats for its impact roll.
                        int arrowCount = Math.Max(1, scrollStats.ProjectileCount);
                        for (int a = 0; a < arrowCount; a++)
                        {
                            float spread = (a - (arrowCount - 1) * 0.5f) * 0.12f;
                            var aDir = Rotate(dir, spread);
                            var arrow = new ServerProjectile
                            {
                                Id = _nextProjectileId++,
                                FromPlayer = true,
                                OwnerId = s.OwnerId,   // kill credit and XP go to the summoner
                                SkillId = s.SkillId,
                                SpriteOverride = "Arrow",
                                Position = s.Position + aDir * 0.4f,
                                Height = s.Height,
                                Direction = aDir,
                                Speed = 11f,
                                MaxRange = ServerSummon.AttackRange + 1.5f,
                                MinDamage = s.Damage * 0.85f,
                                MaxDamage = s.Damage * 1.15f,
                                DamageKind = DamageKind.Thrust,
                                Ailments = scrollStats,
                            };
                            Projectiles[arrow.Id] = arrow;
                            _events.ProjectileSpawned(arrow);
                        }
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
                // Path via flow fields instead of naive point-following: the owner's
                // live field when heeling, the rally's one-shot field when rallied —
                // summons route around walls and water like enemies do.
                FlowField pathField = null;
                if (moveTarget == goal)
                {
                    if (rallied) _rallyFields.TryGetValue((owner.Id, s.SkillId), out pathField);
                    else _flow.TryGetValue(owner.Id, out pathField);
                }
                var waypoint = SummonWaypoint(s, moveTarget, pathField);
                var dir = (waypoint - s.Position).NormalizedOrZero();
                float h = s.Height;
                s.Position = Map.MoveWithCollision(s.Position, dir * ServerSummon.MoveSpeed * dt,
                    ServerSummon.Radius, ref h);
                s.Height = h;
            }
        }
    }

    /// <summary>Where a marching summon should head: straight at a visible same-surface
    /// goal, else the center of the next flow-field tile toward it (mirrors ChaseWaypoint).</summary>
    private Vector2 SummonWaypoint(ServerSummon s, Vector2 goal, FlowField field)
    {
        if (!Map.SegmentBlocked(s.Position, goal, s.Height + 0.5f))
            return goal;
        if (field?.Next != null)
        {
            int node = NodeOf(s.Position, s.Height);
            if (node >= 0 && field.Next[node] >= 0)
            {
                int tile = field.Next[node] / 2;
                return new Vector2(tile % Map.Width + 0.5f, tile / Map.Width + 0.5f);
            }
        }
        return goal;
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
            // Party XP: the killer earns full value, every OTHER player earns
            // XpBalance.PartyShare of it — so one high-damage build sniping every kill
            // no longer starves the rest of the group. Each member's own under-level
            // penalty applies to their own share. Skill XP stays killer-only (it
            // follows the skill that landed the blow).
            foreach (var member in Players.Values)
            {
                float share = member.Id == killer.Id ? 1f : Stats.XpBalance.PartyShare;
                float xp = e.Def.XpReward * e.XpScale * share *
                           Stats.XpBalance.LevelFactor(member.Character.Level, e.Level);
                bool changed = GrantCharacterXp(member, xp);
                if (member.Id == killer.Id)
                {
                    var skill = e.LastHitSkillId != null ? killer.Character.GetSkill(e.LastHitSkillId) : null;
                    if (skill != null)
                        changed |= GrantSkillXp(killer, skill, xp);
                }
                if (changed) _events.CharacterChanged(member);
            }
        }

        // Elites roll the loot table twice; the boss's own table already guarantees
        // an item + both scroll types per roll, so its double roll is the reward burst.
        // Item level comes from e.Level — the SCALED level when a spawner overrides
        // the def — so a level-11 "zombie" drops level-11 loot, not level-1 loot.
        int lootRolls = e.Affixes == EliteAffix.None ? 1 : 2;
        for (int roll = 0; roll < lootRolls; roll++)
            foreach (var item in Loot.RollDrops(e.Def.LootTableId, e.Level))
                SpawnDrop(item, e.Position, e.Height);

        // Gold drop, scaled by enemy level.
        var table = Data.GetLootTable(e.Def.LootTableId);
        if (_rng.NextDouble() < table.GoldDropChance)
        {
            int amount = _rng.Next(table.GoldMin, table.GoldMax + 1);
            amount = Math.Max(1, (int)(amount * (1f + 0.25f * (e.Level - 1))));
            SpawnGoldDrop(amount, e.Position, e.Height);
        }

        // Campaign boss down: the sealed exit opens.
        if (Campaign && e.Id == _bossEnemyId)
        {
            foreach (var pl in Players.Values)
                _events.MessageFor(pl, "The Gravelord falls — the way home is open.");
            _events.ZoneStateChanged(this);
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
        // A summon skill's reservation price rises with its level — reprice every
        // minion already out (and any awaiting a free respawn), not just future ones.
        var def = skill.GetDefinition(Data);
        if (def?.Archetype == SkillArchetype.Summon)
        {
            float reservation = SummonManaCost(p, def, skill.Level);
            foreach (var s in Summons.Values)
                if (s.OwnerId == p.Id && s.SkillId == skillId)
                    s.ManaReserved = reservation;
            foreach (var r in _summonRespawns)
                if (r.OwnerId == p.Id && r.SkillId == skillId)
                    r.ManaReserved = reservation;
            RecomputeManaReservation(p);
        }
        _events.CharacterChanged(p);
    }

    /// <summary>Apply a typed hit to a player. `attackHit` marks a direct Attack (enemy
    /// melee swings and attack projectiles) — the only damage Deflection may run
    /// against. Spells, DoTs, ground effects and slams pass attackHit: false.</summary>
    private void DamagePlayerTyped(ServerPlayer p, List<(DamageKind kind, float amount)> components,
        bool attackHit = true)
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

        // Deflection (DEX defense): direct Attack hits — regardless of their damage
        // composition — run descending independent checks; each success deflects a
        // fraction of the REMAINING hit. Never applies to non-Attack damage.
        if (attackHit && p.Stats.DeflectionChance > 0f)
            damage *= Stats.Deflection.RollDamageMultiplier(p.Stats.DeflectionChance, _rng.NextDouble);

        damage = MathF.Max(0.5f, damage);
        float totalHit = damage; // the floating number shows the WHOLE hit, ES + life

        // Energy Shield absorbs before life. ANY damage taken (even fully absorbed)
        // resets the recharge delay.
        p.LastDamagedAt = Time;
        if (p.EnergyShield > 0f)
        {
            float absorbed = MathF.Min(p.EnergyShield, damage);
            p.EnergyShield -= absorbed;
            p.LastSyncedEnergyShield = p.EnergyShield;
            damage -= absorbed;
        }

        p.Health -= damage;
        p.LastSyncedHealth = p.Health;
        _events.DamageDealt(true, p.Id, totalHit, kind, p.Position);
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
