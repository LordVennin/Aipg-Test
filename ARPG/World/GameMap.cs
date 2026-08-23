using System.Numerics;
using ARPG.Data;

namespace ARPG.World;

/// <summary>Multi-tile generated features. Root tiles anchor the sprite; Part tiles are
/// the rest of the footprint (rendered by the root, still solid for collision/LOS).</summary>
public enum TileFeature : byte
{
    None = 0,
    BigTreeRoot = 1,
    BigTreePart = 2,
}

/// <summary>Ramp ascent direction: the side of the tile where the ramp reaches Ground+1.</summary>
public enum RampDirection : byte
{
    None = 0,
    PlusX = 1,   // height rises toward +X
    MinusX = 2,  // height rises toward -X
    PlusY = 3,
    MinusY = 4,
}

/// <summary>What a map IS. Arena = the original demo/test slice (seeded pillars around
/// the authored terrain showcase). Hub = the small sanctum room between runs (merchants,
/// chests, the run door). Forest = a generated hallway-style run map: long, terraced,
/// with an entry door behind the players and an exit door at the far end.</summary>
public enum MapKind : byte
{
    Arena = 0,
    Hub = 1,
    Forest = 2,
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
    private readonly byte[] _feature;  // TileFeature per tile (theme-generated landmarks)
    private readonly byte[] _water;    // 1 = water: impassable to walkers, open to shots/sight
    private readonly byte[] _tallGrass; // 1 = tall grass: walkable, renders over entity legs

    /// <summary>Vertical step an entity can absorb when moving between surfaces. Level
    /// differences at or under this are walkable (ramp ends); full levels are not.</summary>
    public const float StepTolerance = 0.6f;

    public Vector2 PlayerSpawn { get; private set; }
    public List<Vector2> EnemySpawns { get; } = new();

    /// <summary>What this map is (arena slice / hub sanctum / forest run hallway).</summary>
    public MapKind Kind { get; }
    /// <summary>Door players ARRIVE through (forest maps; zero on other kinds).</summary>
    public Vector2 EntryDoor { get; private set; }
    /// <summary>Door leading onward (hub: into the forest; forest: to the next map).</summary>
    public Vector2 ExitDoor { get; private set; }
    /// <summary>Openable starter-gear chests (hub only).</summary>
    public List<Vector2> ChestSpots { get; } = new();
    /// <summary>Generation-placed pack anchors along the run (forest only).</summary>
    public List<Vector2> PackSpots { get; } = new();
    /// <summary>Overlook plateau anchors for ranged packs (forest only).</summary>
    public List<Vector2> OverlookSpots { get; } = new();
    /// <summary>The cleared arena by the exit where the final map's boss waits.</summary>
    public Vector2 BossSpot { get; private set; }
    /// <summary>Friendly NPC stations (hub: gear merchant first, skill trainer second).</summary>
    public List<Vector2> NpcSpots { get; } = new();
    /// <summary>The flask-refill fountain basin (hub only; zero elsewhere).</summary>
    public Vector2 FountainSpot { get; private set; }
    /// <summary>The stash container's spot (hub only; zero elsewhere). Storage is keyed
    /// by container id so future rooms can hold more than one.</summary>
    public Vector2 StashSpot { get; private set; }
    public const string HubStashId = "hub_stash";

    /// <summary>The zone theme this map was GENERATED with. Themes are decided before
    /// generation and replicated to clients (JoinAccept), because they shape the map
    /// itself — the forest grows multi-tile trees, not just different colors.</summary>
    public ZoneTheme Theme { get; }

