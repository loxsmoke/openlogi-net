namespace OpenLogi.Core.DeviceInfo;

/// <summary>
/// Mirror of HID++ DeviceInformation's transport bitfield — one flag per
/// protocol the firmware exposes (independent, not a state machine).
/// </summary>
public sealed record DeviceTransports
{
    public bool Usb { get; init; }
    public bool Equad { get; init; }
    public bool Btle { get; init; }
    public bool Bluetooth { get; init; }
}
