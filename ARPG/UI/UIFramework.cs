using ARPG.Core;
using ARPG.Render;
using FontStashSharp;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace ARPG.UI;

/// <summary>
/// Minimal retained-mode UI toolkit for MonoGame: panels, labels, buttons, text inputs.
/// Only what the prototype needs, but reusable across all screens.
/// </summary>
public abstract class UIElement
{
    public Rectangle Bounds;
    public bool Visible = true;
    public virtual void Update(InputManager input) { }
    public abstract void Draw(SpriteBatch sb);

    public bool Hovered(InputManager input) => Visible && Bounds.Contains(input.MousePosition);

    protected static void FillRect(SpriteBatch sb, Rectangle r, Color c) =>
        sb.Draw(TextureGen.Pixel, r, c);

    protected static void OutlineRect(SpriteBatch sb, Rectangle r, Color c, int thickness = 1)
    {
        FillRect(sb, new Rectangle(r.X, r.Y, r.Width, thickness), c);
        FillRect(sb, new Rectangle(r.X, r.Bottom - thickness, r.Width, thickness), c);
        FillRect(sb, new Rectangle(r.X, r.Y, thickness, r.Height), c);
        FillRect(sb, new Rectangle(r.Right - thickness, r.Y, thickness, r.Height), c);
    }
}

public class Panel : UIElement
{
    public Color Background = new(24, 24, 32, 235);
    public Color Border = new(90, 84, 60);
    public readonly List<UIElement> Children = new();

    public override void Update(InputManager input)
    {
        if (!Visible) return;
        foreach (var child in Children) child.Update(input);
        if (Bounds.Contains(input.MousePosition))
            input.MouseCapturedByUI = true;
    }

    public override void Draw(SpriteBatch sb)
    {
        if (!Visible) return;
        FillRect(sb, Bounds, Background);
        OutlineRect(sb, Bounds, Border, 2);
        foreach (var child in Children) child.Draw(sb);
    }
}

public class Label : UIElement
{
    public string Text = "";
    public float FontSize = 16;
    public Color Color = Color.White;
    public bool Bold;
    public bool Centered;

    public Label() { }
    public Label(string text, int x, int y, float size = 16, bool bold = false)
    {
        Text = text;
        Bounds = new Rectangle(x, y, 0, 0);
        FontSize = size;
        Bold = bold;
    }

    public override void Draw(SpriteBatch sb)
    {
        if (!Visible || string.IsNullOrEmpty(Text)) return;
        var font = Bold ? FontManager.GetBold(FontSize) : FontManager.Get(FontSize);
        var pos = new Vector2(Bounds.X, Bounds.Y);
        if (Centered)
        {
            var size = font.MeasureString(Text);
            pos = new Vector2(Bounds.Center.X - size.X / 2f, Bounds.Center.Y - size.Y / 2f);
        }
        sb.DrawString(font, Text, pos, Color);
    }
}

public class Button : UIElement
{
    public string Text;
    public Action OnClick;
    public bool Enabled = true;
    public float FontSize = 17;
    public Color Background = new(52, 48, 40);
    public Color HoverBackground = new(84, 76, 56);
    public Color TextColor = new(235, 226, 200);
    private bool _hover;

    public Button(string text, Rectangle bounds, Action onClick)
    {
        Text = text;
        Bounds = bounds;
        OnClick = onClick;
    }

    public override void Update(InputManager input)
    {
        if (!Visible) return;
        _hover = Bounds.Contains(input.MousePosition);
        if (_hover && Enabled && input.MouseLeftPressed)
        {
            Audio.AudioManager.PlayUi("ui_click");
            OnClick?.Invoke();
        }
    }

    public override void Draw(SpriteBatch sb)
    {
        if (!Visible) return;
        var bg = !Enabled ? new Color(35, 33, 30) : _hover ? HoverBackground : Background;
        FillRect(sb, Bounds, bg);
        OutlineRect(sb, Bounds, _hover && Enabled ? new Color(180, 160, 90) : new Color(100, 92, 70));
        var font = FontManager.Get(FontSize);
        var size = font.MeasureString(Text);
        var color = Enabled ? TextColor : new Color(120, 115, 100);
        sb.DrawString(font, Text, new Vector2(Bounds.Center.X - size.X / 2f, Bounds.Center.Y - size.Y / 2f), color);
    }
}

