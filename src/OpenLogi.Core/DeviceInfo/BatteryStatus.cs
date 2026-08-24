namespace OpenLogi.Core.DeviceInfo;

/// <summary>Charging state.</summary>
public enum BatteryStatus { Discharging, Charging, ChargingSlow, Full, Error, Unknown }

public static class BatteryStatusExtensions
{
    /// <summary>Localized display label for battery status text in the app UI.</summary>
    public static string Label(this BatteryStatus status) => status switch
    {
        BatteryStatus.Discharging => Localization.Loc.Current["BatteryStatus_Discharging"],
        BatteryStatus.Charging => Localization.Loc.Current["BatteryStatus_Charging"],
        BatteryStatus.ChargingSlow => Localization.Loc.Current["BatteryStatus_ChargingSlow"],
        BatteryStatus.Full => Localization.Loc.Current["BatteryStatus_Full"],
        BatteryStatus.Error => Localization.Loc.Current["BatteryStatus_Error"],
        BatteryStatus.Unknown => Localization.Loc.Current["BatteryStatus_Unknown"],
        _ => status.ToString(),
    };
}
