using ARPG.Data;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ARPG.Render;

/// <summary>
/// Procedural pixel-art enemy sprites, generated at runtime (no external assets).
/// Each style produces a small set of animation frames drawn facing RIGHT;
/// the renderer mirrors them for left-facing enemies. Frames are cached per enemy type.
/// </summary>
public static class SpriteGen
{
    private static GraphicsDevice _device;
    private static readonly Dictionary<string, Texture2D[]> _cache = new();

    public static void Initialize(GraphicsDevice device) => _device = device;

    public static Texture2D[] GetEnemyFrames(EnemyDefinition def)
    {
        if (def == null || _device == null || string.IsNullOrEmpty(def.SpriteStyle)) return null;
        if (_cache.TryGetValue(def.Id, out var cached)) return cached;

        var tint = WorldRenderer.ParseColor(def.Color, new Color(180, 60, 60));
        var frames = def.SpriteStyle switch
        {
            "Zombie" => new[] { DrawZombie(tint, def.Id, 0), DrawZombie(tint, def.Id, 1), DrawZombie(tint, def.Id, 2) },
            "Ghoul" => new[] { DrawGhoul(tint, def.Id, 0), DrawGhoul(tint, def.Id, 1) },
            _ => null,
        };
        _cache[def.Id] = frames;
        return frames;
    }

    /// <summary>
    /// Held-weapon sprite for a weapon ItemBase, drawn pointing RIGHT with the grip at the
    /// left edge; the renderer rotates it toward the player's aim. Cached per base id.
    /// </summary>
    public static Texture2D GetWeaponSprite(Items.ItemBase itemBase)
    {
        if (itemBase == null || _device == null || !itemBase.IsWeapon) return null;
        string key = "weapon:" + itemBase.Id;
        if (_cache.TryGetValue(key, out var cached)) return cached[0];

        var accent = WorldRenderer.ParseColor(itemBase.SpriteColor,
            itemBase.Category == Items.ItemCategory.Staff ? new Color(140, 170, 255) : new Color(150, 150, 160));
        var tex = itemBase.Category switch
        {
            Items.ItemCategory.Staff => DrawStaff(accent),
            _ => DrawMace(accent, big: itemBase.InventoryWidth >= 2),
        };
        _cache[key] = new[] { tex };
        return tex;
    }

    // ------------------------------------------------------------------ pixel canvas

    private const int W = 26, H = 36;

    private sealed class Canvas
    {
        public readonly Color[] Px = new Color[W * H];

        public void Set(int x, int y, Color c)
        {
            if (x >= 0 && x < W && y >= 0 && y < H) Px[y * W + x] = c;
        }

        public void Rect(int x0, int y0, int w, int h, Color c)
        {
            for (int y = y0; y < y0 + h; y++)
                for (int x = x0; x < x0 + w; x++)
                    Set(x, y, c);
        }