public class TextInput : UIElement
{
    public string Text = "";
    public string Placeholder = "";
    public int MaxLength = 24;
    public bool Focused;
    public bool NumericOnly;
    private float _blink;

    public TextInput(Rectangle bounds, string initial = "")
    {
        Bounds = bounds;
        Text = initial;
    }

    public override void Update(InputManager input)
    {
        if (!Visible) return;
        if (input.MouseLeftPressed)
            Focused = Bounds.Contains(input.MousePosition);

        if (Focused)
        {
            input.KeyboardCapturedByUI = true;
            while (input.TryDequeueTypedChar(out char c))
            {
                if (c == '\b')
                {
                    if (Text.Length > 0) Text = Text[..^1];
                }
                else if (c == '\r' || c == '\n' || c == '\t' || c == 27)
                {
                    Focused = false;
                }
                else if (!char.IsControl(c) && Text.Length < MaxLength)
                {
                    if (NumericOnly && !char.IsDigit(c)) continue;
                    // Allow only characters our font can sensibly show.
                    if (c >= ' ' && c <= '~') Text += c;
                }
            }
            if (input.WasKeyPressed(Keys.Escape)) Focused = false;
        }
        _blink += 1f / 60f;
    }

    public override void Draw(SpriteBatch sb)
    {
        if (!Visible) return;
        FillRect(sb, Bounds, new Color(15, 15, 20));
        OutlineRect(sb, Bounds, Focused ? new Color(200, 180, 100) : new Color(90, 85, 70));
        var font = FontManager.Get(17);
        bool empty = string.IsNullOrEmpty(Text);
        string shown = empty ? Placeholder : Text;
        var color = empty ? new Color(110, 105, 95) : Color.White;
        var size = font.MeasureString(shown.Length > 0 ? shown : " ");
        var pos = new Vector2(Bounds.X + 8, Bounds.Center.Y - size.Y / 2f);
        sb.DrawString(font, shown, pos, color);
        if (Focused && (int)(_blink * 2) % 2 == 0)
        {
            float caretX = pos.X + (empty ? 0 : font.MeasureString(Text).X) + 2;
            FillRect(sb, new Rectangle((int)caretX, Bounds.Y + 6, 2, Bounds.Height - 12), Color.White);
        }
    }
}

/// <summary>
/// Shared tiny "x" close button for in-game panels (inventory, skills, character sheet),
/// drawn in the panel's top-right corner so menus can be closed with the mouse.
/// </summary>
/// <summary>
/// Title-bar dragging for the gameplay panels (inventory, skills, sheet, tree, shop).
/// Each panel owns one of these: Layout() runs the default rect through Place() to get
/// the dragged position (clamped so the bar always stays reachable), and Update() calls
/// HandleBar() with the title-bar strip BEFORE its own click handling — while a drag is
/// live the panel skips everything else so list rows under the bar never react.
/// </summary>
public class WindowDrag
{
    private Point _offset;          // persistent displacement from the default layout
    private bool _dragging;
    private Point _grabMouse;
    private Point _grabOffset;

    public bool Dragging => _dragging;

    /// <summary>The draggable strip: the panel's top edge minus the close button.</summary>
    public static Rectangle BarFor(in Rectangle panel) =>
        new(panel.X, panel.Y, panel.Width - 34, 28);

    /// <summary>Apply the stored drag offset to the panel's default rect, clamped so a
    /// chunk of the title bar always stays on screen (a lost window can't happen).</summary>
    public Rectangle Place(Rectangle def, Point screen)
    {
        int x = Math.Clamp(def.X + _offset.X, 90 - def.Width, Math.Max(90 - def.Width, screen.X - 90));
        int y = Math.Clamp(def.Y + _offset.Y, 0, Math.Max(0, screen.Y - 48));
        _offset = new Point(x - def.X, y - def.Y);
        return new Rectangle(x, y, def.Width, def.Height);
    }

