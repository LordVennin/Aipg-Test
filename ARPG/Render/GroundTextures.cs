using ARPG.World;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ARPG.Render;

/// <summary>
/// One ground material a zone floor is made of: a pixel STYLE (grass, dirt, mud, moss,
/// sand, gravel...) and the base colour it's drawn in. Themes list two or three; the
/// field generator scatters them in broad noise patches, and the tile textures blend
/// across the boundaries with ragged feathered edges, so the floor reads as terrain
/// instead of a board of tinted diamonds.
/// </summary>
public sealed class GroundMaterial
{
    public string Style { get; }
    public Color Base { get; }

    public GroundMaterial(string style, Color baseColor) { Style = style; Base = baseColor; }

    /// <summary>All pixel styles the baker knows.</summary>
    public static readonly string[] Styles =
    {
        "grass", "deadgrass", "dirt", "litter", "mud", "moss", "sand", "cracked", "gravel", "flagstone", "dust",
    };

    /// <summary>Default base colour per style, used when the theme gives only a style name.</summary>
    public static Color DefaultBase(string style) => style switch
    {
        "grass" => new Color(58, 86, 50),
        "deadgrass" => new Color(78, 82, 56),
        "dirt" => new Color(92, 74, 50),
        "litter" => new Color(70, 66, 42),
        "mud" => new Color(54, 50, 42),
        "moss" => new Color(56, 92, 70),
        "sand" => new Color(112, 98, 66),
        "cracked" => new Color(96, 80, 54),
        "gravel" => new Color(90, 84, 76),
        "flagstone" => new Color(66, 64, 74),
        "dust" => new Color(60, 58, 66),
        _ => new Color(70, 70, 70),
    };

    /// <summary>Parse a theme entry: "grass" or "grass:3A5632" (style plus RRGGBB base).</summary>
    public static GroundMaterial Parse(string spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return new GroundMaterial("dirt", DefaultBase("dirt"));
        var parts = spec.Split(':');
        string style = parts[0].Trim().ToLowerInvariant();
        if (Array.IndexOf(Styles, style) < 0) style = "dirt";
        var baseColor = parts.Length > 1 ? WorldRenderer.ParseColor(parts[1].Trim(), DefaultBase(style)) : DefaultBase(style);
        return new GroundMaterial(style, baseColor);
    }

    public string CacheKey => $"{Style}:{Base.R:X2}{Base.G:X2}{Base.B:X2}";
}

/// <summary>Which material every tile of a map wears (index into the theme's list, -1
/// off-map). Pure data, seeded from the map, identical on every client.</summary>
public static class GroundField
{
    private static uint Hash(int seed, int x, int y)
    {
        uint n = (uint)(x * 73856093 ^ y * 19349663 ^ seed * 83492791);
        n ^= n >> 16; n *= 0x7feb352d;
        n ^= n >> 15; n *= 0x846ca68b;
        n ^= n >> 16;
        return n;
    }

    /// <summary>Smoothstep-interpolated lattice noise in [0,1] over tile coordinates.</summary>
    public static float ValueNoise(int seed, float x, float y, float freq)
    {
        float fx = x * freq, fy = y * freq;
        int x0 = (int)MathF.Floor(fx), y0 = (int)MathF.Floor(fy);
        float tx = fx - x0, ty = fy - y0;
        tx = tx * tx * (3f - 2f * tx);
        ty = ty * ty * (3f - 2f * ty);
        float V(int gx, int gy) => (Hash(seed, gx, gy) & 0xFFFFFF) / (float)0xFFFFFF;
        float a = V(x0, y0), b = V(x0 + 1, y0), c = V(x0, y0 + 1), d = V(x0 + 1, y0 + 1);
        return MathHelper.Lerp(MathHelper.Lerp(a, b, tx), MathHelper.Lerp(c, d, tx), ty);
    }

