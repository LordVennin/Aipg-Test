using ARPG.Core;
using ARPG.Data;
using ARPG.Net;
using ARPG.Persistence;
using ARPG.Render;
using ARPG.Server;
using ARPG.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ARPG;

/// <summary>
/// Application shell: window, game loop, screen switching and session startup.
/// Gameplay lives in the server simulation + client; UI lives in the screen classes.
/// </summary>
public class GameMain : Game
{
    public static GameMain Instance { get; private set; }

    private readonly GraphicsDeviceManager _graphics;
    private SpriteBatch _spriteBatch;

    public InputManager Input { get; } = new();
    public GameSettings Settings { get; private set; }
    public GameData Data { get; private set; }
    /// <summary>ACTUAL backbuffer size. In borderless fullscreen the display may refuse the
    /// preferred resolution, so layout must follow what the device really allocated —
    /// otherwise menus lay out for a resolution that doesn't exist and end up off screen.</summary>
    public Point ScreenSize => GraphicsDevice != null
        ? new(GraphicsDevice.PresentationParameters.BackBufferWidth, GraphicsDevice.PresentationParameters.BackBufferHeight)
        : new(_graphics.PreferredBackBufferWidth, _graphics.PreferredBackBufferHeight);
    /// <summary>Screen size in UI (virtual) space — what menus/HUD lay out against.</summary>
    public Point UiScreenSize => new((int)(ScreenSize.X / UIScale.Value), (int)(ScreenSize.Y / UIScale.Value));

    private IScreen _screen;
    private bool _applyingDisplayChange;
    private static readonly Random SeedRng = new();

    /// <summary>Start straight into single player (command line --sp), for quick dev iteration.</summary>
    public bool AutoSinglePlayer { get; init; }

