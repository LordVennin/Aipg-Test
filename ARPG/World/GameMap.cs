using System.Numerics;

namespace ARPG.World;

/// <summary>Ramp ascent direction: the side of the tile where the ramp reaches Ground+1.</summary>
public enum RampDirection : byte
{
    None = 0,
    PlusX = 1,   // height rises toward +X
    MinusX = 2,  // height rises toward -X
    PlusY = 3,
    MinusY = 4,
}

/// <summary>
/// The playable map plus collision, now with LAYERED TERRAIN. The simulation stays
/// fundamentally 2D/isometric: every entity is (X, Y) plus a continuous surface HEIGHT in
/// level units (0 = ground floor, 1 = one level up, ...). Each tile column stores:
///  - GroundLevel: elevation of the base walkable surface (cliffs = adjacent level changes)
///  - WallHeight:  &gt; 0 makes the column a solid obstacle rising that many levels above
///                 ground (tall cliffs/walls of varying height)
///  - Ramp:        the tile's ground surface slopes from GroundLevel up to GroundLevel+1
///                 toward the given direction (the only way to change elevation)
///  - BridgeLevel: &gt; 0 adds a second walkable surface (a deck) above the ground surface —
///                 entities can walk UNDER the bridge at ground height while others stand
///                 on the deck at the same X/Y
/// Movement resolves against the nearest reachable surface (within a step tolerance), so
/// which layer an entity occupies falls out of its continuous height — no explicit layer
/// switching logic anywhere else in the game.
/// Generated deterministically from a seed, so the host only needs to share the seed.
/// </summary>
public class GameMap
{
    public int Width { get; }
    public int Height { get; }
    public int Seed { get; }

    private readonly byte[] _ground;   // walkable ground elevation per tile
    private readonly byte[] _wall;     // solid obstacle height above ground (0 = walkable)
    private readonly byte[] _ramp;     // RampDirection per tile
    private readonly byte[] _rampStyle; // 0 = smooth ramp, 1 = stairs (render-only)
    private readonly byte[] _bridge;   // elevated walkable deck level (0 = none)

    /// <summary>Vertical step an entity can absorb when moving between surfaces. Level
    /// differences at or under this are walkable (ramp ends); full levels are not.</summary>
    public const float StepTolerance = 0.6f;

    public Vector2 PlayerSpawn { get; private set; }
    public List<Vector2> EnemySpawns { get; } = new();

    public GameMap(int seed, int width = 44, int height = 44)
    {
        Seed = seed;
        Width = width;
        Height = height;
        _ground = new byte[width * height];
        _wall = new byte[width * height];
        _ramp = new byte[width * height];
        _rampStyle = new byte[width * height];
        _bridge = new byte[width * height];
        Generate(new Random(seed));
    }

    private int Idx(int x, int y) => y * Width + x;
    private bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;

    public int GroundLevel(int x, int y) => InBounds(x, y) ? _ground[Idx(x, y)] : 0;
    public int WallHeight(int x, int y) => InBounds(x, y) ? _wall[Idx(x, y)] : 1; // out of bounds = solid
    public RampDirection Ramp(int x, int y) => InBounds(x, y) ? (RampDirection)_ramp[Idx(x, y)] : RampDirection.None;
    /// <summary>True when the transition tile renders as stairs instead of a smooth
    /// ramp. Purely visual — movement treats both identically.</summary>
    public bool RampIsStairs(int x, int y) => InBounds(x, y) && _rampStyle[Idx(x, y)] == 1;
    public int BridgeLevel(int x, int y) => InBounds(x, y) ? _bridge[Idx(x, y)] : 0;

    public bool IsSolid(int x, int y) => WallHeight(x, y) > 0;

    /// <summary>Legacy-style solid check (used by tests/debug helpers on the ground layer).</summary>
    public bool IsWallAt(Vector2 pos) => IsSolid((int)MathF.Floor(pos.X), (int)MathF.Floor(pos.Y));

    // ------------------------------------------------------------------ surfaces

    /// <summary>Ground-surface height at a position, with ramp interpolation (ignores bridges).</summary>
    public float GroundHeightAt(Vector2 pos)
    {
        int x = (int)MathF.Floor(pos.X), y = (int)MathF.Floor(pos.Y);
        float g = GroundLevel(x, y);
        return g + RampT(x, y, pos);
    }

