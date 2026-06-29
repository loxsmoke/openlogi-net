using System.Threading.Channels;
using OpenLogi.HidPP.Channel;
using OpenLogi.HidPP.Protocol;

namespace OpenLogi.HidPP.Feature;

/// <summary>Approximate battery level (a bit-flag value). Ported from Rust <c>BatteryLevel</c>.</summary>
public enum HidppBatteryLevel : byte { Critical = 1, Low = 1 << 1, Good = 1 << 2, Full = 1 << 3 }

/// <summary>Battery charging status. Ported from Rust <c>BatteryStatus</c>.</summary>
public enum HidppBatteryStatus : byte { Discharging = 0, Charging = 1, ChargingSlow = 2, Full = 3, Error = 4 }

/// <summary>Current battery charge info. Ported from Rust <c>unified_battery::BatteryInfo</c>.</summary>
public readonly record struct HidppBatteryInfo(byte ChargingPercentage, HidppBatteryLevel Level, HidppBatteryStatus Status);

/// <summary>Capabilities of the UnifiedBattery feature. Ported from Rust <c>BatteryCapabilities</c>.</summary>
public sealed record BatteryCapabilities
{
    public required IReadOnlySet<HidppBatteryLevel> ReportedLevels { get; init; }
    public required bool Rechargeable { get; init; }
    public required bool Percentage { get; init; }

    public static BatteryCapabilities FromBytes(byte flags, byte caps)
    {
        var levels = new HashSet<HidppBatteryLevel>();
        if ((flags & 1) != 0) levels.Add(HidppBatteryLevel.Critical);
        if ((flags & (1 << 1)) != 0) levels.Add(HidppBatteryLevel.Low);
        if ((flags & (1 << 2)) != 0) levels.Add(HidppBatteryLevel.Good);
        if ((flags & (1 << 3)) != 0) levels.Add(HidppBatteryLevel.Full);
        return new BatteryCapabilities
        {
            ReportedLevels = levels,
            Rechargeable = (caps & 1) != 0,
            Percentage = (caps & (1 << 1)) != 0,
        };
    }
}

/// <summary>An event emitted by <see cref="UnifiedBatteryFeature"/>.</summary>
public readonly record struct BatteryEvent(HidppBatteryInfo Info);

/// <summary>
/// The `UnifiedBattery` / 0x1004 feature. Ported from Rust <c>feature::unified_battery</c>.
/// Dispose to remove the broadcast listener it registers.
/// </summary>
public sealed class UnifiedBatteryFeature : ICreatableFeature<UnifiedBatteryFeature>, IDisposable
{
    public static ushort Id => 0x1004;
    public static byte StartingVersion => 0;

    private readonly FeatureEndpoint _endpoint;
    private readonly EventEmitter<BatteryEvent> _emitter = new();
    private readonly IDisposable _listener;

    private UnifiedBatteryFeature(HidppChannel channel, byte deviceIndex, byte featureIndex)
    {
        _endpoint = new FeatureEndpoint(channel, deviceIndex, featureIndex);
        _listener = channel.AddMsgListenerGuarded((raw, matched) =>
        {
            var ev = FeatureEndpoint.EventPayload(raw, matched, deviceIndex, featureIndex);
            if (ev is null || ev.Value.SubId.ToLo() != 0) return; // battery broadcast is sub-id 0
            var payload = ev.Value.Payload;
            if (TryLevel(payload[1]) is not { } level || TryStatus(payload[2]) is not { } status) return;
            _emitter.Emit(new BatteryEvent(new HidppBatteryInfo(payload[0], level, status)));
        });
    }

    public static UnifiedBatteryFeature Create(HidppChannel channel, byte deviceIndex, byte featureIndex) =>
        new(channel, deviceIndex, featureIndex);

    /// <summary>Subscribe to battery-info update events.</summary>
    public ChannelReader<BatteryEvent> Listen() => _emitter.CreateReceiver();

    /// <summary>Retrieve the feature/battery capabilities.</summary>
    public async Task<BatteryCapabilities> GetBatteryCapabilitiesAsync()
    {
        var payload = (await _endpoint.CallAsync(0, [0, 0, 0]).ConfigureAwait(false)).ExtendPayload();
        return BatteryCapabilities.FromBytes(payload[0], payload[1]);
    }

    /// <summary>Retrieve the current battery status.</summary>
    public async Task<HidppBatteryInfo> GetBatteryInfoAsync()
    {
        var payload = (await _endpoint.CallAsync(1, [0, 0, 0]).ConfigureAwait(false)).ExtendPayload();
        var level = TryLevel(payload[1]) ?? throw Hidpp20Exception.UnsupportedResponse();
        var status = TryStatus(payload[2]) ?? throw Hidpp20Exception.UnsupportedResponse();
        return new HidppBatteryInfo(payload[0], level, status);
    }

    public void Dispose() => _listener.Dispose();

    /// <summary>Parse a battery-level flag byte, or <c>null</c> for an undefined value.</summary>
    public static HidppBatteryLevel? TryLevel(byte raw) =>
        Enum.IsDefined(typeof(HidppBatteryLevel), raw) ? (HidppBatteryLevel)raw : null;

    /// <summary>Parse a battery-status byte, or <c>null</c> for an undefined value.</summary>
    public static HidppBatteryStatus? TryStatus(byte raw) =>
        Enum.IsDefined(typeof(HidppBatteryStatus), raw) ? (HidppBatteryStatus)raw : null;
}
