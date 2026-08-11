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
    public Point ScreenSize => new(_graphics.PreferredBackBufferWidth, _graphics.PreferredBackBufferHeight);

    private IScreen _screen;
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
            if (Window.ClientBounds.Width > 100 && Window.ClientBounds.Height > 100)
            {
                _graphics.PreferredBackBufferWidth = Window.ClientBounds.Width;
                _graphics.PreferredBackBufferHeight = Window.ClientBounds.Height;
                _graphics.ApplyChanges();
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

    // ------------------------------------------------------------------ session startup

    /// <summary>Single player = a local server on a loopback OS-assigned port + a normal client.
    /// Exactly the same simulation path as multiplayer.</summary>
    public void StartSinglePlayer()
    {
        var server = new GameServer(Data, SeedRng.Next());
        if (!server.Start(0))
        {
            SwitchScreen(new MainMenuScreen(this, "Could not start the local server."));
            return;
        }
        StartClientSession(server, "127.0.0.1", server.LocalPort);
    }

    /// <summary>Host = the same local server, but listening on 0.0.0.0:port for remote players.</summary>
    public string StartHost(int port)
    {
        var server = new GameServer(Data, SeedRng.Next());
        if (!server.Start(port))
            return $"Could not listen on port {port} (already in use?).";
        StartClientSession(server, "127.0.0.1", port);
        return null;
    }

    public string StartJoin(string ip, int port)
    {
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
        Input.BeginFrame();
        _screen?.Update(dt);
        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(new Color(16, 17, 22));
        _spriteBatch.Begin(samplerState: SamplerState.PointClamp);
        _screen?.Draw(_spriteBatch);
        _spriteBatch.End();
        base.Draw(gameTime);
    }

    protected override void OnExiting(object sender, ExitingEventArgs args)
    {
        (_screen as PlayScreen)?.Shutdown();
        Settings?.Save();
        base.OnExiting(sender, args);
    }
}
