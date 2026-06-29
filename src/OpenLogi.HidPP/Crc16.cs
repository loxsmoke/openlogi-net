namespace OpenLogi.HidPP;

/// <summary>
/// CRC-16/CCITT-FALSE (poly 0x1021, init 0xFFFF, MSB-first, no reflection/xor-out).
/// Logitech onboard-profile sectors store this over the sector body, big-endian at
/// the end (verified against a G915: CRC over bytes [0..253] = stored [253..254]).
/// </summary>
public static class Crc16
{
    public static ushort Ccitt(ReadOnlySpan<byte> data, ushort init = 0xFFFF)
    {
        var crc = init;
        foreach (var b in data)
        {
            crc ^= (ushort)(b << 8);
            for (var i = 0; i < 8; i++)
                crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
        }
        return crc;
    }
}
