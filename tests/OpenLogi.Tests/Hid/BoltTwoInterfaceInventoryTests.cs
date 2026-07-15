using OpenLogi.Hid;
using OpenLogi.HidPP.Channel;
using OpenLogi.HidPP.Protocol;
using OpenLogi.HidPP.Receiver;
using OpenLogi.Tests.HidPP;

namespace OpenLogi.Tests.Hid;

/// <summary>
/// Issue #6: a Bolt receiver whose paired devices carry HID++ 2.0 on a long-only
/// *device* interface (col02), separate from the short *control* interface (col01)
/// where the receiver's connection notifications live but the device slots are
/// unreachable. The inventory must probe the device interface so the mice resolve a
/// model (→ config key) and capabilities, not just a nameless "Mouse".
/// Modeled entirely with scripted channels — no hardware.
/// </summary>
public class BoltTwoInterfaceInventoryTests
{
    private const ushort BoltPid = 0xc548;

    // ── Test HID node backed by a scripted mock channel ──────────────────────
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

    // ── col01: the Bolt receiver control interface (short + long) ─────────────
    private static MockRawHidChannel ControlInterface()
    {
        var raw = new MockRawHidChannel { VendorId = 0x046d, ProductId = BoltPid, Support = (true, true) };
        raw.OnWrite = req =>
        {
            var m = V10Message.FromHidpp(req);
            var idx = m.Header.DeviceIndex;
            var sub = m.Header.SubId;

            if (idx == 0xff && sub == (byte)V10MessageType.SetRegister) // receiver register write
            {
                var address = m.ExtendPayload()[0];
                if (address == 0x02) // RegConnections = trigger device arrival
                {
                    raw.SendIncoming(Arrival(slot: 1, wpid: 0xb042)); // e.g. MX Master 4
                    raw.SendIncoming(Arrival(slot: 2, wpid: 0xb031)); // e.g. Lift
                }
                return HidppMessage.Short([0xff, (byte)V10MessageType.SetRegister, address, 0, 0, 0]); // ACK
            }
            if (idx == 0xff && sub == (byte)V10MessageType.GetLongRegister) // uid / codename read
                return RegisterError(idx, m.ExtendPayload()[0]); // fail → uid/codename null (tolerated)

            // A device slot ping on the *control* interface never answers (the bug): fail
            // it the way the receiver does, so probing here yields no capabilities.
            if (idx is 1 or 2)
                return RegisterError(idx, 0x00);
            return null;
        };
        return raw;
    }

    // ── col02: the long-only device interface answering HID++ 2.0 per slot ────
    private static MockRawHidChannel DeviceInterface()
    {
        var raw = new MockRawHidChannel { VendorId = 0x046d, ProductId = BoltPid, Support = (false, true) };
        raw.OnWrite = req =>
        {
            var m = V20Message.FromHidpp(req);
            var h = m.Header;
            HidppMessage Respond(params byte[] head)
            {
                var p = new byte[16];
                head.CopyTo(p.AsSpan());
                return V20Message.Long(h, p).ToHidpp();
            }

            // Root feature (index 0).
            if (h.FeatureIndex == 0x00 && h.FunctionId.ToLo() == 0x1) // ping
                return Respond(0x04, 0x00, m.ExtendPayload()[2]); // protocol_num = 4 (V20)
            if (h.FeatureIndex == 0x00 && h.FunctionId.ToLo() == 0x0) // root.get_feature(id)
            {
                var id = (ushort)((m.ExtendPayload()[0] << 8) | m.ExtendPayload()[1]);
                return id == 0x0001 // FeatureSet → index 2
                    ? Respond(0x02, 0x00, 0x02)
                    : Respond(0x00, 0x00, 0x00); // anything else: not present
            }
            // FeatureSet (index 2).
            if (h.FeatureIndex == 0x02 && h.FunctionId.ToLo() == 0x0) // count
                return Respond(0x01); // one feature: DeviceInformation
            if (h.FeatureIndex == 0x02 && h.FunctionId.ToLo() == 0x1) // get_feature(i)
            {
                var id = m.ExtendPayload()[0] == 1 ? (ushort)0x0003 : (ushort)0x0000;
                return Respond((byte)(id >> 8), (byte)id, 0x00, 0x01);
            }
            // DeviceInformation (index 1), func 0 = getDeviceInfo. ModelId[0] differs per slot.
            if (h.FeatureIndex == 0x01 && h.FunctionId.ToLo() == 0x0)
            {
                var modelLo = h.DeviceIndex == 1 ? (byte)0x42 : (byte)0x31;
                return Respond(
                    0x01,                    // [0] entity count
                    0x11, 0x22, 0x33, 0x44,  // [1..5] unit id
                    0x00,                    // [5] (gap)
                    0x00,                    // [6] transport
                    0xb0, modelLo,           // [7..9] ModelId[0] (big-endian)
                    0x00, 0x00,              // [9..11] ModelId[1]
                    0x00, 0x00,              // [11..13] ModelId[2]
                    0x01,                    // [13] ExtendedModelId
                    0x00);                   // [14] capabilities
            }
            return null;
        };
        return raw;
    }

    /// <summary>A 0x41 Bolt device-arrival notification for an online mouse.</summary>
    private static HidppMessage Arrival(byte slot, ushort wpid) =>
        HidppMessage.Short([slot, 0x41, 0x04, (byte)BoltDeviceKind.Mouse, (byte)(wpid & 0xff), (byte)(wpid >> 8)]);

    /// <summary>A HID++ 1.0 error response (fast fail, no timeout).</summary>
    private static HidppMessage RegisterError(byte device, byte address) =>
        HidppMessage.Short([device, (byte)V10MessageType.Error, (byte)V10MessageType.GetLongRegister, address, 0x09, 0]);

    [Fact]
    public async Task BoltDevicesResolveOverTheLongDeviceInterface()
    {
        var source = new FixedNodeSource(
            new MockNode(ControlInterface(), @"\\?\hid#vid_046d&pid_c548&mi_02&col01#aaa"),
            new MockNode(DeviceInterface(), @"\\?\hid#vid_046d&pid_c548&mi_02&col02#bbb"));

        var inventory = await HidInventory.EnumerateAsync(source);

        var receiver = Assert.Single(inventory);
        Assert.Equal("Logi Bolt Receiver", receiver.Receiver.Name);
        Assert.Equal(2, receiver.Paired.Count);

        // Both mice must resolve a model — i.e. a non-null config key — so they can be
        // named and configured. Before the fix (probe on the control interface only) the
        // model read fails and ModelInfo is null → nameless, unconfigurable "Mouse".
        var slot1 = Assert.Single(receiver.Paired, d => d.Slot == 1);
        var slot2 = Assert.Single(receiver.Paired, d => d.Slot == 2);
        Assert.NotNull(slot1.ModelInfo);
        Assert.NotNull(slot2.ModelInfo);
        Assert.Equal("1b042", slot1.ModelInfo!.ConfigKey());
        Assert.Equal("1b031", slot2.ModelInfo!.ConfigKey());
    }
}
