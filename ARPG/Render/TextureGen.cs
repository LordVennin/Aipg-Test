using ARPG.World;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ARPG.Render;

/// <summary>
/// Generates all placeholder textures at runtime (flat shapes) so the prototype
/// needs no external art assets or content pipeline.
/// </summary>
public static class TextureGen
{
    public static Texture2D Pixel { get; private set; }
    public static Texture2D Circle32 { get; private set; }
    public static Texture2D Diamond { get; private set; }       // Filled isometric tile (64x32)
    /// <summary>Fully opaque diamond (edge pixels solid, slightly darkened). Elevated
    /// tops use this — the translucent-edged Diamond shows the void behind cliffs as
    /// see-through seams between wall tiles.</summary>
    public static Texture2D DiamondSolid { get; private set; }
    /// <summary>Flat filled diamond with NO edge marking — organic floors tile with it
    /// so the ground reads as continuous terrain instead of a grid.</summary>
    public static Texture2D DiamondFlat { get; private set; }
    public static Texture2D DiamondOutline { get; private set; }

    public const int TileWidth = 64;
    public const int TileHeight = 32;

    private static GraphicsDevice _device;
    private static readonly Dictionary<int, Texture2D> _prismFaces = new();
    private static readonly Dictionary<int, Texture2D> _earthFaces = new();
    private static Texture2D _lipBand;
    private static readonly Dictionary<(RampDirection dir, bool stairs), Texture2D> _ramps = new();

    public static void Initialize(GraphicsDevice device)
    {
        _device = device;
        _prismFaces.Clear();
        _ramps.Clear();
        Pixel = new Texture2D(device, 1, 1);
        Pixel.SetData(new[] { Color.White });

        Circle32 = MakeCircle(device, 32);
        Diamond = MakeDiamond(device, TileWidth, TileHeight, filled: true);
        DiamondSolid = MakeDiamond(device, TileWidth, TileHeight, filled: true, opaqueEdge: true);
        DiamondFlat = MakeDiamond(device, TileWidth, TileHeight, filled: true, opaqueEdge: true, flat: true);
        DiamondOutline = MakeDiamond(device, TileWidth, TileHeight, filled: false);
    }

    /// <summary>
    /// The two visible side faces (SW + SE parallelograms) of an isometric prism
    /// `levels` elevation levels tall, baked in grayscale so styles tint at draw time
    /// (the left face is baked darker for fake directional light). Row 0 aligns with the
    /// horizontal midline of the prism's TOP diamond; the face's upper V edge matches
    /// the diamond's lower edges exactly, so stacked geometry is seamless — this is what
    /// makes cliff/wall silhouettes straight instead of jagged.
    /// </summary>
    public static Texture2D GetPrismFaces(int levels)
    {
        levels = Math.Clamp(levels, 1, 12);
        if (_prismFaces.TryGetValue(levels, out var cached)) return cached;

        int drop = levels * IsoCamera.LevelHeightPx;
        int h = TileHeight / 2 + drop;
        var tex = new Texture2D(_device, TileWidth, h);
        var data = new Color[TileWidth * h];
        for (int py = 0; py < h; py++)
            for (int px = 0; px < TileWidth; px++)
            {
                bool right = px >= TileWidth / 2;
                // Vertical drop of the diamond's lower edge at this column
                // (0 at the side corners, 16 at the bottom corner).
                float edge = (right ? (TileWidth - (px + 0.5f)) / 2f : (px + 0.5f) / 2f) - 1f;
                if (edge < 0) edge = 0;
                float y = py + 0.5f;
                if (y < edge || y >= edge + drop) continue;
                int g = right ? 140 : 96;
                if (y - edge < 1.2f || edge + drop - y < 1.2f) g = g * 2 / 3;   // top/bottom seams
                if (px == TileWidth / 2 - 1 || px == TileWidth / 2) g = g * 3 / 4; // center ridge
                data[py * TileWidth + px] = new Color(g, g, g);
            }
        tex.SetData(data);
        _prismFaces[levels] = tex;
        return tex;
    }

