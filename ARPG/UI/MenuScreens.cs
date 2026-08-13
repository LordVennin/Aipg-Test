using FontStashSharp;
using ARPG.Core;
using ARPG.Render;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ARPG.UI;

public interface IScreen
{
    void Update(float dt);
    void Draw(SpriteBatch sb);
}

/// <summary>Main menu: Single Player / Host Game / Join Game / Options / Quit.</summary>
public class MainMenuScreen : IScreen
{
    private readonly GameMain _game;
    private readonly Panel _panel = new() { Background = Color.Transparent, Border = Color.Transparent };
    private readonly string _message;
    private Point _builtSize;

    public MainMenuScreen(GameMain game, string message = null)
    {
        _game = game;
        _message = message;
        Build();
    }

    private void Build()
    {
        var game = _game;
        var size = game.UiScreenSize;
        _builtSize = size;
        _panel.Children.Clear();
        int cx = size.X / 2 - 130, y = size.Y / 2 - 130, w = 260, h = 44, gap = 12;
        _panel.Children.Add(new Button("Single Player", new Rectangle(cx, y, w, h), game.StartSinglePlayer));
        _panel.Children.Add(new Button("Host Game", new Rectangle(cx, y + (h + gap), w, h), () => game.SwitchScreen(new HostScreen(game))));
        _panel.Children.Add(new Button("Join Game", new Rectangle(cx, y + 2 * (h + gap), w, h), () => game.SwitchScreen(new JoinScreen(game))));
        _panel.Children.Add(new Button("Options", new Rectangle(cx, y + 3 * (h + gap), w, h), () => game.SwitchScreen(new OptionsScreen(game))));
        _panel.Children.Add(new Button("Quit", new Rectangle(cx, y + 4 * (h + gap), w, h), game.ExitGame));
    }

    public void Update(float dt)
    {
        if (_game.UiScreenSize != _builtSize) Build(); // resolution changed under us
        _panel.Update(_game.Input);
    }

    public void Draw(SpriteBatch sb)
    {
        var size = _game.UiScreenSize;
        var title = "SCROLLBOUND";
        var subtitle = "an isometric multiplayer ARPG prototype";
        var titleFont = FontManager.GetBold(52);
        var tSize = titleFont.MeasureString(title);
        sb.DrawString(titleFont, title, new Vector2(size.X / 2f - tSize.X / 2, 90), new Color(230, 210, 150));
        var subFont = FontManager.Get(17);
        var sSize = subFont.MeasureString(subtitle);
        sb.DrawString(subFont, subtitle, new Vector2(size.X / 2f - sSize.X / 2, 150), new Color(150, 145, 130));

        if (!string.IsNullOrEmpty(_message))
        {
            var mSize = subFont.MeasureString(_message);
            sb.DrawString(subFont, _message, new Vector2(size.X / 2f - mSize.X / 2, 195), new Color(255, 120, 110));
        }
        _panel.Draw(sb);

        var hint = $"Playing as '{_game.Settings.PlayerName}' — change name in Host/Join screens";
        var hSize = subFont.MeasureString(hint);
        sb.DrawString(subFont, hint, new Vector2(size.X / 2f - hSize.X / 2, size.Y - 60), new Color(120, 118, 108));
    }
}

/// <summary>Host setup: player name + port, listens on 0.0.0.0 (LAN/ZeroTier/Meshnet friendly).</summary>
public class HostScreen : IScreen
{
    private readonly GameMain _game;
    private readonly Panel _panel;
    private readonly TextInput _name, _port;
    private readonly Label _error;

