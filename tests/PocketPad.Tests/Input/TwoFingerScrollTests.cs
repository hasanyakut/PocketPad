using PocketPad.Shared.Input;
using K = PocketPad.Shared.Input.TouchpadGestureKind;

namespace PocketPad.Tests.Input;

/// <summary>Phase 4: two-finger scroll and the transitions between one and two fingers.</summary>
public class TwoFingerScrollTests
{
    private readonly TouchpadGestureRecognizer _r = new(new TouchpadGestureOptions { TapSlop = 8, ScrollSlop = 10 });
    private readonly List<TouchpadGesture> _out = [];
    private int _cancelled;

    public TwoFingerScrollTests()
    {
        _r.GestureDetected += _out.Add;
        _r.GestureCancelled += () => _cancelled++;
    }

    private IEnumerable<TouchpadGesture> Moves => _out.Where(g => g.Kind == K.Move);
    private IEnumerable<TouchpadGesture> Scrolls => _out.Where(g => g.Kind == K.Scroll);

    /// <summary>Both fingers in one platform move event, as the Android handler delivers them.</summary>
    private void MoveBoth(double x0, double y0, double x1, double y1, long t) =>
        _r.PointersMoved([new TouchPoint(0, x0, y0), new TouchPoint(1, x1, y1)], t);

    private void TwoFingersDown(long t = 0, double y = 300)
    {
        _r.PointerDown(0, 100, y, t);
        _r.PointerDown(1, 180, y, t + 10);
    }

    // 1. Single finger movement still works unchanged.
    [Fact]
    public void SingleFinger_MovesCursor_AndNeverScrolls()
    {
        _r.PointerDown(0, 100, 100, 0);
        _r.PointerMove(0, 100, 120, 10);
        _r.PointerMove(0, 100, 150, 20);
        _r.PointerMove(0, 100, 180, 30);
        _r.PointerUp(0, 100, 180, 40);
        _r.Tick(5000);

        Assert.Empty(Scrolls);
        Assert.Equal(60, Moves.Sum(m => m.Dy));
        Assert.All(_out, g => Assert.Equal(K.Move, g.Kind));
    }

    // 2. Two-finger up / down scroll.
    [Fact]
    public void TwoFingersDown_ScrollsWithPositiveDy_WithoutCursorMovement()
    {
        TwoFingersDown();
        MoveBoth(100, 315, 180, 315, 30);   // crosses the scroll slop: scrolling starts here, no jump
        MoveBoth(100, 335, 180, 335, 40);
        MoveBoth(100, 360, 180, 360, 50);

        Assert.Empty(Moves);
        Assert.Equal([20d, 25d], Scrolls.Select(s => s.Dy));
        Assert.All(Scrolls, s => Assert.Equal(0, s.Dx));
    }

    [Fact]
    public void TwoFingersUp_ScrollsWithNegativeDy()
    {
        TwoFingersDown();
        MoveBoth(100, 285, 180, 285, 30);
        MoveBoth(100, 260, 180, 260, 40);
        MoveBoth(100, 230, 180, 230, 50);

        Assert.Empty(Moves);
        Assert.True(Scrolls.Sum(s => s.Dy) < 0);
        Assert.Equal(-55, Scrolls.Sum(s => s.Dy));
    }

    [Fact]
    public void SmallTwoFingerJitter_DoesNotScroll()
    {
        TwoFingersDown();
        MoveBoth(102, 304, 179, 297, 30);
        MoveBoth(98, 306, 183, 303, 40);

        Assert.Empty(_out);
    }

    [Fact]
    public void FingersMovedSeparately_ProduceTheCentroidDelta()
    {
        TwoFingersDown();
        MoveBoth(100, 330, 180, 330, 30); // start scrolling
        _r.PointerMove(0, 100, 350, 40);  // one finger alone: centroid moves half
        _r.PointerMove(1, 180, 350, 41);

        Assert.Equal(20, Scrolls.Sum(s => s.Dy));
    }

    // 3. One finger → two fingers.
    [Fact]
    public void OneToTwoFingers_StopsCursor_ThenScrolls_WithoutJump()
    {
        _r.PointerDown(0, 100, 100, 0);
        _r.PointerMove(0, 100, 130, 10);
        _r.PointerMove(0, 100, 150, 20);
        var movesBefore = Moves.Count();

        // Second finger lands far away: the centroid jumps, but that must not become a scroll.
        _r.PointerDown(1, 400, 700, 30);
        Assert.Empty(Scrolls);

        MoveBoth(100, 165, 400, 715, 40); // crosses slop → scrolling starts
        MoveBoth(100, 175, 400, 725, 50);

        Assert.Equal(movesBefore, Moves.Count());
        Assert.Equal([10d], Scrolls.Select(s => s.Dy));
    }

    // 4. Two fingers → one finger.
    [Fact]
    public void TwoToOneFinger_ResumesCursorFromRemainingFinger_WithoutJump()
    {
        TwoFingersDown();
        MoveBoth(100, 330, 180, 330, 30);
        MoveBoth(100, 360, 180, 360, 40);
        _r.PointerUp(1, 180, 360, 50);
        var scrollsBefore = Scrolls.Count();

        _r.PointerMove(0, 100, 364, 60);   // lift-off wobble below the slop: ignored
        Assert.Empty(Moves);

        _r.PointerMove(0, 100, 380, 70);   // beyond slop: movement resumes from here
        _r.PointerMove(0, 110, 395, 80);

        Assert.Equal(scrollsBefore, Scrolls.Count());
        Assert.Equal([new TouchpadGesture(K.Move, 10, 15)], Moves);
    }

