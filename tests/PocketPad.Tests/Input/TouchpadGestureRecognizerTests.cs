using PocketPad.Shared.Input;
using K = PocketPad.Shared.Input.TouchpadGestureKind;

namespace PocketPad.Tests.Input;

public class TouchpadGestureRecognizerTests
{
    private static readonly TouchpadGestureOptions Options = new()
    {
        TapSlop = 8,
        TapMaxDurationMs = 250,
        DoubleTapWindowMs = 300,
        DoubleTapSlop = 40,
        TwoFingerTapMaxDurationMs = 300,
    };

    private readonly TouchpadGestureRecognizer _r = new(Options);
    private readonly List<TouchpadGesture> _out = [];

    public TouchpadGestureRecognizerTests() => _r.GestureDetected += _out.Add;

    private IEnumerable<K> Kinds => _out.Select(g => g.Kind);

    private void Tap(long t, double x = 100, double y = 100, int id = 0, long duration = 50)
    {
        _r.PointerDown(id, x, y, t);
        _r.PointerUp(id, x, y, t + duration);
    }

    [Fact]
    public void SingleTap_IsLeftClick_OnlyAfterDoubleTapWindow()
    {
        Tap(0);
        Assert.Empty(_out); // not yet: could still become a double tap
        Assert.Equal(350, _r.NextDeadlineMs);

        _r.Tick(349);
        Assert.Empty(_out);

        _r.Tick(350);
        Assert.Equal([K.LeftClick], Kinds);
        Assert.Null(_r.NextDeadlineMs);
    }

    [Fact]
    public void TapWithSmallJitter_StillCountsAsTap()
    {
        _r.PointerDown(0, 100, 100, 0);
        _r.PointerMove(0, 103, 104, 20);   // 5 dp: involuntary
        _r.PointerMove(0, 105, 98, 40);
        _r.PointerUp(0, 104, 101, 60);
        _r.Tick(1000);

        Assert.Equal([K.LeftClick], Kinds);
    }

    [Fact]
    public void DoubleTap_IsOneDoubleClick_AndNoSingleClick()
    {
        Tap(0);
        Tap(150, x: 110, y: 95);
        _r.Tick(2000);

        Assert.Equal([K.DoubleClick], Kinds);
    }

    [Fact]
    public void SecondTapAfterWindow_IsTwoSingleClicks()
    {
        Tap(0);
        Tap(500); // first tap's deadline (350) passed before second down: flushed on down
        _r.Tick(2000);

        Assert.Equal([K.LeftClick, K.LeftClick], Kinds);
    }

    [Fact]
    public void SecondTapFarAway_IsTwoSingleClicks()
    {
        Tap(0, x: 0, y: 0);
        Tap(100, x: 200, y: 200);
        _r.Tick(2000);

        Assert.Equal([K.LeftClick, K.LeftClick], Kinds);
    }

    [Fact]
    public void MovementBeyondSlop_IsMove_NotTap()
    {
        _r.PointerDown(0, 100, 100, 0);
        _r.PointerMove(0, 110, 100, 10); // crosses slop: no jump emitted
        _r.PointerMove(0, 115, 102, 20);
        _r.PointerMove(0, 118, 106, 30);
        _r.PointerUp(0, 118, 106, 40);
        _r.Tick(2000);

        Assert.Equal(
            [new TouchpadGesture(K.Move, 5, 2), new TouchpadGesture(K.Move, 3, 4)],
            _out);
    }

    [Fact]
    public void LiftingAndTouchingElsewhere_DoesNotJumpCursor()
    {
        _r.PointerDown(0, 100, 100, 0);
        _r.PointerMove(0, 120, 100, 10);
        _r.PointerMove(0, 130, 100, 20);
        _r.PointerUp(0, 130, 100, 30);
        _out.Clear();

        // New touch far from the previous one: only movement within this touch counts.
        _r.PointerDown(1, 10, 400, 1000);
        _r.PointerMove(1, 20, 400, 1010);
        _r.PointerMove(1, 25, 401, 1020);
        _r.PointerUp(1, 25, 401, 1030);

        Assert.All(_out, g => Assert.True(Math.Abs(g.Dx) <= 10 && Math.Abs(g.Dy) <= 10));
        Assert.Equal(5, _out.Sum(g => g.Dx));
    }

    [Fact]
    public void LongStillPress_IsNotAClick()
    {
        Tap(0, duration: 600);
        _r.Tick(5000);
        Assert.Empty(_out);
    }

    [Fact]
    public void TwoFingerShortTap_IsRightClick()
    {
        _r.PointerDown(0, 100, 100, 0);
        _r.PointerDown(1, 160, 100, 20);
        _r.PointerUp(0, 101, 100, 120);
        _r.PointerUp(1, 160, 101, 130);
        _r.Tick(5000);

        Assert.Equal([K.RightClick], Kinds);
    }