    public HostScreen(GameMain game)
    {
        _game = game;
        var size = game.UiScreenSize;
        int cx = size.X / 2 - 170;
        int y = size.Y / 2 - 140;
        _panel = new Panel { Bounds = new Rectangle(cx - 30, y - 60, 400, 340) };
        _panel.Children.Add(new Label("Host Game", cx, y - 40, 26, bold: true));
        _panel.Children.Add(new Label("Player Name", cx, y + 8, 15));
        _name = new TextInput(new Rectangle(cx, y + 30, 340, 36), game.Settings.PlayerName);
        _panel.Children.Add(_name);
        _panel.Children.Add(new Label($"Port (default {GameNetConfig.DefaultPort})", cx, y + 78, 15));
        _port = new TextInput(new Rectangle(cx, y + 100, 340, 36), game.Settings.LastPort.ToString()) { NumericOnly = true, MaxLength = 5 };
        _panel.Children.Add(_port);
        _error = new Label("", cx, y + 148, 15) { Color = new Color(255, 120, 110) };
        _panel.Children.Add(_error);
        _panel.Children.Add(new Button("Start Hosting", new Rectangle(cx, y + 180, 340, 42), StartHosting));
        _panel.Children.Add(new Button("Back", new Rectangle(cx, y + 232, 340, 36), () => game.SwitchScreen(new MainMenuScreen(game))));
    }

    private void StartHosting()
    {
        if (!int.TryParse(_port.Text, out int port) || port < 1 || port > 65535)
        {
            _error.Text = "Invalid port.";
            return;
        }
        if (string.IsNullOrWhiteSpace(_name.Text))
        {
            _error.Text = "Enter a player name.";
            return;
        }
        _game.Settings.PlayerName = _name.Text.Trim();
        _game.Settings.LastPort = port;
        _game.Settings.Save();
        string error = _game.StartHost(port);
        if (error != null) _error.Text = error;
    }

    public void Update(float dt) => _panel.Update(_game.Input);
    public void Draw(SpriteBatch sb) => _panel.Draw(sb);
}

/// <summary>Join setup: player name + direct host IP + port.</summary>
public class JoinScreen : IScreen
{
    private readonly GameMain _game;
    private readonly Panel _panel;
    private readonly TextInput _name, _ip, _port;
    private readonly Label _error;

    public JoinScreen(GameMain game)
    {
        _game = game;
        var size = game.UiScreenSize;
        int cx = size.X / 2 - 170;
        int y = size.Y / 2 - 170;
        _panel = new Panel { Bounds = new Rectangle(cx - 30, y - 60, 400, 420) };
        _panel.Children.Add(new Label("Join Game", cx, y - 40, 26, bold: true));
        _panel.Children.Add(new Label("Player Name", cx, y + 8, 15));
        _name = new TextInput(new Rectangle(cx, y + 30, 340, 36), game.Settings.PlayerName);
        _panel.Children.Add(_name);
        _panel.Children.Add(new Label("Host IP (e.g. 192.168.1.50 or a ZeroTier/Meshnet IP)", cx, y + 78, 15));
        _ip = new TextInput(new Rectangle(cx, y + 100, 340, 36), game.Settings.LastJoinIp) { MaxLength = 45 };
        _panel.Children.Add(_ip);
        _panel.Children.Add(new Label("Port", cx, y + 148, 15));
        _port = new TextInput(new Rectangle(cx, y + 170, 340, 36), game.Settings.LastPort.ToString()) { NumericOnly = true, MaxLength = 5 };
        _panel.Children.Add(_port);
        _error = new Label("", cx, y + 218, 15) { Color = new Color(255, 120, 110) };
        _panel.Children.Add(_error);
        _panel.Children.Add(new Button("Connect", new Rectangle(cx, y + 250, 340, 42), Connect));
        _panel.Children.Add(new Button("Back", new Rectangle(cx, y + 302, 340, 36), () => game.SwitchScreen(new MainMenuScreen(game))));
    }