    public GameMain()
    {
        Instance = this;
        _graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = 1280,
            PreferredBackBufferHeight = 720,
        };
        IsMouseVisible = true;
        Window.Title = "Scrollbound — ARPG Prototype";
        Window.AllowUserResizing = true;
        Window.ClientSizeChanged += (_, _) =>
        {
            // ApplyChanges() itself raises ClientSizeChanged — guard against recursing
            // (toggling fullscreen would otherwise overflow the stack).
            if (_applyingDisplayChange || _graphics.IsFullScreen) return;
            if (Window.ClientBounds.Width > 100 && Window.ClientBounds.Height > 100)
            {
                _applyingDisplayChange = true;
                _graphics.PreferredBackBufferWidth = Window.ClientBounds.Width;
                _graphics.PreferredBackBufferHeight = Window.ClientBounds.Height;
                _graphics.ApplyChanges();
                _applyingDisplayChange = false;
            }
        };
    }

    protected override void LoadContent()
    {
        _spriteBatch = new SpriteBatch(GraphicsDevice);
        TextureGen.Initialize(GraphicsDevice);
        SpriteGen.Initialize(GraphicsDevice);
        FontManager.Initialize();
        Data = GameData.LoadDefault();
        Settings = GameSettings.Load();
        Audio.AudioManager.Initialize();
        Audio.AudioManager.SetVolume(Settings.SoundVolume);
        ApplyDisplaySettings();
        Input.ApplyBindings(Settings.Bindings);
        Window.TextInput += (_, e) => Input.PushTypedChar(e.Character);
        _screen = new MainMenuScreen(this);
        if (AutoSinglePlayer) StartSinglePlayer();
    }

    public void SwitchScreen(IScreen screen)
    {
        Input.ClearTypedChars();
        _screen = screen;
    }

    /// <summary>Apply the persisted resolution + fullscreen mode (borderless).</summary>
    public void ApplyDisplaySettings()
    {
        _applyingDisplayChange = true;
        if (Settings.Fullscreen)
        {
            // Borderless fullscreen ALWAYS uses the desktop resolution: a backbuffer that
            // differs from the display gets stretched by SDL, which desyncs the mouse and
            // off-centers menus. The chosen resolution applies to windowed mode only.
            var display = GraphicsAdapter.DefaultAdapter.CurrentDisplayMode;
            _graphics.PreferredBackBufferWidth = display.Width;
            _graphics.PreferredBackBufferHeight = display.Height;
        }
        else
        {
            _graphics.PreferredBackBufferWidth = Math.Max(640, Settings.ResolutionWidth);
            _graphics.PreferredBackBufferHeight = Math.Max(480, Settings.ResolutionHeight);
        }
        _graphics.HardwareModeSwitch = false; // borderless fullscreen — no display mode change
        _graphics.IsFullScreen = Settings.Fullscreen;
        _graphics.ApplyChanges();
        _applyingDisplayChange = false;
        UIScale.Update(ScreenSize.X, ScreenSize.Y);
    }

    // ------------------------------------------------------------------ session startup

    /// <summary>Single player = a local server on a loopback OS-assigned port + a normal client.
    /// Exactly the same simulation path as multiplayer.</summary>
    /// <summary>Theme for maps WE host: env override, else the settings choice.</summary>
    private string HostZoneThemeId =>
        Environment.GetEnvironmentVariable("ARPG_THEME") ?? Settings.ZoneThemeId;

    /// <summary>Hosted worlds run the CAMPAIGN loop (hub sanctum + generated runs).
    /// ARPG_ARENA=1 keeps the old demo arena for debugging/screenshot harnesses.</summary>
    private static bool HostCampaign =>
        Environment.GetEnvironmentVariable("ARPG_ARENA") != "1";

    /// <summary>Every session start stops at the CHARACTER SELECT screen first: pick a
    /// saved character (or create one) and the interrupted action resumes with it. The
    /// chosen name becomes Settings.PlayerName. Dev/automation boots (--sp) skip the
    /// screen — a default warrior is minted if the name has no save — so harnesses
    /// never stall.</summary>
    private bool RouteThroughSelect(Action proceed)
    {
        if (AutoSinglePlayer)
        {
            if (SaveManager.LoadCharacter(Settings.PlayerName) == null)
                SaveManager.SaveCharacter(Sim.CharacterData.CreateNew(Data, Settings.PlayerName));
            return false;
        }
        SwitchScreen(new UI.CharacterSelectScreen(this, proceed));
        return true;
    }

    public void StartSinglePlayer()
    {
        if (RouteThroughSelect(StartSinglePlayerNow)) return;
        StartSinglePlayerNow();
    }

    private void StartSinglePlayerNow()
    {
        var server = new GameServer(Data, SeedRng.Next(), HostZoneThemeId, campaign: HostCampaign);
        if (!server.Start(0))
        {
            SwitchScreen(new MainMenuScreen(this, "Could not start the local server."));
            return;
        }
        server.StartLoop(); // simulation runs on its own thread, decoupled from rendering
        StartClientSession(server, "127.0.0.1", server.LocalPort);
    }

    /// <summary>Host = the same local server, but listening on 0.0.0.0:port for remote players.</summary>
    public string StartHost(int port)
    {
        if (RouteThroughSelect(() =>
        {
            string err = StartHostNow(port);
            if (err != null) SwitchScreen(new MainMenuScreen(this, err));
        })) return null;
        return StartHostNow(port);
    }

    private string StartHostNow(int port)
    {
        var server = new GameServer(Data, SeedRng.Next(), HostZoneThemeId, campaign: HostCampaign);
        if (!server.Start(port))
            return $"Could not listen on port {port} (already in use?).";
        server.StartLoop(); // simulation runs on its own thread, decoupled from rendering
        StartClientSession(server, "127.0.0.1", port);
        return null;
    }

    public string StartJoin(string ip, int port)
    {
        if (RouteThroughSelect(() =>
        {
            string err = StartClientSession(null, ip, port);
            if (err != null) SwitchScreen(new MainMenuScreen(this, err));
        })) return null;
        return StartClientSession(null, ip, port);
    }

    private string StartClientSession(GameServer server, string ip, int port)
    {
        var saved = SaveManager.LoadCharacter(Settings.PlayerName);
        var client = new GameClient(Data, Settings.PlayerName, saved);
        if (!client.Connect(ip, port, out string error))
        {
            server?.Stop();
            return error ?? "Could not connect.";
        }
        SwitchScreen(new PlayScreen(this, server, client));
        return null;
    }

    public void ExitGame() => Exit();

    // ------------------------------------------------------------------ loop

    protected override void Update(GameTime gameTime)
    {
        float dt = Math.Min((float)gameTime.ElapsedGameTime.TotalSeconds, 0.1f);
        UIScale.Update(ScreenSize.X, ScreenSize.Y);
        Input.BeginFrame();
        _screen?.Update(dt);
        base.Update(gameTime);
    }

    /// <summary>Lightmap composite: scene pixel x lightmap pixel (alpha untouched).</summary>
    private static readonly BlendState MultiplyBlend = new()
    {
        ColorSourceBlend = Blend.DestinationColor,
        ColorDestinationBlend = Blend.Zero,
        AlphaSourceBlend = Blend.Zero,
        AlphaDestinationBlend = Blend.One,
    };

    protected override void Draw(GameTime gameTime)
    {
        var uiMatrix = Matrix.CreateScale(UIScale.Value);
        if (_screen is PlayScreen play)
        {
            // The lightmap renders FIRST (a render-target switch after the world pass
            // would discard the backbuffer), then world -> multiply -> UI on top unlit.
            var lightmap = play.PrepareLightmap(GraphicsDevice, _spriteBatch);
            GraphicsDevice.Clear(play.BackgroundColor);
            // World renders unscaled (its own camera); menus/HUD render through the UI scale.
            _spriteBatch.Begin(samplerState: SamplerState.PointClamp);
            play.DrawWorld(_spriteBatch);
            _spriteBatch.End();
            if (lightmap != null)
            {
                _spriteBatch.Begin(blendState: MultiplyBlend, samplerState: SamplerState.LinearClamp);
                _spriteBatch.Draw(lightmap, new Rectangle(0, 0, ScreenSize.X, ScreenSize.Y), Color.White);
                _spriteBatch.End();
            }
            _spriteBatch.Begin(samplerState: SamplerState.PointClamp, transformMatrix: uiMatrix);
            play.DrawUI(_spriteBatch);
            _spriteBatch.End();
        }
        else
        {
            GraphicsDevice.Clear(new Color(16, 17, 22));
            _spriteBatch.Begin(samplerState: SamplerState.PointClamp, transformMatrix: uiMatrix);
            _screen?.Draw(_spriteBatch);
            _spriteBatch.End();
        }
        base.Draw(gameTime);
    }

    protected override void OnExiting(object sender, ExitingEventArgs args)
    {
        (_screen as PlayScreen)?.Shutdown();
        Settings?.Save();
        base.OnExiting(sender, args);
    }
}
