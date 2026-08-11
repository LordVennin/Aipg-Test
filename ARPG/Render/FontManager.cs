using FontStashSharp;

namespace ARPG.Render;

/// <summary>
/// Runtime TTF font loading via FontStashSharp (no content pipeline needed).
/// Prefers fonts bundled under Content/Fonts, then falls back to common
/// OS-installed fonts so a fresh clone runs without any binary assets.
/// </summary>
public static class FontManager
{
    private static FontSystem _regular;
    private static FontSystem _bold;

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
        _regular = LoadFirst(RegularCandidates, "regular");
        _bold = LoadFirst(BoldCandidates, "bold");
    }

    private static FontSystem LoadFirst(string[] candidates, string kind)
    {
        foreach (var candidate in candidates)
        {
            string path = Path.IsPathRooted(candidate)
                ? candidate
                : Path.Combine(AppContext.BaseDirectory, candidate);
            if (!File.Exists(path)) continue;
            try
            {
                var system = new FontSystem();
                system.AddFont(File.ReadAllBytes(path));
                Console.WriteLine($"[Fonts] Using {kind} font: {path}");
                return system;
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
