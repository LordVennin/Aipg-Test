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
        if (itemBase == null || _device == null || !itemBase.IsHandheld) return null;
        string key = "weapon:" + itemBase.Id;
        if (_cache.TryGetValue(key, out var cached)) return cached[0];

        var accent = WorldRenderer.ParseColor(itemBase.SpriteColor,
            itemBase.Category == Items.ItemCategory.Staff ? new Color(140, 170, 255) : new Color(150, 150, 160));
        var tex = itemBase.Category switch
        {
            Items.ItemCategory.Staff => DrawStaff(accent),
            Items.ItemCategory.Shield => DrawShield(accent, tall: itemBase.InventoryHeight >= 3),
            _ => DrawMace(accent, big: itemBase.InventoryWidth >= 2),
        };
        _cache[key] = new[] { tex };
        return tex;
    }

    /// <summary>
    /// Tiny (9x9) debuff indicator icons drawn above enemy heads — one per debuff flag.
    /// "stun": golden dizzy-star; "burn": orange flame. Cached by kind.
    /// </summary>
    public static Texture2D GetDebuffIcon(string kind)
    {
        if (_device == null) return null;
        string key = "debuff:" + kind;
        if (_cache.TryGetValue(key, out var cached)) return cached[0];

        const int s = 9;
        var px = new Color[s * s];
        void Set(int x, int y, Color c) { if (x >= 0 && x < s && y >= 0 && y < s) px[y * s + x] = c; }

        if (kind == "stun")
        {
            // 4-pointed golden star with a bright core.
            var gold = new Color(255, 210, 70);
            var goldDark = new Color(200, 150, 30);
            Set(4, 0, goldDark); Set(4, 8, goldDark); Set(0, 4, goldDark); Set(8, 4, goldDark);
            for (int i = 1; i < 8; i++) { Set(4, i, gold); Set(i, 4, gold); }
            Set(2, 2, goldDark); Set(6, 2, goldDark); Set(2, 6, goldDark); Set(6, 6, goldDark);
            Set(4, 4, Color.White);
            Set(3, 4, new Color(255, 240, 170)); Set(5, 4, new Color(255, 240, 170));
        }
        else // burn
        {
            var flame = new Color(240, 120, 30);
            var flameDark = new Color(190, 70, 20);
            var core = new Color(255, 220, 90);
            // Teardrop flame: wide base, wavering tip.
            Set(4, 0, flameDark);
            Set(4, 1, flame); Set(3, 2, flame); Set(5, 2, flameDark);
            for (int y = 3; y <= 7; y++)
            {
                int half = y <= 5 ? (y - 1) / 2 : 5 - (y - 5);
                for (int x = 4 - half; x <= 4 + half; x++) Set(x, y, flame);
            }
            Set(2, 4, flameDark); Set(6, 5, flameDark);
            Set(4, 5, core); Set(4, 6, core); Set(3, 6, core);
        }

        var tex = BakeStrip(px, s, s);
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

    /// <summary>
    /// Held shield, drawn in the same right-pointing strip space as weapons (the renderer's
    /// -90° rotation stands it upright): a rimmed face with a raised metal boss and studs.
    /// Round buckler for small shields, elongated kite shape for tall ones.
    /// </summary>
    private static Texture2D DrawShield(Color face, bool tall)
    {
        const int w = 22, h = 14;
        var px = new Color[w * h];
        void Set(int x, int y, Color c) { if (x >= 0 && x < w && y >= 0 && y < h) px[y * w + x] = c; }

        var faceDark = Shade(face, 0.65f);
        var faceLight = Shade(face, 1.3f);
        var rim = new Color(70, 66, 74);
        var boss = new Color(190, 190, 200);
        var bossDark = Shade(boss, 0.6f);

        // Shield silhouette as an ellipse; kite shields taper toward the bottom point
        // (-x in strip space, which becomes downward once rotated upright).
        float cx = tall ? 11.5f : 10.5f, cy = 6.5f;
        float rx = tall ? 9.0f : 6.0f, ry = 5.2f;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float dx = (x - cx) / rx, dy = (y - cy) / ry;
                // Taper: shrink the half-height on the -x side for the kite point.
                if (tall && x < cx) dy /= MathF.Max(0.25f, 1f - (cx - x) / (rx * 1.4f));
                float d = dx * dx + dy * dy;
                if (d > 1f) continue;
                Set(x, y, d > 0.72f ? rim : face);
            }

        // Face shading: light along the top edge, dark along the bottom.
        for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++)
            {
                if (px[y * w + x] != face) continue;
                if (y < cy - ry * 0.45f) Set(x, y, faceLight);
                else if (y > cy + ry * 0.45f) Set(x, y, faceDark);
            }

        // Central boss + rivets.
        int bx = (int)cx, by = (int)cy;
        Set(bx, by, boss); Set(bx + 1, by, boss); Set(bx, by + 1, bossDark); Set(bx + 1, by + 1, bossDark);
        Set(bx - 1, by, bossDark); Set(bx, by - 1, boss);
        Set(bx + (tall ? 5 : 3), by, bossDark);
        Set(bx - (tall ? 5 : 3), by, bossDark);

        return BakeStrip(px, w, h);
    }

    // ------------------------------------------------------------------ zombie (shambler)

    /// <summary>
    /// Decomposed fantasy shambler: hunched posture, rotting mottled flesh with wounds,
    /// ribs showing through the torn burial tunic, one skeletal arm, exposed skull patch,
    /// jagged hem like grave-wrappings, glowing ember eyes. 3-frame shamble. Faces right.
    /// </summary>
    private static Texture2D DrawZombie(Color clothTint, string seedKey, int frame)
    {
        var c = new Canvas();
        var rng = new Random(seedKey.GetHashCode() & int.MaxValue);

        var skin = new Color(122, 150, 96);          // sickly grave-green
        var skinLight = new Color(150, 178, 120);
        var skinDark = Shade(skin, 0.62f);
        var bone = new Color(214, 206, 178);
        var boneDark = Shade(bone, 0.7f);
        var wound = new Color(110, 42, 48);          // dried gore
        var woundDark = Shade(wound, 0.6f);
        var cloth = Shade(clothTint, 0.85f);
        var clothDark = Shade(clothTint, 0.55f);
        var wrap = new Color(96, 90, 70);            // rotten grave-wrappings
        var eye = new Color(255, 96, 40);            // ember glow
        var eyeCore = new Color(255, 200, 120);

        int leftLegUp = frame == 1 ? 1 : 0;
        int rightLegUp = frame == 2 ? 1 : 0;
        int bob = frame == 1 ? 1 : 0;
        int lurch = frame == 2 ? 1 : 0;              // whole body lurches forward on frame 2

        // Legs: one wrapped in rotten bandages, one bare rotting flesh; shuffling feet.
        c.Rect(9, 28, 3, 7 - leftLegUp, wrap);
        c.Set(10, 30, Shade(wrap, 0.7f));
        c.Set(9, 32, Shade(wrap, 0.7f));
        c.Rect(14, 28, 3, 7 - rightLegUp, skinDark);
        c.Set(15, 31, wound);                                     // leg wound
        c.Rect(9, 34 - leftLegUp, 3, 1, Shade(wrap, 0.5f));
        c.Rect(14, 34 - rightLegUp, 3, 1, Shade(skin, 0.4f));

        // Hunched torso in a torn burial tunic; chest split open showing ribs.
        int ty = 18 + bob;
        int tx = 8 + lurch;
        c.Rect(tx, ty, 10, 10, cloth);
        c.Rect(tx + 8, ty, 2, 10, clothDark);
        c.Rect(tx - 1, ty + 1, 2, 4, clothDark);                  // hunch hump behind
        // Torn-open chest: dark cavity with pale ribs.
        c.Rect(tx + 3, ty + 2, 4, 5, woundDark);
        c.Rect(tx + 3, ty + 2, 4, 1, bone);
        c.Rect(tx + 3, ty + 4, 4, 1, bone);
        c.Set(tx + 4, ty + 6, boneDark);
        // Jagged hem, like decayed wrappings trailing off.
        for (int i = 0; i < 5; i++)
            c.Set(tx + rng.Next(10), ty + 9, Color.Transparent);
        c.Set(tx + 2, ty + 8, wrap);
        c.Set(tx + 7, ty + 9, wrap);

        // Leading arm: bare skeletal bone with clawed hand.
        int ay = 19 + bob;
        c.Rect(tx + 9, ay, 2, 2, skinDark);                       // rotted shoulder stump
        c.Rect(tx + 11, ay, 6, 1, bone);                          // humerus
        c.Set(tx + 14, ay, boneDark);                             // joint
        c.Rect(tx + 16, ay - 1, 1, 2, bone);                      // claw up
        c.Set(tx + 16, ay + 1, boneDark);                         // claw down
        // Trailing arm: swollen flesh hanging low.
        c.Rect(tx + 8, ay + 4, 4, 2, skin);
        c.Set(tx + 11, ay + 5, skinDark);
        c.Set(tx + 9, ay + 4, wound);                             // gash

        // Head: hunched forward and low, half flesh half exposed skull.
        int hx = 10 + lurch, hy = 10 + bob;
        c.Rect(hx, hy, 8, 8, skin);
        c.Rect(hx, hy, 8, 1, skinDark);                           // rotted scalp line
        c.Rect(hx + 5, hy + 1, 3, 4, bone);                       // exposed skull side
        c.Set(hx + 5, hy + 1, boneDark);
        c.Rect(hx, hy + 6, 8, 2, skinDark);                       // sagging jaw shadow
        c.Set(hx + 2, hy + 3, eye);                               // sunken ember eyes
        c.Set(hx + 6, hy + 3, eye);
        c.Set(hx + 6, hy + 3, eyeCore);
        c.Set(hx + 2, hy + 2, Shade(skin, 0.4f));
        c.Set(hx + 6, hy + 2, boneDark);
        c.Rect(hx + 3, hy + 6, 4, 1, woundDark);                  // slack open mouth
        c.Set(hx + 4, hy + 7, wound);                             // dripping ichor
        c.Set(hx - 1, hy + 5, skinDark);                          // torn cheek flap

        // Mottled rot, moss and old wounds scattered over flesh and cloth.
        for (int i = 0; i < 7; i++)
        {
            int x = tx + rng.Next(11);
            int y = hy + rng.Next(17);
            if (x < 0 || x >= W || y < 0 || y >= H || c.Px[y * W + x].A == 0) continue;
            c.Set(x, y, rng.Next(3) switch
            {
                0 => wound,
                1 => new Color(70, 96, 54),   // moss
                _ => skinLight,               // blistered highlights
            });
        }

        return c.Bake(_device);
    }

    // ------------------------------------------------------------------ enchanting scrolls

    /// <summary>
    /// Sprite for an Enchanting Scroll base: a parchment scroll whose ribbon color comes from
    /// the base's SpriteColor and whose markings encode the effect — ribbon position shows the
    /// affix side (left = prefix, right = suffix, center = random), notches show the required
    /// rarity tier, a cut line marks remover types, and a wax blob marks the Sealing scroll.
    /// </summary>
    public static Texture2D GetEnchantScrollSprite(Items.ItemBase itemBase)
    {
        if (itemBase == null || _device == null || itemBase.Category != Items.ItemCategory.EnchantScroll)
            return null;
        string key = "enchant:" + itemBase.Id;
        if (_cache.TryGetValue(key, out var cached)) return cached[0];

        var accent = WorldRenderer.ParseColor(itemBase.SpriteColor, new Color(180, 160, 220));
        var tex = DrawEnchantScroll(accent, itemBase.EnchantType);
        _cache[key] = new[] { tex };
        return tex;
    }

    private static Texture2D DrawEnchantScroll(Color accent, Items.EnchantType type)
    {
        const int w = 16, h = 16;
        var px = new Color[w * h];
        void Set(int x, int y, Color c) { if (x >= 0 && x < w && y >= 0 && y < h) px[y * w + x] = c; }
        void Rect(int x0, int y0, int rw, int rh, Color c)
        { for (int y = y0; y < y0 + rh; y++) for (int x = x0; x < x0 + rw; x++) Set(x, y, c); }

        var parchment = new Color(226, 214, 180);
        var parchDark = new Color(188, 174, 140);
        var accentDark = Shade(accent, 0.6f);

        // Parchment body with rolled ends.
        Rect(2, 5, 12, 6, parchment);
        Rect(2, 10, 12, 1, parchDark);
        Rect(1, 4, 2, 8, parchDark);       // left roll
        Rect(13, 4, 2, 8, parchDark);
        Set(1, 4, Shade(parchDark, 0.8f));
        Set(14, 4, Shade(parchDark, 0.8f));

        // Ribbon: left = prefix effects, right = suffix effects, center = random/any.
        int ribbonX = type switch
        {
            Items.EnchantType.AddPrefixMagic or Items.EnchantType.AddPrefixRare or Items.EnchantType.ReforgePrefix => 4,
            Items.EnchantType.AddSuffixMagic or Items.EnchantType.AddSuffixRare or Items.EnchantType.ReforgeSuffix => 10,
            _ => 7,
        };
        Rect(ribbonX, 3, 2, 10, accent);
        Rect(ribbonX, 11, 2, 2, accentDark);

        // Rarity tier notches above the parchment: 1 = blue-tier, 2 = gold-tier.
        int tier = type switch
        {
            Items.EnchantType.AddPrefixMagic or Items.EnchantType.AddSuffixMagic => 1,
            Items.EnchantType.AddRandomRare or Items.EnchantType.AddPrefixRare or
            Items.EnchantType.AddSuffixRare or Items.EnchantType.SealExpand => 2,
            _ => 0,
        };
        for (int i = 0; i < tier; i++)
            Rect(3 + i * 3, 1, 2, 2, accent);

        // Gilding: an upgrade scroll — a blue notch turning into a gold notch, with an arrow.
        if (type == Items.EnchantType.GildUpgrade)
        {
            Rect(3, 1, 2, 2, new Color(95, 143, 239));    // blue (magic)
            Set(6, 2, accent); Set(7, 2, accent);          // arrow shaft
            Set(8, 1, accent); Set(8, 3, accent);          // arrow head
            Rect(9, 1, 2, 2, new Color(232, 190, 70));     // gold (rare)
        }

        // Remover types: a dark cut across the parchment.
        if (type is Items.EnchantType.RemoveRandom or Items.EnchantType.Reforge
            or Items.EnchantType.ReforgePrefix or Items.EnchantType.ReforgeSuffix)
        {
            for (int i = 0; i < 5; i++) Set(4 + i * 2, 7 + (i & 1), new Color(60, 44, 44));
        }

        // Sealing scroll: a wax seal blob.
        if (type == Items.EnchantType.SealExpand)
        {
            Rect(10, 8, 4, 4, accent);
            Set(11, 9, Shade(accent, 1.5f));
            Set(13, 11, accentDark);
        }

        return BakeStrip(px, w, h);
    }

    // ------------------------------------------------------------------ gold pile

    private static Texture2D _goldPile;

    /// <summary>Small pile of coins for gold drops.</summary>
    public static Texture2D GetGoldPile()
    {
        if (_goldPile != null || _device == null) return _goldPile;
        const int w = 16, h = 10;
        var px = new Color[w * h];
        void Set(int x, int y, Color c) { if (x >= 0 && x < w && y >= 0 && y < h) px[y * w + x] = c; }
        var gold = new Color(232, 190, 70);
        var goldDark = new Color(176, 134, 42);
        var goldLight = new Color(255, 232, 150);
        void Coin(int cx, int cy)
        {
            for (int y = -1; y <= 1; y++)
                for (int x = -2; x <= 2; x++)
                    if (Math.Abs(x) + Math.Abs(y) * 2 <= 3) Set(cx + x, cy + y, gold);
            Set(cx - 2, cy + 1, goldDark); Set(cx + 2, cy + 1, goldDark);
            Set(cx, cy + 1, goldDark);
            Set(cx - 1, cy - 1, goldLight);
        }
        Coin(4, 6); Coin(11, 6); Coin(8, 7);
        Coin(7, 3); // top coin
        _goldPile = BakeStrip(px, w, h);
        return _goldPile;
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
