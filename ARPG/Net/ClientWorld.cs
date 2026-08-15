using System.Numerics;
using ARPG.Data;
using ARPG.Items;
using ARPG.Sim;
using ARPG.Stats;
using ARPG.World;

namespace ARPG.Net;

public class ClientPlayer
{
    public int Id;
    public string Name;
    public Vector2 Position;       // displayed (interpolated) position
    public Vector2 NetTarget;      // latest position from the server
    /// <summary>Surface height in elevation levels (see GameMap). Local: predicted; remote: replicated.</summary>
    public float Height;
    public float NetTargetHeight;
    public Vector2 Facing = new(1, 0);
    public float Health;
    public float MaxHealth;
    /// <summary>Current mana (server-authoritative; meaningful for the local player's orb).</summary>
    public float Mana;
    /// <summary>Energy Shield: absorbed before life, replicated with health updates.</summary>
    public float EnergyShield;
    public float MaxEnergyShield;
    /// <summary>Maximum mana reserved by this player's summons (orb shows it darkened).</summary>
    public float ManaReserved;
    public bool Alive = true;
    public bool IsLocal;
    /// <summary>Snap to the next replicated position instead of lerping (set on map
    /// transitions, where interpolating across the whole map reads as a streak).</summary>
    public bool SnapNext;
    /// <summary>Latest round-trip ping in ms (from PlayerPings packets), for the player list.</summary>
    public int PingMs;
    /// <summary>Counts down while this player is dodge-dashing (visual flair + i-frame hint).</summary>
    public float DodgeTimeLeft;
    /// <summary>Counts down while the held weapon plays its melee swing animation.</summary>
    public float SwingTimeLeft;
    public const float SwingDuration = 0.26f;
    /// <summary>Total duration of the current swing (slam wind-ups stretch it so the
    /// overhead chop lands exactly when the delayed hit resolves).</summary>
    public float SwingTotal = SwingDuration;
    /// <summary>World-space direction of the current swing (toward the impact point).</summary>
    public Vector2 SwingDir = new(1, 0);
    /// <summary>0 = horizontal swipe, 1 = overhead slam (Slam-tagged skills).</summary>
    public byte SwingKind;
    /// <summary>Ailment flags from PlayerStates (Server.PlayerDebuffs: Frozen, Shocked).</summary>
    public byte DebuffFlags;
    /// <summary>Main-hand weapon base id (from PlayerAppearance packets), for held-weapon rendering.</summary>
    public string WeaponBaseId;
    /// <summary>Off-hand item base id (usually a shield), rendered in the other hand.</summary>
    public string OffHandBaseId;
}

public class ClientEnemy
{
    public int Id;
    public string TypeId;
    public EnemyDefinition Def;
    public Vector2 Position;
    public Vector2 NetTarget;
    /// <summary>Surface height in elevation levels, replicated in snapshots.</summary>
    public float Height;
    public float NetTargetHeight;
    public float Health;
    public float MaxHealth;
    public byte State;
    /// <summary>Active debuff bitmask from enemy snapshots (Server.EnemyDebuffs flags),
    /// rendered as tiny per-debuff icons above the enemy's head.</summary>
    public byte DebuffFlags;
    /// <summary>Which way the sprite should face on screen (updated from movement).</summary>
    public bool FacingLeft;
    /// <summary>Elite affix bitmask (Server.EliteAffix) from the spawn packet — drives
    /// tinting, bar size and the hover display name.</summary>
    public byte EliteFlags;

    // Telegraphed melee swing animation (EnemyAttack events): 1 = winding up,
    // 2 = swing resolved. The renderer animates from the phase timestamp; stale
    // phases simply stop drawing once their duration has elapsed.
    public byte AttackAnimPhase;
    public Vector2 AttackDir;
    public long AttackAnimAtMs;

    /// <summary>Display name with elite prefixes ("Brutish Gravebound Grunt").</summary>
    public string DisplayName
    {
        get
        {
            string baseName = Def?.Name ?? TypeId;
            if (EliteFlags == 0 || (EliteFlags & 8) != 0) return baseName; // bosses use their own name
            string prefix = "";
            if ((EliteFlags & 1) != 0) prefix += "Brutish ";
            if ((EliteFlags & 2) != 0) prefix += "Swift ";
            if ((EliteFlags & 4) != 0) prefix += "Warded ";
            return prefix + baseName;
        }
    }