    /// <summary>
    /// Assign materials: broad patches of the FIRST material (the zone's staple) with
    /// pools of the second and, rarer, the third, from two octaves of noise; trail
    /// tiles (path strength >= 0.6) wear the path material. Later entries draw over
    /// earlier ones at boundaries, so list them staple-first, strongest-last.
    /// </summary>
    public static int[] Build(GameMap map, int materialCount, IReadOnlyDictionary<int, float> pathField, int pathMaterial)
    {
        var field = new int[map.Width * map.Height];
        if (materialCount <= 0) { Array.Fill(field, -1); return field; }
        for (int y = 0; y < map.Height; y++)
            for (int x = 0; x < map.Width; x++)
            {
                int key = y * map.Width + x;
                if (pathField != null && pathField.TryGetValue(key, out float trail) && trail >= 0.6f && pathMaterial >= 0)
                {
                    field[key] = Math.Min(pathMaterial, materialCount - 1);
                    continue;
                }
                float coarse = ValueNoise(map.Seed ^ 0x4D41544C, x, y, 0.17f);
                float fine = ValueNoise(map.Seed ^ 0x6D617466, x, y, 0.55f);
                float m = 0.72f * coarse + 0.28f * fine;
                int idx = materialCount switch
                {
                    1 => 0,
                    2 => m < 0.56f ? 0 : 1,
                    _ => m < 0.47f ? 0 : m < 0.70f ? 1 : 2,
                };
                field[key] = Math.Min(idx, materialCount - 1);
            }
        return field;
    }
}

/// <summary>
/// Bakes and caches the 64x32 diamond ground textures: several VARIANTS per material
/// (so repetition doesn't read), each with pixel detail in the material's own style,
/// plus FEATHERED copies whose alpha ragged-fades from one edge inward — drawn over a
/// neighbouring tile of a lower-priority material to blend the boundary.
/// </summary>
public static class GroundTiles
{
    public const int VariantCount = 4;
    private const int W = TextureGen.TileWidth, H = TextureGen.TileHeight;

    private static GraphicsDevice _device;
    private static readonly Dictionary<string, Texture2D> _cache = new();

    public static void Initialize(GraphicsDevice device) => _device = device;

    /// <summary>The full tile for a material variant.</summary>
    public static Texture2D Get(GroundMaterial m, int variant)
    {
        if (_device == null) return null;
        variant = ((variant % VariantCount) + VariantCount) % VariantCount;
        string key = m.CacheKey + "#" + variant;
        if (_cache.TryGetValue(key, out var tex)) return tex;
        var px = Bake(m, variant);
        tex = new Texture2D(_device, W, H);
        tex.SetData(px);
        _cache[key] = tex;
        return tex;
    }