    private void Connect()
    {
        if (!int.TryParse(_port.Text, out int port) || port < 1 || port > 65535)
        {
            _error.Text = "Invalid port.";
            return;
        }
        string ip = _ip.Text.Trim();
        if (string.IsNullOrWhiteSpace(ip))
        {
            _error.Text = "Enter a host IP address.";
            return;
        }
        if (string.IsNullOrWhiteSpace(_name.Text))
        {
            _error.Text = "Enter a player name.";
            return;
        }
        _game.Settings.PlayerName = _name.Text.Trim();
        _game.Settings.LastJoinIp = ip;
        _game.Settings.LastPort = port;
        _game.Settings.Save();
        string error = _game.StartJoin(ip, port);
        if (error != null) _error.Text = error;
    }

    public void Update(float dt) => _panel.Update(_game.Input);
    public void Draw(SpriteBatch sb) => _panel.Draw(sb);
}

/// <summary>
/// Reusable options panel, organized into tabs: Display (fullscreen, resolution),
/// Gameplay (HUD toggles) and Controls (full rebinding). Used both as a main-menu screen
/// and as an overlay from the in-game pause menu. New InputActions automatically appear
/// in the Controls tab — the rows are generated from the InputAction enum.
/// </summary>
public class OptionsPanel
{
    private readonly GameMain _game;
    private readonly Rectangle _bounds;
    private readonly Panel _framePanel;    // tab row + bottom buttons, always active
    private readonly Panel[] _tabPanels;   // 0 = Display, 1 = Gameplay, 2 = Controls
    private readonly Button[] _tabButtons;
    private static readonly string[] TabNames = { "Display", "Gameplay", "Controls" };
    private Button _zoneThemeButton;
    private int _tab;

    private InputAction? _rebinding;
    private readonly Dictionary<InputAction, Button> _bindButtons = new();
    private Button _damageNumbersButton, _healthBarsButton, _playerListButton;
    private Button _fullscreenButton, _resolutionButton;