    public GameMap(int seed, ZoneTheme theme = null, MapKind kind = MapKind.Arena,
        int width = 0, int height = 0)
    {
        Seed = seed;
        Theme = theme;
        Kind = kind;
        if (width <= 0 || height <= 0)
            (width, height) = kind switch
            {
                MapKind.Hub => (22, 16),
                MapKind.Forest => (96, 26),
                _ => (44, 44),
            };
        Width = width;
        Height = height;
        _ground = new byte[width * height];
        _wall = new byte[width * height];
        _ramp = new byte[width * height];
        _rampStyle = new byte[width * height];
        _bridge = new byte[width * height];
        _feature = new byte[width * height];
        _water = new byte[width * height];
        _tallGrass = new byte[width * height];
        switch (kind)
        {
            case MapKind.Hub: GenerateHub(); break;
            case MapKind.Forest: GenerateForestRun(new Random(seed)); break;
            default: Generate(new Random(seed)); break;
        }
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
    public TileFeature Feature(int x, int y) => InBounds(x, y) ? (TileFeature)_feature[Idx(x, y)] : TileFeature.None;

    public bool IsSolid(int x, int y) => WallHeight(x, y) > 0;

    /// <summary>Water: a second kind of impassable tile. Unlike walls it has no height —
    /// nothing can WALK onto it, but shots, sight lines and thrown effects pass freely
    /// over the surface (bridge decks above water stay walkable).</summary>
    public bool IsWater(int x, int y) => InBounds(x, y) && _water[Idx(x, y)] == 1;

    /// <summary>Tall grass: purely visual walkable cover — the renderer draws a blade
    /// fringe OVER whatever stands in the tile, hiding its lower half.</summary>
    public bool IsTallGrass(int x, int y) => InBounds(x, y) && _tallGrass[Idx(x, y)] == 1;

    /// <summary>Legacy-style solid check (used by tests/debug helpers on the ground layer).</summary>
    public bool IsWallAt(Vector2 pos) => IsSolid((int)MathF.Floor(pos.X), (int)MathF.Floor(pos.Y));

    /// <summary>
    /// True when WEATHER can't reach a spot: standing under a bridge deck (below its
    /// level — the deck itself stays exposed), or anywhere near a big tree's canopy.
    /// Height matters, so the same X/Y is wet on the deck and dry underneath it.
    /// Future interiors plug their roofed tiles into this same query.
    /// </summary>
    public bool IsSheltered(Vector2 pos, float height)
    {
        int tx = (int)MathF.Floor(pos.X), ty = (int)MathF.Floor(pos.Y);
        int bridge = BridgeLevel(tx, ty);
        if (bridge > 0 && height < bridge - 0.4f) return true;
        // Canopy: big trees shade their trunk footprint and the ring around it.
        for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
                if (Feature(tx + dx, ty + dy) == TileFeature.BigTreeRoot) return true;
        return false;
    }

    /// <summary>The surface weather LANDS on in a tile column: the bridge deck when one
    /// spans it, else the wall top, else the ground.</summary>
    public float WeatherLandHeight(int x, int y) =>
        MathF.Max(BridgeLevel(x, y), GroundLevel(x, y) + WallHeight(x, y));

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

        if (IsWater(x, y))
        {
            // Water offers no ground surface — only a bridge deck above it is standable.
            int wb = BridgeLevel(x, y);
            if (wb > 0 && MathF.Abs(wb - currentHeight) <= StepTolerance) return wb;
            return null;
        }

        float ground = GroundLevel(x, y) + RampT(x, y, pos);
        // Climbing more than the step tolerance is never allowed; DROPPING down is
        // allowed up to a full level on RAMP tiles, so walking onto stairs from the
        // upper side edge hops you down onto the steps instead of wedging you in the
        // corner between the flank and the transition.
        float signed = ground - currentHeight; // positive = climbing
        float dropTol = Ramp(x, y) != RampDirection.None ? 1.05f : StepTolerance;
        if (signed <= StepTolerance && signed >= -dropTol)
        {
            best = ground;
            bestDiff = MathF.Abs(signed);
        }

        int bridge = BridgeLevel(x, y);
        if (bridge > 0)
        {
            float bDiff = MathF.Abs(bridge - currentHeight);
            if (bDiff <= StepTolerance && bDiff < bestDiff) { best = bridge; bestDiff = bDiff; }
        }

        return best;
    }

