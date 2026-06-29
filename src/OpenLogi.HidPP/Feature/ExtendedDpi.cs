using System.Buffers.Binary;
using OpenLogi.HidPP.Protocol;

namespace OpenLogi.HidPP.Feature;

/// <summary>The axis a DPI value or calibration applies to.</summary>
public enum DpiDirection : byte { X = 0, Y = 1 }

/// <summary>A sensor's lift-off distance setting.</summary>
public enum Lod : byte { NotSupported = 0, Low = 1, Medium = 2, High = 3 }

/// <summary>How the device holds the DPI status LED.</summary>
public enum LedHoldType : byte { TimerBased = 0, EventBased = 1, SwControlOn = 2, SwControlOff = 3 }

/// <summary>Where a DPI calibration is computed.</summary>
public enum CalibrationType : byte { Hardware = 0, Software = 1 }

/// <summary>One entry of a sensor's supported-DPI description. Ported from Rust <c>DpiRange</c>.</summary>
public abstract record DpiRange
{
    private DpiRange() { }
    /// <summary>A single selectable DPI value.</summary>
    public sealed record Fixed(ushort Value) : DpiRange;
    /// <summary>A contiguous inclusive range in increments of <paramref name="Step"/>.</summary>
    public sealed record Stepped(ushort From, ushort To, ushort Step) : DpiRange;
}

/// <summary>A DPI calibration correction. Ported from Rust <c>DpiCalibrationCorrection</c>.</summary>
public abstract record DpiCalibrationCorrection
{
    private DpiCalibrationCorrection() { }

    /// <summary>Scale resolution by (1024 + value)/1024; valid -1023..=1023.</summary>
    public sealed record Adjust(short Value) : DpiCalibrationCorrection;
    /// <summary>Revert to the out-of-box setting (wire 0x0000).</summary>
    public sealed record RevertToOob : DpiCalibrationCorrection;
    /// <summary>Revert to the current profile setting (wire 0x8000).</summary>
    public sealed record RevertToProfile : DpiCalibrationCorrection;

    /// <summary>The signed 16-bit wire value; throws <see cref="V20ErrorType.InvalidArgument"/> if out of range.</summary>
    public short ToWire() => this switch
    {
        RevertToProfile => short.MinValue,
        RevertToOob => 0,
        Adjust a when a.Value is >= -1023 and <= 1023 => a.Value,
        Adjust => throw Hidpp20Exception.Feature(V20ErrorType.InvalidArgument),
        _ => throw Hidpp20Exception.UnsupportedResponse(),
    };
}

/// <summary>An event emitted by the ExtendedAdjustableDpi (0x2202) feature.</summary>
public abstract record ExtendedDpiEvent
{
    private ExtendedDpiEvent() { }

    public sealed record ParametersChanged(byte SensorIndex, ushort DpiX, ushort DpiY, Lod Lod) : ExtendedDpiEvent;

    public sealed record CalibrationCompleted(byte SensorIndex, DpiDirection Direction, short Correction, short Delta)
        : ExtendedDpiEvent
    {
        /// <summary>Whether the calibration failed at the sensor level (the 0x8000 sentinel).</summary>
        public bool Failed => Correction == short.MinValue;
    }
}

/// <summary>
/// Pure payload parsers and event decoding for ExtendedAdjustableDpi (0x2202).
/// Ported from Rust <c>extended_dpi::types</c> and <c>extended_dpi::event</c>.
/// </summary>
public static class ExtendedDpi
{
    /// <summary>Words at or above this tag are range "hyphens", not literal DPI values.</summary>
    private const ushort HyphenTag = 0b111 << 13;

    private static ushort Word(ReadOnlySpan<byte> s, int offset) =>
        BinaryPrimitives.ReadUInt16BigEndian(s.Slice(offset, 2));

