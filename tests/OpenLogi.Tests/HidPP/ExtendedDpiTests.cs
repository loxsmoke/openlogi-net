using OpenLogi.HidPP.Feature;
using OpenLogi.HidPP.Protocol;

namespace OpenLogi.Tests.HidPP;

/// <summary>Ported from the Rust <c>extended_dpi/tests.rs</c> module.</summary>
public class ExtendedDpiTests
{
    [Fact]
    public void ParsesFixedDpiRangesPwsExample()
    {
        byte[] stream = [0x01, 0x90, 0x03, 0x20, 0x04, 0xb0, 0x00, 0x00];
        Assert.Equal(
            [new DpiRange.Fixed(400), new DpiRange.Fixed(800), new DpiRange.Fixed(1200)],
            ExtendedDpi.ParseDpiRanges(stream));
    }

    [Fact]
    public void ParsesSteppedDpiRangesGamingExample()
    {
        byte[] stream = [0x00, 0x64, 0xe0, 0x01, 0x03, 0xe8, 0xe0, 0x64, 0x7d, 0x00, 0x00, 0x00];
        Assert.Equal(
            [new DpiRange.Stepped(100, 1000, 1), new DpiRange.Stepped(1000, 32000, 100)],
            ExtendedDpi.ParseDpiRanges(stream));
    }

    [Fact]
    public void ParsesDpiRangesSplitAcrossPages()
    {
        byte[] page0 = [0x00, 0x64, 0xe0, 0x01, 0x00, 0xc8, 0xe0, 0x02, 0x01, 0xf4, 0xe0, 0x05, 0x03];
        byte[] page1 = [0xe8, 0xe0, 0x0a, 0x07, 0xd0, 0xe0, 0x14, 0x13, 0x88, 0x00, 0x00, 0x00, 0x00];

        Assert.Null(ExtendedDpi.TerminatedWordLen(page0));
        var stream = page0.Concat(page1).ToArray();
        Assert.NotNull(ExtendedDpi.TerminatedWordLen(stream));

        Assert.Equal(
        [
            new DpiRange.Stepped(100, 200, 1),
            new DpiRange.Stepped(200, 500, 2),
            new DpiRange.Stepped(500, 1000, 5),
            new DpiRange.Stepped(1000, 2000, 10),
            new DpiRange.Stepped(2000, 5000, 20),
        ], ExtendedDpi.ParseDpiRanges(stream));
    }

    [Fact]
    public void ParsesRangeFollowedByFixedValue()
    {
        byte[] stream = [0x00, 0x64, 0xe0, 0x01, 0x00, 0xc8, 0x01, 0x90, 0x00, 0x00];
        Assert.Equal(
            [new DpiRange.Stepped(100, 200, 1), new DpiRange.Fixed(400)],
            ExtendedDpi.ParseDpiRanges(stream));
    }

    [Theory]
    [InlineData(new byte[] { 0xe0, 0x01, 0x01, 0x90, 0x00, 0x00 })]          // hyphen without preceding value
    [InlineData(new byte[] { 0x01, 0x90, 0xe0, 0x01, 0x00, 0x00 })]          // hyphen without following value
    [InlineData(new byte[] { 0x01, 0x90, 0xe0, 0x00, 0x04, 0xb0, 0x00, 0x00 })] // zero-step unused marker
    [InlineData(new byte[] { 0x06, 0x40, 0xe0, 0x32, 0x01, 0x90, 0x00, 0x00 })] // descending range
    [InlineData(new byte[] { 0x01, 0x90, 0x03, 0x20 })]                      // unterminated
    public void RejectsMalformedDpiRanges(byte[] stream)
    {
        var ex = Assert.Throws<Hidpp20Exception>(() => ExtendedDpi.ParseDpiRanges(stream));
        Assert.Equal(Hidpp20ErrorKind.UnsupportedResponse, ex.Kind);
    }

    [Fact]
    public void ParsesDpiListWithTerminator()
    {
        byte[] bytes = [0x01, 0x90, 0x03, 0x20, 0x06, 0x40, 0x00, 0x00];
        Assert.Equal([(ushort)400, 800, 1600], ExtendedDpi.ParseDpiList(bytes));
    }

