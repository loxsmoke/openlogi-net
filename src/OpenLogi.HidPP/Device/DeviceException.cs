namespace OpenLogi.HidPP.Device;

/// <summary>A device-specific error. Ported from Rust <c>device::DeviceError</c>.</summary>
public sealed class DeviceException(DeviceErrorKind kind, string message) : Exception(message)
{
    public DeviceErrorKind Kind { get; } = kind;
}
