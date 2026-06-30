using OpenLogi.HidPP.Channel;

namespace OpenLogi.HidPP.Feature;

/// <summary>
/// The `BatteryVoltage` / 0x1001 feature — reports the cell voltage (mV) plus a
/// charging-status bitfield. Used by Logitech G (gaming) devices that expose this
/// instead of UnifiedBattery (0x1004). The voltage is converted to an approximate
/// percentage with a single-cell li-ion discharge curve and normalized to the same
/// <see cref="HidppBatteryInfo"/> shape, so the rest of the app treats both battery
/// features identically.
///
/// HARDWARE-UNVERIFIED: the percentage curve and the status-bit decode are
/// best-effort (ported from Solaar's estimate) and should be sanity-checked against
/// G HUB on real hardware.
/// </summary>
public sealed class BatteryVoltageFeature(FeatureEndpoint endpoint) : ICreatableFeature<BatteryVoltageFeature>
{
    public static ushort Id => 0x1001;
    public static byte StartingVersion => 0;
    public static BatteryVoltageFeature Create(HidppChannel channel, byte deviceIndex, byte featureIndex) =>
        new(new FeatureEndpoint(channel, deviceIndex, featureIndex));

    /// <summary>
    /// Read the battery (function 0): cell voltage in millivolts (big-endian, bytes
    /// 0..1) and a status byte (byte 2). The percentage is estimated from the voltage
    /// curve and the level bucketed from it.
    /// </summary>
    public async Task<HidppBatteryInfo> GetBatteryInfoAsync()
    {
        var p = (await endpoint.CallAsync(0, [0, 0, 0]).ConfigureAwait(false)).ExtendPayload();
        var millivolts = (ushort)((p[0] << 8) | p[1]);
        var flags = p[2];

        var percent = VoltageToPercent(millivolts);
        var level = percent >= 80 ? HidppBatteryLevel.Full
            : percent >= 40 ? HidppBatteryLevel.Good
            : percent >= 15 ? HidppBatteryLevel.Low
            : HidppBatteryLevel.Critical;
        // bit 7 = charging; bit 6 = charge complete (per Solaar's status decode).
        var status = (flags & 0x80) != 0
            ? ((flags & 0x40) != 0 ? HidppBatteryStatus.Full : HidppBatteryStatus.Charging)
            : HidppBatteryStatus.Discharging;
        return new HidppBatteryInfo((byte)percent, level, status);
    }

    // Approximate single-cell li-ion discharge curve (mV → %), descending voltage.
    // Ported from Solaar's battery-voltage estimate; interpolated linearly between
    // adjacent points.
    private static readonly (ushort Mv, byte Pct)[] Curve =
    [
        (4186, 100), (4067, 90), (3989, 80), (3922, 70), (3859, 60), (3811, 50),
        (3753, 40), (3729, 30), (3677, 20), (3641, 10), (3550, 5), (3500, 2), (3300, 0),
    ];

    /// <summary>Estimate charge percent (0–100) from cell voltage, clamping past the curve ends.</summary>
    public static byte VoltageToPercent(ushort mv)
    {
        if (mv >= Curve[0].Mv) return 100;
        if (mv <= Curve[^1].Mv) return 0;
        for (var i = 1; i < Curve.Length; i++)
        {
            var (hiMv, hiPct) = Curve[i - 1];
            var (loMv, loPct) = Curve[i];
            if (mv <= hiMv && mv >= loMv)
            {
                var frac = (double)(mv - loMv) / (hiMv - loMv);
                return (byte)Math.Round(loPct + frac * (hiPct - loPct));
            }
        }
        return 0;
    }
}
