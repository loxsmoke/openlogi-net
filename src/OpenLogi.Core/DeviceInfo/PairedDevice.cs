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
    /// <summary>
    /// The device speaks only HID++ 1.0 (register-based; Unifying-era models such as
    /// the Marathon M705 M-R0009). Identified by wpid via <see cref="LegacyDeviceCatalog"/>,
    /// battery read from a register, no configurable features — the UI shows it
    /// read-only and never opens a HID++ 2.0 session to it.
    /// </summary>
    public bool IsHidpp10 { get; init; }

    /// <summary>
    /// The device answered but a read the sweep expected to work failed — the feature
    /// table, its name, or its battery. A BLE mouse probed within seconds of being
    /// switched on does this: it answers the ping, then times out on the rest, and is
    /// listed unnamed with no battery or capabilities. The app re-scans while any
    /// listed device carries this flag, so the tile (and its tray icon) fill in once
    /// the device is ready, instead of staying blank until the next topology change.
    /// </summary>
    public bool ProbeIncomplete { get; init; }
}
