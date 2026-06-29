using OpenLogi.HidPP;

namespace OpenLogi.Tests.HidPP;

public class Crc16Tests
{
    [Fact]
    public void Ccitt_MatchesStandardCheckVector()
    {
        // CRC-16/CCITT-FALSE check value for ASCII "123456789" is 0x29B1.
        var data = "123456789"u8.ToArray();
        Assert.Equal(0x29B1, Crc16.Ccitt(data));
    }

    [Fact]
    public void Ccitt_EmptyIsInit()
    {
        Assert.Equal(0xFFFF, Crc16.Ccitt([]));
    }
}
