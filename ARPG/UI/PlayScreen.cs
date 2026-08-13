using FontStashSharp;
using ARPG.Core;
using ARPG.Net;
using ARPG.Persistence;
using ARPG.Render;
using ARPG.Server;
using ARPG.Skills;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using NumVec2 = System.Numerics.Vector2;

namespace ARPG.UI;

/// <summary>
/// The in-game screen. Owns the GameClient (and the GameServer when hosting/single player),
/// translates input actions into client requests, and composes the world renderer, HUD,
/// inventory, skill menu and debug UI.
/// </summary>
public class PlayScreen : IScreen
{
    private readonly GameMain _game;
    private readonly GameServer _server;   // null when joining someone else's game
    private readonly GameClient _client;

    private readonly IsoCamera _camera = new();
    private readonly WorldRenderer _renderer;
    private readonly HudUI _hud;
    private readonly InventoryUI _inventory;
    private readonly SkillMenuUI _skillMenu;
    private readonly CharacterSheetUI _characterSheet;
    private readonly DebugUI _debug;
    private readonly DragState _drag = new();

    private bool _paused;
    private Point _lastUiScreen;
    private Panel _pausePanel;
    private OptionsPanel _optionsPanel; // non-null while options is open from the pause menu
    private string _pendingDisconnect;

    // Client-predicted dodge state (cooldown + i-frames stay server-authoritative).
    private float _dodgeTimeLeft;
    private NumVec2 _dodgeDir;
    private float _dodgeCooldownEnd;

    // Client-predicted lunge (Shield Bash's forward scoot; server grants the i-frames).
    private float _lungeTimeLeft;
    private NumVec2 _lungeDir;
    private float _lungeSpeed;
    private float _clientTime;
    /// <summary>Client-side cooldown estimates per skill (server still validates).</summary>
    private readonly Dictionary<string, float> _cooldownEnds = new();
    private float _fpsTimer;
    private int _fpsCounter;
    private float _autosaveTimer;

    public PlayScreen(GameMain game, GameServer server, GameClient client)
    {
        _game = game;
        _server = server;
        _client = client;
        _renderer = new WorldRenderer(game.Data, game.Settings);
        _hud = new HudUI(game.Data, client, game.Settings);
        _inventory = new InventoryUI(game.Data, client, _drag);
        _skillMenu = new SkillMenuUI(game.Data, client, _drag);
        _characterSheet = new CharacterSheetUI(game.Data, client);
        _debug = new DebugUI(client) { IsHost = server != null, HostPort = server?.LocalPort ?? 0 };
        _debug.OnCycleTheme = () => _renderer.CycleTheme();

        _client.Disconnected += reason => _pendingDisconnect = reason ?? "Disconnected.";
        _client.ServerMessageReceived += msg => _hud.AddMessage(msg);
        BuildPauseMenu();
    }

    private void BuildPauseMenu()
    {
        var size = _game.UiScreenSize;
        int cx = size.X / 2 - 120, cy = size.Y / 2 - 95;
        _pausePanel = new Panel { Bounds = new Rectangle(cx - 20, cy - 20, 280, 240) };
        _pausePanel.Children.Add(new Label("Paused", cx, cy - 8, 22, bold: true));
        _pausePanel.Children.Add(new Button("Resume", new Rectangle(cx, cy + 30, 240, 40), () => _paused = false));
        _pausePanel.Children.Add(new Button("Options", new Rectangle(cx, cy + 80, 240, 40),
            () => _optionsPanel = new OptionsPanel(_game, () => _optionsPanel = null)));
        _pausePanel.Children.Add(new Button("Save & Exit to Menu", new Rectangle(cx, cy + 130, 240, 40), LeaveToMenu));
    }

    private void LeaveToMenu()
    {
        SaveLocalCharacter();
        _client.Disconnect();
        _server?.Stop();
        _game.Settings.Save();
        _game.SwitchScreen(new MainMenuScreen(_game));
    }

    public void SaveLocalCharacter()
    {
        if (_client.World.MyCharacter != null)
            SaveManager.SaveCharacter(_client.World.MyCharacter);
    }

    public void Shutdown()
    {
        SaveLocalCharacter();
        _client.Disconnect();
        _server?.Stop();
    }