    /// <summary>The tile with its alpha feathered from one edge: 0 = the (x-1,y) edge
    /// (screen upper-left), 1 = (x+1,y) (lower-right), 2 = (x,y-1) (upper-right),
    /// 3 = (x,y+1) (lower-left). The visible band covers roughly the near half of the
    /// tile with a ragged, noise-wobbled boundary.</summary>
    public static Texture2D GetFeathered(GroundMaterial m, int variant, int edge)
    {
        if (_device == null) return null;
        variant = ((variant % VariantCount) + VariantCount) % VariantCount;
        string key = m.CacheKey + "#" + variant + "e" + edge;
        if (_cache.TryGetValue(key, out var tex)) return tex;
        var px = Bake(m, variant);
        int seed = StyleSeed(m.Style) * 31 + variant * 7 + edge * 131;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = y * W + x;
                if (px[i].A == 0) continue;
                float sx = x + 0.5f - W / 2f, sy = y + 0.5f - H / 2f;
                float u = (sx / (W / 2f) + sy / (H / 2f) + 1f) / 2f;
                float v = (sy / (H / 2f) + 1f - sx / (W / 2f)) / 2f;
                float t = edge switch { 0 => u, 1 => 1f - u, 2 => v, _ => 1f - v };
                float wobble = GroundField.ValueNoise(seed, x, y, 0.11f);
                float threshold = 0.44f + 0.36f * (wobble - 0.5f);
                if (t > threshold) px[i] = Color.Transparent;
            }
        tex = new Texture2D(_device, W, H);
        tex.SetData(px);
        _cache[key] = tex;
        return tex;
    }

    /// <summary>The bank of a water tile along `edge` (same numbering as GetFeathered),
    /// cut straight along the diamond so a pond keeps a clean tile-shaped outline.
    /// The water surface sits BELOW the ground, so the two far edges (0 upper-left,
    /// 2 upper-right) show a short earth face dropping to the water with a pale
    /// shallow band at its foot; the two near edges (1, 3) are the land overhanging:
    /// a dark shadow line, then the same shallow band. Drawn over the water fill
    /// after every land tile, so the feathered ground can't nibble the outline.</summary>
    public static Texture2D GetShoreBank(int edge)
    {
        if (_device == null) return null;
        string key = "shorebank" + edge;
        if (_cache.TryGetValue(key, out var tex)) return tex;
        var px = new Color[W * H];
        bool far = edge == 0 || edge == 2;
        var earth = new Color(74, 56, 36);
        var earthDark = new Color(56, 42, 28);
        var lip = new Color(96, 82, 48);
        var shadow = new Color(8, 18, 30);
        var foam = new Color(214, 234, 246);
        var shallow = new Color(126, 178, 206);
        // `t` runs 0 at the edge to 1 at the opposite edge; one unit ≈ 28.6 px across.
        const float px1 = 1f / 28.6f;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                float sx = x + 0.5f - W / 2f, sy = y + 0.5f - H / 2f;
                if (MathF.Abs(sx) / (W / 2f) + MathF.Abs(sy) / (H / 2f) > 1f) continue;
                float u = (sx / (W / 2f) + sy / (H / 2f) + 1f) / 2f;
                float v = (sy / (H / 2f) + 1f - sx / (W / 2f)) / 2f;
                float t = edge switch { 0 => u, 1 => 1f - u, 2 => v, _ => 1f - v };
                Color c;
                if (far)
                {
                    // Bank face: lip row, earth, a darker bottom row, then foam + shallows.
                    if (t < px1 * 1f) c = lip;
                    else if (t < px1 * 3.5f) c = earth;
                    else if (t < px1 * 4.5f) c = earthDark;
                    else if (t < px1 * 5.5f) c = foam * 0.55f;
                    else if (t < px1 * 9f) c = shallow * (0.30f * (1f - (t - px1 * 5.5f) / (px1 * 3.5f)));
                    else continue;
                }
                else
                {
                    if (t < px1 * 1.2f) c = shadow * 0.55f;
                    else if (t < px1 * 2.2f) c = foam * 0.5f;
                    else if (t < px1 * 6f) c = shallow * (0.28f * (1f - (t - px1 * 2.2f) / (px1 * 3.8f)));
                    else continue;
                }
                px[y * W + x] = c;
            }
        tex = new Texture2D(_device, W, H);
        tex.SetData(px);
        _cache[key] = tex;
        return tex;
    }

    /// <summary>The shadow a ledge throws onto the LOWER ground beyond one of its
    /// edges: a band inside a diamond along `edge` (GetFeathered numbering), darkest
    /// at the edge and fading over LedgeShadowPx. Drawn on the lower neighbour's
    /// diamond footprint at the ledge's level, so the band lands just past the rim.</summary>
    public const float LedgeShadowPx = 11f;
    public static Texture2D GetLedgeShadow(int edge)
    {
        if (_device == null) return null;
        string key = "ledgeshadow" + edge;
        if (_cache.TryGetValue(key, out var tex)) return tex;
        var px = new Color[W * H];
        const float px1 = 1f / 28.6f;
        var shade = new Color(4, 6, 8);
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                float sx = x + 0.5f - W / 2f, sy = y + 0.5f - H / 2f;
                if (MathF.Abs(sx) / (W / 2f) + MathF.Abs(sy) / (H / 2f) > 1f) continue;
                float u = (sx / (W / 2f) + sy / (H / 2f) + 1f) / 2f;
                float v = (sy / (H / 2f) + 1f - sx / (W / 2f)) / 2f;
                float t = edge switch { 0 => u, 1 => 1f - u, 2 => v, _ => 1f - v };
                float d = t / (px1 * LedgeShadowPx);
                if (d >= 1f) continue;
                float a = 0.55f * (1f - d) * (1f - d);
                px[y * W + x] = shade * a;
            }
        tex = new Texture2D(_device, W, H);
        tex.SetData(px);
        _cache[key] = tex;
        return tex;
    }

    private static int StyleSeed(string style)
    {
        int h = 17;
        foreach (char c in style) h = h * 31 + c;
        return h;
    }

    private static Color Shade(Color c, float f) => new(
        Math.Clamp((int)(c.R * f), 0, 255), Math.Clamp((int)(c.G * f), 0, 255), Math.Clamp((int)(c.B * f), 0, 255));

    private static Color Warm(Color c, int r, int g, int b) => new(
        Math.Clamp(c.R + r, 0, 255), Math.Clamp(c.G + g, 0, 255), Math.Clamp(c.B + b, 0, 255));

    /// <summary>Tile-local iso coordinates of a pixel: u along world X, v along world Y.</summary>
    private static (float u, float v, bool inside) Iso(int x, int y)
    {
        float sx = x + 0.5f - W / 2f, sy = y + 0.5f - H / 2f;
        float d = MathF.Abs(sx) / (W / 2f) + MathF.Abs(sy) / (H / 2f);
        float u = (sx / (W / 2f) + sy / (H / 2f) + 1f) / 2f;
        float v = (sy / (H / 2f) + 1f - sx / (W / 2f)) / 2f;
        return (u, v, d <= 1f);
    }

    /// <summary>Screen pixel of a tile-local iso point (for placing detail by u/v).</summary>
    private static (int x, int y) Px(float u, float v) =>
        ((int)((u - v) * (W / 2f) + W / 2f), (int)((u + v - 1f) * (H / 2f) + H / 2f));

    private static Color[] Bake(GroundMaterial m, int variant)
    {
        var px = new Color[W * H];
        var rng = new Random(StyleSeed(m.Style) * 977 + variant * 7919 + m.Base.R * 3 + m.Base.G * 5 + m.Base.B * 7);
        int grainSeed = rng.Next();
        var baseC = m.Base;
        var dark = Shade(baseC, 0.80f);
        var darker = Shade(baseC, 0.66f);
        var light = Shade(baseC, 1.18f);
        var lighter = Shade(baseC, 1.34f);

        float grainAmp = m.Style switch
        {
            "sand" or "dust" or "dirt" or "cracked" => 0.10f,
            "gravel" or "flagstone" => 0.07f,
            "mud" => 0.06f,
            _ => 0.08f,
        };
        void Set(int x, int y, Color c)
        {
            if (x < 0 || y < 0 || x >= W || y >= H) return;
            if (px[y * W + x].A == 0) return; // stay inside the diamond
            px[y * W + x] = c;
        }
        // Base fill with fine per-pixel grain (a touch of the two-tone lattice so it's
        // not pure static): the ground's own texture before any feature goes down.
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                var (_, _, inside) = Iso(x, y);
                if (!inside) { px[y * W + x] = Color.Transparent; continue; }
                float g = GroundField.ValueNoise(grainSeed, x, y, 0.5f) - 0.5f;
                float g2 = GroundField.ValueNoise(grainSeed ^ 0x5151, x, y, 0.14f) - 0.5f;
                px[y * W + x] = Shade(baseC, 1f + g * grainAmp * 2f + g2 * 0.10f);
            }

        int Count(int lo, int hi) => rng.Next(lo, hi + 1);
        void Dot(int x, int y, int w, int h, Color c) { for (int yy = 0; yy < h; yy++) for (int xx = 0; xx < w; xx++) Set(x + xx, y + yy, c); }
        (int x, int y) Spot()
        {
            // A random point inside the diamond, away from the very rim.
            float u = 0.06f + (float)rng.NextDouble() * 0.88f, v = 0.06f + (float)rng.NextDouble() * 0.88f;
            return Px(u, v);
        }

        switch (m.Style)
        {
            case "grass":
            {
                var blade = Warm(light, -6, 14, -10);
                var bladeDark = Warm(dark, -4, 6, -6);
                for (int i = Count(16, 22); i > 0; i--)
                {
                    var (x, y) = Spot();
                    Dot(x, y - 1, 1, 2, rng.Next(3) == 0 ? bladeDark : blade);
                    if (rng.Next(2) == 0) Set(x + 1, y, bladeDark);
                }
                for (int i = Count(2, 4); i > 0; i--) { var (x, y) = Spot(); Dot(x, y, 3, 1, darker); Set(x + 1, y - 1, dark); }
                if (variant == 2 && rng.Next(2) == 0) { var (x, y) = Spot(); Set(x, y, new Color(226, 214, 150)); }
                break;
            }
            case "deadgrass":
            {
                var straw = Warm(light, 18, 10, -14);
                var strawDark = Warm(dark, 8, 2, -10);
                for (int i = Count(12, 18); i > 0; i--)
                {
                    var (x, y) = Spot();
                    Dot(x, y - 1, 1, 2, rng.Next(2) == 0 ? straw : strawDark);
                    if (rng.Next(3) == 0) Set(x + 1, y - 2, straw);
                }
                for (int i = Count(2, 3); i > 0; i--) { var (x, y) = Spot(); Dot(x, y, 4, 2, darker); }
                break;
            }
            case "dirt":
            {
                for (int i = Count(4, 7); i > 0; i--) { var (x, y) = Spot(); Dot(x, y, 2, 1, lighter); Set(x, y + 1, dark); }
                for (int i = Count(3, 5); i > 0; i--) { var (x, y) = Spot(); Dot(x, y, 2, 1, darker); }
                for (int i = Count(1, 2); i > 0; i--) { var (x, y) = Spot(); Dot(x, y, 5, 2, dark); }
                break;
            }
            case "litter":
            {
                var leafA = Warm(baseC, 46, 18, -12);   // fallen orange-brown
                var leafB = Warm(baseC, 20, 30, -8);    // olive
                var leafC = Warm(light, 30, 20, 4);     // pale tan
                for (int i = Count(18, 26); i > 0; i--)
                {
                    var (x, y) = Spot();
                    var c = rng.Next(3) switch { 0 => leafA, 1 => leafB, _ => leafC };
                    Dot(x, y, 2, 1, c);
                    if (rng.Next(3) == 0) Set(x + 2, y, Shade(c, 0.8f));
                    if (rng.Next(3) == 0) Set(x, y + 1, darker);
                }
                for (int i = Count(2, 4); i > 0; i--) { var (x, y) = Spot(); Dot(x, y, 1, 3, darker); } // twigs
                break;
            }
            case "mud":
            {
                var wet = Shade(baseC, 0.62f);
                var gloss = Warm(light, 10, 14, 24);
                // One or two wide, lumpy pools (some tiles none), so the ground reads
                // as patchy bog rather than a dotted pattern.
                for (int i = variant == 3 ? 0 : Count(1, 2); i > 0; i--)
                {
                    var (cx, cy) = Spot();
                    int rw = Count(8, 14), rh = Count(3, 5);
                    int lumpSeed = rng.Next();
                    for (int yy = -rh - 1; yy <= rh + 1; yy++)
                        for (int xx = -rw - 2; xx <= rw + 2; xx++)
                        {
                            float wob = 1f + 0.35f * (GroundField.ValueNoise(lumpSeed, xx + 40, yy + 40, 0.35f) - 0.5f);
                            if (xx * xx * rh * rh + yy * yy * rw * rw <= rw * rw * rh * rh * wob)
                                Set(cx + xx, cy + yy, wet);
                        }
                    Dot(cx - rw / 3, cy - rh + 1, rw / 3 + 1, 1, gloss); // a wet glint on the near rim
                }
                for (int i = Count(4, 7); i > 0; i--) { var (x, y) = Spot(); Dot(x, y, 2, 1, dark); }
                for (int i = Count(2, 4); i > 0; i--) { var (x, y) = Spot(); Dot(x, y - 1, 1, 2, Warm(dark, 2, 14, -6)); } // a reed stub
                break;
            }
            case "moss":
            {
                var clump = Warm(light, -10, 12, 0);
                var rim = Shade(baseC, 0.72f);
                for (int i = Count(5, 8); i > 0; i--)
                {
                    var (cx, cy) = Spot();
                    int rw = Count(2, 4), rh = Count(1, 2);
                    for (int yy = -rh; yy <= rh; yy++)
                        for (int xx = -rw; xx <= rw; xx++)
                            if (xx * xx * rh * rh + yy * yy * rw * rw <= rw * rw * rh * rh)
                                Set(cx + xx, cy + yy, clump);
                    Dot(cx - rw, cy + rh + 1, rw * 2 + 1, 1, rim);
                    Set(cx, cy - rh, lighter);
                }
                for (int i = Count(4, 6); i > 0; i--) { var (x, y) = Spot(); Set(x, y, darker); }
                break;
            }
            case "sand":
            {
                // Wind ripples: faint light lines running along the iso X axis.
                for (int r = 0; r < 3; r++)
                {
                    float v = 0.15f + r * 0.3f + (float)rng.NextDouble() * 0.12f;
                    for (float u = 0.08f; u < 0.92f; u += 0.03f)
                    {
                        var (x, y) = Px(u, v + 0.02f * MathF.Sin(u * 14f + r));
                        Set(x, y, light);
                    }
                }
                for (int i = Count(3, 5); i > 0; i--) { var (x, y) = Spot(); Dot(x, y, 2, 1, dark); }
                for (int i = Count(2, 3); i > 0; i--) { var (x, y) = Spot(); Set(x, y, lighter); }
                break;
            }
            case "cracked":
            {
                // Dry earth: two or three crack polylines wandering across the tile.
                for (int c = Count(2, 3); c > 0; c--)
                {
                    float u = (float)rng.NextDouble(), v = (float)rng.NextDouble();
                    float du = ((float)rng.NextDouble() - 0.5f) * 0.18f, dv = ((float)rng.NextDouble() - 0.5f) * 0.18f;
                    for (int s = 0; s < 9; s++)
                    {
                        var (x, y) = Px(u, v);
                        Set(x, y, darker);
                        Set(x + 1, y, dark);
                        u += du + ((float)rng.NextDouble() - 0.5f) * 0.08f;
                        v += dv + ((float)rng.NextDouble() - 0.5f) * 0.08f;
                        if (u < 0.03f || u > 0.97f || v < 0.03f || v > 0.97f) break;
                    }
                }
                for (int i = Count(3, 5); i > 0; i--) { var (x, y) = Spot(); Dot(x, y, 2, 1, lighter); }
                break;
            }
            case "gravel":
            {
                var stoneA = Shade(baseC, 1.24f);
                var stoneB = Shade(baseC, 1.10f);
                var stoneC = Warm(baseC, 8, 6, 12);
                for (int i = Count(16, 24); i > 0; i--)
                {
                    var (x, y) = Spot();
                    var c = rng.Next(3) switch { 0 => stoneA, 1 => stoneB, _ => stoneC };
                    int w = rng.Next(2) == 0 ? 2 : 3;
                    Dot(x, y, w, 1, c);
                    Dot(x, y + 1, w, 1, Shade(c, 0.72f));
                }
                break;
            }
            case "flagstone":
            {
                // Irregular slabs: a jittered 3x3 grid of stones in (u,v) with mortar seams.
                float[] cutsU = { 0.31f + (float)rng.NextDouble() * 0.08f, 0.64f + (float)rng.NextDouble() * 0.08f };
                float[] cutsV = { 0.33f + (float)rng.NextDouble() * 0.08f, 0.66f + (float)rng.NextDouble() * 0.08f };
                var mortar = Shade(baseC, 0.62f);
                for (int y = 0; y < H; y++)
                    for (int x = 0; x < W; x++)
                    {
                        var (u, v, inside) = Iso(x, y);
                        if (!inside) continue;
                        bool seam = false;
                        foreach (var cu in cutsU) if (MathF.Abs(u - cu) < 0.035f) seam = true;
                        foreach (var cv in cutsV) if (MathF.Abs(v - cv) < 0.035f) seam = true;
                        if (u < 0.03f || v < 0.03f || u > 0.97f || v > 0.97f) seam = true;
                        if (seam) { Set(x, y, mortar); continue; }
                        // Per-slab tone.
                        int su = u < cutsU[0] ? 0 : u < cutsU[1] ? 1 : 2, sv = v < cutsV[0] ? 0 : v < cutsV[1] ? 1 : 2;
                        int tone = ((su * 3 + sv + variant) * 7) % 5;
                        Set(x, y, Shade(px[y * W + x], 0.94f + tone * 0.03f));
                    }
                for (int i = Count(3, 6); i > 0; i--) { var (x, y) = Spot(); Set(x, y, darker); }  // pocks
                for (int i = Count(1, 3); i > 0; i--) { var (x, y) = Spot(); Dot(x, y, 1, 3, darker); } // cracks
                break;
            }
            case "dust":
            {
                for (int i = Count(2, 4); i > 0; i--) { var (x, y) = Spot(); Dot(x, y, 6, 2, Shade(baseC, 0.9f)); }
                for (int i = Count(3, 5); i > 0; i--) { var (x, y) = Spot(); Dot(x, y, 2, 1, dark); }
                for (int i = Count(1, 2); i > 0; i--) { var (x, y) = Spot(); Dot(x, y, 2, 1, lighter); } // a bone chip / bright grain
                break;
            }
        }
        return px;
    }
}
