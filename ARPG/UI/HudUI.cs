using FontStashSharp;
using ARPG.Core;
using ARPG.Data;
using ARPG.Net;
using ARPG.Render;
using ARPG.Skills;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ARPG.UI;

/// <summary>In-game HUD: health orb, hotbar with cooldowns, XP bar, server messages.</summary>
public class HudUI
{
    private readonly GameData _data;
    private readonly GameClient _client;
    private readonly GameSettings _settings;
    private readonly List<(string text, float timeLeft)> _messages = new();

    public HudUI(GameData data, GameClient client, GameSettings settings)
    {
        _data = data;
        _client = client;
        _settings = settings;
    }

    public void AddMessage(string text) => _messages.Insert(0, (text, 4f));

    /// <summary>The summon skill the command key currently drives (set by PlayScreen
    /// each frame; highlighted in the summon roster beside the mana orb).</summary>
    public string FocusedSummonSkillId;

    public void Update(float dt)
    {
        for (int i = _messages.Count - 1; i >= 0; i--)
        {
            var (text, t) = _messages[i];
            t -= dt;
            if (t <= 0) _messages.RemoveAt(i);
            else _messages[i] = (text, t);
        }
    }

    public void Draw(SpriteBatch sb, Point screen, InputManager input, IReadOnlyDictionary<string, float> cooldownEnds, float clientTime)
    {
        var me = _client.World.Me;
        var character = _client.World.MyCharacter;
        if (me == null || character == null) return;

        // --- zone banner (top center): where the group is in the campaign loop ---
        if (_client.World.Map?.Kind == World.MapKind.Defense)
        {
            // Defense run: phase + wave count, and the wagon's own big health bar.
            var zoneFont = FontManager.GetBold(16);
            var subFont = FontManager.Get(12);
            var w = _client.World;
            string zoneName = "The Caravan Stand";
            string zoneSub = w.DefensePhase switch
            {
                0 => $"build phase — wave {w.DefenseWave} / {w.DefenseWavesTotal} next · ready up at the workbench",
                1 => $"wave {w.DefenseWave} / {w.DefenseWavesTotal} — defend the wagon!",
                2 => "the wagon stands — take the door home",
                _ => "the wagon is lost...",
            };
            var znSize = zoneFont.MeasureString(zoneName);
            sb.DrawString(zoneFont, zoneName, new Vector2(screen.X / 2f - znSize.X / 2, 26), new Color(230, 215, 165));
            var zsSize = subFont.MeasureString(zoneSub);
            sb.DrawString(subFont, zoneSub, new Vector2(screen.X / 2f - zsSize.X / 2, 46),
                w.DefensePhase == 1 ? new Color(230, 170, 120) : new Color(170, 162, 140));
            // Wagon health, front and center under the banner.
            float wagonFrac = Math.Clamp(w.WagonHealth / w.WagonMaxHealth, 0f, 1f);
            var wagonBar = new Rectangle(screen.X / 2 - 130, 64, 260, 10);
            sb.Draw(TextureGen.Pixel, wagonBar, new Color(20, 18, 16, 220));
            sb.Draw(TextureGen.Pixel,
                new Rectangle(wagonBar.X, wagonBar.Y, (int)(wagonBar.Width * wagonFrac), wagonBar.Height),
                wagonFrac > 0.5f ? new Color(180, 150, 90) : wagonFrac > 0.25f ? new Color(220, 160, 70) : new Color(220, 80, 60));
            sb.DrawString(FontManager.Get(11), $"wagon {w.WagonHealth:0}/{w.WagonMaxHealth:0}",
                new Vector2(wagonBar.Right + 8, wagonBar.Y - 2), new Color(200, 180, 140));
            // The run's build purse, left of the bar (kills and cleared waves feed it).
            string supText = $"supplies {w.MySupplies}";
            var supSize = FontManager.Get(11).MeasureString(supText);
            sb.DrawString(FontManager.Get(11), supText,
                new Vector2(wagonBar.X - supSize.X - 8, wagonBar.Y - 2), new Color(240, 200, 90));
        }
        else if (_client.World.Map?.Kind != World.MapKind.Arena)
        {
            var zoneFont = FontManager.GetBold(16);
            var subFont = FontManager.Get(12);
            string zoneName = _client.World.Map?.Kind == World.MapKind.Hub
                ? "The Sanctum"
                : $"Mirewood Depths {_client.World.ZoneMapIndex} / 3";
            string zoneSub = _client.World.Map?.Kind == World.MapKind.Hub
                ? (_client.World.ZoneLoop > 1 ? $"expedition {_client.World.ZoneLoop} awaits" : "gear up, then take the door")
                : $"enemy level {_client.World.ZoneEnemyLevel}" +
                  (_client.World.ZoneReadyCount > 0
                      ? $"  ·  {_client.World.ZoneReadyCount}/{Math.Max(1, _client.World.ZoneAlivePlayers)} at the door"
                      : "");
            var znSize = zoneFont.MeasureString(zoneName);
            sb.DrawString(zoneFont, zoneName, new Vector2(screen.X / 2f - znSize.X / 2, 26), new Color(230, 215, 165));
            var zsSize = subFont.MeasureString(zoneSub);
            sb.DrawString(subFont, zoneSub, new Vector2(screen.X / 2f - zsSize.X / 2, 46), new Color(170, 162, 140));
        }

        // --- health orb (bottom left) ---
        int orbSize = 96;
        var orbRect = new Rectangle(18, screen.Y - orbSize - 18, orbSize, orbSize);
        sb.Draw(TextureGen.Circle32, orbRect, new Color(40, 14, 14));
        float frac = me.MaxHealth > 0 ? Math.Clamp(me.Health / me.MaxHealth, 0f, 1f) : 0f;
        if (frac > 0)
        {
            int srcY = (int)(32 * (1 - frac));
            var src = new Rectangle(0, srcY, 32, 32 - srcY);
            var dst = new Rectangle(orbRect.X, orbRect.Y + (int)(orbSize * (1 - frac)), orbSize, (int)(orbSize * frac));
            sb.Draw(TextureGen.Circle32, dst, src, new Color(190, 40, 40));
        }
        var font = FontManager.GetBold(15);
        string hpText = $"{me.Health:0}/{me.MaxHealth:0}";
        var hpSize = font.MeasureString(hpText);
        sb.DrawString(font, hpText, new Vector2(orbRect.Center.X - hpSize.X / 2, orbRect.Center.Y - hpSize.Y / 2), Color.White);

        // Energy Shield: a cyan bar capping the health orb (only when the build has any).
        float maxEs = _client.World.MyStats.MaxEnergyShield;
        if (maxEs > 0)
        {
            float esFrac = Math.Clamp(me.EnergyShield / maxEs, 0f, 1f);
            var esBar = new Rectangle(orbRect.X, orbRect.Y - 10, orbRect.Width, 6);
            sb.Draw(TextureGen.Pixel, esBar, new Color(14, 30, 40, 220));
            sb.Draw(TextureGen.Pixel, new Rectangle(esBar.X, esBar.Y, (int)(esBar.Width * esFrac), esBar.Height),
                new Color(90, 200, 235));
            sb.DrawString(FontManager.Get(11), $"{me.EnergyShield:0}/{maxEs:0}",
                new Vector2(esBar.Right + 6, esBar.Y - 3), new Color(140, 210, 235));
        }

        // --- mana orb (bottom right) ---
        // Summons RESERVE maximum mana: the reserved band renders as a dim violet cap
        // at the orb's top and the text shows the usable pool.
        float maxMana = _client.World.MyStats.MaxMana;
        float reserved = Math.Clamp(me.ManaReserved, 0f, maxMana);
        float usableMax = MathF.Max(0f, maxMana - reserved);
        var manaRect = new Rectangle(screen.X - orbSize - 18, screen.Y - orbSize - 18, orbSize, orbSize);
        sb.Draw(TextureGen.Circle32, manaRect, new Color(12, 16, 42));
        float manaFrac = maxMana > 0 ? Math.Clamp(me.Mana / maxMana, 0f, 1f) : 0f;
        if (manaFrac > 0)
        {
            int mSrcY = (int)(32 * (1 - manaFrac));
            var mSrc = new Rectangle(0, mSrcY, 32, 32 - mSrcY);
            var mDst = new Rectangle(manaRect.X, manaRect.Y + (int)(orbSize * (1 - manaFrac)), orbSize, (int)(orbSize * manaFrac));
            sb.Draw(TextureGen.Circle32, mDst, mSrc, new Color(50, 90, 220));
        }
        if (maxMana > 0 && reserved > 0)
        {
            float resFrac = Math.Clamp(reserved / maxMana, 0f, 1f);
            int rH = (int)(32 * resFrac);
            var rSrc = new Rectangle(0, 0, 32, rH);
            var rDst = new Rectangle(manaRect.X, manaRect.Y, orbSize, (int)(orbSize * resFrac));
            sb.Draw(TextureGen.Circle32, rDst, rSrc, new Color(70, 50, 110, 230));
        }
        string manaText = $"{me.Mana:0}/{usableMax:0}";
        var manaSize = font.MeasureString(manaText);
        sb.DrawString(font, manaText, new Vector2(manaRect.Center.X - manaSize.X / 2, manaRect.Center.Y - manaSize.Y / 2), Color.White);
        if (reserved > 0)
            sb.DrawString(FontManager.Get(11), $"{reserved:0} reserved",
                new Vector2(manaRect.X - 4, manaRect.Y - 14), new Color(170, 150, 220));

        // --- summon roster (left of the mana orb): one card per summon skill with at ---
        // least one LIVING minion, showing count / limit; the focused card (the one the
        // command key drives, cycled with Tab) gets a lit border. Skills that are merely
        // learned show nothing — a melee player without summons never sees this UI.
        var summonSkills = character.Skills
            .Where(s => _data.Skills.GetValueOrDefault(s.SkillId)?.Archetype == SkillArchetype.Summon)
            .Where(s => _client.World.Summons.Values.Any(su =>
                su.OwnerId == _client.World.MyPlayerId && su.SkillId == s.SkillId))
            .ToList();
        if (summonSkills.Count > 0)
        {
            var countFont = FontManager.GetBold(13);
            var hintFont2 = FontManager.Get(11);
            int cardW = 44, cardH = 52, cardGap = 6;
            int cx = manaRect.X - 54 - summonSkills.Count * (cardW + cardGap);
            int cy = screen.Y - cardH - 20;
            foreach (var learnedSummon in summonSkills)
            {
                var sDef = _data.Skills[learnedSummon.SkillId];
                int active = _client.World.Summons.Values.Count(su =>
                    su.OwnerId == _client.World.MyPlayerId && su.SkillId == learnedSummon.SkillId);
                int limit = sDef.SummonLimit + _client.World.MyStats.SummonLimitBonus;
                bool focused = learnedSummon.SkillId == FocusedSummonSkillId;
                var card = new Rectangle(cx, cy, cardW, cardH);
                sb.Draw(TextureGen.Pixel, card, new Color(18, 20, 26, 220));
                var borderC = focused ? new Color(190, 225, 160) : new Color(70, 70, 60);
                sb.Draw(TextureGen.Pixel, new Rectangle(card.X, card.Y, card.Width, 2), borderC);
                sb.Draw(TextureGen.Pixel, new Rectangle(card.X, card.Bottom - 2, card.Width, 2), borderC);
                sb.Draw(TextureGen.Pixel, new Rectangle(card.X, card.Y, 2, card.Height), borderC);
                sb.Draw(TextureGen.Pixel, new Rectangle(card.Right - 2, card.Y, 2, card.Height), borderC);
                var minTex = SpriteGen.GetSummonSprite(learnedSummon.SkillId);
                if (minTex != null)
                    sb.Draw(minTex, new Rectangle(card.Center.X - minTex.Width / 2 - 6,
                        card.Y + 3, minTex.Width, minTex.Height + 8), Color.White);
                string cnt = $"{active}/{limit}";
                var cntSize = countFont.MeasureString(cnt);
                sb.DrawString(countFont, cnt,
                    new Vector2(card.Center.X - cntSize.X / 2, card.Bottom - 17),
                    active > 0 ? new Color(180, 230, 180) : new Color(130, 126, 116));
                cx += cardW + cardGap;
            }
            if (summonSkills.Count > 1)
            {
                string cycleHint = $"{input.Bindings[InputAction.CycleSummonFocus].Display()} switch";
                sb.DrawString(hintFont2, cycleHint,
                    new Vector2(manaRect.X - 54 - summonSkills.Count * (cardW + cardGap), cy - 14),
                    new Color(140, 136, 124));
            }
        }

        // --- potion flasks: health beside the health orb, mana beside the mana orb ---
        // The bottles ARE the equipped flask ITEMS: fill = the item's remaining charges
        // (never regenerates — the sanctum fountain refills), border pulses while the
        // restore tick runs. No flask equipped draws a dimmed empty bottle.
        (int charges, int max) EquippedFlaskCharges(bool healthKind)
        {
            foreach (var slot in new[] { Items.EquipSlot.Flask1, Items.EquipSlot.Flask2 })
            {
                var it = character.Equipment.GetValueOrDefault(slot);
                var b = it?.GetBase(_data);
                if (b is not { Category: Items.ItemCategory.Flask }) continue;
                if (healthKind ? b.FlaskHeal <= 0 : b.FlaskMana <= 0) continue;
                return (it.FlaskCharges, Math.Max(1, b.FlaskChargesMax));
            }
            return (0, 0); // none equipped
        }
        void DrawFlask(int fx0, int fy0, bool healthKind, float secondsLeft, string keyHint)
        {
            var (charges, maxCharges) = EquippedFlaskCharges(healthKind);
            bool equipped = maxCharges > 0;
            const int fw = 26, fh = 40;
            var body = new Rectangle(fx0, fy0 + 8, fw, fh - 8);
            var neck = new Rectangle(fx0 + fw / 2 - 5, fy0, 10, 10);
            var glass = equipped ? new Color(26, 30, 40, 235) : new Color(22, 24, 30, 160);
            sb.Draw(TextureGen.Pixel, body, glass);
            sb.Draw(TextureGen.Pixel, neck, glass);
            var liquid = healthKind ? new Color(198, 52, 52) : new Color(66, 108, 226);
            int inner = body.Height - 6;
            if (equipped)
            {
                int fillH = inner * charges / maxCharges;
                if (fillH > 0)
                    sb.Draw(TextureGen.Pixel,
                        new Rectangle(body.X + 3, body.Bottom - 3 - fillH, body.Width - 6, fillH), liquid);
                for (int c = 1; c < maxCharges; c++)
                    sb.Draw(TextureGen.Pixel,
                        new Rectangle(body.X + 3, body.Bottom - 3 - inner * c / maxCharges,
                            body.Width - 6, 1), new Color(0, 0, 0, 120));
            }
            var borderC = equipped ? new Color(90, 88, 70) : new Color(60, 58, 50);
            if (secondsLeft > 0)
                borderC = Color.Lerp(borderC,
                    healthKind ? new Color(255, 130, 115) : new Color(150, 180, 255),
                    0.5f + 0.5f * MathF.Sin(Environment.TickCount64 * 0.012f));
            sb.Draw(TextureGen.Pixel, new Rectangle(body.X, body.Y, body.Width, 2), borderC);
            sb.Draw(TextureGen.Pixel, new Rectangle(body.X, body.Bottom - 2, body.Width, 2), borderC);
            sb.Draw(TextureGen.Pixel, new Rectangle(body.X, body.Y, 2, body.Height), borderC);
            sb.Draw(TextureGen.Pixel, new Rectangle(body.Right - 2, body.Y, 2, body.Height), borderC);
            sb.Draw(TextureGen.Pixel, new Rectangle(neck.X, neck.Y, neck.Width, 2), borderC);
            var hintFont3 = FontManager.Get(11);
            var hSize = hintFont3.MeasureString(keyHint);
            sb.DrawString(hintFont3, keyHint,
                new Vector2(fx0 + fw / 2f - hSize.X / 2, fy0 + fh + 2), new Color(150, 146, 132));
        }
        DrawFlask(orbRect.Right + 8, orbRect.Bottom - 56, healthKind: true,
            me.PotionHealSecondsLeft, input.Bindings[InputAction.HealthPotion].Display());
        DrawFlask(manaRect.X - 34, manaRect.Bottom - 56, healthKind: false,
            me.PotionManaSecondsLeft, input.Bindings[InputAction.ManaPotion].Display());

        // Character level + name above the orb
        var nameFont = FontManager.Get(14);
        sb.DrawString(nameFont, $"{character.Name}  ·  Level {character.Level}", new Vector2(20, orbRect.Y - 22), new Color(220, 210, 180));

        // --- player list + pings (bottom left, above the health orb; Options toggle) ---
        if (_settings.ShowPlayerList)
        {
            var listFont = FontManager.Get(13);
            var players = _client.World.Players.Values.OrderBy(p => p.Id).ToList();
            int ly = orbRect.Y - 40 - (players.Count - 1) * 18;
            foreach (var p in players)
            {
                string line = $"{p.Name}  {p.PingMs} ms";
                var color = p.IsLocal ? new Color(150, 200, 255) : new Color(180, 220, 180);
                if (!p.Alive) color = new Color(120, 115, 110);
                sb.DrawString(listFont, line, new Vector2(20, ly), color);
                ly += 18;
            }
        }

        // --- hotbar (bottom center) ---
        int slotSize = 54, gap = 8;
        int totalW = character.Hotbar.Length * slotSize + (character.Hotbar.Length - 1) * gap;
        int hbX = screen.X / 2 - totalW / 2;
        int hbY = screen.Y - slotSize - 16;
        for (int i = 0; i < character.Hotbar.Length; i++)
        {
            var rect = new Rectangle(hbX + i * (slotSize + gap), hbY, slotSize, slotSize);
            sb.Draw(TextureGen.Pixel, rect, new Color(20, 20, 28, 230));
            string skillId = character.Hotbar[i];
            var def = skillId != null ? _data.Skills.GetValueOrDefault(skillId) : null;
            if (def != null)
            {
                SkillMenuUI.DrawSkillIcon(sb, new Rectangle(rect.X + 6, rect.Y + 4, slotSize - 12, slotSize - 12), def);
                var abbrevFont = FontManager.GetBold(13);
                string abbrev = string.Concat(def.Name.Split(' ').Select(w => w[0]));
                var aSize = abbrevFont.MeasureString(abbrev);
                sb.DrawString(abbrevFont, abbrev, new Vector2(rect.Center.X - aSize.X / 2, rect.Center.Y - aSize.Y / 2 - 2), Color.Black);

                // Cooldown overlay
                if (skillId != null && cooldownEnds.TryGetValue(skillId, out float readyAt) && readyAt > clientTime)
                {
                    var learned = character.GetSkill(skillId);
                    var stats = SkillMath.Compute(_data, def, learned?.Level ?? 1,
                        learned?.ScrollDefinitions(_data) ?? Enumerable.Empty<ScrollDefinition>(), _client.World.MyStats);
                    float cdFrac = Math.Clamp((readyAt - clientTime) / MathF.Max(0.05f, stats.Cooldown), 0f, 1f);
                    int h = (int)(slotSize * cdFrac);
                    sb.Draw(TextureGen.Pixel, new Rectangle(rect.X, rect.Bottom - h, slotSize, h), new Color(0, 0, 0, 160));
                }
            }
            var outline = new Color(95, 88, 62);
            sb.Draw(TextureGen.Pixel, new Rectangle(rect.X, rect.Y, rect.Width, 2), outline);
            sb.Draw(TextureGen.Pixel, new Rectangle(rect.X, rect.Bottom - 2, rect.Width, 2), outline);
            sb.Draw(TextureGen.Pixel, new Rectangle(rect.X, rect.Y, 2, rect.Height), outline);
            sb.Draw(TextureGen.Pixel, new Rectangle(rect.Right - 2, rect.Y, 2, rect.Height), outline);

            string key = i switch
            {
                0 => input.Bindings[InputAction.PrimaryAttack].Display(),
                1 => input.Bindings[InputAction.Skill1].Display(),
                2 => input.Bindings[InputAction.Skill2].Display(),
                3 => input.Bindings[InputAction.Skill3].Display(),
                _ => input.Bindings[InputAction.Skill4].Display(),
            };
            sb.DrawString(FontManager.Get(11), key, new Vector2(rect.X + 4, rect.Y + 2), new Color(220, 220, 220));
        }

        // --- character XP bar (bottom edge) ---
        float xpFrac = Math.Clamp(character.Experience / character.XpToNextLevel(), 0f, 1f);
        sb.Draw(TextureGen.Pixel, new Rectangle(0, screen.Y - 5, screen.X, 5), new Color(25, 25, 30));
        sb.Draw(TextureGen.Pixel, new Rectangle(0, screen.Y - 5, (int)(screen.X * xpFrac), 5), new Color(190, 160, 70));

        // --- messages ---
        var msgFont = FontManager.Get(15);
        int my = hbY - 34;
        foreach (var (text, timeLeft) in _messages.Take(4))
        {
            var size = msgFont.MeasureString(text);
            var alpha = (byte)Math.Clamp(timeLeft * 255, 0, 255);
            sb.DrawString(msgFont, text, new Vector2(screen.X / 2f - size.X / 2, my), new Color((byte)255, (byte)190, (byte)150, alpha));
            my -= 22;
        }

        // --- key hints (top center) ---
        var hintFont = FontManager.Get(12);
        string hints = $"{input.Bindings[InputAction.Inventory].Display()} Inventory · " +
                       $"{input.Bindings[InputAction.SkillMenu].Display()} Skills · " +
                       $"{input.Bindings[InputAction.SkillTree].Display()} Tree · " +
                       $"{input.Bindings[InputAction.Interact].Display()} Pickup · " +
                       $"{input.Bindings[InputAction.DebugMenu].Display()} Debug · " +
                       $"{input.Bindings[InputAction.Pause].Display()} Menu";
        var hintSize = hintFont.MeasureString(hints);
        sb.DrawString(hintFont, hints, new Vector2(screen.X / 2f - hintSize.X / 2, 8), new Color(140, 136, 124));
    }
}