    public void Update(float dt)
    {
        _clientTime += dt;
        _fpsCounter++;
        _fpsTimer += dt;
        if (_fpsTimer >= 0.5f) { _debug.Fps = (int)(_fpsCounter / _fpsTimer); _fpsCounter = 0; _fpsTimer = 0; }

        // The server (when hosting) runs on its OWN thread with a fixed timestep — the
        // render thread only drives the client, which talks to it over loopback UDP.
        _client.Update(dt);
        _hud.Update(dt);

        if (_pendingDisconnect != null)
        {
            SaveLocalCharacter();
            _server?.Stop();
            _game.SwitchScreen(new MainMenuScreen(_game, _pendingDisconnect));
            return;
        }

        var input = _game.Input;
        var screen = _game.ScreenSize;
        _camera.ScreenWidth = screen.X;
        _camera.ScreenHeight = screen.Y;
        var uiScreen = _game.UiScreenSize;
        _inventory.Layout(uiScreen);
        _skillMenu.Layout(uiScreen);
        _characterSheet.Layout(uiScreen);

        if (_client.Status != ClientStatus.InGame)
        {
            if (input.WasActionPressed(InputAction.Pause))
            {
                _client.Disconnect();
                _server?.Stop();
                _game.SwitchScreen(new MainMenuScreen(_game));
            }
            return;
        }

        var me = _client.World.Me;
        var character = _client.World.MyCharacter;
        if (me == null || character == null) return;

        // Periodic local safety save.
        _autosaveTimer += dt;
        if (_autosaveTimer > 30f)
        {
            _autosaveTimer = 0;
            SaveLocalCharacter();
        }

        // Rebuild open overlays when the resolution/UI scale changes under them.
        if (uiScreen != _lastUiScreen)
        {
            _lastUiScreen = uiScreen;
            BuildPauseMenu();
            if (_optionsPanel != null)
                _optionsPanel = new OptionsPanel(_game, () => _optionsPanel = null);
        }

        // --- pause overlay (with nested options panel) ---
        if (_paused)
        {
            if (_optionsPanel != null)
            {
                if (input.WasActionPressed(InputAction.Pause)) _optionsPanel = null;
                else _optionsPanel.Update(input);
            }
            else
            {
                if (input.WasActionPressed(InputAction.Pause)) _paused = false;
                _pausePanel.Update(input);
            }
            return;
        }
        if (input.WasActionPressed(InputAction.Pause))
        {
            if (_inventory.Open || _skillMenu.Open || _debug.Open || _characterSheet.Open)
            {
                _inventory.Open = _skillMenu.Open = _debug.Open = _characterSheet.Open = false;
                _inventory.CancelEnchantMode();
            }
            else
            {
                BuildPauseMenu(); // relayout for the current resolution/UI scale
                _paused = true;
            }
        }

        // --- panel toggles ---
        if (input.WasActionPressed(InputAction.Inventory)) _inventory.Open = !_inventory.Open;
        if (input.WasActionPressed(InputAction.SkillMenu)) _skillMenu.Open = !_skillMenu.Open;
        if (input.WasActionPressed(InputAction.CharacterSheet)) _characterSheet.Open = !_characterSheet.Open;
        if (input.WasActionPressed(InputAction.DebugMenu)) _debug.Open = !_debug.Open;

        // --- UI updates first: they claim the mouse before world input runs ---
        _debug.Update(input);
        _skillMenu.Update(input);
        _characterSheet.Update(input);
        _inventory.Update(input);

        // --- finish drags ---
        if (_drag.Active && input.MouseLeftReleased)
        {
            var mouse = input.MousePosition;
            bool handled = _skillMenu.TryDropAt(mouse) || _inventory.TryDropAt(mouse) ||
                           _debug.Contains(mouse) || _characterSheet.Contains(mouse);
            if (!handled)
                _client.RequestDropItem(_drag.Item.InstanceId); // released over the world: drop it
            _drag.Clear();
        }

        bool mouseFree = !input.MouseCapturedByUI && !_drag.Active;

        // --- movement (WASD in screen space, converted to isometric world space) ---
        if (me.Alive)
        {
            var screenDir = NumVec2.Zero;
            if (input.IsActionDown(InputAction.MoveUp)) screenDir.Y -= 1;
            if (input.IsActionDown(InputAction.MoveDown)) screenDir.Y += 1;
            if (input.IsActionDown(InputAction.MoveLeft)) screenDir.X -= 1;
            if (input.IsActionDown(InputAction.MoveRight)) screenDir.X += 1;
            var worldDir = IsoCamera.ScreenDirToWorldDir(screenDir); // normalized: diagonals aren't faster

            // --- dodge: movement is client-predicted for responsiveness; the server
            // authoritatively validates the cooldown and grants the i-frames ---
            if (input.WasActionPressed(InputAction.Dodge) && _dodgeTimeLeft <= 0 && _clientTime >= _dodgeCooldownEnd)
            {
                var dodgeStats = _client.World.MyStats;
                _dodgeDir = worldDir != NumVec2.Zero ? worldDir : me.Facing;
                _dodgeTimeLeft = dodgeStats.DodgeDuration;
                _dodgeCooldownEnd = _clientTime + dodgeStats.DodgeCooldown;
                me.DodgeTimeLeft = dodgeStats.DodgeDuration; // local dash visual
                _client.RequestDodge(_dodgeDir);
            }

            // All predicted movement is height-aware: walking a ramp raises Me.Height,
            // cliffs and deck edges block, and the height is replicated to the server.
            float myHeight = me.Height;
            if (_dodgeTimeLeft > 0)
            {
                _dodgeTimeLeft -= dt;
                var dodgeStats = _client.World.MyStats;
                float dodgeSpeed = dodgeStats.DodgeDistance / MathF.Max(0.05f, dodgeStats.DodgeDuration);
                me.Position = _client.World.Map.MoveWithCollision(me.Position, _dodgeDir * dodgeSpeed * dt, 0.3f, ref myHeight);
            }
            else if (_lungeTimeLeft > 0)
            {
                _lungeTimeLeft -= dt;
                me.Position = _client.World.Map.MoveWithCollision(me.Position, _lungeDir * _lungeSpeed * dt, 0.3f, ref myHeight);
            }
            else if (worldDir != NumVec2.Zero)
            {
                float speed = _client.World.MyStats.MovementSpeed; // stat-driven, equipment can modify it
                me.Position = _client.World.Map.MoveWithCollision(me.Position, worldDir * speed * dt, 0.3f, ref myHeight);
            }
            me.Height = myHeight;

            // Aim unprojects onto the plane of MY surface, so overlapping layers
            // (bridge deck vs the ground below) resolve to the one I stand on.
            var mouseWorld = _camera.ScreenToWorld(input.RawMousePosition, me.Height);

            // Hover targeting: the front-most enemy sprite under the cursor becomes the
            // target — highlighted red, shown in the top-of-screen display, and casts
            // aim at ITS true position/elevation (this is how you pick a victim on a
            // different level: the unprojection above can't know which surface you meant).
            _renderer.HoveredEnemyId = -1;
            if (mouseFree)
                for (int i = _renderer.EnemyHitRects.Count - 1; i >= 0; i--)
                    if (_renderer.EnemyHitRects[i].rect.Contains(input.RawMousePosition))
                    {
                        _renderer.HoveredEnemyId = _renderer.EnemyHitRects[i].enemyId;
                        break;
                    }
            if (_renderer.HoveredEnemyId >= 0 &&
                _client.World.Enemies.TryGetValue(_renderer.HoveredEnemyId, out var hoveredEnemy))
                mouseWorld = hoveredEnemy.Position;

            var facing = mouseWorld - me.Position;
            if (facing.LengthSquared() > 0.001f)
                me.Facing = NumVec2.Normalize(facing);

            // --- skills ---
            TryUseHotbarSkill(0, input.IsActionDown(InputAction.PrimaryAttack) && mouseFree, mouseWorld);
            TryUseHotbarSkill(1, input.IsActionDown(InputAction.Skill1), mouseWorld);
            TryUseHotbarSkill(2, input.IsActionDown(InputAction.Skill2), mouseWorld);
            TryUseHotbarSkill(3, input.IsActionDown(InputAction.Skill3), mouseWorld);
            TryUseHotbarSkill(4, input.IsActionDown(InputAction.Skill4), mouseWorld);

            // --- pickup ---
            if (input.WasActionPressed(InputAction.Interact))
            {
                var drop = _client.World.NearestDrop(me.Position, 1.8f);
                if (drop != null) _client.RequestPickup(drop.DropId);
            }
            if (mouseFree && input.MouseLeftPressed)
            {
                foreach (var (rect, dropId) in _renderer.DropLabelRects)
                {
                    if (rect.Contains(input.RawMousePosition)) // drop labels render in world space
                    {
                        _client.RequestPickup(dropId);
                        break;
                    }
                }
            }
        }

        // Camera follows the player.
        _camera.Center = NumVec2.Lerp(_camera.Center, me.Position, Math.Clamp(dt * 8f, 0, 1));
    }

