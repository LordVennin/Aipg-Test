using System.Text.Json;
using System.Text.Json.Serialization;

namespace ARPG.Util;

/// <summary>Central JSON configuration used for data files, saves and network payloads.</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        IncludeFields = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static readonly JsonSerializerOptions Compact = new()
    {
        PropertyNameCaseInsensitive = true,
        IncludeFields = true,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Save<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static string SaveCompact<T>(T value) => JsonSerializer.Serialize(value, Compact);
    public static T Load<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    public static T LoadFile<T>(string path) => Load<T>(File.ReadAllText(path));

    /// <summary>Atomic save with a backup: the JSON goes to <c>path.tmp</c> (flushed to
    /// disk), then replaces <c>path</c> in one step, keeping the previous file as
    /// <c>path.bak</c>. A crash mid-write can only ever lose the .tmp.</summary>
    public static void SaveFile<T>(string path, T value) => WriteFileAtomic(path, Save(value));

    public static string BackupPath(string path) => path + ".bak";

    public static void WriteFileAtomic(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
        string tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var sw = new StreamWriter(fs, new System.Text.UTF8Encoding(false)))
        {
            sw.Write(text);
            sw.Flush();
            fs.Flush(flushToDisk: true);
        }
        if (File.Exists(path))
        {
            try { File.Replace(tmp, path, BackupPath(path)); }
            catch (Exception e) when (e is IOException or PlatformNotSupportedException or UnauthorizedAccessException)
            {
                // Some filesystems refuse Replace: copy the backup by hand, then move over.
                File.Copy(path, BackupPath(path), overwrite: true);
                File.Move(tmp, path, overwrite: true);
            }
        }
        else File.Move(tmp, path);
    }

    /// <summary>Load <paramref name="path"/>; if it is missing, empty or fails to parse,
    /// fall back to <c>path.bak</c>. Returns default when neither can be read.
    /// <paramref name="usedBackup"/> reports that the backup is what was loaded.</summary>
    public static T LoadFileWithBackup<T>(string path, out bool usedBackup)
    {
        usedBackup = false;
        Exception mainError = null;
        if (File.Exists(path))
        {
            try
            {
                var value = LoadFile<T>(path);
                if (value != null) return value;
            }
            catch (Exception e) { mainError = e; }
        }
        string bak = BackupPath(path);
        if (File.Exists(bak))
        {
            try
            {
                var value = LoadFile<T>(bak);
                if (value != null)
                {
                    usedBackup = true;
                    return value;
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"[Save] Backup '{Path.GetFileName(bak)}' is unreadable too: {e.Message}");
            }
        }
        if (mainError != null) throw mainError;
        return default;
    }
}