    [Fact]
    public void TwoToOneFinger_RemainingFingerCanBeAnyOfTheTwo()
    {
        TwoFingersDown();
        _r.PointerUp(0, 100, 300, 50);     // the *first* finger lifts; id 1 stays
        _r.PointerMove(1, 180, 330, 60);
        _r.PointerMove(1, 180, 345, 70);

        Assert.Equal([new TouchpadGesture(K.Move, 0, 15)], Moves);
    }

    [Fact]
    public void BackToTwoFingers_AfterResume_ScrollsAgainFromNewBaseline()
    {
        TwoFingersDown();
        _r.PointerUp(1, 180, 300, 50);
        _r.PointerDown(2, 300, 900, 60);   // a new finger with a different id far away
        _r.PointersMoved([new TouchPoint(0, 100, 315), new TouchPoint(2, 300, 915)], 70);
        _r.PointersMoved([new TouchPoint(0, 100, 325), new TouchPoint(2, 300, 925)], 80);

        Assert.Empty(Moves);
        Assert.Equal([10d], Scrolls.Select(s => s.Dy));
    }

    // 5. No clicks during or after a scroll.
    [Fact]
    public void Scroll_NeverProducesClicks()
    {
        TwoFingersDown(t: 0);
        MoveBoth(100, 320, 180, 320, 20);
        MoveBoth(100, 340, 180, 340, 40);
        _r.PointerUp(0, 100, 340, 60);   // quick lift, well within the right-click time limit
        _r.PointerUp(1, 180, 340, 70);
        _r.Tick(5000);

        Assert.NotEmpty(Scrolls);
        Assert.DoesNotContain(_out, g => g.Kind is K.LeftClick or K.RightClick or K.DoubleClick);
        Assert.Null(_r.NextDeadlineMs);
    }

    [Fact]
    public void StillTwoFingerTap_IsStillRightClick()
    {
        TwoFingersDown(t: 0);
        _r.PointerUp(0, 101, 301, 100);
        _r.PointerUp(1, 180, 300, 110);
        _r.Tick(5000);

        Assert.Equal([K.RightClick], _out.Select(g => g.Kind));
    }

    [Fact]
    public void TwoFingerTap_ThenRemainingFingerMoves_IsNotRightClick()
    {
        TwoFingersDown(t: 0);
        _r.PointerUp(1, 180, 300, 50);
        _r.PointerMove(0, 100, 330, 60);
        _r.PointerUp(0, 100, 330, 100);
        _r.Tick(5000);

        Assert.DoesNotContain(K.RightClick, _out.Select(g => g.Kind));
    }

    [Fact]
    public void ThreeFingers_NeitherScrollNorClick()
    {
        TwoFingersDown();
        _r.PointerDown(2, 260, 300, 20);
        _r.PointersMoved([new TouchPoint(0, 100, 360), new TouchPoint(1, 180, 360), new TouchPoint(2, 260, 360)], 40);
        _r.PointerUp(2, 260, 360, 50);
        _r.PointerUp(1, 180, 360, 60);
        _r.PointerUp(0, 100, 360, 70);
        _r.Tick(5000);

        Assert.Empty(_out);
    }

    // 6. ACTION_CANCEL resets everything.
    [Fact]
    public void Cancel_MidScroll_StopsScrolling_AndRaisesCancelled()
    {
        TwoFingersDown();
        MoveBoth(100, 330, 180, 330, 30);
        MoveBoth(100, 350, 180, 350, 40);
        var before = _out.Count;

        _r.Cancel();
        MoveBoth(100, 500, 180, 500, 50);  // stale moves after cancel are ignored
        _r.PointerUp(0, 100, 500, 60);
        _r.PointerUp(1, 180, 500, 60);
        _r.Tick(5000);

        Assert.Equal(1, _cancelled);
        Assert.Equal(before, _out.Count);
        Assert.False(_r.IsTracking);
        Assert.False(_r.IsScrolling);
    }

    [Fact]
    public void AfterCancel_NextGestureStartsClean()
    {
        TwoFingersDown();
        MoveBoth(100, 330, 180, 330, 30);
        _r.Cancel();
        _out.Clear();

        _r.PointerDown(5, 50, 50, 100);
        _r.PointerMove(5, 50, 70, 110);
        _r.PointerMove(5, 50, 80, 120);
        _r.PointerUp(5, 50, 80, 130);

        Assert.Equal([new TouchpadGesture(K.Move, 0, 10)], _out);
    }
}

public class TouchpadSettingsTests
{
    [Fact]
    public void NaturalScroll_FingersDownScrollsUp()
    {
        var s = new TouchpadSettings { ScrollSpeed = 1 };
        Assert.Equal(30, s.WheelDelta(10));   // 10 dp down → +30 wheel units (up)
        Assert.Equal(-30, s.WheelDelta(-10));
    }

    [Fact]
    public void ReverseScroll_FlipsDirection_AndSpeedScales()
    {
        var s = new TouchpadSettings { ScrollSpeed = 2, ReverseScroll = true };
        Assert.Equal(-60, s.WheelDelta(10));
    }

    [Fact]
    public void MouseSensitivity_ScalesPointerDelta()
    {
        var s = new TouchpadSettings { MouseSensitivity = 2 };
        Assert.Equal((4d, -6d), s.PointerDelta(2, -3));
    }

    [Fact]
    public void Normalized_ClampsOutOfRangeStoredValues()
    {
        var s = new TouchpadSettings { MouseSensitivity = 99, ScrollSpeed = -1 }.Normalized();
        Assert.Equal(TouchpadSettings.MaxSensitivity, s.MouseSensitivity);
        Assert.Equal(TouchpadSettings.MinScrollSpeed, s.ScrollSpeed);
    }
}
