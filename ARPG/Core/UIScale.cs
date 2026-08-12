namespace ARPG.Core;

/// <summary>
/// Global UI scale. Menus and HUD are laid out in a 1280x720-ish virtual space and drawn
/// through a scale matrix, so they grow on high resolutions and shrink on small windows
/// instead of staying tiny/huge. World rendering is NOT scaled (it has its own camera).
/// </summary>
public static class UIScale
{
    public static float Value { get; private set; } = 1f;

    /// <summary>Recompute from the current backbuffer size (called once per frame). When the
    /// scale changes, fonts re-rasterize at the new final pixel size so text stays sharp
    /// (see FontManager.SetResolutionFactor).</summary>
    public static void Update(int screenWidth, int screenHeight)
    {
        Value = Math.Clamp(MathF.Min(screenWidth / 1280f, screenHeight / 720f), 0.75f, 2f);
        Render.FontManager.SetResolutionFactor(Value);
    }
}
