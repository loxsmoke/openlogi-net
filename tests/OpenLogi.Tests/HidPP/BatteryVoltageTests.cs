using OpenLogi.HidPP.Feature;

namespace OpenLogi.Tests.HidPP;

public class BatteryVoltageTests
{
    [Fact]
    public void ClampsAboveAndBelowCurve()
    {
        Assert.Equal(100, BatteryVoltageFeature.VoltageToPercent(4300));
        Assert.Equal(100, BatteryVoltageFeature.VoltageToPercent(4186));
        Assert.Equal(0, BatteryVoltageFeature.VoltageToPercent(3300));
        Assert.Equal(0, BatteryVoltageFeature.VoltageToPercent(3000));
    }

    [Fact]
    public void HitsCurvePointsExactly()
    {
        Assert.Equal(50, BatteryVoltageFeature.VoltageToPercent(3811));
        Assert.Equal(80, BatteryVoltageFeature.VoltageToPercent(3989));
        Assert.Equal(10, BatteryVoltageFeature.VoltageToPercent(3641));
    }

    [Fact]
    public void InterpolatesBetweenPoints()
    {
        // Midway between (3811,50) and (3859,60) → ~55%.
        var pct = BatteryVoltageFeature.VoltageToPercent(3835);
        Assert.InRange(pct, 54, 56);
    }

    [Fact]
    public void IsMonotonicNonDecreasingWithVoltage()
    {
        byte prev = 0;
        for (ushort mv = 3300; mv <= 4200; mv += 10)
        {
            var pct = BatteryVoltageFeature.VoltageToPercent(mv);
            Assert.True(pct >= prev, $"percent dropped at {mv} mV ({pct} < {prev})");
            prev = pct;
        }
    }
}
