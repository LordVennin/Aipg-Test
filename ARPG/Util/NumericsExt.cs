using XnaVec2 = Microsoft.Xna.Framework.Vector2;
using NumVec2 = System.Numerics.Vector2;

namespace ARPG.Util;

/// <summary>
/// The simulation and networking layers use System.Numerics.Vector2 so they have no
/// MonoGame dependency (this also lets the headless net test run without graphics).
/// Rendering converts at the boundary with these helpers.
/// </summary>
public static class NumericsExt
{
    public static XnaVec2 ToXna(this NumVec2 v) => new(v.X, v.Y);
    public static NumVec2 ToNum(this XnaVec2 v) => new(v.X, v.Y);

    public static NumVec2 NormalizedOrZero(this NumVec2 v)
    {
        float len = v.Length();
        return len > 0.0001f ? v / len : NumVec2.Zero;
    }
}
