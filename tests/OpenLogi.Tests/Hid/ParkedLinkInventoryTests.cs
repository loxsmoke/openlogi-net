using OpenLogi.Core.DeviceInfo;
using OpenLogi.Hid;
using OpenLogi.HidPP.Channel;
using OpenLogi.HidPP.Protocol;
using OpenLogi.Tests.HidPP;

namespace OpenLogi.Tests.Hid;

/// <summary>
/// A receiver's 0x41 link-established flag reports instantaneous radio state, so a
/// device that parked its RF link for power saving (a G915 does within ~1-2 s of the
/// last keystroke) announces "offline" while fully awake — and the wake-triggered
/// rescan would list the keyboard as asleep right after the keypress that woke it.
/// The inventory pings an announced-offline slot to tell parked (answers → online)
/// from asleep (ResourceError → offline). Modeled with scripted channels — no hardware.
/// </summary>
public class ParkedLinkInventoryTests
{
    private const ushort LightspeedPid = 0xc547; // G915 dongle
    private const ushort G915Wpid = 0x407c;

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
    /// A single-interface LIGHTSPEED receiver whose one paired keyboard announces
    /// itself with the link-not-established bit set. <paramref name="slotAnswersPing"/>
    /// selects parked (ping answered → awake) vs asleep (fast ResourceError NACK,
    /// the way a real dongle fails an unreachable slot).
    /// </summary>
    private static MockRawHidChannel Receiver(bool slotAnswersPing)
    {
        var raw = new MockRawHidChannel { VendorId = 0x046d, ProductId = LightspeedPid, Support = (true, true) };
        raw.OnWrite = req =>
        {
            var v10 = V10Message.FromHidpp(req);
            var idx = v10.Header.DeviceIndex;
            var sub = v10.Header.SubId;

            if (idx == 0xff && sub == (byte)V10MessageType.SetRegister) // receiver register write
            {
                var address = v10.ExtendPayload()[0];
                if (address == 0x02) // RegConnections = trigger device arrival
                    raw.SendIncoming(OfflineKeyboardArrival(slot: 1, wpid: G915Wpid));
                return HidppMessage.Short([0xff, (byte)V10MessageType.SetRegister, address, 0, 0, 0]); // ACK
            }
            if (idx == 0xff && sub == (byte)V10MessageType.GetLongRegister) // codename read
                return RegisterError(idx, v10.ExtendPayload()[0]); // G915 dongle: fast ResourceError

            if (idx == 1)
            {
                var v20 = V20Message.FromHidpp(req);
                var h = v20.Header;
                if (h.FeatureIndex == 0x00 && h.FunctionId.ToLo() == 0x1) // root ping
                {
                    if (!slotAnswersPing)
                        return PingResourceError(req); // receiver NACK: device unreachable
                    var p = new byte[16];
                    p[0] = 0x04; // protocol_num = 4 → HID++ 2.0
                    p[2] = v20.ExtendPayload()[2];
                    return V20Message.Long(h, p).ToHidpp();
                }
                if (h.FeatureIndex == 0x00 && h.FunctionId.ToLo() == 0x0) // root.get_feature(id)
                    return V20Message.Long(h, new byte[16]).ToHidpp(); // FeatureSet absent → no enumeration
            }
            return null;
        };
        return raw;
    }

    /// <summary>A 0x41 arrival for a keyboard with the link-not-established bit (6) set.</summary>
    private static HidppMessage OfflineKeyboardArrival(byte slot, ushort wpid) =>
        HidppMessage.Short([slot, 0x41, 0x04, 0x01 | (1 << 6), (byte)(wpid & 0xff), (byte)(wpid >> 8)]);

    /// <summary>A HID++ 1.0 register-read error (fast fail, no timeout).</summary>
    private static HidppMessage RegisterError(byte device, byte address) =>
        HidppMessage.Short([device, (byte)V10MessageType.Error, (byte)V10MessageType.GetLongRegister, address, 0x09, 0]);

    /// <summary>
    /// The receiver's fast NACK for a ping to an unreachable slot: a HID++ 1.0 error
    /// echoing the ping's feature index (0x00) and funcId/swId byte, code 0x09
    /// (ResourceError) — which pins the version probe to "nothing answered".
    /// </summary>
    private static HidppMessage PingResourceError(HidppMessage req)
    {
        var funcSw = V10Message.FromHidpp(req).ExtendPayload()[0];
        return HidppMessage.Short([1, (byte)V10MessageType.Error, 0x00, funcSw, 0x09, 0]);
    }

    [Fact]
    public async Task AnnouncedOfflineSlotThatAnswersAPingIsListedOnline()
    {
        var source = new FixedNodeSource(
            new MockNode(Receiver(slotAnswersPing: true), @"\\?\hid#vid_046d&pid_c547&mi_02&col01#aaa"));

        var inventory = await HidInventory.EnumerateAsync(source);

        var receiver = Assert.Single(inventory);
        Assert.Equal("Lightspeed Receiver", receiver.Receiver.Name);
        var slot = Assert.Single(receiver.Paired);
        Assert.Equal(DeviceKind.Keyboard, slot.Kind);
        Assert.True(slot.Online); // parked link ≠ asleep: the tile must not flip to "Asleep"
    }

    [Fact]
    public async Task AnnouncedOfflineSlotThatIgnoresThePingStaysOffline()
    {
        var source = new FixedNodeSource(
            new MockNode(Receiver(slotAnswersPing: false), @"\\?\hid#vid_046d&pid_c547&mi_02&col01#aaa"));

        var inventory = await HidInventory.EnumerateAsync(source);

        var receiver = Assert.Single(inventory);
        var slot = Assert.Single(receiver.Paired);
        Assert.False(slot.Online); // genuinely asleep — unchanged behavior
    }
}
