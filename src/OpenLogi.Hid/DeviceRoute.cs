using OpenLogi.Core.DeviceInfo;

namespace OpenLogi.Hid;

/// <summary>
/// How to reach a controllable HID++ device. Ported from Rust
/// <c>openlogi-hid::route::DeviceRoute</c>.
///
/// Two addressing modes: through a receiver (Bolt / Unifying) at a pairing slot,
/// or <see cref="Direct"/> to a USB-cable / Bluetooth device at the HID++
/// self-index <see cref="DirectDeviceIndex"/>.
/// </summary>
public abstract record DeviceRoute
{
    private DeviceRoute() { }

    /// <summary>HID++ device index addressing a directly-attached device's own features.</summary>
    public const byte DirectDeviceIndex = 0xff;

    /// <summary>USB product IDs identifying Logi Bolt receivers.</summary>
    public static readonly ushort[] BoltPids = [0xc548];

    /// <summary>USB product IDs identifying Logi Unifying receivers.</summary>
    public static readonly ushort[] UnifyingPids = [0xc52b, 0xc532];

    /// <summary>USB product IDs identifying LIGHTSPEED (G-series) receivers; keep in sync with <see cref="OpenLogi.HidPP.Receiver.UnifyingReceiver.LightspeedVpidPairs"/>.</summary>
    public static readonly ushort[] LightspeedPids = [0xc539, 0xc53a, 0xc53f, 0xc541, 0xc545, 0xc547];

    /// <summary>Paired to a Logi Bolt receiver at <paramref name="Slot"/> (1..=6).</summary>
    public sealed record Bolt(string ReceiverUid, byte Slot) : DeviceRoute;

    /// <summary>Paired to a Logi Unifying receiver (HID++ 1.0) at <paramref name="Slot"/>.</summary>
    public sealed record Unifying(string ReceiverUid, byte Slot) : DeviceRoute;

    /// <summary>Paired to a LIGHTSPEED receiver (HID++ 1.0, Unifying register set) at <paramref name="Slot"/>.</summary>
    public sealed record Lightspeed(string ReceiverUid, byte Slot) : DeviceRoute;

    /// <summary>Attached straight to the host, addressed at the HID++ self-index.</summary>
    public sealed record Direct(ushort VendorId, ushort ProductId) : DeviceRoute;

    /// <summary>The HID++ device index features are addressed at for this route.</summary>
    public byte DeviceIndex() => this switch
    {
        Bolt b => b.Slot,
        Unifying u => u.Slot,
        Lightspeed l => l.Slot,
        Direct => DirectDeviceIndex,
        _ => DirectDeviceIndex,
    };

    /// <summary>
    /// Build the route reaching a paired device from a receiver inventory. A
    /// receiver PID in neither <see cref="UnifyingPids"/> nor <see cref="LightspeedPids"/>
    /// defaults to <see cref="Bolt"/> (so future Bolt variants keep working); a slot of
    /// <see cref="DirectDeviceIndex"/> with no receiver UID is <see cref="Direct"/>.
    /// Returns <c>null</c> when the receiver UID is unknown (writes are skipped, not mis-routed).
    /// </summary>
    public static DeviceRoute? DeviceRouteFor(DeviceInventory inv, byte slot)
    {
        var uid = inv.Receiver.UniqueId;
        if (uid is not null)
        {
            if (UnifyingPids.Contains(inv.Receiver.ProductId)) return new Unifying(uid, slot);
            if (LightspeedPids.Contains(inv.Receiver.ProductId)) return new Lightspeed(uid, slot);
            return new Bolt(uid, slot);
        }
        return slot == DirectDeviceIndex
            ? new Direct(inv.Receiver.VendorId, inv.Receiver.ProductId)
            : null;
    }

    public sealed override string ToString() => this switch
    {
        Bolt b => $"slot {b.Slot} on receiver {b.ReceiverUid}",
        Unifying u => $"slot {u.Slot} on receiver {u.ReceiverUid}",
        Lightspeed l => $"slot {l.Slot} on receiver {l.ReceiverUid}",
        Direct d => $"direct {d.VendorId:x4}:{d.ProductId:x4}",
        _ => base.ToString() ?? "",
    };
}