    /// <summary>
    /// Organic cliff sides: layered EARTH instead of stone — brown strata with embedded
    /// pebbles, darker on the left face. Baked in color (drawn white); the grass lip on
    /// top is a separate tintable band so it can match each tile's surface shade.
    /// </summary>
    public static Texture2D GetEarthFaces(int levels)
    {
        levels = Math.Clamp(levels, 1, 12);
        if (_earthFaces.TryGetValue(levels, out var cached)) return cached;

        int drop = levels * IsoCamera.LevelHeightPx;
        int h = TileHeight / 2 + drop;
        var tex = new Texture2D(_device, TileWidth, h);
        var data = new Color[TileWidth * h];
        for (int py = 0; py < h; py++)
            for (int px = 0; px < TileWidth; px++)
            {
                bool right = px >= TileWidth / 2;
                float edge = (right ? (TileWidth - (px + 0.5f)) / 2f : (px + 0.5f) / 2f) - 1f;
                if (edge < 0) edge = 0;
                float y = py + 0.5f;
                if (y < edge || y >= edge + drop) continue;
                float depth = (y - edge) / drop; // 0 at the lip, 1 at the base
                int r = (int)MathHelper.Lerp(92, 62, depth);
                int g = (int)MathHelper.Lerp(70, 48, depth);
                int b = (int)MathHelper.Lerp(46, 33, depth);
                // Horizontal strata bands.
                int band = (int)(y - edge);
                if (band % 6 == 4) { r = r * 86 / 100; g = g * 86 / 100; b = b * 86 / 100; }
                // Sparse embedded pebbles.
                uint hnoise = (uint)(px * 374761393 ^ band * 668265263);
                hnoise ^= hnoise >> 13; hnoise *= 1274126177; hnoise ^= hnoise >> 16;
                if ((hnoise & 63) == 0)
                {
                    int stone = 96 + (int)(hnoise >> 8 & 31);
                    r = stone; g = stone; b = stone - 6;
                }
                if (!right) { r = r * 78 / 100; g = g * 78 / 100; b = b * 78 / 100; }
                if (edge + drop - y < 1.2f) { r = r * 2 / 3; g = g * 2 / 3; b = b * 2 / 3; }
                data[py * TileWidth + px] = new Color(r, g, b);
            }
        tex.SetData(data);
        _earthFaces[levels] = tex;
        return tex;
    }

    /// <summary>A 4px band hugging the diamond's lower V edges — drawn tinted with the
    /// tile's surface color over cliff faces so grass overhangs the earth, hiding the
    /// face/top seam per tile.</summary>
    public static Texture2D GetLipBand()
    {
        if (_lipBand != null) return _lipBand;
        int h = TileHeight / 2 + 5;
        var tex = new Texture2D(_device, TileWidth, h);
        var data = new Color[TileWidth * h];
        for (int py = 0; py < h; py++)
            for (int px = 0; px < TileWidth; px++)
            {
                bool right = px >= TileWidth / 2;
                float edge = (right ? (TileWidth - (px + 0.5f)) / 2f : (px + 0.5f) / 2f) - 1f;
                if (edge < 0) edge = 0;
                float y = py + 0.5f;
                if (y < edge || y >= edge + 4) continue;
                // Ragged bottom: some columns hang one pixel longer.
                uint hnoise = (uint)(px * 2654435761);
                hnoise ^= hnoise >> 15;
                int extra = (int)(hnoise % 3) - 1;
                if (y >= edge + 3 + extra) continue;
                byte a = (byte)(y - edge < 2 ? 255 : 200);
                data[py * TileWidth + px] = new Color((byte)255, (byte)255, (byte)255, a);
            }
        tex.SetData(data);
        return _lipBand = tex;
    }

