using OpenLogi.HidPP;
using OpenLogi.HidPP.Channel;
using OpenLogi.HidPP.Protocol;

namespace OpenLogi.Tests.HidPP;

public class NibbleTests
{
    [Fact]
    public void SplitsAndCombinesNibbles()
    {
        Assert.Equal(0x0a, U4.FromLo(0xfa).ToLo());
        Assert.Equal(0x0f, U4.FromHi(0xfa).ToLo());
        Assert.Equal(0xa0, U4.FromLo(0x0a).ToHi());
        Assert.Equal(0x1e, U4.Combine(U4.FromLo(0x1), U4.FromLo(0xe)));
    }
}

public class V20FramingTests
{
    [Fact]
    public void ShortHeaderRoundTripsThroughHidpp()
    {
        var header = new V20MessageHeader(0x02, 0x05, U4.FromLo(0x3), U4.FromLo(0xa));
        var msg = V20Message.Short(header, [0xaa, 0xbb, 0xcc]);
        var back = V20Message.FromHidpp(msg.ToHidpp());
        Assert.Equal(header, back.Header);
        Assert.Equal(msg, back);
        Assert.Equal(HidppReportKind.Short, back.Kind);
    }

    [Fact]
    public void LongHeaderRoundTripsThroughHidpp()
    {
        var header = new V20MessageHeader(0xff, 0x0e, U4.FromLo(0x1), U4.FromLo(0x0));
        var payload = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        var msg = V20Message.Long(header, payload);
        var back = V20Message.FromHidpp(msg.ToHidpp());
        Assert.Equal(msg, back);
    }

    [Fact]
    public void ExtendPayloadPadsShortToLongWidth()
    {
        var msg = V20Message.Short(new V20MessageHeader(1, 2, U4.FromLo(3), U4.FromLo(4)), [0x11, 0x22, 0x33]);
        var extended = msg.ExtendPayload();
        Assert.Equal(16, extended.Length);
        Assert.Equal(new byte[] { 0x11, 0x22, 0x33 }, extended[..3]);
        Assert.True(extended[3..].All(b => b == 0));
    }

    [Fact]
    public async Task SendV20TranslatesDeviceErrorResponse()
    {
        var raw = new MockRawHidChannel();
        await using var channel = await HidppChannel.FromRawChannelAsync(raw);

        var header = new V20MessageHeader(0x02, 0x05, U4.FromLo(0x3), U4.FromLo(0x1));
        var request = V20Message.Short(header, [0x00, 0x00, 0x00]);

        // Error response: feature index 0xff, payload[0]=combine(reqFunc,reqSw),
        // payload[2 of hidpp]=feature index, payload[1 of v20]=error code.
        var errorPayload = new byte[16];
        errorPayload[0] = U4.Combine(header.FunctionId, header.SoftwareId);
        errorPayload[1] = (byte)V20ErrorType.InvalidArgument;
        var errorHeader = new V20MessageHeader(
            header.DeviceIndex, 0xff,
            U4.FromHi(header.FeatureIndex), U4.FromLo(header.FeatureIndex));
        raw.QueueResponse(V20Message.Long(errorHeader, errorPayload).ToHidpp());

        var ex = await Assert.ThrowsAsync<Hidpp20Exception>(() => channel.SendV20Async(request));
        Assert.Equal(Hidpp20ErrorKind.Feature, ex.Kind);
        Assert.Equal(V20ErrorType.InvalidArgument, ex.FeatureError);
    }

    [Fact]
    public async Task SendV20ReturnsMatchingResponse()
    {
        var raw = new MockRawHidChannel();
        await using var channel = await HidppChannel.FromRawChannelAsync(raw);

        var header = new V20MessageHeader(0x02, 0x05, U4.FromLo(0x3), U4.FromLo(0x1));
        var request = V20Message.Short(header, [0x00, 0x00, 0x00]);
        var response = V20Message.Long(header, Enumerable.Repeat((byte)0x7, 16).ToArray());
        raw.QueueResponse(response.ToHidpp());

        var actual = await channel.SendV20Async(request);
        Assert.Equal(header, actual.Header);
        Assert.Equal(Enumerable.Repeat((byte)0x7, 16).ToArray(), actual.ExtendPayload());
    }
}

public class VersionPingTests
{
    private static HidppMessage PingReply(HidppMessage request, byte major, byte minor)
    {
        var header = V20Message.FromHidpp(request).Header;
        var payload = new byte[16];
        payload[0] = major;
        payload[1] = minor;
        return V20Message.Long(header, payload).ToHidpp();
    }

    [Fact]
    public async Task SleepyDeviceAnsweringSecondPingIsDetected()
    {
        // A sleeping Bluetooth device swallows the first ping while its link
        // re-establishes; the retry must find it (regression: single 5 s attempt).
        var pings = 0;
        var raw = new MockRawHidChannel();
        raw.OnWrite = req => ++pings == 1 ? null : PingReply(req, 4, 5);
        await using var channel = await HidppChannel.FromRawChannelAsync(raw);

        var version = await V20.DetermineVersionAsync(
            channel, 0xff, pingTimeout: TimeSpan.FromMilliseconds(50));

        var v20 = Assert.IsType<ProtocolVersion.V20>(version);
        Assert.Equal(4, v20.ProtocolNum);
        Assert.Equal(5, v20.TargetSw);
        Assert.Equal(2, pings);
    }

    [Fact]
    public async Task SilentNodeGivesUpAfterAllAttempts()
    {
        var raw = new MockRawHidChannel();
        await using var channel = await HidppChannel.FromRawChannelAsync(raw);

        var version = await V20.DetermineVersionAsync(
            channel, 0xff, pingTimeout: TimeSpan.FromMilliseconds(50));

        Assert.Null(version);
        Assert.Equal(V20.PingAttempts, raw.WrittenReports().Count);
    }
}

public class V10FramingTests
{
    [Fact]
    public void ShortRoundTripsThroughHidpp()
    {
        var header = new V10MessageHeader(0x01, (byte)V10MessageType.GetRegister);
        var msg = V10Message.Short(header, [0xde, 0xad, 0xbe, 0xef]);
        var back = V10Message.FromHidpp(msg.ToHidpp());
        Assert.Equal(header, back.Header);
        Assert.Equal(new byte[] { 0xde, 0xad, 0xbe, 0xef }, back.ExtendPayload()[..4]);
    }
}