    private void TryUseHotbarSkill(int slot, bool pressed, NumVec2 target)
    {
        if (!pressed) return;
        var character = _client.World.MyCharacter;
        string skillId = slot < character.Hotbar.Length ? character.Hotbar[slot] : null;
        if (skillId == null) return;
        if (_cooldownEnds.TryGetValue(skillId, out float readyAt) && _clientTime < readyAt) return;

        var learned = character.GetSkill(skillId);
        var def = _game.Data.Skills.GetValueOrDefault(skillId);
        if (learned == null || def == null) return;
        var stats = SkillMath.Compute(_game.Data, def, learned.Level, learned.ScrollDefinitions(_game.Data), _client.World.MyStats);
        _cooldownEnds[skillId] = _clientTime + stats.Cooldown;
        _client.RequestUseSkill(skillId, target, _renderer.HoveredEnemyId);

        // Lunge skills (Shield Bash): scoot toward the aim, stopping just short of the
        // first enemy along the path so the shove reads as a body-check, not a pass-through.
        if (def.LungeDistance > 0 && _client.World.Me is { Alive: true } lunger)
        {
            var toTarget = target - lunger.Position;
            _lungeDir = toTarget.LengthSquared() > 0.001f ? NumVec2.Normalize(toTarget) : lunger.Facing;
            float dist = def.LungeDistance;
            foreach (var e in _client.World.Enemies.Values)
            {
                if (MathF.Abs(e.Height - lunger.Height) > 0.75f) continue; // other layers don't body-check
                var toEnemy = e.Position - lunger.Position;
                float along = NumVec2.Dot(toEnemy, _lungeDir);
                if (along > 0.2f && (toEnemy - _lungeDir * along).Length() < 0.6f)
                    dist = MathF.Min(dist, MathF.Max(0.15f, along - 0.55f));
            }
            const float lungeDuration = 0.14f;
            _lungeTimeLeft = lungeDuration;
            _lungeSpeed = dist / lungeDuration;
        }
    }

