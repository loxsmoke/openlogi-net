using OpenLogi.Core;

namespace OpenLogi.Tests;

public class SmoothScrollScalerTests
{
    [Fact]
    public void MultiplierEightYieldsFifteenPerTick()
    {
        var scaler = new SmoothScrollScaler(8);
        Assert.Equal(15, scaler.Add(1));
        Assert.Equal(-15, scaler.Add(-1));
        Assert.Equal(120, scaler.Add(8)); // a full notch in one event
    }

    [Fact]
    public void RemainderCarriesSoNoRotationIsLost()
    {
        // 7 doesn't divide 120: single ticks emit 17,17,17,17,17,17,18 — exactly 120.
        var scaler = new SmoothScrollScaler(7);
        var total = 0;
        for (var i = 0; i < 7; i++) total += scaler.Add(1);
        Assert.Equal(SmoothScrollScaler.WheelDelta, total);
    }

    [Fact]
    public void NegativeDeltasCarrySymmetrically()
    {
        var scaler = new SmoothScrollScaler(7);
        var total = 0;
        for (var i = 0; i < 7; i++) total += scaler.Add(-1);
        Assert.Equal(-SmoothScrollScaler.WheelDelta, total);
    }

    [Fact]
    public void DirectionReversalMidCarryStaysBounded()
    {
        var scaler = new SmoothScrollScaler(7);
        var total = scaler.Add(1) + scaler.Add(-1);
        Assert.InRange(total, -1, 1); // the carried remainder cancels out
    }

    [Fact]
    public void NonPositiveMultiplierClampsToOne()
    {
        Assert.Equal(120, new SmoothScrollScaler(0).Add(1));
        Assert.Equal(120, new SmoothScrollScaler(-3).Add(1));
    }
}