    /// <summary>Can a circle at `height` overlap this tile at all? (Used for collision:
    /// a tile with no reachable surface acts as a solid wall for that entity.)</summary>
    private bool TileReachable(int x, int y, float height)
    {
        if (!InBounds(x, y) || IsSolid(x, y)) return false;
        if (!IsWater(x, y))
        {
            int g = GroundLevel(x, y);
            float lo = g, hi = Ramp(x, y) != RampDirection.None ? g + 1 : g;
            if (height >= lo - StepTolerance && height <= hi + StepTolerance) return true;
        }
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
        // A step is allowed when it doesn't push deeper into unreachable tiles — OR
        // when the penetration only APPEARS because the surface height changed under
        // us (dropping onto stairs makes the flank behind 'unreachable' while the
        // circle still brushes it); the push-out below separates us over the next
        // few steps. At the old height such a position is essentially clear, so this
        // can never tunnel through a genuinely solid wall.
        bool StepAllowed(Vector2 tryPos, float newHeight, float oldHeight, float basePen)
        {
            float cap = MathF.Max(basePen, PenSlack) + 0.001f;
            if (CirclePenetration(tryPos, radius, newHeight) <= cap) return true;
            return MathF.Abs(newHeight - oldHeight) > 0.2f &&
                   CirclePenetration(tryPos, radius, oldHeight) <= cap;
        }
        var tryX = pos + new Vector2(delta.X, 0);
        if (SampleHeight(tryX, height) is { } hx && StepAllowed(tryX, hx, height, penHere))
        {
            pos = tryX;
            height = hx;
            penHere = CirclePenetration(pos, radius, height);
        }
        var tryY = pos + new Vector2(0, delta.Y);
        if (SampleHeight(tryY, height) is { } hy && StepAllowed(tryY, hy, height, penHere))
        {
            pos = tryY;
            height = hy;
            penHere = CirclePenetration(pos, radius, height);
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

    /// <summary>
    /// Line-of-fire test between two points with DIFFERENT flight heights, interpolating
    /// the height along the segment (a descending or climbing shot). Blocks on walls and
    /// terrain rising past the flight height, like SegmentBlocked — and additionally on
    /// CROSSING a bridge deck's plane, so nothing shoots up or down through the planks
    /// at someone on the other side of the deck.
    /// </summary>
    public bool ShotBlocked(Vector2 a, float ha, Vector2 b, float hb)
    {
        float dist = Vector2.Distance(a, b);
        int steps = Math.Max(1, (int)(dist * 4));
        for (int i = 0; i <= steps; i++)
        {
            float t = i / (float)steps;
            var p = Vector2.Lerp(a, b, t);
            float h = ha + (hb - ha) * t;
            int x = (int)MathF.Floor(p.X), y = (int)MathF.Floor(p.Y);
            if (!InBounds(x, y)) return true;
            int g = GroundLevel(x, y);
            if (IsSolid(x, y) && h < g + WallHeight(x, y) - 0.25f) return true;
            // Flight below the local surface = inside the terrain. The tight margin
            // matters for arcs: shots from below always graze the cliff lip on the way
            // up, so only true rim shooters get the angle — as they should.
            if (!IsSolid(x, y) && h < g + RampT(x, y, p) - 0.05f) return true;
            int bridge = BridgeLevel(x, y);
            if (bridge > 0 && MathF.Abs(h - bridge) <= 0.3f) return true; // deck plane
        }
        return false;
    }

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

        GenerateThemeFeatures();
        GeneratePonds();

        // Enemy spawn points: walkable ground tiles far enough from the player spawn.
        // Includes elevated ground — enemies spawn at whatever level the tile has.
        int wanted = 14;
        int attempts = 0;
        while (EnemySpawns.Count < wanted && attempts++ < 500)
        {
            var p = new Vector2(rng.Next(3, Width - 3) + 0.5f, rng.Next(3, Height - 3) + 0.5f);
            if (!IsWallAt(p) && !IsWater((int)p.X, (int)p.Y) && Vector2.Distance(p, PlayerSpawn) > 8f)
                EnemySpawns.Add(p);
        }
    }

    // ------------------------------------------------------------------ hub generation

    /// <summary>
    /// The sanctum between runs: a small flat room. West half is the players' —
    /// spawn point and a row of starter-gear chests along the wall; east wall holds
    /// the run door; the two merchants station mid-room, out of the walking line.
    /// </summary>
    private void GenerateHub()
    {
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
                if (x == 0 || y == 0 || x == Width - 1 || y == Height - 1)
                    _wall[Idx(x, y)] = 2;

        PlayerSpawn = new Vector2(5.5f, Height / 2f);
        ExitDoor = new Vector2(Width - 2.5f, Height / 2f);
        FountainSpot = new Vector2(10.5f, Height / 2f); // mid-room, on the walk to the door
        StashSpot = new Vector2(4.5f, 2.6f);            // against the north wall by the spawn
        NpcSpots.Add(new Vector2(Width * 0.62f, 3.6f));           // gear merchant, north side
        NpcSpots.Add(new Vector2(Width * 0.62f, Height - 3.6f));  // skill trainer, south side
        ChestSpots.Add(new Vector2(2.6f, 3.5f));
        ChestSpots.Add(new Vector2(2.6f, Height - 3.5f));
        ChestSpots.Add(new Vector2(6.5f, 2.4f));
        ChestSpots.Add(new Vector2(6.5f, Height - 2.4f));
    }

    // ------------------------------------------------------------------ forest run generation

    /// <summary>
    /// A run map: a LONG hallway with real terrain — a meandering corridor, terrace
    /// bands crossing the hall (stairs only near the corridor line, cliffs elsewhere),
    /// overlook plateaus hugging the side walls, pillar clusters, ponds and the theme's
    /// big trees. Entry door behind the spawn (west), exit door at the far east end,
    /// with a cleared arena in front of it for the final map's boss. Pack anchors are
    /// laid along the corridor at generation time. A ground-surface BFS validates the
    /// spawn-to-exit path; if decorations ever pinch it shut, the corridor line is
    /// carved flat as a deterministic fallback.
    /// </summary>
    private void GenerateForestRun(Random rng)
    {
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
                if (x == 0 || y == 0 || x == Width - 1 || y == Height - 1)
                    _wall[Idx(x, y)] = 2;

        // Meandering corridor line: a bounded random walk in y across the hall.
        var corridor = new int[Width];
        int cy = Height / 2;
        for (int x = 0; x < Width; x++)
        {
            corridor[x] = cy;
            if (x % 3 == 0)
                cy = Math.Clamp(cy + rng.Next(-1, 2), 4, Height - 5);
        }

        // Terrace bands crossing the hall: raised strips with stair columns cut in at
        // the corridor rows (±1) — everywhere else the band edge is a sheer cliff.
        int bx = 12 + rng.Next(5);
        while (bx < Width - 16)
        {
            int bw = 3 + rng.Next(3);
            for (int y = 1; y < Height - 1; y++)
                for (int x = bx; x < bx + bw; x++)
                    _ground[Idx(x, y)] = 1;
            foreach (int side in new[] { bx - 1, bx + bw })
            {
                var dir = side < bx ? RampDirection.PlusX : RampDirection.MinusX;
                int mid = corridor[Math.Clamp(side, 0, Width - 1)];
                for (int y = mid - 1; y <= mid + 1; y++)
                {
                    if (y < 1 || y >= Height - 1) continue;
                    int i = Idx(side, y);
                    _ground[i] = 0;
                    _ramp[i] = (byte)dir;
                    _rampStyle[i] = 1;
                }
            }
            bx += bw + 11 + rng.Next(8);
        }

        // Overlook plateaus hugging the side walls, each with a stair inset facing the
        // hall — spitter packs hold the tops (anchors recorded for the server).
        int plateaus = 3 + rng.Next(3);
        for (int i = 0; i < plateaus; i++)
        {
            int pw = 5 + rng.Next(4), ph = 4 + rng.Next(2);
            int px = 14 + rng.Next(Math.Max(1, Width - 30 - pw));
            bool north = rng.Next(2) == 0;
            int py = north ? 1 : Height - 1 - ph;
            // Skip plateaus that would land on a terrace ramp column or the boss arena.
            bool blocked = false;
            for (int x = px - 1; x <= px + pw && !blocked; x++)
                for (int y = py; y < py + ph && !blocked; y++)
                    blocked = !InBounds(x, y) || _ramp[Idx(x, y)] != 0 || x >= Width - 14;
            if (blocked) continue;
            byte level = (byte)(_ground[Idx(px, py + ph / 2)] + 1);
            for (int y = py; y < py + ph; y++)
                for (int x = px; x < px + pw; x++)
                    _ground[Idx(x, y)] = level;
            // Stair inset on the hall-facing edge, two tiles wide.
            int stairY = north ? py + ph - 1 : py;
            var stairDir = north ? RampDirection.MinusY : RampDirection.PlusY;
            for (int x = px + pw / 2 - 1; x <= px + pw / 2; x++)
            {
                int si = Idx(x, stairY);
                _ground[si] = (byte)(level - 1);
                _ramp[si] = (byte)stairDir;
                _rampStyle[si] = 1;
            }
            OverlookSpots.Add(new Vector2(px + pw / 2f, py + ph / 2f));
        }

        // Pillar clusters for cover — never pinching the corridor line itself.
        int clusters = Width / 6;
        for (int i = 0; i < clusters; i++)
        {
            int cx = rng.Next(5, Width - 6);
            int cyy = rng.Next(2, Height - 3);
            int w = rng.Next(1, 3), h = rng.Next(1, 3);
            byte tall = (byte)rng.Next(1, 4);
            for (int y = cyy; y < Math.Min(cyy + h, Height - 1); y++)
                for (int x = cx; x < Math.Min(cx + w, Width - 1); x++)
                {
                    if (Math.Abs(y - corridor[x]) <= 1) continue;   // corridor stays open
                    if (_ramp[Idx(x, y)] != 0) continue;            // stairs stay usable
                    if (x >= Width - 13) continue;                  // boss arena stays open
                    _wall[Idx(x, y)] = tall;
                }
        }

        // Spawn + doors sit on the corridor line at either end.
        PlayerSpawn = new Vector2(4.5f, corridor[4] + 0.5f);
        EntryDoor = new Vector2(1.6f, corridor[2] + 0.5f);
        ExitDoor = new Vector2(Width - 1.6f, corridor[Width - 3] + 0.5f);

        // Boss arena: a cleared flat pocket in front of the exit door.
        BossSpot = new Vector2(Width - 7.5f, corridor[Width - 8] + 0.5f);
        for (int y = 1; y < Height - 1; y++)
            for (int x = Width - 13; x < Width - 1; x++)
                if (Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), BossSpot) < 5.2f)
                {
                    int i = Idx(x, y);
                    _wall[i] = 0;
                    _ground[i] = 0;
                    _ramp[i] = 0;
                    _rampStyle[i] = 0;
                    _water[i] = 0;
                    _feature[i] = 0;
                }

