using FontStashSharp;
using ARPG.Data;
using ARPG.Items;
using ARPG.Net;
using ARPG.World;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using NumVec2 = System.Numerics.Vector2;

namespace ARPG.Render;

/// <summary>Draws the isometric world: tiles, walls, entities, projectiles, drops, effects.</summary>
public class WorldRenderer
{
    private readonly GameData _data;
    private readonly Core.GameSettings _settings;
    private readonly List<(float depth, Action<SpriteBatch> draw)> _sorted = new();

    /// <summary>Screen rectangles of drop name labels this frame, for click-to-pick-up.</summary>
    public readonly List<(Rectangle rect, Guid dropId)> DropLabelRects = new();

    /// <summary>Screen rectangles of enemy sprites this frame, for hover targeting
    /// (front-most = last drawn; hit-test in reverse).</summary>
    public readonly List<(Rectangle rect, int enemyId)> EnemyHitRects = new();
    /// <summary>Enemy currently under the mouse (set by PlayScreen) — tinted red and
    /// shown in the top-of-screen target display.</summary>
    public int HoveredEnemyId = -1;

    private const int WallHeight = 24;

    // ------------------------------------------------------------------ zone theme
    /// <summary>Active visual theme. All palette colors below are parsed from it; the
    /// clutter/feature layout is rebuilt deterministically from the map seed on change.</summary>
    public ZoneTheme Theme { get; private set; }
    public Color BackgroundColor { get; private set; } = new(16, 17, 22);
    private Color _floorA, _floorB, _floorC, _floorD, _cliffFace, _elevTop, _wallFace, _wallTop, _rampTint;
    private Color _deckA, _deckB, _deckLip;
    private GameMap _themedMap;
    private readonly List<(NumVec2 pos, float height, string key)> _clutter = new();
    private readonly List<(int x, int y, int top, string key)> _features = new();

    public void SetTheme(ZoneTheme theme, GameMap map)
    {
        Theme = theme;
        _themedMap = map;
        if (theme == null) return;
        BackgroundColor = ParseColor(theme.Background, new Color(16, 17, 22));
        _floorA = ParseColor(theme.FloorA, new Color(58, 66, 58));
        _floorB = ParseColor(theme.FloorB, new Color(52, 60, 54));
        _floorC = ParseColor(theme.FloorC ?? theme.FloorA, _floorA);
        _floorD = ParseColor(theme.FloorD ?? theme.FloorB, _floorB);
        _cliffFace = ParseColor(theme.CliffFace, new Color(128, 140, 128));
        _elevTop = ParseColor(theme.ElevatedTop, new Color(70, 80, 68));
        _wallFace = ParseColor(theme.WallFace, new Color(140, 133, 173));
        _wallTop = ParseColor(theme.WallTop, new Color(84, 80, 104));
        _rampTint = ParseColor(theme.RampTint, new Color(150, 160, 130));
        _deckA = ParseColor(theme.DeckA, new Color(122, 96, 62));
        _deckB = ParseColor(theme.DeckB, new Color(112, 88, 58));
        _deckLip = ParseColor(theme.DeckLip, new Color(70, 54, 36));
        GeneratePaths(map);
        RebuildProps(map);
    }

    /// <summary>Deterministic per-tile hash (independent of process randomization) so
    /// every client lays out identical decoration for the same map seed. Uses a full
    /// avalanche mix — low bits of a plain multiply-xor correlate with tile parity and
    /// read as a checkerboard on organic floors.</summary>
    private static uint TileHash(int seed, int x, int y)
    {
        uint n = (uint)(x * 73856093 ^ y * 19349663 ^ seed * 83492791);
        n ^= n >> 16; n *= 0x7feb352d;
        n ^= n >> 15; n *= 0x846ca68b;
        n ^= n >> 16;
        return n;
    }

    /// <summary>Smooth per-tile value noise in [0,1]: hashed lattice values interpolated
    /// with smoothstep, so ADJACENT tiles differ only slightly — this is what blends
    /// organic ground without any visible tile boundary.</summary>
    private static float ValueNoise(int seed, float x, float y, float freq)
    {
        float fx = x * freq, fy = y * freq;
        int x0 = (int)MathF.Floor(fx), y0 = (int)MathF.Floor(fy);
        float tx = fx - x0, ty = fy - y0;
        tx = tx * tx * (3f - 2f * tx);
        ty = ty * ty * (3f - 2f * ty);
        float V(int gx, int gy) => (TileHash(seed, gx, gy) & 0xFFFFFF) / (float)0xFFFFFF;
        float a = V(x0, y0), b = V(x0 + 1, y0), c = V(x0, y0 + 1), d = V(x0 + 1, y0 + 1);
        return MathHelper.Lerp(MathHelper.Lerp(a, b, tx), MathHelper.Lerp(c, d, tx), ty);
    }

    /// <summary>Two octaves of value noise: broad patches plus fine mottling.</summary>
    private float GroundNoise(int seed, int x, int y) =>
        0.62f * ValueNoise(seed, x, y, 0.21f) + 0.38f * ValueNoise(seed ^ unchecked((int)0x9E3779B9), x, y, 0.57f);

    private static Color LerpColor(Color a, Color b, float t) => new(
        (int)MathHelper.Lerp(a.R, b.R, t),
        (int)MathHelper.Lerp(a.G, b.G, t),
        (int)MathHelper.Lerp(a.B, b.B, t));

    /// <summary>Winding dirt paths (organic themes): seed-generated random walks across
    /// the map, purely visual — floor tiles recolored to dirt with blended edges, and
    /// clutter stays off the trail.</summary>
    private readonly HashSet<int> _pathTiles = new();
    /// <summary>Per-tile path strength 0..1 (core 1, fading rings outward), noise-scaled
    /// at draw time so trail edges are ragged instead of diamond-staircased.</summary>
    private readonly Dictionary<int, float> _pathField = new();