    [Fact]
    public void ParsesDpiListFillingPayload()
    {
        byte[] bytes = [0x01, 0x90, 0x03, 0x20];
        Assert.Equal([(ushort)400, 800], ExtendedDpi.ParseDpiList(bytes));
    }

    [Fact]
    public void ParsesLodList()
    {
        byte[] bytes = [1, 2, 3, 0, 0, 0];
        Assert.Equal([Lod.Low, Lod.Medium, Lod.High], ExtendedDpi.ParseLodList(bytes, 3));
    }

    [Fact]
    public void RejectsUnknownLodValue()
    {
        var ex = Assert.Throws<Hidpp20Exception>(() => ExtendedDpi.ParseLodList([9], 1));
        Assert.Equal(Hidpp20ErrorKind.UnsupportedResponse, ex.Kind);
    }

    [Fact]
    public void RejectsLodListLongerThanPayload()
    {
        var ex = Assert.Throws<Hidpp20Exception>(() => ExtendedDpi.ParseLodList([1, 2], 3));
        Assert.Equal(Hidpp20ErrorKind.UnsupportedResponse, ex.Kind);
    }

    [Fact]
    public void DecodesParametersChangedEvent()
    {
        var payload = new byte[16];
        payload[0] = 1;
        payload[1] = 800 >> 8; payload[2] = 800 & 0xff;
        payload[3] = 1600 >> 8; payload[4] = 1600 & 0xff;
        payload[5] = 2;
        var ev = Assert.IsType<ExtendedDpiEvent.ParametersChanged>(ExtendedDpi.DecodeEvent(0, payload));
        Assert.Equal(1, ev.SensorIndex);
        Assert.Equal((ushort)800, ev.DpiX);
        Assert.Equal((ushort)1600, ev.DpiY);
        Assert.Equal(Lod.Medium, ev.Lod);
    }

    [Fact]
    public void DecodesCalibrationCompletedEvent()
    {
        var payload = new byte[16];
        payload[1] = 1;
        System.Buffers.Binary.BinaryPrimitives.WriteInt16BigEndian(payload.AsSpan(2), 100);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16BigEndian(payload.AsSpan(4), -1);
        var ev = Assert.IsType<ExtendedDpiEvent.CalibrationCompleted>(ExtendedDpi.DecodeEvent(1, payload));
        Assert.Equal(DpiDirection.Y, ev.Direction);
        Assert.Equal((short)100, ev.Correction);
        Assert.Equal((short)-1, ev.Delta);
        Assert.False(ev.Failed);
    }

    [Fact]
    public void FlagsFailedCalibrationEvent()
    {
        var payload = new byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteInt16BigEndian(payload.AsSpan(2), short.MinValue);
        var ev = Assert.IsType<ExtendedDpiEvent.CalibrationCompleted>(ExtendedDpi.DecodeEvent(1, payload));
        Assert.True(ev.Failed);
    }

    [Fact]
    public void IgnoresUnknownEventSubId() => Assert.Null(ExtendedDpi.DecodeEvent(7, new byte[16]));

    [Fact]
    public void IgnoresEventWithUnknownLod()
    {
        var payload = new byte[16];
        payload[5] = 9;
        Assert.Null(ExtendedDpi.DecodeEvent(0, payload));
    }

    [Fact]
    public void EncodesCalibrationCorrectionSentinels()
    {
        Assert.Equal((short)100, new DpiCalibrationCorrection.Adjust(100).ToWire());
        Assert.Equal((short)-512, new DpiCalibrationCorrection.Adjust(-512).ToWire());
        Assert.Equal((short)0, new DpiCalibrationCorrection.RevertToOob().ToWire());
        Assert.Equal(short.MinValue, new DpiCalibrationCorrection.RevertToProfile().ToWire());
    }

    [Theory]
    [InlineData(-1024)]
    [InlineData(1024)]
    [InlineData(short.MinValue)]
    public void RejectsOutOfRangeCalibrationCorrections(short value)
    {
        var ex = Assert.Throws<Hidpp20Exception>(() => new DpiCalibrationCorrection.Adjust(value).ToWire());
        Assert.Equal(Hidpp20ErrorKind.Feature, ex.Kind);
        Assert.Equal(V20ErrorType.InvalidArgument, ex.FeatureError);
    }
}