        // Clear the spawn pocket.
        for (int y = -2; y <= 2; y++)
            for (int x = -2; x <= 2; x++)
            {
                int tx = (int)PlayerSpawn.X + x, ty = (int)PlayerSpawn.Y + y;
                if (!InBounds(tx, ty) || tx == 0 || ty == 0 || tx == Width - 1 || ty == Height - 1) continue;
                int i = Idx(tx, ty);
                _wall[i] = 0;
                _ground[i] = 0;
                _ramp[i] = 0;
                _water[i] = 0;
                _feature[i] = 0;
            }

        GenerateForestTrees(corridor);
        GenerateRunPonds(corridor);
        GenerateTallGrass(corridor);

        // Pack anchors along the corridor, spaced down the hall, none in the spawn
        // pocket or the boss arena (the first sits past aggro range of the spawn).
        int ax = 17 + rng.Next(5);
        while (ax < Width - 15)
        {
            PackSpots.Add(new Vector2(ax + 0.5f, corridor[ax] + 0.5f));
            ax += 9 + rng.Next(6);
        }

        // Connectivity guarantee: if generation pinched the hall shut anywhere, carve
        // the corridor line flat. Deterministic — clients regenerate the exact map.
        if (!GroundPathExists(PlayerSpawn, new Vector2(Width - 3.5f, corridor[Width - 4] + 0.5f)))
            for (int x = 1; x < Width - 1; x++)
                for (int y = corridor[x] - 1; y <= corridor[x] + 1; y++)
                {
                    if (y < 1 || y >= Height - 1) continue;
                    int i = Idx(x, y);
                    _wall[i] = 0;
                    _ground[i] = 0;
                    _ramp[i] = 0;
                    _water[i] = 0;
                    _feature[i] = 0;
                }

