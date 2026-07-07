namespace OpenLogi.Core.DeviceInfo;

public sealed record BatteryInfo
{
    public required byte Percentage { get; init; }
    public required BatteryLevel Level { get; init; }
    public required BatteryStatus Status { get; init; }
}
