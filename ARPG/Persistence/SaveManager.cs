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

    /// <summary>Every saved character in the save directory, newest-played first
    /// (file write time). Unreadable files are skipped, never fatal.</summary>
    public static List<CharacterData> ListCharacters()
    {
        var found = new List<(CharacterData Character, DateTime Touched)>();
        try
        {
            if (Directory.Exists(GameSettings.SaveDirectory))
                foreach (var path in Directory.GetFiles(GameSettings.SaveDirectory, "char_*.json"))
                {
                    try
                    {
                        var c = Json.LoadFile<CharacterData>(path);
                        if (c?.Name != null) found.Add((c, File.GetLastWriteTimeUtc(path)));
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine($"[Save] Skipping unreadable save '{Path.GetFileName(path)}': {e.Message}");
                    }
                }
        }
        catch (Exception e)
        {
            Console.WriteLine($"[Save] Could not list saves: {e.Message}");
        }
        return found.OrderByDescending(f => f.Touched).Select(f => f.Character).ToList();
    }

    public static bool CharacterExists(string name) => File.Exists(CharacterPath(name));

    public static void DeleteCharacter(string name)
    {
        try
        {
            string path = CharacterPath(name);
            if (File.Exists(path)) File.Delete(path);
            Console.WriteLine($"[Save] Deleted character '{name}'.");
        }
        catch (Exception e)
        {
            Console.WriteLine($"[Save] Failed to delete character '{name}': {e.Message}");
        }
    }
}
