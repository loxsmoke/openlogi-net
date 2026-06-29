using OpenLogi.HidPP.Channel;

namespace OpenLogi.HidPP.Protocol;

/// <summary>The header every HID++1.0 message starts with.</summary>
public readonly record struct V10MessageHeader(byte DeviceIndex, byte SubId);

/// <summary>A HID++1.0 message: short (4-byte payload) or long (17-byte payload).</summary>
public sealed class V10Message
{
    public const int ShortPayloadLength = HidppMessage.ShortReportLength - 3; // 4
    public const int LongPayloadLength = HidppMessage.LongReportLength - 3;   // 17

    public HidppReportKind Kind { get; }
    public V10MessageHeader Header { get; }
    private readonly byte[] _payload;

    private V10Message(HidppReportKind kind, V10MessageHeader header, byte[] payload)
    {
        Kind = kind;
        Header = header;
        _payload = payload;
    }

    public static V10Message Short(V10MessageHeader header, ReadOnlySpan<byte> payload)
    {
        if (payload.Length != ShortPayloadLength)
            throw new ArgumentException($"short v10 payload must be {ShortPayloadLength} bytes", nameof(payload));
        return new V10Message(HidppReportKind.Short, header, payload.ToArray());
    }

    public static V10Message Long(V10MessageHeader header, ReadOnlySpan<byte> payload)
    {
        if (payload.Length != LongPayloadLength)
            throw new ArgumentException($"long v10 payload must be {LongPayloadLength} bytes", nameof(payload));
        return new V10Message(HidppReportKind.Long, header, payload.ToArray());
    }

    /// <summary>The payload extended to the longest possible width (17 bytes), zero-padded.</summary>
    public byte[] ExtendPayload()
    {
        var data = new byte[LongPayloadLength];
        _payload.CopyTo(data.AsSpan());
        return data;
    }

    public static V10Message FromHidpp(HidppMessage msg)
    {
        var p = msg.Payload;
        var header = new V10MessageHeader(p[0], p[1]);
        return msg.Kind == HidppReportKind.Short ? Short(header, p[2..]) : Long(header, p[2..]);
    }

    public HidppMessage ToHidpp()
    {
        if (Kind == HidppReportKind.Short)
        {
            var data = new byte[HidppMessage.ShortPayloadLength];
            data[0] = Header.DeviceIndex;
            data[1] = Header.SubId;
            _payload.CopyTo(data.AsSpan(2));
            return HidppMessage.Short(data);
        }
        else
        {
            var data = new byte[HidppMessage.LongPayloadLength];
            data[0] = Header.DeviceIndex;
            data[1] = Header.SubId;
            _payload.CopyTo(data.AsSpan(2));
            return HidppMessage.Long(data);
        }
    }
}

/// <summary>Globally defined HID++1.0 message sub-ids.</summary>
public enum V10MessageType : byte
{
    SetRegister = 0x80,
    GetRegister = 0x81,
    SetLongRegister = 0x82,
    GetLongRegister = 0x83,
    Error = 0x8f,
}

/// <summary>HID++1.0 error codes returned in an <see cref="V10MessageType.Error"/> message.</summary>
public enum V10ErrorType : byte
{
    Success = 0x00,
    InvalidSubId = 0x01,
    InvalidAddress = 0x02,
    InvalidValue = 0x03,
    ConnectFail = 0x04,
    TooManyDevices = 0x05,
    AlreadyExists = 0x06,
    Busy = 0x07,
    UnknownDevice = 0x08,
    ResourceError = 0x09,
    RequestUnavailable = 0x0a,
    InvalidParamValue = 0x0b,
    WrongPinCode = 0x0c,
}

public enum Hidpp10ErrorKind { Channel, RegisterAccess, UnsupportedResponse }

/// <summary>An error accessing HID++1.0 registers. Ported from Rust <c>protocol::v10::Hidpp10Error</c>.</summary>
public sealed class Hidpp10Exception : Exception
{
    public Hidpp10ErrorKind Kind { get; }
    public V10ErrorType? RegisterError { get; }

    private Hidpp10Exception(Hidpp10ErrorKind kind, string message, V10ErrorType? registerError = null, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        RegisterError = registerError;
    }

