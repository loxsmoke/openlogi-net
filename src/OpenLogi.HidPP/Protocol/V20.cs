using OpenLogi.HidPP.Channel;

namespace OpenLogi.HidPP.Protocol;

/// <summary>The header every HID++2.0 message starts with. Ported from Rust <c>protocol::v20::MessageHeader</c>.</summary>
public readonly record struct V20MessageHeader(byte DeviceIndex, byte FeatureIndex, U4 FunctionId, U4 SoftwareId);

/// <summary>A HID++2.0 message: short (3-byte payload) or long (16-byte payload).</summary>
public sealed class V20Message : IEquatable<V20Message>
{
    /// <summary>Payload length of a short v20 message.</summary>
    public const int ShortPayloadLength = HidppMessage.ShortReportLength - 4; // 3
    /// <summary>Payload length of a long v20 message.</summary>
    public const int LongPayloadLength = HidppMessage.LongReportLength - 4; // 16

    public HidppReportKind Kind { get; }
    public V20MessageHeader Header { get; }
    private readonly byte[] _payload;

    private V20Message(HidppReportKind kind, V20MessageHeader header, byte[] payload)
    {
        Kind = kind;
        Header = header;
        _payload = payload;
    }

    public static V20Message Short(V20MessageHeader header, ReadOnlySpan<byte> payload)
    {
        if (payload.Length != ShortPayloadLength)
            throw new ArgumentException($"short v20 payload must be {ShortPayloadLength} bytes", nameof(payload));
        return new V20Message(HidppReportKind.Short, header, payload.ToArray());
    }

    public static V20Message Long(V20MessageHeader header, ReadOnlySpan<byte> payload)
    {
        if (payload.Length != LongPayloadLength)
            throw new ArgumentException($"long v20 payload must be {LongPayloadLength} bytes", nameof(payload));
        return new V20Message(HidppReportKind.Long, header, payload.ToArray());
    }

    /// <summary>The payload extended to the longest possible width (16 bytes), zero-padded.</summary>
    public byte[] ExtendPayload()
    {
        var data = new byte[LongPayloadLength];
        _payload.CopyTo(data.AsSpan());
        return data;
    }

    public static V20Message FromHidpp(HidppMessage msg)
    {
        var p = msg.Payload;
        var header = new V20MessageHeader(p[0], p[1], U4.FromHi(p[2]), U4.FromLo(p[2]));
        return msg.Kind == HidppReportKind.Short
            ? Short(header, p[3..])
            : Long(header, p[3..]);
    }

    public HidppMessage ToHidpp()
    {
        if (Kind == HidppReportKind.Short)
        {
            var data = new byte[HidppMessage.ShortPayloadLength];
            data[0] = Header.DeviceIndex;
            data[1] = Header.FeatureIndex;
            data[2] = U4.Combine(Header.FunctionId, Header.SoftwareId);
            _payload.CopyTo(data.AsSpan(3));
            return HidppMessage.Short(data);
        }
        else
        {
            var data = new byte[HidppMessage.LongPayloadLength];
            data[0] = Header.DeviceIndex;
            data[1] = Header.FeatureIndex;
            data[2] = U4.Combine(Header.FunctionId, Header.SoftwareId);
            _payload.CopyTo(data.AsSpan(3));
            return HidppMessage.Long(data);
        }
    }

    public bool Equals(V20Message? other) =>
        other is not null && Kind == other.Kind && Header == other.Header
        && _payload.AsSpan().SequenceEqual(other._payload);

    public override bool Equals(object? obj) => Equals(obj as V20Message);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Kind);
        hash.Add(Header);
        hash.AddBytes(_payload);
        return hash.ToHashCode();
    }
}

/// <summary>HID++2.0 feature error codes. Ported from Rust <c>protocol::v20::ErrorType</c>.</summary>
public enum V20ErrorType : byte
{
    NoError = 0,
    Unknown = 1,
    InvalidArgument = 2,
    OutOfRange = 3,
    HwError = 4,
    LogitechInternal = 5,
    InvalidFeatureIndex = 6,
    InvalidFunctionId = 7,
    Busy = 8,
    Unsupported = 9,
}

/// <summary>Discriminator for <see cref="Hidpp20Exception"/>.</summary>
public enum Hidpp20ErrorKind { Channel, Feature, UnsupportedResponse }

/// <summary>An error from a HID++2.0 feature call. Ported from Rust <c>protocol::v20::Hidpp20Error</c>.</summary>
public sealed class Hidpp20Exception : Exception
{
    public Hidpp20ErrorKind Kind { get; }
    /// <summary>The device-reported error code, set only when <see cref="Kind"/> is <see cref="Hidpp20ErrorKind.Feature"/>.</summary>
    public V20ErrorType? FeatureError { get; }

