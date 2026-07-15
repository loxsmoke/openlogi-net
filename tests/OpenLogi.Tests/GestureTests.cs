using OpenLogi.Core.Gestures;

namespace OpenLogi.Tests;

public class GestureTests
{
    [Fact]
    public void DetectSwipeBelowThresholdKeepsAccumulating()
    {
        Assert.Null(Gestures.DetectSwipe(40, 5));
        Assert.Null(Gestures.DetectSwipe(0, 0));
    }

    [Fact]
    public void DetectSwipeCommitsCleanDirection()
    {
        Assert.Equal(GestureDirection.Right, Gestures.DetectSwipe(120, 5));
        Assert.Equal(GestureDirection.Left, Gestures.DetectSwipe(-120, 5));
        Assert.Equal(GestureDirection.Down, Gestures.DetectSwipe(5, 120));
        Assert.Equal(GestureDirection.Up, Gestures.DetectSwipe(5, -120));
    }

    [Fact]
    public void DetectSwipeRejectsDiagonal()
    {
        Assert.Null(Gestures.DetectSwipe(60, 60));
        Assert.Null(Gestures.DetectSwipe(-60, -60));
    }

    [Fact]
    public void DetectSwipeThresholdAndCrossBandBoundaries()
    {
        Assert.Equal(GestureDirection.Right, Gestures.DetectSwipe(Gestures.SwipeThreshold, 0));
        Assert.Null(Gestures.DetectSwipe(Gestures.SwipeThreshold - 1, 0));

        Assert.Equal(GestureDirection.Right, Gestures.DetectSwipe(200, 69));
        Assert.Null(Gestures.DetectSwipe(200, 71));
        Assert.Equal(GestureDirection.Right, Gestures.DetectSwipe(100, 39));
        Assert.Null(Gestures.DetectSwipe(100, 41));
    }

    [Fact]
    public void DetectSwipeDoesNotThrowOnExtremeValues()
    {
        Assert.Equal(GestureDirection.Right, Gestures.DetectSwipe(int.MaxValue, 0));
        Assert.Equal(GestureDirection.Left, Gestures.DetectSwipe(int.MinValue, 0));
        Assert.Equal(GestureDirection.Down, Gestures.DetectSwipe(0, int.MaxValue));
        Assert.Equal(GestureDirection.Up, Gestures.DetectSwipe(0, int.MinValue));
        Assert.Null(Gestures.DetectSwipe(int.MinValue, int.MinValue));
    }

    [Fact]
    public void AccumulatorCommitsADirectionOnceAfterTheHoldGate()
    {
        var acc = new SwipeAccumulator();
        acc.Begin();
        acc.BackdateHoldForTest();
        Assert.Equal(GestureDirection.Right, acc.Accumulate(Gestures.SwipeThreshold + 10, 0));
        Assert.Null(acc.Accumulate(50, 0));
    }

    [Fact]
    public void AccumulatorDoesNotCommitBeforeTheHoldGate()
    {
        var acc = new SwipeAccumulator();
        acc.Begin();
        Assert.Null(acc.Accumulate(Gestures.SwipeThreshold + 100, 0));
        acc.BackdateHoldForTest();
        Assert.NotNull(acc.Accumulate(Gestures.SwipeThreshold + 100, 0));
    }

    [Fact]
    public void AccumulatorEndReportsClickOnlyWhenNoSwipeFired()
    {
        var acc = new SwipeAccumulator();
        acc.Begin();
        acc.BackdateHoldForTest();
        Assert.Null(acc.Accumulate(2, -1));
        Assert.Equal(GestureDirection.Click, acc.End());

        acc.Begin();
        acc.BackdateHoldForTest();
        Assert.NotNull(acc.Accumulate(Gestures.SwipeThreshold + 10, 0));
        Assert.Null(acc.End());
    }

    [Fact]
    public void AccumulatorIgnoresMotionWhenNotHolding()
    {
        var acc = new SwipeAccumulator();
        Assert.False(acc.IsHolding);
        Assert.Null(acc.Accumulate(Gestures.SwipeThreshold + 100, 0));
    }

    [Fact]
    public void AccumulatorSumsSubThresholdDeltasUntilTheyCommit()
    {
        var acc = new SwipeAccumulator();
        acc.Begin();
        acc.BackdateHoldForTest();
        var step = Gestures.SwipeThreshold / 2 - 1;
        Assert.Null(acc.Accumulate(step, 0));
        Assert.Null(acc.Accumulate(step, 0));
        Assert.Equal(GestureDirection.Right, acc.Accumulate(step, 0));
    }

