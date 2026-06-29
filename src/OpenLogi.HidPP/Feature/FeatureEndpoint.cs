using OpenLogi.HidPP.Channel;
using OpenLogi.HidPP.Protocol;

namespace OpenLogi.HidPP.Feature;

/// <summary>Marker for a concrete HID++2.0 feature implementation.</summary>
public interface IFeature { }

/// <summary>
/// A feature that can be instantiated automatically from its addressing tuple.
/// Uses static abstract members so <see cref="HidppDevice.GetFeature{T}"/> can
/// resolve a feature by ID without an instance. Ported from Rust
/// <c>CreatableFeature</c>.
/// </summary>
public interface ICreatableFeature<out TSelf> : IFeature where TSelf : ICreatableFeature<TSelf>
{
    /// <summary>The protocol ID of the implemented feature.</summary>
    static abstract ushort Id { get; }

    /// <summary>The feature version the implementation starts to support.</summary>
    static abstract byte StartingVersion { get; }

    /// <summary>Create an instance bound to a feature index on a device.</summary>
    static abstract TSelf Create(HidppChannel channel, byte deviceIndex, byte featureIndex);
}

/// <summary>
/// A feature's addressable <c>(device, feature)</c> endpoint on a channel,
/// centralising HID++2.0 request framing. Ported from Rust <c>FeatureEndpoint</c>.
/// </summary>
public sealed class FeatureEndpoint(HidppChannel channel, byte deviceIndex, byte featureIndex)
{
    public HidppChannel Channel { get; } = channel;
    public byte DeviceIndex { get; } = deviceIndex;
    public byte FeatureIndex { get; } = featureIndex;

    private V20MessageHeader Header(byte function)
    {
        if (function >= 16)
            throw new ArgumentOutOfRangeException(nameof(function), $"HID++2.0 function id {function} exceeds 4 bits");
        return new V20MessageHeader(DeviceIndex, FeatureIndex, U4.FromLo(function), Channel.GetSwId());
    }

    /// <summary>Call <paramref name="function"/> with a 3-byte short payload and await the response.</summary>
    public Task<V20Message> CallAsync(byte function, ReadOnlySpan<byte> args) =>
        Channel.SendV20Async(V20Message.Short(Header(function), args));

    /// <summary>Call <paramref name="function"/> with a 16-byte long payload and await the response.</summary>
    public Task<V20Message> CallLongAsync(byte function, ReadOnlySpan<byte> args) =>
        Channel.SendV20Async(V20Message.Long(Header(function), args));

    /// <summary>Send <paramref name="function"/> with a 3-byte payload without waiting (e.g. a host switch that resets the device).</summary>
    public async Task NotifyAsync(byte function, byte[] args)
    {
        try
        {
            await Channel.SendAndForgetAsync(V20Message.Short(Header(function), args).ToHidpp()).ConfigureAwait(false);
        }
        catch (ChannelException e)
        {
            throw Hidpp20Exception.Channel(e);
        }
    }

    /// <summary>Send <paramref name="function"/> with a 16-byte long payload without waiting (for high-rate streaming, e.g. lighting frames).</summary>
    public async Task NotifyLongAsync(byte function, byte[] args)
    {
        try
        {
            await Channel.SendAndForgetAsync(V20Message.Long(Header(function), args).ToHidpp()).ConfigureAwait(false);
        }
        catch (ChannelException e)
        {
            throw Hidpp20Exception.Channel(e);
        }
    }

    /// <summary>
    /// Shared prelude for a feature event listener: drops request-matched reports,
    /// keeps only unsolicited broadcasts to this <c>(device, feature)</c> with a
    /// zero software id, and returns the event's sub-id + extended payload.
    /// Ported from Rust <c>feature::event_payload</c>.
    /// </summary>
    public static (U4 SubId, byte[] Payload)? EventPayload(HidppMessage raw, bool matched, byte deviceIndex, byte featureIndex)
    {
        if (matched) return null;
        var msg = V20Message.FromHidpp(raw);
        var header = msg.Header;
        if (header.DeviceIndex != deviceIndex || header.FeatureIndex != featureIndex || header.SoftwareId.ToLo() != 0)
            return null;
        return (header.FunctionId, msg.ExtendPayload());
    }
}

/// <summary>A bitfield describing properties of a feature. Ported from Rust <c>FeatureType</c>.</summary>
public readonly record struct FeatureType(
    bool Obsolete, bool Hidden, bool Engineering, bool ManufacturingDeactivatable, bool ComplianceDeactivatable)
{
    public static FeatureType FromByte(byte value) => new(
        Obsolete: (value & (1 << 7)) != 0,
        Hidden: (value & (1 << 6)) != 0,
        Engineering: (value & (1 << 5)) != 0,
        ManufacturingDeactivatable: (value & (1 << 4)) != 0,
        ComplianceDeactivatable: (value & (1 << 3)) != 0);

    public byte ToByte()
    {
        byte raw = 0;
        if (Obsolete) raw |= 1 << 7;
        if (Hidden) raw |= 1 << 6;
        if (Engineering) raw |= 1 << 5;
        if (ManufacturingDeactivatable) raw |= 1 << 4;
        if (ComplianceDeactivatable) raw |= 1 << 3;
        return raw;
    }
}