    /// <summary>Start/continue a title-bar drag. True while a drag is live (the panel
    /// should skip its other mouse handling for the frame).</summary>
    public bool HandleBar(InputManager input, in Rectangle bar)
    {
        if (!_dragging && input.MouseLeftPressed && bar.Contains(input.MousePosition))
        {
            _dragging = true;
            _grabMouse = input.MousePosition;
            _grabOffset = _offset;
        }
        if (!_dragging) return false;
        if (!input.MouseLeftDown)
        {
            _dragging = false;
            return false;
        }
        _offset = new Point(_grabOffset.X + input.MousePosition.X - _grabMouse.X,
                            _grabOffset.Y + input.MousePosition.Y - _grabMouse.Y);
        input.MouseCapturedByUI = true;
        return true;
    }

    /// <summary>The grip visual: a subtle darker strip with drag dots, drawn right after
    /// the panel background so the panel's own title text sits on top of it.</summary>
    public static void DrawBar(SpriteBatch sb, in Rectangle panel, Point mouse)
    {
        var bar = BarFor(panel);
        bool hover = bar.Contains(mouse);
        sb.Draw(TextureGen.Pixel, bar, new Color((byte)255, (byte)255, (byte)255, hover ? (byte)14 : (byte)7));
        sb.Draw(TextureGen.Pixel, new Rectangle(bar.X, bar.Bottom - 1, panel.Width, 1),
            new Color((byte)255, (byte)255, (byte)255, (byte)10));
        var dotColor = new Color((byte)255, (byte)255, (byte)255, hover ? (byte)90 : (byte)45);
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 2; j++)
                sb.Draw(TextureGen.Pixel,
                    new Rectangle(bar.Right - 22 + i * 6, bar.Y + 11 + j * 5, 2, 2), dotColor);
    }
}

public static class CloseButton
{
    public static Rectangle RectFor(in Rectangle panel) => new(panel.Right - 26, panel.Y + 6, 20, 20);

    /// <summary>True when the close button was clicked this frame (also claims the mouse).</summary>
    public static bool Handle(InputManager input, in Rectangle panel)
    {
        if (input.MouseLeftPressed && RectFor(panel).Contains(input.MousePosition))
        {
            input.MouseCapturedByUI = true;
            return true;
        }
        return false;
    }

    public static void Draw(SpriteBatch sb, in Rectangle panel, Point mouse)
    {
        var r = RectFor(panel);
        bool hover = r.Contains(mouse);
        sb.Draw(TextureGen.Pixel, r, hover ? new Color(150, 60, 55) : new Color(52, 50, 46));
        var border = new Color(95, 88, 62);
        sb.Draw(TextureGen.Pixel, new Rectangle(r.X, r.Y, r.Width, 1), border);
        sb.Draw(TextureGen.Pixel, new Rectangle(r.X, r.Bottom - 1, r.Width, 1), border);
        sb.Draw(TextureGen.Pixel, new Rectangle(r.X, r.Y, 1, r.Height), border);
        sb.Draw(TextureGen.Pixel, new Rectangle(r.Right - 1, r.Y, 1, r.Height), border);
        var font = FontManager.GetBold(13);
        var size = font.MeasureString("X");
        sb.DrawString(font, "X", new Vector2(r.Center.X - size.X / 2, r.Center.Y - size.Y / 2), new Color(225, 215, 200));
    }
}

/// <summary>Text helpers shared by UI panels.</summary>
public static class TextUtil
{
    /// <summary>Word-wrap text so each line fits within maxPixels for the given font.</summary>
    public static IEnumerable<string> WrapToWidth(string text, FontStashSharp.SpriteFontBase font, float maxPixels)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        string line = "";
        foreach (var word in text.Split(' '))
        {
            string candidate = line.Length == 0 ? word : line + " " + word;
            if (line.Length > 0 && font.MeasureString(candidate).X > maxPixels)
            {
                yield return line;
                line = word;
            }
            else
            {
                line = candidate;
            }
        }
        if (line.Length > 0) yield return line;
    }
}
