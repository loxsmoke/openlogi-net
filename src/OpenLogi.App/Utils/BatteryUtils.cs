using Avalonia.Media;

namespace OpenLogi.App.Utils;

internal class BatteryUtils
{
    /// <summary>At or below this percent the fill turns red (Logitech's low-battery LED point).</summary>
    public const int LowThreshold = 15;
    /// <summary>
    /// The one low-battery rule, shared by this gauge and the tray battery icons so the
    /// two always turn red at the same reading.
    /// </summary>
    public static bool IsLow(int percentage) => percentage <= LowThreshold;

    public static readonly Color CoolGreen = Color.Parse("#3FB950");
    public static readonly Color CriticalRed = Color.Parse("#DA3633");
    public static readonly Color ChargingOrange = Color.Parse("#FF9A1F");

    /// <summary>
    /// Level colour.
    /// Orange if charging and <paramref name="highlightCharging"/> is true.
    /// Green above 15% or when charging.
    /// Red at or below 15% and not charging. 
    /// </summary>
    public static Color ColorFor(int percentage, bool charging, bool highlightCharging = false) =>
        ColorFor(IsLow(percentage), charging, highlightCharging);

    public static Color ColorFor(bool lowBattery, bool charging, bool highlightCharging) =>
        charging ?
            (highlightCharging ? ChargingOrange : CoolGreen) :
            (lowBattery ? CriticalRed : CoolGreen);
}
