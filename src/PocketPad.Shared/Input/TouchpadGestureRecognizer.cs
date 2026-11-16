namespace PocketPad.Shared.Input;

public enum TouchpadGestureKind { Move, LeftClick, RightClick, DoubleClick, Scroll }

/// <summary>
/// Output of <see cref="TouchpadGestureRecognizer"/>. <see cref="Dx"/>/<see cref="Dy"/> carry the pointer delta for
/// <see cref="TouchpadGestureKind.Move"/> and the finger (centroid) delta for <see cref="TouchpadGestureKind.Scroll"/>,
/// in the caller's units (dp). Scroll is vertical only: Dx is always 0.
/// </summary>
public readonly record struct TouchpadGesture(TouchpadGestureKind Kind, double Dx = 0, double Dy = 0);

/// <summary>One finger position, identified by its stable platform pointer id (not its index).</summary>
public readonly record struct TouchPoint(int Id, double X, double Y);

public sealed record TouchpadGestureOptions
{
    /// <summary>Movement (in the caller's units, e.g. dp) a finger may drift and still count as a tap.</summary>
    public double TapSlop { get; init; } = 8;
    /// <summary>Longest press that still counts as a tap.</summary>
    public long TapMaxDurationMs { get; init; } = 250;
    /// <summary>Time after a tap during which a second tap turns it into a double click.</summary>
    public long DoubleTapWindowMs { get; init; } = 300;
    /// <summary>Max distance between the two taps of a double tap.</summary>
    public double DoubleTapSlop { get; init; } = 40;
    /// <summary>Longest two-finger touch that still counts as a right click.</summary>
    public long TwoFingerTapMaxDurationMs { get; init; } = 300;
    /// <summary>Distance the two-finger centroid must travel before scrolling starts (keeps two-finger taps clean).</summary>
    public double ScrollSlop { get; init; } = 10;
}

/// <summary>
/// Platform independent touchpad gesture state machine. Feed it raw pointer events keyed by stable pointer id
/// (one thread only) and call <see cref="Tick"/> at <see cref="NextDeadlineMs"/> so a pending single tap can be emitted.
/// <list type="bullet">
/// <item>One finger moving beyond <see cref="TouchpadGestureOptions.TapSlop"/> → relative moves (no jump on re-touch).</item>
/// <item>Short, still tap → left click, delayed by the double-tap window so single and double clicks never both fire.</item>
/// <item>Two quick taps → double click.</item>
/// <item>Two fingers → no cursor movement; centroid moving beyond <see cref="TouchpadGestureOptions.ScrollSlop"/> → vertical scroll;
///       a short still two-finger touch → right click. Three or more fingers do nothing.</item>
/// <item>Two → one finger → cursor movement resumes from the remaining finger's position once it moves beyond the tap slop
///       (no jump, no click). One → two fingers → movement stops, scrolling starts from the current centroid.</item>
/// <item>Cancel → all state, including a pending tap, is discarded and <see cref="GestureCancelled"/> is raised.</item>
/// </list>
/// </summary>
public sealed class TouchpadGestureRecognizer(TouchpadGestureOptions? options = null)
{
    private enum State { Idle, Pressed, Moving, TwoFinger, MultiTouch }

    private sealed class Pointer(double x, double y)
    {
        public double StartX { get; set; } = x;
        public double StartY { get; set; } = y;
        public double X { get; set; } = x;
        public double Y { get; set; } = y;
    }

    private readonly TouchpadGestureOptions _options = options ?? new TouchpadGestureOptions();
    private readonly Dictionary<int, Pointer> _pointers = [];

    private State _state = State.Idle;

    // Single finger
    private int _primaryId;
    private long _downTime;
    private double _downX, _downY, _lastX, _lastY;
    private bool _tapEligible;          // false when this single-finger phase follows a multi-finger phase

    // Two / multi finger
    private long _multiStartTime;
    private bool _rightClickEligible;
    private bool _scrolling;
    private double _centroidStartX, _centroidStartY, _lastCentroidY;

    // A completed single tap waiting to see whether a second tap follows.
    private (long Deadline, double X, double Y)? _pendingTap;
    private bool _secondTapCandidate;

