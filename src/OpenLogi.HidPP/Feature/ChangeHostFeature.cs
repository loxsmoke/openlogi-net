using OpenLogi.HidPP.Channel;
using OpenLogi.HidPP.Protocol;

namespace OpenLogi.HidPP.Feature;

/// <summary>Host-switching capabilities reported by ChangeHost (0x1814).</summary>
[Flags]
public enum ChangeHostCapabilities : byte
{
    None = 0,
    EnhancedHostSwitch = 1 << 0,
}

/// <summary>Host configuration returned by <see cref="ChangeHostFeature.GetHostInfoAsync"/>.</summary>
public readonly record struct ChangeHostInfo(byte HostCount, byte CurrentHost, ChangeHostCapabilities Capabilities);

/// <summary>
/// The `ChangeHost` / 0x1814 feature — select which host / RF channel a
/// multi-host device is connected to. Ported from Rust <c>feature::change_host</c>.
/// </summary>
public sealed class ChangeHostFeature(FeatureEndpoint endpoint) : ICreatableFeature<ChangeHostFeature>
{
    public static ushort Id => 0x1814;
    public static byte StartingVersion => 0;
    public static ChangeHostFeature Create(HidppChannel channel, byte deviceIndex, byte featureIndex) =>
        new(new FeatureEndpoint(channel, deviceIndex, featureIndex));

    /// <summary>The host count, current host, and host-switching flags.</summary>
    public async Task<ChangeHostInfo> GetHostInfoAsync()
    {
        var p = (await endpoint.CallAsync(0, [0, 0, 0]).ConfigureAwait(false)).ExtendPayload();
        return new ChangeHostInfo(p[0], p[1], (ChangeHostCapabilities)p[2]);
    }

    /// <summary>
    /// Select <paramref name="host"/> as the current host. Fire-and-forget: a
    /// successful switch usually resets the device, so no response is awaited.
    /// </summary>
    public Task SetCurrentHostAsync(byte host) => endpoint.NotifyAsync(1, [host, 0, 0]);

    /// <summary>The persistent per-host cookie bytes (one per host, undelimited).</summary>
    public async Task<byte[]> GetCookiesAsync(byte hostCount)
    {
        var payload = (await endpoint.CallAsync(2, [0, 0, 0]).ConfigureAwait(false)).ExtendPayload();
        if (hostCount > payload.Length)
            throw Hidpp20Exception.UnsupportedResponse();
        return payload[..hostCount];
    }

    /// <summary>Write the persistent cookie byte for <paramref name="host"/>.</summary>
    public async Task SetCookieAsync(byte host, byte cookie) =>
        await endpoint.CallAsync(3, [host, cookie, 0]).ConfigureAwait(false);
}
