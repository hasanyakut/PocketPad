namespace PocketPad.Shared.Protocol;

public enum MouseButton { Left, Right, Middle }

public enum MouseClickAction { Click, DoubleClick, Down, Up }

public enum KeyAction { Press, Down, Up }

[Flags]
public enum KeyModifiers { None = 0, Shift = 1, Control = 2, Alt = 4, Windows = 8 }

/// <summary>Platform neutral key identifiers. The Windows side maps them to virtual-key codes.</summary>
public enum KeyCode
{
    Enter, Backspace, Tab, Escape, Space, Delete, Insert,
    Left, Up, Right, Down, Home, End, PageUp, PageDown,
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
    PrintScreen, Windows, ContextMenu,

    // Letters and digits (for shortcuts such as Ctrl+C or Win+D; typed text goes through KeyboardTextMessage).
    A, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
    D0, D1, D2, D3, D4, D5, D6, D7, D8, D9,

    /// <summary>The "+ / =" key (Ctrl+Plus = zoom in).</summary>
    Plus,
    /// <summary>The "- / _" key (Ctrl+Minus = zoom out).</summary>
    Minus,
}

public enum MediaCommand { PlayPause, Stop, Next, Previous, VolumeUp, VolumeDown, Mute }

public sealed record MouseMoveMessage : ProtocolMessage, IInputMessage
{
    public required double Dx { get; init; }
    public required double Dy { get; init; }

    public override string? Validate() => FirstError(ValidateDelta(Dx, nameof(Dx)), ValidateDelta(Dy, nameof(Dy)));
}

public sealed record MouseClickMessage : ProtocolMessage, IInputMessage
{
    public required MouseButton Button { get; init; }
    public MouseClickAction Action { get; init; } = MouseClickAction.Click;

    public override string? Validate() => FirstError(ValidateEnum(Button), ValidateEnum(Action));
}

/// <summary>
/// Wheel scroll in Windows wheel units (120 = one notch). <see cref="Dy"/> &gt; 0 scrolls up, <see cref="Dx"/> &gt; 0 scrolls right.
/// Values may be fractional; the receiver carries the remainder (high-resolution scrolling).
/// </summary>
public sealed record MouseScrollMessage : ProtocolMessage, IInputMessage
{
    public double Dx { get; init; }
    public required double Dy { get; init; }

    public override string? Validate() => FirstError(ValidateDelta(Dx, nameof(Dx)), ValidateDelta(Dy, nameof(Dy)));
}

public sealed record KeyboardTextMessage : ProtocolMessage, IInputMessage
{
    public required string Text { get; init; }

    public override string? Validate()
    {
        if (string.IsNullOrEmpty(Text)) return "Text is required.";
        if (Text.Length > ProtocolInfo.MaxTextLength) return "Text is too long.";
        return null;
    }

    // Typed text may be sensitive (passwords); never print it.
    public override string ToString() => $"{nameof(KeyboardTextMessage)} {{ Length = {Text?.Length ?? 0} }}";
}

public sealed record KeyboardKeyMessage : ProtocolMessage, IInputMessage
{
    public required KeyCode Key { get; init; }
    public KeyModifiers Modifiers { get; init; }
    public KeyAction Action { get; init; } = KeyAction.Press;

    public override string? Validate()
    {
        const KeyModifiers all = KeyModifiers.Shift | KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Windows;
        return FirstError(
            ValidateEnum(Key),
            ValidateEnum(Action),
            (Modifiers & ~all) == 0 ? null : "Unknown modifier flags.");
    }
}

public sealed record MediaCommandMessage : ProtocolMessage, IInputMessage
{
    public required MediaCommand Command { get; init; }

    public override string? Validate() => ValidateEnum(Command);
}
