using System.Numerics;
using ARPG.Items;
using ARPG.Sim;
using ARPG.Skills;
using ARPG.Util;
using ARPG.World;

namespace ARPG.Server;

/// <summary>Where a defense run stands. Byte on the wire (DefenseState packet).</summary>
public enum DefensePhase : byte
{
    /// <summary>Between waves: build turrets/barriers, then ready up at the workbench.</summary>
    Build = 0,
    /// <summary>A wave is pouring out of the portals.</summary>
    Wave = 1,
    /// <summary>Every wave beaten — the loot is out and the exit is open.</summary>
    Won = 2,
    /// <summary>The wagon broke. The hub reclaims the party shortly.</summary>
    Lost = 3,
}

/// <summary>
/// The wagon-defense loop (Dungeon Defenders style, ARPG scale): through the hub's
/// west door lies a generated arena where a caravan wagon must survive
/// DefenseBalance.WavesTotal waves. Between waves players spend GOLD at the workbench
/// on crossbow turrets and spiked barriers — the economy's first real sink — then
/// ready up to call the next wave. Enemies pour from edge portals and march on the
/// wagon, chewing through anything built in their path; win and the caravan pays out,
/// lose the wagon and the Sanctum reclaims everyone empty-handed.
/// </summary>
public partial class ServerWorld
{
    /// <summary>All structures on the defense map (wagon and workbench included), by id.</summary>
    public readonly Dictionary<int, ServerStructure> Structures = new();
    private int _nextStructureId = 1;

    /// <summary>Current defense phase (meaningful only while Map.Kind == Defense).</summary>
    public DefensePhase DefPhase { get; private set; } = DefensePhase.Build;
    /// <summary>Waves fully beaten this run.</summary>
    public int WavesCleared { get; private set; }
    /// <summary>1-based wave number: the one running during Wave, the next during Build.</summary>
    public int WaveNumber => Math.Min(DefenseBalance.WavesTotal, WavesCleared + 1);
    /// <summary>The defense target, or null once it's been destroyed.</summary>
    public ServerStructure Wagon =>
        Structures.Values.FirstOrDefault(s => s.Kind == StructureKind.Wagon);

    private int _waveToSpawn;        // enemies still to trickle out this wave
    private float _nextWaveSpawnAt;
    private int _portalCursor;
    private float _defenseReturnAt;  // after a loss: when the hub reclaims the party
    private FlowField _wagonFlow;    // every attacker's shared route home to the wagon
    /// <summary>Mercs already deployed THIS run (one deployment each, dead or alive).</summary>
    private readonly HashSet<string> _deployedMercIds = new();

    /// <summary>Furnish a fresh defense arena: the wagon, its workbench, the camp
    /// crew, and the shared wagon-bound flow field the horde will march along.</summary>
    private void SetupDefense()
    {
        DefPhase = DefensePhase.Build;
        WavesCleared = 0;
        _waveToSpawn = 0;
        _defenseReturnAt = 0;
        _deployedMercIds.Clear();
        int players = Math.Max(1, Players.Count);
        float wagonHp = DefenseBalance.WagonHealth *
                        (1f + DefenseBalance.WagonHealthPerExtraPlayer * (players - 1));
        AddStructure(StructureKind.Wagon, Map.WagonSpot, wagonHp, ownerId: -1, radius: 0.85f);
        AddStructure(StructureKind.Workbench, Map.WorkbenchSpot, 1f, ownerId: -1, radius: 0.5f);
        SpawnDefenseNpcs();
        _wagonFlow = new FlowField();
        ComputeFlowFrom(NodeOf(Map.WagonSpot, Map.GroundHeightAt(Map.WagonSpot)),
            _wagonFlow, maxRadius: 200);
        _events.DefenseStateChanged(this);
    }

    private ServerStructure AddStructure(StructureKind kind, Vector2 pos, float hp,
        int ownerId, float radius)
    {
        var s = new ServerStructure
        {
            Id = _nextStructureId++, Kind = kind, Position = pos,
            Height = Map.GroundHeightAt(pos), Health = hp, MaxHealth = hp,
            OwnerId = ownerId, Radius = radius,
        };
        Structures[s.Id] = s;
        _events.StructureAdded(s);
        return s;
    }