    public event Action<TouchpadGesture>? GestureDetected;

    /// <summary>Raised by <see cref="Cancel"/> so consumers can drop queued, not yet sent motion.</summary>
    public event Action? GestureCancelled;

    /// <summary>When <see cref="Tick"/> must be called to flush a pending single tap, or null.</summary>
    public long? NextDeadlineMs => _pendingTap is { } p && _pointers.Count == 0 ? p.Deadline : null;

    public bool IsTracking => _pointers.Count > 0;
    public bool IsScrolling => _scrolling;

    public void PointerDown(int id, double x, double y, long timeMs)
    {
        if (_pointers.ContainsKey(id)) return;

        if (_pointers.Count == 0)
        {
            if (_pendingTap is { } pending)
            {
                _secondTapCandidate = timeMs <= pending.Deadline
                                      && Distance(x, y, pending.X, pending.Y) <= _options.DoubleTapSlop;
                if (!_secondTapCandidate) FlushPendingTap();
            }

            _pointers[id] = new Pointer(x, y);
            StartSingle(id, x, y, timeMs, tapEligible: true);
            _rightClickEligible = false;
            return;
        }

        // Additional finger: the cursor stops; with two fingers we may scroll or right click.
        if (_secondTapCandidate) FlushPendingTap();
        if (_state is State.Pressed or State.Moving)
        {
            _multiStartTime = _downTime;
            // A two-finger tap requires the first finger not to have moved and to be a fresh touch.
            _rightClickEligible = _state == State.Pressed && _tapEligible;
        }

        _pointers[id] = new Pointer(x, y);
        if (_pointers.Count == 2)
        {
            _state = State.TwoFinger;
            ResetScrollBaseline();
        }
        else
        {
            _state = State.MultiTouch;
            _rightClickEligible = false;
            _scrolling = false;
        }
    }

    public void PointerMove(int id, double x, double y, long timeMs) =>
        PointersMoved([new TouchPoint(id, x, y)], timeMs);

    /// <summary>All pointers of one platform move event; positions are applied before the centroid is evaluated.</summary>
    public void PointersMoved(ReadOnlySpan<TouchPoint> points, long timeMs)
    {
        var any = false;
        foreach (var p in points)
        {
            if (!_pointers.TryGetValue(p.Id, out var pointer)) continue;
            pointer.X = p.X;
            pointer.Y = p.Y;
            any = true;
            if (Distance(p.X, p.Y, pointer.StartX, pointer.StartY) > _options.TapSlop) _rightClickEligible = false;
        }
        if (!any) return;

        switch (_state)
        {
            case State.Pressed when _pointers.TryGetValue(_primaryId, out var primary):
                if (Distance(primary.X, primary.Y, _downX, _downY) > _options.TapSlop)
                {
                    _state = State.Moving;
                    if (_secondTapCandidate) FlushPendingTap();
                    _rightClickEligible = false;
                    // Start relative motion from here: the slop distance is not replayed as a jump.
                    _lastX = primary.X;
                    _lastY = primary.Y;
                }
                break;

            case State.Moving when _pointers.TryGetValue(_primaryId, out var primary):
                var dx = primary.X - _lastX;
                var dy = primary.Y - _lastY;
                _lastX = primary.X;
                _lastY = primary.Y;
                if (dx != 0 || dy != 0) Emit(new TouchpadGesture(TouchpadGestureKind.Move, dx, dy));
                break;

            case State.TwoFinger:
                var (cx, cy) = Centroid();
                if (!_scrolling)
                {
                    if (Distance(cx, cy, _centroidStartX, _centroidStartY) <= _options.ScrollSlop) break;
                    _scrolling = true;
                    _rightClickEligible = false;
                    _lastCentroidY = cy; // scroll starts here: the slop itself is not replayed
                    break;
                }
                var scroll = cy - _lastCentroidY;
                _lastCentroidY = cy;
                if (scroll != 0) Emit(new TouchpadGesture(TouchpadGestureKind.Scroll, 0, scroll));
                break;
        }
    }

