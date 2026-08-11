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
    public Vector2 Facing = new(1, 0);
    public float Health;
    public float MaxHealth;
    public bool Alive = true;
    public bool IsLocal;
    /// <summary>Counts down while this player is dodge-dashing (visual flair + i-frame hint).</summary>
    public float DodgeTimeLeft;
}

public class ClientEnemy
{
    public int Id;
    public string TypeId;
    public EnemyDefinition Def;
    public Vector2 Position;
    public Vector2 NetTarget;
    public float Health;
    public float MaxHealth;
    public byte State;
}

public class ClientProjectile
{
    public int Id;
    public bool FromPlayer;
    public string SkillId;
    public Vector2 Position;
    public Vector2 Direction;
    public float Speed;
    public float MaxRange;
    public float Traveled;
}

public class ClientDrop
{
    public Guid DropId;
    public Vector2 Position;
    public ItemInstance Item;
}

/// <summary>A floating combat number spawned from a server DamageEvent.</summary>
public class FloatingNumber
{
    public Vector2 Position;
    public float Amount;
    public byte Kind;          // (Skills.DamageKind)
    public bool TargetIsPlayer;
    public float Age;
    public const float Lifetime = 0.9f;
}

/// <summary>A transient visual effect (skill flash, projectile impact).</summary>
public class ClientEffect
{
    public Vector2 Position;
    public float Radius;
    public float TimeLeft;
    public float Duration;
    public string Kind; // "slam", "burst", "hit", "melee"
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
    public readonly Dictionary<Guid, ClientDrop> Drops = new();
    public readonly List<ClientEffect> Effects = new();
    public readonly List<FloatingNumber> FloatingNumbers = new();
    /// <summary>Diagnostic counter: dodge events received (used by the headless net test).</summary>
    public int DodgeEventsSeen;

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
            if (p.IsLocal) continue;
            p.Position = Vector2.Lerp(p.Position, p.NetTarget, Math.Clamp(dt * 12f, 0f, 1f));
        }
        foreach (var e in Enemies.Values)
            e.Position = Vector2.Lerp(e.Position, e.NetTarget, Math.Clamp(dt * 10f, 0f, 1f));

        foreach (var pr in Projectiles.Values.ToList())
        {
            float step = pr.Speed * dt;
            pr.Position += pr.Direction * step;
            pr.Traveled += step;
            if (pr.Traveled > pr.MaxRange + 2f)
                Projectiles.Remove(pr.Id);
        }

        for (int i = Effects.Count - 1; i >= 0; i--)
        {
            Effects[i].TimeLeft -= dt;
            if (Effects[i].TimeLeft <= 0) Effects.RemoveAt(i);
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

    public void AddEffect(Vector2 pos, float radius, float duration, string kind) =>
        Effects.Add(new ClientEffect { Position = pos, Radius = radius, TimeLeft = duration, Duration = duration, Kind = kind });

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