    /// <summary>The camp crew (build phases only — they step away when a wave runs).</summary>
    private void SpawnDefenseNpcs()
    {
        if (!Data.Npcs.ContainsKey("mercenary") || Map.NpcSpots.Count == 0) return;
        if (Npcs.Any(n => n.TypeId == "mercenary")) return;
        var npc = new ServerNpc
        {
            Id = 5, TypeId = "mercenary", Position = Map.NpcSpots[0],
            Height = Map.GroundHeightAt(Map.NpcSpots[0]),
        };
        Npcs.Add(npc);
        _events.NpcAdded(npc);
    }

    /// <summary>Defense-map interact routing (from DoorReady): ready up at the
    /// workbench to call the wave; once every wave is beaten, ready at the exit door
    /// to head home.</summary>
    private void DefenseReady(ServerPlayer p)
    {
        int alive = Players.Values.Count(pl => pl.Alive);
        if (DefPhase == DefensePhase.Won &&
            Vector2.Distance(p.Position, Map.ExitDoor) <= 2.6f)
        {
            bool nowReady = _readyAtDoor.Add(p.Id);
            if (!nowReady) _readyAtDoor.Remove(p.Id);
            foreach (var pl in Players.Values)
                _events.MessageFor(pl,
                    $"{p.Name} is {(nowReady ? "ready" : "no longer ready")} at the door ({_readyAtDoor.Count}/{alive}).");
            _events.ZoneStateChanged(this);
            if (alive > 0 && _readyAtDoor.Count >= alive) TransitionTo(0);
            return;
        }
        if (DefPhase == DefensePhase.Build &&
            Vector2.Distance(p.Position, Map.WorkbenchSpot) <= 3.2f)
        {
            bool nowReady = _readyAtDefenseDoor.Add(p.Id);
            if (!nowReady) _readyAtDefenseDoor.Remove(p.Id);
            foreach (var pl in Players.Values)
                _events.MessageFor(pl,
                    $"{p.Name} is {(nowReady ? "ready" : "not ready")} for wave {WaveNumber} ({_readyAtDefenseDoor.Count}/{alive}).");
            if (alive > 0 && _readyAtDefenseDoor.Count >= alive) StartWave();
            return;
        }
        if (DefPhase != DefensePhase.Won &&
            Vector2.Distance(p.Position, Map.ExitDoor) <= 2.6f)
            _events.MessageFor(p, "The wagon still needs defending.");
    }

    private void StartWave()
    {
        _readyAtDefenseDoor.Clear();
        DefPhase = DefensePhase.Wave;
        int players = Math.Max(1, Players.Count);
        int wave = WaveNumber;
        _waveToSpawn = DefenseBalance.WaveBaseCount +
                       DefenseBalance.WavePerIndex * (wave - 1) +
                       DefenseBalance.WavePerExtraPlayer * (players - 1);
        _nextWaveSpawnAt = Time + 1.5f;
        _portalCursor = 0;
        // The camp crew steps clear while the fighting runs.
        foreach (var npc in Npcs.ToList())
        {
            Npcs.Remove(npc);
            _events.NpcRemoved(npc);
        }
        foreach (var pl in Players.Values)
            _events.MessageFor(pl, $"Wave {wave} of {DefenseBalance.WavesTotal} — defend the wagon!");
        _events.DefenseStateChanged(this);
        _events.ZoneStateChanged(this);
    }

    /// <summary>What crawls out of the portal next: chaff early, teeth later.</summary>
    private string RollWaveEnemy(int wave)
    {
        int roll = _rng.Next(100);
        return wave switch
        {
            1 => roll < 70 ? "grunt" : "spitter",
            2 => roll < 45 ? "grunt" : roll < 65 ? "shambler" : roll < 85 ? "spitter" : "crypt_leaper",
            3 => roll < 35 ? "grunt" : roll < 55 ? "shambler" : roll < 75 ? "crypt_leaper" : "spitter",
            4 => roll < 25 ? "grunt" : roll < 45 ? "crypt_leaper" : roll < 65 ? "shambler"
                : roll < 85 ? "bone_knight" : "spitter",
            _ => roll < 20 ? "shambler" : roll < 40 ? "crypt_leaper" : roll < 65 ? "bone_knight"
                : roll < 85 ? "spitter" : "grave_caller",
        };
    }

