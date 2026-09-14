using ARPG.Core;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ARPG.UI;

/// <summary>
/// Scissor-clipped drawing for a scrollable panel body: everything drawn between
/// Begin and End is cut at the rectangle's edge, so content scrolled past the frame
/// disappears under it instead of spilling over the rest of the screen. Restarts the
/// UI sprite batch with the same sampler and UI-scale transform GameMain uses.
/// </summary>
public static class UiClip
{
    private static readonly RasterizerState Scissor = new() { ScissorTestEnable = true };

    public static void Begin(SpriteBatch sb, Rectangle uiRect)
    {
        sb.End();
        float k = UIScale.Value;
        var gd = sb.GraphicsDevice;
        var r = new Rectangle((int)(uiRect.X * k), (int)(uiRect.Y * k),
            (int)MathF.Ceiling(uiRect.Width * k), (int)MathF.Ceiling(uiRect.Height * k));
        r = Rectangle.Intersect(r, gd.Viewport.Bounds);
        gd.ScissorRectangle = r;
        sb.Begin(samplerState: SamplerState.PointClamp, rasterizerState: Scissor,
            transformMatrix: Matrix.CreateScale(k));
    }

    public static void End(SpriteBatch sb)
    {
        sb.End();
        sb.Begin(samplerState: SamplerState.PointClamp, transformMatrix: Matrix.CreateScale(UIScale.Value));
    }

    /// <summary>Wheel scrolling for a body whose content stands `contentHeight` tall
    /// inside `viewHeight`: returns the new offset, clamped so the bottom of the
    /// content never rises above the bottom of the view.</summary>
    public static int Scroll(int current, int wheelDelta, int contentHeight, int viewHeight, int step = 42)
    {
        int max = Math.Max(0, contentHeight - viewHeight);
        if (wheelDelta != 0) current -= Math.Sign(wheelDelta) * step;
        return Math.Clamp(current, 0, max);
    }

    /// <summary>A slim track + thumb at the body's right edge when there is more
    /// content than fits — the cue that the wheel does something here.</summary>
    public static void DrawScrollbar(SpriteBatch sb, Rectangle body, int scroll, int contentHeight)
    {
        if (contentHeight <= body.Height) return;
        var track = new Rectangle(body.Right - 5, body.Y + 2, 3, body.Height - 4);
        sb.Draw(Render.TextureGen.Pixel, track, new Color(50, 48, 58));
        int thumbH = Math.Max(14, track.Height * body.Height / contentHeight);
        int thumbY = track.Y + (track.Height - thumbH) * scroll / Math.Max(1, contentHeight - body.Height);
        sb.Draw(Render.TextureGen.Pixel, new Rectangle(track.X, thumbY, 3, thumbH), new Color(150, 140, 120));
    }
}
