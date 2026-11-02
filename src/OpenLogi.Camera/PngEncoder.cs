using System.Buffers.Binary;
using System.IO.Compression;

namespace OpenLogi.Camera;

/// <summary>Minimal PNG writer for a captured <see cref="Frame"/> (8-bit RGB, no filtering).</summary>
public static class PngEncoder
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static void Write(Frame frame, Stream output)
    {
        output.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0), frame.Width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), frame.Height);
        header[8] = 8; // bit depth
        header[9] = 2; // colour type: truecolour
        WriteChunk(output, "IHDR", header);

        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
        {
            var row = new byte[1 + frame.Width * 3];
            for (var y = 0; y < frame.Height; y++)
            {
                row[0] = 0; // filter: none
                var src = (int)(y * frame.Width * 4);
                for (var x = 0; x < frame.Width; x++, src += 4)
                {
                    row[1 + x * 3] = frame.Bgra[src + 2];
                    row[2 + x * 3] = frame.Bgra[src + 1];
                    row[3 + x * 3] = frame.Bgra[src];
                }
                z.Write(row);
            }
        }
        WriteChunk(output, "IDAT", raw.ToArray());
        WriteChunk(output, "IEND", []);
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        output.Write(length);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);
        var crc = Crc(Crc(0xFFFF_FFFF, typeBytes), data) ^ 0xFFFF_FFFF;
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }

    private static uint Crc(uint crc, byte[] data)
    {
        foreach (var b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB8_8320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
