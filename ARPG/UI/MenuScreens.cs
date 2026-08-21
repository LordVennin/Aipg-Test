using FontStashSharp;
using ARPG.Core;
using ARPG.Persistence;
using ARPG.Render;
using ARPG.Sim;
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
            // Disconnect reasons can carry multi-line diagnostics — center each line.
            float my = 195;
            foreach (var line in _message.Split('\n'))
            {
                var mSize = subFont.MeasureString(line);
                sb.DrawString(subFont, line, new Vector2(size.X / 2f - mSize.X / 2, my), new Color(255, 120, 110));
                my += 22;
            }
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
        int y = size.Y / 2 - 170;
        _panel = new Panel { Bounds = new Rectangle(cx - 30, y - 60, 400, 452) };
        _panel.Children.Add(new Label("Host Game", cx, y - 40, 26, bold: true));
        _panel.Children.Add(new Label("Player Name", cx, y + 8, 15));
        _name = new TextInput(new Rectangle(cx, y + 30, 340, 36), game.Settings.PlayerName);
        _panel.Children.Add(_name);
        _panel.Children.Add(new Label($"Port (default {GameNetConfig.DefaultPort})", cx, y + 78, 15));
        _port = new TextInput(new Rectangle(cx, y + 100, 340, 36), game.Settings.LastPort.ToString()) { NumericOnly = true, MaxLength = 5 };
        _panel.Children.Add(_port);
        // The host's own addresses (LAN + VPN adapters like Meshnet/ZeroTier): this is
        // exactly what a friend types into Join — and proof the game sees the adapter.
        _panel.Children.Add(new Label("Friends connect to one of YOUR addresses:", cx, y + 148, 15));
        int addrY = y + 168;
        foreach (var line in LocalAddressLines())
        {
            _panel.Children.Add(new Label(line, cx, addrY, 14) { Color = new Color(240, 200, 90) });
            addrY += 20;
        }
        _error = new Label("", cx, y + 230, 15) { Color = new Color(255, 120, 110) };
        _panel.Children.Add(_error);
        _panel.Children.Add(new Button("Start Hosting", new Rectangle(cx, y + 262, 340, 42), StartHosting));
        _panel.Children.Add(new Button("Back", new Rectangle(cx, y + 314, 340, 36), () => game.SwitchScreen(new MainMenuScreen(game))));
    }

    /// <summary>Up to three lines of this machine's IPv4 addresses on live non-loopback
    /// interfaces — LAN and VPN (Meshnet/ZeroTier/Tailscale) alike.</summary>
    private static List<string> LocalAddressLines()
    {
        var addrs = new List<string>();
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up ||
                    ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                    continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        addrs.Add(ua.Address.ToString());
            }
        }
        catch { /* no interface access: show the fallback line below */ }
        addrs = addrs.Distinct().Take(6).ToList();
        if (addrs.Count == 0) return new List<string> { "(no LAN/VPN address found)" };
        var lines = new List<string>();
        for (int i = 0; i < addrs.Count; i += 2)
            lines.Add(string.Join("   ·   ", addrs.Skip(i).Take(2)));
        return lines.Take(3).ToList();
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
    /// <summary>Controls tab scroll state: the binding rows overflow the panel, so the
    /// wheel slides them; rows outside the content window are hidden.</summary>
    private readonly List<(UIElement element, int baseY)> _controlRows = new();
    private int _controlsScroll;
    private int _controlsContentTop, _controlsContentBottom, _controlRowsHeight;
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
        controls.Children.Add(new Label("Click a binding, then press the new key or mouse button. Scroll for more.", cx, contentY, 14));
        int rowY = contentY + 24;
        _controlsContentTop = rowY;
        _controlsContentBottom = bottomY - 8;
        foreach (var action in actions)
        {
            var a = action;
            var lbl = new Label(ActionName(a), cx, rowY + 6, 16);
            controls.Children.Add(lbl);
            _controlRows.Add((lbl, rowY + 6));
            var btn = new Button(game.Input.Bindings[a].Display(), new Rectangle(cx + 260, rowY, 200, 28),
                () => _rebinding = a) { FontSize = 15 };
            _bindButtons[a] = btn;
            controls.Children.Add(btn);
            _controlRows.Add((btn, rowY));
            rowY += 34;
        }
        _controlRowsHeight = rowY - _controlsContentTop;
        ApplyControlsScroll();
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
        InputAction.CommandSummons => "Command Summons",
        InputAction.CycleSummonFocus => "Cycle Summon Focus",
        InputAction.Interact => "Interact / Pickup",
        InputAction.HealthPotion => "Health Potion",
        InputAction.ManaPotion => "Mana Potion",
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
        // Controls tab: the binding list is taller than the panel — wheel to scroll.
        if (_tab == 2 && input.ScrollDelta != 0)
        {
            _controlsScroll -= input.ScrollDelta / 4;
            ApplyControlsScroll();
        }
        _framePanel.Update(input);
        _tabPanels[_tab].Update(input);
        input.KeyboardCapturedByUI = true; // an open options panel owns the keyboard
    }

    /// <summary>Slide the binding rows by the scroll offset and hide rows that fall
    /// outside the content window (so they can't overlap the bottom buttons).</summary>
    private void ApplyControlsScroll()
    {
        int maxScroll = Math.Max(0, _controlRowsHeight - (_controlsContentBottom - _controlsContentTop));
        _controlsScroll = Math.Clamp(_controlsScroll, 0, maxScroll);
        foreach (var (element, baseY) in _controlRows)
        {
            element.Bounds = new Rectangle(element.Bounds.X, baseY - _controlsScroll,
                element.Bounds.Width, element.Bounds.Height);
            element.Visible = element.Bounds.Y >= _controlsContentTop - 4 &&
                              element.Bounds.Y + 30 <= _controlsContentBottom + 4;
        }
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

/// <summary>
/// First-run character creation: pick a starting class (a starting KIT — classes never
/// gate items or skills later), a body style and a skin tone, with a live animated
/// preview of the body sprite. Shown once per player name; the finished character is
/// saved and the interrupted action (single player / host / join) re-entered.
/// </summary>
public class CharacterCreateScreen : IScreen
{
    private readonly GameMain _game;
    private readonly Action _proceed;
    private Panel _panel;
    private Button[] _classButtons;
    private Button _maleButton, _femaleButton;
    private Point _builtSize;

    private int _classIdx;
    private byte _body;      // 0 = male, 1 = female
    private byte _tone = 2;

    // Layout anchors captured by Build() so Draw/Update can place the custom widgets
    // (tone swatches, preview, description) that aren't UIElements.
    private Rectangle _toneRects0; // first swatch; the rest step right from it
    private Rectangle _previewRect;
    private int _descX, _descY, _descWidth;

    private const int ToneSwatch = 34, ToneGap = 8;

    public CharacterCreateScreen(GameMain game, Action proceed)
    {
        _game = game;
        _proceed = proceed;
        Build();
    }

    private void Build()
    {
        var size = _game.UiScreenSize;
        _builtSize = size;
        int w = 720, h = 470;
        int px = size.X / 2 - w / 2, py = Math.Max(12, size.Y / 2 - h / 2);
        _panel = new Panel { Bounds = new Rectangle(px, py, w, h) };
        _panel.Children.Add(new Label("Create Your Character", px + 24, py + 16, 26, bold: true));
        _panel.Children.Add(new Label($"Playing as '{_game.Settings.PlayerName}'", px + 24, py + 52, 14)
            { Color = new Color(150, 145, 130) });

        // --- left column: class cards ---
        int colX = px + 24, colY = py + 92;
        _panel.Children.Add(new Label("Class", colX, colY - 24, 16, bold: true));
        var classes = _game.Data.Classes;
        _classButtons = new Button[classes.Count];
        for (int i = 0; i < classes.Count; i++)
        {
            int idx = i;
            _classButtons[i] = new Button(classes[i].Name ?? classes[i].Id,
                new Rectangle(colX, colY + i * 52, 190, 42),
                () => { _classIdx = idx; RefreshSelection(); });
            _panel.Children.Add(_classButtons[i]);
        }
        _descX = colX;
        _descY = colY + classes.Count * 52 + 14;
        _descWidth = 300;

        // --- middle column: body + skin tone ---
        int midX = px + 250;
        _panel.Children.Add(new Label("Body", midX, colY - 24, 16, bold: true));
        _maleButton = new Button("Male", new Rectangle(midX, colY, 100, 36),
            () => { _body = 0; RefreshSelection(); });
        _femaleButton = new Button("Female", new Rectangle(midX + 108, colY, 100, 36),
            () => { _body = 1; RefreshSelection(); });
        _panel.Children.Add(_maleButton);
        _panel.Children.Add(_femaleButton);
        _panel.Children.Add(new Label("Skin Tone", midX, colY + 58, 16, bold: true));
        _toneRects0 = new Rectangle(midX, colY + 82, ToneSwatch, ToneSwatch);

        // --- right column: live preview ---
        _previewRect = new Rectangle(px + w - 200, py + 88, 168, 240);

        // --- bottom row ---
        _panel.Children.Add(new Button("Begin", new Rectangle(px + w - 224, py + h - 60, 200, 42), Create));
        _panel.Children.Add(new Button("Back", new Rectangle(px + 24, py + h - 60, 140, 36),
            () => _game.SwitchScreen(new MainMenuScreen(_game))));
        RefreshSelection();
    }

    private void RefreshSelection()
    {
        var classes = _game.Data.Classes;
        for (int i = 0; i < _classButtons.Length; i++)
        {
            bool sel = i == _classIdx;
            _classButtons[i].Text = sel ? $"[ {classes[i].Name ?? classes[i].Id} ]" : classes[i].Name ?? classes[i].Id;
            _classButtons[i].Background = sel ? new Color(84, 76, 56) : new Color(52, 48, 40);
        }
        _maleButton.Text = _body == 0 ? "[ Male ]" : "Male";
        _maleButton.Background = _body == 0 ? new Color(84, 76, 56) : new Color(52, 48, 40);
        _femaleButton.Text = _body == 1 ? "[ Female ]" : "Female";
        _femaleButton.Background = _body == 1 ? new Color(84, 76, 56) : new Color(52, 48, 40);
    }

    private void Create()
    {
        var classes = _game.Data.Classes;
        string classId = classes.Count > 0 ? classes[Math.Clamp(_classIdx, 0, classes.Count - 1)].Id : "warrior";
        SaveManager.SaveCharacter(CharacterData.CreateNew(_game.Data, _game.Settings.PlayerName, classId, _body, _tone));
        _proceed();
    }

    private Rectangle ToneRect(int i) =>
        new(_toneRects0.X + i * (ToneSwatch + ToneGap), _toneRects0.Y, ToneSwatch, ToneSwatch);

    public void Update(float dt)
    {
        if (_game.UiScreenSize != _builtSize) Build(); // resolution changed under us
        var input = _game.Input;
        if (input.MouseLeftPressed)
            for (int i = 0; i < SpriteGen.SkinTones.Length; i++)
                if (ToneRect(i).Contains(input.MousePosition))
                {
                    _tone = (byte)i;
                    input.MouseCapturedByUI = true;
                    break;
                }
        _panel.Update(input);
    }

    public void Draw(SpriteBatch sb)
    {
        _panel.Draw(sb);

        // Class description under the cards, word-wrapped.
        var cls = _game.Data.Classes.Count > 0 ? _game.Data.Classes[Math.Clamp(_classIdx, 0, _game.Data.Classes.Count - 1)] : null;
        if (cls?.Description != null)
        {
            var font = FontManager.Get(14);
            float dy = _descY;
            foreach (var line in TextUtil.WrapToWidth(cls.Description, font, _descWidth))
            {
                sb.DrawString(font, line, new Vector2(_descX, dy), new Color(185, 178, 160));
                dy += 20;
            }
        }

        // Skin tone swatches, selected one ringed gold.
        for (int i = 0; i < SpriteGen.SkinTones.Length; i++)
        {
            var r = ToneRect(i);
            sb.Draw(TextureGen.Pixel, r, SpriteGen.SkinTones[i]);
            var ring = i == _tone ? new Color(240, 200, 90) : new Color(70, 66, 56);
            int t = i == _tone ? 3 : 1;
            sb.Draw(TextureGen.Pixel, new Rectangle(r.X, r.Y, r.Width, t), ring);
            sb.Draw(TextureGen.Pixel, new Rectangle(r.X, r.Bottom - t, r.Width, t), ring);
            sb.Draw(TextureGen.Pixel, new Rectangle(r.X, r.Y, t, r.Height), ring);
            sb.Draw(TextureGen.Pixel, new Rectangle(r.Right - t, r.Y, t, r.Height), ring);
        }

        // Live preview: the actual in-game body sprite at 6x, walking in place.
        sb.Draw(TextureGen.Pixel, _previewRect, new Color(15, 15, 20));
        var pborder = new Color(90, 84, 60);
        sb.Draw(TextureGen.Pixel, new Rectangle(_previewRect.X, _previewRect.Y, _previewRect.Width, 2), pborder);
        sb.Draw(TextureGen.Pixel, new Rectangle(_previewRect.X, _previewRect.Bottom - 2, _previewRect.Width, 2), pborder);
        sb.Draw(TextureGen.Pixel, new Rectangle(_previewRect.X, _previewRect.Y, 2, _previewRect.Height), pborder);
        sb.Draw(TextureGen.Pixel, new Rectangle(_previewRect.Right - 2, _previewRect.Y, 2, _previewRect.Height), pborder);
        var frames = SpriteGen.GetPlayerFrames(_body, _tone);
        if (frames != null)
        {
            // Classic 4-beat walk: idle, stride A, idle, stride B.
            int[] cycle = { 0, 1, 0, 2 };
            var tex = frames[cycle[(int)(Environment.TickCount64 / 220 % 4)]];
            int scale = 6;
            int tw = tex.Width * scale, th = tex.Height * scale;
            int cx = _previewRect.Center.X, footY = _previewRect.Bottom - 28;
            sb.Draw(TextureGen.Circle32, new Rectangle(cx - tw / 3, footY - 10, tw * 2 / 3, 20), new Color(0, 0, 0, 90));
            sb.Draw(tex, new Rectangle(cx - tw / 2, footY - th, tw, th), Color.White);
        }
    }
}