    private void TickDefense(float dt)
    {
        if (!Campaign || Map.Kind != MapKind.Defense) return;

        if (DefPhase == DefensePhase.Lost)
        {
            if (_defenseReturnAt > 0 && Time >= _defenseReturnAt)
            {
                _defenseReturnAt = 0;
                TransitionTo(0);
            }
            return;
        }

        if (DefPhase == DefensePhase.Wave)
        {
            // Trickle the wave out of the portals, cycling them so pressure comes
            // from more than one direction.
            if (_waveToSpawn > 0 && Time >= _nextWaveSpawnAt)
            {
                _nextWaveSpawnAt = Time + DefenseBalance.SpawnInterval;
                _waveToSpawn--;
                var portal = Map.SpawnPortals.Count > 0
                    ? Map.SpawnPortals[_portalCursor++ % Map.SpawnPortals.Count]
                    : Map.PlayerSpawn;
                var pos = portal + new Vector2(
                    (float)(_rng.NextDouble() - 0.5) * 1.2f,
                    (float)(_rng.NextDouble() - 0.5) * 1.2f);
                if (Map.CircleHitsWall(pos, 0.4f)) pos = portal;
                int level = CampaignEnemyLevel + DefenseBalance.WaveLevelStep * (WaveNumber - 1);
                var e = SpawnEnemy(RollWaveEnemy(WaveNumber), pos, level: level);
                e.State = EnemyState.Chase; // marching orders from birth
                _events.WorldEffect("burst", portal, 1.0f, 0.35f, e.Height);
            }
            // Wave down to the last body: back to building, or the payout.
            else if (_waveToSpawn <= 0 && !Enemies.Values.Any(en => !en.Dead))
            {
                WavesCleared++;
                if (WavesCleared >= DefenseBalance.WavesTotal)
                {
                    DefenseWon();
                }
                else
                {
                    DefPhase = DefensePhase.Build;
                    SpawnDefenseNpcs();
                    foreach (var pl in Players.Values)
                        _events.MessageFor(pl,
                            $"Wave beaten — rebuild, then ready up at the workbench for wave {WaveNumber}.");
                    _events.DefenseStateChanged(this);
                }
            }
        }

        TickTurrets();
    }

    private void DefenseWon()
    {
        DefPhase = DefensePhase.Won;
        var wagon = Wagon;
        var at = wagon?.Position ?? Map.WagonSpot;
        float h = wagon?.Height ?? Map.GroundHeightAt(at);
        // The caravan pays its escort: two boss-table showers plus a purse of gold.
        int lootLevel = CampaignEnemyLevel + DefenseBalance.WavesTotal;
        for (int roll = 0; roll < 2; roll++)
            foreach (var item in Loot.RollDrops("boss", lootLevel))
                SpawnDrop(item, at + new Vector2(
                    (float)(_rng.NextDouble() - 0.5) * 2.4f,
                    0.8f + (float)_rng.NextDouble() * 1.2f), h);
        SpawnGoldDrop(120 + 40 * CampaignEnemyLevel, at + new Vector2(0f, 1.2f), h);
        foreach (var pl in Players.Values)
            _events.MessageFor(pl, "The wagon stands! The caravan pays its debt — the way home is open.");
        _events.DefenseStateChanged(this);
        _events.ZoneStateChanged(this); // the exit unlocks
    }

    private void DefenseLost()
    {
        if (DefPhase == DefensePhase.Lost) return;
        DefPhase = DefensePhase.Lost;
        _defenseReturnAt = Time + 3.5f;
        foreach (var pl in Players.Values)
            _events.MessageFor(pl, "The wagon is destroyed — the caravan is lost.");
        _events.DefenseStateChanged(this);
    }

