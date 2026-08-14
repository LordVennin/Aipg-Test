using ARPG.Util;

namespace ARPG.Core;

/// <summary>Persisted user settings: keybindings, player name and last used connection info.</summary>
public class GameSettings
{
    public string PlayerName { get; set; } = "Exile";
    public string LastJoinIp { get; set; } = "127.0.0.1";
    public int LastPort { get; set; } = GameNetConfig.DefaultPort;
    public bool ShowDamageNumbers { get; set; } = true;
    public bool ShowEnemyHealthBars { get; set; } = true;
    /// <summary>Show the multiplayer player list with pings (bottom left of the HUD).</summary>
    public bool ShowPlayerList { get; set; } = true;
    /// <summary>Zone theme used when hosting/starting single player. Themes shape map
    /// GENERATION (the forest grows big trees), so this is decided before the map exists
    /// and replicated to joining clients.</summary>
    public string ZoneThemeId { get; set; } = "forest";
    public bool Fullscreen { get; set; }
    public int ResolutionWidth { get; set; } = 1280;
    public int ResolutionHeight { get; set; } = 720;
    public Dictionary<string, string> Bindings { get; set; } = new();

    /// <summary>Selectable window resolutions (Display options cycle through these).</summary>
    public static readonly (int W, int H)[] Resolutions =
    {
        (1280, 720), (1366, 768), (1600, 900), (1920, 1080), (2560, 1440),
    };

    public static string SaveDirectory =>
        Path.Combine(AppContext.BaseDirectory, "Saves");

    private static string SettingsPath => Path.Combine(SaveDirectory, "settings.json");

    public static GameSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return Json.LoadFile<GameSettings>(SettingsPath) ?? new GameSettings();
        }
        catch (Exception e)
        {
            Console.WriteLine($"[Settings] Failed to load settings, using defaults: {e.Message}");
        }
        return new GameSettings();
    }

    public void Save()
    {
        try
        {
            Json.SaveFile(SettingsPath, this);
        }
        catch (Exception e)
        {
            Console.WriteLine($"[Settings] Failed to save settings: {e.Message}");
        }
    }
}

public static class GameNetConfig
{
    public const int DefaultPort = 7777;
    public const int ProtocolVersion = 19; // v19: attributes + energy shield in health sync
    public const int MaxPlayers = 4;
    public const string ConnectionKey = "ARPG-Proto";
}
