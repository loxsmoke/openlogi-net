using OpenLogi.Core.DeviceInfo;
using OpenLogi.Hid;
using OpenLogi.HidPP.Channel;
using OpenLogi.HidPP.Protocol;
using OpenLogi.HidPP.Receiver;
using OpenLogi.Tests.HidPP;

namespace OpenLogi.Tests.Hid;

/// <summary>
/// Issue #13: a Marathon M705 (M-R0009, wpid 101b, HID++ 1.0 only) on a classic
/// Unifying receiver (046d:c52b) showed up as a nameless "Mouse" with nothing to
/// configure. Modeled on the reporter's Windows interface layout: col01 carries
/// short reports only, col02 long reports only, and every long-reply register read
/// on col01 fails (so uid + codename are null — the receiver-side bug is separate).
/// The device must still resolve a name from its wpid and a battery from register
/// 0x07, and be flagged HID++ 1.0 with no presumed capabilities.
/// </summary>
public class Hidpp10InventoryTests
{
    private const ushort UnifyingPid = 0xc52b;
    private const ushort M705Wpid = 0x101b;

    private sealed class MockNode(MockRawHidChannel raw, string path) : IHidNode
    {
        public ushort VendorId => raw.VendorId;
        public ushort ProductId => raw.ProductId;
        public string DevicePath => path;
        public bool SupportsShort => raw.Support.SupportsShort;
        public bool SupportsLong => raw.Support.SupportsLong;
        public IRawHidChannel Open() => raw;
    }

    private sealed class FixedNodeSource(params IHidNode[] nodes) : IHidNodeSource
    {
        public IReadOnlyList<IHidNode> Enumerate() => nodes;
    }

    /// <summary>
    /// col01: the receiver control interface (short reports only) with one online
    /// HID++ 1.0 mouse at slot 1 whose register 0x07 reports <paramref name="batteryLevel"/>.
    /// </summary>
    private static MockRawHidChannel ControlInterface(ushort wpid, byte batteryLevel, List<byte> registerReads)
    {
        var raw = new MockRawHidChannel { VendorId = 0x046d, ProductId = UnifyingPid, Support = (true, false) };
        raw.OnWrite = req =>
        {
            var p = req.Payload;
            var idx = p[0];
            var sub = p[1];

            if (idx == 0xff && sub == (byte)V10MessageType.SetRegister)
            {
                if (p[2] == 0x02) // trigger device arrival → 0x41 for the mouse
                    raw.SendIncoming(HidppMessage.Short([0x01, 0x41, 0x04, (byte)UnifyingDeviceKind.Mouse, (byte)(wpid & 0xff), (byte)(wpid >> 8)]));
                return HidppMessage.Short([0xff, (byte)V10MessageType.SetRegister, p[2], 0, 0, 0]);
            }
            if (idx == 0xff && sub == (byte)V10MessageType.GetLongRegister) // uid / codename: fails on col01
                return HidppMessage.Short([0xff, (byte)V10MessageType.Error, sub, p[2], (byte)V10ErrorType.ResourceError, 0]);

            if (idx == 0x01)
            {
                if (sub == 0x00) // HID++ 2.0 ping (feature index 0) → 1.0 "invalid sub-id" error
                    return HidppMessage.Short([0x01, (byte)V10MessageType.Error, p[1], p[2], (byte)V10ErrorType.InvalidSubId, 0]);
                if (sub == (byte)V10MessageType.GetRegister)
                {
                    registerReads.Add(p[2]);
                    return p[2] == V10Battery.RegBatteryStatus
                        ? HidppMessage.Short([0x01, sub, p[2], batteryLevel, 0x00, 0x00])
                        : HidppMessage.Short([0x01, (byte)V10MessageType.Error, sub, p[2], (byte)V10ErrorType.InvalidAddress, 0]);
                }
            }
            return null;
        };
        return raw;
    }

    /// <summary>col02: the long-only sibling; nothing for a 1.0 device ever rides it.</summary>
    private static MockRawHidChannel LongOnlyInterface() =>
        new() { VendorId = 0x046d, ProductId = UnifyingPid, Support = (false, true), OnWrite = _ => null };

    [Fact]
    public async Task M705OnUnifyingResolvesNameAndBatteryReadOnly()
    {
        var reads = new List<byte>();
        var source = new FixedNodeSource(
            new MockNode(ControlInterface(M705Wpid, batteryLevel: 0x05, reads), @"\?\hid#vid_046d&pid_c52b&mi_02&col01#aaa"),
            new MockNode(LongOnlyInterface(), @"\?\hid#vid_046d&pid_c52b&mi_02&col02#bbb"));

        var inventory = await HidInventory.EnumerateAsync(source);

        var receiver = Assert.Single(inventory);
        Assert.Equal(UnifyingPid, receiver.Receiver.ProductId);
        var mouse = Assert.Single(receiver.Paired);

        Assert.True(mouse.IsHidpp10);
        Assert.True(mouse.Online);
        Assert.Equal(M705Wpid, mouse.Wpid);
        Assert.Equal("Marathon Mouse M705", mouse.Codename);
        Assert.Equal(DeviceKind.Mouse, mouse.Kind);

        Assert.NotNull(mouse.Battery);
        Assert.Equal(BatteryLevel.Good, mouse.Battery.Level);
        Assert.Equal(50, mouse.Battery.Percentage);
        Assert.Equal(BatteryStatus.Discharging, mouse.Battery.Status);

        // No HID++ 2.0 panel can work on it: the kind-presumed guess must not apply.
        Assert.NotNull(mouse.Capabilities);
        Assert.False(mouse.Capabilities.Buttons);
        Assert.False(mouse.Capabilities.Pointer);

        Assert.Equal([V10Battery.RegBatteryStatus], reads); // 0x07 answered → no 0x0D fallback
    }

    [Fact]
    public async Task UnlistedHidpp10DeviceStillGetsBatteryAndFlag()
    {
        // The catalog is small: a 1.0 device with an unknown wpid must still be
        // flagged 1.0 (so the UI stays read-only) and read its battery; it keeps the
        // receiver-reported kind, which the UI falls back to for the name.
        var reads = new List<byte>();
        var source = new FixedNodeSource(
            new MockNode(ControlInterface(0x1fff, batteryLevel: 0x03, reads), @"\?\hid#vid_046d&pid_c52b&mi_02&col01#aaa"));

        var inventory = await HidInventory.EnumerateAsync(source);

        var mouse = Assert.Single(Assert.Single(inventory).Paired);
        Assert.True(mouse.IsHidpp10);
        Assert.Null(mouse.Codename);
        Assert.Equal(DeviceKind.Mouse, mouse.Kind);
        Assert.Equal(BatteryLevel.Low, mouse.Battery?.Level);
    }
}