    /// <summary>Turrets fight only while a wave runs (they'd shoot at nothing anyway,
    /// but the clock discipline keeps things deterministic for tests).</summary>
    private void TickTurrets()
    {
        if (DefPhase != DefensePhase.Wave) return;
        int level = CampaignEnemyLevel + DefenseBalance.WaveLevelStep * (WaveNumber - 1);
        float levelMult = 1f + DefenseBalance.CrossbowDamagePerLevel * Math.Max(0, level - 1);
        foreach (var s in Structures.Values.ToList())
        {
            if (s.Kind == StructureKind.CrossbowTurret)
            {
                if (Time < s.NextShotAt) continue;
                ServerEnemy prey = null;
                float best = DefenseBalance.CrossbowRange;
                foreach (var e in Enemies.Values)
                {
                    if (e.Dead) continue;
                    float d = Vector2.Distance(e.Position, s.Position);
                    if (d < best && !Map.ShotBlocked(s.Position, s.Height + 0.9f,
                            e.Position, e.Height + 0.5f))
                    {
                        best = d;
                        prey = e;
                    }
                }
                if (prey == null) continue;
                s.NextShotAt = Time + DefenseBalance.CrossbowCooldown;
                var dir = (prey.Position - s.Position).NormalizedOrZero();
                var bolt = new ServerProjectile
                {
                    Id = _nextProjectileId++,
                    FromPlayer = true,
                    OwnerId = s.OwnerId,   // kill credit to whoever paid for the turret
                    SkillId = null,        // no skill trains on turret bolts
                    SpriteOverride = "Arrow",
                    Position = s.Position + dir * 0.5f,
                    Height = s.Height + 0.4f,
                    Direction = dir,
                    Speed = 12f,
                    MaxRange = DefenseBalance.CrossbowRange + 1.5f,
                    HeightStep = (prey.Height - (s.Height + 0.4f)) / MathF.Max(0.5f, best),
                    MinDamage = DefenseBalance.CrossbowDamageMin * levelMult,
                    MaxDamage = DefenseBalance.CrossbowDamageMax * levelMult,
                    DamageKind = DamageKind.Thrust,
                };
                Projectiles[bolt.Id] = bolt;
                _events.ProjectileSpawned(bolt);
            }
            else if (s.Kind == StructureKind.FlameTurret)
            {
                if (Time < s.NextShotAt) continue;
                float flameMult = 1f + DefenseBalance.FlameDamagePerLevel * Math.Max(0, level - 1);
                bool burned = false;
                foreach (var e in Enemies.Values.ToList())
                {
                    if (e.Dead || MathF.Abs(e.Height - s.Height) > 0.75f) continue;
                    if (Vector2.Distance(e.Position, s.Position) > DefenseBalance.FlameRange) continue;
                    var comps = RollComponentList(DefenseBalance.FlameDamageMin * flameMult,
                        DefenseBalance.FlameDamageMax * flameMult, DamageKind.Fire, null);
                    var (dmg, kind) = MitigateForEnemy(e, comps);
                    HitEnemy(e, dmg, s.OwnerId, null, kind);
                    burned = true;
                }
                if (burned)
                {
                    s.NextShotAt = Time + DefenseBalance.FlameCooldown;
                    _events.WorldEffect("firepatch", s.Position, DefenseBalance.FlameRange * 0.8f,
                        DefenseBalance.FlameCooldown, s.Height);
                }
            }
        }
    }