    /// <summary>0..1 ramp progress at a position (0 outside ramps).</summary>
    private float RampT(int x, int y, Vector2 pos) => Ramp(x, y) switch
    {
        RampDirection.PlusX => Math.Clamp(pos.X - x, 0f, 1f),
        RampDirection.MinusX => Math.Clamp(1f - (pos.X - x), 0f, 1f),
        RampDirection.PlusY => Math.Clamp(pos.Y - y, 0f, 1f),
        RampDirection.MinusY => Math.Clamp(1f - (pos.Y - y), 0f, 1f),
        _ => 0f,
    };

    /// <summary>
    /// The height of the surface an entity at `currentHeight` would stand on at `pos`,
    /// or null when no surface there is reachable (solid column, empty air off a deck,
    /// a cliff more than StepTolerance away...). Chooses the nearest candidate among the
    /// ground surface (ramp-interpolated) and a bridge deck, which is what lets one
    /// entity walk under a bridge while another stands on it.
    /// </summary>
    public float? SampleHeight(Vector2 pos, float currentHeight)
    {
        int x = (int)MathF.Floor(pos.X), y = (int)MathF.Floor(pos.Y);
        if (!InBounds(x, y) || IsSolid(x, y)) return null;

        float? best = null;
        float bestDiff = float.MaxValue;

        float ground = GroundLevel(x, y) + RampT(x, y, pos);
        float diff = MathF.Abs(ground - currentHeight);
        if (diff <= bestDiff) { best = ground; bestDiff = diff; }

        int bridge = BridgeLevel(x, y);
        if (bridge > 0)
        {
            float bDiff = MathF.Abs(bridge - currentHeight);
            if (bDiff < bestDiff) { best = bridge; bestDiff = bDiff; }
        }

        return bestDiff <= StepTolerance ? best : null;
    }

    /// <summary>Can a circle at `height` overlap this tile at all? (Used for collision:
    /// a tile with no reachable surface acts as a solid wall for that entity.)</summary>
    private bool TileReachable(int x, int y, float height)
    {
        if (!InBounds(x, y) || IsSolid(x, y)) return false;
        int g = GroundLevel(x, y);
        float lo = g, hi = Ramp(x, y) != RampDirection.None ? g + 1 : g;
        if (height >= lo - StepTolerance && height <= hi + StepTolerance) return true;
        int bridge = BridgeLevel(x, y);
        return bridge > 0 && MathF.Abs(bridge - height) <= StepTolerance;
    }

    // ------------------------------------------------------------------ movement

    /// <summary>
    /// Move a circle through the map with axis-separated collision (slide along blocked
    /// edges), resolving the entity's surface height as it goes: walking up a ramp raises
    /// `height` continuously; cliffs, walls, deck edges and solid columns block.
    /// </summary>
    public Vector2 MoveWithCollision(Vector2 from, Vector2 delta, float radius, ref float height)
    {
        // Blocking compares PENETRATION into unreachable tiles instead of a hard
        // overlap test: reachability depends on the mover's height, so traversing a
        // ramp can flip an overlapped tile between reachable and not mid-walk. A hard
        // test wedges the mover forever at that threshold. Instead, small penetration
        // is tolerated (PenSlack) and actively pushed out below, so movers slide off
        // cliff flanks instead of sticking to them — while walls stay solid (per-step
        // penetration is capped well under the radius, so nothing tunnels).
        const float PenSlack = 0.15f;
        var pos = from;
        float penHere = CirclePenetration(pos, radius, height);
        var tryX = pos + new Vector2(delta.X, 0);
        if (SampleHeight(tryX, height) is { } hx &&
            CirclePenetration(tryX, radius, hx) is { } px && px <= MathF.Max(penHere, PenSlack) + 0.001f)
        {
            pos = tryX;
            height = hx;
            penHere = px;
        }
        var tryY = pos + new Vector2(0, delta.Y);
        if (SampleHeight(tryY, height) is { } hy &&
            CirclePenetration(tryY, radius, hy) is { } py && py <= MathF.Max(penHere, PenSlack) + 0.001f)
        {
            pos = tryY;
            height = hy;
            penHere = py;
        }
        if (penHere > 0f)
        {
            var freed = pos + PushOut(pos, radius, height);
            if (freed != pos && SampleHeight(freed, height) is { } hf &&
                CirclePenetration(freed, radius, hf) <= penHere)
            {
                pos = freed;
                height = hf;
            }
        }
        return pos;
    }

