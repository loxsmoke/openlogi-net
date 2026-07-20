namespace OpenLogi.HidPP.Device;

/// <summary>Discriminator for <see cref="DeviceException"/>.</summary>
public enum DeviceErrorKind
{
    /// <summary>No device with the specified index is connected.</summary>
    DeviceNotFound,
    /// <summary>The device only supports HID++1.0.</summary>
    UnsupportedProtocolVersion,
}