    /// <summary>One defense-mode enemy tick. True = fully handled here (attacking a
    /// structure or marching on the wagon); false = a player/summon is close enough
    /// that the normal fighting AI should run instead.</summary>
    private bool DefenseEnemyTick(ServerEnemy e, ServerPlayer target, float dist,
        bool sameSurface, float meatDist, float dt)
    {
        float threat = MathF.Min(target != null && sameSurface ? dist : float.MaxValue, meatDist);
        if (threat <= e.Def.AggroRange * 0.9f)
        {
            if (e.State == EnemyState.Idle) e.State = EnemyState.Chase;
            return false;
        }

        var wagon = Wagon;
        if (wagon == null) return false; // already broken — behave normally

        // A structure in arm's reach gets chewed: plain damage on a steady clock,
        // no telegraph — walls don't dodge. (The workbench is camp scenery, not prey.)
        ServerStructure blocker = null;
        float bestGap = float.MaxValue;
        foreach (var s in Structures.Values)
        {
            if (s.Kind == StructureKind.Workbench) continue;
            if (MathF.Abs(s.Height - e.Height) > 0.75f) continue;
            float gap = Vector2.Distance(s.Position, e.Position) - s.Radius;
            if (gap < bestGap)
            {
                bestGap = gap;
                blocker = s;
            }
        }
        if (blocker != null && bestGap <= e.Def.Radius + 0.5f)
        {
            if (Time >= e.AttackReadyAt)
            {
                e.AttackReadyAt = Time + DefenseBalance.StructureAttackInterval * e.CooldownScale;
                var dir = (blocker.Position - e.Position).NormalizedOrZero();
                _events.EnemyAttacked(e, 2, dir); // the swing visual, no wind-up phase
                DamageStructure(blocker,
                    RollEnemyDamage(e).Sum(c => c.amount) * DefenseBalance.StructureDamageFactor);
            }
            return true;
        }

        // March on the wagon: straight when the lane is clear, else along the shared
        // wagon flow field (terrain-aware; built structures are for breaking, not
        // pathing around — the structure push-out plants blocked enemies at the wall).
        var goal = wagon.Position;
        if (!Map.SegmentBlocked(e.Position, goal, e.Height + 0.5f))
        {
            MoveEnemyToward(e, goal, dt);
            return true;
        }
        if (_wagonFlow?.Next != null)
        {
            int node = NodeOf(e.Position, e.Height);
            if (node >= 0 && _wagonFlow.Next[node] >= 0)
            {
                int tile = _wagonFlow.Next[node] / 2;
                MoveEnemyToward(e,
                    new Vector2(tile % Map.Width + 0.5f, tile / Map.Width + 0.5f), dt);
                return true;
            }
        }
        MoveEnemyToward(e, goal, dt);
        return true;
    }

    /// <summary>Structures are solid to ENEMIES only — players and summons move through
    /// their own camp freely. Runs after every enemy movement step.</summary>
    private void PushOutOfStructures(ServerEnemy e)
    {
        foreach (var s in Structures.Values)
        {
            if (s.Kind == StructureKind.Workbench) continue;
            if (MathF.Abs(s.Height - e.Height) > 0.75f) continue;
            float minDist = s.Radius + e.Def.Radius * 0.8f;
            var delta = e.Position - s.Position;
            float d2 = delta.LengthSquared();
            if (d2 >= minDist * minDist) continue;
            var dir = d2 > 0.0001f
                ? delta / MathF.Sqrt(d2)
                : new Vector2(MathF.Cos(e.Id * 2.4f), MathF.Sin(e.Id * 2.4f));
            var pushed = s.Position + dir * minDist;
            float h = e.Height;
            if (Map.SampleHeight(pushed, h) is { } nh &&
                !Map.CircleBlocked(pushed, e.Def.Radius * 0.7f, nh))
            {
                e.Position = pushed;
                e.Height = nh;
            }
        }
    }

    /// <summary>Damage a structure (enemy chewing). The wagon hitting zero loses the run.</summary>
    public void DamageStructure(ServerStructure s, float damage)
    {
        if (s.Destroyed || damage <= 0 || s.Kind == StructureKind.Workbench) return;
        s.Health -= damage;
        if (s.Health <= 0)
        {
            s.Health = 0;
            Structures.Remove(s.Id);
            _events.StructureRemoved(s);
            _events.WorldEffect("burst", s.Position, 0.9f, 0.4f, s.Height);
            if (s.Kind == StructureKind.Wagon) DefenseLost();
        }
        else
        {
            _events.StructureHealthChanged(s);
        }
    }