    public void PointerUp(int id, double x, double y, long timeMs)
    {
        if (!_pointers.ContainsKey(id)) return;
        PointerMove(id, x, y, timeMs);
        _pointers.Remove(id);

        switch (_pointers.Count)
        {
            case 0:
                Finish(timeMs);
                return;

            case 1:
                if (_state is State.TwoFinger or State.MultiTouch)
                {
                    // Back to one finger: resume pointer movement from where that finger is now, without a jump.
                    // It must travel the tap slop first, so lifting fingers one after the other does not twitch the cursor.
                    var (remainingId, remaining) = _pointers.First();
                    remaining.StartX = remaining.X;
                    remaining.StartY = remaining.Y;
                    StartSingle(remainingId, remaining.X, remaining.Y, _downTime, tapEligible: false);
                }
                return;

            case 2:
                // From three fingers back to two: scroll again, measured from the current centroid.
                _state = State.TwoFinger;
                ResetScrollBaseline();
                return;
        }
    }

    /// <summary>Touch cancelled by the system (e.g. app switch, notification shade): discard everything.</summary>
    public void Cancel()
    {
        _pointers.Clear();
        _state = State.Idle;
        _pendingTap = null;
        _secondTapCandidate = false;
        _rightClickEligible = false;
        _scrolling = false;
        _tapEligible = false;
        GestureCancelled?.Invoke();
    }

    public void Tick(long timeMs)
    {
        if (_pendingTap is { } pending && _pointers.Count == 0 && timeMs >= pending.Deadline)
            FlushPendingTap();
    }

    private void StartSingle(int id, double x, double y, long downTime, bool tapEligible)
    {
        _state = State.Pressed;
        _primaryId = id;
        _downTime = downTime;
        _downX = _lastX = x;
        _downY = _lastY = y;
        _tapEligible = tapEligible;
        _scrolling = false;
    }

    /// <summary>Last finger lifted: decide clicks.</summary>
    private void Finish(long timeMs)
    {
        if (_state == State.Pressed)
        {
            if (!_tapEligible)
            {
                // The touch started with two fingers and both were lifted without moving: two-finger tap.
                if (_rightClickEligible && timeMs - _multiStartTime <= _options.TwoFingerTapMaxDurationMs)
                    Emit(new TouchpadGesture(TouchpadGestureKind.RightClick));
            }
            else
            {
                var isTap = timeMs - _downTime <= _options.TapMaxDurationMs;
                if (isTap && _secondTapCandidate)
                {
                    _pendingTap = null;
                    Emit(new TouchpadGesture(TouchpadGestureKind.DoubleClick));
                }
                else if (isTap)
                {
                    _pendingTap = (timeMs + _options.DoubleTapWindowMs, _downX, _downY);
                }
                else if (_secondTapCandidate)
                {
                    FlushPendingTap(); // first tap stands; the long press itself does nothing
                }
            }
        }
        else if (_state == State.TwoFinger && _rightClickEligible
                 && timeMs - _multiStartTime <= _options.TwoFingerTapMaxDurationMs)
        {
            // Both fingers reported up in the same event.
            Emit(new TouchpadGesture(TouchpadGestureKind.RightClick));
        }

        _state = State.Idle;
        _secondTapCandidate = false;
        _rightClickEligible = false;
        _scrolling = false;
    }

    private void ResetScrollBaseline()
    {
        _scrolling = false;
        (_centroidStartX, _centroidStartY) = Centroid();
        _lastCentroidY = _centroidStartY;
    }

    private (double X, double Y) Centroid()
    {
        double sx = 0, sy = 0;
        foreach (var p in _pointers.Values)
        {
            sx += p.X;
            sy += p.Y;
        }
        return (sx / _pointers.Count, sy / _pointers.Count);
    }

    private void FlushPendingTap()
    {
        _secondTapCandidate = false;
        if (_pendingTap is null) return;
        _pendingTap = null;
        Emit(new TouchpadGesture(TouchpadGestureKind.LeftClick));
    }

    private void Emit(TouchpadGesture gesture) => GestureDetected?.Invoke(gesture);

    private static double Distance(double x1, double y1, double x2, double y2)
    {
        var dx = x1 - x2;
        var dy = y1 - y2;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
