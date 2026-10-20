using System.Text;
using OpenLogi.Core.DeviceInfo;
using OpenLogi.Hid;
using OpenLogi.HidPP.Channel;
using OpenLogi.HidPP.Protocol;
using OpenLogi.HidPP.Receiver;
using OpenLogi.Tests.HidPP;

namespace OpenLogi.Tests.Hid;

/// <summary>
/// Issue #19: an MX Anywhere 2S (HID++ 2.0, wpid 406a) on a Unifying receiver
/// (046d:c52b) that Windows splits into col01 (short only) and col02 (long only).
/// Every long reply (the receiver's serial and codename registers, the mouse's
/// HID++ 2.0 answers) lands on col02, so probing col01 alone left a nameless "Mouse"
/// with no uid (not controllable) and a probe that never completed. Modeled with
/// scripted channels.
/// </summary>
public class UnifyingSplitInventoryTests
{
    private const ushort UnifyingPid = 0xc52b;
    private const string Guid = "#{4d1e55b2-f16f-11cf-88cb-001111000030}";

    /// <summary>One receiver's pairing: its serial plus the HID++ 2.0 mouse at slot 1.</summary>
    private sealed record Pairing(byte[] Serial, ushort Wpid, string Codename);

    private static readonly Pairing MxAnywhere2S = new([0xaa, 0xbb, 0xcc, 0xdd], 0x406a, "MX Anywhere 2S");
    private static readonly Pairing MxMaster3 = new([0x11, 0x22, 0x33, 0x44], 0x4082, "MX Master 3");

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

    /// <summary>A long HID++ 1.0 register reply from the receiver: [0xff, 0x83, address, data…].</summary>
    private static HidppMessage LongRegisterReply(byte address, byte[] data)
    {
        var p = new byte[HidppMessage.LongPayloadLength];
        p[0] = 0xff;
        p[1] = (byte)V10MessageType.GetLongRegister;
        p[2] = address;
        data.CopyTo(p.AsSpan(3));
        return HidppMessage.Long(p);
    }

    /// <summary>
    /// col01: receiver registers and arrival notifications. Long register replies are
    /// delivered on <paramref name="col02"/>, and device-slot requests here go unanswered.
    /// </summary>
    private static MockRawHidChannel ControlInterface(MockRawHidChannel col02, Pairing pairing)
    {
        var raw = new MockRawHidChannel { VendorId = 0x046d, ProductId = UnifyingPid, Support = (true, false) };
        raw.OnWrite = req =>
        {
            var p = req.Payload;
            if (p[0] != 0xff) return null; // device slot on the control side: silence
            if (p[1] == (byte)V10MessageType.SetRegister)
            {
                if (p[2] == 0x02) // trigger device arrival → 0x41 for the mouse
                    raw.SendIncoming(HidppMessage.Short([0x01, 0x41, 0x04, (byte)UnifyingDeviceKind.Mouse,
                        (byte)(pairing.Wpid & 0xff), (byte)(pairing.Wpid >> 8)]));
                return HidppMessage.Short([0xff, (byte)V10MessageType.SetRegister, p[2], 0, 0, 0]);
            }
            if (p[1] == (byte)V10MessageType.GetLongRegister && p[2] == 0xb5)
            {
                var sub = p[3];
                if (sub == 0x03) // receiver info: serial in bytes 1..4, slot count at 6
                    col02.SendIncoming(LongRegisterReply(0xb5, [0x03, .. pairing.Serial, 0x00, 0x06]));
                else if (sub == 0x41) // slot 1 codename: length, then the name
                    col02.SendIncoming(LongRegisterReply(0xb5,
                        [0x41, (byte)pairing.Codename.Length, .. Encoding.ASCII.GetBytes(pairing.Codename)]));
                return null;
            }
            return null;
        };
        return raw;
    }

