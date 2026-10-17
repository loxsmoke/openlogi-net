using OpenLogi.App.Controls;
using OpenLogi.App.Tray;
using OpenLogi.App.Utils;
using OpenLogi.App.ViewModels;
using OpenLogi.Core.DeviceInfo;
using OpenLogi.Core.Localization;
using OpenLogi.HidPP.Feature;

namespace OpenLogi.Tests.App;

/// <summary>
/// The decisions behind a tray battery icon — glyph choice, charge bucketing, the
/// low/charging colour rules and the no-gap layout — without rendering (which needs
/// a live Avalonia platform).
/// </summary>
public class TrayBatteryIconTests
{
    [Theory]
    [InlineData(DeviceKind.Keyboard, TrayGlyph.Keyboard)]
    [InlineData(DeviceKind.Numpad, TrayGlyph.Keyboard)]
    [InlineData(DeviceKind.Mouse, TrayGlyph.Mouse)]
    [InlineData(DeviceKind.Trackball, TrayGlyph.Mouse)]
    [InlineData(DeviceKind.Headset, TrayGlyph.None)]
    [InlineData(DeviceKind.Unknown, TrayGlyph.None)]
    public void GlyphFollowsDeviceKind(DeviceKind kind, TrayGlyph expected) =>
        Assert.Equal(expected, TrayBatteryIconKey.GlyphFor(kind));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(5, 0)]    // flat: draws empty (the red outline carries the warning)
    [InlineData(12, 0)]
    [InlineData(13, 1)]
    [InlineData(37, 1)]
    [InlineData(38, 2)]
    [InlineData(62, 2)]
    [InlineData(63, 3)]
    [InlineData(72, 3)]
    [InlineData(87, 3)]
    [InlineData(88, 4)]
    [InlineData(100, 4)]
    [InlineData(140, 4)]  // clamped
    [InlineData(-5, 0)]
    public void ChargeRoundsToTheNearestQuarter(int percent, int bucket) =>
        Assert.Equal(bucket, TrayBatteryIconKey.BucketFor(percent));

    [Fact]
    public void NearbyReadingsShareAKeySoTheIconIsNotRedrawn()
    {
        var a = TrayBatteryIconKey.For(DeviceKind.Mouse, 63, false, true);
        var b = TrayBatteryIconKey.For(DeviceKind.Mouse, 87, false, true);
        var c = TrayBatteryIconKey.For(DeviceKind.Mouse, 88, false, true);
        Assert.Equal(a, b);
        Assert.Equal(75, a.Percent);
        Assert.NotEqual(a, c);
    }

    [Theory]
    [InlineData(100, false)]
    [InlineData(20, false)]
    [InlineData(16, false)]
    [InlineData(15, true)]   // red battery from here down: 15 % itself is low
    [InlineData(14, true)]
    [InlineData(13, true)]
    [InlineData(0, true)]
    public void LowBatteryIsCorrect(int percent, bool expectedLow)
    {
        Assert.Equal(expectedLow, TrayBatteryIconKey.For(DeviceKind.Mouse, percent, charging: true, true).Low);
        Assert.Equal(expectedLow, TrayBatteryIconKey.For(DeviceKind.Mouse, percent, charging: false, true).Low);
    }

    [Theory]
    [InlineData(100, false, false, "green")]
    [InlineData(100, false, true, "green")]
    [InlineData(100, true, false, "green")]
    [InlineData(20, false, false, "green")]  // same quarter fill as 15 %, but green
    [InlineData(16, false, false, "green")]
    [InlineData(15, false, false, "red")]   // red battery from here down: 15 % itself is low
    [InlineData(14, false, false, "red")]
    [InlineData(13, false, false, "red")]
    [InlineData(12, false, false, "red")]
    [InlineData(0, false, false, "red")]
    [InlineData(100, true, true, "orange")] // Orange battery when charging and highlightCharging is true
    [InlineData(0, true, true, "orange")]
    public void CorrectBatteryLevelColor(int percentage, bool charging, bool highlightCharging, string expectedColor)
    {
        var color = expectedColor switch
        {
            "green" => BatteryUtils.CoolGreen,
            "red" => BatteryUtils.CriticalRed,
            "orange" => BatteryUtils.ChargingOrange,
            _ => throw new ArgumentException($"Unknown expected color '{expectedColor}'")
        };
        Assert.Equal(color, BatteryUtils.ColorFor(percentage, charging, highlightCharging));
    }

    [Fact]
    public void LowReadingsGetTheirOwnKeyEvenWhenTheFillHeightMatches()
    {
        var twenty = TrayBatteryIconKey.For(DeviceKind.Mouse, 20, false, true);
        var fourteen = TrayBatteryIconKey.For(DeviceKind.Mouse, 14, false, true);
        Assert.Equal(twenty.Percent, fourteen.Percent);
        Assert.NotEqual(twenty, fourteen);
    }

    [Fact]
    public void FillRisesFromTheBottomAndKeepsASliver()
    {
        Assert.Equal(0, TrayBatteryIcon.FillHeightFor(0));
        Assert.Equal(5, TrayBatteryIcon.FillHeightFor(25), 3);
        Assert.Equal(10, TrayBatteryIcon.FillHeightFor(50), 3);
        Assert.Equal(15, TrayBatteryIcon.FillHeightFor(75), 3);
        Assert.Equal(20, TrayBatteryIcon.FillHeightFor(100), 3);
        Assert.Equal(20, TrayBatteryIcon.FillHeightFor(140), 3); // never spills past the top
    }

    [Fact]
    public void NoGapLayoutPutsTheGlyphEdgeOnTheBatteryWall()
    {
        // Keyboard body right edge 11.5, mouse cut edge 11, battery left wall 15.5 on
        // the design grid; after the offsets all three sit on the same x.
        var (kbGlyph, kbBattery) = TrayBatteryIcon.OffsetsFor(TrayGlyph.Keyboard);
        var (msGlyph, msBattery) = TrayBatteryIcon.OffsetsFor(TrayGlyph.Mouse);
        Assert.Equal(11.5 + kbGlyph, 15.5 + kbBattery, 3);
        Assert.Equal(11 + msGlyph, 15.5 + msBattery, 3);
        // Battery alone: centred in the 32-unit frame.
        var (_, aloneBattery) = TrayBatteryIcon.OffsetsFor(TrayGlyph.None);
        Assert.Equal(16, (15.5 + 27.5) / 2 + aloneBattery, 3);
    }

    [Theory]
    [InlineData(1.0, 16)]
    [InlineData(1.5, 24)]
    [InlineData(2.0, 32)]
    [InlineData(0.0, 16)]
    [InlineData(double.NaN, 16)]
    public void PixelSizeMatchesTheTaskbarIconSize(double scaling, int expected) =>
        Assert.Equal(expected, TrayBatteryIcon.PixelSizeFor(scaling));

    [Fact]
    public void TooltipNamesDeviceAndCharge()
    {
        var awake = new DeviceViewModel("Bolt", new PairedDevice
        {
            Slot = 1, Codename = "MX Keys", Kind = DeviceKind.Keyboard, Online = true,
            Battery = new BatteryInfo { Percentage = 72, Level = BatteryLevel.Good, Status = BatteryStatus.Discharging },
        }, null);
        Assert.Equal($"MX Keys · 72% · {BatteryStatus.Discharging.Label()}", TrayBatteryIcons.TooltipFor(awake));

        var asleep = new DeviceViewModel("Bolt", new PairedDevice
        {
            Slot = 2, Codename = "MX Master 3S", Kind = DeviceKind.Mouse, Online = false,
            Battery = new BatteryInfo { Percentage = 40, Level = BatteryLevel.Good, Status = BatteryStatus.Discharging },
        }, null);
        Assert.EndsWith(Loc.Current["Device_Asleep"], TrayBatteryIcons.TooltipFor(asleep));
    }

    [Fact]
    public void IdentitySurvivesARescan()
    {
        PairedDevice Device(byte pct) => new()
        {
            Slot = 3, Codename = "MX Anywhere 3S", Kind = DeviceKind.Mouse, Online = true,
            Battery = new BatteryInfo { Percentage = pct, Level = BatteryLevel.Good, Status = BatteryStatus.Discharging },
        };
        var before = new DeviceViewModel("Bolt", Device(80), null);
        var after = new DeviceViewModel("Bolt", Device(60), null);
        Assert.Equal(TrayBatteryIcons.IdentityOf(before), TrayBatteryIcons.IdentityOf(after));
        Assert.NotEqual(TrayBatteryIcons.IdentityOf(before),
            TrayBatteryIcons.IdentityOf(new DeviceViewModel("Bolt", Device(80) with { Slot = 4 }, null)));
    }
}