    [Fact]
    public void AccumulatorSaturatesInsteadOfOverflowing()
    {
        var acc = new SwipeAccumulator();
        acc.Begin();
        acc.BackdateHoldForTest();
        Assert.Null(acc.Accumulate(int.MaxValue, int.MaxValue));
        Assert.Null(acc.Accumulate(int.MaxValue, int.MaxValue));
        acc.Begin();
        acc.BackdateHoldForTest();
        Assert.Equal(GestureDirection.Right, acc.Accumulate(int.MaxValue, 0));
    }

    [Fact]
    public void AccumulatorBeginRecoversAStaleHold()
    {
        var acc = new SwipeAccumulator();
        acc.Begin();
        acc.BackdateHoldForTest();
        Assert.Equal(GestureDirection.Left, acc.Accumulate(-(Gestures.SwipeThreshold + 10), 0));
        acc.Begin();
        acc.BackdateHoldForTest();
        Assert.Equal(GestureDirection.Right, acc.Accumulate(Gestures.SwipeThreshold + 10, 0));
    }

    [Fact]
    public void AccumulatorEndWithoutAHoldIsNotAClick()
    {
        var acc = new SwipeAccumulator();
        Assert.Null(acc.End());
        acc.Begin();
        Assert.Equal(GestureDirection.Click, acc.End());
        Assert.Null(acc.End());
    }

    /// <summary>
    /// The flick that "sometimes does nothing": all the travel lands inside the 160ms
    /// hold gate, so no <see cref="SwipeAccumulator.Accumulate"/> call was allowed to
    /// commit — and once the mouse stops moving the device sends no further raw-XY
    /// event to re-check it. The hold must still settle as the swipe, not a Click.
    /// </summary>
    [Fact]
    public void QuickFlickInsideTheHoldGateStillCommitsOnRelease()
    {
        foreach (var (dx, dy, expected) in new[]
                 {
                     (Gestures.SwipeThreshold + 10, 0, GestureDirection.Right),
                     (-(Gestures.SwipeThreshold + 10), 0, GestureDirection.Left),
                     (0, Gestures.SwipeThreshold + 10, GestureDirection.Down),
                     (0, -(Gestures.SwipeThreshold + 10), GestureDirection.Up),
                 })
        {
            var acc = new SwipeAccumulator();
            acc.Begin();
            Assert.Null(acc.Accumulate(dx, dy)); // gate not passed yet — cannot commit
            acc.BackdateHoldForTest();           // button held on past the gate, mouse now still
            Assert.Equal(expected, acc.End());
        }
    }

    [Fact]
    public void FlickReleasedBeforeTheHoldGateIsStillAClick()
    {
        var acc = new SwipeAccumulator();
        acc.Begin();
        Assert.Null(acc.Accumulate(Gestures.SwipeThreshold + 10, 0));
        // Released inside the gate: a quick click that drifted, not a deliberate swipe.
        Assert.Equal(GestureDirection.Click, acc.End());
    }

    [Fact]
    public void CommittedSwipeIsNotAlsoReportedOnRelease()
    {
        var acc = new SwipeAccumulator();
        acc.Begin();
        acc.BackdateHoldForTest();
        Assert.Equal(GestureDirection.Right, acc.Accumulate(Gestures.SwipeThreshold + 10, 0));
        Assert.Null(acc.End()); // already fired mid-hold — release adds nothing
    }

    [Fact]
    public void LongHoldWithoutCleanTravelIsAClick()
    {
        var acc = new SwipeAccumulator();
        acc.Begin();
        acc.BackdateHoldForTest();
        // Travel is ample but diagonal — no direction resolves, so it stays a click.
        Assert.Null(acc.Accumulate(Gestures.SwipeThreshold + 10, Gestures.SwipeThreshold + 10));
        Assert.Equal(GestureDirection.Click, acc.End());
    }

    // ── Chained gestures within one hold ─────────────────────────────────────
    // Re-arming requires a change of direction: press → left → right fires both
    // without releasing, while one long sweep stays worth exactly one gesture.

    private const int Travel = Gestures.SwipeThreshold + 10;

    /// <summary>Drag in <paramref name="steps"/> increments, collecting whatever fires.</summary>
    private static List<GestureDirection> Drag(SwipeAccumulator acc, int dx, int dy, int steps)
    {
        var fired = new List<GestureDirection>();
        for (var i = 0; i < steps; i++)
            if (acc.Accumulate(dx, dy) is { } dir) fired.Add(dir);
        return fired;
    }

