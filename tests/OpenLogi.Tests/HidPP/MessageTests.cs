using OpenLogi.HidPP.Channel;

namespace OpenLogi.Tests.HidPP;

/// <summary>
/// Raw report framing. Windows reads always return the collection's maximum
/// input-report length (zero-padded), so parsing must tolerate oversized
/// buffers — a short report in a 20-byte read is the normal case on an
/// interface that declares both HID++ widths (regression: such reports were
/// dropped, so every Unifying-receiver response timed out and receiver-paired
/// devices were never detected).
/// </summary>
public class MessageTests
{
    [Fact]
    public void ShortReportInPaddedBufferParses()
    {
        byte[] payload = [0xff, 0x80, 0x02, 0x00, 0x00, 0x00];
        var padded = new byte[HidppMessage.LongReportLength]; // Windows pads to the collection max (20 here)
        padded[0] = HidppMessage.ShortReportId;
        payload.CopyTo(padded, 1);

        Assert.Equal(HidppMessage.Short(payload), HidppMessage.ReadRaw(padded));
    }

    [Fact]
    public void LongReportInPaddedBufferParses()
    {
        var payload = Enumerable.Range(1, HidppMessage.LongPayloadLength).Select(i => (byte)i).ToArray();
        var padded = new byte[32]; // DJ receivers carry 32-byte reports, so the collection max can exceed 20
        padded[0] = HidppMessage.LongReportId;
        payload.CopyTo(padded, 1);

        Assert.Equal(HidppMessage.Long(payload), HidppMessage.ReadRaw(padded));
    }

    [Fact]
    public void ExactLengthReportsStillParse()
    {
        byte[] shortRaw = [HidppMessage.ShortReportId, 0xff, 0x81, 0x02, 0x01, 0x02, 0x03];
        Assert.Equal(HidppMessage.Short(shortRaw[1..]), HidppMessage.ReadRaw(shortRaw));

        var longRaw = new byte[HidppMessage.LongReportLength];
        longRaw[0] = HidppMessage.LongReportId;
        Assert.Equal(HidppMessage.Long(longRaw[1..]), HidppMessage.ReadRaw(longRaw));
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { HidppMessage.ShortReportId, 0xff, 0x80 })] // truncated short
    [InlineData(new byte[] { HidppMessage.LongReportId, 0xff, 0x80, 0x02, 0x00, 0x00, 0x00 })] // long id, short body
    [InlineData(new byte[] { 0x20, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06 })] // DJ report id
    public void OtherReportsAreRejected(byte[] raw)
    {
        Assert.Null(HidppMessage.ReadRaw(raw));
    }
}
