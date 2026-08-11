using Microsoft.Xna.Framework;
using NumVec2 = System.Numerics.Vector2;

namespace ARPG.Render;

/// <summary>
/// Isometric projection + camera. Simulation positions are world/tile coordinates;
/// this is the ONLY place that converts between world space and isometric screen space.
/// </summary>
public class IsoCamera
{
    public NumVec2 Center;         // world position the camera looks at
    public int ScreenWidth = 1280;
    public int ScreenHeight = 720;

    public const float HalfTileW = TextureGen.TileWidth / 2f;   // 32
    public const float HalfTileH = TextureGen.TileHeight / 2f;  // 16

    /// <summary>World (tile) coordinates to isometric pixel coordinates (no camera).</summary>
    public static Vector2 WorldToIso(NumVec2 world) =>
        new((world.X - world.Y) * HalfTileW, (world.X + world.Y) * HalfTileH);

    public Vector2 WorldToScreen(NumVec2 world)
    {
        var iso = WorldToIso(world);
        var camIso = WorldToIso(Center);
        return new Vector2(iso.X - camIso.X + ScreenWidth / 2f, iso.Y - camIso.Y + ScreenHeight / 2f);
    }

    public NumVec2 ScreenToWorld(Point screen)
    {
        var camIso = WorldToIso(Center);
        float isoX = screen.X - ScreenWidth / 2f + camIso.X;
        float isoY = screen.Y - ScreenHeight / 2f + camIso.Y;
        // Invert: isoX = (x - y) * 32, isoY = (x + y) * 16
        float x = isoX / (2f * HalfTileW) + isoY / (2f * HalfTileH);
        float y = -isoX / (2f * HalfTileW) + isoY / (2f * HalfTileH);
        return new NumVec2(x, y);
    }

    /// <summary>Convert a screen-space direction (e.g. from WASD) into a normalized world direction,
    /// so movement keys feel correct relative to the isometric view.</summary>
    public static NumVec2 ScreenDirToWorldDir(NumVec2 screenDir)
    {
        if (screenDir == NumVec2.Zero) return NumVec2.Zero;
        float x = screenDir.X / (2f * HalfTileW) + screenDir.Y / (2f * HalfTileH);
        float y = -screenDir.X / (2f * HalfTileW) + screenDir.Y / (2f * HalfTileH);
        var world = new NumVec2(x, y);
        float len = world.Length();
        return len > 0.0001f ? world / len : NumVec2.Zero;
    }
}
