namespace OpenLogi.HidPP.Channel;

/// <summary>Short (7-byte) or long (20-byte) HID++ report.</summary>
public enum HidppReportKind { Short, Long }

/// <summary>
/// An unversioned HID++ message — the raw report payload without the report-id
/// byte. Ported from Rust <c>channel::HidppMessage</c>.
/// </summary>
public sealed class HidppMessage : IEquatable<HidppMessage>
{
    /// <summary>Report id for short HID++ messages.</summary>
    public const byte ShortReportId = 0x10;
    /// <summary>Report id for long HID++ messages.</summary>
    public const byte LongReportId = 0x11;
    /// <summary>Length of a short report including the report-id byte.</summary>
    public const int ShortReportLength = 7;
    /// <summary>Length of a long report including the report-id byte.</summary>
    public const int LongReportLength = 20;

    /// <summary>Payload length (excluding report id): 6 for short, 19 for long.</summary>
    public const int ShortPayloadLength = ShortReportLength - 1;
    public const int LongPayloadLength = LongReportLength - 1;

    private readonly byte[] _payload;

    public HidppReportKind Kind { get; }

    /// <summary>The payload bytes, excluding the report-id byte.</summary>
    public ReadOnlySpan<byte> Payload => _payload;

    private HidppMessage(HidppReportKind kind, byte[] payload)
    {
        Kind = kind;
        _payload = payload;
    }

    /// <summary>Build a short message from a 6-byte payload.</summary>
    public static HidppMessage Short(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != ShortPayloadLength)
            throw new ArgumentException($"short payload must be {ShortPayloadLength} bytes", nameof(payload));
        return new HidppMessage(HidppReportKind.Short, payload.ToArray());
    }

    /// <summary>Build a long message from a 19-byte payload.</summary>
    public static HidppMessage Long(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != LongPayloadLength)
            throw new ArgumentException($"long payload must be {LongPayloadLength} bytes", nameof(payload));
        return new HidppMessage(HidppReportKind.Long, payload.ToArray());
    }

    /// <summary>Try to read a HID++ message from a raw report (including report id), or <c>null</c>.</summary>
    public static HidppMessage? ReadRaw(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return null;
        if (data[0] == ShortReportId)
            return data.Length == ShortReportLength ? Short(data[1..]) : null;
        if (data[0] == LongReportId)
            return data.Length == LongReportLength ? Long(data[1..]) : null;
        return null;
    }

    /// <summary>Write this message in raw report form (report id + payload) into <paramref name="buf"/>; returns bytes written.</summary>
    public int WriteRaw(Span<byte> buf)
    {
        if (Kind == HidppReportKind.Short)
        {
            buf[0] = ShortReportId;
            _payload.CopyTo(buf[1..ShortReportLength]);
            return ShortReportLength;
        }
        buf[0] = LongReportId;
        _payload.CopyTo(buf[1..LongReportLength]);
        return LongReportLength;
    }

    public bool Equals(HidppMessage? other) =>
        other is not null && Kind == other.Kind && _payload.AsSpan().SequenceEqual(other._payload);

    public override bool Equals(object? obj) => Equals(obj as HidppMessage);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Kind);
        hash.AddBytes(_payload);
        return hash.ToHashCode();
    }

    public static bool operator ==(HidppMessage? a, HidppMessage? b) => a?.Equals(b) ?? b is null;
    public static bool operator !=(HidppMessage? a, HidppMessage? b) => !(a == b);

    public override string ToString() => $"{Kind}[{Convert.ToHexString(_payload)}]";
}
