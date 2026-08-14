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

    /// <summary>Friendly NPC sprites (the test merchant). Cached per type id.</summary>
    public static Texture2D GetNpcSprite(string typeId)
    {
        if (_device == null || string.IsNullOrEmpty(typeId)) return null;
        string key = "npc:" + typeId;
        if (_cache.TryGetValue(key, out var cached)) return cached[0];
        var tex = DrawMerchant();
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
        string key = warrior ? "summon:skeleton_warrior" : "summon:skeleton_archer";
        if (_cache.TryGetValue(key, out var cached)) return cached;
        var frames = new[]
        {
            DrawSummonFrame(warrior, 0), DrawSummonFrame(warrior, 1), DrawSummonFrame(warrior, 2),
        };
        _cache[key] = frames;
        return frames;
    }

    /// <summary>Standing frame only (HUD cards and other static uses).</summary>
    public static Texture2D GetSummonSprite(string skillId = null) => GetSummonFrames(skillId)?[0];

    private static Texture2D DrawSummonFrame(bool warrior, int frame)
    {
        const int w = 16, h = 22;
        var px = new Color[w * h];
        void Set(int x, int y, Color c) { if (x >= 0 && x < w && y >= 0 && y < h) px[y * w + x] = c; }
        void Rect(int x0, int y0, int x1, int y1, Color c)
        { for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) Set(x, y, c); }

        var bone = new Color(226, 222, 204);
        var boneDark = new Color(168, 162, 142);
        var socket = new Color(30, 28, 26);

        // Stride: one leg lifts 2px while the other plants, the torso bobs 1px and
        // the arms counter-swing 1px — a real gait instead of a glide.
        int leftUp = frame == 1 ? 2 : 0;
        int rightUp = frame == 2 ? 2 : 0;
        int bob = frame == 0 ? 0 : 1;
        int armSwing = frame == 1 ? 1 : frame == 2 ? -1 : 0;

        // Skull
        Rect(5, 0 + bob, 10, 5 + bob, bone);
        Set(5, 0 + bob, Color.Transparent); Set(10, 0 + bob, Color.Transparent);
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
        const int w = 11, h = 7;
        var px = new Color[w * h];
        void Set(int x, int y, Color c) { if (x >= 0 && x < w && y >= 0 && y < h) px[y * w + x] = c; }

        var glass = new Color(190, 235, 255);
        var glassDeep = new Color(110, 170, 235);
        var edge = new Color(70, 120, 200);
        var glint = Color.White;

        // Chunky angular splinter: a broken-off wedge, thick then snapping to a point.
        for (int x = 1; x <= 9; x++)
        {
            int hw = x <= 3 ? 2 : x <= 6 ? 1 : 0;
            for (int y = 3 - hw; y <= 3 + hw; y++)
                Set(x, y, y == 3 ? glass : y < 3 ? glassDeep : edge);
        }
        // Fracture notch at the back and a glint near the tip.
        Set(1, 2, edge); Set(0, 3, edge); Set(1, 4, Color.Transparent);
        Set(8, 3, glint); Set(9, 3, glint);
        Set(4, 1, glassDeep); Set(5, 5, edge);

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
                else // taller grass clump
                {
                    Init(12, 11);
                    var grass = new Color(90, 144, 70); var lite = Shade(grass, 1.25f); var dark = Shade(grass, 0.78f);
                    VLine(2, 5, 5, grass); VLine(4, 2, 8, lite); VLine(6, 1, 9, grass);
                    VLine(8, 3, 7, dark); VLine(9, 6, 4, grass);
                    Set(3, 4, dark); Set(5, 2, lite); Set(7, 2, lite);
                    Set(1, 7, grass); Set(10, 5, dark);
                }
                break;
            case "forest:bigtree": // large 2x2-tile canopy trees (generated landmarks)
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
