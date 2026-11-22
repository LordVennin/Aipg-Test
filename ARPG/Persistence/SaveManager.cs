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
    public static string CharacterPath(string name)
    {
        var safe = string.Concat(name.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_'));
        if (safe.Length == 0) safe = "character";
        return Path.Combine(GameSettings.SaveDirectory, $"char_{safe.ToLowerInvariant()}.json");
    }

    public static CharacterData LoadCharacter(string name)
    {
        try
        {
            // A save that fails to parse (a crash mid-write on an old build, a bad
            // disk) falls back to the .bak the atomic writer keeps beside it.
            string path = CharacterPath(name);
            var loaded = Json.LoadFileWithBackup<CharacterData>(path, out bool usedBackup);
            if (usedBackup)
                Console.WriteLine($"[Save] '{Path.GetFileName(path)}' was unreadable — loaded the backup ({Path.GetFileName(Json.BackupPath(path))}).");
            loaded?.MigrateLegacySkillIds();
            return loaded;
        }
        catch (Exception e)
        {
            Console.WriteLine($"[Save] Failed to load character '{name}': {e.Message}");
        }
        return null;
    }

    // ---- write coordination -------------------------------------------------------
    // Saves come from the render thread (Save & Exit, disconnect), from background
    // autosaves and from the crash handler. Per file: one lock (never two concurrent
    // writes) and a generation counter (a queued older snapshot never overwrites a
    // newer one that already landed).
    private sealed class FileState
    {
        public readonly object WriteLock = new();
        public long LatestGeneration;
        public Task LastWrite = Task.CompletedTask;
    }
    private static readonly Dictionary<string, FileState> Files = new();
    /// <summary>Completed file writes (sync + background) — diagnostics and tests.</summary>
    public static int WritesCompleted;
    /// <summary>Most writers ever inside one file's write section at once (must stay 1).</summary>
    public static int MaxConcurrentWrites;
    private static int _writersInside;

    private static FileState StateFor(string path)
    {
        lock (Files)
        {
            if (!Files.TryGetValue(path, out var st)) Files[path] = st = new FileState();
            return st;
        }
    }

    private static void WriteGeneration(string path, FileState st, long generation, string json)
    {
        lock (st.WriteLock)
        {
            if (generation < Interlocked.Read(ref st.LatestGeneration)) return; // superseded
            int inside = Interlocked.Increment(ref _writersInside);
            if (inside > MaxConcurrentWrites) MaxConcurrentWrites = inside;
            try { Json.WriteFileAtomic(path, json); }
            finally { Interlocked.Decrement(ref _writersInside); }
            Interlocked.Increment(ref WritesCompleted);
        }
    }

    /// <summary>Save now, on the calling thread (exit, disconnect, crash handler).</summary>
    public static void SaveCharacter(CharacterData character)
    {
        if (character == null) return;
        try
        {
            character.SaveFormat = CharacterData.CurrentSaveFormat;
            string path = CharacterPath(character.Name);
            string json = Json.Save(character);
            var st = StateFor(path);
            WriteGeneration(path, st, Interlocked.Increment(ref st.LatestGeneration), json);
            Console.WriteLine($"[Save] Saved character '{character.Name}'.");
        }
        catch (Exception e)
        {
            Console.WriteLine($"[Save] Failed to save character: {e.Message}");
        }
    }

    /// <summary>Autosave: serialize HERE (the caller's thread owns the character object),
    /// write on a background task. Returns the write task (already-completed on failure).</summary>
    public static Task SaveCharacterInBackground(CharacterData character)
    {
        if (character == null) return Task.CompletedTask;
        try
        {
            character.SaveFormat = CharacterData.CurrentSaveFormat;
            string path = CharacterPath(character.Name);
            string json = Json.Save(character);
            var st = StateFor(path);
            long generation = Interlocked.Increment(ref st.LatestGeneration);
            string name = character.Name;
            var task = Task.Run(() =>
            {
                try { WriteGeneration(path, st, generation, json); }
                catch (Exception e) { Console.WriteLine($"[Save] Autosave of '{name}' failed: {e.Message}"); }
            });
            lock (Files) st.LastWrite = task;
            return task;
        }
        catch (Exception e)
        {
            Console.WriteLine($"[Save] Autosave failed: {e.Message}");
            return Task.CompletedTask;
        }
    }

    /// <summary>Block until every background write has landed (shutdown, tests).</summary>
    public static void FlushBackgroundWrites(int timeoutMs = 3000)
    {
        Task[] pending;
        lock (Files) pending = Files.Values.Select(f => f.LastWrite).ToArray();
        try { Task.WaitAll(pending, timeoutMs); } catch { /* failures were logged by the task */ }
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
                        var c = Json.LoadFileWithBackup<CharacterData>(path, out _);
                        c?.MigrateLegacySkillIds();
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
            // The backup goes too — a deleted character must not come back from it.
            if (File.Exists(Json.BackupPath(path))) File.Delete(Json.BackupPath(path));
            if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
            Console.WriteLine($"[Save] Deleted character '{name}'.");
        }
        catch (Exception e)
        {
            Console.WriteLine($"[Save] Failed to delete character '{name}': {e.Message}");
        }
    }
}