    public OptionsPanel(GameMain game, Action onClose)
    {
        _game = game;
        var size = game.UiScreenSize;
        var actions = Enum.GetValues<InputAction>();
        int panelH = Math.Min(actions.Length * 34 + 200, size.Y - 12);
        int cx = size.X / 2 - 240;
        int y = Math.Max(16, size.Y / 2 - panelH / 2);
        _bounds = new Rectangle(cx - 20, y - 10, 520, panelH);

        _framePanel = new Panel { Bounds = _bounds };
        _framePanel.Children.Add(new Label("Options", cx, y, 24, bold: true));

        // --- tab row ---
        _tabButtons = new Button[TabNames.Length];
        for (int i = 0; i < TabNames.Length; i++)
        {
            int tabIndex = i;
            _tabButtons[i] = new Button(TabNames[i], new Rectangle(cx + i * 160, y + 36, 150, 30),
                () => { _tab = tabIndex; _rebinding = null; RefreshLabels(); }) { FontSize = 15 };
            _framePanel.Children.Add(_tabButtons[i]);
        }
        int contentY = y + 80;
        int bottomY = _bounds.Bottom - 56;

        _framePanel.Children.Add(new Button("Save & Close", new Rectangle(cx + 260, bottomY, 200, 36), () =>
        {
            game.Settings.Bindings = game.Input.ExportBindings();
            game.Settings.Save();
            onClose();
        }));

        // --- Display tab ---
        var display = new Panel { Bounds = Rectangle.Empty, Background = Color.Transparent, Border = Color.Transparent };
        _fullscreenButton = new Button(ToggleLabel("Fullscreen", game.Settings.Fullscreen),
            new Rectangle(cx, contentY, 240, 30), () =>
            {
                game.Settings.Fullscreen = !game.Settings.Fullscreen;
                game.ApplyDisplaySettings();
                game.Settings.Save();
                RefreshLabels();
            }) { FontSize = 15 };
        display.Children.Add(_fullscreenButton);
        _resolutionButton = new Button(ResolutionLabel(),
            new Rectangle(cx + 250, contentY, 240, 30), () =>
            {
                var list = GameSettings.Resolutions;
                int idx = Array.FindIndex(list, r => r.W == game.Settings.ResolutionWidth && r.H == game.Settings.ResolutionHeight);
                var next = list[(idx + 1 + list.Length) % list.Length];
                (game.Settings.ResolutionWidth, game.Settings.ResolutionHeight) = next;
                game.ApplyDisplaySettings();
                game.Settings.Save();
                RefreshLabels();
            }) { FontSize = 15 };
        display.Children.Add(_resolutionButton);
        display.Children.Add(new Label("Fullscreen is borderless at the DESKTOP resolution;", cx, contentY + 44, 14));
        display.Children.Add(new Label("the resolution setting applies to windowed mode.", cx, contentY + 66, 14));
        display.Children.Add(new Label("Menus and HUD scale automatically with the resolution.", cx, contentY + 88, 14));

        // --- Gameplay tab ---
        var gameplay = new Panel { Bounds = Rectangle.Empty, Background = Color.Transparent, Border = Color.Transparent };
        _damageNumbersButton = new Button(ToggleLabel("Damage Numbers", game.Settings.ShowDamageNumbers),
            new Rectangle(cx, contentY, 240, 30), () =>
            {
                game.Settings.ShowDamageNumbers = !game.Settings.ShowDamageNumbers;
                RefreshLabels();
            }) { FontSize = 15 };
        gameplay.Children.Add(_damageNumbersButton);
        _healthBarsButton = new Button(ToggleLabel("Enemy Health Bars", game.Settings.ShowEnemyHealthBars),
            new Rectangle(cx + 250, contentY, 240, 30), () =>
            {
                game.Settings.ShowEnemyHealthBars = !game.Settings.ShowEnemyHealthBars;
                RefreshLabels();
            }) { FontSize = 15 };
        gameplay.Children.Add(_healthBarsButton);
        _playerListButton = new Button(ToggleLabel("Player List & Pings", game.Settings.ShowPlayerList),
            new Rectangle(cx, contentY + 40, 240, 30), () =>
            {
                game.Settings.ShowPlayerList = !game.Settings.ShowPlayerList;
                RefreshLabels();
            }) { FontSize = 15 };
        gameplay.Children.Add(_playerListButton);
        _zoneThemeButton = new Button(ZoneThemeLabel(),
            new Rectangle(cx + 250, contentY + 40, 240, 30), () =>
            {
                var themes = game.Data.ZoneThemes;
                if (themes.Count == 0) return;
                int idx = themes.FindIndex(t => t.Id == game.Settings.ZoneThemeId);
                game.Settings.ZoneThemeId = themes[(idx + 1) % themes.Count].Id;
                RefreshLabels();
            }) { FontSize = 15 };
        gameplay.Children.Add(_zoneThemeButton);
        gameplay.Children.Add(new Label("Zone theme shapes the NEXT map you host (forest grows big trees).",
            cx, contentY + 80, 14));

        // --- Controls tab ---
        var controls = new Panel { Bounds = Rectangle.Empty, Background = Color.Transparent, Border = Color.Transparent };
        controls.Children.Add(new Label("Click a binding, then press the new key or mouse button.", cx, contentY, 14));
        int rowY = contentY + 24;
        foreach (var action in actions)
        {
            var a = action;
            controls.Children.Add(new Label(ActionName(a), cx, rowY + 6, 16));
            var btn = new Button(game.Input.Bindings[a].Display(), new Rectangle(cx + 260, rowY, 200, 28),
                () => _rebinding = a) { FontSize = 15 };
            _bindButtons[a] = btn;
            controls.Children.Add(btn);
            rowY += 34;
        }
        controls.Children.Add(new Button("Reset Default Keys", new Rectangle(cx, bottomY, 200, 36), () =>
        {
            game.Input.ApplyBindings(null);
            RefreshLabels();
        }));

        _tabPanels = new[] { display, gameplay, controls };
        RefreshLabels();
    }

    private string ZoneThemeLabel()
    {
        var t = _game.Data.ZoneThemes.FirstOrDefault(z => z.Id == _game.Settings.ZoneThemeId);
        return $"Zone: {t?.Name ?? _game.Settings.ZoneThemeId}";
    }

