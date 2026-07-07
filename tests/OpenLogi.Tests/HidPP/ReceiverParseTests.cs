using OpenLogi.HidPP.Receiver;

namespace OpenLogi.Tests.HidPP;

/// <summary>Ported from the Rust <c>receiver::bolt</c> parse-helper tests.</summary>
public class ReceiverParseTests
{
    [Fact]
    public void DiscoveryNameWithOversizedLengthIsDropped()
    {
        var payload = new byte[17];
        payload[3] = 200;
        Assert.Null(BoltReceiver.ParseDiscoveryName(payload));
    }

    [Fact]
    public void DiscoveryNameWithinBoundsParses()
    {
        var payload = new byte[17];
        payload[0] = 7;
        payload[3] = 4;
        "Casa"u8.CopyTo(payload.AsSpan(4));
        Assert.Equal((ushort)7, BoltReceiver.ParseDiscoveryName(payload)!.Value.Counter);
        Assert.Equal("Casa", BoltReceiver.ParseDiscoveryName(payload)!.Value.Name);
    }

    [Fact]
    public void DiscoveryNameRejectsInvalidUtf8()
    {
        var payload = new byte[17];
        payload[3] = 2;
        payload[4] = 0xff;
        payload[5] = 0xfe;
        Assert.Null(BoltReceiver.ParseDiscoveryName(payload));
    }

    [Fact]
    public void CodenameWithOversizedLengthClampsToAvailableChunk()
    {
        var response = new byte[16];
        response[2] = 200;
        "MX Anywhere 3"u8.CopyTo(response.AsSpan(3));
        Assert.Equal("MX Anywhere 3", BoltReceiver.ParseCodename(response));
    }

    [Fact]
    public void CodenameWithinBoundsParses()
    {
        var response = new byte[16];
        response[2] = 5;
        "Casa!"u8.CopyTo(response.AsSpan(3));
        Assert.Equal("Casa!", BoltReceiver.ParseCodename(response));
    }

    [Fact]
    public void CodenameRejectsInvalidUtf8()
    {
        var response = new byte[16];
        response[2] = 2;
        response[3] = 0xff;
        response[4] = 0xfe;
        Assert.Null(BoltReceiver.ParseCodename(response));
    }

    // Unifying's 0xb5/0x4n layout differs from Bolt's: length at [1], name from [2].

    [Fact]
    public void UnifyingCodenameParses()
    {
        var response = new byte[16];
        response[0] = 0x41;
        response[1] = 4;
        "G915"u8.CopyTo(response.AsSpan(2));
        Assert.Equal("G915", UnifyingReceiver.ParseCodename(response));
    }

    [Fact]
    public void UnifyingCodenameWithOversizedLengthClampsToPacket()
    {
        var response = new byte[16];
        response[1] = 200;
        "MX Vertical"u8.CopyTo(response.AsSpan(2));
        Assert.Equal("MX Vertical", UnifyingReceiver.ParseCodename(response));
    }

    [Fact]
    public void UnifyingCodenameEmptyOrInvalidYieldsNull()
    {
        Assert.Null(UnifyingReceiver.ParseCodename(new byte[16])); // zero length
        Assert.Null(UnifyingReceiver.ParseCodename([0x41, 2])); // truncated packet

        var bad = new byte[16];
        bad[1] = 2;
        bad[2] = 0xff;
        bad[3] = 0xfe;
        Assert.Null(UnifyingReceiver.ParseCodename(bad));
    }
}
