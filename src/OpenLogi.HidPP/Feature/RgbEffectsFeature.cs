using OpenLogi.HidPP.Channel;

namespace OpenLogi.HidPP.Feature;

/// <summary>
/// The `RgbEffects` / 0x8071 per-cluster RGB effect engine (newer G-series
/// keyboards, e.g. the G915). Ported from Rust <c>feature::rgb_effects</c> — the
/// subset needed for a solid colour: take software control, then apply a fixed
/// effect. The event listener and read functions are not ported.
/// </summary>
public sealed class RgbEffectsFeature(FeatureEndpoint endpoint) : ICreatableFeature<RgbEffectsFeature>
{
    public static ushort Id => 0x8071;
    public static byte StartingVersion => 0;
    public static RgbEffectsFeature Create(HidppChannel channel, byte deviceIndex, byte featureIndex) =>
        new(new FeatureEndpoint(channel, deviceIndex, featureIndex));

    public const int ClusterEffectParamCount = 10;
    public const byte AllClusters = 0xff;
    /// <summary>The fixed/static-colour effect index (params[0..3] = R,G,B).</summary>
    public const byte EffectFixed = 1;
    /// <summary>SwControlFlags::ALL_CLUSTERS — required before setRgbClusterEffect.</summary>
    public const byte SwControlAllClusters = 1 << 0;
    /// <summary>RgbPersistence::VOLATILE.</summary>
    public const byte PersistenceVolatile = 1 << 0;
    /// <summary>PowerModeTarget::FullPower.</summary>
    public const byte PowerFull = 0;

    private const byte GetOrSetSet = 1;
    private const byte PowerTargetShift = 2;
    private const byte TypeGeneralInfo = 0x00;

    private static ushort Be16(byte[] p, int i) => (ushort)((p[i] << 8) | p[i + 1]);

    /// <summary>Number of effects supported by <paramref name="cluster"/> (RgbClusterInfo.effects_number).</summary>
    public async Task<byte> GetEffectCountAsync(byte cluster)
    {
        var p = (await endpoint.CallAsync(0, [cluster, 0xff, TypeGeneralInfo]).ConfigureAwait(false)).ExtendPayload();
        return p[4];
    }

    /// <summary>The effect-type id (raw effectID) of <paramref name="effectIndex"/> on <paramref name="cluster"/>.</summary>
    public async Task<ushort> GetEffectIdAsync(byte cluster, byte effectIndex)
    {
        var p = (await endpoint.CallAsync(0, [cluster, effectIndex, TypeGeneralInfo]).ConfigureAwait(false)).ExtendPayload();
        return Be16(p, 2);
    }

    /// <summary>Take or release software control of the RGB clusters / power modes.</summary>
    public async Task SetSwControlAsync(byte control, byte events = 0) =>
        await endpoint.CallAsync(5, [GetOrSetSet, control, events]).ConfigureAwait(false);

    /// <summary>Apply <paramref name="effectIndex"/> to <paramref name="cluster"/> with effect-specific params.</summary>
    public async Task SetRgbClusterEffectAsync(byte cluster, byte effectIndex, byte[] paramsBytes, byte persistence, byte powerMode)
    {
        var args = new byte[16];
        args[0] = cluster;
        args[1] = effectIndex;
        paramsBytes.AsSpan(0, Math.Min(paramsBytes.Length, ClusterEffectParamCount)).CopyTo(args.AsSpan(2));
        args[12] = (byte)(persistence | (powerMode << PowerTargetShift));
        await endpoint.CallLongAsync(1, args).ConfigureAwait(false);
    }
}
