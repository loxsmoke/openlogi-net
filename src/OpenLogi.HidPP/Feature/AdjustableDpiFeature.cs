using System.Buffers.Binary;
using OpenLogi.HidPP.Channel;
using OpenLogi.HidPP.Protocol;

namespace OpenLogi.HidPP.Feature;

/// <summary>
/// The `AdjustableDpi` / 0x2201 feature — read/change a mouse sensor's DPI.
/// Ported from Rust <c>feature::adjustable_dpi</c>.
/// </summary>
public sealed class AdjustableDpiFeature(FeatureEndpoint endpoint) : ICreatableFeature<AdjustableDpiFeature>
{
    public static ushort Id => 0x2201;
    public static byte StartingVersion => 0;
    public static AdjustableDpiFeature Create(HidppChannel channel, byte deviceIndex, byte featureIndex) =>
        new(new FeatureEndpoint(channel, deviceIndex, featureIndex));

    /// <summary>The number of sensors the device exposes.</summary>
    public async Task<byte> GetSensorCountAsync() =>
        (await endpoint.CallAsync(0, [0, 0, 0]).ConfigureAwait(false)).ExtendPayload()[0];

    /// <summary>The supported DPI values for a sensor (range markers expanded, sorted, deduplicated).</summary>
    public async Task<List<ushort>> GetSensorDpiListAsync(byte sensorIndex)
    {
        var payload = (await endpoint.CallAsync(1, [sensorIndex, 0x00, 0x00]).ConfigureAwait(false)).ExtendPayload();
        return ParseDpiListPayload(payload.AsSpan(1)); // skip the echoed sensor index
    }

    /// <summary>The currently configured DPI for a sensor.</summary>
    public async Task<ushort> GetSensorDpiAsync(byte sensorIndex)
    {
        var payload = (await endpoint.CallAsync(2, [sensorIndex, 0x00, 0x00]).ConfigureAwait(false)).ExtendPayload();
        return BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(1, 2));
    }

    /// <summary>Set the DPI for a sensor.</summary>
    public async Task SetSensorDpiAsync(byte sensorIndex, ushort dpi)
    {
        Span<byte> dpiBytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(dpiBytes, dpi);
        await endpoint.CallAsync(3, [sensorIndex, dpiBytes[0], dpiBytes[1]]).ConfigureAwait(false);
    }

    /// <summary>
    /// Parse a getSensorDpiList payload (after the echoed sensor index) into
    /// explicit DPI values: big-endian words, a <c>0xe000|step</c> range marker
    /// expands between the previous value and the following one, <c>0x0000</c>
    /// terminates. The result is sorted and deduplicated. Throws on malformed input.
    /// </summary>
    public static List<ushort> ParseDpiListPayload(ReadOnlySpan<byte> bytes)
    {
        var values = new List<ushort>();
        var offset = 0;
        while (offset + 1 < bytes.Length)
        {
            var value = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset, 2));
            if (value == 0) break; // terminator (absent when the list fills the response)

            if (value >> 13 == 0b111)
            {
                var step = value & 0x1fff;
                if (step == 0 || offset + 3 >= bytes.Length)
                    throw Hidpp20Exception.UnsupportedResponse();
                if (values.Count == 0)
                    throw Hidpp20Exception.UnsupportedResponse();
                uint start = values[^1];
                var last = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 2, 2));
                if (last < start)
                    throw Hidpp20Exception.UnsupportedResponse();
                var next = start + (uint)step;
                while (next < last)
                {
                    values.Add((ushort)next);
                    next += (uint)step;
                }
                values.Add(last); // high endpoint always supported, even off-grid
                offset += 4;
            }
            else
            {
                values.Add(value);
                offset += 2;
            }
        }

        if (values.Count == 0)
            throw Hidpp20Exception.UnsupportedResponse();
        values.Sort();
        return [.. values.Distinct()];
    }
}
