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
    public static Texture2D DiamondOutline { get; private set; }

    public const int TileWidth = 64;
    public const int TileHeight = 32;

    public static void Initialize(GraphicsDevice device)
    {
        Pixel = new Texture2D(device, 1, 1);
        Pixel.SetData(new[] { Color.White });

        Circle32 = MakeCircle(device, 32);
        Diamond = MakeDiamond(device, TileWidth, TileHeight, filled: true);
        DiamondOutline = MakeDiamond(device, TileWidth, TileHeight, filled: false);
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

    private static Texture2D MakeDiamond(GraphicsDevice device, int w, int h, bool filled)
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
                    bool edge = d >= 0.93f;
                    c = filled ? (edge ? new Color(255, 255, 255, 160) : Color.White)
                               : (edge ? Color.White : Color.Transparent);
                }
                data[y * w + x] = c;
            }
        tex.SetData(data);
        return tex;
    }
}
