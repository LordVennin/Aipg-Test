using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace ARPG.Core;

/// <summary>All rebindable game actions. Gameplay code refers to these, never to raw keys.</summary>
public enum InputAction
{
    MoveUp,
    MoveDown,
    MoveLeft,
    MoveRight,
    PrimaryAttack,
    Skill1,
    Skill2,
    Skill3,
    Skill4,
    Dodge,
    Inventory,
    SkillMenu,
    CharacterSheet,
    SkillTree,
    CommandSummons,
    CycleSummonFocus,
    Interact,
    Pause,
    DebugMenu,
}

/// <summary>A binding is either a keyboard key or a mouse button. Serialized as e.g. "Key:W" or "Mouse:Left".</summary>
public readonly struct InputBinding : IEquatable<InputBinding>
{
    public readonly bool IsMouse;
    public readonly Keys Key;
    public readonly int MouseButton; // 0 = left, 1 = right, 2 = middle

    public InputBinding(Keys key) { IsMouse = false; Key = key; MouseButton = 0; }
    public InputBinding(int mouseButton) { IsMouse = true; Key = Keys.None; MouseButton = mouseButton; }

    public override string ToString() =>
        IsMouse ? MouseButton switch { 0 => "Mouse:Left", 1 => "Mouse:Right", _ => "Mouse:Middle" } : $"Key:{Key}";

    public string Display()
    {
        if (IsMouse) return MouseButton switch { 0 => "LMB", 1 => "RMB", _ => "MMB" };
        if (Key >= Keys.D0 && Key <= Keys.D9) return ((int)(Key - Keys.D0)).ToString();
        return Key.ToString();
    }

    public static bool TryParse(string text, out InputBinding binding)
    {
        binding = default;
        if (string.IsNullOrEmpty(text)) return false;
        if (text.StartsWith("Mouse:"))
        {
            binding = text[6..] switch
            {
                "Left" => new InputBinding(0),
                "Right" => new InputBinding(1),
                "Middle" => new InputBinding(2),
                _ => new InputBinding(0),
            };
            return true;
        }
        if (text.StartsWith("Key:") && Enum.TryParse(text[4..], out Keys key))
        {
            binding = new InputBinding(key);
            return true;
        }
        return false;
    }

    public bool Equals(InputBinding other) => IsMouse == other.IsMouse && Key == other.Key && MouseButton == other.MouseButton;
    public override bool Equals(object obj) => obj is InputBinding b && Equals(b);
    public override int GetHashCode() => HashCode.Combine(IsMouse, Key, MouseButton);
}

/// <summary>
/// Central input system. Polls keyboard/mouse once per frame and exposes action queries
/// (IsActionDown / WasActionPressed) driven by a rebindable action map.
/// </summary>
public class InputManager
{
    private KeyboardState _prevKeys, _keys;
    private MouseState _prevMouse, _mouse;
    private readonly Queue<char> _typedChars = new();

    public Dictionary<InputAction, InputBinding> Bindings { get; private set; } = DefaultBindings();

    /// <summary>Set when UI consumed the mouse this frame; world input should ignore clicks.</summary>
    public bool MouseCapturedByUI;
    /// <summary>Set when a text box has keyboard focus; action presses should be suppressed.</summary>
    public bool KeyboardCapturedByUI;

    public static Dictionary<InputAction, InputBinding> DefaultBindings() => new()
    {
        [InputAction.MoveUp] = new InputBinding(Keys.W),
        [InputAction.MoveDown] = new InputBinding(Keys.S),
        [InputAction.MoveLeft] = new InputBinding(Keys.A),
        [InputAction.MoveRight] = new InputBinding(Keys.D),
        [InputAction.PrimaryAttack] = new InputBinding(0),
        [InputAction.Skill1] = new InputBinding(Keys.D1),
        [InputAction.Skill2] = new InputBinding(Keys.D2),
        [InputAction.Skill3] = new InputBinding(Keys.D3),
        [InputAction.Skill4] = new InputBinding(Keys.D4),
        [InputAction.Dodge] = new InputBinding(Keys.Space),
        [InputAction.Inventory] = new InputBinding(Keys.I),
        [InputAction.SkillMenu] = new InputBinding(Keys.K),
        [InputAction.CharacterSheet] = new InputBinding(Keys.C),
        [InputAction.SkillTree] = new InputBinding(Keys.P),
        [InputAction.CommandSummons] = new InputBinding(Keys.OemTilde),
        [InputAction.CycleSummonFocus] = new InputBinding(Keys.Tab),
        [InputAction.Interact] = new InputBinding(Keys.F),
        [InputAction.Pause] = new InputBinding(Keys.Escape),
        [InputAction.DebugMenu] = new InputBinding(Keys.F1),
    };