    public bool IsElite => EliteFlags != 0;
    public bool IsBoss => (EliteFlags & 8) != 0;
}

public class ClientProjectile
{
    public int Id;
    public bool FromPlayer;
    public string SkillId;
    public Vector2 Position;
    /// <summary>Flight height in elevation levels.</summary>
    public float Height;
    public Vector2 Direction;
    public float Speed;
    public float MaxRange;
    public float Traveled;
    /// <summary>Height change per tile traveled (overlook shots arc to their target).</summary>
    public float HeightStep;
    /// <summary>Sprite name override (shatter shards); null = the skill's sprite.</summary>
    public string SpriteOverride;
    /// <summary>Client-side cast prediction: a cosmetic local projectile spawned the
    /// instant the cast was REQUESTED, so remote players see their bolt leave on click
    /// instead of a round trip later. Replaced by the authoritative projectile when the
    /// server confirms (negative id; fizzles quickly if the cast was rejected).</summary>
    public bool Ghost;
}

/// <summary>A predicted ghost projectile that already ENDED (touched an enemy or ran
/// out of range) before the authoritative spawn arrived. The spawn handler consumes
/// these to fast-forward the real projectile to where the ghost finished — without it,
/// the real bolt replays the whole flight from the caster and high-ping players see
/// every close-range cast "fire twice".</summary>
public struct SpentGhost
{
    public string SkillId;
    public float Traveled;
    public long AtMs;
}

/// <summary>A player's minion (skeleton archer), replicated like a small friendly enemy.</summary>
public class ClientSummon
{
    public int Id;
    public int OwnerId;
    public string SkillId;
    public Vector2 Position;
    public Vector2 NetTarget;
    public float Height;
    public float NetTargetHeight;
    public float Health;
    public float MaxHealth;
    public bool FacingLeft;

    // Attack animation (SummonAttack events): warriors chop their drawn sword along
    // AttackDir, archers recoil from the bow release. Stale timestamps just stop
    // drawing once the short animation duration has elapsed.
    public Vector2 AttackDir;
    public long AttackAnimAtMs;
}

/// <summary>A friendly NPC (the test merchant): stationary, interacted with via the pickup key.</summary>
/// <summary>A hub chest as the client sees it (position + popped-lid state).</summary>
public class ClientChest
{
    public int Id;
    public Vector2 Position;
    public float Height;
    public bool Opened;
}

public class ClientNpc
{
    public int Id;
    public string TypeId;
    public string Name;
    public Vector2 Position;
    public float Height;
}

/// <summary>One merchant stock slot as replicated for the local player.</summary>
public class ClientShopEntry
{
    public int Slot;
    public int Price;
    public bool Sold;
    public ItemInstance Item;
}

public class ClientDrop
{
    public Guid DropId;
    public Vector2 Position;
    /// <summary>Surface height the drop rests on.</summary>
    public float Height;
    /// <summary>Dropped item, or null for a gold pile.</summary>
    public ItemInstance Item;
    public int GoldAmount;

    public bool IsGold => Item == null;
}

/// <summary>A floating combat number spawned from a server DamageEvent.</summary>
public class FloatingNumber
{
    public Vector2 Position;
    /// <summary>Surface height of the damaged entity, for elevation-correct rendering.</summary>
    public float Height;
    public float Amount;
    public byte Kind;          // (Skills.DamageKind)
    public bool TargetIsPlayer;
    /// <summary>True: the hit was fully blocked — render "Blocked" instead of a number.</summary>
    public bool Blocked;
    public float Age;
    public const float Lifetime = 0.9f;
}

/// <summary>A transient visual effect (skill flash, projectile impact).</summary>
public class ClientEffect
{
    public Vector2 Position;
    /// <summary>Surface height the effect plays at.</summary>
    public float Height;
    public float Radius;
    public float TimeLeft;
    public float Duration;
    /// <summary>Seconds before the effect starts playing (slam windups land late).</summary>
    public float Delay;
    public string Kind; // "slam", "burst", "hit", "melee", "swipe", "chain", "impact", "debris"
    /// <summary>World-space direction, used by directional effects (the swipe arc).</summary>
    public Vector2 Dir;
    /// <summary>World-space path for chained effects (chain lightning: caster -> victims).</summary>
    public List<Vector2> Points;
}

