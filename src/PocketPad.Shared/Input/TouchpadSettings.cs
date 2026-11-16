namespace PocketPad.Shared.Input;

/// <summary>
/// User touchpad settings and the conversion from finger motion (dp) to protocol units.
/// Applied on the phone, so the PC receives final values and does not need its own copy of the settings.
/// </summary>
public sealed record TouchpadSettings
{
    public const double MinSensitivity = 0.5, MaxSensitivity = 4.0, DefaultSensitivity = 1.5;
    public const double MinScrollSpeed = 0.25, MaxScrollSpeed = 3.0, DefaultScrollSpeed = 1.0;

    /// <summary>Wheel units per dp of two-finger movement at speed 1 (≈ one notch per 40 dp).</summary>
    public const double WheelUnitsPerDp = 3.0;

    public double MouseSensitivity { get; init; } = DefaultSensitivity;
    public double ScrollSpeed { get; init; } = DefaultScrollSpeed;

    /// <summary>
    /// false (default) = natural, like a phone or a Windows precision touchpad: content follows the fingers
    /// (fingers down → page moves down → wheel up). true = classic mouse-wheel direction.
    /// </summary>
    public bool ReverseScroll { get; init; }

    public (double Dx, double Dy) PointerDelta(double dxDp, double dyDp) =>
        (dxDp * MouseSensitivity, dyDp * MouseSensitivity);

    /// <summary>Vertical wheel delta (120 = one notch, positive = scroll up) for a finger centroid movement.</summary>
    public double WheelDelta(double fingerDyDp)
    {
        var natural = fingerDyDp * WheelUnitsPerDp * ScrollSpeed; // fingers down (dy > 0) → wheel up (> 0)
        return ReverseScroll ? -natural : natural;
    }

    public TouchpadSettings Normalized() => this with
    {
        MouseSensitivity = Math.Clamp(MouseSensitivity, MinSensitivity, MaxSensitivity),
        ScrollSpeed = Math.Clamp(ScrollSpeed, MinScrollSpeed, MaxScrollSpeed),
    };
}
