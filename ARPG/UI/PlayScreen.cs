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
    private readonly SkillTreeUI _skillTree;
    private readonly ShopUI _shop;
    private readonly TrainerUI _trainer;
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
    /// <summary>ARPG_DEVUI=drops: scatter one of every scroll shortly after joining.</summary>
    private bool _devDropScrolls;
    /// <summary>ARPG_DEVUI=shop: walk-free shop open shortly after joining (GUI automation).</summary>
    private bool _devOpenShop;
    /// <summary>ARPG_DEVUI=summons: learn the skeleton archers and raise a pack (GUI automation).</summary>
    private bool _devLearnSummons, _devRaiseSummons;
    /// <summary>ARPG_DEVUI=knight: spawn Barrow Knights next to the player (GUI automation).</summary>
    private bool _devSpawnKnights;
    private bool _devWarpNext;
    /// <summary>True while a left-button press that a UI panel consumed (e.g. an X close
    /// button) is STILL held — the held-triggered primary attack must not fire from it.</summary>
    private bool _lmbClaimedByUI;
    private bool _rmbClaimedByUI;
    /// <summary>Client-side cooldown estimates per skill (server still validates).</summary>
    private readonly Dictionary<string, float> _cooldownEnds = new();
    /// <summary>Client-side mirror of the server's global use-time lockout.</summary>
    private float _globalReadyEnd;
    /// <summary>Hotbar slot currently charging a Chargeable skill (-1 = none).</summary>
    private int _chargingSlot = -1;
    private float _chargeStart;
    /// <summary>0..1 charge of the currently charging skill (drawn as a bar).</summary>
    public float ChargeFraction => _chargingSlot >= 0
        ? Math.Clamp((_clientTime - _chargeStart) / ChargeTime, 0f, 1f) : 0f;
    private const float ChargeTime = 0.9f;
    /// <summary>Auto-walk pickup: the drop we're heading toward after the player
    /// pressed pickup on a hovered (but out-of-range) item label.</summary>
    private Guid _pickupTargetId = Guid.Empty;
    /// <summary>Summon command key state: press time (for tap-vs-hold) and whether the
    /// current press already resolved (a hold sends "follow" once, then goes quiet).</summary>
    private float _summonCmdDownAt = -1f;
    private bool _summonCmdHandled;
    private const float SummonFollowHoldTime = 0.45f;
    /// <summary>Which learned summon SKILL the command key drives (cycled with Tab).</summary>
    private int _summonFocusIdx;

    /// <summary>Summon skills with at least one ACTIVE minion, in the stable order the
    /// skill list shows them. Merely LEARNING a summon skill doesn't put it here — the
    /// focus cycling / command UI only exists while something is actually summoned.</summary>
    private List<string> SummonSkillIds()
    {
        var character = _client.World.MyCharacter;
        if (character == null) return new List<string>();
        return character.Skills
            .Where(s => _game.Data.Skills.GetValueOrDefault(s.SkillId)?.Archetype == SkillArchetype.Summon)
            .Select(s => s.SkillId)
            .Where(id => _client.World.Summons.Values.Any(su =>
                su.OwnerId == _client.World.MyPlayerId && su.SkillId == id))
            .ToList();
    }

    /// <summary>The focused summon skill id, or null when none are learned.</summary>
    public string FocusedSummonSkillId
    {
        get
        {
            var ids = SummonSkillIds();
            if (ids.Count == 0) return null;
            _summonFocusIdx %= ids.Count;
            return ids[_summonFocusIdx];
        }
    }
    private float _fpsTimer;
    private int _fpsCounter;
    private float _autosaveTimer;
    private float _lastNoManaMsgAt = -10f;

    /// <summary>A draggable gameplay panel wrapped for z-ordering (see ctor).</summary>
    private sealed class PanelZ
    {
        public object Owner;
        public Func<bool> IsOpen;
        public Func<Point, bool> Contains;
        public Action<InputManager, bool> Update;
        public Action<SpriteBatch> Draw;
    }
    private readonly List<PanelZ> _panelZ = new();

    /// <summary>Move a panel to the top of the z-order (drawn last, updated first).</summary>
    private void RaisePanel(object owner)
    {
        int idx = _panelZ.FindIndex(p => p.Owner == owner);
        if (idx < 0 || idx == _panelZ.Count - 1) return;
        var top = _panelZ[idx];
        _panelZ.RemoveAt(idx);
        _panelZ.Add(top);
    }

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
        _skillTree = new SkillTreeUI(game.Data, client);
        _shop = new ShopUI(game.Data, client, _inventory);
        _trainer = new TrainerUI(game.Data, client);
        // Entering the shop opens the bag in sell mode beside it; closing ends selling.
        _shop.ModeChanged += mode =>
        {
            if (mode == ShopUI.ShopMode.Shop)
            {
                _inventory.Open = true;
                _inventory.SellClickHandler = item => _client.RequestShopSell(item.InstanceId);
            }
            else
            {
                _inventory.SellClickHandler = null;
                if (mode == ShopUI.ShopMode.Closed) _inventory.Open = false;
            }
        };
        _debug = new DebugUI(client) { IsHost = server != null, HostPort = server?.LocalPort ?? 0 };

        // Draggable gameplay panels in z-order (last entry = topmost). Clicking an open
        // panel raises it; updates run topmost-first so a window never reacts to input
        // meant for one stacked above it. The debug panel stays outside the stack —
        // always on top, drawn last.
        _panelZ.Add(new PanelZ { Owner = _skillMenu, IsOpen = () => _skillMenu.Open, Contains = p => _skillMenu.Contains(p), Update = (i, b) => _skillMenu.Update(i, b), Draw = sb => _skillMenu.Draw(sb, _game.Input) });
        _panelZ.Add(new PanelZ { Owner = _characterSheet, IsOpen = () => _characterSheet.Open, Contains = p => _characterSheet.Contains(p), Update = (i, b) => _characterSheet.Update(i, b), Draw = sb => _characterSheet.Draw(sb) });
        _panelZ.Add(new PanelZ { Owner = _skillTree, IsOpen = () => _skillTree.Open, Contains = p => _skillTree.Contains(p), Update = (i, b) => _skillTree.Update(i, b), Draw = sb => _skillTree.Draw(sb) });
        _panelZ.Add(new PanelZ { Owner = _shop, IsOpen = () => _shop.Open, Contains = p => _shop.Contains(p), Update = (i, b) => _shop.Update(i, b), Draw = sb => _shop.Draw(sb, _game.UiScreenSize) });
        _panelZ.Add(new PanelZ { Owner = _trainer, IsOpen = () => _trainer.Open, Contains = p => _trainer.Contains(p), Update = (i, b) => _trainer.Update(i, b), Draw = sb => _trainer.Draw(sb) });
        _panelZ.Add(new PanelZ { Owner = _inventory, IsOpen = () => _inventory.Open, Contains = p => _inventory.Contains(p), Update = (i, b) => _inventory.Update(i, b), Draw = sb => _inventory.Draw(sb, _game.Input) });

        // Dev convenience (like --sp): ARPG_DEVUI=debug[,skills][,inventory] opens
        // panels at startup — lets headless/automated sessions drive them by mouse alone.
        var devUi = Environment.GetEnvironmentVariable("ARPG_DEVUI");
        if (!string.IsNullOrEmpty(devUi))
        {
            Console.WriteLine($"[DevUI] Startup panels/actions: {devUi}");
            if (devUi.Contains("debug")) _debug.Open = true;
            if (devUi.Contains("skills")) _skillMenu.Open = true;
            if (devUi.Contains("inventory")) _inventory.Open = true;
            if (devUi.Contains("drops")) _devDropScrolls = true;
            if (devUi.Contains("shop")) _devOpenShop = true;
            if (devUi.Contains("shopgrid")) _shop.DevAutoGrid = true;
            if (devUi.Contains("tree")) _skillTree.Open = true;
            if (devUi.Contains("sheet")) _characterSheet.Open = true;
            if (devUi.Contains("summons")) _devLearnSummons = _devRaiseSummons = true;
            if (devUi.Contains("knight")) _devSpawnKnights = true;
            if (devUi.Contains("warp")) _devWarpNext = true;
        }

        _client.Disconnected += reason => _pendingDisconnect = reason ?? "Disconnected.";
        _client.ServerMessageReceived += msg => _hud.AddMessage(msg);
        _client.MapChanged += () =>
        {
            // New map: snap the camera to the arrival spot and close world-anchored UI.
            if (_client.World.Me is { } me2) _camera.Center = me2.Position;
            _shop.Close();
            _trainer.Open = false;
            _pickupTargetId = Guid.Empty;
        };
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
        if (_devDropScrolls && _clientTime > 1.5f)
        {
            _devDropScrolls = false;
            _client.SendDebugCommand("drop_scrolls");
        }
        if (_devOpenShop && _clientTime > 2f && _client.World.Npcs.Count > 0 && _client.World.Me != null)
        {
            // Dev aid: stand beside the merchant first (the server range-gates
            // ShopOpen), let the position replicate, then ask for the stock.
            var devNpc = _client.World.Npcs.Values.First(n => n.TypeId != "skill_trainer");
            _client.World.Me.Position = devNpc.Position + new NumVec2(0.9f, 0.4f);
            if (_clientTime > 2.6f)
            {
                _devOpenShop = false;
                _client.RequestShopOpen(devNpc.Id);
            }
        }
        if (_devLearnSummons && _clientTime > 1.5f)
        {
            _devLearnSummons = false;
            _client.RequestLearnSkill("summon_skeleton");
            _client.RequestLearnSkill("summon_skeleton_warrior");
        }
        if (_devRaiseSummons && _clientTime > 2.5f)
        {
            _devRaiseSummons = false;
            _client.RequestSummonAdjust("summon_skeleton_warrior", +1);
            _client.RequestSummonAdjust("summon_skeleton", +1);
            _client.RequestSummonAdjust("summon_skeleton", +1);
        }
        if (_devSpawnKnights && _clientTime > 9f)
        {
            _devSpawnKnights = false;
            _client.SendDebugCommand("spawn_enemy", "bone_knight");
        }
        if (_devWarpNext && _clientTime > 3f)
        {
            _devWarpNext = false;
            _client.SendDebugCommand("warp_next");
        }

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
        _skillTree.Layout(uiScreen);
        _shop.Layout(uiScreen);
        _trainer.Layout(uiScreen);

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
            if (_inventory.Open || _skillMenu.Open || _debug.Open || _characterSheet.Open ||
                _skillTree.Open || _shop.Open || _trainer.Open)
            {
                _inventory.Open = _skillMenu.Open = _debug.Open = _characterSheet.Open = _skillTree.Open = false;
                _trainer.Open = false;
                _shop.Close();
                _inventory.CancelEnchantMode();
            }
            else
            {
                BuildPauseMenu(); // relayout for the current resolution/UI scale
                _paused = true;
            }
        }

        // --- panel toggles (opening a window puts it on top of the stack) ---
        if (input.WasActionPressed(InputAction.Inventory)) { _inventory.Open = !_inventory.Open; if (_inventory.Open) RaisePanel(_inventory); }
        if (input.WasActionPressed(InputAction.SkillMenu)) { _skillMenu.Open = !_skillMenu.Open; if (_skillMenu.Open) RaisePanel(_skillMenu); }
        if (input.WasActionPressed(InputAction.CharacterSheet)) { _characterSheet.Open = !_characterSheet.Open; if (_characterSheet.Open) RaisePanel(_characterSheet); }
        if (input.WasActionPressed(InputAction.SkillTree)) { _skillTree.Open = !_skillTree.Open; if (_skillTree.Open) RaisePanel(_skillTree); }
        if (input.WasActionPressed(InputAction.DebugMenu)) _debug.Open = !_debug.Open;

        // --- UI updates first: they claim the mouse before world input runs ---
        // A click raises the topmost open panel under the mouse; updates then run
        // topmost-first, and any panel below a window that holds the mouse is blocked
        // for the frame — overlapping menus never both react to one click.
        if (input.MouseLeftPressed && !_debug.Contains(input.MousePosition))
            for (int i = _panelZ.Count - 1; i >= 0; i--)
                if (_panelZ[i].IsOpen() && _panelZ[i].Contains(input.MousePosition))
                {
                    RaisePanel(_panelZ[i].Owner);
                    break;
                }
        _debug.Update(input);
        bool uiMouseTaken = _debug.Contains(input.MousePosition);
        for (int i = _panelZ.Count - 1; i >= 0; i--)
        {
            var panel = _panelZ[i];
            panel.Update(input, uiMouseTaken);
            if (panel.IsOpen() && panel.Contains(input.MousePosition)) uiMouseTaken = true;
        }

        // --- finish drags ---
        if (_drag.Active && input.MouseLeftReleased)
        {
            var mouse = input.MousePosition;
            bool handled = _skillMenu.TryDropAt(mouse) || _inventory.TryDropAt(mouse) ||
                           _debug.Contains(mouse) || _characterSheet.Contains(mouse) ||
                           _skillTree.Contains(mouse) || _shop.Contains(mouse);
            if (!handled)
                _client.RequestDropItem(_drag.Item.InstanceId); // released over the world: drop it
            _drag.Clear();
        }

        // A UI-consumed click stays consumed for as long as the button is held, so
        // closing a panel with the X never leaks into a primary attack.
        if (input.MouseCapturedByUI && input.MouseLeftDown) _lmbClaimedByUI = true;
        else if (!input.MouseLeftDown) _lmbClaimedByUI = false;
        if (input.MouseCapturedByUI && input.MouseRightDown) _rmbClaimedByUI = true;
        else if (!input.MouseRightDown) _rmbClaimedByUI = false;

        bool mouseFree = !input.MouseCapturedByUI && !_drag.Active;

        // --- movement (WASD in screen space, converted to isometric world space) ---
        if (me.Alive)
        {
            var screenDir = NumVec2.Zero;
            if (input.IsActionDown(InputAction.MoveUp)) screenDir.Y -= 1;
            if (input.IsActionDown(InputAction.MoveDown)) screenDir.Y += 1;
            if (input.IsActionDown(InputAction.MoveLeft)) screenDir.X -= 1;
            if (input.IsActionDown(InputAction.MoveRight)) screenDir.X += 1;
            // Frozen solid (chill freeze / electrocute seize): the server pins us anyway;
            // dropping the input locally avoids a rubber-band fight.
            if ((me.DebuffFlags & Server.PlayerDebuffs.Frozen) != 0) screenDir = NumVec2.Zero;
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

            // --- skills (chargeable skills fire on RELEASE, scaled by held time) ---
            // Any MOUSE-bound action respects UI capture — a right-click that quick-
            // equipped an item in the bag must never double as a skill cast.
            bool SkillHeld(InputAction action)
            {
                if (!input.IsActionDown(action)) return false;
                var binding = input.Bindings[action];
                if (!binding.IsMouse) return true;
                bool claimed = binding.MouseButton switch
                {
                    0 => _lmbClaimedByUI,
                    1 => _rmbClaimedByUI,
                    _ => false,
                };
                return mouseFree && !claimed;
            }
            HandleHotbarSlot(0, SkillHeld(InputAction.PrimaryAttack), mouseWorld);
            HandleHotbarSlot(1, SkillHeld(InputAction.Skill1), mouseWorld);
            HandleHotbarSlot(2, SkillHeld(InputAction.Skill2), mouseWorld);
            HandleHotbarSlot(3, SkillHeld(InputAction.Skill3), mouseWorld);
            HandleHotbarSlot(4, SkillHeld(InputAction.Skill4), mouseWorld);

            // --- pickup ---
            // Hover a drop label to target it: the pickup key then grabs THAT item,
            // auto-walking over first when it's out of reach.
            _renderer.HoveredDropId = Guid.Empty;
            if (mouseFree)
                for (int i = _renderer.DropLabelRects.Count - 1; i >= 0; i--)
                    if (_renderer.DropLabelRects[i].rect.Contains(input.RawMousePosition))
                    {
                        _renderer.HoveredDropId = _renderer.DropLabelRects[i].dropId;
                        break;
                    }
            if (input.WasActionPressed(InputAction.Interact))
            {
                var npcNear = _client.World.Npcs.Values
                    .FirstOrDefault(n => NumVec2.Distance(me.Position, n.Position) <= 3f);
                var chestNear = _client.World.Chests.Values
                    .FirstOrDefault(c => !c.Opened && NumVec2.Distance(me.Position, c.Position) <= 2.2f);
                bool doorNear = _client.World.Map.ExitDoor != NumVec2.Zero &&
                                NumVec2.Distance(me.Position, _client.World.Map.ExitDoor) <= 2.4f;
                bool fountainNear = _client.World.Map.FountainSpot != NumVec2.Zero &&
                                    NumVec2.Distance(me.Position, _client.World.Map.FountainSpot) <= 2.4f;
                if (_renderer.HoveredDropId != Guid.Empty &&
                    _client.World.Drops.TryGetValue(_renderer.HoveredDropId, out var targeted))
                {
                    if (NumVec2.Distance(me.Position, targeted.Position) <= 1.8f)
                        _client.RequestPickup(targeted.DropId);
                    else
                        _pickupTargetId = targeted.DropId; // walk over, then grab it
                }
                else if (doorNear)
                {
                    _client.RequestDoorReady(); // toggle READY; server moves everyone when all are
                }
                else if (chestNear != null)
                {
                    _client.RequestOpenChest(chestNear.Id);
                }
                else if (fountainNear)
                {
                    _client.RequestUseFountain();
                }
                else if (npcNear != null && !_shop.Open && !_trainer.Open)
                {
                    if (npcNear.TypeId == "skill_trainer")
                    {
                        // The trainer's list is local knowledge — no stock roundtrip.
                        _trainer.Open = true;
                        RaisePanel(_trainer);
                    }
                    else
                    {
                        _client.RequestShopOpen(npcNear.Id); // shop opens when the stock arrives
                    }
                }
                else
                {
                    var drop = _client.World.NearestDrop(me.Position, 1.8f);
                    if (drop != null) _client.RequestPickup(drop.DropId);
                }
            }
            // Command summons (backquote), acting on the FOCUSED summon skill (Tab
            // cycles focus, so different packs can hold different marks):
            //  - TAP: rally the pack at the cursor (aiming at yourself also recalls)
            //  - HOLD: order the pack back to following you.
            if (input.WasActionPressed(InputAction.CycleSummonFocus))
            {
                var ids = SummonSkillIds();
                if (ids.Count > 1)
                {
                    _summonFocusIdx = (_summonFocusIdx + 1) % ids.Count;
                    var fDef = _game.Data.Skills.GetValueOrDefault(ids[_summonFocusIdx]);
                    _hud.AddMessage($"Commanding: {fDef?.Name ?? ids[_summonFocusIdx]}");
                }
            }
            string focusedSummon = FocusedSummonSkillId;
            if (focusedSummon != null)
            {
                string packName = _game.Data.Skills.GetValueOrDefault(focusedSummon)?.Name ?? "Summons";
                if (input.WasActionPressed(InputAction.CommandSummons))
                {
                    _summonCmdDownAt = _clientTime;
                    _summonCmdHandled = false;
                }
                if (_summonCmdDownAt >= 0 && !_summonCmdHandled &&
                    input.IsActionDown(InputAction.CommandSummons) &&
                    _clientTime - _summonCmdDownAt >= SummonFollowHoldTime)
                {
                    _summonCmdHandled = true; // held: back to heel
                    _client.RequestSummonRally(focusedSummon, false, default);
                    _hud.AddMessage($"{packName} follow you.");
                }
                if (_summonCmdDownAt >= 0 && !input.IsActionDown(InputAction.CommandSummons))
                {
                    if (!_summonCmdHandled)
                    {
                        bool recall = NumVec2.Distance(mouseWorld, me.Position) < 1.5f;
                        _client.RequestSummonRally(focusedSummon, !recall, mouseWorld);
                        _hud.AddMessage(recall
                            ? $"{packName} follow you."
                            : $"{packName} rally to your mark.");
                    }
                    _summonCmdDownAt = -1f;
                }
            }
            _hud.FocusedSummonSkillId = focusedSummon;

            // Auto-walk toward a targeted pickup; any manual movement cancels it.
            if (_pickupTargetId != Guid.Empty)
            {
                if (worldDir != NumVec2.Zero ||
                    !_client.World.Drops.TryGetValue(_pickupTargetId, out var walkTo))
                {
                    _pickupTargetId = Guid.Empty;
                }
                else if (NumVec2.Distance(me.Position, walkTo.Position) <= 1.6f)
                {
                    _client.RequestPickup(walkTo.DropId);
                    _pickupTargetId = Guid.Empty;
                }
                else
                {
                    var dir = NumVec2.Normalize(walkTo.Position - me.Position);
                    float speed = _client.World.MyStats.MovementSpeed;
                    float wh = me.Height;
                    me.Position = _client.World.Map.MoveWithCollision(me.Position, dir * speed * dt, 0.3f, ref wh);
                    me.Height = wh;
                }
            }
            // (No LMB pickup: clicking a drop label only HOVER-targets it — the pickup
            // key is the one way to grab items, so attacks never eat loot clicks.)

            // Potion flasks: a sip over time, server-validated (charges, already-active).
            if (input.WasActionPressed(InputAction.HealthPotion))
                _client.RequestUsePotion(0);
            if (input.WasActionPressed(InputAction.ManaPotion))
                _client.RequestUsePotion(1);
        }

        // Camera follows the player.
        _camera.Center = NumVec2.Lerp(_camera.Center, me.Position, Math.Clamp(dt * 8f, 0, 1));
    }

    private void HandleHotbarSlot(int slot, bool down, NumVec2 target)
    {
        var character = _client.World.MyCharacter;
        string skillId = slot < character.Hotbar.Length ? character.Hotbar[slot] : null;
        var def = skillId != null ? _game.Data.Skills.GetValueOrDefault(skillId) : null;
        if (def == null) return;

        if (def.Chargeable)
        {
            if (_chargingSlot == slot)
            {
                if (down) return; // still holding — keep charging
                float charge = Math.Clamp((_clientTime - _chargeStart) / ChargeTime, 0f, 1f);
                _chargingSlot = -1;
                TryUseHotbarSkill(slot, target, charge);
            }
            else if (down && _chargingSlot < 0 && _clientTime >= _globalReadyEnd &&
                     MeetsEquipmentGates(def) &&
                     !(_cooldownEnds.TryGetValue(skillId, out float r) && _clientTime < r))
            {
                _chargingSlot = slot;
                _chargeStart = _clientTime;
            }
            return;
        }
        if (down) TryUseHotbarSkill(slot, target, 0f);
    }

    /// <summary>Client mirror of the server's equipment gates (shield/weapon category), so
    /// we never predict a lunge or start a charge for a cast the server will reject.</summary>
    private bool MeetsEquipmentGates(SkillDefinition def)
    {
        if (def.RequiresShield && !_client.World.MyStats.HasShield) return false;
        if (def.RequiredWeapon.HasValue &&
            _client.World.MyCharacter?.MainHand?.GetBase(_game.Data)?.Category != def.RequiredWeapon.Value)
            return false;
        return true;
    }

    private void TryUseHotbarSkill(int slot, NumVec2 target, float charge)
    {
        var character = _client.World.MyCharacter;
        string skillId = slot < character.Hotbar.Length ? character.Hotbar[slot] : null;
        if (skillId == null) return;
        if (_cooldownEnds.TryGetValue(skillId, out float readyAt) && _clientTime < readyAt) return;
        if (_clientTime < _globalReadyEnd) return; // predicted global use-time lockout

        var learned = character.GetSkill(skillId);
        var def = _game.Data.Skills.GetValueOrDefault(skillId);
        if (learned == null || def == null) return;
        if (def.Archetype == SkillArchetype.Summon) return; // managed from the Skill Menu

        // Mirror the server's equipment gates: without them the client would predict
        // the lunge/cooldown for a cast the server is about to reject (e.g. Shield
        // Bash scooting you forward with no shield equipped).
        if (!MeetsEquipmentGates(def)) return;

        var stats = SkillMath.Compute(_game.Data, def, learned.Level, learned.ScrollDefinitions(_game.Data), _client.World.MyStats);

        // Not enough mana: the cast never starts — no animation, no cooldown, no
        // request. A little reminder instead of a swing that does nothing.
        var me = _client.World.Me;
        if (stats.ManaCost > 0 && me != null && me.Mana < stats.ManaCost - 0.01f)
        {
            if (_clientTime - _lastNoManaMsgAt > 1.2f)
            {
                _lastNoManaMsgAt = _clientTime;
                _hud.AddMessage("Not enough mana.");
            }
            return;
        }
        // Instant-target skills (Chain Lightning) fizzle for free with nothing in
        // reach — mirror the server: no cooldown, no request, no cost.
        if (def.Archetype == SkillArchetype.ChainLightning && me != null)
        {
            var toAim = target - me.Position;
            float aimDist = toAim.Length();
            var probe = aimDist > stats.Range && aimDist > 0.001f
                ? me.Position + toAim / aimDist * stats.Range
                : target;
            bool anyTarget = _client.World.Enemies.Values.Any(e =>
                MathF.Abs(e.Height - me.Height) <= 0.75f &&
                NumVec2.Distance(e.Position, probe) <= 2.2f);
            if (!anyTarget) return;
        }

        _cooldownEnds[skillId] = _clientTime + stats.Cooldown;
        _globalReadyEnd = _clientTime + def.UseTime;
        _client.RequestUseSkill(skillId, target, _renderer.HoveredEnemyId, charge);

        // Lunge skills (Shield Bash): scoot toward the aim, stopping just short of the
        // first enemy along the path so the shove reads as a body-check, not a pass-through.
        if (def.LungeDistance > 0 && _client.World.Me is { Alive: true } lunger)
        {
            var toTarget = target - lunger.Position;
            _lungeDir = toTarget.LengthSquared() > 0.001f ? NumVec2.Normalize(toTarget) : lunger.Facing;
            float dist = def.LungeDistance * (1f + 0.8f * charge);
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
        if (_chargingSlot >= 0)
        {
            // Charge meter above the hotbar: fills over ChargeTime, flashes when full.
            const int cw = 180, chh = 10;
            int cx = screen.X / 2 - cw / 2, cy = screen.Y - 96;
            float frac = ChargeFraction;
            sb.Draw(TextureGen.Pixel, new Rectangle(cx - 1, cy - 1, cw + 2, chh + 2), new Color(20, 20, 26, 220));
            sb.Draw(TextureGen.Pixel, new Rectangle(cx, cy, (int)(cw * frac), chh),
                frac >= 1f ? new Color(255, 226, 120) : new Color(120, 180, 255));
        }
        _hud.Draw(sb, screen, _game.Input, _cooldownEnds, _clientTime);
        // Panels draw bottom-to-top so the last-raised window overlays the rest;
        // the debug panel always sits above the stack.
        foreach (var panel in _panelZ) panel.Draw(sb);
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
