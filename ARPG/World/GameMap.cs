using System.Numerics;

namespace ARPG.World;

public enum TileType : byte
{
    Floor = 0,
    Wall = 1,
}

/// <summary>
/// The playable map plus collision. Generated deterministically from a seed, so the host
/// only needs to share the seed with clients for everyone to have an identical map.
/// Gameplay positions are world/tile coordinates — never isometric screen coordinates.
/// </summary>
public class GameMap
{
    public int Width { get; }
    public int Height { get; }
    public int Seed { get; }
    private readonly TileType[] _tiles;

    public Vector2 PlayerSpawn { get; private set; }
    public List<Vector2> EnemySpawns { get; } = new();

    public GameMap(int seed, int width = 44, int height = 44)
    {
        Seed = seed;
        Width = width;
        Height = height;
        _tiles = new TileType[width * height];
        Generate(new Random(seed));
    }

    public TileType Tile(int x, int y) =>
        x < 0 || y < 0 || x >= Width || y >= Height ? TileType.Wall : _tiles[y * Width + x];

    public bool IsWall(int x, int y) => Tile(x, y) == TileType.Wall;
    public bool IsWallAt(Vector2 pos) => IsWall((int)MathF.Floor(pos.X), (int)MathF.Floor(pos.Y));

    private void Generate(Random rng)
    {
        // Border walls.
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
                _tiles[y * Width + x] =
                    (x == 0 || y == 0 || x == Width - 1 || y == Height - 1) ? TileType.Wall : TileType.Floor;

        // Scattered pillar clusters as obstacles.
        int clusters = 26;
        for (int i = 0; i < clusters; i++)
        {
            int cx = rng.Next(4, Width - 5);
            int cy = rng.Next(4, Height - 5);
            int w = rng.Next(1, 4), h = rng.Next(1, 4);
            for (int y = cy; y < Math.Min(cy + h, Height - 2); y++)
                for (int x = cx; x < Math.Min(cx + w, Width - 2); x++)
                    _tiles[y * Width + x] = TileType.Wall;
        }

        // Keep the player spawn area open.
        PlayerSpawn = new Vector2(Width / 2f, Height / 2f);
        for (int y = -2; y <= 2; y++)
            for (int x = -2; x <= 2; x++)
                _tiles[((int)PlayerSpawn.Y + y) * Width + (int)PlayerSpawn.X + x] = TileType.Floor;

        // Enemy spawn points: floor tiles far enough from the player spawn.
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
    /// Move a circle through the map with axis-separated collision (slide along walls).
    /// Returns the final position.
    /// </summary>
    public Vector2 MoveWithCollision(Vector2 from, Vector2 delta, float radius)
    {
        var pos = from;
        var tryX = pos + new Vector2(delta.X, 0);
        if (!CircleHitsWall(tryX, radius)) pos = tryX;
        var tryY = pos + new Vector2(0, delta.Y);
        if (!CircleHitsWall(tryY, radius)) pos = tryY;
        return pos;
    }

    public bool CircleHitsWall(Vector2 center, float radius)
    {
        int minX = (int)MathF.Floor(center.X - radius);
        int maxX = (int)MathF.Floor(center.X + radius);
        int minY = (int)MathF.Floor(center.Y - radius);
        int maxY = (int)MathF.Floor(center.Y + radius);
        for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
            {
                if (!IsWall(x, y)) continue;
                // Closest point on the tile AABB to the circle center.
                float cx = Math.Clamp(center.X, x, x + 1);
                float cy = Math.Clamp(center.Y, y, y + 1);
                float dx = center.X - cx, dy = center.Y - cy;
                if (dx * dx + dy * dy < radius * radius) return true;
            }
        return false;
    }

    /// <summary>Straight-line visibility/projectile blocking test, sampled along the segment.</summary>
    public bool SegmentHitsWall(Vector2 a, Vector2 b)
    {
        float dist = Vector2.Distance(a, b);
        int steps = Math.Max(1, (int)(dist * 4));
        for (int i = 0; i <= steps; i++)
        {
            var p = Vector2.Lerp(a, b, i / (float)steps);
            if (IsWallAt(p)) return true;
        }
        return false;
    }
}