    /// <summary>col02: the long-only collection answering the mouse's HID++ 2.0 at slot 1.</summary>
    private static MockRawHidChannel DeviceInterface(Pairing pairing)
    {
        var raw = new MockRawHidChannel { VendorId = 0x046d, ProductId = UnifyingPid, Support = (false, true) };
        raw.OnWrite = req =>
        {
            var m = V20Message.FromHidpp(req);
            var h = m.Header;
            if (h.DeviceIndex != 1) return null;
            HidppMessage Respond(params byte[] head)
            {
                var p = new byte[16];
                head.CopyTo(p.AsSpan());
                return V20Message.Long(h, p).ToHidpp();
            }

            if (h.FeatureIndex == 0x00 && h.FunctionId.ToLo() == 0x1) // ping
                return Respond(0x04, 0x00, m.ExtendPayload()[2]);
            if (h.FeatureIndex == 0x00 && h.FunctionId.ToLo() == 0x0) // root.get_feature(id)
            {
                var id = (ushort)((m.ExtendPayload()[0] << 8) | m.ExtendPayload()[1]);
                return id == 0x0001 ? Respond(0x02, 0x00, 0x02) : Respond(0x00, 0x00, 0x00);
            }
            if (h.FeatureIndex == 0x02 && h.FunctionId.ToLo() == 0x0) // FeatureSet.count
                return Respond(0x01);
            if (h.FeatureIndex == 0x02 && h.FunctionId.ToLo() == 0x1) // FeatureSet.get_feature(i)
                return Respond(0x00, 0x03, 0x00, 0x01); // DeviceInformation
            if (h.FeatureIndex == 0x01 && h.FunctionId.ToLo() == 0x0) // getDeviceInfo
                return Respond(
                    0x01,                                                // entity count
                    0x11, 0x22, 0x33, 0x44,                              // unit id
                    0x00, 0x00,                                          // gap, transport
                    (byte)(pairing.Wpid >> 8), (byte)pairing.Wpid,       // ModelId[0]
                    0x00, 0x00, 0x00, 0x00,                              // ModelId[1..2]
                    0x01, 0x00);                                         // extended model id, capabilities
            return null;
        };
        return raw;
    }

    /// <summary>
    /// col01 + col02 of one receiver. <paramref name="instance"/> is the parent part of
    /// the instance id Windows gives both collections; identical dongles differ only there.
    /// </summary>
    private static IHidNode[] Receiver(Pairing pairing, string instance)
    {
        var col02 = DeviceInterface(pairing);
        return
        [
            new MockNode(ControlInterface(col02, pairing), $@"\\?\hid#vid_046d&pid_c52b&mi_02&col01#{instance}&0000{Guid}"),
            new MockNode(col02, $@"\\?\hid#vid_046d&pid_c52b&mi_02&col02#{instance}&0001{Guid}"),
        ];
    }

    private static void AssertResolved(DeviceInventory receiver, Pairing pairing)
    {
        Assert.Equal(Convert.ToHexString(pairing.Serial), receiver.Receiver.UniqueId); // controllable: the route needs it
        var mouse = Assert.Single(receiver.Paired);
        Assert.False(mouse.ProbeIncomplete);
        Assert.False(mouse.IsHidpp10);
        Assert.Equal(pairing.Codename, mouse.Codename);
        Assert.Equal($"1{pairing.Wpid:x4}", mouse.ModelInfo?.ConfigKey());
        Assert.NotNull(DeviceRoute.DeviceRouteFor(receiver, mouse.Slot));
    }

    [Fact]
    public async Task Hidpp20MouseOnSplitUnifyingResolvesIdentityAndUid()
    {
        var inventory = await HidInventory.EnumerateAsync(new FixedNodeSource(Receiver(MxAnywhere2S, "8&2de4abc&0")));

        var receiver = Assert.Single(inventory);
        Assert.Equal("Unifying Receiver", receiver.Receiver.Name);
        AssertResolved(receiver, MxAnywhere2S);
    }

    [Fact]
    public async Task TwinUnifyingReceiversEachProbeTheirOwnDeviceCollection()
    {
        // Two identical dongles: same VID/PID, so only the device path ties each col01
        // to its own col02. Pairing them by VID/PID probed receiver B's slot through
        // receiver A's col02 and lost B's serial reply.
        var nodes = Receiver(MxAnywhere2S, "8&2de4abc&0").Concat(Receiver(MxMaster3, "8&17f0e9&0")).ToArray();

        var inventory = await HidInventory.EnumerateAsync(new FixedNodeSource(nodes));

        Assert.Equal(2, inventory.Count);
        AssertResolved(Assert.Single(inventory, i => i.Receiver.UniqueId == "AABBCCDD"), MxAnywhere2S);
        AssertResolved(Assert.Single(inventory, i => i.Receiver.UniqueId == "11223344"), MxMaster3);
    }
}