/// <summary>
/// The client's view of the game world, built purely from server packets (plus locally
/// predicted movement for the local player). Remote entities interpolate toward their
/// latest network positions.
/// </summary>
public class ClientWorld
{
    public GameMap Map;
    public int MyPlayerId = -1;
    public readonly Dictionary<int, ClientPlayer> Players = new();
    public readonly Dictionary<int, ClientEnemy> Enemies = new();
    public readonly Dictionary<int, ClientProjectile> Projectiles = new();
    /// <summary>Recently finished ghost projectiles (see SpentGhost) awaiting adoption.</summary>
    public readonly List<SpentGhost> SpentGhosts = new();

    /// <summary>Remember how far a just-removed ghost flew so the authoritative spawn
    /// can pick up from there instead of re-flying the whole path.</summary>
    public void RecordSpentGhost(ClientProjectile pr) =>
        SpentGhosts.Add(new SpentGhost
        {
            SkillId = pr.SkillId,
            Traveled = pr.Traveled,
            AtMs = Environment.TickCount64,
        });
    public readonly Dictionary<Guid, ClientDrop> Drops = new();
    public readonly Dictionary<int, ClientNpc> Npcs = new();
    public readonly Dictionary<int, ClientSummon> Summons = new();
    public readonly Dictionary<int, ClientChest> Chests = new();
    public readonly List<ClientEffect> Effects = new();

    // Campaign zone state (from ZoneState packets; drives the HUD banner + door hints).
    public int ZoneLoop = 1;
    public int ZoneMapIndex;
    public int ZoneEnemyLevel = 1;
    public int ZoneReadyCount;
    public int ZoneAlivePlayers;
    public bool ZoneExitLocked;

    /// <summary>Wipe every replicated world object for a map transition (players stay —
    /// they travel together; their positions snap on the next state packet).</summary>
    public void ClearForMapChange()
    {
        Enemies.Clear();
        Projectiles.Clear();
        Drops.Clear();
        Npcs.Clear();
        Summons.Clear();
        Chests.Clear();
        Effects.Clear();
        FloatingNumbers.Clear();
        SpentGhosts.Clear();
        foreach (var p in Players.Values) p.SnapNext = true;
    }
    public readonly List<FloatingNumber> FloatingNumbers = new();
    /// <summary>Diagnostic counter: dodge events received (used by the headless net test).</summary>
    public int DodgeEventsSeen;
    /// <summary>Diagnostic counter: blocked-hit events received (used by the headless net test).</summary>
    public int BlockedEventsSeen;

    /// <summary>Authoritative character state for the local player (from CharacterState packets).</summary>
    public CharacterData MyCharacter;
    public ComputedStats MyStats;

    public ClientPlayer Me => Players.GetValueOrDefault(MyPlayerId);

    public void RecomputeMyStats(GameData data)
    {
        if (MyCharacter != null)
            MyStats = StatCalculator.Compute(data, MyCharacter);
    }

