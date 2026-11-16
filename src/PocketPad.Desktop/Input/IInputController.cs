using PocketPad.Shared.Protocol;

namespace PocketPad.Desktop.Input;

/// <summary>Injects mouse, keyboard and media input into the interactive Windows session.</summary>
public interface IInputController
{
    /// <summary>Relative cursor move in pixels. Returns false if Windows refused the input.</summary>
    bool MoveRelative(int dx, int dy);

    /// <summary>Returns false if Windows refused the input.</summary>
    bool Click(MouseButton button, MouseClickAction action);

    /// <summary>Mouse wheel in wheel units (120 = one notch; positive = up / right). Returns false if Windows refused the input.</summary>
    bool Wheel(int delta, bool horizontal);

    /// <summary>Types text independent of the active keyboard layout. Returns false if Windows refused the input.</summary>
    bool TypeText(string text);

    /// <summary>Presses/holds/releases a key with modifiers. Returns false if Windows refused the input.</summary>
    bool Key(KeyCode key, KeyModifiers modifiers, KeyAction action);

    /// <summary>Returns false if Windows refused the input.</summary>
    bool Media(MediaCommand command);
}