        /// <summary>Add a dark cartoon outline around the silhouette, then bake to a texture.</summary>
        public Texture2D Bake(GraphicsDevice device)
        {
            var outline = new Color(14, 10, 16);
            var result = (Color[])Px.Clone();
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    if (Px[y * W + x].A != 0) continue;
                    bool nearBody =
                        (x > 0 && Px[y * W + x - 1].A != 0) || (x < W - 1 && Px[y * W + x + 1].A != 0) ||
                        (y > 0 && Px[(y - 1) * W + x].A != 0) || (y < H - 1 && Px[(y + 1) * W + x].A != 0);
                    if (nearBody) result[y * W + x] = outline;
                }
            var tex = new Texture2D(device, W, H);
            tex.SetData(result);
            return tex;
        }
    }

    private static Color Shade(Color c, float factor) =>
        new((int)Math.Min(255, c.R * factor), (int)Math.Min(255, c.G * factor), (int)Math.Min(255, c.B * factor));

    // ------------------------------------------------------------------ held weapons

    /// <summary>Small horizontal canvas for held weapons (grip left, business end right).</summary>
    private static Texture2D BakeStrip(Color[] px, int w, int h)
    {
        // Outline pass identical in spirit to Canvas.Bake.
        var outline = new Color(14, 10, 16);
        var result = (Color[])px.Clone();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (px[y * w + x].A != 0) continue;
                bool nearBody =
                    (x > 0 && px[y * w + x - 1].A != 0) || (x < w - 1 && px[y * w + x + 1].A != 0) ||
                    (y > 0 && px[(y - 1) * w + x].A != 0) || (y < h - 1 && px[(y + 1) * w + x].A != 0);
                if (nearBody) result[y * w + x] = outline;
            }
        var tex = new Texture2D(_device, w, h);
        tex.SetData(result);
        return tex;
    }

    private static Texture2D DrawMace(Color metal, bool big)
    {
        const int w = 26, h = 12;
        var px = new Color[w * h];
        void Set(int x, int y, Color c) { if (x >= 0 && x < w && y >= 0 && y < h) px[y * w + x] = c; }
        void Rect(int x0, int y0, int rw, int rh, Color c)
        { for (int y = y0; y < y0 + rh; y++) for (int x = x0; x < x0 + rw; x++) Set(x, y, c); }

        var wood = new Color(120, 86, 48);
        var woodDark = Shade(wood, 0.7f);
        var metalDark = Shade(metal, 0.65f);
        var metalLight = Shade(metal, 1.35f);

        // Haft with a wrapped grip.
        Rect(1, 5, 15, 2, wood);
        Rect(1, 5, 4, 2, woodDark);       // grip wrap
        Set(3, 5, wood);

        // Head: rounded block with studs; bigger for two-handed maces.
        int headW = big ? 9 : 7, headH = big ? 10 : 8;
        int hx = 15, hy = (h - headH) / 2;
        Rect(hx, hy + 1, headW, headH - 2, metal);
        Rect(hx + 1, hy, headW - 2, headH, metal);
        Rect(hx + 1, hy + headH - 2, headW - 2, 2, metalDark);   // bottom shading
        Rect(hx + 1, hy, headW - 2, 1, metalLight);              // top highlight
        // Studs
        Set(hx + 2, hy + 3, metalLight);
        Set(hx + headW - 3, hy + 3, metalLight);
        Set(hx + headW / 2, hy + headH - 3, metalDark);
        Set(hx + headW / 2, hy + 1, metalLight);

        return BakeStrip(px, w, h);
    }

    private static Texture2D DrawStaff(Color orb)
    {
        const int w = 30, h = 12;
        var px = new Color[w * h];
        void Set(int x, int y, Color c) { if (x >= 0 && x < w && y >= 0 && y < h) px[y * w + x] = c; }
        void Rect(int x0, int y0, int rw, int rh, Color c)
        { for (int y = y0; y < y0 + rh; y++) for (int x = x0; x < x0 + rw; x++) Set(x, y, c); }

        var wood = new Color(104, 76, 46);
        var woodDark = Shade(wood, 0.7f);
        var orbDark = Shade(orb, 0.6f);
        var orbLight = Shade(orb, 1.5f);

        // Long shaft with grip wrap and a couple of carved rings.
        Rect(1, 5, 22, 2, wood);
        Rect(1, 5, 4, 2, woodDark);
        Set(10, 5, woodDark); Set(10, 6, woodDark);
        Set(16, 5, woodDark); Set(16, 6, woodDark);

        // Prongs cradling the orb.
        Set(22, 3, woodDark); Set(23, 2, woodDark);
        Set(22, 8, woodDark); Set(23, 9, woodDark);

        // Glowing orb.
        int ox = 24, oy = 3;
        Rect(ox, oy + 1, 5, 4, orb);
        Rect(ox + 1, oy, 3, 6, orb);
        Set(ox + 1, oy + 1, orbLight);   // sparkle highlight
        Set(ox + 3, oy + 4, orbDark);
        Set(ox + 4, oy + 4, orbDark);

        return BakeStrip(px, w, h);
    }

    // ------------------------------------------------------------------ zombie (shambler)

    /// <summary>Classic shambler: rotting skin, tattered shirt (tinted by the enemy color),
    /// both arms stretched forward, glowing eyes, 3-frame walk cycle. Faces right.</summary>
    private static Texture2D DrawZombie(Color clothTint, string seedKey, int frame)
    {
        var c = new Canvas();
        var rng = new Random(seedKey.GetHashCode() & int.MaxValue);

        var skin = new Color(150, 176, 128);
        var skinDark = Shade(skin, 0.72f);
        var cloth = clothTint;
        var clothDark = Shade(clothTint, 0.6f);
        var pants = Shade(clothTint, 0.42f);
        var eye = new Color(235, 60, 40);
        var decay = new Color(84, 104, 62);

        // Walk cycle: legs alternate, arms/torso bob 1px on the middle frame.
        int leftLegUp = frame == 1 ? 1 : 0;
        int rightLegUp = frame == 2 ? 1 : 0;
        int bob = frame == 1 ? 1 : 0;

        // Legs (pants), feet at y=34.
        c.Rect(9, 27, 3, 8 - leftLegUp, pants);
        c.Rect(14, 27, 3, 8 - rightLegUp, pants);
        c.Rect(9, 33 - leftLegUp, 3, 1, Shade(pants, 0.7f));   // foot shading
        c.Rect(14, 33 - rightLegUp, 3, 1, Shade(pants, 0.7f));

        // Torso (tattered shirt).
        int ty = 17 + bob;
        c.Rect(8, ty, 10, 10, cloth);
        c.Rect(16, ty, 2, 10, clothDark);                      // side shading
        for (int i = 0; i < 4; i++)                            // torn hem
            c.Set(8 + rng.Next(10), ty + 9, Color.Transparent);
        c.Set(10, ty + 3, clothDark);                          // rips
        c.Set(13, ty + 6, clothDark);

        // Arms reaching forward (sleeve then bare skin).
        int ay = 18 + bob;
        c.Rect(17, ay, 3, 2, clothDark);
        c.Rect(20, ay, 5, 2, skin);
        c.Rect(24, ay, 1, 1, skinDark);                        // hand
        c.Rect(17, ay + 4, 2, 2, clothDark);
        c.Rect(19, ay + 4, 4, 2, skinDark);                    // lower arm, slightly shorter

        // Head.
        int hy = 8 + bob;
        c.Rect(9, hy, 8, 9, skin);
        c.Rect(9, hy + 7, 8, 2, skinDark);                     // jaw shadow
        c.Rect(9, hy, 8, 1, new Color(60, 72, 48));            // scraggly hair
        c.Set(10, hy + 1, new Color(60, 72, 48));
        c.Set(15, hy + 1, new Color(60, 72, 48));
        c.Set(12, hy + 3, eye);                                // glowing eyes
        c.Set(15, hy + 3, eye);
        c.Set(12, hy + 2, Shade(skin, 0.5f));                  // sunken sockets
        c.Set(15, hy + 2, Shade(skin, 0.5f));
        c.Rect(13, hy + 6, 3, 1, new Color(70, 40, 40));       // slack mouth

        // Random decay patches on skin and shirt.
        for (int i = 0; i < 5; i++)
        {
            int x = 8 + rng.Next(10);
            int y = hy + rng.Next(16);
            if (c.Px[y * W + x].A != 0) c.Set(x, y, decay);
        }

        return c.Bake(_device);
    }

    // ------------------------------------------------------------------ ghoul (hunched spitter)

    /// <summary>Hunched four-legged spitter with a gaping jaw and back spines. Faces right.</summary>
    private static Texture2D DrawGhoul(Color bodyTint, string seedKey, int frame)
    {
        var c = new Canvas();
        var rng = new Random((seedKey + "g").GetHashCode() & int.MaxValue);

        var body = bodyTint;
        var bodyDark = Shade(bodyTint, 0.62f);
        var belly = Shade(bodyTint, 1.25f);
        var eye = new Color(250, 220, 80);
        var maw = new Color(70, 25, 35);
        var teeth = new Color(230, 225, 200);

        int bob = frame == 1 ? 1 : 0;

        // Four stubby legs, alternating pairs.
        int f = frame == 1 ? 1 : 0;
        c.Rect(7, 28 - f, 2, 6 + f, bodyDark);
        c.Rect(11, 28, 2, 6, bodyDark);
        c.Rect(15, 28 - f, 2, 6 + f, bodyDark);
        c.Rect(19, 28, 2, 6, bodyDark);

        // Humped body.
        int by = 17 + bob;
        c.Rect(5, by + 2, 14, 9, body);
        c.Rect(6, by, 11, 2, body);                            // hump top
        c.Rect(5, by + 8, 14, 3, bodyDark);                    // underside shading
        c.Rect(7, by + 6, 9, 2, belly);                        // belly highlight

        // Back spines.
        foreach (int sx in new[] { 7, 10, 13 })
        {
            c.Set(sx, by - 1, bodyDark);
            c.Set(sx, by - 2, Shade(bodyTint, 0.45f));
        }

        // Head thrust forward with a gaping jaw (opens wider on frame 1).
        int hy2 = 13 + bob;
        c.Rect(17, hy2, 7, 6, body);
        c.Rect(17, hy2 + 5, 6, 2, bodyDark);
        c.Set(20, hy2 + 2, eye);
        c.Set(20, hy2 + 1, Shade(bodyTint, 0.45f));
        int jaw = frame == 1 ? 3 : 2;
        c.Rect(20, hy2 + 4, 5, jaw, maw);                      // open mouth
        c.Set(20, hy2 + 4, teeth);                             // teeth
        c.Set(23, hy2 + 4, teeth);
        c.Set(21, hy2 + 4 + jaw - 1, teeth);

        // Mottled hide.
        for (int i = 0; i < 6; i++)
        {
            int x = 5 + rng.Next(14);
            int y = by + rng.Next(9);
            if (c.Px[y * W + x].A != 0) c.Set(x, y, bodyDark);
        }

        return c.Bake(_device);
    }
}