    /// <summary>The count of bytes up to (excluding) the first 0x0000 word, or <c>null</c> if none (another page is needed).</summary>
    public static int? TerminatedWordLen(ReadOnlySpan<byte> stream)
    {
        var offset = 0;
        while (offset + 1 < stream.Length)
        {
            if (Word(stream, offset) == 0) return offset;
            offset += 2;
        }
        return null;
    }

    /// <summary>Parse an accumulated getSensorDpiRanges stream into <see cref="DpiRange"/>s. Throws on malformed input.</summary>
    public static List<DpiRange> ParseDpiRanges(ReadOnlySpan<byte> stream)
    {
        var len = TerminatedWordLen(stream) ?? throw Hidpp20Exception.UnsupportedResponse();
        var ranges = new List<DpiRange>();
        ushort? pending = null;
        var pendingIsRangeEnd = false;
        var offset = 0;

        while (offset < len)
        {
            var value = Word(stream, offset);
            if (value >= HyphenTag)
            {
                var step = (ushort)(value & ~HyphenTag);
                if (pending is not { } from) throw Hidpp20Exception.UnsupportedResponse();
                if (step == 0 || offset + 3 >= len) throw Hidpp20Exception.UnsupportedResponse();
                var to = Word(stream, offset + 2);
                if (to >= HyphenTag || to < from) throw Hidpp20Exception.UnsupportedResponse();
                ranges.Add(new DpiRange.Stepped(from, to, step));
                pending = to;
                pendingIsRangeEnd = true;
                offset += 4;
            }
            else
            {
                if (pending is { } previous && !pendingIsRangeEnd)
                    ranges.Add(new DpiRange.Fixed(previous));
                pending = value;
                pendingIsRangeEnd = false;
                offset += 2;
            }
        }

        if (pending is { } last && !pendingIsRangeEnd)
            ranges.Add(new DpiRange.Fixed(last));

        if (ranges.Count == 0) throw Hidpp20Exception.UnsupportedResponse();
        return ranges;
    }

    /// <summary>Parse a getSensorDpiList payload (after sensor index/direction) into explicit values, stopping at 0x0000.</summary>
    public static List<ushort> ParseDpiList(ReadOnlySpan<byte> bytes)
    {
        var values = new List<ushort>();
        var offset = 0;
        while (offset + 1 < bytes.Length)
        {
            var value = Word(bytes, offset);
            if (value == 0) break;
            values.Add(value);
            offset += 2;
        }
        return values;
    }

    /// <summary>Parse the first <paramref name="count"/> lift-off-distance entries. Throws on overrun or unknown value.</summary>
    public static List<Lod> ParseLodList(ReadOnlySpan<byte> bytes, int count)
    {
        if (count > bytes.Length) throw Hidpp20Exception.UnsupportedResponse();
        var result = new List<Lod>(count);
        for (var i = 0; i < count; i++)
        {
            if (!Enum.IsDefined(typeof(Lod), bytes[i])) throw Hidpp20Exception.UnsupportedResponse();
            result.Add((Lod)bytes[i]);
        }
        return result;
    }

    /// <summary>Decode an unsolicited 0x2202 event by its sub-id, or <c>null</c> for unknown sub-ids / values.</summary>
    public static ExtendedDpiEvent? DecodeEvent(byte subId, ReadOnlySpan<byte> payload)
    {
        switch (subId)
        {
            case 0:
                if (!Enum.IsDefined(typeof(Lod), payload[5])) return null;
                return new ExtendedDpiEvent.ParametersChanged(
                    payload[0],
                    BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(1, 2)),
                    BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(3, 2)),
                    (Lod)payload[5]);
            case 1:
                if (!Enum.IsDefined(typeof(DpiDirection), payload[1])) return null;
                return new ExtendedDpiEvent.CalibrationCompleted(
                    payload[0],
                    (DpiDirection)payload[1],
                    BinaryPrimitives.ReadInt16BigEndian(payload.Slice(2, 2)),
                    BinaryPrimitives.ReadInt16BigEndian(payload.Slice(4, 2)));
            default:
                return null;
        }
    }
}