    /// <summary>
    /// A one-level elevation transition tile: a genuinely sloped top surface (smooth
    /// ramp) or four flat steps with risers (stairs), including the tile's own outer
    /// skirt faces. Baked per ascent direction in grayscale for tinting. Anchor: the
    /// texture's (32, 24) pixel is the tile's (0,0) world corner at the LOW level.
    /// </summary>
    public static Texture2D GetRampSprite(RampDirection dir, bool stairs)
    {
        if (_ramps.TryGetValue((dir, stairs), out var cached)) return cached;
        var tex = MakeRamp(_device, dir, stairs);
        _ramps[(dir, stairs)] = tex;
        return tex;
    }

    public const int RampSpriteOffsetY = 24;

    private static Texture2D MakeRamp(GraphicsDevice device, RampDirection dir, bool stairs)
    {
        const int W = TileWidth, H = 96;
        int rise = IsoCamera.LevelHeightPx;
        // Ramp progress t (0 = low edge, 1 = high edge) as a linear map t = a*u + b*v + c
        // over tile-local coordinates u,v in [0,1].
        (float a, float b, float c) = dir switch
        {
            RampDirection.PlusX => (1f, 0f, 0f),
            RampDirection.MinusX => (-1f, 0f, 1f),
            RampDirection.PlusY => (0f, 1f, 0f),
            _ => (0f, -1f, 1f),
        };
        float T(float u, float v) => Math.Clamp(a * u + b * v + c, 0f, 1f);
        const int Steps = 4;
        // Stepped surface height (px above the low level) for the stairs variant.
        float StepH(int band) => rise * (band + 1) / (float)Steps;
        float SurfH(float u, float v) => stairs
            ? StepH(Math.Clamp((int)(T(u, v) * Steps), 0, Steps - 1))
            : rise * T(u, v);

        var data = new Color[W * H];
        for (int py = 0; py < H; py++)
            for (int px = 0; px < W; px++)
            {
                float sxl = px + 0.5f - W / 2f;       // screen-x relative to the (0,0) corner
                float syl = py + 0.5f - RampSpriteOffsetY; // screen-y relative to it
                // Several surfaces can project onto one pixel; along the view ray both
                // u+v and height rise together, so the candidate with max u+v is visible.
                float bestUV = float.MinValue;
                int shade = 0;

                void Consider(float u, float v, int g)
                {
                    if (u + v > bestUV) { bestUV = u + v; shade = g; }
                }

                if (!stairs)
                {
                    // Sloped top: solve 16(u+v) - rise*T(u,v) = syl with 32(u-v) = sxl.
                    float A = 16 - rise * a, B = 16 - rise * b;
                    float det = A + B;
                    if (MathF.Abs(det) > 0.01f)
                    {
                        float u = (syl + rise * c + B * sxl / 32f) / det;
                        float v = u - sxl / 32f;
                        if (u is >= 0 and <= 1 && v is >= 0 and <= 1)
                        {
                            float t = T(u, v);
                            // High end matches the plateau top it meets; the low end
                            // (facing the camera) is a touch lighter — mild gradient so
                            // the transition reads without glowing against the terrain.
                            int g = 175 - (int)(25 * t);
                            // Accent only the slope's high/low edges — side borders stay
                            // clean so multi-tile ramps read as ONE continuous band.
                            if (t < 0.04f || t > 0.96f) g = g * 3 / 4;
                            Consider(u, v, g);
                        }
                    }
                }
                else
                {
                    // Flat step surfaces.
                    for (int i = 0; i < Steps; i++)
                    {
                        float s = (syl + StepH(i)) / 16f;   // u + v
                        float d = sxl / 32f;                // u - v
                        float u = (s + d) / 2f, v = (s - d) / 2f;
                        if (u is >= 0 and <= 1 && v is >= 0 and <= 1 &&
                            Math.Clamp((int)(T(u, v) * Steps), 0, Steps - 1) == i)
                        {
                            int g = 145 + (int)(55 * (i + 1) / (float)Steps);
                            Consider(u, v, g);
                        }
                    }
                    // Risers between steps — only visible when ascending toward the
                    // camera (-u / -v): faces then point at the viewer.
                    if (a < -0.5f || b < -0.5f)
                    {
                        bool alongU = a < -0.5f; // boundary lines are u = const (else v = const)
                        for (int k = 1; k < Steps; k++)
                        {
                            float fixedC = 1f - k / (float)Steps;
                            float other = alongU ? fixedC - sxl / 32f : fixedC + sxl / 32f;
                            if (other is < 0 or > 1) continue;
                            float u = alongU ? fixedC : other;
                            float v = alongU ? other : fixedC;
                            float hgt = 16 * (u + v) - syl;
                            if (hgt >= StepH(k - 1) && hgt <= StepH(k))
                                Consider(u, v, 118);
                        }
                    }
                }

                // Outer skirt faces below the tile's u=1 (SE) and v=1 (SW) edges,
                // following the surface profile (stepped for stairs) down to a 16px lip.
                {
                    // Skirts vanish where the surface meets the ground (hs ~ 0) so the
                    // ramp's low edge blends into the floor instead of showing a lip.
                    float v = 1f - sxl / 32f; // u = 1 edge
                    if (v is >= 0 and <= 1)
                    {
                        float hs = SurfH(1f, v);
                        float hgt = 16 * (1 + v) - syl;
                        if (hs > 1f && hgt >= 0 && hgt <= hs)
                            Consider(1f, v, hs - hgt < 1.2f ? 88 : 140);
                    }
                    float uu = 1f + sxl / 32f; // v = 1 edge
                    if (uu is >= 0 and <= 1)
                    {
                        float hs = SurfH(uu, 1f);
                        float hgt = 16 * (uu + 1) - syl;
                        if (hs > 1f && hgt >= 0 && hgt <= hs)
                            Consider(uu, 1f, hs - hgt < 1.2f ? 64 : 96);
                    }
                }

                if (bestUV > float.MinValue)
                    data[py * W + px] = new Color(shade, shade, shade);
            }

        var tex = new Texture2D(device, W, H);
        tex.SetData(data);
        return tex;
    }

