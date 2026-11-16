using PocketPad.Shared.Protocol;

namespace PocketPad.Desktop.Input;

/// <summary>
/// Translates validated input messages of the active control session into <see cref="IInputController"/> calls.
/// Fractional pointer deltas are carried over so slow finger movement is not lost to rounding.
/// Keys and mouse buttons held via Down actions are tracked and released by <see cref="Reset"/>, so a dropped
/// connection never leaves e.g. Ctrl or the left button stuck.
/// </summary>
public sealed class InputDispatcher(IInputController controller)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<KeyCode, KeyModifiers> _heldKeys = [];
    private readonly HashSet<MouseButton> _heldButtons = [];
    private double _remainderX, _remainderY;
    private double _wheelRemainderX, _wheelRemainderY;

    /// <summary>Returns true if the message was executed.</summary>
    public bool Dispatch(ProtocolMessage message)
    {
        lock (_gate)
        {
            switch (message)
            {
                case MouseMoveMessage move:
                    var x = move.Dx + _remainderX;
                    var y = move.Dy + _remainderY;
                    var dx = (int)Math.Truncate(x);
                    var dy = (int)Math.Truncate(y);
                    _remainderX = x - dx;
                    _remainderY = y - dy;
                    return controller.MoveRelative(dx, dy);

                case MouseScrollMessage scroll:
                    // Fractional wheel units are carried over; Windows accepts deltas below 120 (high-resolution scrolling).
                    var wy = scroll.Dy + _wheelRemainderY;
                    var wx = scroll.Dx + _wheelRemainderX;
                    var wheelY = (int)Math.Truncate(wy);
                    var wheelX = (int)Math.Truncate(wx);
                    _wheelRemainderY = wy - wheelY;
                    _wheelRemainderX = wx - wheelX;
                    var ok = true;
                    if (wheelY != 0) ok &= controller.Wheel(wheelY, horizontal: false);
                    if (wheelX != 0) ok &= controller.Wheel(wheelX, horizontal: true);
                    return ok;

                case MouseClickMessage click:
                    if (click.Action == MouseClickAction.Down) _heldButtons.Add(click.Button);
                    if (click.Action == MouseClickAction.Up) _heldButtons.Remove(click.Button);
                    return controller.Click(click.Button, click.Action);

                case KeyboardTextMessage text:
                    return controller.TypeText(text.Text);

                case KeyboardKeyMessage key:
                    if (key.Action == KeyAction.Down) _heldKeys[key.Key] = key.Modifiers;
                    if (key.Action == KeyAction.Up) _heldKeys.Remove(key.Key);
                    return controller.Key(key.Key, key.Modifiers, key.Action);

                case MediaCommandMessage media:
                    return controller.Media(media.Command);

                default:
                    return false;
            }
        }
    }

    /// <summary>Called when the control session changes or ends: releases held input and drops pointer leftovers.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            foreach (var (key, modifiers) in _heldKeys) controller.Key(key, modifiers, KeyAction.Up);
            foreach (var button in _heldButtons) controller.Click(button, MouseClickAction.Up);
            _heldKeys.Clear();
            _heldButtons.Clear();
            _remainderX = _remainderY = 0;
            _wheelRemainderX = _wheelRemainderY = 0;
        }
    }
}
