using HidSharp;

namespace OpenLogi.Hid;

/// <summary>
/// Discovers Logitech HID++ interfaces on the local machine. Replaces the Rust
/// <c>openlogi-hid::transport::enumerate_hidpp_devices</c> (async-hid) with a
/// HidSharp enumeration filtered to nodes that advertise a HID++ report.
/// </summary>
public static class HidDiscovery
{
    /// <summary>Logitech USB vendor ID.</summary>
    public const ushort LogitechVendorId = 0x046d;

    /// <summary>All Logitech HID devices that advertise a HID++ short or long report.</summary>
    public static IReadOnlyList<HidDevice> EnumerateHidppDevices()
    {
        var result = new List<HidDevice>();
        foreach (var device in DeviceList.Local.GetHidDevices(LogitechVendorId))
        {
            if (WindowsRawHidChannel.DetectSupport(device) is not null)
                result.Add(device);
        }
        return result;
    }
}
