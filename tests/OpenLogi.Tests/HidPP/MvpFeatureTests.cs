using OpenLogi.HidPP.Feature;
using OpenLogi.HidPP.Protocol;

namespace OpenLogi.Tests.HidPP;

/// <summary>Ported from the Rust <c>adjustable_dpi</c> parser tests.</summary>
public class AdjustableDpiTests
{
    [Fact]
    public void ParsesExplicitDpiList()
    {
        byte[] payload = [0x01, 0x90, 0x03, 0x20, 0x06, 0x40, 0x00, 0x00];
        Assert.Equal([(ushort)400, 800, 1600], AdjustableDpiFeature.ParseDpiListPayload(payload));
    }

    [Fact]
    public void ExpandsRangeEncodedDpiList()
    {
        byte[] payload = [0x01, 0x90, 0xe1, 0x90, 0x06, 0x40, 0x00, 0x00];
        Assert.Equal([(ushort)400, 800, 1200, 1600], AdjustableDpiFeature.ParseDpiListPayload(payload));
    }

    [Fact]
    public void SortsAndDeduplicatesValues()
    {
        byte[] payload = [0x06, 0x40, 0x03, 0x20, 0x03, 0x20, 0x00, 0x00];
        Assert.Equal([(ushort)800, 1600], AdjustableDpiFeature.ParseDpiListPayload(payload));
    }

    [Fact]
    public void RangeKeepsOffGridHighEndpoint()
    {
        byte[] payload = [0x01, 0x90, 0xe1, 0x90, 0x05, 0xdc, 0x00, 0x00];
        Assert.Equal([(ushort)400, 800, 1200, 1500], AdjustableDpiFeature.ParseDpiListPayload(payload));
    }

    [Fact]
    public void ParsesFullListWithoutTerminator()
    {
        byte[] payload = [0x01, 0x90, 0x03, 0x20, 0x06, 0x40];
        Assert.Equal([(ushort)400, 800, 1600], AdjustableDpiFeature.ParseDpiListPayload(payload));
    }

    [Theory]
    [InlineData(new byte[] { 0xe0, 0x32, 0x1f, 0x40, 0x00, 0x00 })]          // range marker without previous value
    [InlineData(new byte[] { 0x01, 0x90, 0xe0, 0x32 })]                      // range marker without end value
    [InlineData(new byte[] { 0x01, 0x90, 0xe0, 0x00, 0x06, 0x40, 0x00, 0x00 })] // zero-step marker
    [InlineData(new byte[] { 0x06, 0x40, 0xe0, 0x32, 0x01, 0x90, 0x00, 0x00 })] // descending marker
    [InlineData(new byte[] { 0x00, 0x00 })]                                  // no values
    public void RejectsMalformedDpiList(byte[] payload)
    {
        var ex = Assert.Throws<Hidpp20Exception>(() => AdjustableDpiFeature.ParseDpiListPayload(payload));
        Assert.Equal(Hidpp20ErrorKind.UnsupportedResponse, ex.Kind);
    }
}

public class SmartShiftBatteryTests
{
    [Fact]
    public void BatteryCapabilitiesParseFlags()
    {
        // flags: Critical(1) + Good(4) = 0b0101; caps: rechargeable(1) + percentage(2) = 0b11
        var caps = BatteryCapabilities.FromBytes(0b0000_0101, 0b0000_0011);
        Assert.Contains(HidppBatteryLevel.Critical, caps.ReportedLevels);
        Assert.Contains(HidppBatteryLevel.Good, caps.ReportedLevels);
        Assert.DoesNotContain(HidppBatteryLevel.Low, caps.ReportedLevels);
        Assert.True(caps.Rechargeable);
        Assert.True(caps.Percentage);
    }

    [Fact]
    public void BatteryLevelAndStatusParsing()
    {
        Assert.Equal(HidppBatteryLevel.Full, UnifiedBatteryFeature.TryLevel(0b1000));
        Assert.Null(UnifiedBatteryFeature.TryLevel(0b0101)); // not a single defined flag value
        Assert.Equal(HidppBatteryStatus.ChargingSlow, UnifiedBatteryFeature.TryStatus(2));
        Assert.Null(UnifiedBatteryFeature.TryStatus(9));
    }

    [Fact]
    public void SmartShiftWheelModeValues()
    {
        Assert.Equal(1, (byte)SmartShiftWheelMode.Freespin);
        Assert.Equal(2, (byte)SmartShiftWheelMode.Ratchet);
    }
}

public class EventEmitterTests
{
    [Fact]
    public async Task EmitReachesEachReceiver()
    {
        var emitter = new EventEmitter<int>();
        var a = emitter.CreateReceiver();
        var b = emitter.CreateReceiver();

        emitter.Emit(7);

        Assert.Equal(7, await a.ReadAsync());
        Assert.Equal(7, await b.ReadAsync());
    }

    [Fact]
    public void RemovedReceiverStopsGettingEvents()
    {
        var emitter = new EventEmitter<int>();
        var a = emitter.CreateReceiver();
        emitter.RemoveReceiver(a);
        emitter.Emit(7);
        Assert.False(a.TryRead(out _));
    }
}
