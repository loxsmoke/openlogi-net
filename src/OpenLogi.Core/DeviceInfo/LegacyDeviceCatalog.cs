namespace OpenLogi.Core.DeviceInfo;

/// <summary>
/// Names for HID++ 1.0 devices, keyed by wireless product ID (wpid). A HID++ 1.0
/// device has no DeviceTypeAndName (0x0005) / DeviceInformation (0x0003) features
/// to identify itself with, so the receiver's pairing record (which carries the
/// wpid) is the only handle on what it is. Marketing names as printed on the box;
/// sourced from Solaar's device descriptors. Only HID++ 1.0 models belong here —
/// a HID++ 2.0 device names itself, and the sweep never consults this table for one.
/// </summary>
public static class LegacyDeviceCatalog
{
    public sealed record Entry(string Name, DeviceKind Kind);

    private static readonly Dictionary<ushort, Entry> Entries = new()
    {
        // Mice
        [0x1017] = new("Anywhere Mouse MX", DeviceKind.Mouse),
        [0x101a] = new("Performance Mouse MX", DeviceKind.Mouse),
        [0x101b] = new("Marathon Mouse M705", DeviceKind.Mouse),   // M-R0009 (issue #13)
        [0x101c] = new("Wireless Mouse M350", DeviceKind.Mouse),
        [0x101d] = new("Wireless Mouse M505", DeviceKind.Mouse),
        [0x101f] = new("Wireless Mouse M305", DeviceKind.Mouse),
        [0x1020] = new("Wireless Mouse M215", DeviceKind.Mouse),
        [0x1024] = new("Wireless Mouse M310", DeviceKind.Mouse),
        [0x1025] = new("Wireless Mouse M510", DeviceKind.Mouse),
        [0x1028] = new("Wireless Trackball M570", DeviceKind.Trackball),
        // Keyboards
        [0x2007] = new("Wireless Keyboard K340", DeviceKind.Keyboard),
        [0x2008] = new("Wireless Keyboard MK700", DeviceKind.Keyboard),
        [0x200a] = new("Wireless Keyboard K350", DeviceKind.Keyboard),
        [0x2010] = new("Wireless Illuminated Keyboard K800", DeviceKind.Keyboard),
        [0x2011] = new("Wireless Keyboard K520", DeviceKind.Keyboard),
    };

    /// <summary>The catalog entry for <paramref name="wpid"/>, or <c>null</c> for an unlisted device.</summary>
    public static Entry? Lookup(ushort wpid) => Entries.TryGetValue(wpid, out var e) ? e : null;
}
