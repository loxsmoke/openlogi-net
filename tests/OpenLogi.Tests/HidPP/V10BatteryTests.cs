using OpenLogi.HidPP.Feature;
using OpenLogi.HidPP.Protocol;

namespace OpenLogi.Tests.HidPP;

/// <summary>Register-layout parsing for HID++ 1.0 battery readouts (issue #13, Marathon M705).</summary>
public class V10BatteryTests
{
    [Theory]
    [InlineData(7, HidppBatteryLevel.Full, 90)]
    [InlineData(5, HidppBatteryLevel.Good, 50)]
    [InlineData(3, HidppBatteryLevel.Low, 20)]
    [InlineData(1, HidppBatteryLevel.Critical, 5)]
    public void BatteryStatusMapsTheFourLevels(byte raw, HidppBatteryLevel level, byte percent)
    {
        var info = V10Battery.ParseBatteryStatus([raw, 0x00, 0x00]);
        Assert.NotNull(info);
        Assert.Equal(level, info.Value.Level);
        Assert.Equal(percent, info.Value.ChargingPercentage);
        Assert.Equal(HidppBatteryStatus.Discharging, info.Value.Status);
    }

    [Fact]
    public void BatteryStatusChargingBits()
    {
        Assert.Equal(HidppBatteryStatus.Charging, V10Battery.ParseBatteryStatus([5, 0x21, 0])!.Value.Status);
        Assert.Equal(HidppBatteryStatus.Full, V10Battery.ParseBatteryStatus([7, 0x22, 0])!.Value.Status);
    }

    [Fact]
    public void BatteryStatusRejectsUnknownLevel()
    {
        Assert.Null(V10Battery.ParseBatteryStatus([0, 0, 0]));
        Assert.Null(V10Battery.ParseBatteryStatus([9, 0, 0]));
        Assert.Null(V10Battery.ParseBatteryStatus([7]));
    }

    [Fact]
    public void BatteryChargeIsAPercentageWithStatusNibble()
    {
        var info = V10Battery.ParseBatteryCharge([55, 0x00, 0x30]);
        Assert.NotNull(info);
        Assert.Equal(55, info.Value.ChargingPercentage);
        Assert.Equal(HidppBatteryLevel.Good, info.Value.Level);
        Assert.Equal(HidppBatteryStatus.Discharging, info.Value.Status);

        Assert.Equal(HidppBatteryStatus.Charging, V10Battery.ParseBatteryCharge([12, 0, 0x50])!.Value.Status);
        Assert.Equal(HidppBatteryStatus.Full, V10Battery.ParseBatteryCharge([100, 0, 0x90])!.Value.Status);
        Assert.Equal(HidppBatteryLevel.Critical, V10Battery.ParseBatteryCharge([4, 0, 0x30])!.Value.Level);
    }

    [Fact]
    public void BatteryChargeRejectsImpossiblePercentage() =>
        Assert.Null(V10Battery.ParseBatteryCharge([0xff, 0, 0x30]));
}
