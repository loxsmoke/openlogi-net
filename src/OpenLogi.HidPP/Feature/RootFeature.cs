using OpenLogi.HidPP.Channel;

namespace OpenLogi.HidPP.Feature;

/// <summary>Information about a feature, as returned by <see cref="RootFeature.GetFeatureAsync"/>.</summary>
public readonly record struct RootFeatureInfo(byte Index, FeatureType Type, byte Version);

/// <summary>
/// The `Root` / 0x0000 feature every HID++2.0 device supports, always at feature
/// index 0. Ported from Rust <c>feature::root</c>.
/// </summary>
public sealed class RootFeature(FeatureEndpoint endpoint) : ICreatableFeature<RootFeature>
{
    public static ushort Id => 0x0000;
    public static byte StartingVersion => 0;
    public static RootFeature Create(HidppChannel channel, byte deviceIndex, byte featureIndex) =>
        new(new FeatureEndpoint(channel, deviceIndex, 0));

    /// <summary>Look up a feature ID; returns its table info, or <c>null</c> if unsupported.</summary>
    public async Task<RootFeatureInfo?> GetFeatureAsync(ushort id)
    {
        var payload = (await endpoint.CallAsync(0, [(byte)(id >> 8), (byte)id, 0x00]).ConfigureAwait(false)).ExtendPayload();
        if (payload[0] == 0) return null;
        return new RootFeatureInfo(payload[0], FeatureType.FromByte(payload[1]), payload[2]);
    }

    /// <summary>Ping the device; it echoes <paramref name="data"/> back on success.</summary>
    public async Task<byte> PingAsync(byte data)
    {
        var payload = (await endpoint.CallAsync(1, [0x00, 0x00, data]).ConfigureAwait(false)).ExtendPayload();
        return payload[2];
    }
}