    /// <summary>Top-of-screen display for the hovered enemy: name (colored by rank)
    /// over a large health bar, so you can pick targets across elevations at a glance.</summary>
    private void DrawTargetDisplay(SpriteBatch sb, Point screen)
    {
        if (_renderer.HoveredEnemyId < 0 ||
            !_client.World.Enemies.TryGetValue(_renderer.HoveredEnemyId, out var e))
            return;

        var nameColor = e.IsBoss ? new Color(216, 150, 255)
            : e.IsElite ? new Color(255, 210, 110)
            : new Color(230, 226, 214);
        var font = FontManager.GetBold(20);
        string name = e.DisplayName;
        var nameSize = font.MeasureString(name);

        const int barW = 340, barH = 16;
        int cx = screen.X / 2;
        int barX = cx - barW / 2, barY = 34;

        // Backing panel sized to the wider of the name and the bar.
        int panelW = (int)MathF.Max(barW + 24, nameSize.X + 40);
        var panel = new Rectangle(cx - panelW / 2, 6, panelW, 52);
        sb.Draw(TextureGen.Pixel, panel, new Color(12, 12, 16, 205));
        sb.Draw(TextureGen.Pixel, new Rectangle(panel.X, panel.Y, panel.Width, 1), new Color(90, 85, 110));
        sb.Draw(TextureGen.Pixel, new Rectangle(panel.X, panel.Bottom - 1, panel.Width, 1), new Color(90, 85, 110));

        sb.DrawString(font, name, new Vector2(cx - nameSize.X / 2, 10), nameColor);

        float frac = e.MaxHealth > 0 ? Math.Clamp(e.Health / e.MaxHealth, 0f, 1f) : 0f;
        sb.Draw(TextureGen.Pixel, new Rectangle(barX - 1, barY - 1, barW + 2, barH + 2), new Color(60, 55, 70));
        sb.Draw(TextureGen.Pixel, new Rectangle(barX, barY, barW, barH), new Color(30, 24, 26));
        sb.Draw(TextureGen.Pixel, new Rectangle(barX, barY, (int)(barW * frac), barH), new Color(196, 54, 50));
        var hpFont = FontManager.Get(13);
        string hp = $"{MathF.Ceiling(MathF.Max(0, e.Health)):0} / {e.MaxHealth:0}";
        var hpSize = hpFont.MeasureString(hp);
        sb.DrawString(hpFont, hp, new Vector2(cx - hpSize.X / 2, barY + barH / 2 - hpSize.Y / 2), Color.White);
    }

