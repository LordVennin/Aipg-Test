using FontStashSharp;

namespace ARPG.Render;

/// <summary>Runtime TTF font loading via FontStashSharp (no content pipeline needed).</summary>
public static class FontManager
{
    private static FontSystem _regular;
    private static FontSystem _bold;

    public static void Initialize()
    {
        _regular = new FontSystem();
        _bold = new FontSystem();
        string dir = Path.Combine(AppContext.BaseDirectory, "Content", "Fonts");
        _regular.AddFont(File.ReadAllBytes(Path.Combine(dir, "DejaVuSans.ttf")));
        _bold.AddFont(File.ReadAllBytes(Path.Combine(dir, "DejaVuSans-Bold.ttf")));
    }

    public static SpriteFontBase Get(float size) => _regular.GetFont(size);
    public static SpriteFontBase GetBold(float size) => _bold.GetFont(size);
}
