using System.Security.Cryptography;
using System.Text;

namespace ARPG.Data;

/// <summary>
/// A SHA-256 over every <c>Data/**/*.json</c> file (sorted relative paths, CRLF→LF), so
/// two builds can prove they run the same content before they play together. Clients
/// regenerate maps and resolve skills/items from these files — a silent difference
/// desyncs, so the join handshake compares fingerprints (see GameServer.HandleJoin).
/// </summary>
public static class ContentFingerprint
{
    /// <summary>Fingerprint of the most recently loaded GameData ("" before any load).</summary>
    public static string Current { get; internal set; } = "";
    /// <summary>The first 8 hex characters — what the main menu shows.</summary>
    public static string Short => ShortOf(Current);
    public static string ShortOf(string fingerprint) =>
        string.IsNullOrEmpty(fingerprint) ? "--------" : fingerprint[..Math.Min(8, fingerprint.Length)];

    public static string Compute(string dataDir)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (Directory.Exists(dataDir))
        {
            var files = Directory.GetFiles(dataDir, "*.json", SearchOption.AllDirectories)
                .Select(f => (Full: f, Rel: Path.GetRelativePath(dataDir, f).Replace('\\', '/')))
                .OrderBy(f => f.Rel, StringComparer.Ordinal);
            foreach (var (full, rel) in files)
            {
                sha.AppendData(Encoding.UTF8.GetBytes(rel));
                sha.AppendData(new byte[] { 0 });
                string text = File.ReadAllText(full).Replace("\r\n", "\n");
                sha.AppendData(Encoding.UTF8.GetBytes(text));
                sha.AppendData(new byte[] { 0 });
            }
        }
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }
}