    [Fact]
    public void TwoFingerTap_TooLong_IsNothing()
    {
        _r.PointerDown(0, 100, 100, 0);
        _r.PointerDown(1, 160, 100, 20);
        _r.PointerUp(0, 100, 100, 500);
        _r.PointerUp(1, 160, 100, 510);
        _r.Tick(5000);

        Assert.Empty(_out);
    }

    [Fact]
    public void SecondFinger_StopsCursorMovement()
    {
        _r.PointerDown(0, 100, 100, 0);
        _r.PointerMove(0, 120, 100, 10);
        _r.PointerMove(0, 130, 100, 20);
        var movesBefore = _out.Count;

        _r.PointerDown(1, 200, 100, 30);
        _r.PointerMove(0, 180, 100, 40); // sideways two-finger motion: neither cursor move nor (vertical) scroll
        _r.PointerMove(1, 250, 100, 40);

        Assert.Equal(movesBefore, _out.Count);
    }

    [Fact]
    public void TwoFingerTapWithMovement_IsNotRightClick()
    {
        _r.PointerDown(0, 100, 100, 0);
        _r.PointerDown(1, 160, 100, 10);
        _r.PointerMove(1, 160, 160, 50);
        _r.PointerUp(0, 100, 100, 100);
        _r.PointerUp(1, 160, 160, 110);
        _r.Tick(5000);

        Assert.DoesNotContain(K.RightClick, Kinds);
        Assert.All(_out, g => Assert.Equal(K.Scroll, g.Kind));
    }

    [Fact]
    public void ThreeFingerTap_IsNothing()
    {
        _r.PointerDown(0, 100, 100, 0);
        _r.PointerDown(1, 150, 100, 10);
        _r.PointerDown(2, 200, 100, 20);
        _r.PointerUp(0, 100, 100, 80);
        _r.PointerUp(1, 150, 100, 80);
        _r.PointerUp(2, 200, 100, 80);
        _r.Tick(5000);

        Assert.Empty(_out);
    }

    [Fact]
    public void PendingTap_IsFlushedBeforeTwoFingerGesture()
    {
        Tap(0);
        _r.PointerDown(0, 100, 100, 100);
        _r.PointerDown(1, 150, 100, 110);
        _r.PointerUp(0, 100, 100, 150);
        _r.PointerUp(1, 150, 100, 160);

        Assert.Equal([K.LeftClick, K.RightClick], Kinds);
    }

    [Fact]
    public void TapThenDrag_IsClickThenMoves()
    {
        Tap(0);
        _r.PointerDown(0, 100, 100, 100);
        _r.PointerMove(0, 120, 100, 120);
        _r.PointerMove(0, 125, 100, 130);
        _r.PointerUp(0, 125, 100, 140);

        Assert.Equal([K.LeftClick, K.Move], Kinds);
    }

    [Fact]
    public void TapThenLongPress_KeepsFirstClickOnly()
    {
        Tap(0);
        _r.PointerDown(0, 100, 100, 100);
        _r.Tick(500); // deadline passes while finger is down: decision waits for the up
        Assert.Empty(_out);
        _r.PointerUp(0, 100, 100, 900);

        Assert.Equal([K.LeftClick], Kinds);
    }

    [Fact]
    public void Cancel_DiscardsGestureAndPendingTap()
    {
        Tap(0);
        _r.PointerDown(0, 100, 100, 100);
        _r.PointerDown(1, 150, 100, 110);
        _r.Cancel();

        Assert.False(_r.IsTracking);
        Assert.Null(_r.NextDeadlineMs);
        _r.Tick(5000);
        // The pending tap was flushed when the second finger landed (before cancel); nothing after cancel.
        Assert.Equal([K.LeftClick], Kinds);

        _out.Clear();
        Tap(1000);
        _r.Cancel();
        _r.Tick(5000);
        Assert.Empty(_out);
    }

    [Fact]
    public void Cancel_MidMove_StopsAndNextTouchStartsClean()
    {
        _r.PointerDown(0, 100, 100, 0);
        _r.PointerMove(0, 130, 100, 10);
        _r.Cancel();
        _out.Clear();

        _r.PointerMove(0, 200, 200, 20); // stale pointer after cancel: ignored
        Assert.Empty(_out);

        Tap(100);
        _r.Tick(5000);
        Assert.Equal([K.LeftClick], Kinds);
    }

    [Fact]
    public void UnknownPointerEvents_AreIgnored()
    {
        _r.PointerMove(7, 10, 10, 0);
        _r.PointerUp(7, 10, 10, 10);
        _r.Tick(5000);
        Assert.Empty(_out);
    }
}
