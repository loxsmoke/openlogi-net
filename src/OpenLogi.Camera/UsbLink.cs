namespace OpenLogi.Camera;

/// <summary>A USB signalling speed, slowest first.</summary>
public enum UsbSpeed
{
    Low,
    Full,
    High,
    Super,
    SuperPlus,
}

public static class UsbSpeedExtensions
{
    /// <summary>Technical label shown in the link notice (a measurement, not prose).</summary>
    public static string Label(this UsbSpeed speed) => speed switch
    {
        UsbSpeed.Low => "USB 1.0 (1.5 Mbps)",
        UsbSpeed.Full => "USB 1.1 (12 Mbps)",
        UsbSpeed.High => "USB 2.0 (480 Mbps)",
        UsbSpeed.Super => "USB 3.0 (5 Gbps)",
        _ => "USB 3.1 (10 Gbps)",
    };
}

/// <summary>
/// How a camera is attached: the speed its link runs at, and the highest one the
/// camera says it supports. A UVC camera lists only the modes its link has the
/// bandwidth for, so a USB 3 camera on a USB 2 link offers less than it can do.
/// Ported from Rust <c>openlogi_camera::usb_link</c>.
/// </summary>
/// <param name="Capable">The highest speed the camera declares, or null when it declares none beyond high speed.</param>
public readonly record struct UsbLink(UsbSpeed Speed, UsbSpeed? Capable)
{
    /// <summary>The faster speed the camera supports when the link runs slower than that.</summary>
    public UsbSpeed? FasterAvailable => Capable is { } c && c > Speed ? c : null;
}

/// <summary>
/// The pure halves of reading a camera's link on Windows: decoding the hub's
/// answers and walking from a device-interface path to the USB device. Ported from
/// Rust <c>usb_windows.rs</c>; the Configuration Manager calls live in
/// <see cref="Interop.UsbHub"/>.
/// </summary>
internal static class UsbLinks
{
    // USB_DEVICE_SPEED values a hub reports for a connection.
    private const byte SpeedLow = 0, SpeedFull = 1, SpeedHigh = 2, SpeedSuper = 3;

    // USB_NODE_CONNECTION_INFORMATION_EX_V2_FLAGS.
    private const uint OperatingAtSuperSpeed = 1 << 0;
    private const uint CapableOfSuperSpeed = 1 << 1;
    private const uint OperatingAtSuperSpeedPlus = 1 << 2;
    private const uint CapableOfSuperSpeedPlus = 1 << 3;

    /// <summary>
    /// The link a hub's two answers describe. The first answer reports high speed
    /// for a SuperSpeed device too, so the V2 flags are read first. Without them the
    /// camera is reported at its speed declaring nothing.
    /// </summary>
    public static UsbLink? LinkFrom(byte speed, uint? flags)
    {
        var f = flags ?? 0;
        UsbSpeed running;
        if ((f & OperatingAtSuperSpeedPlus) != 0) running = UsbSpeed.SuperPlus;
        else if ((f & OperatingAtSuperSpeed) != 0) running = UsbSpeed.Super;
        else
        {
            switch (speed)
            {
                case SpeedLow: running = UsbSpeed.Low; break;
                case SpeedFull: running = UsbSpeed.Full; break;
                case SpeedHigh: running = UsbSpeed.High; break;
                case SpeedSuper: running = UsbSpeed.Super; break;
                default: return null;
            }
        }
        UsbSpeed? capable = (f & CapableOfSuperSpeedPlus) != 0 ? UsbSpeed.SuperPlus
            : (f & CapableOfSuperSpeed) != 0 ? UsbSpeed.Super
            : null;
        return new UsbLink(running, capable);
    }

    /// <summary>
    /// The device-instance id behind a USB device-interface path: the path without
    /// its <c>\\?\</c> prefix and trailing <c>#{class-guid}\reference</c>, with the
    /// <c>#</c> separators back to <c>\</c>. <c>null</c> for a non-USB path.
    /// </summary>
    public static string? InstanceId(string interfacePath)
    {
        string path;
        if (interfacePath.StartsWith(@"\\?\", StringComparison.Ordinal)) path = interfacePath[4..];
        else if (interfacePath.StartsWith(@"\\.\", StringComparison.Ordinal)) path = interfacePath[4..];
        else return null;
        var id = path.Split("#{")[0].Replace('#', '\\');
        return id.StartsWith(@"usb\", StringComparison.OrdinalIgnoreCase) ? id : null;
    }

    /// <summary>Whether the instance is one function of a composite device (<c>&amp;MI_00</c>), whose parent holds the port.</summary>
    public static bool IsFunction(string instanceId) =>
        instanceId.Contains("&mi_", StringComparison.OrdinalIgnoreCase);

    private static readonly Lock Gate = new();
    private static Dictionary<string, UsbLink> _links = [];

    /// <summary>Record what a scan found, replacing earlier scans: a detached camera reports no link.</summary>
    public static void Remember(Dictionary<string, UsbLink> found)
    {
        lock (Gate) _links = found;
    }

    /// <summary>The link the latest enumeration found for <paramref name="uniqueId"/>; no I/O.</summary>
    public static UsbLink? For(string uniqueId)
    {
        lock (Gate) return _links.TryGetValue(uniqueId, out var link) ? link : null;
    }
}
