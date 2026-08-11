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
    public Dictionary<string, string> Bindings { get; set; } = new();

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
    public const int ProtocolVersion = 4; // v4: shields, off-hand appearance, block events
    public const int MaxPlayers = 4;
    public const string ConnectionKey = "ARPG-Proto";
}
