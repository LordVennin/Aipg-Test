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

        // --- base pass: flat level-0 floor tiles (nothing ever renders beneath them) ---
        var floorA = new Color(58, 66, 58);
        var floorB = new Color(52, 60, 54);
        for (int y = 0; y < map.Height; y++)
        {
            for (int x = 0; x < map.Width; x++)
            {
                if (map.IsSolid(x, y) || map.GroundLevel(x, y) > 0 || map.Ramp(x, y) != RampDirection.None)
                    continue;
                var screen = camera.WorldToScreen(new NumVec2(x + 0.5f, y + 0.5f));
                if (screen.X < -80 || screen.X > camera.ScreenWidth + 80 ||
                    screen.Y < -80 || screen.Y > camera.ScreenHeight + 80) continue;
                var tint = ((x + y) & 1) == 0 ? floorA : floorB;
                sb.Draw(TextureGen.Diamond, new Vector2(screen.X - 32, screen.Y - 16), tint);
            }
        }

        // --- depth-sorted world objects: elevated terrain, bridges and entities share ---
        // --- ONE painter's list so tall geometry occludes what stands behind/under it ---
        _sorted.Clear();

        // Depth key convention: tiles/decks use (x + y + height*0.6); entities add +0.1 on
        // top of their own surface height so they draw over their floor but under nearer
        // (or higher) geometry — e.g. a bridge deck covers the entity walking beneath it,
        // while an entity ON the deck draws above it.
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
                if (wall > 0)
                {
                    int top = ground + wall;
                    int topPx = top * hpx;
                    var faceTint = new Color(140, 133, 173);
                    _sorted.Add((x + y + 1 + top * 0.001f, batch =>
                        batch.Draw(TextureGen.GetPrismFaces(top),
                            new Vector2(baseScreen.X - 32, baseScreen.Y - topPx), faceTint)));
                    _sorted.Add((x + y + top * 0.6f, batch =>
                        batch.Draw(TextureGen.Diamond,
                            new Vector2(baseScreen.X - 32, baseScreen.Y - 16 - topPx),
                            new Color(84, 80, 104))));
                    continue;
                }

                if (ramp != RampDirection.None)
                {
                    // Transition tile: baked sloped-ramp or stairs sprite (its own skirt
                    // included), anchored at the tile's (x, y) corner at the LOW level.
                    var sprite = TextureGen.GetRampSprite(ramp, map.RampIsStairs(x, y));
                    var corner = camera.WorldToScreen(new NumVec2(x, y), ground);
                    var rampTint = new Color(150, 160, 130);
                    _sorted.Add((x + y + (ground + 0.5f) * 0.6f, batch =>
                        batch.Draw(sprite,
                            new Vector2(corner.X - 32, corner.Y - TextureGen.RampSpriteOffsetY),
                            rampTint)));
                    if (ground > 0)
                        _sorted.Add((x + y + 1 + ground * 0.001f, batch =>
                            batch.Draw(TextureGen.GetPrismFaces(ground),
                                new Vector2(baseScreen.X - 32, baseScreen.Y - ground * hpx),
                                new Color(128, 140, 128))));
                }
                else if (ground > 0)
                {
                    // Elevated ground: top diamond at its level plus prism faces down to
                    // the ground floor (front tiles' tops cover interior faces).
                    int topPx = ground * hpx;
                    var top = ((x + y) & 1) == 0
                        ? new Color(70 + ground * 12, 80 + ground * 10, 68 + ground * 8)
                        : new Color(64 + ground * 12, 74 + ground * 10, 62 + ground * 8);
                    _sorted.Add((x + y + 1 + ground * 0.001f, batch =>
                        batch.Draw(TextureGen.GetPrismFaces(ground),
                            new Vector2(baseScreen.X - 32, baseScreen.Y - topPx),
                            new Color(128, 140, 128))));
                    _sorted.Add((x + y + ground * 0.6f, batch =>
                        batch.Draw(TextureGen.Diamond,
                            new Vector2(baseScreen.X - 32, baseScreen.Y - 16 - topPx), top)));
                }

                if (bridge > 0)
                {
                    // Bridge deck: a plank-toned diamond floating at its level with a thin
                    // lip. The ground below was already drawn (base pass or elevated above),
                    // so entities beneath draw between the two in the depth order.
                    int deckPx = bridge * hpx;
                    float depth = x + y + bridge * 0.6f;
                    var deck = ((x + y) & 1) == 0 ? new Color(122, 96, 62) : new Color(112, 88, 58);
                    _sorted.Add((depth, batch =>
                    {
                        batch.Draw(TextureGen.Pixel,
                            new Rectangle((int)baseScreen.X - 32, (int)baseScreen.Y - deckPx, 64, 6),
                            new Color(70, 54, 36));
                        batch.Draw(TextureGen.Diamond, new Vector2(baseScreen.X - 32, baseScreen.Y - 16 - deckPx), deck);
                    }));
                }
            }
        }

        foreach (var drop in world.Drops.Values)
        {
            var pos = drop.Position;
            var screen = camera.WorldToScreen(pos, drop.Height);
            var item = drop.Item;
            _sorted.Add((pos.X + pos.Y + drop.Height * 0.6f + 0.05f, batch =>
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
            _sorted.Add((pos.X + pos.Y + e.Height * 0.6f + 0.1f, batch =>
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
            _sorted.Add((pos.X + pos.Y + p.Height * 0.6f + 0.1f, batch =>
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
                _sorted.Add((pr.Position.X + pr.Position.Y + pr.Height * 0.6f + 0.1f, batch =>
                    batch.Draw(projSprite, new Vector2(screen.X, screen.Y - 14), null, Color.White, ang,
                        new Vector2(projSprite.Width / 2f, projSprite.Height / 2f), 2f, SpriteEffects.None, 0f)));
            }
            else
            {
                var color = pr.FromPlayer ? new Color(255, 150, 50) : new Color(140, 255, 90);
                _sorted.Add((pr.Position.X + pr.Position.Y + pr.Height * 0.6f + 0.1f, batch =>
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
                _sorted.Add((fx.Position.X + fx.Position.Y + fx.Height * 0.6f + 0.2f, batch =>
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
                _sorted.Add((fx.Position.X + fx.Position.Y + fx.Height * 0.6f + 1.2f, batch =>
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
            _sorted.Add((fx.Position.X + fx.Position.Y + fx.Height * 0.6f + 0.2f, batch =>
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