    /// <summary>Fallback combined draw (unused by GameMain, which calls the split methods).</summary>
    public void Draw(SpriteBatch sb)
    {
        DrawWorld(sb);
        DrawUI(sb);
    }

    /// <summary>The active zone theme's void/background color (GameMain clears with it).</summary>
    public Color BackgroundColor => _renderer.BackgroundColor;

    /// <summary>World rendering — drawn UNSCALED in raw screen space (camera-driven).</summary>
    public void DrawWorld(SpriteBatch sb)
    {
        if (_client.Status != ClientStatus.InGame) return;
        _renderer.Draw(sb, _camera, _client.World);
    }

    /// <summary>HUD + menus — drawn in UI (virtual) space through the global UI scale matrix.</summary>
    public void DrawUI(SpriteBatch sb)
    {
        var screen = _game.UiScreenSize;

        if (_client.Status != ClientStatus.InGame)
        {
            string text = _client.Status switch
            {
                ClientStatus.Connecting => "Connecting to host...",
                ClientStatus.Joining => "Joining game...",
                _ => "Disconnected.",
            };
            var font = FontManager.GetBold(24);
            var size = font.MeasureString(text);
            sb.DrawString(font, text, new Vector2(screen.X / 2f - size.X / 2, screen.Y / 2f - 40), Color.White);
            var hintFont = FontManager.Get(15);
            var hint = "Press Escape to cancel";
            var hSize = hintFont.MeasureString(hint);
            sb.DrawString(hintFont, hint, new Vector2(screen.X / 2f - hSize.X / 2, screen.Y / 2f + 4), new Color(160, 155, 140));
            return;
        }

        DrawTargetDisplay(sb, screen);
        _hud.Draw(sb, screen, _game.Input, _cooldownEnds, _clientTime);
        _skillMenu.Draw(sb, _game.Input);
        _characterSheet.Draw(sb);
        _inventory.Draw(sb, _game.Input);
        _debug.Draw(sb);

        // Drag ghost + tooltips on top.
        var input = _game.Input;
        if (_drag.Active)
        {
            var b = _drag.Item.GetBase(_game.Data);
            var rect = new Rectangle(input.MousePosition.X - b.InventoryWidth * InventoryUI.Cell / 2,
                input.MousePosition.Y - b.InventoryHeight * InventoryUI.Cell / 2,
                b.InventoryWidth * InventoryUI.Cell, b.InventoryHeight * InventoryUI.Cell);
            _inventory.DrawItemBox(sb, rect, _drag.Item);
        }
        else
        {
            var hovered = _inventory.HoveredItem ?? _skillMenu.HoveredScrollItem;
            if (hovered != null)
                ItemTooltip.Draw(sb, _game.Data, hovered, input.MousePosition, screen);
        }

        var me = _client.World.Me;
        if (me != null && !me.Alive)
        {
            sb.Draw(TextureGen.Pixel, new Rectangle(0, 0, screen.X, screen.Y), new Color(60, 0, 0, 90));
            var font = FontManager.GetBold(30);
            var text = "You died — respawning...";
            var size = font.MeasureString(text);
            sb.DrawString(font, text, new Vector2(screen.X / 2f - size.X / 2, screen.Y / 2f - 60), new Color(255, 120, 100));
        }

        if (_paused)
        {
            sb.Draw(TextureGen.Pixel, new Rectangle(0, 0, screen.X, screen.Y), new Color(0, 0, 0, 120));
            if (_optionsPanel != null) _optionsPanel.Draw(sb);
            else _pausePanel.Draw(sb);
        }
    }
}
