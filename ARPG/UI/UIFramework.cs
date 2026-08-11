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
            OnClick?.Invoke();
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