    /// <summary>Displacement that pushes a penetrating circle back out of unreachable
    /// tiles (summed per tile, capped).</summary>
    private Vector2 PushOut(Vector2 center, float radius, float height)
    {
        int minX = (int)MathF.Floor(center.X - radius);
        int maxX = (int)MathF.Floor(center.X + radius);
        int minY = (int)MathF.Floor(center.Y - radius);
        int maxY = (int)MathF.Floor(center.Y + radius);
        var push = Vector2.Zero;
        for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
            {
                if (TileReachable(x, y, height)) continue;
                float cx = Math.Clamp(center.X, x, x + 1);
                float cy = Math.Clamp(center.Y, y, y + 1);
                var away = center - new Vector2(cx, cy);
                float dist = away.Length();
                if (dist <= 0.0001f || dist >= radius) continue;
                push += away / dist * (radius - dist);
            }
        float len = push.Length();
        return len > 0.25f ? push / len * 0.25f : push;
    }

    /// <summary>Deepest overlap between the circle and any tile with no reachable
    /// surface at this height (0 = clear).</summary>
    private float CirclePenetration(Vector2 center, float radius, float height)
    {
        int minX = (int)MathF.Floor(center.X - radius);
        int maxX = (int)MathF.Floor(center.X + radius);
        int minY = (int)MathF.Floor(center.Y - radius);
        int maxY = (int)MathF.Floor(center.Y + radius);
        float worst = 0f;
        for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
            {
                if (TileReachable(x, y, height)) continue;
                float cx = Math.Clamp(center.X, x, x + 1);
                float cy = Math.Clamp(center.Y, y, y + 1);
                float dx = center.X - cx, dy = center.Y - cy;
                float pen = radius - MathF.Sqrt(dx * dx + dy * dy);
                if (pen > worst) worst = pen;
            }
        return worst;
    }

    /// <summary>Ground-layer convenience overload (entities that never leave height 0
    /// and legacy call sites).</summary>
    public Vector2 MoveWithCollision(Vector2 from, Vector2 delta, float radius)
    {
        float h = GroundHeightAt(from);
        return MoveWithCollision(from, delta, radius, ref h);
    }

    public bool CircleBlocked(Vector2 center, float radius, float height)
    {
        int minX = (int)MathF.Floor(center.X - radius);
        int maxX = (int)MathF.Floor(center.X + radius);
        int minY = (int)MathF.Floor(center.Y - radius);
        int maxY = (int)MathF.Floor(center.Y + radius);
        for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
            {
                if (TileReachable(x, y, height)) continue;
                // Closest point on the tile AABB to the circle center.
                float cx = Math.Clamp(center.X, x, x + 1);
                float cy = Math.Clamp(center.Y, y, y + 1);
                float dx = center.X - cx, dy = center.Y - cy;
                if (dx * dx + dy * dy < radius * radius) return true;
            }
        return false;
    }

    /// <summary>Legacy ground-layer circle test (tests/debug helpers).</summary>
    public bool CircleHitsWall(Vector2 center, float radius) =>
        CircleBlocked(center, radius, GroundHeightAt(center));

    // ------------------------------------------------------------------ line of sight

    /// <summary>
    /// Straight-line visibility/projectile blocking at a given flight height, sampled
    /// along the segment. Blocked by solid columns that rise past the height and by
    /// ground higher than the height (cliff faces). Lower ground does NOT block (shots
    /// fly over gaps), and bridge decks never block (the deck is a thin surface).
    /// </summary>
    public bool SegmentBlocked(Vector2 a, Vector2 b, float height)
    {
        float dist = Vector2.Distance(a, b);
        int steps = Math.Max(1, (int)(dist * 4));
        for (int i = 0; i <= steps; i++)
        {
            var p = Vector2.Lerp(a, b, i / (float)steps);
            int x = (int)MathF.Floor(p.X), y = (int)MathF.Floor(p.Y);
            if (!InBounds(x, y)) return true;
            int g = GroundLevel(x, y);
            if (IsSolid(x, y) && height < g + WallHeight(x, y) - 0.25f) return true;
            // Terrain higher than the flight height blocks (cliff faces). The clearance
            // is tighter than StepTolerance: a shot at chest height (surface + 0.5)
            // must NOT clear a full-level cliff the shooter can't walk up.
            if (!IsSolid(x, y) && g + RampT(x, y, p) > height + 0.25f) return true;
        }
        return false;
    }

    /// <summary>Legacy ground-layer segment test.</summary>
    public bool SegmentHitsWall(Vector2 a, Vector2 b) => SegmentBlocked(a, b, GroundHeightAt(a));

    // ------------------------------------------------------------------ generation

