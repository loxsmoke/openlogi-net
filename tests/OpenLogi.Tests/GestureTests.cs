using OpenLogi.Core;

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
        Assert.True(acc.End());

        acc.Begin();
        acc.BackdateHoldForTest();
        Assert.NotNull(acc.Accumulate(Gestures.SwipeThreshold + 10, 0));
        Assert.False(acc.End());
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
        Assert.False(acc.End());
        acc.Begin();
        Assert.True(acc.End());
        Assert.False(acc.End());
    }
}