/// <summary>F1 debug menu: developer commands (executed server-side) plus live diagnostics.</summary>
public class DebugUI
{
    public bool Open;
    private readonly GameClient _client;
    private readonly Panel _panel;
    public int Fps;
    public bool IsHost;
    public int HostPort;

    public DebugUI(GameClient client)
    {
        _client = client;
        _panel = new Panel { Bounds = new Rectangle(8, 30, 250, 672), Background = new Color(16, 16, 22, 235) };
        var commands = new (string label, string cmd, string arg)[]
        {
            ("Spawn Enemy", "spawn_enemy", ""),
            ("Give Random Mace", "give_mace", ""),
            ("Give Random Staff", "give_staff", ""),
            ("Give Random Shield", "give_shield", ""),
            ("Give Random Rare", "give_rare", ""),
            ("Give 10-Modifier Item", "give_10mod", ""),
            ("Give Skill Scroll", "give_scroll", ""),
            ("Give Enchant Scrolls", "give_enchant", ""),
            ("Drop All Scrolls", "drop_scrolls", ""),
            ("Drop Pets", "drop_pets", ""),
            ("Skip To Final Wave", "wave_skip", ""),
            ("Grant Skill XP", "skill_xp", ""),
            ("Grant Character XP", "char_xp", ""),
            ("Kill Nearby Enemies", "kill_nearby", ""),
            ("Full Heal", "heal", ""),
        };
        int y = _panel.Bounds.Y + 34;
        foreach (var (label, cmd, arg) in commands)
        {
            _panel.Children.Add(new Button(label, new Rectangle(_panel.Bounds.X + 10, y, 230, 28),
                () => _client.SendDebugCommand(cmd, arg)) { FontSize = 14 });
            y += 33;
        }
        // Local weather test cycler: Map default -> Rain -> Snow -> Wind -> Off ->
        // back to Map. Client-side only (weather is a MAP attribute; this forces a
        // look for testing). PlayScreen wires the hooks to the world renderer.
        _weatherButton = new Button("Weather: Map", new Rectangle(_panel.Bounds.X + 10, y, 230, 28),
            () =>
            {
                if (CycleWeatherOverride == null) return;
                string next = CycleWeatherOverride();
                _weatherButton.Text = $"Weather: {(next == null ? "Map" : char.ToUpper(next[0]) + next[1..])}";
            }) { FontSize = 14 };
        _panel.Children.Add(_weatherButton);
        _commandRows = commands.Length + 1;
    }

