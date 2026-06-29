using OpenLogi.HidPP.Channel;

namespace OpenLogi.HidPP.Feature;

/// <summary>Information about a feature, as returned by <see cref="FeatureSetFeature.GetFeatureAsync"/>.</summary>
public readonly record struct FeatureInformation(ushort Id, FeatureType Type, byte Version);

/// <summary>
/// The `FeatureSet` / 0x0001 feature: enumerate all features a device supports.
/// Ported from Rust <c>feature::feature_set</c>.
/// </summary>
public sealed class FeatureSetFeature(FeatureEndpoint endpoint) : ICreatableFeature<FeatureSetFeature>
{
    public static ushort Id => 0x0001;
    public static byte StartingVersion => 0;
    public static FeatureSetFeature Create(HidppChannel channel, byte deviceIndex, byte featureIndex) =>
        new(new FeatureEndpoint(channel, deviceIndex, featureIndex));

    /// <summary>The number of features the device supports, excluding the root feature.</summary>
    public async Task<byte> CountAsync() =>
        (await endpoint.CallAsync(0, [0, 0, 0]).ConfigureAwait(false)).ExtendPayload()[0];

    /// <summary>Information about the feature at <paramref name="index"/> in the table (index 0 = root is not allowed).</summary>
    public async Task<FeatureInformation> GetFeatureAsync(byte index)
    {
        var payload = (await endpoint.CallAsync(1, [index, 0x00, 0x00]).ConfigureAwait(false)).ExtendPayload();
        return new FeatureInformation(
            (ushort)((payload[0] << 8) | payload[1]),
            FeatureType.FromByte(payload[2]),
            payload[3]);
    }
}
