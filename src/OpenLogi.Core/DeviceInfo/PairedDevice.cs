namespace OpenLogi.Core.DeviceInfo;

public sealed record PairedDevice
{
    /// <summary>Receiver-assigned slot (1..=6 for Bolt).</summary>
    public required byte Slot { get; init; }
    public string? Codename { get; init; }
    /// <summary>Wireless product ID. <c>null</c> for offline / unreachable devices.</summary>
    public ushort? Wpid { get; init; }
    public required DeviceKind Kind { get; init; }
    public required bool Online { get; init; }
    public BatteryInfo? Battery { get; init; }
    public DeviceModelInfo? ModelInfo { get; init; }
    public Capabilities? Capabilities { get; init; }
}
