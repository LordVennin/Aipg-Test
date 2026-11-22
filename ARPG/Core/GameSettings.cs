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
    /// <summary>Also draw stun/chill buildup strips under enemies' small overhead
    /// health bars (they always show on the top-of-screen hover display).</summary>
    public bool ShowOverheadStatusBars { get; set; }
    /// <summary>HUD style: false = the classic health/mana ORBS in the corners, true =
    /// long horizontal BARS along the bottom edge either side of the hotbar.</summary>
    public bool HudBars { get; set; }
    /// <summary>Zone theme used when hosting/starting single player. Themes shape map
    /// GENERATION (the forest grows big trees), so this is decided before the map exists
    /// and replicated to joining clients.</summary>
    public string ZoneThemeId { get; set; } = "forest";
    /// <summary>What a hosted/single-player world starts as: "story" (the road, then the
    /// ruins hub and the lower levels) or "test" (the sanctum hub and the test maps).</summary>
    public string StartMode { get; set; } = "story";
    /// <summary>Master sound volume, 0..1 (cycled in Options → Gameplay).</summary>
    public float SoundVolume { get; set; } = 0.8f;
    /// <summary>The alpha-blend lighting pass in dark zones (sanctum, graveyard, tomb).</summary>
    public bool Lighting { get; set; } = true;
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
            // Falls back to settings.json.bak when the main file is corrupt.
            var loaded = Json.LoadFileWithBackup<GameSettings>(SettingsPath, out bool usedBackup);
            if (usedBackup) Console.WriteLine("[Settings] settings.json was unreadable — loaded the backup.");
            if (loaded != null) return loaded;
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
    public const int ProtocolVersion = 49; // v49: SkillBookTurnInRequest, ZoneState carries SkillsGated. v48: JoinRequest carries SimVersion + the content fingerprint; the host may attach a reason to a disconnect
    /// <summary>Bump on ANY change to map generation, collision or simulation rules:
    /// clients rebuild maps from a seed, so two builds that generate differently must
    /// refuse each other at the handshake instead of silently desyncing.
    /// v1 (batch 85): first version; covers batches 82-84's GameMap/barrier changes.</summary>
    public const int SimVersion = 1;
    public const int MaxPlayers = 4;
    public const string ConnectionKey = "ARPG-Proto";
}
