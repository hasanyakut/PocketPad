using PocketPad.Shared.Input;

namespace PocketPad.Mobile.Controls;

/// <summary>
/// Touch area that turns raw multi-touch pointer events (delivered by the platform handler, in dp)
/// into touchpad gestures. All calls happen on the UI thread; gesture consumers must not block.
/// </summary>
public sealed class TouchpadSurface : View
{
    private readonly TouchpadGestureRecognizer _recognizer = new();
    private IDispatcherTimer? _tapTimer;

    public TouchpadSurface()
    {
        _recognizer.GestureDetected += g => GestureDetected?.Invoke(this, g);
        _recognizer.GestureCancelled += () => GestureCancelled?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler<TouchpadGesture>? GestureDetected;

    /// <summary>The touch was cancelled (system took it over, or the surface got disabled): drop unsent motion.</summary>
    public event EventHandler? GestureCancelled;

    private static long Now => Environment.TickCount64;

    internal void OnPointerDown(int id, double x, double y) => Run(() => _recognizer.PointerDown(id, x, y, Now));

    internal void OnPointersMoved(ReadOnlySpan<TouchPoint> points)
    {
        if (!CanHandle()) return;
        _recognizer.PointersMoved(points, Now);
        ScheduleTapFlush();
    }

    internal void OnPointerUp(int id, double x, double y) => Run(() => _recognizer.PointerUp(id, x, y, Now));

    internal void OnCancel() => Run(_recognizer.Cancel);

    private void Run(Action action)
    {
        if (!CanHandle()) return;
        action();
        ScheduleTapFlush();
    }

    private bool CanHandle()
    {
        if (IsEnabled) return true;
        if (_recognizer.IsTracking) _recognizer.Cancel();
        return false;
    }

    /// <summary>A completed single tap is emitted once the double-tap window has passed.</summary>
    private void ScheduleTapFlush()
    {
        _tapTimer?.Stop();
        if (_recognizer.NextDeadlineMs is not { } deadline) return;

        _tapTimer ??= CreateTimer();
        _tapTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, deadline - Now));
        _tapTimer.Start();
    }

    private IDispatcherTimer CreateTimer()
    {
        var timer = Dispatcher.CreateTimer();
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            _recognizer.Tick(Now);
            ScheduleTapFlush();
        };
        return timer;
    }

    protected override void OnPropertyChanged(string? propertyName = null)
    {
        base.OnPropertyChanged(propertyName);
        if (propertyName == nameof(IsEnabled) && !IsEnabled)
        {
            _tapTimer?.Stop();
            _recognizer.Cancel(); // e.g. connection lost mid-gesture
        }
    }
}