    private static string ToggleLabel(string name, bool on) => $"{name}: {(on ? "ON" : "OFF")}";
    private string ResolutionLabel() =>
        $"Resolution: {_game.Settings.ResolutionWidth}x{_game.Settings.ResolutionHeight}";

    public static string ActionName(InputAction a) => a switch
    {
        InputAction.MoveUp => "Move Up (North)",
        InputAction.MoveDown => "Move Down (South)",
        InputAction.MoveLeft => "Move Left (West)",
        InputAction.MoveRight => "Move Right (East)",
        InputAction.PrimaryAttack => "Primary Attack",
        InputAction.Skill1 => "Skill 1",
        InputAction.Skill2 => "Skill 2",
        InputAction.Skill3 => "Skill 3",
        InputAction.Skill4 => "Skill 4",
        InputAction.Inventory => "Inventory",
        InputAction.SkillMenu => "Skill Menu",
        InputAction.CharacterSheet => "Character Sheet",
        InputAction.Dodge => "Dodge",
        InputAction.Interact => "Interact / Pickup",
        InputAction.Pause => "Pause",
        InputAction.DebugMenu => "Debug Menu",
        _ => a.ToString(),
    };

    private void RefreshLabels()
    {
        foreach (var (action, btn) in _bindButtons)
            btn.Text = _game.Input.Bindings[action].Display();
        if (_damageNumbersButton != null)
        {
            _damageNumbersButton.Text = ToggleLabel("Damage Numbers", _game.Settings.ShowDamageNumbers);
            _healthBarsButton.Text = ToggleLabel("Enemy Health Bars", _game.Settings.ShowEnemyHealthBars);
            _playerListButton.Text = ToggleLabel("Player List & Pings", _game.Settings.ShowPlayerList);
            if (_zoneThemeButton != null) _zoneThemeButton.Text = ZoneThemeLabel();
            _fullscreenButton.Text = ToggleLabel("Fullscreen", _game.Settings.Fullscreen);
            _resolutionButton.Text = ResolutionLabel();
        }
        if (_tabButtons != null)
            for (int i = 0; i < _tabButtons.Length; i++)
                _tabButtons[i].Text = i == _tab ? $"[ {TabNames[i]} ]" : TabNames[i];
    }

    public void Update(InputManager input)
    {
        if (_rebinding.HasValue)
        {
            _bindButtons[_rebinding.Value].Text = "press a key...";
            if (input.TryCaptureBinding(out var binding))
            {
                input.Bindings[_rebinding.Value] = binding;
                _rebinding = null;
                RefreshLabels();
            }
            input.MouseCapturedByUI = true;
            input.KeyboardCapturedByUI = true;
            return; // swallow other UI input while capturing
        }
        _framePanel.Update(input);
        _tabPanels[_tab].Update(input);
        input.KeyboardCapturedByUI = true; // an open options panel owns the keyboard
    }

    public void Draw(SpriteBatch sb)
    {
        _framePanel.Draw(sb);
        _tabPanels[_tab].Draw(sb);
    }
}

/// <summary>Main-menu wrapper around the reusable OptionsPanel.</summary>
public class OptionsScreen : IScreen
{
    private readonly GameMain _game;
    private OptionsPanel _options;
    private Point _builtSize;

    public OptionsScreen(GameMain game)
    {
        _game = game;
        Build();
    }

    private void Build()
    {
        _builtSize = _game.UiScreenSize;
        _options = new OptionsPanel(_game, () => _game.SwitchScreen(new MainMenuScreen(_game)));
    }

    public void Update(float dt)
    {
        // Changing resolution/fullscreen from this very panel moves the layout basis —
        // rebuild so the buttons stay on screen.
        if (_game.UiScreenSize != _builtSize) Build();
        _options.Update(_game.Input);
    }

    public void Draw(SpriteBatch sb) => _options.Draw(sb);
}
