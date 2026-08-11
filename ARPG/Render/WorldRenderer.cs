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

    private const int WallHeight = 24;

    public WorldRenderer(GameData data, Core.GameSettings settings)
    {
        _data = data;
        _settings = settings;
    }

    public void Draw(SpriteBatch sb, IsoCamera camera, ClientWorld world)
    {
        var map = world.Map;
        if (map == null) return;
        DropLabelRects.Clear();

        // --- floor tiles ---
        var floorA = new Color(58, 66, 58);
        var floorB = new Color(52, 60, 54);
        for (int y = 0; y < map.Height; y++)
        {
            for (int x = 0; x < map.Width; x++)
            {
                if (map.Tile(x, y) == TileType.Wall) continue;
                var screen = camera.WorldToScreen(new NumVec2(x + 0.5f, y + 0.5f));
                if (screen.X < -80 || screen.X > camera.ScreenWidth + 80 ||
                    screen.Y < -80 || screen.Y > camera.ScreenHeight + 80) continue;
                var tint = ((x + y) & 1) == 0 ? floorA : floorB;
                sb.Draw(TextureGen.Diamond, new Vector2(screen.X - 32, screen.Y - 16), tint);
            }
        }

        // --- depth-sorted world objects ---
        _sorted.Clear();

        for (int y = 0; y < map.Height; y++)
        {
            for (int x = 0; x < map.Width; x++)
            {
                if (map.Tile(x, y) != TileType.Wall) continue;
                var screen = camera.WorldToScreen(new NumVec2(x + 0.5f, y + 0.5f));
                if (screen.X < -80 || screen.X > camera.ScreenWidth + 80 ||
                    screen.Y < -100 || screen.Y > camera.ScreenHeight + 100) continue;
                float depth = x + y;
                _sorted.Add((depth, batch =>
                {
                    // Simple wall block: dark side slab + lighter top diamond.
                    batch.Draw(TextureGen.Diamond, new Vector2(screen.X - 32, screen.Y - 16), new Color(30, 30, 38));
                    batch.Draw(TextureGen.Pixel,
                        new Rectangle((int)screen.X - 32, (int)screen.Y - 16 - WallHeight + 16, 64, WallHeight),
                        new Color(44, 42, 56));
                    batch.Draw(TextureGen.Diamond, new Vector2(screen.X - 32, screen.Y - 16 - WallHeight),
                        new Color(84, 80, 104));
                }));
            }
        }

        foreach (var drop in world.Drops.Values)
        {
            var pos = drop.Position;
            var screen = camera.WorldToScreen(pos);
            var item = drop.Item;
            _sorted.Add((pos.X + pos.Y - 0.3f, batch =>
            {
                var color = RarityColor(item.Rarity);
                batch.Draw(TextureGen.Diamond,
                    new Rectangle((int)screen.X - 10, (int)screen.Y - 5, 20, 10), color);
            }));
        }

        long animClock = Environment.TickCount64; // visual-only animation timer
        foreach (var e in world.Enemies.Values)
        {
            var pos = e.Position;
            var screen = camera.WorldToScreen(pos);
            var def = e.Def;
            var color = ParseColor(def?.Color, new Color(190, 60, 60));
            float size = (def?.Radius ?? 0.4f) * 90f;
            var frames = SpriteGen.GetEnemyFrames(def);
            _sorted.Add((pos.X + pos.Y, batch =>
            {
                int barY;
                if (frames != null)
                {
                    // Procedural pixel sprite: shamble animation while chasing/attacking.
                    bool animated = e.State is (byte)Server.EnemyState.Chase or (byte)Server.EnemyState.Attack;
                    int frame = animated ? (int)((animClock / 170 + e.Id) % frames.Length) : 0;
                    var tex = frames[frame];
                    const int scale = 2;
                    int w = tex.Width * scale, h = tex.Height * scale;
                    batch.Draw(TextureGen.Circle32,
                        new Rectangle((int)(screen.X - size / 2), (int)(screen.Y - size / 4), (int)size, (int)(size / 2)),
                        new Color(0, 0, 0, 90)); // shadow
                    batch.Draw(tex, new Rectangle((int)screen.X - w / 2, (int)screen.Y - h + 6, w, h), null,
                        Color.White, 0f, Vector2.Zero,
                        e.FacingLeft ? SpriteEffects.FlipHorizontally : SpriteEffects.None, 0f);
                    barY = (int)screen.Y - h + 2;
                }
                else
                {
                    DrawUnitToken(batch, screen, size, color);
                    barY = (int)screen.Y - (int)size - 26;
                }
                if (_settings.ShowEnemyHealthBars)
                {
                    float frac = e.MaxHealth > 0 ? Math.Clamp(e.Health / e.MaxHealth, 0f, 1f) : 0;
                    var bar = new Rectangle((int)screen.X - 16, barY, 32, 4);
                    batch.Draw(TextureGen.Pixel, bar, new Color(20, 20, 20, 200));
                    batch.Draw(TextureGen.Pixel, new Rectangle(bar.X, bar.Y, (int)(bar.Width * frac), bar.Height),
                        new Color(200, 50, 50));
                }
            }));
        }

        foreach (var p in world.Players.Values)
        {
            var pos = p.Position;
            var screen = camera.WorldToScreen(pos);
            var color = p.IsLocal ? new Color(90, 170, 255) : new Color(110, 235, 140);
            if (!p.Alive) color = new Color(80, 80, 90);
            if (p.DodgeTimeLeft > 0) color = Color.Lerp(color, Color.White, 0.65f); // dash flash / i-frame hint
            var name = p.Name ?? "?";
            _sorted.Add((pos.X + pos.Y, batch =>
            {
                DrawUnitToken(batch, screen, 34f, color);

                // Held weapon (or a small facing dot when unarmed), rotated toward the aim.
                var screenDir = new Vector2(p.Facing.X - p.Facing.Y, (p.Facing.X + p.Facing.Y) * 0.5f);
                if (screenDir.LengthSquared() < 0.001f) screenDir = new Vector2(1, 0);
                screenDir.Normalize();
                var weaponBase = p.WeaponBaseId != null ? _data.Items.GetValueOrDefault(p.WeaponBaseId) : null;
                var weaponTex = SpriteGen.GetWeaponSprite(weaponBase);
                if (weaponTex != null)
                {
                    float angle = MathF.Atan2(screenDir.Y, screenDir.X);
                    var hand = screen + screenDir * 12f + new Vector2(0, -16);
                    batch.Draw(weaponTex, hand, null, Color.White, angle,
                        new Vector2(2, weaponTex.Height / 2f), 2f, SpriteEffects.None, 0f);
                }
                else
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
            var screen = camera.WorldToScreen(pr.Position);
            var color = pr.FromPlayer ? new Color(255, 150, 50) : new Color(140, 255, 90);
            _sorted.Add((pr.Position.X + pr.Position.Y, batch =>
                batch.Draw(TextureGen.Circle32, new Rectangle((int)screen.X - 6, (int)screen.Y - 6 - 14, 12, 12), color)));
        }

        foreach (var fx in world.Effects)
        {
            var screen = camera.WorldToScreen(fx.Position);
            float t = 1f - fx.TimeLeft / fx.Duration;
            float radiusPx = fx.Radius * 2f * IsoCamera.HalfTileW * (0.4f + 0.6f * t);
            byte alpha = (byte)(180 * (1f - t));
            var color = fx.Kind switch
            {
                "burst" => new Color((byte)170, (byte)90, (byte)255, alpha),
                "slam" => new Color((byte)230, (byte)200, (byte)90, alpha),
                "melee" => new Color((byte)255, (byte)255, (byte)255, alpha),
                _ => new Color((byte)255, (byte)120, (byte)60, alpha),
            };
            _sorted.Add((fx.Position.X + fx.Position.Y + 0.5f, batch =>
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
            var screen = camera.WorldToScreen(drop.Position);
            string label = drop.Item.DisplayName(_data);
            var size = labelFont.MeasureString(label);
            var rect = new Rectangle((int)(screen.X - size.X / 2) - 4, (int)(screen.Y - 30), (int)size.X + 8, (int)size.Y + 4);
            bool hover = false; // filled by caller via DropLabelRects; draw hover tint next frame if needed
            sb.Draw(TextureGen.Pixel, rect, new Color(0, 0, 0, hover ? 220 : 170));
            sb.DrawString(labelFont, label, new Vector2(rect.X + 4, rect.Y + 2), RarityColor(drop.Item.Rarity));
            DropLabelRects.Add((rect, drop.DropId));
        }

        // --- floating damage numbers (from server DamageEvents; toggle in Options) ---
        if (_settings.ShowDamageNumbers)
        {
            var dmgFont = FontManager.GetBold(15);
            foreach (var fn in world.FloatingNumbers)
            {
                float t = fn.Age / Net.FloatingNumber.Lifetime;
                var screen = camera.WorldToScreen(fn.Position);
                screen.Y -= 42 + 34 * t; // rise as it ages
                byte alpha = (byte)(255 * (1f - t * t));
                var color = fn.TargetIsPlayer
                    ? new Color((byte)255, (byte)80, (byte)80, alpha)
                    : (Skills.DamageKind)fn.Kind switch
                    {
                        Skills.DamageKind.Fire => new Color((byte)255, (byte)150, (byte)60, alpha),
                        Skills.DamageKind.Cold => new Color((byte)120, (byte)190, (byte)255, alpha),
                        Skills.DamageKind.Lightning => new Color((byte)250, (byte)235, (byte)120, alpha),
                        Skills.DamageKind.Arcane => new Color((byte)200, (byte)130, (byte)255, alpha),
                        _ => new Color((byte)245, (byte)240, (byte)230, alpha),
                    };
                string text = $"{MathF.Max(1, MathF.Round(fn.Amount)):0}";
                var size = dmgFont.MeasureString(text);
                sb.DrawString(dmgFont, text, new Vector2(screen.X - size.X / 2, screen.Y), color);
            }
        }
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