    private Hidpp20Exception(Hidpp20ErrorKind kind, string message, V20ErrorType? featureError = null, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        FeatureError = featureError;
    }

    public static Hidpp20Exception Feature(V20ErrorType error) =>
        new(Hidpp20ErrorKind.Feature, $"a HID++2.0 feature returned an error: {error}", error);

    public static Hidpp20Exception UnsupportedResponse() =>
        new(Hidpp20ErrorKind.UnsupportedResponse, "the received response from the device is (partly) unsupported");

    public static Hidpp20Exception Channel(ChannelException inner) =>
        new(Hidpp20ErrorKind.Channel, "the HID++ channel returned an error", inner: inner);
}

/// <summary>HID++2.0 request helpers and protocol-version detection over a <see cref="HidppChannel"/>.</summary>
public static class V20
{
    /// <summary>
    /// Send a HID++2.0 message and wait for a response matching the header,
    /// translating a device error response into <see cref="Hidpp20Exception"/>.
    /// Ported from Rust <c>HidppChannel::send_v20</c>.
    /// </summary>
    public static async Task<V20Message> SendV20Async(this HidppChannel channel, V20Message msg)
    {
        var header = msg.Header;
        HidppMessage response;
        try
        {
            response = await channel.SendAsync(msg.ToHidpp(), raw =>
            {
                var respMsg = V20Message.FromHidpp(raw);
                var respHeader = respMsg.Header;

                // An error response sets feature index 0xFF and shifts header values right by one byte.
                var isError = respHeader.DeviceIndex == header.DeviceIndex
                    && respHeader.FeatureIndex == 0xff
                    && U4.Combine(respHeader.FunctionId, respHeader.SoftwareId) == header.FeatureIndex
                    && respMsg.ExtendPayload()[0] == U4.Combine(header.FunctionId, header.SoftwareId);

                return isError || respHeader == header;
            }).ConfigureAwait(false);
        }
        catch (ChannelException e)
        {
            throw Hidpp20Exception.Channel(e);
        }

        var responseMsg = V20Message.FromHidpp(response);
        if (responseMsg.Header.FeatureIndex == 0xff)
        {
            var code = responseMsg.ExtendPayload()[1];
            if (!Enum.IsDefined(typeof(V20ErrorType), code))
                throw Hidpp20Exception.UnsupportedResponse();
            throw Hidpp20Exception.Feature((V20ErrorType)code);
        }

        return responseMsg;
    }

    /// <summary>
    /// Determine a device's protocol version by sending a HID++2.0 ping. A
    /// HID++1.0-only device answers with an "invalid sub-id" error, which pins
    /// it to v1.0. Returns <c>null</c> if no device answered for the index.
    /// Ported from Rust <c>protocol::determine_version</c>.
    /// </summary>
    public static async Task<ProtocolVersion?> DetermineVersionAsync(HidppChannel channel, byte deviceIndex)
    {
        var swId = channel.GetSwId();
        var msg = V20Message.Short(
            new V20MessageHeader(deviceIndex, 0x00, U4.FromLo(0x1), swId),
            [0x00, 0x00, 0x00]);
        var header = msg.Header;

        HidppMessage response;
        try
        {
            response = await channel.SendAsync(msg.ToHidpp(), raw =>
            {
                if (V20Message.FromHidpp(raw).Header == header)
                    return true;

                // HID++1.0 error messages are always short.
                var v10 = V10Message.FromHidpp(raw);
                if (v10.Kind == HidppReportKind.Short
                    && v10.Header.DeviceIndex == deviceIndex
                    && v10.Header.SubId == (byte)V10MessageType.Error)
                {
                    var payload = v10.ExtendPayload();
                    if (payload[0] == 0x00
                        && payload[1] == U4.Combine(header.FunctionId, swId))
                        return true;
                }
                return false;
            }).ConfigureAwait(false);
        }
        catch (ChannelException)
        {
            return null;
        }

        var v20 = V20Message.FromHidpp(response);
        if (v20.Header == header)
        {
            var payload = v20.ExtendPayload();
            return new ProtocolVersion.V20(payload[0], payload[1]);
        }

        var v10msg = V10Message.FromHidpp(response);
        if (v10msg.Kind != HidppReportKind.Short)
            return null;
        var p = v10msg.ExtendPayload();
        return p[2] == (byte)V10ErrorType.InvalidSubId ? ProtocolVersion.V10.Instance : null;
    }
}