    private void GeneratePaths(GameMap map)
    {
        _pathTiles.Clear();
        _pathField.Clear();
        if (Theme?.OrganicFloor != true) return;
        var rng = new Random(map.Seed ^ 0x50415448);
        bool Walkable(int tx, int ty) =>
            tx > 0 && ty > 0 && tx < map.Width - 1 && ty < map.Height - 1 &&
            !map.IsSolid(tx, ty) && map.Ramp(tx, ty) == RampDirection.None &&
            map.GroundLevel(tx, ty) == 0 && map.BridgeLevel(tx, ty) == 0;
        void Mark(int tx, int ty)
        {
            if (Walkable(tx, ty)) _pathTiles.Add(ty * map.Width + tx);
        }
        for (int p = 0; p < 3; p++)
        {
            bool horizontal = rng.Next(2) == 0;
            var pos = horizontal
                ? new NumVec2(2, rng.Next(4, map.Height - 4))
                : new NumVec2(rng.Next(4, map.Width - 4), 2);
            var goal = horizontal
                ? new NumVec2(map.Width - 3, rng.Next(4, map.Height - 4))
                : new NumVec2(rng.Next(4, map.Width - 4), map.Height - 3);
            for (int step = 0; step < 160 && NumVec2.Distance(pos, goal) > 2f; step++)
            {
                var dir = NumVec2.Normalize(goal - pos);
                float wobble = (float)(rng.NextDouble() * 2 - 1) * 0.9f;
                var d = new NumVec2(
                    dir.X * MathF.Cos(wobble) - dir.Y * MathF.Sin(wobble),
                    dir.X * MathF.Sin(wobble) + dir.Y * MathF.Cos(wobble));
                pos += d;
                int tx = (int)pos.X, ty = (int)pos.Y;
                Mark(tx, ty);
                if (rng.Next(3) != 0) Mark(tx + rng.Next(-1, 2), ty + rng.Next(-1, 2));
            }
        }
        // Graded field: core tiles at full strength, two fading rings outward. The
        // draw pass scales this by fine noise, so the trail's boundary wanders
        // naturally instead of following tile diamonds.
        foreach (int key in _pathTiles) _pathField[key] = 1f;
        void Spread(float strength)
        {
            foreach (var (key, val) in _pathField.ToList())
            {
                if (val < strength + 0.01f) continue;
                int tx = key % map.Width, ty = key / map.Width;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nk = (ty + dy) * map.Width + (tx + dx);
                        if (Walkable(tx + dx, ty + dy) && _pathField.GetValueOrDefault(nk) < strength)
                            _pathField[nk] = strength;
                    }
            }
        }
        Spread(0.55f);
        Spread(0.22f);
    }

    private void RebuildProps(GameMap map)
    {
        _clutter.Clear();
        _features.Clear();
        if (map == null || Theme == null) return;
        string style = Theme.PropStyle;
        for (int y = 1; y < map.Height - 1; y++)
            for (int x = 1; x < map.Width - 1; x++)
            {
                uint n = TileHash(map.Seed, x, y);
                float roll = (n & 0xFFFF) / 65536f;
                int wall = map.WallHeight(x, y);
                if (wall == 1 && map.GroundLevel(x, y) == 0)
                {
                    if (roll < Theme.WallFeatureChance)
                        _features.Add((x, y, wall, $"{style}:feature:{(n >> 16) % 2}"));
                    continue;
                }
                if (wall > 0 || map.Ramp(x, y) != RampDirection.None || map.BridgeLevel(x, y) > 0)
                    continue;
                if (_pathTiles.Contains(y * map.Width + x)) continue; // trails stay clear
                if (roll < Theme.ClutterDensity)
                {
                    float ox = ((n >> 16 & 0xFF) / 255f - 0.5f) * 0.6f;
                    float oy = ((n >> 24 & 0xFF) / 255f - 0.5f) * 0.6f;
                    var pos = new NumVec2(x + 0.5f + ox, y + 0.5f + oy);
                    _clutter.Add((pos, map.GroundLevel(x, y), $"{style}:clutter:{(n >> 8) % 3}"));
                }
            }
    }

    /// <summary>Sprite tint marking elite affixes (boss purple, brutish red, swift gold,
    /// warded blue) so dangerous enemies stand out before you read their name.</summary>
    private static Color EliteTint(ClientEnemy e)
    {
        if (e.IsBoss) return new Color(228, 178, 255);
        if ((e.EliteFlags & 1) != 0) return new Color(255, 158, 148);
        if ((e.EliteFlags & 2) != 0) return new Color(255, 238, 150);
        if ((e.EliteFlags & 4) != 0) return new Color(160, 195, 255);
        return Color.White;
    }

    public WorldRenderer(GameData data, Core.GameSettings settings)
    {
        _data = data;
        _settings = settings;
    }

    public void Draw(SpriteBatch sb, IsoCamera camera, ClientWorld world)
    {
        var map = world.Map;
        if (map == null) return;
        if (Theme == null || _themedMap != map)
            SetTheme(map.Theme ?? _data.ZoneThemes.FirstOrDefault(), map);
        DropLabelRects.Clear();
        EnemyHitRects.Clear();

        // --- base pass: flat level-0 floor tiles (nothing ever renders beneath them) ---
        // Grid style: the classic checker with subtle tile edges. Organic style (forest):
        // per-tile shades picked by seed hash on a gridless diamond, plus tiny speckles,
        // so the ground reads as continuous terrain instead of a board.
        bool organic = Theme?.OrganicFloor == true;
        var floorA = Theme != null ? _floorA : new Color(58, 66, 58);
        var floorB = Theme != null ? _floorB : new Color(52, 60, 54);
        for (int y = 0; y < map.Height; y++)
        {
            for (int x = 0; x < map.Width; x++)
            {
                if ((map.IsSolid(x, y) && map.Feature(x, y) == TileFeature.None) ||
                    map.GroundLevel(x, y) > 0 || map.Ramp(x, y) != RampDirection.None)
                    continue;
                var screen = camera.WorldToScreen(new NumVec2(x + 0.5f, y + 0.5f));
                if (screen.X < -80 || screen.X > camera.ScreenWidth + 80 ||
                    screen.Y < -80 || screen.Y > camera.ScreenHeight + 80) continue;
                if (!organic)
                {
                    var tint = ((x + y) & 1) == 0 ? floorA : floorB;
                    sb.Draw(TextureGen.Diamond, new Vector2((int)screen.X - 32, (int)screen.Y - 16), tint);
                    continue;
                }
                uint n = TileHash(map.Seed, x, y);
                // Smooth noise field: neighbors differ only slightly, so tile edges
                // disappear and the ground reads as continuous mottled terrain.
                float gn = GroundNoise(map.Seed, x, y);
                var shade = LerpColor(_floorD, _floorC, gn);
                int key = y * map.Width + x;
                float trail = _pathField.GetValueOrDefault(key);
                bool onPath = trail > 0.6f;
                if (trail > 0f)
                {
                    // Ragged edge: strength wobbles with fine noise so the dirt/grass
                    // boundary wanders across tiles instead of tracing diamonds.
                    float ragged = Math.Clamp(trail * (0.55f + 0.9f * ValueNoise(
                        map.Seed ^ 0x7261696C, x, y, 0.9f)), 0f, 1f);
                    var dirt = LerpColor(new Color(96, 78, 50), new Color(116, 96, 62), gn);
                    shade = LerpColor(shade, dirt, ragged);
                }
                sb.Draw(TextureGen.DiamondFlat, new Vector2((int)screen.X - 32, (int)screen.Y - 16), shade);
                // Ground detail: tiny grass blades off-trail, pebbles on it.
                var dark = new Color((int)(shade.R * 0.8f), (int)(shade.G * 0.8f), (int)(shade.B * 0.8f));
                var lite = new Color(Math.Min(255, shade.R + 22), Math.Min(255, shade.G + 30), Math.Min(255, shade.B + 16));
                for (int spk = 0; spk < 3; spk++)
                {
                    int shift = 6 + spk * 9;
                    int sx1 = (int)(10 + (n >> shift) % 44), sy1 = (int)(6 + (n >> (shift + 5)) % 18);
                    if (!onPath && ((n >> shift) & 3) != 0)
                        sb.Draw(TextureGen.Pixel, new Rectangle((int)screen.X - 32 + sx1, (int)screen.Y - 16 + sy1 - 1, 1, 2),
                            spk == 1 ? lite : new Color(88, 138, 66)); // grass blade
                    else
                        sb.Draw(TextureGen.Pixel, new Rectangle((int)screen.X - 32 + sx1, (int)screen.Y - 16 + sy1, 2, 1), dark);
                }
            }
        }

        // --- depth-sorted world objects: elevated terrain, bridges and entities share ---
        // --- ONE painter's list so tall geometry occludes what stands behind/under it ---
        _sorted.Clear();

        // Entities/effects UNDERNEATH a bridge deck sort a full unit lower, so every
        // tile of the deck above draws over them (heads no longer poke through the
        // deck plane); entities ON the deck are unaffected (their height ~ the deck's).
        float UnderDeckBias(NumVec2 pos, float h)
        {
            int b = map.BridgeLevel((int)MathF.Floor(pos.X), (int)MathF.Floor(pos.Y));
            return b > 0 && h < b - 0.5f ? -1f : 0f;
        }

        // Occlusion reveal: terrain that would draw OVER the local player fades out
        // near them (per-tile alpha with distance falloff — reads as a soft circle),
        // so your own character stays visible behind walls and under bridge decks.
        ClientPlayer localPlayer = null;
        foreach (var pp in world.Players.Values)
            if (pp.IsLocal && pp.Alive) { localPlayer = pp; break; }
        Vector2 revealPoint = default;
        float localDepth = float.MaxValue;
        if (localPlayer != null)
        {
            var ls = camera.WorldToScreen(localPlayer.Position, localPlayer.Height);
            revealPoint = new Vector2(ls.X, ls.Y - 14); // body center, not feet
            localDepth = localPlayer.Position.X + localPlayer.Position.Y +
                         localPlayer.Height * 1.0f + 0.1f +
                         UnderDeckBias(localPlayer.Position, localPlayer.Height);
        }

        float OccluderFade(float depth, Rectangle spriteRect)
        {
            const float Radius = 96f;
            if (localPlayer == null || depth <= localDepth) return 1f;
            // Only geometry that rises ABOVE the body center can hide the character —
            // low front terrain (a one-level ledge by your feet) keeps full opacity.
            if (spriteRect.Top > revealPoint.Y) return 1f;
            float cx = Math.Clamp(revealPoint.X, spriteRect.Left, spriteRect.Right);
            float cy = Math.Clamp(revealPoint.Y, spriteRect.Top, spriteRect.Bottom);
            float dist = Vector2.Distance(new Vector2(cx, cy), revealPoint);
            if (dist >= Radius) return 1f;
            return MathHelper.Lerp(0.35f, 1f, dist / Radius);
        }

        // Depth key convention: walkable TOPS sort low (tile x + y + level*0.6) so
        // entities draw above their floor; occluders (prism side faces, bridge decks)
        // sort like an occupant of their tile (x + y + 1 + ...). Entities use their
        // continuous position plus height*1.0 + 0.1 — high enough that standing
        // anywhere on a deck clears the deck's key, while entities walking UNDER the
        // deck stay below it.
        for (int y = 0; y < map.Height; y++)
        {
            for (int x = 0; x < map.Width; x++)
            {
                int ground = map.GroundLevel(x, y);
                int wall = map.WallHeight(x, y);
                var ramp = map.Ramp(x, y);
                int bridge = map.BridgeLevel(x, y);
                bool elevated = ground > 0 || wall > 0 || ramp != RampDirection.None || bridge > 0;
                if (!elevated) continue;

                var baseScreen = camera.WorldToScreen(new NumVec2(x + 0.5f, y + 0.5f));
                if (baseScreen.X < -96 || baseScreen.X > camera.ScreenWidth + 96 ||
                    baseScreen.Y < -200 || baseScreen.Y > camera.ScreenHeight + 200) continue;

                int hpx = IsoCamera.LevelHeightPx;

                // Depth keys: TOP surfaces stay low (x+y + level*0.6) so entities standing
                // on/near them draw above; SIDE FACES are occluders and sort like an object
                // standing on the tile (x+y+1) with the tiny height term only breaking ties,
                // so a tall column BEHIND a short one can no longer paint over it. Face
                // geometry matches diamond edges exactly, so overdraw between neighbors is
                // seamless instead of jagged.
                var tileFeature = map.Feature(x, y);
                if (tileFeature != TileFeature.None)
                {
                    // Multi-tile generated feature (forest big tree): the wall data
                    // supplies collision/LOS; the ROOT tile draws one large sprite
                    // anchored at the 2x2 footprint's center instead of blocks.
                    if (tileFeature == TileFeature.BigTreeRoot)
                    {
                        var tex = SpriteGen.GetPropSprite($"forest:bigtree:{(TileHash(map.Seed, x, y) >> 5) % 2}");
                        if (tex != null)
                        {
                            var center = camera.WorldToScreen(new NumVec2(x + 1f, y + 1f));
                            int tw = tex.Width * 2, th = tex.Height * 2;
                            var dest = new Rectangle((int)center.X - tw / 2, (int)center.Y - th + 10, tw, th);
                            float tdepth = x + y + 2 + 0.002f; // occupant of the footprint center
                            float tfade = OccluderFade(tdepth, dest);
                            _sorted.Add((tdepth, batch => batch.Draw(tex, dest, Color.White * tfade)));
                        }
                    }
                    continue;
                }

                if (wall > 0)
                {
                    int top = ground + wall;
                    int topPx = top * hpx;
                    float faceDepth = x + y + 1 + top * 0.001f;
                    float faceFade = OccluderFade(faceDepth,
                        new Rectangle((int)baseScreen.X - 32, (int)baseScreen.Y - topPx, 64, topPx + 16));
                    var faceTint = _wallFace * faceFade;
                    _sorted.Add((faceDepth, batch =>
                        batch.Draw(TextureGen.GetPrismFaces(top),
                            new Vector2((int)baseScreen.X - 32, (int)baseScreen.Y - topPx), faceTint)));
                    float topDepth = x + y + top * 0.6f;
                    var wallTopColor = _wallTop;
                    if (organic)
                    {
                        float wn = GroundNoise(map.Seed, x, y);
                        wallTopColor = LerpColor(
                            new Color(Math.Max(0, _wallTop.R - 9), Math.Max(0, _wallTop.G - 9), Math.Max(0, _wallTop.B - 9)),
                            new Color(_wallTop.R + 11, _wallTop.G + 13, _wallTop.B + 9), wn);
                    }
                    var topTint = wallTopColor * OccluderFade(topDepth,
                        new Rectangle((int)baseScreen.X - 32, (int)baseScreen.Y - 16 - topPx, 64, 32));
                    var wtTex = organic ? TextureGen.DiamondFlat : TextureGen.DiamondSolid;
                    _sorted.Add((topDepth, batch =>
                        batch.Draw(wtTex,
                            new Vector2((int)baseScreen.X - 32, (int)baseScreen.Y - 16 - topPx),
                            topTint)));
                    continue;
                }

                if (ramp != RampDirection.None)
                {
                    // Transition tile: baked sloped-ramp or stairs sprite (its own skirt
                    // included), anchored at the tile's (x, y) corner at the LOW level.
                    var sprite = TextureGen.GetRampSprite(ramp, map.RampIsStairs(x, y));
                    var corner = camera.WorldToScreen(new NumVec2(x, y), ground);
                    float rampDepth = x + y + (ground + 0.5f) * 0.6f;
                    var rampTint = _rampTint * OccluderFade(rampDepth,
                        new Rectangle((int)corner.X - 32, (int)corner.Y - TextureGen.RampSpriteOffsetY, 64, 96));
                    _sorted.Add((rampDepth, batch =>
                        batch.Draw(sprite,
                            new Vector2((int)corner.X - 32, (int)corner.Y - TextureGen.RampSpriteOffsetY),
                            rampTint)));
                    if (ground > 0)
                    {
                        float rbDepth = x + y + 1 + ground * 0.001f;
                        var rbTint = _cliffFace * OccluderFade(rbDepth,
                            new Rectangle((int)baseScreen.X - 32, (int)baseScreen.Y - ground * hpx, 64, ground * hpx + 16));
                        _sorted.Add((rbDepth, batch =>
                            batch.Draw(TextureGen.GetPrismFaces(ground),
                                new Vector2((int)baseScreen.X - 32, (int)baseScreen.Y - ground * hpx),
                                rbTint)));
                    }
                }
                else if (ground > 0)
                {
                    // Elevated ground: top diamond at its level plus prism faces down to
                    // the ground floor (front tiles' tops cover interior faces).
                    int topPx = ground * hpx;
                    int lift = ground * 11;
                    Color top;
                    if (organic)
                    {
                        float en = GroundNoise(map.Seed, x, y);
                        var baseTop = LerpColor(
                            new Color(Math.Max(0, _elevTop.R - 8), Math.Max(0, _elevTop.G - 8), Math.Max(0, _elevTop.B - 8)),
                            new Color(_elevTop.R + 10, _elevTop.G + 12, _elevTop.B + 8), en);
                        top = new Color(
                            Math.Min(255, baseTop.R + lift), Math.Min(255, baseTop.G + lift), Math.Min(255, baseTop.B + lift));
                    }
                    else
                    {
                        int check = ((x + y) & 1) == 0 ? 4 : 0;
                        top = new Color(
                            Math.Min(255, _elevTop.R + lift + check),
                            Math.Min(255, _elevTop.G + lift + check),
                            Math.Min(255, _elevTop.B + lift + check));
                    }
                    bool Exposed(int nx, int ny) =>
                        !map.IsSolid(nx, ny) &&
                        (map.GroundLevel(nx, ny) < ground || map.Ramp(nx, ny) != RampDirection.None);
                    if (Exposed(x + 1, y) || Exposed(x, y + 1))
                    {
                        float efDepth = x + y + 1 + ground * 0.001f;
                        var efTint = _cliffFace * OccluderFade(efDepth,
                            new Rectangle((int)baseScreen.X - 32, (int)baseScreen.Y - topPx, 64, topPx + 16));
                        _sorted.Add((efDepth, batch =>
                            batch.Draw(TextureGen.GetPrismFaces(ground),
                                new Vector2((int)baseScreen.X - 32, (int)baseScreen.Y - topPx),
                                efTint)));
                    }
                    float etDepth = x + y + ground * 0.6f;
                    var etTint = top * OccluderFade(etDepth,
                        new Rectangle((int)baseScreen.X - 32, (int)baseScreen.Y - 16 - topPx, 64, 32));
                    var etTex = organic ? TextureGen.DiamondFlat : TextureGen.DiamondSolid;
                    _sorted.Add((etDepth, batch =>
                        batch.Draw(etTex,
                            new Vector2((int)baseScreen.X - 32, (int)baseScreen.Y - 16 - topPx), etTint)));
                }

                if (bridge > 0)
                {
                    // Bridge deck: a plank-toned diamond floating at its level with a thin
                    // lip. The ground below was already drawn (base pass or elevated above),
                    // so entities beneath draw between the two in the depth order.
                    int deckPx = bridge * hpx;
                    float depth = x + y + 1 + bridge * 0.6f;
                    float deckFade = OccluderFade(depth,
                        new Rectangle((int)baseScreen.X - 32, (int)baseScreen.Y - 16 - deckPx, 64, 38));
                    var deckColor = organic
                        ? LerpColor(_deckB, _deckA, GroundNoise(map.Seed, x, y))
                        : ((x + y) & 1) == 0 ? _deckA : _deckB;
                    var deck = deckColor * deckFade;
                    var lip = _deckLip * deckFade;
                    _sorted.Add((depth, batch =>
                    {
                        batch.Draw(TextureGen.Pixel,
                            new Rectangle((int)baseScreen.X - 32, (int)baseScreen.Y - deckPx, 64, 6),
                            lip);
                        batch.Draw(organic ? TextureGen.DiamondFlat : TextureGen.DiamondSolid,
                            new Vector2((int)baseScreen.X - 32, (int)baseScreen.Y - 16 - deckPx), deck);
                    }));
                }
            }
        }

        // Themed decoration: ground clutter (no collision) and wall-tile features
        // (trees, crypts, spires standing on their base block — collision comes free
        // from the wall tile). Both fade with the occlusion reveal like terrain.
        foreach (var (cpos, cheight, ckey) in _clutter)
        {
            var tex = SpriteGen.GetPropSprite(ckey);
            if (tex == null) continue;
            var screen = camera.WorldToScreen(cpos, cheight);
            if (screen.X < -60 || screen.X > camera.ScreenWidth + 60 ||
                screen.Y < -60 || screen.Y > camera.ScreenHeight + 60) continue;
            int cw = tex.Width * 2, ch = tex.Height * 2;
            var dest = new Rectangle((int)screen.X - cw / 2, (int)screen.Y - ch + 3, cw, ch);
            _sorted.Add((cpos.X + cpos.Y + cheight * 1.0f + 0.04f + UnderDeckBias(cpos, cheight), batch =>
                batch.Draw(tex, dest, Color.White)));
        }
        foreach (var (fx0, fy0, ftop, fkey) in _features)
        {
            var tex = SpriteGen.GetPropSprite(fkey);
            if (tex == null) continue;
            var screen = camera.WorldToScreen(new NumVec2(fx0 + 0.5f, fy0 + 0.5f), ftop);
            if (screen.X < -80 || screen.X > camera.ScreenWidth + 80 ||
                screen.Y < -100 || screen.Y > camera.ScreenHeight + 100) continue;
            int fw = tex.Width * 2, fh = tex.Height * 2;
            var dest = new Rectangle((int)screen.X - fw / 2, (int)screen.Y - fh + 4, fw, fh);
            float fdepth = fx0 + fy0 + 1 + ftop * 0.001f + 0.0005f;
            float ffade = OccluderFade(fdepth, dest);
            _sorted.Add((fdepth, batch => batch.Draw(tex, dest, Color.White * ffade)));
        }

        foreach (var drop in world.Drops.Values)
        {
            var pos = drop.Position;
            var screen = camera.WorldToScreen(pos, drop.Height);
            var item = drop.Item;
            _sorted.Add((pos.X + pos.Y + drop.Height * 1.0f + 0.05f + UnderDeckBias(pos, drop.Height), batch =>
            {
                if (drop.IsGold)
                {
                    var pile = SpriteGen.GetGoldPile();
                    if (pile != null)
                        batch.Draw(pile, new Rectangle((int)screen.X - pile.Width, (int)screen.Y - pile.Height,
                            pile.Width * 2, pile.Height * 2), Color.White);
                    return;
                }
                var enchantTex = SpriteGen.GetEnchantScrollSprite(item.GetBase(_data));
                if (enchantTex != null)
                {
                    batch.Draw(enchantTex, new Rectangle((int)screen.X - 12, (int)screen.Y - 16, 24, 24), Color.White);
                    return;
                }
                var weaponTex = SpriteGen.GetWeaponSprite(item.GetBase(_data));
                if (weaponTex != null)
                {
                    // Weapons lie on the ground as their actual sprite (diagonal, as if dropped).
                    batch.Draw(weaponTex, new Vector2(screen.X, screen.Y - 4), null, Color.White,
                        -MathF.PI / 5f, new Vector2(weaponTex.Width / 2f, weaponTex.Height / 2f),
                        1.6f, SpriteEffects.None, 0f);
                    return;
                }
                var color = RarityColor(item.Rarity);
                batch.Draw(TextureGen.Diamond,
                    new Rectangle((int)screen.X - 10, (int)screen.Y - 5, 20, 10), color);
            }));
        }

        long animClock = Environment.TickCount64; // visual-only animation timer
        foreach (var e in world.Enemies.Values)
        {
            var pos = e.Position;
            var screen = camera.WorldToScreen(pos, e.Height);
            var def = e.Def;
            var color = ParseColor(def?.Color, new Color(190, 60, 60));
            float size = (def?.Radius ?? 0.4f) * 90f;
            var frames = SpriteGen.GetEnemyFrames(def);
            _sorted.Add((pos.X + pos.Y + e.Height * 1.0f + 0.1f + UnderDeckBias(pos, e.Height), batch =>
            {
                int barY;
                if (frames != null)
                {
                    // Procedural pixel sprite: shamble animation while chasing/attacking.
                    bool animated = e.State is (byte)Server.EnemyState.Chase or (byte)Server.EnemyState.Attack;
                    int frame = animated ? (int)((animClock / 170 + e.Id) % frames.Length) : 0;
                    var tex = frames[frame];
                    int scale = e.IsBoss ? 3 : 2; // the boss reads bigger at a glance
                    int w = tex.Width * scale, h = tex.Height * scale;
                    var spriteRect = new Rectangle((int)screen.X - w / 2, (int)screen.Y - h + 6, w, h);
                    EnemyHitRects.Add((spriteRect, e.Id));
                    var tint = e.Id == HoveredEnemyId ? new Color(255, 105, 95) : EliteTint(e);
                    batch.Draw(TextureGen.Circle32,
                        new Rectangle((int)(screen.X - size / 2), (int)(screen.Y - size / 4), (int)size, (int)(size / 2)),
                        new Color(0, 0, 0, 90)); // shadow
                    batch.Draw(tex, spriteRect, null,
                        tint, 0f, Vector2.Zero,
                        e.FacingLeft ? SpriteEffects.FlipHorizontally : SpriteEffects.None, 0f);
                    barY = (int)screen.Y - h + 2;
                }
                else
                {
                    EnemyHitRects.Add((new Rectangle((int)(screen.X - size / 2), (int)(screen.Y - size),
                        (int)size, (int)size), e.Id));
                    DrawUnitToken(batch, screen, size, color);
                    barY = (int)screen.Y - (int)size - 26;
                }
                if (_settings.ShowEnemyHealthBars)
                {
                    float frac = e.MaxHealth > 0 ? Math.Clamp(e.Health / e.MaxHealth, 0f, 1f) : 0;
                    int barW = e.IsElite ? 44 : 32;
                    var bar = new Rectangle((int)screen.X - barW / 2, barY, barW, e.IsElite ? 5 : 4);
                    batch.Draw(TextureGen.Pixel, bar, new Color(20, 20, 20, 200));
                    batch.Draw(TextureGen.Pixel, new Rectangle(bar.X, bar.Y, (int)(bar.Width * frac), bar.Height),
                        new Color(200, 50, 50));
                }
                DrawDebuffIcons(batch, e.DebuffFlags, (int)screen.X, barY - 16);
            }));
        }

        foreach (var p in world.Players.Values)
        {
            var pos = p.Position;
            var screen = camera.WorldToScreen(pos, p.Height);
            var color = p.IsLocal ? new Color(90, 170, 255) : new Color(110, 235, 140);
            if (!p.Alive) color = new Color(80, 80, 90);
            if (p.DodgeTimeLeft > 0) color = Color.Lerp(color, Color.White, 0.65f); // dash flash / i-frame hint
            var name = p.Name ?? "?";
            _sorted.Add((pos.X + pos.Y + p.Height * 1.0f + 0.1f + UnderDeckBias(pos, p.Height), batch =>
            {
                // Held weapon, upright, orbiting the body toward the aim (like the old
                // facing dot). When the aim points up-screen the weapon is behind the
                // character: draw it under the body and slightly faded.
                var screenDir = new Vector2(p.Facing.X - p.Facing.Y, (p.Facing.X + p.Facing.Y) * 0.5f);
                if (screenDir.LengthSquared() < 0.001f) screenDir = new Vector2(1, 0);
                screenDir.Normalize();
                var weaponBase = p.WeaponBaseId != null ? _data.Items.GetValueOrDefault(p.WeaponBaseId) : null;
                var offHandBase = p.OffHandBaseId != null ? _data.Items.GetValueOrDefault(p.OffHandBaseId) : null;
                var weaponTex = SpriteGen.GetWeaponSprite(weaponBase);
                var offHandTex = SpriteGen.GetWeaponSprite(offHandBase);
                bool weaponBehind = screenDir.Y < -0.1f;
                // With both hands full, items shift apart perpendicular to the aim — one in
                // each hand — instead of overlapping at the center. Hands keep their true
                // sides in every aim direction; overlap is resolved by painter's order:
                // whichever hand hangs lower on screen (nearer the viewer) draws on top.
                var perp = new Vector2(-screenDir.Y, screenDir.X);
                bool bothHands = weaponTex != null && offHandTex != null;
                bool swinging = p.SwingTimeLeft > 0 && weaponTex != null;

                void DrawHeld(Texture2D tex, float side)
                {
                    var hand = screen + screenDir * 16f + perp * (side * 14f) + new Vector2(0, -12);
                    var tint = weaponBehind ? Color.White * 0.55f : Color.White;
                    batch.Draw(tex, hand, null, tint, -MathF.PI / 2f,
                        new Vector2(tex.Width * 0.4f, tex.Height / 2f), 2f, SpriteEffects.None, 0f);
                }

                // Melee swing: the weapon sweeps an arc through the aim direction, gripped
                // at its handle, instead of standing upright.
                void DrawSwingingWeapon()
                {
                    float st = 1f - Math.Clamp(p.SwingTimeLeft / ClientPlayer.SwingDuration, 0f, 1f);
                    var swingIso = new Vector2(p.SwingDir.X - p.SwingDir.Y, (p.SwingDir.X + p.SwingDir.Y) * 0.5f);
                    if (swingIso.LengthSquared() < 0.001f) swingIso = screenDir;
                    swingIso.Normalize();
                    float aimAng = MathF.Atan2(swingIso.Y, swingIso.X);
                    float ang = aimAng - 1.25f + 2.5f * st;
                    var hand = screen + new Vector2(MathF.Cos(ang), MathF.Sin(ang) * 0.6f) * 20f + new Vector2(0, -12);
                    batch.Draw(weaponTex, hand, null, Color.White, ang,
                        new Vector2(weaponTex.Width * 0.15f, weaponTex.Height / 2f), 2f, SpriteEffects.None, 0f);
                }

                void DrawHands()
                {
                    if (!bothHands)
                    {
                        if (offHandTex != null) DrawHeld(offHandTex, 0f);
                        if (weaponTex != null && !swinging) DrawHeld(weaponTex, 0f);
                        return;
                    }
                    if (swinging)
                    {
                        DrawHeld(offHandTex, -1f); // weapon is mid-swing, drawn separately on top
                        return;
                    }
                    bool weaponNearHand = perp.Y >= 0; // weapon sits at +perp
                    if (weaponNearHand) { DrawHeld(offHandTex, -1f); DrawHeld(weaponTex, 1f); }
                    else { DrawHeld(weaponTex, 1f); DrawHeld(offHandTex, -1f); }
                }

                if (weaponBehind) DrawHands();
                DrawUnitToken(batch, screen, 34f, color);
                if (!weaponBehind) DrawHands();
                if (swinging) DrawSwingingWeapon();
                if (weaponTex == null && offHandTex == null)
                {
                    var tip = screen + screenDir * 22f;
                    batch.Draw(TextureGen.Circle32, new Rectangle((int)tip.X - 3, (int)tip.Y - 3 - 14, 6, 6), Color.White);
                }

                var font = FontManager.Get(13);
                var nameSize = font.MeasureString(name);
                batch.DrawString(font, name, new Vector2(screen.X - nameSize.X / 2, screen.Y - 62), Color.White);
                float frac = p.MaxHealth > 0 ? Math.Clamp(p.Health / p.MaxHealth, 0f, 1f) : 0;
                var bar = new Rectangle((int)screen.X - 18, (int)screen.Y - 48, 36, 4);
                batch.Draw(TextureGen.Pixel, bar, new Color(20, 20, 20, 200));
                batch.Draw(TextureGen.Pixel, new Rectangle(bar.X, bar.Y, (int)(bar.Width * frac), bar.Height),
                    new Color(70, 200, 90));
            }));
        }

        foreach (var pr in world.Projectiles.Values)
        {
            var screen = camera.WorldToScreen(pr.Position, pr.Height);
            var projDef = pr.SkillId != null ? _data.Skills.GetValueOrDefault(pr.SkillId) : null;
            var projSprite = SpriteGen.GetProjectileSprite(projDef?.ProjectileSprite);
            if (projSprite != null)
            {
                // Named sprite (e.g. the ice spike shard), rotated along the flight path.
                var isoDir = new Vector2(pr.Direction.X - pr.Direction.Y, (pr.Direction.X + pr.Direction.Y) * 0.5f);
                float ang = MathF.Atan2(isoDir.Y, isoDir.X);
                _sorted.Add((pr.Position.X + pr.Position.Y + pr.Height * 1.0f + 0.1f + UnderDeckBias(pr.Position, pr.Height), batch =>
                    batch.Draw(projSprite, new Vector2(screen.X, screen.Y - 14), null, Color.White, ang,
                        new Vector2(projSprite.Width / 2f, projSprite.Height / 2f), 2f, SpriteEffects.None, 0f)));
            }
            else
            {
                var color = pr.FromPlayer ? new Color(255, 150, 50) : new Color(140, 255, 90);
                _sorted.Add((pr.Position.X + pr.Position.Y + pr.Height * 1.0f + 0.1f + UnderDeckBias(pr.Position, pr.Height), batch =>
                    batch.Draw(TextureGen.Circle32, new Rectangle((int)screen.X - 6, (int)screen.Y - 6 - 14, 12, 12), color)));
            }
        }

        foreach (var fx in world.Effects)
        {
            var screen = camera.WorldToScreen(fx.Position, fx.Height);
            float t = 1f - fx.TimeLeft / fx.Duration;

            if (fx.Kind == "swipe")
            {
                // Weapon swipe: an arc of fading slashes sweeping across the aim direction.
                var isoDir = new Vector2(fx.Dir.X - fx.Dir.Y, (fx.Dir.X + fx.Dir.Y) * 0.5f);
                if (isoDir.LengthSquared() < 0.001f) isoDir = new Vector2(1, 0);
                float baseAngle = MathF.Atan2(isoDir.Y, isoDir.X);
                float arcRadius = fx.Radius * IsoCamera.HalfTileW;
                const float halfArc = 1.1f;
                float head = -halfArc + 2f * halfArc * t; // sweep position this frame
                _sorted.Add((fx.Position.X + fx.Position.Y + fx.Height * 1.0f + 0.2f + UnderDeckBias(fx.Position, fx.Height), batch =>
                {
                    for (int k = 0; k < 5; k++)
                    {
                        float a = head - k * 0.22f;
                        if (a < -halfArc) break;
                        float ang = baseAngle + a;
                        var p = new Vector2(screen.X + MathF.Cos(ang) * arcRadius,
                                            screen.Y - 14 + MathF.Sin(ang) * arcRadius * 0.55f);
                        float fade = (1f - t * 0.6f) * (1f - k * 0.18f);
                        int size = 12 - k * 2;
                        batch.Draw(TextureGen.Circle32,
                            new Rectangle((int)(p.X - size / 2f), (int)(p.Y - size / 2f), size, size),
                            Color.White * fade);
                    }
                }));
                continue;
            }

            if (fx.Kind == "chain" && fx.Points is { Count: > 1 })
            {
                // Chain lightning: jagged flickering bolts between the exact chain points
                // (caster -> victim -> victim...). Re-jittered every few frames so it crackles.
                float fade = 1f - t;
                var boltColor = DamageKindColor(Skills.DamageKind.Lightning) * fade;
                var coreColor = Color.White * (fade * 0.85f);
                int flickerSeed = (int)(Environment.TickCount64 / 45);
                var pts = fx.Points;
                _sorted.Add((fx.Position.X + fx.Position.Y + fx.Height * 1.0f + 1.2f, batch =>
                {
                    for (int seg = 0; seg < pts.Count - 1; seg++)
                    {
                        var a = camera.WorldToScreen(pts[seg], fx.Height);
                        var b = camera.WorldToScreen(pts[seg + 1], fx.Height);
                        a.Y -= 16;
                        b.Y -= 16;
                        var rng = new Random(flickerSeed * 131 + seg * 17);
                        const int subdivisions = 4;
                        var prev = a;
                        for (int i = 1; i <= subdivisions; i++)
                        {
                            var along = Vector2.Lerp(a, b, i / (float)subdivisions);
                            if (i < subdivisions)
                            {
                                var dir = b - a;
                                var perp = new Vector2(-dir.Y, dir.X);
                                if (perp.LengthSquared() > 0.001f) perp.Normalize();
                                along += perp * (float)(rng.NextDouble() - 0.5) * 14f;
                            }
                            DrawScreenLine(batch, prev, along, boltColor, 3);
                            DrawScreenLine(batch, prev, along, coreColor, 1);
                            prev = along;
                        }
                        // Impact spark at each victim.
                        batch.Draw(TextureGen.Circle32,
                            new Rectangle((int)b.X - 5, (int)b.Y - 5, 10, 10), coreColor);
                    }
                }));
                continue;
            }

            float radiusPx = fx.Radius * 2f * IsoCamera.HalfTileW * (0.4f + 0.6f * t);
            byte alpha = (byte)(180 * (1f - t));
            var color = fx.Kind switch
            {
                "burst" => new Color((byte)170, (byte)90, (byte)255, alpha),
                "slam" => new Color((byte)230, (byte)200, (byte)90, alpha),
                "melee" => new Color((byte)255, (byte)255, (byte)255, alpha),
                _ => new Color((byte)255, (byte)120, (byte)60, alpha),
            };
            _sorted.Add((fx.Position.X + fx.Position.Y + fx.Height * 1.0f + 0.2f + UnderDeckBias(fx.Position, fx.Height), batch =>
                batch.Draw(TextureGen.Circle32,
                    new Rectangle((int)(screen.X - radiusPx), (int)(screen.Y - radiusPx / 2f),
                        (int)(radiusPx * 2), (int)radiusPx), color)));
        }

        foreach (var (_, draw) in _sorted.OrderBy(e => e.depth))
            draw(sb);

        // --- drop name labels (screen space, on top) ---
        var labelFont = FontManager.Get(13);
        foreach (var drop in world.Drops.Values)
        {
            var screen = camera.WorldToScreen(drop.Position, drop.Height);
            string label = drop.IsGold ? $"{drop.GoldAmount} Gold" : drop.Item.DisplayName(_data);
            if (!drop.IsGold && drop.Item.StackCount > 1) label += $" x{drop.Item.StackCount}";
            var labelColor = drop.IsGold ? new Color(240, 200, 90) : RarityColor(drop.Item.Rarity);
            var size = labelFont.MeasureString(label);
            var rect = new Rectangle((int)(screen.X - size.X / 2) - 4, (int)(screen.Y - 30), (int)size.X + 8, (int)size.Y + 4);
            sb.Draw(TextureGen.Pixel, rect, new Color(0, 0, 0, 170));
            sb.DrawString(labelFont, label, new Vector2(rect.X + 4, rect.Y + 2), labelColor);
            DropLabelRects.Add((rect, drop.DropId));
        }

        // --- floating damage numbers (from server DamageEvents; toggle in Options) ---
        if (_settings.ShowDamageNumbers)
        {
            var dmgFont = FontManager.GetBold(15);
            foreach (var fn in world.FloatingNumbers)
            {
                float t = fn.Age / Net.FloatingNumber.Lifetime;
                var screen = camera.WorldToScreen(fn.Position, fn.Height);
                screen.Y -= 42 + 34 * t; // rise as it ages
                float alpha = 1f - t * t;
                var color = (fn.Blocked
                    ? new Color(180, 200, 230)
                    : fn.TargetIsPlayer
                        ? new Color(255, 80, 80)
                        : DamageKindColor((Skills.DamageKind)fn.Kind)) * alpha;
                string text = fn.Blocked ? "Blocked" : $"{MathF.Max(1, MathF.Round(fn.Amount)):0}";
                var size = dmgFont.MeasureString(text);
                sb.DrawString(dmgFont, text, new Vector2(screen.X - size.X / 2, screen.Y), color);
            }
        }
    }

    /// <summary>Row of tiny per-debuff icons centered above an enemy's head — one icon
    /// per active flag in Server.EnemyDebuffs order.</summary>
    private static void DrawDebuffIcons(SpriteBatch sb, byte flags, int centerX, int y)
    {
        if (flags == 0) return;
        var kinds = new string[2];
        int count = 0;
        if ((flags & Server.EnemyDebuffs.Stunned) != 0) kinds[count++] = "stun";
        if ((flags & Server.EnemyDebuffs.Burning) != 0) kinds[count++] = "burn";

        const int iconSize = 13, gap = 2;
        int totalW = count * iconSize + (count - 1) * gap;
        int x = centerX - totalW / 2;
        for (int i = 0; i < count; i++)
        {
            var tex = SpriteGen.GetDebuffIcon(kinds[i]);
            if (tex != null)
                sb.Draw(tex, new Rectangle(x + i * (iconSize + gap), y, iconSize, iconSize), Color.White);
        }
    }

    /// <summary>A screen-space line segment drawn with the 1x1 pixel texture.</summary>
    private static void DrawScreenLine(SpriteBatch sb, Vector2 a, Vector2 b, Color color, int thickness)
    {
        var d = b - a;
        float len = d.Length();
        if (len < 0.5f) return;
        float ang = MathF.Atan2(d.Y, d.X);
        sb.Draw(TextureGen.Pixel, a, null, color, ang, new Vector2(0, 0.5f),
            new Vector2(len, thickness), SpriteEffects.None, 0f);
    }

    private static void DrawUnitToken(SpriteBatch sb, Vector2 feet, float size, Color color)
    {
        // Shadow ellipse at the feet + body circle floating above.
        sb.Draw(TextureGen.Circle32,
            new Rectangle((int)(feet.X - size / 2), (int)(feet.Y - size / 4), (int)size, (int)(size / 2)),
            new Color(0, 0, 0, 90));
        sb.Draw(TextureGen.Circle32,
            new Rectangle((int)(feet.X - size / 2), (int)(feet.Y - size / 2 - 14), (int)size, (int)size),
            color);
    }

    /// <summary>Display color per damage type (floating numbers, character sheet).</summary>
    public static Color DamageKindColor(Skills.DamageKind kind) => kind switch
    {
        Skills.DamageKind.Fire => new Color(255, 150, 60),
        Skills.DamageKind.Cold => new Color(120, 190, 255),
        Skills.DamageKind.Lightning => new Color(250, 235, 120),
        Skills.DamageKind.Arcane => new Color(200, 130, 255),
        Skills.DamageKind.Acid => new Color(140, 220, 70),
        Skills.DamageKind.Dark => new Color(150, 110, 185),
        Skills.DamageKind.Light => new Color(255, 252, 210),
        _ => new Color(245, 240, 230), // physical: thrust/blunt/slash
    };

    public static Color RarityColor(ItemRarity rarity) => rarity switch
    {
        ItemRarity.Magic => new Color(110, 140, 255),
        ItemRarity.Rare => new Color(255, 220, 80),
        _ => Color.White,
    };

    public static Color ParseColor(string hex, Color fallback)
    {
        if (string.IsNullOrEmpty(hex) || hex.Length < 6) return fallback;
        try
        {
            return new Color(
                Convert.ToInt32(hex[..2], 16),
                Convert.ToInt32(hex[2..4], 16),
                Convert.ToInt32(hex[4..6], 16));
        }
        catch { return fallback; }
    }
}