    private static Texture2D MakeCircle(GraphicsDevice device, int diameter)
    {
        var tex = new Texture2D(device, diameter, diameter);
        var data = new Color[diameter * diameter];
        float r = diameter / 2f - 0.5f;
        float cx = diameter / 2f - 0.5f, cy = diameter / 2f - 0.5f;
        for (int y = 0; y < diameter; y++)
            for (int x = 0; x < diameter; x++)
            {
                float dx = x - cx, dy = y - cy;
                float dist = MathF.Sqrt(dx * dx + dy * dy);
                Color c = Color.Transparent;
                if (dist <= r) c = dist >= r - 1.5f ? new Color(0, 0, 0, 255) : Color.White;
                data[y * diameter + x] = c;
            }
        tex.SetData(data);
        return tex;
    }

    private static Texture2D MakeDiamond(GraphicsDevice device, int w, int h, bool filled, bool opaqueEdge = false, bool flat = false)
    {
        var tex = new Texture2D(device, w, h);
        var data = new Color[w * h];
        float hw = w / 2f, hh = h / 2f;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                // Diamond: |x - hw|/hw + |y - hh|/hh <= 1
                float d = MathF.Abs(x + 0.5f - hw) / hw + MathF.Abs(y + 0.5f - hh) / hh;
                Color c = Color.Transparent;
                if (d <= 1f)
                {
                    bool edge = !flat && d >= 0.93f;
                    c = filled ? (edge ? (opaqueEdge ? new Color(215, 215, 215) : new Color(255, 255, 255, 160)) : Color.White)
                               : (edge ? Color.White : Color.Transparent);
                }
                data[y * w + x] = c;
            }
        tex.SetData(data);
        return tex;
    }
}
