using OpenLogi.HidPP.Channel;
using OpenLogi.HidPP.Feature;

namespace OpenLogi.HidPP.Protocol;

/// <summary>
/// Battery readout for HID++ 1.0 devices, which have no UnifiedBattery feature.
/// Two register layouts exist (register maps follow Solaar's <c>hidpp10</c>):
/// <list type="bullet">
/// <item>0x07 "battery status": a coarse 4-step level plus a charging byte — the
/// Marathon M705 (M-R0009), M510, Performance MX and the K800 use this.</item>
/// <item>0x0D "battery charge": a real percentage plus a status nibble — Anywhere
/// MX, M505, M350.</item>
/// </list>
/// A device implements one of the two and answers the other with an
/// invalid-address error, so the read tries 0x07 first and falls back to 0x0D.
/// </summary>
public static class V10Battery
{
    public const byte RegBatteryStatus = 0x07;
    public const byte RegBatteryCharge = 0x0d;

    /// <summary>
    /// Read the battery of the HID++ 1.0 device at <paramref name="deviceIndex"/>.
    /// <c>null</c> when the device implements neither register or the reply is
    /// unparseable. A channel error (timeout) propagates as <see cref="Hidpp10Exception"/>.
    /// </summary>
    public static async Task<HidppBatteryInfo?> ReadAsync(HidppChannel channel, byte deviceIndex)
    {
        try
        {
            var r = await channel.ReadRegisterAsync(deviceIndex, RegBatteryStatus, [0, 0, 0]).ConfigureAwait(false);
            if (ParseBatteryStatus(r) is { } status) return status;
        }
        catch (Hidpp10Exception e) when (e.Kind == Hidpp10ErrorKind.RegisterAccess)
        {
            // Not this layout — try the percentage register.
        }
        try
        {
            var r = await channel.ReadRegisterAsync(deviceIndex, RegBatteryCharge, [0, 0, 0]).ConfigureAwait(false);
            return ParseBatteryCharge(r);
        }
        catch (Hidpp10Exception e) when (e.Kind == Hidpp10ErrorKind.RegisterAccess)
        {
            return null;
        }
    }

    /// <summary>
    /// Parse a 0x07 reply: [0] level (7 full, 5 good, 3 low, 1 critical), [1] charging
    /// (0x00 discharging; bit pattern 0x21 recharging, 0x22 charge complete). The
    /// level is mapped to a nominal percentage the way Solaar does, since the
    /// device reports nothing finer.
    /// </summary>
    public static HidppBatteryInfo? ParseBatteryStatus(ReadOnlySpan<byte> reply)
    {
        if (reply.Length < 2) return null;
        var (level, percent) = reply[0] switch
        {
            7 => (HidppBatteryLevel.Full, (byte)90),
            5 => (HidppBatteryLevel.Good, (byte)50),
            3 => (HidppBatteryLevel.Low, (byte)20),
            1 => (HidppBatteryLevel.Critical, (byte)5),
            _ => ((HidppBatteryLevel)0, (byte)0),
        };
        if (level == 0) return null;
        var charging = reply[1];
        var status = charging == 0x00 ? HidppBatteryStatus.Discharging
            : (charging & 0x22) == 0x22 ? HidppBatteryStatus.Full
            : (charging & 0x21) == 0x21 ? HidppBatteryStatus.Charging
            : HidppBatteryStatus.Discharging;
        return new HidppBatteryInfo(percent, level, status);
    }

    /// <summary>
    /// Parse a 0x0D reply: [0] percentage, [2] status in the high nibble
    /// (0x30 discharging, 0x50 recharging, 0x90 charge complete).
    /// </summary>
    public static HidppBatteryInfo? ParseBatteryCharge(ReadOnlySpan<byte> reply)
    {
        if (reply.Length < 3) return null;
        var percent = reply[0];
        if (percent > 100) return null;
        var status = (reply[2] & 0xf0) switch
        {
            0x50 => HidppBatteryStatus.Charging,
            0x90 => HidppBatteryStatus.Full,
            _ => HidppBatteryStatus.Discharging,
        };
        var level = percent switch
        {
            >= 90 => HidppBatteryLevel.Full,
            >= 30 => HidppBatteryLevel.Good,
            >= 10 => HidppBatteryLevel.Low,
            _ => HidppBatteryLevel.Critical,
        };
        return new HidppBatteryInfo(percent, level, status);
    }
}