    private void Generate(Random rng)
    {
        // Border: solid ring.
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
                if (x == 0 || y == 0 || x == Width - 1 || y == Height - 1)
                    _wall[Idx(x, y)] = 1;

        // Scattered pillar clusters as obstacles, with varying heights (1-3 levels tall).
        int clusters = 26;
        for (int i = 0; i < clusters; i++)
        {
            int cx = rng.Next(4, Width - 5);
            int cy = rng.Next(4, Height - 5);
            int w = rng.Next(1, 4), h = rng.Next(1, 4);
            byte tall = (byte)rng.Next(1, 4);
            for (int y = cy; y < Math.Min(cy + h, Height - 2); y++)
                for (int x = cx; x < Math.Min(cx + w, Width - 2); x++)
                    _wall[Idx(x, y)] = tall;
        }

        CarveDemoTerrain();

        // Keep the player spawn area open.
        PlayerSpawn = new Vector2(Width / 2f, Height / 2f);
        for (int y = -2; y <= 2; y++)
            for (int x = -2; x <= 2; x++)
            {
                int i = Idx((int)PlayerSpawn.X + x, (int)PlayerSpawn.Y + y);
                _wall[i] = 0;
                _ground[i] = 0;
                _ramp[i] = 0;
                _rampStyle[i] = 0;
                _bridge[i] = 0;
            }

        // Enemy spawn points: walkable ground tiles far enough from the player spawn.
        // Includes elevated ground — enemies spawn at whatever level the tile has.
        int wanted = 14;
        int attempts = 0;
        while (EnemySpawns.Count < wanted && attempts++ < 500)
        {
            var p = new Vector2(rng.Next(3, Width - 3) + 0.5f, rng.Next(3, Height - 3) + 0.5f);
            if (!IsWallAt(p) && Vector2.Distance(p, PlayerSpawn) > 8f)
                EnemySpawns.Add(p);
        }
    }

    /// <summary>
    /// Deterministic terrain showcase (independent of the seed): two raised plateaus, a
    /// tall cliff wall, a ramp up, and a bridge between the plateaus with a walkable
    /// passage underneath. The procedural generator stays simple around it; this is the
    /// scaffolding future generation can replace tile-by-tile.
    /// </summary>
    private void CarveDemoTerrain()
    {
        // Clear the demo region of random pillars first.
        for (int y = 5; y <= 31; y++)
            for (int x = 5; x <= 18; x++)
            {
                _wall[Idx(x, y)] = 0;
                _ground[Idx(x, y)] = 0;
                _ramp[Idx(x, y)] = 0;
                _rampStyle[Idx(x, y)] = 0;
                _bridge[Idx(x, y)] = 0;
            }

        // Plateau A (level 1) with a small level-2 crown: two elevation levels + cliffs.
        for (int y = 6; y < 16; y++)
            for (int x = 6; x < 16; x++)
                _ground[Idx(x, y)] = 1;
        for (int y = 6; y < 9; y++)
            for (int x = 6; x < 9; x++)
                _ground[Idx(x, y)] = 2;

        // Plateau B (level 1), south of A, separated by a ground-level corridor.
        for (int y = 20; y < 30; y++)
            for (int x = 6; x < 16; x++)
                _ground[Idx(x, y)] = 1;

        // Smooth ramp INSET into plateau A's east edge (a notch cut out of the cliff, so
        // the flanking cliff faces read as retaining walls instead of a floating wedge).
        _ramp[Idx(15, 10)] = (byte)RampDirection.MinusX;
        _ramp[Idx(15, 11)] = (byte)RampDirection.MinusX;
        _ground[Idx(15, 10)] = 0;
        _ground[Idx(15, 11)] = 0;
        // Stairs between plateau A (level 1) and its level-2 crown.
        _ramp[Idx(9, 7)] = (byte)RampDirection.MinusX;
        _rampStyle[Idx(9, 7)] = 1;
        _ground[Idx(9, 7)] = 1;
        // Stairs inset into plateau B's east edge — the demo shows both transition styles.
        _ramp[Idx(15, 24)] = (byte)RampDirection.MinusX;
        _ramp[Idx(15, 25)] = (byte)RampDirection.MinusX;
        _rampStyle[Idx(15, 24)] = 1;
        _rampStyle[Idx(15, 25)] = 1;
        _ground[Idx(15, 24)] = 0;
        _ground[Idx(15, 25)] = 0;

        // A tall free-standing cliff wall on open ground, varying height (2-3 levels).
        for (int x = 19; x < 26; x++)
            _wall[Idx(x, 7)] = (byte)(x < 22 ? 3 : 2);

        // Bridge: a level-1 deck spanning the corridor between the plateaus. The ground
        // under the deck stays level 0 and walkable — entities pass beneath while others
        // stand on the deck at the same X/Y.
        for (int y = 16; y < 20; y++)
        {
            _bridge[Idx(10, y)] = 1;
            _bridge[Idx(11, y)] = 1;
        }
    }
}
