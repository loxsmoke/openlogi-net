namespace OpenLogi.HidPP;

/// <summary>Packed binary-coded-decimal decoding. Ported from Rust <c>bcd</c>.</summary>
public static class Bcd
{
    /// <summary>Decode a packed-BCD byte (two decimal digits), or <c>null</c> if a nibble exceeds 9.</summary>
    public static byte? ConvertPackedU8(byte bcd)
    {
        var digit0 = U4.FromHi(bcd).ToLo();
        var digit1 = U4.FromLo(bcd).ToLo();
        if (digit0 > 9 || digit1 > 9) return null;
        return (byte)(digit0 * 10 + digit1);
    }

    /// <summary>Decode a packed-BCD u16 (four decimal digits), or <c>null</c> if any nibble exceeds 9.</summary>
    public static ushort? ConvertPackedU16(ushort bcd)
    {
        var hi = ConvertPackedU8((byte)(bcd >> 8));
        var lo = ConvertPackedU8((byte)(bcd & 0xff));
        if (hi is null || lo is null) return null;
        return (ushort)(hi.Value * 100 + lo.Value);
    }
}
