using System.Threading.Channels;
using OpenLogi.HidPP.Channel;

namespace OpenLogi.HidPP.Feature;

/// <summary>
/// The `WirelessDeviceStatus` / 0x1d4b feature: a single broadcast event the device
/// sends when its wireless link (re)establishes — the firmware's explicit "my
/// volatile settings may have been reset, reconfigure me" signal (Status=1
/// reconnection, Request=1 software reconfiguration needed). Used to re-assert
/// volatile divert state the instant a napping mouse wakes, instead of the user's
/// first gestures silently acting native until a periodic keepalive lands.
/// Dispose to remove the listener.
/// </summary>
public sealed class WirelessDeviceStatusFeature : ICreatableFeature<WirelessDeviceStatusFeature>, IDisposable
{
    public static ushort Id => 0x1d4b;
    public static byte StartingVersion => 0;

    /// <summary>A status broadcast (fields per the 0x1d4b spec; all zero when the payload is short).</summary>
    public readonly record struct StatusBroadcast(byte Status, byte Request, byte Reason);

    private readonly EventEmitter<StatusBroadcast> _emitter = new();
    private readonly IDisposable _listener;

    private WirelessDeviceStatusFeature(HidppChannel channel, byte deviceIndex, byte featureIndex)
    {
        _listener = channel.AddMsgListenerGuarded((raw, matched) =>
        {
            var ev = FeatureEndpoint.EventPayload(raw, matched, deviceIndex, featureIndex);
            if (ev is null || ev.Value.SubId.ToLo() != 0) return; // function 0 = statusBroadcast
            var p = ev.Value.Payload;
            _emitter.Emit(new StatusBroadcast(
                p.Length > 0 ? p[0] : (byte)0,
                p.Length > 1 ? p[1] : (byte)0,
                p.Length > 2 ? p[2] : (byte)0));
        });
    }

    public static WirelessDeviceStatusFeature Create(HidppChannel channel, byte deviceIndex, byte featureIndex) =>
        new(channel, deviceIndex, featureIndex);

    /// <summary>Subscribe to status broadcasts.</summary>
    public ChannelReader<StatusBroadcast> Listen() => _emitter.CreateReceiver();

    public void Dispose() => _listener.Dispose();
}