    public static Hidpp10Exception RegisterAccess(V10ErrorType error) =>
        new(Hidpp10ErrorKind.RegisterAccess, $"a HID++1.0 register access failed: {error}", error);

    public static Hidpp10Exception UnsupportedResponse() =>
        new(Hidpp10ErrorKind.UnsupportedResponse, "the received response from the device is (partly) unsupported");

    public static Hidpp10Exception Channel(ChannelException inner) =>
        new(Hidpp10ErrorKind.Channel, "the HID++ channel returned an error", inner: inner);
}

/// <summary>HID++1.0 register-access protocol (RAP) over a <see cref="HidppChannel"/>.</summary>
public static class V10
{
    private static bool IsRapResponse(byte device, V10MessageType type, byte address, HidppMessage msg)
    {
        var raw = msg.Payload;
        return raw[0] == device
            && ((raw[1] == (byte)type && raw[2] == address)
                || (raw[1] == (byte)V10MessageType.Error && raw[2] == (byte)type && raw[3] == address));
    }

    private static async Task<V10Message> SendRapAsync(HidppChannel channel, V10Message request, byte device, V10MessageType type, byte address)
    {
        try
        {
            var raw = await channel.SendAsync(request.ToHidpp(),
                m => IsRapResponse(device, type, address, m)).ConfigureAwait(false);
            return V10Message.FromHidpp(raw);
        }
        catch (ChannelException e)
        {
            throw Hidpp10Exception.Channel(e);
        }
    }

    private static void ThrowIfError(V10Message response)
    {
        if (response.Header.SubId != (byte)V10MessageType.Error) return;
        var code = response.ExtendPayload()[2];
        if (!Enum.IsDefined(typeof(V10ErrorType), code))
            throw Hidpp10Exception.UnsupportedResponse();
        throw Hidpp10Exception.RegisterAccess((V10ErrorType)code);
    }

    /// <summary>Read a short 3-byte register.</summary>
    public static async Task<byte[]> ReadRegisterAsync(this HidppChannel channel, byte device, byte address, byte[] parameters)
    {
        byte[] data = [address, parameters[0], parameters[1], parameters[2]];
        var response = await SendRapAsync(channel,
            V10Message.Short(new V10MessageHeader(device, (byte)V10MessageType.GetRegister), data),
            device, V10MessageType.GetRegister, address).ConfigureAwait(false);
        ThrowIfError(response);
        return response.ExtendPayload()[1..4];
    }

    /// <summary>Write a short 3-byte register.</summary>
    public static async Task WriteRegisterAsync(this HidppChannel channel, byte device, byte address, byte[] payload)
    {
        byte[] data = [address, payload[0], payload[1], payload[2]];
        var response = await SendRapAsync(channel,
            V10Message.Short(new V10MessageHeader(device, (byte)V10MessageType.SetRegister), data),
            device, V10MessageType.SetRegister, address).ConfigureAwait(false);
        ThrowIfError(response);
    }

    /// <summary>Read a long 16-byte register.</summary>
    public static async Task<byte[]> ReadLongRegisterAsync(this HidppChannel channel, byte device, byte address, byte[] parameters)
    {
        byte[] data = [address, parameters[0], parameters[1], parameters[2]];
        var response = await SendRapAsync(channel,
            V10Message.Short(new V10MessageHeader(device, (byte)V10MessageType.GetLongRegister), data),
            device, V10MessageType.GetLongRegister, address).ConfigureAwait(false);
        ThrowIfError(response);
        return response.ExtendPayload()[1..17];
    }

    /// <summary>Write a long 16-byte register.</summary>
    public static async Task WriteLongRegisterAsync(this HidppChannel channel, byte device, byte address, byte[] payload)
    {
        var data = new byte[17];
        data[0] = address;
        payload.CopyTo(data.AsSpan(1));
        var response = await SendRapAsync(channel,
            V10Message.Long(new V10MessageHeader(device, (byte)V10MessageType.SetLongRegister), data),
            device, V10MessageType.SetLongRegister, address).ConfigureAwait(false);
        ThrowIfError(response);
    }
}
