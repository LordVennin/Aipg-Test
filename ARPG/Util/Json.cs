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

    public static void SaveFile<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
        File.WriteAllText(path, Save(value));
    }
}