    /// <summary>Build a structure at a spot (BuildRequest). Gold is the price — the
    /// game's first true sink — and every rule is re-validated here regardless of what
    /// the client's build preview allowed.</summary>
    public void Build(int playerId, StructureKind kind, Vector2 pos)
    {
        if (!Campaign || Map.Kind != MapKind.Defense) return;
        if (!Players.TryGetValue(playerId, out var p) || !p.Alive) return;
        if (DefPhase != DefensePhase.Build)
        {
            _events.MessageFor(p, "You can only build between waves.");
            return;
        }
        if (!DefenseBalance.PlayerBuildable(kind)) return;
        if (kind == StructureKind.FlameTurret && !p.Character.FlamethrowerUnlocked)
        {
            _events.MessageFor(p, "You haven't researched the flamethrower — its blueprint is out there somewhere.");
            return;
        }
        if (Vector2.Distance(p.Position, pos) > DefenseBalance.BuildReach)
        {
            _events.MessageFor(p, "Too far away to build there.");
            return;
        }
        if (Map.SampleHeight(pos, p.Height) is not { } h || Map.CircleBlocked(pos, 0.45f, h))
        {
            _events.MessageFor(p, "No room to build there.");
            return;
        }
        foreach (var s in Structures.Values)
            if (Vector2.Distance(s.Position, pos) <
                MathF.Max(DefenseBalance.MinSpacing, s.Radius + 0.5f))
            {
                _events.MessageFor(p, "Too close to another structure.");
                return;
            }
        if (Map.SpawnPortals.Any(pp => Vector2.Distance(pp, pos) < DefenseBalance.PortalExclusion))
        {
            _events.MessageFor(p, "Too close to a portal to build.");
            return;
        }
        int cost = DefenseBalance.Cost(kind);
        if (p.Character.Gold < cost)
        {
            _events.MessageFor(p, $"Not enough gold ({cost} needed).");
            return;
        }
        p.Character.Gold -= cost;
        var built = AddStructure(kind, pos, DefenseBalance.Health(kind), p.Id,
            kind == StructureKind.SpikedBarrier ? 0.45f : 0.4f);
        _events.CharacterChanged(p);
        _events.WorldEffect("hit", pos, 0.5f, 0.3f, built.Height);
    }

    // ------------------------------------------------------------------ the researcher

    private static readonly string[] MercFirstNames =
    {
        "Harl", "Vesna", "Odo", "Grit", "Mara", "Tull", "Ilse", "Brant", "Kessa", "Rurik",
        "Petra", "Joss", "Wilm", "Sable", "Doran", "Ythel", "Nadia", "Corvin", "Ede", "Falk",
    };
    private static readonly string[] MercEpithets =
    {
        "the Unpaid", "Ironjaw", "of the Ditch", "Two-Blades", "the Patient", "Halfboot",
        "the Lucky", "Longwalk", "Cindershot", "the Quiet", "Oakarm", "Threefingers",
        "the Stray", "Grimtooth", "of Nowhere", "Quickstring",
    };

    /// <summary>One consumable curio of the given base from the player's BAG (contracts
    /// stack; the whole placed stack is returned — the caller decrements it).</summary>
    private Inventory.PlacedItem FindCurio(ServerPlayer p, string baseId) =>
        p.Character.Inventory.Items.FirstOrDefault(pl => pl.Item.BaseItemId == baseId);