        CleanOrphanRamps();
        ConnectStrandedAreas();
    }

    /// <summary>
    /// Remove orphan stairs: later passes (boss arena, spawn pocket, ponds, the
    /// corridor fallback) can flatten the terrace a stair used to climb, leaving a
    /// stray staircase embedded in flat ground. A ramp is only kept when its ascent
    /// side actually reaches walkable ground one level up AND its low side is
    /// walkable ground at the ramp's own level.
    /// </summary>
    private void CleanOrphanRamps()
    {
        bool WalkableAt(int x, int y, int level)
        {
            if (x < 1 || y < 1 || x >= Width - 1 || y >= Height - 1) return false;
            int i = Idx(x, y);
            return _wall[i] == 0 && _water[i] == 0 && _ramp[i] == 0 && _ground[i] == level;
        }
        for (int y = 1; y < Height - 1; y++)
            for (int x = 1; x < Width - 1; x++)
            {
                int i = Idx(x, y);
                if (_ramp[i] == 0) continue;
                var (dx, dy) = (RampDirection)_ramp[i] switch
                {
                    RampDirection.PlusX => (1, 0),
                    RampDirection.MinusX => (-1, 0),
                    RampDirection.PlusY => (0, 1),
                    _ => (0, -1),
                };
                int g = _ground[i];
                if (WalkableAt(x + dx, y + dy, g + 1) && WalkableAt(x - dx, y - dy, g)) continue;
                _ramp[i] = 0;
                _rampStyle[i] = 0;
            }
    }

    /// <summary>Ramps whose ascent side does NOT reach walkable ground one level up —
    /// the "staircase to nowhere" the cleanup pass exists to prevent. Should be zero
    /// after generation (ConnectStrandedAreas only carves ramps whose ascent side is
    /// checked walkable ground+1 by construction). Test hook.</summary>
    public int CountOrphanRamps()
    {
        int orphans = 0;
        for (int y = 1; y < Height - 1; y++)
            for (int x = 1; x < Width - 1; x++)
            {
                int i = Idx(x, y);
                if (_ramp[i] == 0) continue;
                var (dx, dy) = (RampDirection)_ramp[i] switch
                {
                    RampDirection.PlusX => (1, 0),
                    RampDirection.MinusX => (-1, 0),
                    RampDirection.PlusY => (0, 1),
                    _ => (0, -1),
                };
                int nx = x + dx, ny = y + dy;
                if (nx < 1 || ny < 1 || nx >= Width - 1 || ny >= Height - 1) { orphans++; continue; }
                int ni = Idx(nx, ny);
                if (_wall[ni] != 0 || _water[ni] != 0 || _ground[ni] != _ground[i] + 1)
                    orphans++;
            }
        return orphans;
    }

    /// <summary>
    /// No unreachable pockets: BFS the reachable set from the spawn, then carve stair
    /// ramps wherever a stranded walkable area sits one clean level from reachable
    /// ground (repeats until everything connects). Any sliver still stranded after
    /// that — pinched between water, pillars and walls — becomes a low rock outcrop
    /// so the map never SHOWS ground a player can't stand on. Deterministic.
    /// </summary>
    private void ConnectStrandedAreas()
    {
        // Empty, walkable, un-decorated ground — the only tiles a stair may occupy.
        bool Clear(int x, int y)
        {
            if (x < 1 || y < 1 || x >= Width - 1 || y >= Height - 1) return false;
            int i = Idx(x, y);
            return _wall[i] == 0 && _water[i] == 0 && _ramp[i] == 0 &&
                   _bridge[i] == 0 && _feature[i] == 0;
        }
        // A stair may sit at (rx,ry) ascending (dx,dy) when both its own tile and the
        // ascent-side tile are clear with a one-level rise.
        bool ValidStair(int rx, int ry, int dx, int dy) =>
            Clear(rx, ry) && Clear(rx + dx, ry + dy) &&
            _ground[Idx(rx + dx, ry + dy)] == _ground[Idx(rx, ry)] + 1;

        Span<(int dx, int dy, RampDirection up)> dirs = stackalloc (int, int, RampDirection)[]
        {
            (1, 0, RampDirection.PlusX), (-1, 0, RampDirection.MinusX),
            (0, 1, RampDirection.PlusY), (0, -1, RampDirection.MinusY),
        };
        for (int guard = 0; guard < 40; guard++)
        {
            var reach = ReachableFrom(PlayerSpawn);
            // Gather EVERY spot where a stair would bridge reachable ground to a
            // stranded area one level away, then carve the most natural-looking one —
            // first-found placement used to leave lone stairs poking out of plateau
            // interiors at whatever corner the scan touched first.
            int bestScore = int.MinValue, bestX = 0, bestY = 0, bestDx = 0, bestDy = 0;
            RampDirection bestDir = RampDirection.None;
            for (int y = 1; y < Height - 1; y++)
                for (int x = 1; x < Width - 1; x++)
                {
                    foreach (var (dx, dy, up) in dirs)
                    {
                        // The stair tile is the LOW side; ascent side is one level up.
                        // Connectivity needs exactly one of the two sides reachable.
                        if (!ValidStair(x, y, dx, dy)) continue;
                        bool lowReach = reach[Idx(x, y)];
                        bool highReach = reach[Idx(x + dx, y + dy)];
                        if (lowReach == highReach) continue;

                        int score = 0;
                        int g = _ground[Idx(x, y)];
                        // A clean walk-up (level ground behind the stair) strictly
                        // dominates every cosmetic preference — a stair you approach
                        // through a wall never beats one you can actually walk onto.
                        if (Clear(x - dx, y - dy) && _ground[Idx(x - dx, y - dy)] == g) score += 100;
                        // Mid-cliff, not a corner: the ascent row continues sideways.
                        int lx = dy == 0 ? 0 : 1, ly = dx == 0 ? 0 : 1; // lateral axis
                        if (InBounds(x + dx + lx, y + dy + ly) &&
                            _ground[Idx(x + dx + lx, y + dy + ly)] == g + 1) score += 1;
                        if (InBounds(x + dx - lx, y + dy - ly) &&
                            _ground[Idx(x + dx - lx, y + dy - ly)] == g + 1) score += 1;
                        // Room to widen into a proper two-tile staircase.
                        if (ValidStair(x + lx, y + ly, dx, dy) ||
                            ValidStair(x - lx, y - ly, dx, dy)) score += 2;
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestX = x; bestY = y; bestDx = dx; bestDy = dy; bestDir = up;
                        }
                    }
                }
            if (bestDir == RampDirection.None) break;
            _ramp[Idx(bestX, bestY)] = (byte)bestDir;
            _rampStyle[Idx(bestX, bestY)] = 1;
            // No clean-approach candidate existed anywhere for this region (water or a
            // pillar hugs the only usable cliff): open the approach tile up — one pond
            // tile becomes a stone ford, one pillar tile gives way — so the stair never
            // climbs out of water or a wall.
            if (bestScore < 100)
            {
                int ax = bestX - bestDx, ay = bestY - bestDy;
                if (ax >= 1 && ay >= 1 && ax < Width - 1 && ay < Height - 1)
                {
                    int ai = Idx(ax, ay);
                    if ((_water[ai] != 0 || _wall[ai] != 0) && _feature[ai] == 0 &&
                        _ground[ai] == _ground[Idx(bestX, bestY)])
                    {
                        _wall[ai] = 0;
                        _water[ai] = 0;
                    }
                }
            }
            // Widen to two tiles where the cliff allows — lone one-tile stairs read
            // as generation glitches; a two-wide flight reads as intentional.
            int wlx = bestDy == 0 ? 0 : 1, wly = bestDx == 0 ? 0 : 1;
            foreach (var (sx, sy) in new[] { (bestX + wlx, bestY + wly), (bestX - wlx, bestY - wly) })
                if (ValidStair(sx, sy, bestDx, bestDy) &&
                    _ground[Idx(sx, sy)] == _ground[Idx(bestX, bestY)])
                {
                    _ramp[Idx(sx, sy)] = (byte)bestDir;
                    _rampStyle[Idx(sx, sy)] = 1;
                    break;
                }
        }
        // Whatever is STILL stranded becomes scenery, never fake floor.
        var final = ReachableFrom(PlayerSpawn);
        for (int y = 1; y < Height - 1; y++)
            for (int x = 1; x < Width - 1; x++)
            {
                int i = Idx(x, y);
                if (final[i] || _wall[i] != 0 || _water[i] != 0 || _feature[i] != 0) continue;
                _wall[i] = 1;
                _ramp[i] = 0;
                _rampStyle[i] = 0;
                _tallGrass[i] = 0;
            }
    }

    /// <summary>Walkable-looking tiles NOT reachable from the player spawn (should be
    /// zero on generated run maps — the connectivity pass guarantees it). Test hook.</summary>
    public int CountUnreachableWalkable()
    {
        var reach = ReachableFrom(PlayerSpawn);
        int stranded = 0;
        for (int y = 1; y < Height - 1; y++)
            for (int x = 1; x < Width - 1; x++)
                if (!IsSolid(x, y) && !IsWater(x, y) && !reach[Idx(x, y)])
                    stranded++;
        return stranded;
    }

    /// <summary>The set of tiles walkable from a start point (same edge rule as
    /// GroundPathExists).</summary>
    private bool[] ReachableFrom(Vector2 from)
    {
        var seen = new bool[Width * Height];
        int sx = (int)MathF.Floor(from.X), sy = (int)MathF.Floor(from.Y);
        if (!InBounds(sx, sy)) return seen;
        var queue = new Queue<int>();
        seen[Idx(sx, sy)] = true;
        queue.Enqueue(Idx(sx, sy));
        Span<(int dx, int dy)> dirs = stackalloc (int, int)[] { (1, 0), (-1, 0), (0, 1), (0, -1) };
        while (queue.Count > 0)
        {
            int node = queue.Dequeue();
            int x = node % Width, y = node / Width;
            foreach (var (dx, dy) in dirs)
            {
                int nx = x + dx, ny = y + dy;
                if (!InBounds(nx, ny) || seen[Idx(nx, ny)]) continue;
                if (IsSolid(nx, ny) || IsWater(nx, ny)) continue;
                var pa = new Vector2(x + 0.5f + dx * 0.49f, y + 0.5f + dy * 0.49f);
                var pb = new Vector2(nx + 0.5f - dx * 0.49f, ny + 0.5f - dy * 0.49f);
                if (MathF.Abs(GroundHeightAt(pa) - GroundHeightAt(pb)) > StepTolerance) continue;
                seen[Idx(nx, ny)] = true;
                queue.Enqueue(Idx(nx, ny));
            }
        }
        return seen;
    }

    /// <summary>Big trees for run maps: same 2x2 solid columns as the arena forest, but
    /// planted anywhere the terrain is uniform (terrace tops included) away from the
    /// corridor line, doors and boss arena.</summary>
    private void GenerateForestTrees(int[] corridor)
    {
        if (Theme?.Id != "forest") return;
        var rng = new Random(Seed ^ 0x466F7245);
        int wanted = Width * Height / 70;
        int planted = 0, attempts = 0;
        while (planted < wanted && attempts++ < wanted * 30)
        {
            int x = rng.Next(2, Width - 3), y = rng.Next(2, Height - 3);
            if (Math.Abs(y - corridor[x]) <= 2 || Math.Abs(y + 1 - corridor[x + 1]) <= 2) continue;
            if (x >= Width - 14 || x <= 7) continue; // arena + spawn stay open
            byte g0 = _ground[Idx(x, y)];
            bool clear = true;
            for (int dy = 0; dy < 2 && clear; dy++)
                for (int dx = 0; dx < 2 && clear; dx++)
                    clear = _wall[Idx(x + dx, y + dy)] == 0 && _ground[Idx(x + dx, y + dy)] == g0 &&
                            _ramp[Idx(x + dx, y + dy)] == 0 && _water[Idx(x + dx, y + dy)] == 0 &&
                            _feature[Idx(x + dx, y + dy)] == 0;
            if (!clear) continue;
            // Only the TRUNK blocks: one solid two-level tile at the root; the rest of
            // the footprint stays walkable (markers keep trees from overlapping) and
            // the canopy simply overhangs it.
            for (int dy = 0; dy < 2; dy++)
                for (int dx = 0; dx < 2; dx++)
                    _feature[Idx(x + dx, y + dy)] = (byte)TileFeature.BigTreePart;
            _wall[Idx(x, y)] = 2;
            _feature[Idx(x, y)] = (byte)TileFeature.BigTreeRoot;
            planted++;
        }
    }

    /// <summary>Ponds for run maps: same organic noisy ellipses, kept off the corridor
    /// line and the arena so water reads as scenery, not a roadblock.</summary>
    private void GenerateRunPonds(int[] corridor)
    {
        var rng = new Random(Seed ^ 0x57415452);
        int ponds = Width / 16;
        for (int p = 0; p < ponds; p++)
        {
            int cx = rng.Next(8, Width - 16), cyp = rng.Next(4, Height - 4);
            float rx = 1.4f + (float)rng.NextDouble() * 1.5f;
            float ry = 1.4f + (float)rng.NextDouble() * 1.5f;
            double wobblePhase = rng.NextDouble() * Math.PI * 2;
            for (int y = cyp - 4; y <= cyp + 4; y++)
                for (int x = cx - 4; x <= cx + 4; x++)
                {
                    if (x < 2 || y < 2 || x >= Width - 2 || y >= Height - 2) continue;
                    if (Math.Abs(y - corridor[x]) <= 1) continue;
                    if (Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), PlayerSpawn) < 6f) continue;
                    int i = Idx(x, y);
                    if (_wall[i] != 0 || _ground[i] != 0 || _ramp[i] != 0 || _bridge[i] != 0 || _feature[i] != 0)
                        continue;
                    float dx = (x - cx) / rx, dy = (y - cyp) / ry;
                    double ang = Math.Atan2(dy, dx);
                    float wobble = 1f + 0.25f * (float)Math.Sin(ang * 3 + wobblePhase);
                    if (dx * dx + dy * dy <= wobble) _water[i] = 1;
                }
        }
    }

    /// <summary>Pokemon-style tall grass: noisy elliptical patches on walkable ground
    /// (any elevation — terrace tops included), the corridor line very much allowed —
    /// wading through cover is the point. Purely visual; no gameplay effect yet.</summary>
    private void GenerateTallGrass(int[] corridor)
    {
        var rng = new Random(Seed ^ 0x47524153); // "GRAS"
        int patches = Math.Max(6, Width / 7);
        for (int p = 0; p < patches; p++)
        {
            int cx = rng.Next(7, Width - 9), cy = rng.Next(3, Height - 3);
            float rx = 1.3f + (float)rng.NextDouble() * 1.6f;
            float ry = 1.1f + (float)rng.NextDouble() * 1.3f;
            double wobblePhase = rng.NextDouble() * Math.PI * 2;
            byte patchLevel = _ground[Idx(Math.Clamp(cx, 0, Width - 1), Math.Clamp(cy, 0, Height - 1))];
            for (int y = cy - 4; y <= cy + 4; y++)
                for (int x = cx - 4; x <= cx + 4; x++)
                {
                    if (x < 1 || y < 1 || x >= Width - 1 || y >= Height - 1) continue;
                    int i = Idx(x, y);
                    if (_wall[i] != 0 || _water[i] != 0 || _ramp[i] != 0 ||
                        _bridge[i] != 0 || _feature[i] != 0) continue;
                    if (_ground[i] != patchLevel) continue; // one patch, one terrace
                    var tileCenter = new Vector2(x + 0.5f, y + 0.5f);
                    if (Vector2.Distance(tileCenter, PlayerSpawn) < 3f) continue;
                    if (ExitDoor != Vector2.Zero && Vector2.Distance(tileCenter, ExitDoor) < 3f) continue;
                    if (BossSpot != Vector2.Zero && Vector2.Distance(tileCenter, BossSpot) < 4f) continue;
                    float dx = (x - cx) / rx, dy = (y - cy) / ry;
                    double ang = Math.Atan2(dy, dx);
                    float wobble = 1f + 0.3f * (float)Math.Sin(ang * 3 + wobblePhase);
                    if (dx * dx + dy * dy <= wobble) _tallGrass[i] = 1;
                }
        }
    }

    /// <summary>Tile-level BFS over GROUND surfaces (ramp-aware, mirroring the server's
    /// flow-field connectivity rule): is there a walkable path between two points?
    /// Used to validate generated runs before players ever load them.</summary>
    public bool GroundPathExists(Vector2 from, Vector2 to)
    {
        int sx = (int)MathF.Floor(from.X), sy = (int)MathF.Floor(from.Y);
        int tx = (int)MathF.Floor(to.X), ty = (int)MathF.Floor(to.Y);
        if (!InBounds(sx, sy) || !InBounds(tx, ty)) return false;
        var seen = new bool[Width * Height];
        var queue = new Queue<int>();
        seen[Idx(sx, sy)] = true;
        queue.Enqueue(Idx(sx, sy));
        Span<(int dx, int dy)> dirs = stackalloc (int, int)[] { (1, 0), (-1, 0), (0, 1), (0, -1) };
        while (queue.Count > 0)
        {
            int node = queue.Dequeue();
            int x = node % Width, y = node / Width;
            if (x == tx && y == ty) return true;
            foreach (var (dx, dy) in dirs)
            {
                int nx = x + dx, ny = y + dy;
                if (!InBounds(nx, ny) || seen[Idx(nx, ny)]) continue;
                if (IsSolid(nx, ny) || IsWater(nx, ny)) continue;
                // Surface heights at the shared edge (ramp-interpolated).
                var pa = new Vector2(x + 0.5f + dx * 0.49f, y + 0.5f + dy * 0.49f);
                var pb = new Vector2(nx + 0.5f - dx * 0.49f, ny + 0.5f - dy * 0.49f);
                if (MathF.Abs(GroundHeightAt(pa) - GroundHeightAt(pb)) > StepTolerance) continue;
                seen[Idx(nx, ny)] = true;
                queue.Enqueue(Idx(nx, ny));
            }
        }
        return false;
    }

    /// <summary>
    /// Water: a few organic ponds on open level-0 ground, from their own seeded stream
    /// (base layout and theme features unchanged for the same seed). Each pond is a
    /// noisy ellipse; tiles only flood where nothing else lives — never in the authored
    /// demo region, near the player spawn, or under walls/ramps/bridges/features.
    /// </summary>
    private void GeneratePonds()
    {
        var rng = new Random(Seed ^ 0x57415452); // "WATR"
        int ponds = 4;
        for (int p = 0; p < ponds; p++)
        {
            int cx = rng.Next(5, Width - 5), cy = rng.Next(5, Height - 5);
            float rx = 1.6f + (float)rng.NextDouble() * 1.6f;
            float ry = 1.6f + (float)rng.NextDouble() * 1.6f;
            double wobblePhase = rng.NextDouble() * Math.PI * 2;
            for (int y = cy - 4; y <= cy + 4; y++)
                for (int x = cx - 4; x <= cx + 4; x++)
                {
                    if (x < 2 || y < 2 || x >= Width - 2 || y >= Height - 2) continue;
                    if (x >= 4 && x <= 19 && y >= 4 && y <= 32) continue; // authored demo region
                    if (Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), PlayerSpawn) < 6f) continue;
                    int i = Idx(x, y);
                    if (_wall[i] != 0 || _ground[i] != 0 || _ramp[i] != 0 || _bridge[i] != 0 || _feature[i] != 0)
                        continue;
                    // Noisy ellipse: the rim wobbles with angle so shores read organic.
                    float dx = (x - cx) / rx, dy = (y - cy) / ry;
                    double ang = Math.Atan2(dy, dx);
                    float wobble = 1f + 0.25f * (float)Math.Sin(ang * 3 + wobblePhase);
                    if (dx * dx + dy * dy <= wobble) _water[i] = 1;
                }
        }
    }

    /// <summary>
    /// Theme-specific landmarks, generated from their own seeded stream so the BASE
    /// layout stays identical across themes for the same seed. The forest grows large
    /// 2x2 trees: solid 2-level wall columns (collision, pathfinding avoidance and
    /// line-of-sight blocking come free) marked with feature flags so the renderer
    /// draws one big canopy sprite instead of blocks.
    /// </summary>
    private void GenerateThemeFeatures()
    {
        if (Theme?.Id != "forest") return;
        var rng = new Random(Seed ^ 0x466F7245); // separate stream: base layout unchanged
        int planted = 0, attempts = 0;
        while (planted < 16 && attempts++ < 400)
        {
            int x = rng.Next(2, Width - 3), y = rng.Next(2, Height - 3);
            // Keep the authored demo region and the spawn area clear of big trees.
            if (x >= 4 && x <= 19 && y >= 4 && y <= 32) continue;
            if (Vector2.Distance(new Vector2(x + 1f, y + 1f), PlayerSpawn) < 5f) continue;
            bool clear = true;
            for (int dy = 0; dy < 2 && clear; dy++)
                for (int dx = 0; dx < 2 && clear; dx++)
                    clear = _wall[Idx(x + dx, y + dy)] == 0 && _ground[Idx(x + dx, y + dy)] == 0 &&
                            _ramp[Idx(x + dx, y + dy)] == 0 && _bridge[Idx(x + dx, y + dy)] == 0 &&
                            _feature[Idx(x + dx, y + dy)] == 0;
            if (!clear) continue;
            // Only the TRUNK blocks (see GenerateForestTrees) — footprint markers just
            // keep trees apart and decorations out from under the canopy.
            for (int dy = 0; dy < 2; dy++)
                for (int dx = 0; dx < 2; dx++)
                    _feature[Idx(x + dx, y + dy)] = (byte)TileFeature.BigTreePart;
            _wall[Idx(x, y)] = 2;
            _feature[Idx(x, y)] = (byte)TileFeature.BigTreeRoot;
            planted++;
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
