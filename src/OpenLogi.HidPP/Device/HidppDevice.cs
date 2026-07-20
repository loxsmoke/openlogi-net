using OpenLogi.HidPP.Channel;
using OpenLogi.HidPP.Feature;
using OpenLogi.HidPP.Protocol;

namespace OpenLogi.HidPP.Device;

/// <summary>
/// A single HID++ peripheral on a <see cref="HidppChannel"/> (not a receiver).
/// Ported from Rust <c>device::Device</c>. The Rust type-erased feature registry
/// becomes a feature table (ID → index/version) populated by enumeration; typed
/// features are resolved on demand via <see cref="GetFeature{T}"/>.
/// </summary>
public sealed class HidppDevice
{
    /// <summary>An enumerated feature's table index plus its reported version/type.</summary>
    public readonly record struct FeatureEntry(byte Index, byte Version, FeatureType Type);

    private readonly HidppChannel _channel;
    private readonly Dictionary<ushort, FeatureEntry> _featureTable = [];

    public byte DeviceIndex { get; }
    public ProtocolVersion ProtocolVersion { get; }

    private HidppDevice(HidppChannel channel, byte deviceIndex, ProtocolVersion protocolVersion)
    {
        _channel = channel;
        DeviceIndex = deviceIndex;
        ProtocolVersion = protocolVersion;
    }

    /// <summary>
    /// Initialize a device, pinging it to determine the protocol version.
    /// Throws <see cref="DeviceException"/> if no device answers or it is v1.0-only.
    /// </summary>
    public static async Task<HidppDevice> NewAsync(HidppChannel channel, byte deviceIndex)
    {
        var version = await V20.DetermineVersionAsync(channel, deviceIndex).ConfigureAwait(false)
            ?? throw new DeviceException(DeviceErrorKind.DeviceNotFound, "there is no device with the specified device index");
        if (version is ProtocolVersion.V10)
            throw new DeviceException(DeviceErrorKind.UnsupportedProtocolVersion, "the device does not support HID++2.0 or newer");
        return new HidppDevice(channel, deviceIndex, version);
    }

    /// <summary>The root feature (always at index 0).</summary>
    public RootFeature Root => RootFeature.Create(_channel, DeviceIndex, 0);

    /// <summary>Whether the device reported support for feature <typeparamref name="T"/> during enumeration.</summary>
    public bool ProvidesFeature<T>() where T : class, ICreatableFeature<T> =>
        _featureTable.ContainsKey(T.Id);

    /// <summary>
    /// Resolve a typed feature, or <c>null</c> if the device did not report it
    /// during <see cref="EnumerateFeaturesAsync"/>.
    /// </summary>
    public T? GetFeature<T>() where T : class, ICreatableFeature<T> =>
        _featureTable.TryGetValue(T.Id, out var entry)
            ? T.Create(_channel, DeviceIndex, entry.Index)
            : null;

    /// <summary>
    /// Detect all features the device supports and record them in the feature
    /// table. Returns the feature list, or <c>null</c> if FeatureSet (required for
    /// enumeration) is unsupported. Ported from Rust <c>enumerate_features</c>.
    /// </summary>
    public async Task<IReadOnlyList<FeatureInformation>?> EnumerateFeaturesAsync()
    {
        var featureSetInfo = await Root.GetFeatureAsync(FeatureSetFeature.Id).ConfigureAwait(false);
        if (featureSetInfo is null) return null;

        var featureSet = FeatureSetFeature.Create(_channel, DeviceIndex, featureSetInfo.Value.Index);
        var count = await featureSet.CountAsync().ConfigureAwait(false);

        var features = new List<FeatureInformation>(count);
        for (byte i = 1; i <= count; i++)
        {
            var info = await featureSet.GetFeatureAsync(i).ConfigureAwait(false);
            features.Add(info);
            // Record the table index (the loop position) against the feature ID
            // so GetFeature<T>() can resolve it later.
            _featureTable[info.Id] = new FeatureEntry(i, info.Version, info.Type);
        }
        return features;
    }

    /// <summary>The recorded feature-table index for a feature ID, if enumerated.</summary>
    public byte? FeatureIndex(ushort id) => _featureTable.TryGetValue(id, out var e) ? e.Index : null;
}
