using FontStashSharp;

namespace ARPG.Render;

/// <summary>
/// Runtime TTF font loading via FontStashSharp (no content pipeline needed).
/// Prefers fonts bundled under Content/Fonts, then falls back to common
/// OS-installed fonts so a fresh clone runs without any binary assets.
///
/// Text sharpness: the UI is laid out in a virtual 1280x720-ish space and drawn through
/// a global scale matrix (see UIScale). Scaling already-rasterized glyph bitmaps makes
/// text blurry at 1080p/1440p/4K, so the font systems rasterize glyphs at
/// fontSize * FontResolutionFactor — kept equal to the current UI scale — while glyph
/// METRICS stay at fontSize. Layout and MeasureString are unchanged; on screen each
/// glyph texel maps ~1:1 to a device pixel, so text stays sharp at any scale,
/// fractional ones included.
/// </summary>
public static class FontManager
{
    private static FontSystem _regular;
    private static FontSystem _bold;
    private static byte[] _regularBytes;
    private static byte[] _boldBytes;
    private static float _resolutionFactor = 1f;

    private static readonly string[] RegularCandidates =
    {
        // Bundled (optional)
        "Content/Fonts/DejaVuSans.ttf",
        // Linux
        "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
        "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf",
        "/usr/share/fonts/truetype/freefont/FreeSans.ttf",
        // Windows
        @"C:\Windows\Fonts\segoeui.ttf",
        @"C:\Windows\Fonts\arial.ttf",
        // macOS
        "/System/Library/Fonts/Supplemental/Arial.ttf",
        "/Library/Fonts/Arial.ttf",
        "/System/Library/Fonts/Helvetica.ttc",
    };

    private static readonly string[] BoldCandidates =
    {
        "Content/Fonts/DejaVuSans-Bold.ttf",
        "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
        "/usr/share/fonts/truetype/liberation/LiberationSans-Bold.ttf",
        "/usr/share/fonts/truetype/freefont/FreeSansBold.ttf",
        @"C:\Windows\Fonts\segoeuib.ttf",
        @"C:\Windows\Fonts\arialbd.ttf",
        "/System/Library/Fonts/Supplemental/Arial Bold.ttf",
        "/Library/Fonts/Arial Bold.ttf",
        "/System/Library/Fonts/Helvetica.ttc",
    };

    public static void Initialize()
    {
        _regularBytes = LoadFirstBytes(RegularCandidates, "regular");
        _boldBytes = LoadFirstBytes(BoldCandidates, "bold");
        RebuildSystems();
    }

    /// <summary>
    /// Re-rasterize all glyphs for a new UI scale (called by UIScale when it changes —
    /// at startup, on window resize and on resolution/fullscreen switches). Cheap when
    /// the factor is unchanged; rebuilds the glyph atlases from the cached TTF bytes
    /// otherwise, so the existing font fallback/loading is untouched.
    /// </summary>
    public static void SetResolutionFactor(float factor)
    {
        factor = Math.Clamp(factor, 0.5f, 4f);
        if (_regularBytes == null || MathF.Abs(factor - _resolutionFactor) < 0.01f)
        {
            _resolutionFactor = factor;
            return;
        }
        _resolutionFactor = factor;
        RebuildSystems();
    }

    private static void RebuildSystems()
    {
        _regular?.Dispose();
        _bold?.Dispose();
        var settings = new FontSystemSettings { FontResolutionFactor = _resolutionFactor };
        _regular = new FontSystem(settings);
        _regular.AddFont(_regularBytes);
        _bold = new FontSystem(settings);
        _bold.AddFont(_boldBytes);
    }

    private static byte[] LoadFirstBytes(string[] candidates, string kind)
    {
        foreach (var candidate in candidates)
        {
            string path = Path.IsPathRooted(candidate)
                ? candidate
                : Path.Combine(AppContext.BaseDirectory, candidate);
            if (!File.Exists(path)) continue;
            try
            {
                var bytes = File.ReadAllBytes(path);
                // Validate the font up-front so fallback still works per-candidate.
                using (var probe = new FontSystem())
                    probe.AddFont(bytes);
                Console.WriteLine($"[Fonts] Using {kind} font: {path}");
                return bytes;
            }
            catch (Exception e)
            {
                Console.WriteLine($"[Fonts] Failed to load {path}: {e.Message}");
            }
        }
        throw new FileNotFoundException(
            $"No usable {kind} TTF font found. Place a font at Content/Fonts/DejaVuSans{(kind == "bold" ? "-Bold" : "")}.ttf next to the executable.");
    }

    public static SpriteFontBase Get(float size) => _regular.GetFont(size);
    public static SpriteFontBase GetBold(float size) => _bold.GetFont(size);
}