    /// <summary>Per-frame interpolation and local projectile simulation (visual only —
    /// all hits are decided by the server).</summary>
    public void Tick(float dt)
    {
        foreach (var p in Players.Values)
        {
            if (p.SwingTimeLeft > 0) p.SwingTimeLeft -= dt;
            if (p.IsLocal) continue;
            p.Position = Vector2.Lerp(p.Position, p.NetTarget, Math.Clamp(dt * 12f, 0f, 1f));
            p.Height = float.Lerp(p.Height, p.NetTargetHeight, Math.Clamp(dt * 12f, 0f, 1f));
        }
        foreach (var e in Enemies.Values)
        {
            // Screen-space horizontal direction in isometric projection: (dx - dy).
            var delta = e.NetTarget - e.Position;
            float screenDx = delta.X - delta.Y;
            if (MathF.Abs(screenDx) > 0.02f)
            {
                e.FacingLeft = screenDx < 0;
            }
            else if (e.State == (byte)Server.EnemyState.Attack)
            {
                // Standing still while attacking (ranged spitters): face the nearest
                // player — the same target the server's AI picks.
                ClientPlayer nearest = null;
                float best = float.MaxValue;
                foreach (var p in Players.Values)
                {
                    if (!p.Alive) continue;
                    float d = Vector2.DistanceSquared(p.Position, e.Position);
                    if (d < best) { best = d; nearest = p; }
                }
                if (nearest != null)
                {
                    var toPlayer = nearest.Position - e.Position;
                    float faceDx = toPlayer.X - toPlayer.Y;
                    if (MathF.Abs(faceDx) > 0.02f) e.FacingLeft = faceDx < 0;
                }
            }
            e.Position = Vector2.Lerp(e.Position, e.NetTarget, Math.Clamp(dt * 10f, 0f, 1f));
            e.Height = float.Lerp(e.Height, e.NetTargetHeight, Math.Clamp(dt * 10f, 0f, 1f));
        }

        foreach (var pr in Projectiles.Values.ToList())
        {
            float step = pr.Speed * dt;
            pr.Position += pr.Direction * step;
            pr.Height += pr.HeightStep * step;
            pr.Traveled += step;
            if (pr.Traveled > pr.MaxRange + 2f)
            {
                Projectiles.Remove(pr.Id);
                if (pr.Ghost) RecordSpentGhost(pr);
                continue;
            }
            // Predicted ghosts stop on the first enemy they visually touch — the REAL
            // hit is the server's call, but a cosmetic bolt gliding through a body
            // reads as a pass-through bug.
            if (pr.Ghost)
            {
                foreach (var e in Enemies.Values)
                {
                    if (MathF.Abs(e.Height - pr.Height) > 0.75f) continue;
                    if (Vector2.Distance(pr.Position, e.Position) <= (e.Def?.Radius ?? 0.4f) + 0.25f)
                    {
                        Projectiles.Remove(pr.Id);
                        RecordSpentGhost(pr);
                        break;
                    }
                }
            }
        }
        // Spent-ghost records only matter for the ~one round trip until the matching
        // authoritative spawn arrives; drop stale ones so they never mis-adopt.
        SpentGhosts.RemoveAll(g => Environment.TickCount64 - g.AtMs > 1500);

        for (int i = Effects.Count - 1; i >= 0; i--)
        {
            if (Effects[i].Delay > 0) { Effects[i].Delay -= dt; continue; }
            Effects[i].TimeLeft -= dt;
            if (Effects[i].TimeLeft <= 0) Effects.RemoveAt(i);
        }

        foreach (var s in Summons.Values)
        {
            if (MathF.Abs(s.NetTarget.X - s.Position.X) > 0.02f)
                s.FacingLeft = s.NetTarget.X < s.Position.X;
            s.Position = Vector2.Lerp(s.Position, s.NetTarget, Math.Clamp(dt * 12f, 0f, 1f));
            s.Height += (s.NetTargetHeight - s.Height) * Math.Clamp(dt * 12f, 0f, 1f);
        }

        foreach (var p in Players.Values)
            if (p.DodgeTimeLeft > 0)
                p.DodgeTimeLeft -= dt;

        for (int i = FloatingNumbers.Count - 1; i >= 0; i--)
        {
            FloatingNumbers[i].Age += dt;
            if (FloatingNumbers[i].Age >= FloatingNumber.Lifetime) FloatingNumbers.RemoveAt(i);
        }
    }

    public void AddEffect(Vector2 pos, float radius, float duration, string kind, float height = 0f, float delay = 0f) =>
        Effects.Add(new ClientEffect { Position = pos, Radius = radius, TimeLeft = duration, Duration = duration, Kind = kind, Height = height, Delay = delay });

    public ClientDrop NearestDrop(Vector2 pos, float maxDist)
    {
        ClientDrop best = null;
        float bestDist = maxDist;
        foreach (var drop in Drops.Values)
        {
            float d = Vector2.Distance(pos, drop.Position);
            if (d <= bestDist) { bestDist = d; best = drop; }
        }
        return best;
    }
}