    [Fact]
    public void ReversingWithoutReleasingChainsGestures()
    {
        var acc = new SwipeAccumulator();
        acc.Begin();
        acc.BackdateHoldForTest();
        Assert.Equal([GestureDirection.Left], Drag(acc, -10, 0, 6));
        Assert.Equal([GestureDirection.Right], Drag(acc, 10, 0, 6));
        Assert.Equal([GestureDirection.Left], Drag(acc, -10, 0, 6));
        Assert.Null(acc.End()); // swipes fired — the release is not a click
    }

    [Fact]
    public void TurningOntoTheOtherAxisChainsToo()
    {
        var acc = new SwipeAccumulator();
        acc.Begin();
        acc.BackdateHoldForTest();
        Assert.Equal([GestureDirection.Left], Drag(acc, -10, 0, 6));
        Assert.Equal([GestureDirection.Up], Drag(acc, 0, -10, 6));
    }

    [Fact]
    public void OneLongSweepStillFiresExactlyOnce()
    {
        var acc = new SwipeAccumulator();
        acc.Begin();
        acc.BackdateHoldForTest();
        // 400 units of travel — eight times the threshold — in one continuous sweep.
        Assert.Equal([GestureDirection.Left], Drag(acc, -10, 0, 40));
        Assert.Null(acc.End());
    }

    [Fact]
    public void ArcingSweepDoesNotFireAPhantomCrossAxisGesture()
    {
        var acc = new SwipeAccumulator();
        acc.Begin();
        acc.BackdateHoldForTest();
        // A hand sweeping left naturally curves upward: dominant-left deltas that
        // each carry some upward drift. The drift must not bank into an Up.
        Assert.Equal([GestureDirection.Left], Drag(acc, -10, -4, 40));
        Assert.Null(acc.End());
    }

    [Fact]
    public void JitterOpposingTheStrokeDoesNotResetIt()
    {
        var acc = new SwipeAccumulator();
        acc.Begin();
        acc.BackdateHoldForTest();
        var fired = new List<GestureDirection>();
        // Sub-threshold leftward travel speckled with 1-unit rightward jitter.
        for (var i = 0; i < 12; i++)
        {
            if (acc.Accumulate(-10, 0) is { } a) fired.Add(a);
            if (acc.Accumulate(1, 0) is { } b) fired.Add(b);
        }
        Assert.Equal([GestureDirection.Left], fired);
    }

    [Fact]
    public void FollowThroughDoesNotStiffenTheReversal()
    {
        var acc = new SwipeAccumulator();
        acc.Begin();
        acc.BackdateHoldForTest();
        Assert.Equal([GestureDirection.Left], Drag(acc, -10, 0, 6));
        Drag(acc, -10, 0, 8); // hand drifts on past the commit before reversing
        // The reversal commits on its own fresh threshold — the drift is not owed back.
        Assert.Equal([GestureDirection.Right], Drag(acc, 10, 0, 6));
    }

    [Fact]
    public void SameDirectionCannotRepeatWithinAHold()
    {
        var acc = new SwipeAccumulator();
        acc.Begin();
        acc.BackdateHoldForTest();
        Assert.Equal([GestureDirection.Left], Drag(acc, -10, 0, 6));
        Assert.Empty(Drag(acc, -10, 0, 20)); // still left — re-arm needs a direction change
    }

    /// <summary>
    /// The release-time fallback only ever settles the *first* stroke — the one the
    /// 160ms gate blocked. The gate is measured from the press, so by the time a chain
    /// exists the hold is past it and every later stroke commits on the very event that
    /// crosses the threshold, leaving nothing for <see cref="SwipeAccumulator.End"/>.
    /// </summary>
    [Fact]
    public void ChainedStrokeCommitsOnItsOwnEventNotOnRelease()
    {
        var acc = new SwipeAccumulator();
        acc.Begin();
        acc.BackdateHoldForTest();
        Assert.Equal([GestureDirection.Left], Drag(acc, -10, 0, 6));
        Assert.Equal(GestureDirection.Right, acc.Accumulate(Travel, 0));
        Assert.Null(acc.End());
    }

    [Fact]
    public void PartialReversalAfterAChainIsNeitherGestureNorClick()
    {
        var acc = new SwipeAccumulator();
        acc.Begin();
        acc.BackdateHoldForTest();
        Assert.Equal([GestureDirection.Left], Drag(acc, -10, 0, 6));
        acc.Accumulate(Gestures.SwipeThreshold - 10, 0); // reversal never reaches threshold
        Assert.Null(acc.End());
    }
}
