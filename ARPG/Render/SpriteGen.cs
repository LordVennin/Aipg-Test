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
            "Skeleton" => new[]
            {
                DrawSkeletonKnight(tint, def.Id, 0), DrawSkeletonKnight(tint, def.Id, 1),
                DrawSkeletonKnight(tint, def.Id, 2),
            },
            "Necro" => new[] { DrawNecromancer(tint, 0), DrawNecromancer(tint, 1) },
            _ => null,
        };
        _cache[def.Id] = frames;
        return frames;
    }

    /// <summary>
    /// A DEAD body sprite derived from the enemy's OWN standing sprite, so the corpse
    /// stays recognizably that enemy: frame 0 rotated onto its side, squashed flat
    /// against the ground, darkened, with ragged silhouette edges — plus a few loose
    /// bones shaken out of skeletons. Cached per definition.
    /// </summary>
    public static Texture2D GetEnemyCorpseSprite(EnemyDefinition def)
    {
        if (def == null || _device == null) return null;
        var frames = GetEnemyFrames(def);
        if (frames == null || frames.Length == 0) return null;
        string key = "corpse:" + def.Id;
        if (_cache.TryGetValue(key, out var cached)) return cached[0];

        var srcTex = frames[0];
        int sw = srcTex.Width, sh = srcTex.Height;
        var src = new Color[sw * sh];
        srcTex.GetData(src);

        // Rotate 90° clockwise: the body lies on its side, head to the right.
        int rw = sh, rh = sw;
        var rot = new Color[rw * rh];
        for (int y = 0; y < rh; y++)
            for (int x = 0; x < rw; x++)
                rot[y * rw + x] = src[(sh - 1 - x) * sw + y];

        // Squash flat against the ground (the body has no strength left in it),
        // darken, and roughen the silhouette so it reads crumpled — not just tipped.
        int fh = Math.Max(5, (int)(rh * 0.62f));
        var px = new Color[rw * (fh + 2)];
        uint idHash = 2166136261u;
        foreach (char ch in def.Id) idHash = (idHash ^ ch) * 16777619u;
        for (int y = 0; y < fh; y++)
        {
            int syRow = Math.Min(rh - 1, y * rh / fh);
            for (int x = 0; x < rw; x++)
            {
                var c = rot[syRow * rw + x];
                if (c.A == 0) continue;
                // Ragged edges: silhouette-border pixels drop out by hash.
                bool border =
                    x == 0 || x == rw - 1 || rot[syRow * rw + Math.Max(0, x - 1)].A == 0 ||
                    rot[syRow * rw + Math.Min(rw - 1, x + 1)].A == 0;
                uint n = (uint)(x * 73856093 ^ y * 19349663) ^ idHash;
                n ^= n >> 13; n *= 0x5bd1e995; n ^= n >> 15;
                if (border && (n & 0xFF) < 70) continue;
                px[(y + 2) * rw + x] = new Color(
                    (byte)(c.R * 0.8f), (byte)(c.G * 0.78f), (byte)(c.B * 0.8f), c.A);
            }
        }
        // Skeletons shake a few loose bones out of the pile.
        if (def.SpriteStyle == "Skeleton")
        {
            var bone = new Color(210, 202, 180);
            void Set(int x, int y, Color c)
            { if (x >= 0 && x < rw && y >= 0 && y < fh + 2) px[y * rw + x] = c; }
            Set(1, fh - 1, bone); Set(2, fh - 1, bone);
            Set(rw - 3, 3, bone); Set(rw - 2, 3, bone);
            Set(rw / 2, 0, bone);
        }

        var tex = BakeStrip(px, rw, fh + 2);
        _cache[key] = new[] { tex };
        return tex;
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
            Items.ItemCategory.Bow => DrawBow(accent),
            _ => DrawMace(accent, big: itemBase.InventoryWidth >= 2),
        };
        _cache[key] = new[] { tex };
        return tex;
    }

    /// <summary>The bow seen EDGE-ON — used when the character faces toward or away
    /// from the camera, where the bow's plane is perpendicular to the screen and the
    /// full arc shouldn't show. Same axis convention as every held weapon (drawn along
    /// X, rotated upright by the renderer). Cached per base id.</summary>
    public static Texture2D GetBowFrontSprite(Items.ItemBase itemBase)
    {
        if (itemBase == null || _device == null || itemBase.Category != Items.ItemCategory.Bow) return null;
        string key = "bowfront:" + itemBase.Id;
        if (_cache.TryGetValue(key, out var cached)) return cached[0];

        var wood = WorldRenderer.ParseColor(itemBase.SpriteColor, new Color(150, 150, 160));
        var woodDark = Shade(wood, 0.7f);
        var grip = new Color(88, 64, 42);
        const int w = 30, h = 8;
        var px = new Color[w * h];
        void Set(int x, int y, Color c) { if (x >= 0 && x < w && y >= 0 && y < h) px[y * w + x] = c; }

        // A near-straight stave: the limbs foreshorten to a stick, tips flare a pixel
        // toward the viewer, and the grip bulges at the middle. No visible string.
        for (int x = 3; x <= 26; x++) { Set(x, 3, wood); Set(x, 4, woodDark); }
        Set(2, 3, woodDark); Set(27, 3, woodDark);            // tips
        Set(2, 2, wood); Set(27, 2, wood);                    // tip flare toward the viewer
        Set(6, 3, Shade(wood, 1.2f)); Set(23, 3, Shade(wood, 1.2f)); // limb sheen
        for (int x = 13; x <= 16; x++) { Set(x, 2, grip); Set(x, 3, grip); Set(x, 4, grip); Set(x, 5, Shade(grip, 0.7f)); }

        var tex = BakeStrip(px, w, h);
        _cache[key] = new[] { tex };
        return tex;
    }

    /// <summary>Inventory icon for worn armor and jewelry: a small upright glyph per
    /// category, shaped by the base's ArmorStyle and tinted by its SpriteColor — so a
    /// hood, a cap and a full helm read apart at a glance in the bag. Cached per base.</summary>
    public static Texture2D GetArmorSprite(Items.ItemBase itemBase)
    {
        if (_device == null || itemBase == null) return null;
        bool armorish = itemBase.Category is Items.ItemCategory.Helmet or Items.ItemCategory.BodyArmor
            or Items.ItemCategory.Gloves or Items.ItemCategory.Boots or Items.ItemCategory.Belt
            or Items.ItemCategory.Amulet or Items.ItemCategory.Ring;
        if (!armorish) return null;
        string key = "armor:" + itemBase.Id;
        if (_cache.TryGetValue(key, out var cached)) return cached[0];
        var tint = WorldRenderer.ParseColor(itemBase.SpriteColor, new Color(140, 128, 110));
        var tex = itemBase.Category switch
        {
            Items.ItemCategory.Helmet => DrawHelmetIcon(PlayerLook.HelmetStyleId(itemBase.ArmorStyle), tint),
            Items.ItemCategory.BodyArmor => DrawBodyArmorIcon(PlayerLook.ArmorStyleId(itemBase.ArmorStyle), tint),
            Items.ItemCategory.Gloves => DrawGlovesIcon(tint),
            Items.ItemCategory.Boots => DrawBootsIcon(tint),
            Items.ItemCategory.Belt => DrawBeltIcon(tint),
            Items.ItemCategory.Amulet => DrawAmuletIcon(tint),
            _ => DrawRingIcon(tint),
        };
        _cache[key] = new[] { tex };
        return tex;
    }

    private static Texture2D DrawHelmetIcon(byte style, Color c)
    {
        const int w = 14, h = 13;
        var px = new Color[w * h];
        void Set(int x, int y, Color k) { if (x >= 0 && x < w && y >= 0 && y < h) px[y * w + x] = k; }
        void Rect(int x0, int y0, int x1, int y1, Color k)
        { for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) Set(x, y, k); }
        var dark = Shade(c, 0.68f);
        var light = Shade(c, 1.25f);
        var hollow = new Color(24, 22, 20);
        switch (style)
        {
            case 3: // cap: open dome, rim, cheek guards.
                Rect(3, 2, 10, 5, c);
                Rect(4, 1, 9, 1, light);
                Rect(2, 6, 11, 6, dark);
                Rect(2, 7, 3, 10, c); Rect(10, 7, 11, 10, c);
                Rect(5, 7, 8, 10, hollow);
                break;
            case 4: // helm: closed box with an eye slit.
                Rect(2, 1, 11, 11, c);
                Rect(3, 0, 10, 0, light);
                Rect(3, 5, 10, 5, hollow);
                Rect(6, 7, 7, 10, dark);
                Set(2, 11, dark); Set(11, 11, dark);
                break;
            default: // hood (and cowl adds the peak): soft drape with a dark opening.
                Rect(3, 2, 10, 4, c);
                Rect(2, 5, 3, 11, c); Rect(10, 5, 11, 11, c);
                Rect(2, 11, 11, 11, dark);
                Rect(4, 5, 9, 10, hollow);
                Set(3, 2, light); Set(10, 2, light);
                if (style == 2) { Rect(6, 0, 7, 1, c); Set(6, 0, light); } // cowl peak
                break;
        }
        return BakeStrip(px, w, h);
    }

    private static Texture2D DrawBodyArmorIcon(byte style, Color c)
    {
        const int w = 14, h = 15;
        var px = new Color[w * h];
        void Set(int x, int y, Color k) { if (x >= 0 && x < w && y >= 0 && y < h) px[y * w + x] = k; }
        void Rect(int x0, int y0, int x1, int y1, Color k)
        { for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) Set(x, y, k); }
        var dark = Shade(c, 0.68f);
        var light = Shade(c, 1.25f);
        // Shared shoulders + neck hole.
        Rect(1, 1, 12, 3, c);
        Rect(5, 1, 8, 1, new Color(24, 22, 20));
        Set(1, 1, dark); Set(12, 1, dark);
        switch (style)
        {
            case 1: // cloth: a long robe with a center fold and hem.
                Rect(2, 4, 11, 13, c);
                Rect(2, 13, 11, 13, dark);
                Rect(6, 4, 6, 12, dark);
                break;
            case 3: // mail: cropped torso in rings.
                Rect(2, 4, 11, 11, c);
                for (int y = 4; y <= 11; y++)
                    for (int x = 2; x <= 11; x++)
                        if (((x + y) & 1) == 0) Set(x, y, light);
                Rect(2, 11, 11, 11, dark);
                break;
            case 4: // plate: cuirass with ridge and waist cut.
                Rect(2, 4, 11, 10, c);
                Rect(4, 4, 4, 10, light);
                Rect(2, 8, 11, 8, dark);
                Rect(3, 11, 10, 12, c);
                Rect(3, 12, 10, 12, dark);
                break;
            default: // leather: jerkin with a chest strap and stitches.
                Rect(2, 4, 11, 12, c);
                Rect(2, 7, 11, 7, dark);
                Rect(2, 12, 11, 12, dark);
                Set(3, 5, dark); Set(10, 5, dark); Set(3, 10, dark); Set(10, 10, dark);
                break;
        }
        return BakeStrip(px, w, h);
    }

    private static Texture2D DrawGlovesIcon(Color c)
    {
        const int w = 14, h = 11;
        var px = new Color[w * h];
        void Rect(int x0, int y0, int x1, int y1, Color k)
        { for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) if (x >= 0 && x < w && y >= 0 && y < h) px[y * w + x] = k; }
        var dark = Shade(c, 0.68f);
        // A mirrored pair of mitts with cuffs and thumbs.
        Rect(1, 1, 5, 2, dark); Rect(8, 1, 12, 2, dark);      // cuffs
        Rect(1, 3, 5, 8, c); Rect(8, 3, 12, 8, c);
        Rect(0, 4, 0, 6, c); Rect(13, 4, 13, 6, c);           // thumbs
        Rect(1, 8, 5, 8, dark); Rect(8, 8, 12, 8, dark);
        return BakeStrip(px, w, h);
    }

    private static Texture2D DrawBootsIcon(Color c)
    {
        const int w = 15, h = 12;
        var px = new Color[w * h];
        void Rect(int x0, int y0, int x1, int y1, Color k)
        { for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) if (x >= 0 && x < w && y >= 0 && y < h) px[y * w + x] = k; }
        var dark = Shade(c, 0.68f);
        var sole = new Color(40, 34, 28);
        // Two boots, toes pointing right.
        Rect(1, 1, 4, 8, c); Rect(8, 1, 11, 8, c);            // shafts
        Rect(1, 8, 6, 10, c); Rect(8, 8, 13, 10, c);          // feet
        Rect(1, 10, 6, 10, sole); Rect(8, 10, 13, 10, sole);  // soles
        Rect(1, 1, 4, 1, dark); Rect(8, 1, 11, 1, dark);      // cuffs
        return BakeStrip(px, w, h);
    }

    private static Texture2D DrawBeltIcon(Color c)
    {
        const int w = 15, h = 9;
        var px = new Color[w * h];
        void Rect(int x0, int y0, int x1, int y1, Color k)
        { for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) if (x >= 0 && x < w && y >= 0 && y < h) px[y * w + x] = k; }
        var dark = Shade(c, 0.68f);
        var buckle = new Color(184, 178, 150);
        Rect(0, 3, 14, 6, c);                                 // strap
        Rect(0, 3, 14, 3, Shade(c, 1.2f));
        Rect(0, 6, 14, 6, dark);
        Rect(5, 2, 9, 7, buckle);                             // buckle frame
        Rect(6, 3, 8, 6, c);                                  // buckle window
        Rect(7, 3, 7, 6, Shade(buckle, 0.7f));                // prong
        return BakeStrip(px, w, h);
    }

    private static Texture2D DrawAmuletIcon(Color c)
    {
        const int w = 13, h = 14;
        var px = new Color[w * h];
        void Set(int x, int y, Color k) { if (x >= 0 && x < w && y >= 0 && y < h) px[y * w + x] = k; }
        var cord = new Color(120, 100, 70);
        // Cord: a V from the top corners down to the pendant.
        for (int i = 0; i <= 5; i++) { Set(1 + i, i, cord); Set(11 - i, i, cord); }
        // Pendant: a diamond of the tint with a glint.
        for (int dy = -3; dy <= 3; dy++)
            for (int dx = -3; dx <= 3; dx++)
                if (Math.Abs(dx) + Math.Abs(dy) <= 3) Set(6 + dx, 9 + dy, c);
        Set(5, 8, Shade(c, 1.45f));
        Set(6, 12, Shade(c, 0.6f));
        return BakeStrip(px, w, h);
    }

    private static Texture2D DrawRingIcon(Color c)
    {
        const int w = 11, h = 11;
        var px = new Color[w * h];
        void Set(int x, int y, Color k) { if (x >= 0 && x < w && y >= 0 && y < h) px[y * w + x] = k; }
        var band = new Color(196, 186, 150);
        var bandDark = Shade(band, 0.7f);
        // The band: a ring of pixels; the stone: the tint on top.
        for (int a = 0; a < 24; a++)
        {
            double ang = a * Math.PI / 12;
            Set(5 + (int)Math.Round(3.2 * Math.Cos(ang)), 6 + (int)Math.Round(3.2 * Math.Sin(ang)),
                ang < Math.PI ? band : bandDark);
        }
        Set(4, 1, c); Set(5, 1, c); Set(6, 1, c); Set(5, 0, c);
        Set(4, 1, Shade(c, 1.4f));
        return BakeStrip(px, w, h);
    }

    /// <summary>Friendly NPC sprites. Cached per type id — the skill trainer is the
    /// same hooded silhouette in scholar's violet with a tome instead of a satchel.</summary>
    public static Texture2D GetNpcSprite(string typeId)
    {
        if (_device == null || string.IsNullOrEmpty(typeId)) return null;
        string key = "npc:" + typeId;
        if (_cache.TryGetValue(key, out var cached)) return cached[0];
        var tex = typeId switch
        {
            "skill_trainer" => DrawTrainer(),
            "mercenary" => DrawMercenary(),
            _ => DrawMerchant(),
        };
        _cache[key] = new[] { tex };
        return tex;
    }

    /// <summary>The sellsword: battered plate, a red sash and a shouldered blade —
    /// unmistakably a fighter next to the robed merchants.</summary>
    private static Texture2D DrawMercenary()
    {
        const int w = 20, h = 28;
        var px = new Color[w * h];
        void Set(int x, int y, Color c) { if (x >= 0 && x < w && y >= 0 && y < h) px[y * w + x] = c; }
        void Rect(int x0, int y0, int x1, int y1, Color c)
        { for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) Set(x, y, c); }

        var plate = new Color(118, 116, 126);
        var plateDark = new Color(82, 80, 90);
        var plateLight = new Color(152, 150, 160);
        var sash = new Color(158, 52, 44);
        var skin = new Color(210, 172, 136);
        var eyes = new Color(36, 30, 24);
        var steel = new Color(196, 196, 206);
        var grip = new Color(96, 68, 42);

        // Legs + boots.
        Rect(6, 20, 8, 26, plateDark);
        Rect(11, 20, 13, 26, plateDark);
        Rect(5, 26, 9, 27, new Color(60, 50, 40));
        Rect(10, 26, 14, 27, new Color(60, 50, 40));
        // Cuirass with a highlight edge and the red sash across it.
        Rect(5, 11, 14, 19, plate);
        Rect(5, 11, 14, 11, plateLight);
        Rect(5, 19, 14, 19, plateDark);
        for (int i = 0; i < 8; i++) Set(6 + i, 12 + i / 2, sash);
        // Pauldrons.
        Rect(3, 11, 5, 14, plateDark);
        Rect(14, 11, 16, 14, plateDark);
        // Head: bare, scarred, cropped hair.
        Rect(7, 3, 12, 9, skin);
        Rect(7, 2, 12, 3, new Color(70, 56, 40));
        Set(8, 6, eyes); Set(11, 6, eyes);
        Set(12, 8, sash); // scar nick
        // Shouldered greatsword rising past the right pauldron.
        for (int i = 0; i < 10; i++) Set(15 + i / 4, 12 - i, steel);
        Set(15, 13, grip); Set(15, 14, grip);
        Rect(14, 12, 17, 12, grip); // crossguard
        return BakeStrip(px, w, h);
    }

    /// <summary>The skill trainer: violet robes, silver trim, an open tome held in
    /// front — the merchant's silhouette recolored so the two read apart at a glance.</summary>
    private static Texture2D DrawTrainer()
    {
        const int w = 20, h = 28;
        var px = new Color[w * h];
        void Set(int x, int y, Color c) { if (x >= 0 && x < w && y >= 0 && y < h) px[y * w + x] = c; }
        void Rect(int x0, int y0, int x1, int y1, Color c)
        { for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) Set(x, y, c); }

        var robe = new Color(74, 58, 104);
        var robeDark = new Color(54, 42, 78);
        var trim = new Color(190, 186, 210);
        var hood = new Color(62, 48, 90);
        var skin = new Color(224, 188, 152);
        var eyes = new Color(40, 32, 26);
        var tome = new Color(160, 130, 70);
        var page = new Color(230, 220, 190);

        for (int y = 10; y <= 26; y++)
        {
            int half = 3 + (y - 10) * 3 / 16;
            Rect(9 - half, y, 10 + half, y, robe);
            Set(9 - half, y, robeDark);
            Set(10 + half, y, robeDark);
        }
        Rect(3, 26, 16, 27, robeDark);
        Rect(3, 25, 16, 25, trim);
        Rect(6, 2, 13, 9, hood);
        Set(6, 2, Color.Transparent); Set(13, 2, Color.Transparent);
        Rect(7, 1, 12, 1, hood);
        Rect(8, 5, 11, 8, skin);
        Set(8, 6, eyes); Set(11, 6, eyes);
        Rect(6, 9, 13, 10, robeDark);
        Rect(9, 11, 10, 24, trim);
        Rect(4, 12, 6, 16, robeDark);
        Rect(13, 12, 15, 16, robeDark);
        // Open tome held in both hands.
        Rect(5, 15, 14, 19, tome);
        Rect(6, 16, 9, 18, page);
        Rect(10, 16, 13, 18, page);
        Set(9, 17, robeDark); Set(10, 17, robeDark); // spine
        return BakeStrip(px, w, h);
    }

    /// <summary>Hub chest sprites: closed (latched lid) and opened (lid thrown back,
    /// a gold glint inside). Cached as a 2-frame pair.</summary>
    public static Texture2D GetChest(bool opened)
    {
        if (_device == null) return null;
        string key = "chest";
        if (!_cache.TryGetValue(key, out var frames))
        {
            frames = new[] { DrawChest(false), DrawChest(true) };
            _cache[key] = frames;
        }
        return frames[opened ? 1 : 0];
    }

    private static Texture2D DrawChest(bool opened)
    {
        const int w = 22, h = 18;
        var px = new Color[w * h];
        void Set(int x, int y, Color c) { if (x >= 0 && x < w && y >= 0 && y < h) px[y * w + x] = c; }
        void Rect(int x0, int y0, int x1, int y1, Color c)
        { for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) Set(x, y, c); }

        var wood = new Color(122, 86, 52);
        var woodDark = new Color(88, 62, 38);
        var band = new Color(70, 68, 74);
        var gold = new Color(240, 200, 90);

        // Body box.
        Rect(2, 8, 19, 16, wood);
        Rect(2, 8, 19, 8, woodDark);
        Rect(2, 16, 19, 16, woodDark);
        Rect(2, 8, 2, 16, woodDark);
        Rect(19, 8, 19, 16, woodDark);
        Rect(9, 8, 12, 16, band);              // center strap
        if (opened)
        {
            // Lid thrown back behind the box; glint of loot inside.
            Rect(3, 1, 18, 4, woodDark);
            Rect(3, 1, 18, 1, band);
            Rect(4, 6, 17, 7, gold);
            Set(7, 5, gold); Set(13, 5, gold);
        }
        else
        {
            // Domed lid + latch.
            Rect(2, 4, 19, 7, wood);
            Rect(3, 3, 18, 3, woodDark);
            Rect(2, 7, 19, 7, woodDark);
            Rect(9, 4, 12, 7, band);
            Set(10, 7, gold); Set(11, 7, gold); // latch glint
        }
        return BakeStrip(px, w, h);
    }

    /// <summary>The sanctum's flask-refill fountain: a stone basin with a spout column
    /// and glinting water.</summary>
    public static Texture2D GetFountain()
    {
        if (_device == null) return null;
        string key = "fountain";
        if (!_cache.TryGetValue(key, out var frames))
        {
            frames = new[] { DrawFountain() };
            _cache[key] = frames;
        }
        return frames[0];
    }

    private static Texture2D DrawFountain()
    {
        const int w = 26, h = 26;
        var px = new Color[w * h];
        void Set(int x, int y, Color c) { if (x >= 0 && x < w && y >= 0 && y < h) px[y * w + x] = c; }
        void Rect(int x0, int y0, int x1, int y1, Color c)
        { for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) Set(x, y, c); }

        var stone = new Color(148, 146, 140);
        var stoneDark = new Color(104, 102, 98);
        var stoneLight = new Color(184, 182, 174);
        var water = new Color(80, 140, 220);
        var waterLight = new Color(150, 200, 255);

        // Basin: a wide stone bowl.
        Rect(2, 16, 23, 23, stone);
        Rect(2, 16, 23, 16, stoneLight);
        Rect(2, 22, 23, 23, stoneDark);
        Rect(2, 16, 3, 23, stoneDark);
        Rect(22, 16, 23, 23, stoneDark);
        // Water surface inside the basin.
        Rect(5, 17, 20, 19, water);
        Set(7, 17, waterLight); Set(12, 18, waterLight); Set(17, 17, waterLight);

        // Center column + top cup.
        Rect(11, 6, 14, 16, stone);
        Rect(11, 6, 11, 16, stoneDark);
        Rect(9, 4, 16, 6, stone);
        Rect(9, 4, 16, 4, stoneLight);
        // Falling water threads either side of the column.
        for (int y = 7; y <= 16; y++)
        {
            Set(9, y, (y & 1) == 0 ? waterLight : water);
            Set(16, y, (y & 1) == 1 ? waterLight : water);
        }
        Set(12, 3, waterLight); Set(13, 2, waterLight); // spray at the top

        return BakeStrip(px, w, h);
    }

    /// <summary>Defense structures by wire kind (0 crossbow turret, 1 spiked barrier,
    /// 2 flame turret, 3 wagon, 4 workbench). Cached; same clean pixel look as props.</summary>
    public static Texture2D GetStructureSprite(byte kind)
    {
        if (_device == null) return null;
        string key = "structure_" + kind;
        if (!_cache.TryGetValue(key, out var frames))
        {
            frames = new[]
            {
                kind switch
                {
                    0 => DrawCrossbowTurret(false),
                    1 => DrawSpikedBarrier(),
                    2 => DrawCrossbowTurret(true),
                    3 => DrawWagon(),
                    4 => DrawWorkbench(),
                    _ => DrawSpikedBarrier(),
                },
            };
            _cache[key] = frames;
        }
        return frames[0];
    }

    /// <summary>The defense arena's enemy portal: a dark standing arch with an eerie glow.</summary>
    public static Texture2D GetPortalSprite()
    {
        if (_device == null) return null;
        string key = "defense_portal";
        if (!_cache.TryGetValue(key, out var frames))
        {
            frames = new[] { DrawPortal() };
            _cache[key] = frames;
        }
        return frames[0];
    }

    private static Texture2D DrawWagon()
    {
        const int w = 42, h = 32;
        var px = new Color[w * h];
        void Set(int x, int y, Color c) { if (x >= 0 && x < w && y >= 0 && y < h) px[y * w + x] = c; }
        void Rect(int x0, int y0, int x1, int y1, Color c)
        { for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) Set(x, y, c); }

        var wood = new Color(126, 90, 54);
        var woodDark = new Color(92, 64, 40);
        var woodLight = new Color(154, 116, 74);
        var canvas = new Color(214, 204, 178);
        var canvasShade = new Color(178, 168, 144);
        var iron = new Color(74, 72, 78);

        // Canvas bonnet: a rounded hood over the bed.
        for (int x = 5; x <= 36; x++)
        {
            int rise = (int)(5.5f * MathF.Sin((x - 5) / 31f * MathF.PI));
            Rect(x, 9 - rise, x, 18, (x % 6) < 2 ? canvasShade : canvas);
        }
        Rect(5, 17, 36, 18, canvasShade); // hem line
        // Bed planks.
        Rect(3, 19, 38, 23, wood);
        Rect(3, 19, 38, 19, woodLight);
        Rect(3, 23, 38, 23, woodDark);
        for (int x = 7; x <= 36; x += 6) Rect(x, 19, x, 23, woodDark); // plank seams
        // Undercarriage + wheels.
        Rect(6, 24, 35, 25, woodDark);
        foreach (int cx in new[] { 11, 30 })
        {
            for (int y = -5; y <= 5; y++)
                for (int x = -5; x <= 5; x++)
                {
                    float d = MathF.Sqrt(x * x + y * y);
                    if (d is > 3.4f and <= 5.2f) Set(cx + x, 26 + y, iron);
                    else if (d <= 1.2f) Set(cx + x, 26 + y, woodDark);
                    else if (d <= 3.4f && (Math.Abs(x) <= 1 || Math.Abs(y) <= 1 || Math.Abs(x - y) <= 1 || Math.Abs(x + y) <= 1))
                        Set(cx + x, 26 + y, woodLight); // spokes
                }
        }
        // Tow bar poking out front.
        Rect(38, 21, 41, 22, woodDark);
        return BakeStrip(px, w, h);
    }

    private static Texture2D DrawCrossbowTurret(bool flame)
    {
        const int w = 24, h = 26;
        var px = new Color[w * h];
        void Set(int x, int y, Color c) { if (x >= 0 && x < w && y >= 0 && y < h) px[y * w + x] = c; }
        void Rect(int x0, int y0, int x1, int y1, Color c)
        { for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) Set(x, y, c); }

        var wood = new Color(122, 86, 52);
        var woodDark = new Color(88, 62, 38);
        var iron = new Color(96, 94, 102);
        var ironDark = new Color(64, 62, 70);
        var string_ = new Color(214, 204, 178);
        var brass = new Color(196, 150, 70);
        var flameC = new Color(235, 130, 40);

        // Tripod legs.
        for (int i = 0; i < 7; i++)
        {
            Set(8 - i / 2, 18 + i, woodDark);
            Set(15 + i / 2, 18 + i, woodDark);
            Set(11, 18 + i, wood); Set(12, 18 + i, wood);
        }
        // Mount block.
        Rect(8, 14, 15, 18, wood);
        Rect(8, 18, 15, 18, woodDark);
        if (flame)
        {
            // Flamethrower: a brass tank with a stubby nozzle and pilot flame.
            Rect(7, 7, 16, 13, brass);
            Rect(7, 7, 16, 7, new Color(226, 184, 104));
            Rect(7, 13, 16, 13, new Color(150, 110, 50));
            Rect(17, 9, 21, 11, ironDark);          // nozzle
            Set(22, 9, flameC); Set(22, 10, new Color(250, 200, 80)); Set(22, 11, flameC);
            Rect(10, 4, 13, 6, ironDark);            // filler cap
        }
        else
        {
            // Crossbow: stock + iron bow arms + drawn string, aimed right.
            Rect(4, 10, 19, 12, woodDark);           // stock
            Rect(4, 10, 19, 10, wood);
            Rect(17, 5, 18, 16, iron);               // bow riser
            for (int i = 0; i < 5; i++)
            {
                Set(19 + i / 2, 5 - i / 3, ironDark); // upper limb
                Set(19 + i / 2, 16 + i / 3, ironDark); // lower limb
            }
            for (int y = 5; y <= 16; y++) Set(16, y, string_); // string
            Rect(18, 11, 22, 11, new Color(214, 204, 178));    // loaded bolt
        }
        return BakeStrip(px, w, h);
    }

    private static Texture2D DrawSpikedBarrier()
    {
        // An ISO wall segment: the fence runs along the world +X axis (screen
        // down-right at the 2:1 tile slope), spanning one tile — the renderer
        // mirrors it for +Y-axis walls, and adjacent tiles chain into a line.
        const int w = 40, h = 30;
        var px = new Color[w * h];
        void Set(int x, int y, Color c) { if (x >= 0 && x < w && y >= 0 && y < h) px[y * w + x] = c; }
        void Rect(int x0, int y0, int x1, int y1, Color c)
        { for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) Set(x, y, c); }

        var wood = new Color(116, 84, 52);
        var woodDark = new Color(84, 60, 38);
        var woodLight = new Color(146, 110, 70);
        var point = new Color(200, 190, 168);

        // The 2:1 baseline the fence stands on, plus its ground shadow.
        for (int i = 0; i <= 34; i++)
        {
            int x = 2 + i, y = 12 + i / 2;
            Set(x, y + 1, woodDark);
            Set(x, y + 2, new Color(40, 34, 26));
        }
        // Two rails riding the same slope.
        for (int i = 0; i <= 34; i++)
        {
            int x = 2 + i, y = 12 + i / 2;
            Set(x, y - 3, wood);
            Set(x, y - 4, woodLight);
            Set(x, y - 7, wood);
        }
        // Posts every ~third of the tile, each crowned with a sharpened tip.
        foreach (int i in new[] { 1, 12, 23, 33 })
        {
            int x = 2 + i, y = 12 + i / 2;
            Rect(x, y - 9, x + 1, y + 1, woodDark);
            Set(x, y - 10, point);
            Set(x + 1, y - 10, point);
            Set(x, y - 11, point);
        }
        // Angled stakes leaning out between the posts.
        foreach (int i in new[] { 6, 17, 28 })
        {
            int x = 2 + i, y = 12 + i / 2;
            for (int k = 0; k < 6; k++)
                Set(x + k / 2, y - 2 - k, k >= 4 ? point : wood);
            for (int k = 0; k < 4; k++)
                Set(x - k / 2, y - 2 - k, k >= 2 ? woodDark : wood);
        }
        return BakeStrip(px, w, h);
    }

    private static Texture2D DrawWorkbench()
    {
        const int w = 26, h = 20;
        var px = new Color[w * h];
        void Set(int x, int y, Color c) { if (x >= 0 && x < w && y >= 0 && y < h) px[y * w + x] = c; }
        void Rect(int x0, int y0, int x1, int y1, Color c)
        { for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) Set(x, y, c); }

        var wood = new Color(126, 92, 56);
        var woodDark = new Color(90, 66, 42);
        var woodLight = new Color(156, 118, 76);
        var iron = new Color(96, 94, 102);
        var paper = new Color(224, 214, 186);

        // Table top + legs.
        Rect(1, 8, 24, 11, wood);
        Rect(1, 8, 24, 8, woodLight);
        Rect(1, 11, 24, 11, woodDark);
        Rect(2, 12, 4, 19, woodDark);
        Rect(21, 12, 23, 19, woodDark);
        // Tools on the top: hammer, plans, a spare bolt bundle.
        Rect(4, 5, 5, 8, woodDark);              // hammer handle
        Rect(3, 4, 7, 5, iron);                  // hammer head
        Rect(10, 5, 16, 7, paper);               // pinned plans
        Set(11, 6, woodDark); Set(13, 6, woodDark); Set(15, 6, woodDark);
        Rect(19, 5, 22, 6, new Color(196, 186, 164)); // bolt bundle
        Rect(19, 7, 22, 7, iron);
        return BakeStrip(px, w, h);
    }

    private static Texture2D DrawPortal()
    {
        const int w = 26, h = 30;
        var px = new Color[w * h];
        void Set(int x, int y, Color c) { if (x >= 0 && x < w && y >= 0 && y < h) px[y * w + x] = c; }

        var stone = new Color(96, 88, 104);
        var stoneDark = new Color(64, 58, 72);
        var glow = new Color(120, 70, 160);
        var glowBright = new Color(178, 110, 220);

        // Standing arch of rough stones with a dim violet void inside.
        for (int y = 2; y < 28; y++)
            for (int x = 2; x < 24; x++)
            {
                float nx = (x - 12.5f) / 9.5f, ny = (y - 16f) / 13f;
                float d = nx * nx + ny * ny;
                bool arch = y >= 6 || d <= 1f;
                if (!arch) continue;
                if (d is > 0.62f and <= 1.0f)
                    Set(x, y, ((x * 7 + y * 13) % 5) < 2 ? stoneDark : stone);
                else if (d <= 0.62f && y > 4)
                {
                    int hash = (x * 31 + y * 17) % 11;
                    Set(x, y, hash < 2 ? glowBright : hash < 6 ? glow : new Color(30, 20, 44));
                }
            }
        return BakeStrip(px, w, h);
    }

    /// <summary>Inventory icons for Curio items: the mercenary contract (a sealed
    /// letter) and the flamethrower blueprint (a blue schematic sheet). Null for
    /// anything that isn't a curio.</summary>
    public static Texture2D GetCurioSprite(Items.ItemBase itemBase)
    {
        if (_device == null || itemBase is not { Category: Items.ItemCategory.Curio }) return null;
        string key = "curio:" + itemBase.Id;
        if (!_cache.TryGetValue(key, out var frames))
        {
            frames = new[]
            {
                itemBase.Id == "flamethrower_blueprint" ? DrawBlueprint() : DrawContract(),
            };
            _cache[key] = frames;
        }
        return frames[0];
    }

    private static Texture2D DrawContract()
    {
        const int w = 16, h = 14;
        var px = new Color[w * h];
        void Set(int x, int y, Color c) { if (x >= 0 && x < w && y >= 0 && y < h) px[y * w + x] = c; }
        void Rect(int x0, int y0, int x1, int y1, Color c)
        { for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) Set(x, y, c); }

        var paper = new Color(226, 214, 182);
        var paperShade = new Color(192, 180, 150);
        var ink = new Color(80, 70, 58);
        var wax = new Color(168, 44, 40);

        // Folded letter with a shadowed flap and script lines.
        Rect(1, 2, 14, 11, paper);
        Rect(1, 2, 14, 2, paperShade);
        Rect(1, 11, 14, 11, paperShade);
        for (int x = 1; x <= 14; x++) Set(x, 2 + (x < 8 ? x / 2 : (15 - x) / 2), paperShade); // flap crease
        Rect(3, 7, 12, 7, ink);
        Rect(3, 9, 10, 9, ink);
        // Wax seal.
        Rect(11, 4, 13, 6, wax);
        Set(12, 5, new Color(210, 90, 80));
        return BakeStrip(px, w, h);
    }

    private static Texture2D DrawBlueprint()
    {
        const int w = 20, h = 14;
        var px = new Color[w * h];
        void Set(int x, int y, Color c) { if (x >= 0 && x < w && y >= 0 && y < h) px[y * w + x] = c; }
        void Rect(int x0, int y0, int x1, int y1, Color c)
        { for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) Set(x, y, c); }

        var blue = new Color(46, 78, 130);
        var blueDark = new Color(32, 56, 96);
        var line = new Color(180, 208, 240);
        var scorch = new Color(70, 52, 36);

        // The unrolled sheet, edges curling.
        Rect(1, 1, 18, 12, blue);
        Rect(1, 1, 18, 1, blueDark);
        Rect(1, 12, 18, 12, blueDark);
        Rect(1, 1, 1, 12, blueDark);
        Rect(18, 1, 18, 12, blueDark);
        // The fire-spitter drawn in white line-work: tank, nozzle, flame ticks.
        Rect(4, 5, 8, 9, line);
        Rect(5, 6, 7, 8, blue);       // hollow tank
        Rect(9, 6, 13, 7, line);      // nozzle
        Set(14, 5, line); Set(15, 6, line); Set(14, 7, line); // flame ticks
        Rect(4, 3, 6, 3, line);       // filler cap sketch
        // Dimension marks + a scorched corner.
        Rect(4, 11, 12, 11, line);
        Set(17, 2, scorch); Set(16, 1, scorch); Set(17, 1, scorch);
        return BakeStrip(px, w, h);
    }

    /// <summary>Inventory icon for a flask base: a corked bottle filled with the
    /// potion's liquid (red for health, blue for mana).</summary>
    public static Texture2D GetFlaskSprite(Items.ItemBase itemBase)
    {
        if (_device == null || itemBase is not { Category: Items.ItemCategory.Flask }) return null;
        string key = "flask:" + itemBase.Id;
        if (_cache.TryGetValue(key, out var cached)) return cached[0];
        var tex = DrawFlaskItem(itemBase.FlaskHeal > 0
            ? new Color(198, 52, 52) : new Color(66, 108, 226));
        _cache[key] = new[] { tex };
        return tex;
    }

    private static Texture2D DrawFlaskItem(Color liquid)
    {
        const int w = 14, h = 20;
        var px = new Color[w * h];
        void Set(int x, int y, Color c) { if (x >= 0 && x < w && y >= 0 && y < h) px[y * w + x] = c; }
        void Rect(int x0, int y0, int x1, int y1, Color c)
        { for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) Set(x, y, c); }

        var glass = new Color(190, 205, 215, 90);
        var glassEdge = new Color(210, 220, 230);
        var cork = new Color(150, 112, 66);
        var liquidLight = Shade(liquid, 1.4f);

        // Bulb body.
        Rect(3, 8, 10, 17, glass);
        Rect(2, 10, 11, 15, glass);
        // Liquid fill (lower two thirds of the bulb).
        Rect(3, 11, 10, 16, liquid);
        Rect(2, 12, 11, 15, liquid);
        Set(4, 11, liquidLight); Set(8, 12, liquidLight);
        // Neck + cork.
        Rect(5, 3, 8, 8, glass);
        Rect(5, 1, 8, 3, cork);
        // Glass edge highlights.
        Set(2, 10, glassEdge); Set(11, 10, glassEdge);
        Set(3, 8, glassEdge); Set(10, 8, glassEdge);
        Set(3, 17, glassEdge); Set(10, 17, glassEdge);

        return BakeStrip(px, w, h);
    }

    /// <summary>Everything that shapes one player's baked body sprite: the creation
    /// choices plus what the visible armor slots hold. Body armor and helmets paint
    /// real silhouette layers onto the rig; gloves/boots/belt recolor rig pixels.</summary>
    public struct PlayerLook
    {
        public byte BodyStyle, HairStyle;
        public Color Skin, Hair;
        public byte ArmorStyle;              // 0 none, 1 cloth, 2 leather, 3 mail, 4 plate
        public Color ArmorColor;
        public byte HelmetStyle;             // 0 none, 1 hood, 2 cowl, 3 cap, 4 helm
        public Color HelmetColor;
        public Color? Gloves, Boots, Belt;   // tints; null = bare skin / default rig colors

        public static byte ArmorStyleId(string s) => s switch
        {
            "cloth" => 1, "leather" => 2, "mail" => 3, "plate" => 4, _ => 0,
        };

        public static byte HelmetStyleId(string s) => s switch
        {
            "hood" => 1, "cowl" => 2, "cap" => 3, "helm" => 4, _ => 0,
        };

        public string Key() =>
            $"{BodyStyle}:{HairStyle}:{Skin.PackedValue:x8}:{Hair.PackedValue:x8}" +
            $":{ArmorStyle}:{ArmorColor.PackedValue:x8}:{HelmetStyle}:{HelmetColor.PackedValue:x8}" +
            $":{Gloves?.PackedValue ?? 0:x8}:{Boots?.PackedValue ?? 0:x8}:{Belt?.PackedValue ?? 0:x8}";
    }

    /// <summary>Facing directions for the baked body frames. West is the East strip
    /// mirrored at draw time — the rig itself only knows three views.</summary>
    public const int DirSouth = 0, DirNorth = 1, DirEast = 2;

    /// <summary>Build the full baked look straight from a CharacterData — creation
    /// choices plus worn armor. Used by menus (character select) that render a saved
    /// character without a connected session.</summary>
    public static PlayerLook LookForCharacter(Data.GameData data, Sim.CharacterData c)
    {
        var look = new PlayerLook
        {
            BodyStyle = c.BodyStyle,
            HairStyle = c.EffectiveHairStyle,
            Skin = c.EffectiveSkinColor,
            Hair = c.EffectiveHairColor,
        };
        Items.ItemBase BaseAt(Items.EquipSlot slot) =>
            c.Equipment.GetValueOrDefault(slot) is { } it ? data.Items.GetValueOrDefault(it.BaseItemId) : null;
        if (BaseAt(Items.EquipSlot.BodyArmor) is { } body)
        {
            look.ArmorStyle = PlayerLook.ArmorStyleId(body.ArmorStyle);
            look.ArmorColor = WorldRenderer.ParseColor(body.SpriteColor, new Color(120, 112, 100));
        }
        if (BaseAt(Items.EquipSlot.Helmet) is { } helm)
        {
            look.HelmetStyle = PlayerLook.HelmetStyleId(helm.ArmorStyle);
            look.HelmetColor = WorldRenderer.ParseColor(helm.SpriteColor, new Color(120, 112, 100));
        }
        if (BaseAt(Items.EquipSlot.Gloves) is { } glove)
            look.Gloves = WorldRenderer.ParseColor(glove.SpriteColor, new Color(120, 112, 100));
        if (BaseAt(Items.EquipSlot.Boots) is { } boot)
            look.Boots = WorldRenderer.ParseColor(boot.SpriteColor, new Color(70, 60, 50));
        if (BaseAt(Items.EquipSlot.Belt) is { } belt)
            look.Belt = WorldRenderer.ParseColor(belt.SpriteColor, new Color(100, 88, 60));
        return look;
    }

    /// <summary>Player body frames for one look, indexed [direction * 3 + frame]:
    /// directions South (front) / North (back) / East (side, mirror for West), frames
    /// [0] idle + [1]/[2] walk. ONE human rig — style only changes silhouette pixels,
    /// so every armor layer fits every body. Cached per exact look (colors are free
    /// 24-bit values, but a session only ever holds a handful of players).</summary>
    public static Texture2D[] GetPlayerFrames(in PlayerLook look)
    {
        if (_device == null) return null;
        string key = $"player:{look.Key()}";
        if (_cache.TryGetValue(key, out var cached)) return cached;
        var frames = CreatePlayerFrames(look);
        _cache[key] = frames;
        return frames;
    }

    /// <summary>UNCACHED body frames — the creation screen's live preview bakes these
    /// while the player drags the color sliders and disposes each superseded set, so
    /// slider scrubbing never floods the shared cache with one-frame colors.</summary>
    public static Texture2D[] CreatePlayerFrames(byte bodyStyle, byte hairStyle, Color skin, Color hair) =>
        CreatePlayerFrames(new PlayerLook
        { BodyStyle = bodyStyle, HairStyle = hairStyle, Skin = skin, Hair = hair });

    public static Texture2D[] CreatePlayerFrames(in PlayerLook look)
    {
        if (_device == null) return null;
        var frames = new Texture2D[9];
        for (int dir = 0; dir < 3; dir++)
            for (int f = 0; f < 3; f++)
                frames[dir * 3 + f] = dir == DirEast
                    ? DrawHumanBodySide(look, f)
                    : DrawHumanBody(look, dir, f);
        return frames;
    }

    /// <summary>The human rig, front (South) and back (North) views: 16x27, feet on the
    /// bottom row. Frame 0 stands; frames 1/2 alternate the stride. Style 0 = male
    /// (broad shoulders), 1 = female (tapered waist); hair style and both colors are
    /// independent of the body. Underclothes are a neutral tunic so an unarmored
    /// character still reads; worn armor paints over the same coordinates, which is why
    /// every layer fits every body.</summary>
    private static Texture2D DrawHumanBody(in PlayerLook look, int dir, int frame)
    {
        const int w = 16, h = 27;
        var px = new Color[w * h];
        void Set(int x, int y, Color c) { if (x >= 0 && x < w && y >= 0 && y < h) px[y * w + x] = c; }
        void Rect(int x0, int y0, int x1, int y1, Color c)
        { for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) Set(x, y, c); }

        var skin = look.Skin;
        var skinShade = Shade(skin, 0.78f);
        var hairDark = Shade(look.Hair, 0.7f);
        // The garment: bare tunic, or the body armor's color when one is worn.
        bool armored = look.ArmorStyle != 0;
        var garb = armored ? look.ArmorColor : new Color(104, 98, 88);
        var garbDark = Shade(garb, 0.72f);
        var garbLight = Shade(garb, 1.22f);
        var pants = new Color(70, 62, 56);
        var boots = look.Boots ?? new Color(52, 44, 38);
        var bootsDark = Shade(boots, 0.75f);
        var eyes = new Color(32, 28, 26);
        bool fem = look.BodyStyle == 1;
        bool robe = look.ArmorStyle == 1;      // cloth armor drapes over the legs
        bool back = dir == DirNorth;           // the view from behind: no face, more hair

        // Legs + stride: frame 1 leads left, frame 2 leads right.
        int lead = frame == 0 ? 0 : frame == 1 ? 1 : -1;
        Rect(6, 20, 7, 24 + Math.Min(0, lead), pants);        // left leg
        Rect(8, 20, 9, 24 - Math.Max(0, lead), pants);        // right leg
        Set(6 - Math.Max(0, lead), 25, boots); Set(7 - Math.Max(0, lead), 25, boots);
        Set(8 + Math.Max(0, -lead), 25, boots); Set(9 + Math.Max(0, -lead), 25, boots);
        Rect(6, 25, 7, 26, boots);
        Rect(8, 25, 9, 26, boots);
        Set(7, 26, bootsDark); Set(9, 26, bootsDark);         // sole shading

        // Torso: broad and straight for the male rig, tapered for the female.
        int shL = fem ? 5 : 4, shR = fem ? 10 : 11;
        Rect(shL, 11, shR, 16, garb);
        if (fem && !robe)
        {
            Rect(6, 17, 9, 19, garb);        // waist taper
            Set(5, 17, garbDark); Set(10, 17, garbDark);
        }
        else
            Rect(5, 17, 10, 19, garb);
        Rect(shL, 11, shR, 11, garbDark);     // shoulder seam

        // Body-armor silhouettes over the garment base.
        switch (look.ArmorStyle)
        {
            case 1: // cloth: a full robe — skirt over the legs down to the ankles.
                Rect(5, 20, 10, 24, garb);
                Set(5, 24, garbDark); Set(10, 24, garbDark);
                Set(7, 14, garbDark); Set(7, 18, garbDark); Set(7, 22, garbDark); // fold line
                Rect(5, 24, 10, 24, garbDark);                // hem
                break;
            case 2: // leather: stitched jerkin — seams and shoulder patches.
                Set(shL, 12, garbDark); Set(shR, 12, garbDark);
                Rect(shL + 1, 14, shR - 1, 14, garbDark);     // chest strap
                Set(shL + 1, 11, garbLight); Set(shR - 1, 11, garbLight);
                break;
            case 3: // mail: alternating rings catch the light.
                for (int my = 12; my <= 17; my++)
                    for (int mx = shL; mx <= shR; mx++)
                        if (((mx + my) & 1) == 0 && px[my * w + mx] == garb)
                            Set(mx, my, garbLight);
                break;
            case 4: // plate: pauldrons past the shoulders, bright chest ridge.
                Set(shL - 1, 11, garb); Set(shR + 1, 11, garb);
                Set(shL - 1, 12, garbDark); Set(shR + 1, 12, garbDark);
                Rect(shL + 1, 12, shL + 1, 17, garbLight);    // ridge highlight
                Rect(shL, 15, shR, 15, garbDark);             // breastplate seam
                break;
        }

        // Belt line (hidden under a robe's drape).
        if (!robe)
            Rect(6, 19, 9, 19, look.Belt ?? Shade(pants, 0.8f));

        // Arms: sleeves at the sides with hands; a light swing with the stride.
        int armSwing = lead;
        var hand = look.Gloves ?? skin;
        Rect(shL - 1, 12, shL - 1, 16 + armSwing, garbDark);
        Set(shL - 1, 17 + armSwing, hand);
        Rect(shR + 1, 12, shR + 1, 16 - armSwing, garbDark);
        Set(shR + 1, 17 - armSwing, hand);

        // Head: the face only exists on the front view.
        Rect(5, 3, 10, 9, skin);
        Rect(5, 9, 10, 9, skinShade);         // jaw / nape shade
        if (!back) { Set(6, 6, eyes); Set(9, 6, eyes); }
        Rect(6, 10, 9, 10, skinShade);        // neck — fills the head/torso seam row

        // Hair by style (independent of body) — hidden entirely under any helmet.
        if (look.HelmetStyle == 0)
        {
            if (look.HairStyle != Sim.Appearance.HairBald)
            {
                Rect(4, 1, 11, 2, look.Hair);
                Rect(4, 3, 4, 4, look.Hair); Rect(11, 3, 11, 4, look.Hair);
                Set(5, 1, hairDark); Set(10, 1, hairDark);
                if (back) Rect(5, 3, 10, 6, look.Hair);       // the back of the head is hair
            }
            else
            {
                Rect(5, 3, 10, 3, skinShade); // bald crown catches the light
            }
            if (look.HairStyle == Sim.Appearance.HairLong)
            {
                Rect(4, 3, 4, 12, look.Hair); // side falls to the shoulders
                Rect(11, 3, 11, 12, look.Hair);
                Set(4, 12, hairDark); Set(11, 12, hairDark);
                if (back) Rect(5, 3, 10, 9, look.Hair);       // full curtain from behind
            }
            else if (look.HairStyle == Sim.Appearance.HairBun)
            {
                Rect(6, 0, 9, 0, look.Hair);  // topknot above the crop
                Set(6, 0, hairDark); Set(9, 0, hairDark);
            }
        }
        else
        {
            var met = look.HelmetColor;
            var metDark = Shade(met, 0.7f);
            var metLight = Shade(met, 1.25f);
            switch (look.HelmetStyle)
            {
                case 1: // hood: soft drape around the face; closed from behind.
                    if (back) Rect(5, 3, 10, 9, met);
                    Rect(4, 1, 11, 2, met);
                    Rect(4, 3, 4, 10, met); Rect(11, 3, 11, 10, met);
                    Set(5, 2, metDark); Set(10, 2, metDark);
                    Set(4, 10, metDark); Set(11, 10, metDark);
                    if (back) Rect(7, 4, 8, 9, metDark);      // drape fold
                    break;
                case 2: // cowl: the hood with a peaked tip.
                    if (back) Rect(5, 3, 10, 9, met);
                    Rect(4, 1, 11, 2, met);
                    Rect(4, 3, 4, 10, met); Rect(11, 3, 11, 10, met);
                    Rect(6, 0, 9, 0, met); Set(7, 0, metLight);
                    Set(4, 10, metDark); Set(11, 10, metDark);
                    if (back) Rect(7, 4, 8, 9, metDark);      // drape fold
                    break;
                case 3: // cap: a metal dome with cheek guards; same from behind.
                    Rect(4, 1, 11, 3, met);
                    Rect(4, 1, 11, 1, metLight);
                    Rect(4, 4, 4, 5, met); Rect(11, 4, 11, 5, met);
                    Rect(4, 3, 11, 3, metDark);               // rim
                    break;
                case 4: // helm: full faceplate with a dark eye slit (plain from behind).
                    Rect(4, 1, 11, 9, met);
                    Rect(4, 1, 11, 1, metLight);
                    if (!back)
                    {
                        Rect(5, 6, 10, 6, new Color(20, 18, 16)); // eye slit
                        Rect(7, 7, 8, 9, metDark);                // breath ridge
                    }
                    else
                        Rect(7, 2, 8, 8, metDark);                // back seam
                    Set(4, 9, metDark); Set(11, 9, metDark);
                    break;
            }
        }

        return BakeStrip(px, w, h);
    }

    /// <summary>The side (East) view of the rig — mirrored at draw time for West. A
    /// slimmer profile: one visible arm, legs that scissor front/back with the stride,
    /// the face edge toward +x. Armor paints the same styles onto the narrower body.</summary>
    private static Texture2D DrawHumanBodySide(in PlayerLook look, int frame)
    {
        const int w = 16, h = 27;
        var px = new Color[w * h];
        void Set(int x, int y, Color c) { if (x >= 0 && x < w && y >= 0 && y < h) px[y * w + x] = c; }
        void Rect(int x0, int y0, int x1, int y1, Color c)
        { for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) Set(x, y, c); }

        var skin = look.Skin;
        var skinShade = Shade(skin, 0.78f);
        var hairDark = Shade(look.Hair, 0.7f);
        bool armored = look.ArmorStyle != 0;
        var garb = armored ? look.ArmorColor : new Color(104, 98, 88);
        var garbDark = Shade(garb, 0.72f);
        var garbLight = Shade(garb, 1.22f);
        var pants = new Color(70, 62, 56);
        var pantsDark = Shade(pants, 0.75f);
        var boots = look.Boots ?? new Color(52, 44, 38);
        var eyes = new Color(32, 28, 26);
        bool robe = look.ArmorStyle == 1;

        // Legs scissor along the walk direction (+x = forward); each boot grows a
        // 1px toe toward wherever that leg is stepping.
        int lead = frame == 0 ? 0 : frame == 1 ? 1 : -1;
        Rect(7 - lead, 20, 8 - lead, 24, pantsDark);          // far leg
        Rect(7 - lead - (lead > 0 ? 1 : 0), 25, 8 - lead, 26, Shade(boots, 0.8f));
        Rect(7 + lead, 20, 8 + lead, 24, pants);              // near leg
        Rect(7 + lead, 25, 8 + lead + (lead > 0 ? 1 : 0), 26, boots);
        if (lead == 0) Rect(7, 25, 9, 26, boots);             // standing: one merged foot

        // Torso: a 5-wide profile column.
        Rect(6, 11, 10, 16, garb);
        Rect(6, 17, 9, 19, garb);
        Rect(6, 11, 10, 11, garbDark);        // shoulder seam

        switch (look.ArmorStyle)
        {
            case 1: // robe skirt falls over the legs.
                Rect(6, 20, 10, 24, garb);
                Rect(6, 24, 10, 24, garbDark);                // hem
                Set(8, 14, garbDark); Set(8, 18, garbDark); Set(8, 22, garbDark);
                break;
            case 2: // leather: chest strap.
                Rect(7, 14, 9, 14, garbDark);
                Set(7, 11, garbLight);
                break;
            case 3: // mail rings.
                for (int my = 12; my <= 17; my++)
                    for (int mx = 6; mx <= 10; mx++)
                        if (((mx + my) & 1) == 0 && px[my * w + mx] == garb)
                            Set(mx, my, garbLight);
                break;
            case 4: // plate: pauldron hump + ridge.
                Set(5, 11, garb); Set(5, 12, garbDark);
                Rect(7, 12, 7, 17, garbLight);
                Rect(6, 15, 10, 15, garbDark);
                break;
        }

        if (!robe)
            Rect(6, 19, 9, 19, look.Belt ?? Shade(pants, 0.8f));

        // The near arm swings over the torso; the far arm hides behind it.
        int armSwing = lead;
        var hand = look.Gloves ?? skin;
        Rect(8, 12, 8, 16 + armSwing, garbDark);
        Set(8, 17 + armSwing, hand);

        // Head in profile: face edge toward +x, eye near the front.
        Rect(5, 3, 10, 9, skin);
        Rect(5, 9, 10, 9, skinShade);
        Set(9, 6, eyes);
        Set(10, 7, skinShade);                // nose hint
        Rect(6, 10, 9, 10, skinShade);        // neck

        if (look.HelmetStyle == 0)
        {
            if (look.HairStyle != Sim.Appearance.HairBald)
            {
                Rect(4, 1, 10, 2, look.Hair);                 // crop, hugging the crown
                Rect(4, 3, 5, 5, look.Hair);                  // back of the head
                Set(4, 1, hairDark); Set(10, 2, hairDark);
            }
            else
                Rect(5, 3, 10, 3, skinShade);
            if (look.HairStyle == Sim.Appearance.HairLong)
            {
                Rect(4, 3, 5, 13, look.Hair);                 // fall down the back
                Set(4, 13, hairDark); Set(5, 13, hairDark);
            }
            else if (look.HairStyle == Sim.Appearance.HairBun)
            {
                Rect(4, 0, 6, 1, look.Hair);                  // knot at the back crown
                Set(4, 1, hairDark);
            }
        }
        else
        {
            var met = look.HelmetColor;
            var metDark = Shade(met, 0.7f);
            var metLight = Shade(met, 1.25f);
            switch (look.HelmetStyle)
            {
                case 1: // hood in profile: drape down the back of the head.
                    Rect(4, 1, 10, 2, met);
                    Rect(4, 3, 5, 10, met);
                    Set(4, 10, metDark); Set(10, 2, metDark);
                    break;
                case 2: // cowl: hood + peak trailing back.
                    Rect(4, 1, 10, 2, met);
                    Rect(4, 3, 5, 10, met);
                    Rect(4, 0, 7, 0, met); Set(4, 0, metLight);
                    Set(4, 10, metDark);
                    break;
                case 3: // cap dome + cheek guard on the visible side.
                    Rect(4, 1, 10, 3, met);
                    Rect(4, 1, 10, 1, metLight);
                    Rect(9, 4, 9, 5, met);
                    Rect(4, 3, 10, 3, metDark);
                    break;
                case 4: // helm in profile: slit only at the face edge.
                    Rect(4, 1, 10, 9, met);
                    Rect(4, 1, 10, 1, metLight);
                    Rect(8, 6, 10, 6, new Color(20, 18, 16));
                    Rect(5, 2, 5, 8, metDark);                // back seam
                    Set(4, 9, metDark); Set(10, 9, metDark);
                    break;
            }
        }

        return BakeStrip(px, w, h);
    }

    /// <summary>The Grave Caller: a hunched robed conjurer — deep hood with glowing
    /// eyes, tint-colored robe, a crooked staff topped with a skull that pulses
    /// between the two frames (its "chant").</summary>
    private static Texture2D DrawNecromancer(Color robe, int frame)
    {
        const int w = 20, h = 28;
        var px = new Color[w * h];
        void Set(int x, int y, Color c) { if (x >= 0 && x < w && y >= 0 && y < h) px[y * w + x] = c; }
        void Rect(int x0, int y0, int x1, int y1, Color c)
        { for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) Set(x, y, c); }

        var robeDark = Shade(robe, 0.62f);
        var hood = Shade(robe, 0.5f);
        var eyes = frame == 0 ? new Color(140, 255, 150) : new Color(190, 255, 200);
        var wood = new Color(96, 76, 52);
        var bone = new Color(226, 222, 206);
        var glow = frame == 0 ? new Color(120, 90, 200) : new Color(170, 130, 255);

        // Robe: widens to the hem, swaying slightly between frames.
        int sway = frame == 0 ? 0 : 1;
        for (int y = 10; y <= 26; y++)
        {
            int half = 3 + (y - 10) * 3 / 16;
            Rect(8 - half + sway, y, 9 + half + sway, y, robe);
            Set(8 - half + sway, y, robeDark);
            Set(9 + half + sway, y, robeDark);
        }
        Rect(6 + sway, 26, 11 + sway, 26, robeDark); // ragged hem
        // Hood: a deep cowl with only the eyes inside.
        Rect(5 + sway, 4, 12 + sway, 10, hood);
        Rect(6 + sway, 3, 11 + sway, 3, hood);
        Rect(7 + sway, 6, 10 + sway, 8, new Color(16, 12, 20)); // hollow face
        Set(7 + sway, 7, eyes); Set(10 + sway, 7, eyes);
        // Crooked staff on the right, skull on top.
        Rect(15, 8, 15, 25, wood);
        Set(14, 12, wood); Set(16, 18, wood);
        Rect(14, 4, 16, 6, bone);
        Set(14, 5, new Color(30, 26, 34)); Set(16, 5, new Color(30, 26, 34)); // sockets
        Set(15, 3, glow); Set(14, 2, glow); Set(16, 2, glow);                 // chant glow
        return BakeStrip(px, w, h);
    }

    /// <summary>The hub's stash: an iron-banded storage chest with a purple-gem lock —
    /// visually distinct from the loot chests so "my storage" reads at a glance.</summary>
    public static Texture2D GetStash()
    {
        if (_device == null) return null;
        if (!_cache.TryGetValue("stash", out var frames))
        {
            frames = new[] { DrawStashChest() };
            _cache["stash"] = frames;
        }
        return frames[0];
    }

    private static Texture2D DrawStashChest()
    {
        const int w = 24, h = 20;
        var px = new Color[w * h];
        void Set(int x, int y, Color c) { if (x >= 0 && x < w && y >= 0 && y < h) px[y * w + x] = c; }
        void Rect(int x0, int y0, int x1, int y1, Color c)
        { for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) Set(x, y, c); }

        var wood = new Color(94, 74, 108);       // purple-stained wood
        var woodDark = new Color(66, 52, 78);
        var band = new Color(148, 148, 158);
        var bandDark = new Color(96, 96, 106);
        var gem = new Color(190, 120, 255);

        // Tall body with a domed lid.
        Rect(1, 7, 22, 18, wood);
        Rect(1, 7, 22, 7, woodDark);
        Rect(1, 18, 22, 18, woodDark);
        Rect(1, 7, 1, 18, woodDark);
        Rect(22, 7, 22, 18, woodDark);
        Rect(1, 3, 22, 6, wood);
        Rect(2, 2, 21, 2, woodDark);
        // Iron bands: two verticals plus the lid rim.
        Rect(5, 2, 7, 18, band); Rect(5, 2, 5, 18, bandDark);
        Rect(16, 2, 18, 18, band); Rect(16, 2, 16, 18, bandDark);
        Rect(1, 6, 22, 6, bandDark);
        // Lock plate with the gem.
        Rect(10, 9, 13, 13, band);
        Set(11, 10, gem); Set(12, 10, gem); Set(11, 11, gem); Set(12, 11, gem);
        Set(11, 12, new Color(120, 70, 170));
        return BakeStrip(px, w, h);
    }

    /// <summary>Inventory/held sprite for a quiver: a leather tube with fletched arrows.</summary>
    public static Texture2D GetQuiverSprite(Items.ItemBase itemBase)
    {
        if (_device == null || itemBase is not { Category: Items.ItemCategory.Quiver }) return null;
        string key = "quiver:" + itemBase.Id;
        if (_cache.TryGetValue(key, out var cached)) return cached[0];
        var leather = WorldRenderer.ParseColor(itemBase.SpriteColor, new Color(138, 106, 66));
        const int w = 12, h = 22;
        var px = new Color[w * h];
        void Set(int x, int y, Color c) { if (x >= 0 && x < w && y >= 0 && y < h) px[y * w + x] = c; }
        void Rect(int x0, int y0, int x1, int y1, Color c)
        { for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) Set(x, y, c); }
        var dark = Shade(leather, 0.65f);
        var shaft = new Color(150, 120, 78);
        var fletch = new Color(214, 214, 220);
        // Tube.
        Rect(2, 7, 9, 20, leather);
        Rect(2, 7, 2, 20, dark);
        Rect(9, 7, 9, 20, dark);
        Rect(2, 20, 9, 20, dark);
        Rect(2, 12, 9, 12, dark); // strap band
        // Three arrows poking out.
        foreach (int ax in new[] { 3, 5, 7 })
        {
            Rect(ax, 2, ax, 7, shaft);
            Set(ax - 1, 2, fletch); Set(ax, 1, fletch); Set(ax + 1, 2, fletch);
        }
        var tex = BakeStrip(px, w, h);
        _cache[key] = new[] { tex };
        return tex;
    }

    /// <summary>A hooded peddler: earthy robe, gold trim, a bulging satchel at the hip.</summary>
    private static Texture2D DrawMerchant()
    {
        const int w = 20, h = 28;
        var px = new Color[w * h];
        void Set(int x, int y, Color c) { if (x >= 0 && x < w && y >= 0 && y < h) px[y * w + x] = c; }
        void Rect(int x0, int y0, int x1, int y1, Color c)
        { for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) Set(x, y, c); }

        var robe = new Color(96, 74, 56);
        var robeDark = new Color(72, 55, 42);
        var trim = new Color(212, 178, 90);
        var hood = new Color(82, 62, 48);
        var skin = new Color(224, 188, 152);
        var eyes = new Color(40, 32, 26);
        var satchel = new Color(140, 100, 60);
        var strap = new Color(60, 46, 36);

        // Robe body: widens toward the hem.
        for (int y = 10; y <= 26; y++)
        {
            int half = 3 + (y - 10) * 3 / 16; // 3..6
            Rect(9 - half, y, 10 + half, y, robe);
            Set(9 - half, y, robeDark);
            Set(10 + half, y, robeDark);
        }
        Rect(3, 26, 16, 27, robeDark);            // hem shadow
        Rect(3, 25, 16, 25, trim);                // gold hem trim
        // Hood: rounded cap over the head, face opening in front.
        Rect(6, 2, 13, 9, hood);
        Set(6, 2, Color.Transparent); Set(13, 2, Color.Transparent);
        Rect(7, 1, 12, 1, hood);
        Rect(8, 5, 11, 8, skin);                  // face
        Set(8, 6, eyes); Set(11, 6, eyes);        // eyes
        Rect(6, 9, 13, 10, robeDark);             // hood drape onto shoulders
        Rect(9, 11, 10, 24, trim);                // front trim stripe
        // Arms folded into sleeves.
        Rect(4, 12, 6, 16, robeDark);
        Rect(13, 12, 15, 16, robeDark);
        Rect(6, 15, 13, 16, robe);                // hands tucked across
        // Satchel on the right hip with a shoulder strap.
        for (int i = 0; i < 10; i++) Set(6 + i / 2, 10 + i, strap);
        Rect(13, 18, 17, 23, satchel);
        Rect(13, 18, 17, 18, strap);
        Set(15, 20, trim);                        // buckle glint

        return BakeStrip(px, w, h);
    }

    /// <summary>Named projectile sprites (SkillDefinition.ProjectileSprite), drawn pointing
    /// RIGHT; the renderer rotates them along the flight direction. Cached per name.</summary>
    public static Texture2D GetProjectileSprite(string key)
    {
        if (_device == null || string.IsNullOrEmpty(key)) return null;
        string cacheKey = "proj:" + key;
        if (_cache.TryGetValue(cacheKey, out var cached)) return cached[0];
        var tex = key switch
        {
            "IceSpike" => DrawIceSpike(),
            "IceShard" => DrawIceShard(),
            "Arrow" => DrawArrow(),
            _ => null,
        };
        if (tex == null) return null;
        _cache[cacheKey] = new[] { tex };
        return tex;
    }

    /// <summary>A jagged crystalline shard: icy blue body, bright core, trailing crystals.</summary>
    private static Texture2D DrawIceSpike()
    {
        const int w = 18, h = 9;
        var px = new Color[w * h];
        void Set(int x, int y, Color c) { if (x >= 0 && x < w && y >= 0 && y < h) px[y * w + x] = c; }

        var ice = new Color(140, 200, 245);
        var iceDark = new Color(80, 140, 215);
        var core = new Color(235, 250, 255);

        // Main shard: thick at the back (left), tapering to a point at the tip (right).
        for (int x = 2; x <= 16; x++)
        {
            int hw = x <= 8 ? 2 : x <= 12 ? 1 : 0; // half-width shrinks toward the tip
            for (int y = 4 - hw; y <= 4 + hw; y++)
                Set(x, y, y == 4 ? (x >= 12 ? core : ice) : (y < 4 ? ice : iceDark));
        }
        Set(16, 4, core); // gleaming tip
        // Small trailing crystals above/below the back end.
        Set(3, 1, iceDark); Set(4, 1, ice); Set(4, 2, ice);
        Set(5, 7, iceDark); Set(6, 7, ice);
        Set(1, 4, iceDark);

        return BakeStrip(px, w, h);
    }

    /// <summary>Walk-cycle frames for a skeletal minion, keyed by its summon skill:
    /// frame 0 stands, frames 1/2 stride (alternating leg lift, body bob, arm swing).
    /// Archers carry a short bow; warriors a scrap shield (their sword is drawn and
    /// animated separately by the renderer).</summary>
    public static Texture2D[] GetSummonFrames(string skillId = null)
    {
        if (_device == null) return null;
        bool warrior = skillId != null && skillId.Contains("warrior");
        // Hired mercenaries ride the same rig in flesh and leathers instead of bone.
        bool merc = skillId != null && skillId.StartsWith("merc");
        string key = (merc ? "summon:merc_" : "summon:skeleton_") + (warrior ? "warrior" : "archer");
        if (_cache.TryGetValue(key, out var cached)) return cached;
        var frames = new[]
        {
            DrawSummonFrame(warrior, 0, merc), DrawSummonFrame(warrior, 1, merc),
            DrawSummonFrame(warrior, 2, merc),
        };
        _cache[key] = frames;
        return frames;
    }

    /// <summary>Standing frame only (HUD cards and other static uses).</summary>
    public static Texture2D GetSummonSprite(string skillId = null) => GetSummonFrames(skillId)?[0];

    /// <summary>Companion pet frames (2 each): the gutter rat scurries low to the
    /// ground; the vagrant grimoire is drawn upright with fluttering pages (the
    /// renderer adds the hover). Brown-gold, matching their unique rarity.</summary>
    public static Texture2D[] GetPetFrames(string petId)
    {
        if (_device == null) return null;
        string key = "pet:" + petId;
        if (_cache.TryGetValue(key, out var cached)) return cached;
        var frames = petId == "pet_tome"
            ? new[] { DrawPetTome(0), DrawPetTome(1) }
            : new[] { DrawPetRat(0), DrawPetRat(1) };
        _cache[key] = frames;
        return frames;
    }

    public static Texture2D GetPetSprite(string petId) => GetPetFrames(petId)?[0];

    private static Texture2D DrawPetRat(int frame)
    {
        const int w = 14, h = 9;
        var px = new Color[w * h];
        void Set(int x, int y, Color c) { if (x >= 0 && x < w && y >= 0 && y < h) px[y * w + x] = c; }
        void Rect(int x0, int y0, int x1, int y1, Color c)
        { for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) Set(x, y, c); }
        var fur = new Color(138, 106, 69);
        var furDark = new Color(104, 78, 50);
        var belly = new Color(172, 142, 104);
        var pink = new Color(196, 132, 122);
        // Body: a low teardrop, nose to the right.
        Rect(3, 3, 9, 6, fur);
        Rect(4, 6, 8, 6, belly);
        Rect(9, 4, 11, 5, fur);        // head
        Set(12, 5, pink);              // nose
        Set(10, 3, furDark);           // ear
        Set(10, 4, new Color(24, 20, 18)); // eye
        // Tail: a thin curl behind, flicking with the scurry frame.
        int flick = frame == 1 ? 1 : 0;
        Set(2, 4 + flick, pink);
        Set(1, 3 + flick, pink);
        Set(0, 2 + flick, pink);
        // Legs: alternate pairs per frame for the scurry.
        if (frame == 0) { Set(4, 7, furDark); Set(8, 7, furDark); }
        else { Set(5, 7, furDark); Set(9, 7, furDark); }
        return BakeStrip(px, w, h);
    }

    private static Texture2D DrawPetTome(int frame)
    {
        const int w = 12, h = 13;
        var px = new Color[w * h];
        void Set(int x, int y, Color c) { if (x >= 0 && x < w && y >= 0 && y < h) px[y * w + x] = c; }
        void Rect(int x0, int y0, int x1, int y1, Color c)
        { for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) Set(x, y, c); }
        var cover = new Color(148, 108, 56);
        var coverDark = new Color(110, 78, 40);
        var page = new Color(226, 214, 182);
        var rune = new Color(196, 150, 80);
        // An open book seen edge-on: two leaves of pages over a leather cover.
        Rect(1, 4, 10, 9, cover);
        Rect(1, 9, 10, 10, coverDark);
        Rect(2, 3, 5, 7, page);
        Rect(6, 3, 9, 7, page);
        Set(5, 3, coverDark); Set(5, 4, coverDark); // the spine gutter
        // A fluttering corner page on alternate frames.
        if (frame == 1) { Set(1, 2, page); Set(10, 2, page); }
        // The rune glinting on the spine below.
        Set(5, 10, rune); Set(6, 10, rune);
        // Faint drifting motes above (the muttering marginalia).
        Set(frame == 0 ? 2 : 9, 0, rune * 0.8f);
        return BakeStrip(px, w, h);
    }

    private static Texture2D DrawSummonFrame(bool warrior, int frame, bool merc = false)
    {
        const int w = 16, h = 22;
        var px = new Color[w * h];
        void Set(int x, int y, Color c) { if (x >= 0 && x < w && y >= 0 && y < h) px[y * w + x] = c; }
        void Rect(int x0, int y0, int x1, int y1, Color c)
        { for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) Set(x, y, c); }

        // Mercs are living hirelings: the skeleton's bone palette becomes leathers,
        // and the skull becomes a flesh-and-hair head — same rig, same animation.
        var bone = merc ? new Color(146, 108, 68) : new Color(226, 222, 204);
        var boneDark = merc ? new Color(104, 76, 48) : new Color(168, 162, 142);
        var head = merc ? new Color(212, 174, 138) : new Color(226, 222, 204);
        var socket = new Color(30, 28, 26);

        // Stride: one leg lifts 2px while the other plants, the torso bobs 1px and
        // the arms counter-swing 1px — a real gait instead of a glide.
        int leftUp = frame == 1 ? 2 : 0;
        int rightUp = frame == 2 ? 2 : 0;
        int bob = frame == 0 ? 0 : 1;
        int armSwing = frame == 1 ? 1 : frame == 2 ? -1 : 0;

        // Skull (mercs: a living head with a cropped mop of hair)
        Rect(5, 0 + bob, 10, 5 + bob, head);
        Set(5, 0 + bob, Color.Transparent); Set(10, 0 + bob, Color.Transparent);
        if (merc) Rect(6, 0 + bob, 9, 1 + bob, new Color(74, 56, 38));
        Set(6, 2 + bob, socket); Set(9, 2 + bob, socket);
        Rect(6, 4 + bob, 9, 4 + bob, boneDark);          // jaw shadow
        // Spine + ribcage
        Set(7, 6 + bob, boneDark); Set(8, 6 + bob, boneDark);
        Rect(5, 7 + bob, 10, 7 + bob, bone);
        Rect(5, 9 + bob, 10, 9 + bob, bone);
        Rect(5, 11 + bob, 10, 11 + bob, bone);
        Set(7, 8 + bob, boneDark); Set(8, 8 + bob, boneDark);
        Set(7, 10 + bob, boneDark); Set(8, 10 + bob, boneDark);
        // Pelvis + striding legs
        Rect(6, 12 + bob, 9, 13 + bob, boneDark);
        Rect(5, 14, 6, 18 - leftUp, bone);
        Rect(9, 14, 10, 18 - rightUp, bone);
        Set(5, 19 - leftUp, boneDark); Set(6, 19 - leftUp, boneDark);
        Set(9, 19 - rightUp, boneDark); Set(10, 19 - rightUp, boneDark);
        Rect(4 + leftUp / 2, 20 - leftUp, 6 + leftUp / 2, 20 - leftUp, bone);   // striding feet
        Rect(9 - rightUp / 2, 20 - rightUp, 11 - rightUp / 2, 20 - rightUp, bone);

        if (warrior)
        {
            var shield = new Color(104, 88, 60);
            var shieldRim = new Color(140, 122, 88);
            // Sword arm reaching forward — the blade itself is NOT baked in: the
            // renderer draws GetBoneSword() in this hand and animates rest/chop.
            Rect(11 + armSwing, 8 + bob, 13 + armSwing, 9 + bob, bone);
            Set(13 + armSwing, 9 + bob, boneDark);
            // Scrap shield strapped to the off arm, swinging counter to the stride
            Rect(3 - armSwing, 8 + bob, 4 - armSwing, 10 + bob, bone);
            Rect(1 - armSwing, 7 + bob, 3 - armSwing, 11 + bob, shield);
            Set(1 - armSwing, 7 + bob, shieldRim); Set(3 - armSwing, 7 + bob, shieldRim);
            Set(1 - armSwing, 11 + bob, shieldRim); Set(3 - armSwing, 11 + bob, shieldRim);
            Set(2 - armSwing, 9 + bob, shieldRim);         // boss stud
        }
        else
        {
            var bow = new Color(110, 78, 46);
            var stringC = new Color(200, 196, 180);
            // Bow arm + short bow held to the right side, riding the stride
            Rect(11 + armSwing, 8 + bob, 12 + armSwing, 9 + bob, bone);
            for (int i = 0; i < 7; i++) Set(13 + armSwing + (i is 0 or 6 ? 0 : 1), 5 + bob + i, bow);
            for (int i = 1; i < 6; i++) Set(13 + armSwing, 5 + bob + i, stringC);
            // Off arm counter-swings
            Rect(3 - armSwing, 8 + bob, 4 - armSwing, 10 + bob, bone);
        }

        return BakeStrip(px, w, h);
    }

    /// <summary>A slim arrow for skeleton archers: shaft, iron head, feather fletching.</summary>
    private static Texture2D DrawArrow()
    {
        const int w = 13, h = 5;
        var px = new Color[w * h];
        void Set(int x, int y, Color c) { if (x >= 0 && x < w && y >= 0 && y < h) px[y * w + x] = c; }
        var shaft = new Color(150, 116, 72);
        var head = new Color(180, 184, 192);
        var feather = new Color(210, 205, 190);
        for (int x = 1; x <= 9; x++) Set(x, 2, shaft);
        Set(10, 2, head); Set(11, 2, head); Set(12, 2, head);
        Set(10, 1, head); Set(10, 3, head);
        Set(0, 1, feather); Set(1, 1, feather);
        Set(0, 3, feather); Set(1, 3, feather);
        Set(0, 2, feather);
        return BakeStrip(px, w, h);
    }

    /// <summary>A stubby splinter of ice for Shattering shards — deliberately distinct
    /// from the Ice Spike: shorter, angular, with a frosted glassy edge.</summary>
    private static Texture2D DrawIceShard()
    {
        // A small broken-off PIECE of ice — a compact faceted chunk, nothing like the
        // long tapered Ice Spike it burst from.
        const int w = 8, h = 8;
        var px = new Color[w * h];
        void Set(int x, int y, Color c) { if (x >= 0 && x < w && y >= 0 && y < h) px[y * w + x] = c; }

        var glass = new Color(196, 238, 255);
        var glassDeep = new Color(116, 176, 238);
        var edge = new Color(66, 116, 196);
        var glint = Color.White;

        // Irregular lump: a squat pentagon with one sheared corner.
        Set(3, 1, glassDeep); Set(4, 1, glassDeep);
        Set(2, 2, glassDeep); Set(3, 2, glass); Set(4, 2, glass); Set(5, 2, edge);
        Set(1, 3, edge); Set(2, 3, glass); Set(3, 3, glint); Set(4, 3, glass); Set(5, 3, glassDeep); Set(6, 3, edge);
        Set(1, 4, edge); Set(2, 4, glassDeep); Set(3, 4, glass); Set(4, 4, glassDeep); Set(5, 4, edge);
        Set(2, 5, edge); Set(3, 5, glassDeep); Set(4, 5, edge);
        Set(3, 6, edge);
        // Facet line + sparkle.
        Set(4, 2, Shade(glass, 1.05f)); Set(5, 1, glint);

        return BakeStrip(px, w, h);
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
        else if (kind == "slow")
        {
            // Double blue chevron pointing down: movement dragged toward the ground.
            var blue = new Color(110, 170, 245);
            var blueDark = new Color(60, 110, 200);
            for (int i = 0; i < 4; i++)
            {
                Set(1 + i, 1 + i, blue); Set(7 - i, 1 + i, blue);
                Set(1 + i, 4 + i, blueDark); Set(7 - i, 4 + i, blueDark);
            }
            Set(4, 4, blue); Set(4, 7, blueDark);
        }
        else if (kind == "chill")
        {
            // Six-armed snowflake.
            var ice = new Color(160, 215, 250);
            var iceDark = new Color(90, 150, 220);
            for (int i = 0; i < 9; i++) { Set(4, i, ice); Set(i, 4, ice); }
            for (int i = 1; i < 8; i++) { Set(i, i, iceDark); Set(8 - i, i, iceDark); }
            Set(4, 4, Color.White);
        }
        else if (kind == "frozen")
        {
            // Solid ice crystal: pale diamond with a bright core.
            var ice = new Color(150, 200, 250);
            var iceDeep = new Color(80, 130, 220);
            for (int y = 0; y < 9; y++)
            {
                int half = 4 - Math.Abs(y - 4);
                for (int x = 4 - half; x <= 4 + half; x++)
                    Set(x, y, x == 4 - half || x == 4 + half ? iceDeep : ice);
            }
            Set(4, 4, Color.White); Set(4, 3, new Color(220, 240, 255));
        }
        else if (kind == "shock")
        {
            // Jagged yellow lightning bolt.
            var bolt = new Color(255, 235, 110);
            var boltDark = new Color(210, 170, 40);
            Set(5, 0, bolt); Set(4, 1, bolt); Set(4, 2, bolt); Set(3, 3, bolt);
            Set(3, 4, bolt); Set(5, 4, boltDark); Set(4, 4, bolt);
            Set(5, 5, bolt); Set(4, 6, bolt); Set(4, 7, boltDark); Set(3, 8, bolt);
        }
        else if (kind == "poison")
        {
            // Green droplet with bubbles.
            var venom = new Color(120, 220, 80);
            var venomDark = new Color(60, 150, 40);
            Set(4, 0, venomDark); Set(4, 1, venom);
            for (int y = 2; y <= 6; y++)
            {
                int half = y <= 4 ? (y - 1) / 2 + 1 : 6 - y + 1;
                for (int x = 4 - half; x <= 4 + half; x++) Set(x, y, venom);
            }
            Set(3, 3, new Color(200, 255, 170)); Set(4, 7, venomDark);
            Set(1, 7, venomDark); Set(7, 6, venomDark);
        }
        else if (kind == "bleed")
        {
            // Deep red drop with a falling drip.
            var blood = new Color(210, 50, 45);
            var bloodDark = new Color(140, 25, 25);
            Set(4, 0, bloodDark); Set(4, 1, blood);
            for (int y = 2; y <= 5; y++)
            {
                int half = y <= 3 ? y - 1 : 5 - y + 1;
                for (int x = 4 - half; x <= 4 + half; x++) Set(x, y, blood);
            }
            Set(3, 2, new Color(255, 150, 140));
            Set(4, 6, bloodDark); Set(4, 8, bloodDark);
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

    /// <summary>Solid-red silhouette of an enemy frame, cached per type+frame — the
    /// renderer draws it at small offsets beneath the sprite as a hover OUTLINE
    /// (tinting the whole sprite red made elites unreadable).</summary>
    public static Texture2D GetEnemySilhouette(EnemyDefinition def, int frame)
    {
        var frames = GetEnemyFrames(def);
        if (frames == null || frames.Length == 0) return null;
        frame = Math.Abs(frame) % frames.Length;
        string key = $"sil:{def.Id}:{frame}";
        if (_cache.TryGetValue(key, out var cached)) return cached[0];
        var src = frames[frame];
        var data = new Color[src.Width * src.Height];
        src.GetData(data);
        var red = new Color(255, 66, 52);
        for (int i = 0; i < data.Length; i++)
            data[i] = data[i].A != 0 ? red : Color.Transparent;
        var tex = new Texture2D(_device, src.Width, src.Height);
        tex.SetData(data);
        _cache[key] = new[] { tex };
        return tex;
    }

    /// <summary>Radial ground-crack impact overlay (drawn iso-squashed by the renderer):
    /// jagged spokes radiating from a bright center, used by Slam skills.</summary>
    public static Texture2D GetImpactSprite()
    {
        if (_device == null) return null;
        if (_cache.TryGetValue("fx:impact", out var cached)) return cached[0];
        const int s2 = 64;
        var px = new Color[s2 * s2];
        void Set(int x, int y, Color c) { if (x >= 0 && x < s2 && y >= 0 && y < s2) px[y * s2 + x] = c; }
        var crack = new Color(58, 46, 34);
        var crackLite = new Color(96, 78, 56);
        var flash = new Color(235, 220, 170);
        var rng = new Random(1234);
        const int spokes = 9;
        for (int i = 0; i < spokes; i++)
        {
            float ang = i / (float)spokes * MathF.Tau + (float)rng.NextDouble() * 0.4f;
            float len = 18 + (float)rng.NextDouble() * 12;
            float cx = 32, cy = 32;
            float dx = MathF.Cos(ang), dy = MathF.Sin(ang);
            for (float d = 3; d < len; d += 0.7f)
            {
                // jitter the crack line as it travels
                if (rng.Next(4) == 0) { cx += -dy * (rng.Next(3) - 1) * 0.8f; cy += dx * (rng.Next(3) - 1) * 0.8f; }
                int xx = (int)(cx + dx * d), yy = (int)(cy + dy * d);
                Set(xx, yy, d < len * 0.55f ? crack : crackLite);
                if (d < len * 0.35f) Set(xx + 1, yy, crack);
            }
        }
        // Bright center flash.
        for (int y = -3; y <= 3; y++)
            for (int x = -3; x <= 3; x++)
                if (x * x + y * y <= 9) Set(32 + x, 32 + y, flash);
        var tex = new Texture2D(_device, s2, s2);
        tex.SetData(px);
        _cache["fx:impact"] = new[] { tex };
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
    /// <summary>
    /// Themed zone decoration sprites (gravestones, pillars, rocks, trees...), anchored
    /// at their bottom-center when drawn. Key format "style:kind:variant" — see
    /// WorldRenderer's clutter/feature tables. Cached like everything else.
    /// </summary>
    public static Texture2D GetPropSprite(string key)
    {
        if (_device == null) return null;
        string cacheKey = "prop:" + key;
        if (_cache.TryGetValue(cacheKey, out var cached)) return cached[0];
        var parts = key.Split(':');
        var tex = DrawProp(parts[0], parts[1], parts.Length > 2 ? int.Parse(parts[2]) : 0);
        if (tex == null) return null;
        _cache[cacheKey] = new[] { tex };
        return tex;
    }

    private static Texture2D DrawProp(string style, string kind, int variant)
    {
        Color[] px = null;
        int w = 0, h = 0;
        void Init(int pw, int ph) { w = pw; h = ph; px = new Color[w * h]; }
        void Set(int x, int y, Color c) { if (x >= 0 && x < w && y >= 0 && y < h) px[y * w + x] = c; }
        void Rect(int x0, int y0, int rw, int rh, Color c)
        { for (int y = y0; y < y0 + rh; y++) for (int x = x0; x < x0 + rw; x++) Set(x, y, c); }
        void VLine(int x, int y0, int len, Color c) { for (int y = y0; y < y0 + len; y++) Set(x, y, c); }

        switch ($"{style}:{kind}")
        {
            case "graveyard:clutter":
                if (variant == 0) // rounded gravestone
                {
                    Init(10, 12);
                    var stone = new Color(120, 122, 130); var dark = Shade(stone, 0.7f);
                    Rect(2, 3, 6, 8, stone);
                    Rect(3, 1, 4, 3, stone);
                    Rect(2, 9, 6, 2, dark);
                    Set(4, 4, dark); Set(5, 6, dark); // weathering
                }
                else if (variant == 1) // cross marker
                {
                    Init(10, 13);
                    var wood = new Color(96, 76, 52); var dark = Shade(wood, 0.7f);
                    Rect(4, 1, 2, 11, wood);
                    Rect(1, 3, 8, 2, wood);
                    Rect(4, 10, 2, 2, dark);
                }
                else // bone pile
                {
                    Init(12, 7);
                    var bone = new Color(205, 198, 178); var dark = Shade(bone, 0.75f);
                    Rect(2, 4, 8, 2, bone);
                    Rect(1, 5, 3, 1, dark);
                    Set(9, 3, bone); Set(3, 3, bone); Set(6, 2, dark);
                }
                break;
            case "graveyard:feature":
                if (variant == 0) // obelisk
                {
                    Init(14, 30);
                    var stone = new Color(126, 128, 140); var dark = Shade(stone, 0.68f); var lite = Shade(stone, 1.25f);
                    Rect(4, 4, 6, 22, stone);
                    Rect(5, 1, 4, 4, stone);
                    Set(6, 0, lite); Set(7, 0, lite);
                    Rect(2, 26, 10, 3, dark);
                    VLine(4, 4, 22, lite);
                    VLine(9, 4, 22, dark);
                    Set(6, 8, dark); Set(7, 12, dark); Set(6, 16, dark); // runes
                }
                else // crypt slab
                {
                    Init(22, 20);
                    var stone = new Color(108, 110, 122); var dark = Shade(stone, 0.7f); var lite = Shade(stone, 1.2f);
                    Rect(2, 8, 18, 10, stone);
                    Rect(1, 16, 20, 3, dark);
                    Rect(4, 4, 14, 5, stone);
                    Rect(4, 4, 14, 1, lite);
                    Rect(8, 10, 6, 6, dark); // doorway
                    Rect(9, 11, 4, 5, new Color(18, 14, 22));
                }
                break;

            case "tomb:clutter":
                if (variant == 0) // urn
                {
                    Init(9, 11);
                    var clay = new Color(150, 110, 76); var dark = Shade(clay, 0.7f);
                    Rect(2, 4, 5, 5, clay);
                    Rect(3, 2, 3, 2, dark);
                    Rect(2, 8, 5, 1, dark);
                    Set(2, 5, Shade(clay, 1.2f));
                }
                else if (variant == 1) // rubble
                {
                    Init(13, 7);
                    var stone = new Color(112, 106, 118); var dark = Shade(stone, 0.7f);
                    Rect(2, 4, 4, 2, stone); Rect(7, 3, 4, 3, dark); Set(5, 2, stone); Set(10, 2, stone);
                }
                else // fallen column chunk
                {
                    Init(14, 8);
                    var stone = new Color(140, 130, 104); var dark = Shade(stone, 0.7f);
                    Rect(2, 2, 10, 5, stone);
                    Rect(2, 5, 10, 2, dark);
                    VLine(5, 2, 5, dark); VLine(9, 2, 5, dark);
                }
                break;
            case "tomb:feature":
                if (variant == 0) // standing column
                {
                    Init(14, 30);
                    var stone = new Color(158, 146, 116); var dark = Shade(stone, 0.68f); var lite = Shade(stone, 1.2f);
                    Rect(3, 2, 8, 3, stone);
                    Rect(2, 1, 10, 2, lite);
                    Rect(4, 5, 6, 20, stone);
                    VLine(4, 5, 20, lite); VLine(9, 5, 20, dark);
                    VLine(6, 5, 20, dark);
                    Rect(3, 25, 8, 3, dark);
                }
                else // sarcophagus
                {
                    Init(22, 16);
                    var stone = new Color(150, 138, 108); var dark = Shade(stone, 0.7f); var lite = Shade(stone, 1.2f);
                    Rect(3, 5, 16, 9, stone);
                    Rect(2, 3, 18, 3, lite);
                    Rect(3, 12, 16, 2, dark);
                    Rect(8, 7, 6, 1, dark); // carving
                    Rect(10, 5, 2, 7, dark);
                }
                break;

            case "arid:clutter":
                if (variant == 0) // rock
                {
                    Init(11, 8);
                    var rock = new Color(148, 112, 84); var dark = Shade(rock, 0.7f);
                    Rect(2, 3, 7, 4, rock);
                    Rect(3, 2, 5, 1, rock);
                    Rect(2, 6, 7, 1, dark);
                    Set(4, 3, Shade(rock, 1.2f));
                }
                else if (variant == 1) // skull
                {
                    Init(9, 8);
                    var bone = new Color(214, 204, 182); var dark = Shade(bone, 0.65f);
                    Rect(2, 1, 5, 4, bone);
                    Rect(3, 5, 3, 2, bone);
                    Set(3, 3, dark); Set(5, 3, dark); // sockets
                }
                else // dry shrub
                {
                    Init(12, 9);
                    var twig = new Color(150, 122, 70);
                    VLine(5, 3, 5, twig); VLine(6, 2, 6, Shade(twig, 0.8f));
                    Set(3, 3, twig); Set(4, 4, twig); Set(8, 3, twig); Set(7, 4, twig);
                    Set(2, 2, Shade(twig, 0.8f)); Set(9, 2, Shade(twig, 0.8f));
                }
                break;
            case "arid:feature":
                if (variant == 0) // rock spire
                {
                    Init(18, 28);
                    var rock = new Color(168, 122, 88); var dark = Shade(rock, 0.68f); var lite = Shade(rock, 1.2f);
                    Rect(6, 2, 6, 8, rock);
                    Rect(5, 8, 9, 9, rock);
                    Rect(3, 16, 12, 9, rock);
                    Rect(3, 23, 12, 2, dark);
                    VLine(6, 2, 22, lite);
                    VLine(12, 8, 16, dark);
                    Set(8, 1, lite);
                }
                else // saguaro cactus
                {
                    Init(18, 28);
                    var cactus = new Color(88, 130, 72); var dark = Shade(cactus, 0.7f); var lite = Shade(cactus, 1.25f);
                    Rect(8, 3, 3, 23, cactus);
                    VLine(8, 3, 23, lite); VLine(10, 3, 23, dark);
                    Rect(3, 8, 2, 3, cactus); Rect(3, 8, 6, 2, cactus); // left arm
                    VLine(3, 6, 3, cactus);
                    Rect(13, 12, 2, 3, cactus); Rect(11, 12, 4, 2, cactus); // right arm
                    VLine(14, 10, 3, cactus);
                }
                break;

            case "forest:clutter":
                if (variant is 0 or 5) // grass tuft (two spawn slots: grass is common)
                {
                    Init(9, 7);
                    var grass = new Color(96, 150, 74);
                    VLine(2, 3, 3, grass); VLine(4, 1, 5, Shade(grass, 1.2f)); VLine(6, 2, 4, grass);
                    Set(3, 2, Shade(grass, 0.8f)); Set(5, 3, Shade(grass, 0.8f));
                    if (variant == 5) { Set(1, 4, grass); Set(7, 3, Shade(grass, 1.1f)); }
                }
                else if (variant == 1) // mushroom
                {
                    Init(8, 8);
                    var cap = new Color(178, 84, 70); var stem = new Color(206, 196, 172);
                    Rect(1, 2, 6, 3, cap);
                    Rect(2, 1, 4, 1, cap);
                    Set(2, 2, Shade(cap, 1.3f)); Set(5, 3, Shade(cap, 1.3f)); // spots
                    Rect(3, 5, 2, 3, stem);
                }
                else if (variant == 2) // bush
                {
                    Init(13, 9);
                    var leaf = new Color(66, 106, 54); var dark = Shade(leaf, 0.72f); var lite = Shade(leaf, 1.25f);
                    Rect(2, 3, 9, 5, leaf);
                    Rect(3, 1, 7, 3, leaf);
                    Rect(2, 7, 9, 1, dark);
                    Set(4, 2, lite); Set(7, 3, lite); Set(9, 4, lite);
                }
                else if (variant == 3) // small stones
                {
                    Init(12, 7);
                    var stone = new Color(136, 134, 128); var dark = Shade(stone, 0.72f); var lite = Shade(stone, 1.2f);
                    Rect(2, 3, 4, 3, stone);
                    Set(2, 5, dark); Set(5, 3, lite);
                    Rect(7, 4, 3, 2, dark);
                    Set(8, 3, stone);
                    Set(5, 6, dark);
                }
                else if (variant == 4) // taller grass clump
                {
                    Init(12, 11);
                    var grass = new Color(90, 144, 70); var lite = Shade(grass, 1.25f); var dark = Shade(grass, 0.78f);
                    VLine(2, 5, 5, grass); VLine(4, 2, 8, lite); VLine(6, 1, 9, grass);
                    VLine(8, 3, 7, dark); VLine(9, 6, 4, grass);
                    Set(3, 4, dark); Set(5, 2, lite); Set(7, 2, lite);
                    Set(1, 7, grass); Set(10, 5, dark);
                }
                else if (variant == 6) // mossy boulder
                {
                    Init(16, 12);
                    var rock = new Color(122, 120, 116); var dark = Shade(rock, 0.68f); var lite = Shade(rock, 1.22f);
                    var moss = new Color(84, 122, 62);
                    Rect(3, 4, 10, 6, rock);
                    Rect(4, 2, 8, 3, rock);
                    Rect(3, 9, 10, 2, dark);
                    Set(2, 6, rock); Set(13, 5, rock);           // ragged silhouette
                    Set(5, 3, lite); Set(7, 2, lite);            // top light
                    Rect(4, 2, 5, 2, moss);                      // moss cap
                    Set(9, 3, moss); Set(3, 4, Shade(moss, 0.8f));
                    Set(8, 6, dark); Set(6, 8, dark);            // cracks
                }
                else if (variant == 7) // lichen-spotted slab pair
                {
                    Init(15, 9);
                    var rock = new Color(134, 128, 118); var dark = Shade(rock, 0.7f); var lite = Shade(rock, 1.2f);
                    var lichen = new Color(150, 158, 96);
                    Rect(2, 3, 7, 5, rock);                      // leaning slab
                    Rect(3, 2, 5, 2, rock);
                    Rect(2, 7, 7, 1, dark);
                    Set(4, 3, lichen); Set(6, 4, lichen);        // lichen spots
                    Set(3, 2, lite);
                    Rect(10, 5, 4, 3, dark);                     // smaller companion
                    Set(11, 4, rock); Set(12, 4, lite);
                }
                else // fern
                {
                    Init(13, 10);
                    var frond = new Color(58, 118, 62); var lite = Shade(frond, 1.3f); var dark = Shade(frond, 0.72f);
                    VLine(6, 3, 6, dark);                        // stem
                    // Arching fronds: pixel steps out from the stem.
                    Set(5, 4, frond); Set(4, 3, frond); Set(3, 3, lite); Set(2, 4, frond);
                    Set(7, 4, frond); Set(8, 3, frond); Set(9, 3, lite); Set(10, 4, frond);
                    Set(5, 6, frond); Set(4, 6, lite); Set(3, 7, frond);
                    Set(7, 6, frond); Set(8, 6, lite); Set(9, 7, frond);
                    Set(6, 2, lite); Set(6, 1, frond);
                }
                break;
            case "forest:bigtree": // large 2x2-tile canopy trees (generated landmarks)
                if (variant == 2) // dark fir: stacked triangular tiers
                {
                    Init(66, 88);
                    var trunk = new Color(88, 62, 40); var trunkD = Shade(trunk, 0.68f);
                    var leaf = new Color(38, 78, 46); var leafD = Shade(leaf, 0.7f); var leafL = Shade(leaf, 1.32f);
                    Rect(30, 66, 6, 19, trunk);
                    VLine(35, 66, 19, trunkD);
                    Rect(25, 81, 5, 4, trunkD); Rect(37, 81, 5, 4, trunkD);
                    // Tiers, widest at the base.
                    Rect(12, 52, 42, 16, leaf);
                    Rect(17, 38, 32, 16, leaf);
                    Rect(22, 24, 22, 16, leaf);
                    Rect(27, 12, 12, 14, leaf);
                    Rect(30, 4, 6, 10, leaf);
                    // Tier undershadows + snow-less lit edges.
                    Rect(12, 64, 42, 4, leafD);
                    Rect(17, 50, 32, 4, leafD);
                    Rect(22, 36, 22, 4, leafD);
                    Rect(27, 23, 12, 3, leafD);
                    Rect(14, 52, 6, 2, leafL); Rect(19, 38, 6, 2, leafL);
                    Rect(24, 24, 5, 2, leafL); Rect(29, 12, 4, 2, leafL);
                    Set(32, 3, leafL); Set(33, 3, leafL);
                }
                else if (variant == 3) // pale birch: slim white trunk, airy light canopy
                {
                    Init(66, 88);
                    var bark = new Color(214, 210, 198); var barkD = new Color(60, 58, 52);
                    var leaf = new Color(104, 138, 58); var leafD = Shade(leaf, 0.72f); var leafL = Shade(leaf, 1.28f);
                    Rect(31, 44, 5, 41, bark);
                    Set(32, 50, barkD); Set(33, 56, barkD); Set(31, 64, barkD); // bark scars
                    Set(34, 70, barkD); Set(32, 76, barkD);
                    Rect(27, 80, 4, 5, Shade(bark, 0.82f)); Rect(36, 80, 4, 5, Shade(bark, 0.82f));
                    Rect(24, 34, 5, 3, bark); Rect(38, 28, 5, 3, bark);        // branches
                    // Airy canopy: smaller separated clumps with sky gaps.
                    Rect(14, 18, 22, 14, leaf);
                    Rect(32, 10, 22, 16, leaf);
                    Rect(22, 4, 20, 10, leaf);
                    Rect(8, 28, 14, 12, leaf);
                    Rect(42, 26, 16, 12, leaf);
                    Rect(26, 28, 14, 12, leaf);
                    Rect(8, 37, 14, 3, leafD); Rect(42, 35, 16, 3, leafD); Rect(26, 37, 14, 3, leafD);
                    Rect(24, 5, 14, 3, leafL); Rect(34, 12, 10, 3, leafL);
                    Set(18, 22, leafL); Set(46, 20, leafL); Set(30, 32, leafD); Set(50, 30, leafD);
                }
                else
                {
                    Init(66, 88);
                    var trunk = new Color(98, 72, 46); var trunkD = Shade(trunk, 0.68f); var trunkL = Shade(trunk, 1.2f);
                    var leaf = variant == 0 ? new Color(56, 96, 44) : new Color(66, 90, 40);
                    var leafD = Shade(leaf, 0.7f); var leafL = Shade(leaf, 1.3f); var leafM = Shade(leaf, 1.12f);
                    // Trunk with buttress roots.
                    Rect(29, 58, 8, 27, trunk);
                    VLine(35, 58, 27, trunkD); VLine(36, 58, 27, trunkD);
                    VLine(29, 58, 27, trunkL);
                    Rect(23, 80, 6, 5, trunkD); Rect(37, 80, 7, 5, trunkD);
                    Rect(26, 55, 14, 4, trunk);
                    // A visible branch into the canopy.
                    Rect(20, 46, 4, 3, trunk); Rect(23, 48, 8, 3, trunk);
                    // Canopy: tall irregular mass built from stacked blobs.
                    Rect(8, 22, 50, 32, leaf);
                    Rect(13, 11, 40, 16, leaf);
                    Rect(21, 4, 24, 11, leaf);
                    Rect(2, 30, 10, 16, leaf);
                    Rect(54, 28, 10, 16, leaf);
                    // Shadow bottom + lit crown.
                    Rect(8, 48, 50, 6, leafD);
                    Rect(2, 42, 10, 4, leafD);
                    Rect(54, 40, 10, 4, leafD);
                    Rect(17, 6, 20, 4, leafL);
                    Rect(12, 15, 14, 5, leafM);
                    Rect(40, 13, 12, 4, leafM);
                    // Texture clumps.
                    Rect(18, 28, 7, 4, leafD); Rect(38, 34, 8, 4, leafD); Rect(28, 22, 6, 3, leafD);
                    Rect(30, 18, 6, 3, leafL); Rect(46, 24, 5, 3, leafL); Rect(12, 36, 5, 3, leafL);
                    Set(14, 34, leafL); Set(52, 32, leafL); Set(24, 40, leafL);
                }
                break;
            case "forest:feature": // trees
                if (variant == 2) // slender young fir
                {
                    Init(20, 34);
                    var trunk = new Color(96, 68, 44);
                    var leaf = new Color(42, 84, 50); var leafD = Shade(leaf, 0.72f); var leafL = Shade(leaf, 1.3f);
                    Rect(9, 26, 2, 6, trunk);
                    Rect(4, 18, 12, 8, leaf);
                    Rect(6, 10, 8, 9, leaf);
                    Rect(8, 4, 4, 7, leaf);
                    Set(9, 2, leaf); Set(10, 2, leaf);
                    Rect(4, 24, 12, 2, leafD);
                    Rect(6, 17, 8, 2, leafD);
                    Set(6, 11, leafL); Set(8, 5, leafL); Set(5, 19, leafL);
                }
                else
                {
                    Init(24, 34);
                    var trunk = new Color(104, 78, 50); var trunkD = Shade(trunk, 0.7f);
                    var leaf = variant == 0 ? new Color(60, 102, 48) : new Color(72, 96, 44);
                    var leafD = Shade(leaf, 0.72f); var leafL = Shade(leaf, 1.28f);
                    Rect(10, 22, 4, 10, trunk);
                    VLine(13, 22, 10, trunkD);
                    Rect(8, 30, 2, 2, trunkD); Rect(14, 30, 2, 2, trunkD); // roots
                    // Canopy: stacked blobs.
                    Rect(4, 10, 16, 12, leaf);
                    Rect(6, 5, 12, 7, leaf);
                    Rect(8, 2, 8, 5, leaf);
                    Rect(4, 19, 16, 3, leafD);
                    Rect(6, 3, 6, 2, leafL);
                    Set(6, 12, leafL); Set(15, 8, leafL); Set(10, 16, leafD); Set(17, 14, leafD);
                }
                break;

            default:
                return null;
        }
        return BakeStrip(px, w, h);
    }

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

    /// <summary>A bow, drawn like every held weapon: pointing RIGHT (the arrow's flight
    /// direction) with the grip at the left edge — stave arcs vertically, string near
    /// the grip, a nocked arrow along the middle.</summary>
    private static Texture2D DrawBow(Color wood)
    {
        // Drawn ALONG X like every held weapon (the renderer rotates -90°, so in hand
        // the bow stands upright): a clean stave arc with the string on the chord —
        // no nocked arrow (arrows exist only as flying projectiles).
        const int w = 30, h = 12;
        var px = new Color[w * h];
        void Set(int x, int y, Color c) { if (x >= 0 && x < w && y >= 0 && y < h) px[y * w + x] = c; }

        var woodDark = Shade(wood, 0.7f);
        var stringC = new Color(222, 222, 210);
        var grip = new Color(88, 64, 42);

        // String first: the straight chord between the tips.
        for (int x = 3; x <= 26; x++) Set(x, 3, stringC);
        // Stave: tips at the string, belly bulging away from it.
        for (int x = 2; x <= 27; x++)
        {
            float t = (x - 2) / 25f;
            int y = 3 + (int)(5.6f * MathF.Sin(t * MathF.PI)); // 3 at tips, ~9 mid
            Set(x, y, wood);
            Set(x, y + 1, woodDark);
        }
        Set(2, 2, woodDark); Set(27, 2, woodDark);            // tip nocks
        // Leather grip wrap at the middle of the belly.
        for (int x = 13; x <= 16; x++) { Set(x, 8, grip); Set(x, 9, grip); Set(x, 10, Shade(grip, 0.7f)); }

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

    /// <summary>An armored skeleton knight: bone frame under a dented breastplate and
    /// open-faced helm, tattered tabard in the enemy's tint. The sword is NOT part of the
    /// body sprite — the renderer draws GetBoneSword() in its hand and animates the raise
    /// and chop from EnemyAttack events.</summary>
    private static Texture2D DrawSkeletonKnight(Color clothTint, string seedKey, int frame)
    {
        var c = new Canvas();
        var rng = new Random(seedKey.GetHashCode() & int.MaxValue);

        var bone = new Color(224, 218, 198);
        var boneDark = Shade(bone, 0.68f);
        var steel = new Color(150, 154, 166);
        var steelLight = new Color(190, 194, 205);
        var steelDark = Shade(steel, 0.6f);
        var cloth = Shade(clothTint, 0.9f);
        var clothDark = Shade(clothTint, 0.55f);
        var eye = new Color(120, 200, 255);          // cold sockets

        // A marching gait with real amplitude: 2px leg lifts, a step bob on BOTH
        // stride frames, and arm/plume counter-motion below so it reads at a glance.
        int leftLegUp = frame == 1 ? 2 : 0;
        int rightLegUp = frame == 2 ? 2 : 0;
        int bob = frame == 0 ? 0 : 1;
        int armSwing = frame == 1 ? 1 : frame == 2 ? -1 : 0;

        // Bone legs with steel greave caps; striding feet shift under the lifted leg.
        c.Rect(9, 27, 3, 8 - leftLegUp, bone);
        c.Rect(14, 27, 3, 8 - rightLegUp, bone);
        c.Set(10, 29, boneDark); c.Set(15, 30, boneDark);   // knee joints
        c.Rect(9, 27, 3, 2, steelDark);                     // greave tops
        c.Rect(14, 27, 3, 2, steelDark);
        c.Rect(8 + leftLegUp / 2, 35 - leftLegUp, 4, 1, boneDark);   // feet
        c.Rect(14 - rightLegUp / 2, 35 - rightLegUp, 4, 1, boneDark);

        // Torso: breastplate over ribs, tabard hanging below the belt.
        int ty = 17 + bob;
        c.Rect(8, ty, 10, 7, steel);
        c.Rect(8, ty, 10, 1, steelLight);                   // collar shine
        c.Rect(16, ty, 2, 7, steelDark);                    // side shade
        c.Set(10 + rng.Next(5), ty + 2 + rng.Next(3), steelDark); // dent
        c.Set(9 + rng.Next(6), ty + 1 + rng.Next(4), steelDark);  // dent
        c.Rect(8, ty + 7, 10, 1, clothDark);                // belt
        c.Rect(10, ty + 8, 6, 3 - bob, cloth);              // tabard
        c.Set(11 + rng.Next(4), ty + 9, Color.Transparent); // tattered hem
        // Ribs peeking under the plate's arm gaps.
        c.Set(8, ty + 3, bone); c.Set(8, ty + 5, bone);

        // Arms: bare bone, counter-swinging the stride. Leading arm reaches forward
        // (the renderer puts the sword there); off arm hangs with a pauldron scrap.
        int ay = 18 + bob;
        c.Rect(16 + armSwing, ay, 5, 2, bone);              // leading arm
        c.Set(18 + armSwing, ay + 1, boneDark);
        c.Rect(6 - armSwing, ay, 3, 2, steelDark);          // pauldron scrap
        c.Rect(6 - armSwing, ay + 2, 2, 4, bone);           // hanging off arm
        c.Set(6 - armSwing, ay + 6, boneDark);

        // Skull in an open-faced helm with a tint plume that sways with the march.
        int hy = 9 + bob;
        c.Rect(9, hy, 8, 8, bone);
        c.Rect(9, hy, 8, 2, steel);                         // helm brow
        c.Rect(8, hy, 1, 5, steel);                         // cheek guard left
        c.Rect(17, hy, 1, 5, steel);                        // cheek guard right
        c.Rect(9, hy - 1, 8, 1, steelLight);                // helm crown
        c.Rect(12 - armSwing, hy - 3, 2, 2, cloth);         // plume sway
        c.Set(12 - armSwing, hy - 4, clothDark);
        c.Set(11, hy + 3, eye); c.Set(15, hy + 3, eye);     // glowing sockets
        c.Rect(10, hy + 6, 6, 1, boneDark);                 // jaw line
        c.Set(11, hy + 7, boneDark); c.Set(13, hy + 7, boneDark); c.Set(15, hy + 7, boneDark); // teeth

        return c.Bake(_device);
    }

    /// <summary>The Barrow Knight's notched bone-hilted blade, drawn pointing RIGHT with
    /// the grip at the left edge; the renderer rotates it through raise and chop arcs.</summary>
    public static Texture2D GetBoneSword()
    {
        if (_device == null) return null;
        if (_cache.TryGetValue("weapon:bone_sword", out var cached)) return cached[0];
        const int w = 18, h = 5;
        var px = new Color[w * h];
        void Set(int x, int y, Color c) { if (x >= 0 && x < w && y >= 0 && y < h) px[y * w + x] = c; }
        var blade = new Color(184, 188, 198);
        var bladeDark = new Color(126, 130, 142);
        var boneHilt = new Color(210, 202, 180);
        var wrap = new Color(96, 78, 56);
        // Grip + bone crossguard.
        Set(0, 2, wrap); Set(1, 2, wrap); Set(2, 2, wrap);
        Set(3, 1, boneHilt); Set(3, 2, boneHilt); Set(3, 3, boneHilt);
        // Blade with edge shading, a notch, and a tapered tip.
        for (int x = 4; x <= 15; x++)
        {
            Set(x, 1, blade);
            Set(x, 2, x % 4 == 3 ? bladeDark : blade);
            Set(x, 3, bladeDark);
        }
        Set(9, 1, Color.Transparent);         // notch bitten out of the edge
        Set(16, 2, blade); Set(17, 2, bladeDark); // tip
        var tex = BakeStrip(px, w, h);
        _cache["weapon:bone_sword"] = new[] { tex };
        return tex;
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

    // ------------------------------------------------------------------ weapon gore

    /// <summary>A splatter mask for a melee weapon: white droplets clinging to the
    /// striking HALF of its sprite, tinted at draw time with the victim's blood color.
    /// Gore BUILDS UP: stage 1 = a few flecks, 2 = a real splatter, 3 = soaked. Each
    /// stage is a superset of the last (the same droplets stay put as more land).
    /// Deterministic per base id; cached per stage.</summary>
    public static Texture2D GetWeaponGoreMask(Items.ItemBase itemBase, int stage)
    {
        if (itemBase == null || _device == null || stage <= 0) return null;
        stage = Math.Min(stage, 3);
        var baseTex = GetWeaponSprite(itemBase);
        if (baseTex == null) return null;
        string key = $"goremask:{itemBase.Id}:{stage}";
        if (_cache.TryGetValue(key, out var cached)) return cached[0];

        var src = new Color[baseTex.Width * baseTex.Height];
        baseTex.GetData(src);
        var dst = new Color[src.Length];
        int head = (int)(baseTex.Width * 0.45f); // splatter clings to the striking end
        uint idHash = 2166136261u;
        foreach (char ch in itemBase.Id) idHash = (idHash ^ ch) * 16777619u;
        for (int y = 0; y < baseTex.Height; y++)
            for (int x = head; x < baseTex.Width; x++)
            {
                int i = y * baseTex.Width + x;
                if (src[i].A == 0) continue;
                uint n = (uint)(x * 73856093 ^ y * 19349663) ^ idHash;
                n ^= n >> 13; n *= 0x5bd1e995; n ^= n >> 15;
                // A STAIN, not a coat of paint: sparse dark soak marks, densest at the
                // very tip; the stage scales the spread (same hash -> earlier marks
                // persist as more soak in).
                int chance = (26 + (x - head) * 70 / Math.Max(1, baseTex.Width - head))
                             * stage / 3;
                if ((n & 0xFF) < chance)
                {
                    byte v = (byte)(120 + (n >> 8) % 55); // dark: tint soaks IN, not on
                    dst[i] = new Color(v, v, v);
                }
            }
        var tex = BakeStrip(dst, baseTex.Width, baseTex.Height);
        _cache[key] = new[] { tex };
        return tex;
    }

    // ------------------------------------------------------------------ skill scrolls

    /// <summary>
    /// Sprite for a Skill Scroll: an UNROLLED hanging scroll — wooden dowel on top,
    /// dangling parchment, curled foot — a deliberately different silhouette from the
    /// rolled-horizontal Enchanting Scrolls, so the two scroll families read apart at
    /// a glance. The accent color comes from the base's SpriteColor (themed per
    /// effect) and a rune glyph picked by the scroll id marks the parchment.
    /// </summary>
    public static Texture2D GetSkillScrollSprite(Items.ItemBase itemBase)
    {
        if (itemBase == null || _device == null || itemBase.Category != Items.ItemCategory.SkillScroll)
            return null;
        string key = "skillscroll:" + itemBase.Id;
        if (_cache.TryGetValue(key, out var cached)) return cached[0];

        var accent = WorldRenderer.ParseColor(itemBase.SpriteColor, new Color(200, 150, 255));
        int runeSeed = 0;
        foreach (char c in itemBase.ScrollId ?? itemBase.Id) runeSeed = runeSeed * 31 + c;
        var tex = DrawSkillScroll(accent, Math.Abs(runeSeed));
        _cache[key] = new[] { tex };
        return tex;
    }

    private static Texture2D DrawSkillScroll(Color accent, int runeSeed)
    {
        const int w = 16, h = 22;
        var px = new Color[w * h];
        void Set(int x, int y, Color c) { if (x >= 0 && x < w && y >= 0 && y < h) px[y * w + x] = c; }
        void Rect(int x0, int y0, int rw, int rh, Color c)
        { for (int y = y0; y < y0 + rh; y++) for (int x = x0; x < x0 + rw; x++) Set(x, y, c); }

        var parchment = new Color(232, 222, 192);
        var parchDark = new Color(196, 184, 152);
        var wood = new Color(112, 80, 48);
        var woodDark = new Color(76, 52, 32);
        var accentDark = Shade(accent, 0.6f);

        // Top dowel with end knobs poking past the sheet.
        Rect(1, 1, 14, 2, wood);
        Rect(1, 3, 14, 1, woodDark);
        Set(0, 1, woodDark); Set(15, 1, woodDark);
        Set(0, 2, wood); Set(15, 2, wood);

        // Hanging parchment sheet with an accent trim band under the rod.
        Rect(3, 4, 10, 14, parchment);
        Rect(3, 4, 10, 1, parchDark);
        Rect(3, 5, 10, 1, accentDark);
        for (int y = 7; y < 17; y += 3) Set(12, y, parchDark); // worn right edge

        // Curled foot: the un-read remainder still rolled up.
        Rect(2, 18, 12, 2, parchDark);
        Rect(2, 20, 12, 1, Shade(parchDark, 0.75f));
        Set(2, 19, Shade(parchDark, 0.7f));
        Set(13, 19, Shade(parchDark, 0.7f));

        // Rune glyph in the accent color — five stroke patterns keyed by scroll id.
        switch (runeSeed % 5)
        {
            case 0: // ascending chevron (projectile / motion)
                for (int i = 0; i < 3; i++)
                { Set(7 - i, 10 + i, accent); Set(8 + i, 10 + i, accent); }
                Rect(7, 9, 2, 5, accentDark);
                Set(7, 8, accent); Set(8, 8, accent);
                break;
            case 1: // crossed strike (impact)
                for (int i = 0; i < 5; i++)
                { Set(5 + i, 8 + i, accent); Set(9 - i, 8 + i, i == 2 ? Shade(accent, 1.3f) : accent); }
                Set(5, 13, accentDark); Set(9, 13, accentDark);
                break;
            case 2: // jagged bolt (energy)
                Set(9, 7, accent); Set(8, 8, accent); Set(7, 9, accent);
                Rect(6, 10, 4, 1, accent);
                Set(8, 11, accent); Set(7, 12, accent); Set(6, 13, accentDark);
                break;
            case 3: // open ring with a core (aura / ground)
                Rect(6, 8, 4, 1, accent); Rect(6, 13, 4, 1, accent);
                Rect(5, 9, 1, 4, accent); Rect(10, 9, 1, 4, accent);
                Set(7, 10, accentDark); Set(8, 11, accentDark);
                break;
            default: // rooted triangle (form / body)
                Rect(5, 13, 6, 1, accent);
                for (int i = 0; i < 3; i++) { Set(7 - i, 10 + i, accent); Set(8 + i, 10 + i, accent); }
                Set(7, 9, accent); Set(8, 9, accent);
                Set(7, 14, accentDark); Set(8, 14, accentDark);
                break;
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
