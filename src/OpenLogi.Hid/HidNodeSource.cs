using HidSharp;
using OpenLogi.HidPP.Channel;

namespace OpenLogi.Hid;

/// <summary>
/// One Logitech HID node (an interface/collection) that can be opened into a raw
/// HID++ channel. Abstracts HidSharp so <see cref="HidInventory"/>'s sweep can be
/// driven by scripted nodes in tests — e.g. a receiver split across a short
/// *control* interface and a long-only *device* interface.
/// </summary>
public interface IHidNode
{
    ushort VendorId { get; }
    ushort ProductId { get; }

    /// <summary>The OS device path — tags logs and distinguishes sibling interfaces.</summary>
    string DevicePath { get; }

    /// <summary>Whether the node advertises HID++ short / long reports (from its report descriptor).</summary>
    bool SupportsShort { get; }
    bool SupportsLong { get; }

    /// <summary>Open a raw channel to this node. Throws if it cannot be opened.</summary>
    IRawHidChannel Open();
}

/// <summary>Enumerates the Logitech HID++ nodes to sweep. Swap the implementation in tests.</summary>
public interface IHidNodeSource
{
    IReadOnlyList<IHidNode> Enumerate();
}

/// <summary>The real machine: HidSharp enumeration filtered to HID++-capable nodes.</summary>
public sealed class LocalHidNodeSource : IHidNodeSource
{
    public static readonly LocalHidNodeSource Instance = new();

    public IReadOnlyList<IHidNode> Enumerate()
    {
        var result = new List<IHidNode>();
        foreach (var device in DeviceList.Local.GetHidDevices(HidDiscovery.LogitechVendorId))
            if (WindowsRawHidChannel.DetectSupport(device) is { } support)
                result.Add(new HidSharpNode(device, support));
        return result;
    }

    private sealed class HidSharpNode(HidDevice device, (bool Short, bool Long) support) : IHidNode
    {
        public ushort VendorId => (ushort)device.VendorID;
        public ushort ProductId => (ushort)device.ProductID;
        public string DevicePath => device.DevicePath;
        public bool SupportsShort => support.Short;
        public bool SupportsLong => support.Long;
        public IRawHidChannel Open() => WindowsRawHidChannel.Open(device);
    }
}