    /// <summary>Advances the renderer's weather override and returns the new value
    /// (null = follow the map). Wired by PlayScreen.</summary>
    public Func<string> CycleWeatherOverride;
    private readonly Button _weatherButton;
    private readonly int _commandRows;

    public bool Contains(Point p) => Open && _panel.Bounds.Contains(p);

    public void Update(InputManager input)
    {
        if (!Open) return;
        _panel.Update(input);
    }

    public void Draw(SpriteBatch sb)
    {
        if (!Open) return;
        _panel.Draw(sb);
        sb.DrawString(FontManager.GetBold(15), "Debug (F1)", new Vector2(_panel.Bounds.X + 10, _panel.Bounds.Y + 8), new Color(255, 200, 120));

        var font = FontManager.Get(13);
        var world = _client.World;
        var me = world.Me;
        var lines = new List<string>
        {
            $"FPS: {Fps}",
            $"Mode: {(IsHost ? $"Host (0.0.0.0:{HostPort})" : "Remote client")}",
            $"Status: {_client.Status}   Ping: {_client.PingMs} ms",
            $"Players: {world.Players.Count} [{string.Join(", ", world.Players.Values.Select(p => $"{p.Id}:{p.Name}"))}]",
            $"Enemies: {world.Enemies.Count}   Projectiles: {world.Projectiles.Count}",
            $"Drops: {world.Drops.Count}",
            me != null ? $"Pos: {me.Position.X:0.0}, {me.Position.Y:0.0}" : "Pos: -",
            $"Move speed: {world.MyStats.MovementSpeed:0.0}   Armor: {world.MyStats.Armor:0}",
            $"Weapon: {world.MyStats.WeaponMinDamage:0}-{world.MyStats.WeaponMaxDamage:0} @ {world.MyStats.WeaponAttackSpeed:0.0#}aps",
            $"Res F/C/L: {world.MyStats.FireResistance:0}/{world.MyStats.ColdResistance:0}/{world.MyStats.LightningResistance:0}",
        };
        int ly = _panel.Bounds.Y + 34 + _commandRows * 33 + 6;
        foreach (var line in lines)
        {
            sb.DrawString(font, line, new Vector2(_panel.Bounds.X + 10, ly), new Color(190, 200, 190));
            ly += 17;
        }
    }
}