    public void ApplyBindings(Dictionary<string, string> saved)
    {
        Bindings = DefaultBindings();
        if (saved == null) return;
        foreach (var (actionName, bindingText) in saved)
            if (Enum.TryParse(actionName, out InputAction action) && InputBinding.TryParse(bindingText, out var b))
                Bindings[action] = b;
    }

    public Dictionary<string, string> ExportBindings() =>
        Bindings.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value.ToString());

    public void BeginFrame()
    {
        _prevKeys = _keys;
        _prevMouse = _mouse;
        _keys = Keyboard.GetState();
        _mouse = Mouse.GetState();
        MouseCapturedByUI = false;
        KeyboardCapturedByUI = false;
    }

    public void PushTypedChar(char c) => _typedChars.Enqueue(c);
    public bool TryDequeueTypedChar(out char c)
    {
        if (_typedChars.Count > 0) { c = _typedChars.Dequeue(); return true; }
        c = '\0';
        return false;
    }
    public void ClearTypedChars() => _typedChars.Clear();

    private bool BindingDown(InputBinding b, in MouseState m, in KeyboardState k) =>
        b.IsMouse
            ? b.MouseButton switch
            {
                0 => m.LeftButton == ButtonState.Pressed,
                1 => m.RightButton == ButtonState.Pressed,
                _ => m.MiddleButton == ButtonState.Pressed,
            }
            : k.IsKeyDown(b.Key);

    public bool IsActionDown(InputAction action)
    {
        if (KeyboardCapturedByUI && !Bindings[action].IsMouse) return false;
        return Bindings.TryGetValue(action, out var b) && BindingDown(b, _mouse, _keys);
    }

    public bool WasActionPressed(InputAction action)
    {
        if (!Bindings.TryGetValue(action, out var b)) return false;
        if (KeyboardCapturedByUI && !b.IsMouse) return false;
        return BindingDown(b, _mouse, _keys) && !BindingDown(b, _prevMouse, _prevKeys);
    }

    // Raw helpers (used by UI internals and the rebinding screen, not by gameplay code).
    /// <summary>Mouse position in UI (virtual) space: raw pixels divided by the UI scale.
    /// All menus/panels lay out and hit-test in this space.</summary>
    public Point MousePosition => new((int)(_mouse.Position.X / UIScale.Value), (int)(_mouse.Position.Y / UIScale.Value));
    /// <summary>Mouse position in raw screen pixels — for WORLD interactions (aiming,
    /// camera picking), which render unscaled.</summary>
    public Point RawMousePosition => _mouse.Position;
    public bool MouseLeftDown => _mouse.LeftButton == ButtonState.Pressed;
    public bool MouseLeftPressed => _mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released;
    public bool MouseLeftReleased => _mouse.LeftButton == ButtonState.Released && _prevMouse.LeftButton == ButtonState.Pressed;
    public bool MouseRightPressed => _mouse.RightButton == ButtonState.Pressed && _prevMouse.RightButton == ButtonState.Released;
    public int ScrollDelta => _mouse.ScrollWheelValue - _prevMouse.ScrollWheelValue;
    public bool WasKeyPressed(Keys key) => _keys.IsKeyDown(key) && !_prevKeys.IsKeyDown(key);

    /// <summary>Used by the options screen to capture the next pressed key/mouse button for rebinding.</summary>
    public bool TryCaptureBinding(out InputBinding binding)
    {
        foreach (Keys key in _keys.GetPressedKeys())
        {
            if (!_prevKeys.IsKeyDown(key))
            {
                binding = new InputBinding(key);
                return true;
            }
        }
        if (MouseLeftPressed) { binding = new InputBinding(0); return true; }
        if (MouseRightPressed) { binding = new InputBinding(1); return true; }
        if (_mouse.MiddleButton == ButtonState.Pressed && _prevMouse.MiddleButton == ButtonState.Released)
        { binding = new InputBinding(2); return true; }
        binding = default;
        return false;
    }
}
