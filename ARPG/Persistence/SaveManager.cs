using ARPG.Core;
using ARPG.Sim;
using ARPG.Util;

namespace ARPG.Persistence;

/// <summary>
/// Local character persistence. The save file is the same CharacterData JSON the server
/// syncs to the client, so items keep base id, item level, rarity, modifier rolls,
/// modifier limit and instance id exactly as generated.
/// </summary>
public static class SaveManager
{
    private static string CharacterPath(string name)
    {
        var safe = string.Concat(name.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_'));
        if (safe.Length == 0) safe = "character";
        return Path.Combine(GameSettings.SaveDirectory, $"char_{safe.ToLowerInvariant()}.json");
    }

    public static CharacterData LoadCharacter(string name)
    {
        try
        {
            string path = CharacterPath(name);
            if (File.Exists(path))
                return Json.LoadFile<CharacterData>(path);
        }
        catch (Exception e)
        {
            Console.WriteLine($"[Save] Failed to load character '{name}': {e.Message}");
        }
        return null;
    }

    public static void SaveCharacter(CharacterData character)
    {
        if (character == null) return;
        try
        {
            Json.SaveFile(CharacterPath(character.Name), character);
            Console.WriteLine($"[Save] Saved character '{character.Name}'.");
        }
        catch (Exception e)
        {
            Console.WriteLine($"[Save] Failed to save character: {e.Message}");
        }
    }
}