    /// <summary>The researcher's desk (ResearchRequest): action 0 spends one Mercenary
    /// Contract on a RANDOMIZED hire (kind, name and power are the roll); action 1
    /// hands over the Flamethrower Blueprint and unlocks the turret for good.</summary>
    public void Research(int playerId, byte action)
    {
        if (!Players.TryGetValue(playerId, out var p) || !p.Alive) return;
        var researcher = Npcs.FirstOrDefault(n => n.TypeId == "researcher");
        if (researcher == null ||
            Vector2.Distance(p.Position, researcher.Position) > 3.5f) return;

        if (action == 0)
        {
            var contract = FindCurio(p, "merc_contract");
            if (contract == null)
            {
                _events.MessageFor(p, "No mercenary contracts in your bag — they turn up as rare spoils.");
                return;
            }
            contract.Item.StackCount--;
            if (contract.Item.StackCount <= 0) p.Character.Inventory.Items.Remove(contract);
            // The roll: kind, name and power are fate's. Power rides the character's
            // level so late hires stay relevant without out-muscling built turrets.
            var merc = new MercData
            {
                Kind = _rng.Next(2) == 0 ? "warrior" : "archer",
                Power = Math.Max(1, p.Character.Level / 2 + _rng.Next(-1, 3)),
                Name = $"{MercFirstNames[_rng.Next(MercFirstNames.Length)]} {MercEpithets[_rng.Next(MercEpithets.Length)]}",
            };
            p.Character.Mercs.Add(merc);
            foreach (var pl in Players.Values)
                _events.MessageFor(pl,
                    $"{p.Name} hired {merc.Name} — {(merc.Kind == "archer" ? "an" : "a")} {merc.Kind} of power {merc.Power}.");
            _events.CharacterChanged(p);
            return;
        }

        if (action == 1)
        {
            if (p.Character.FlamethrowerUnlocked)
            {
                _events.MessageFor(p, "The flamethrower is already researched.");
                return;
            }
            var blueprint = FindCurio(p, "flamethrower_blueprint");
            if (blueprint == null)
            {
                _events.MessageFor(p, "Bring me the flamethrower blueprint and I'll make it real.");
                return;
            }
            blueprint.Item.StackCount--;
            if (blueprint.Item.StackCount <= 0) p.Character.Inventory.Items.Remove(blueprint);
            p.Character.FlamethrowerUnlocked = true;
            _events.MessageFor(p, "Odessa pores over the schematics... the flamethrower turret is yours to build.");
            _events.CharacterChanged(p);
        }
    }

    /// <summary>Deploy a hired mercenary onto the defense map (build phases only, one
    /// deployment per merc per run). They spawn as owner-bound summons that GUARD the
    /// chosen spot — and unlike skeletons, dead mercs stay down until the next run.</summary>
    public void DeployMerc(int playerId, string mercId, Vector2 pos)
    {
        if (!Campaign || Map.Kind != MapKind.Defense) return;
        if (!Players.TryGetValue(playerId, out var p) || !p.Alive) return;
        if (DefPhase != DefensePhase.Build)
        {
            _events.MessageFor(p, "Mercenaries deploy between waves.");
            return;
        }
        var merc = p.Character.Mercs.FirstOrDefault(m => m.Id == mercId);
        if (merc == null) return;
        if (_deployedMercIds.Contains(merc.Id))
        {
            _events.MessageFor(p, $"{merc.Name} has already taken the field this run.");
            return;
        }
        if (Vector2.Distance(p.Position, pos) > DefenseBalance.BuildReach)
        {
            _events.MessageFor(p, "Too far away to post anyone there.");
            return;
        }
        if (Map.SampleHeight(pos, p.Height) is not { } h || Map.CircleBlocked(pos, 0.35f, h))
        {
            _events.MessageFor(p, "No footing to post anyone there.");
            return;
        }
        _deployedMercIds.Add(merc.Id);
        bool warrior = merc.Kind == "warrior";
        var s = new ServerSummon
        {
            Id = _nextSummonId++,
            OwnerId = p.Id,
            SkillId = warrior ? "merc_warrior" : "merc_archer",
            Position = pos,
            Height = h,
            Health = 35f + 15f * merc.Power,
            MaxHealth = 35f + 15f * merc.Power,
            Damage = 5f + 2.5f * merc.Power,
            Melee = warrior,
            Reach = warrior ? 1.1f : ServerSummon.AttackRange,
            SwingTime = warrior ? 1.0f : ServerSummon.AttackCooldown,
            GuardPoint = pos,
        };
        Summons[s.Id] = s;
        _events.SummonSpawned(s);
        _events.WorldEffect("hit", pos, 0.5f, 0.3f, h);
        foreach (var pl in Players.Values)
            _events.MessageFor(pl, $"{merc.Name} takes the field.");
    }
}
