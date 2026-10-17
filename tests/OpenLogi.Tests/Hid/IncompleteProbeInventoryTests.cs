using OpenLogi.Core.DeviceInfo;
using OpenLogi.Hid;
using OpenLogi.HidPP.Channel;
using OpenLogi.HidPP.Protocol;
using OpenLogi.Tests.HidPP;

namespace OpenLogi.Tests.Hid;

/// <summary>
/// A BLE mouse switched back on answers the HID++ ping within seconds but times out
/// on the rest of the probe for several more (2026-10-16 log: an MX Anywhere 3S came
/// back as "(unnamed) (Unknown, online)" with no battery, and its tray icon never
/// returned). The sweep flags such a probe as incomplete so the app re-scans; a
/// device that genuinely lacks a feature must not be flagged. Scripted channels only.
/// </summary>
public class IncompleteProbeInventoryTests
{
    private const ushort BleMousePid = 0xb037; // MX Anywhere 3S for Mac over Bluetooth
    private const byte FeatureSetIndex = 1, NameIndex = 2, BatteryIndex = 3;

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

    private enum Failure { None, FeatureTable, Battery }

    /// <summary>
    /// A direct HID++ 2.0 mouse at 0xff with FeatureSet, DeviceTypeAndName ("MX A3",
    /// Mouse) and UnifiedBattery (72 %, discharging). <paramref name="failure"/> makes
    /// one read answer with a HID++ 2.0 Busy error, the fast form of "not ready yet".
    /// </summary>
    private static MockRawHidChannel Mouse(Failure failure)
    {
        var name = "MX A3"u8.ToArray();
        var raw = new MockRawHidChannel { VendorId = 0x046d, ProductId = BleMousePid, Support = (true, true) };
        raw.OnWrite = req =>
        {
            var msg = V20Message.FromHidpp(req);
            var h = msg.Header;
            if (h.DeviceIndex != 0xff) return null;
            var fn = h.FunctionId.ToLo();
            HidppMessage Reply(params byte[] head)
            {
                var p = new byte[16];
                head.CopyTo(p.AsSpan());
                return V20Message.Long(h, p).ToHidpp();
            }

            if (h.FeatureIndex == 0x00 && fn == 0x1) // root ping → HID++ 2.0
                return Reply(0x04, 0x00, msg.ExtendPayload()[2]);
            if (h.FeatureIndex == 0x00 && fn == 0x0) // root.get_feature(FeatureSet)
                return Reply(FeatureSetIndex, 0x00, 0x02);

            if (h.FeatureIndex == FeatureSetIndex)
            {
                if (failure == Failure.FeatureTable) return Busy(req);
                if (fn == 0x0) return Reply(0x03); // three features
                var id = msg.ExtendPayload()[0] switch { 1 => (ushort)0x0001, 2 => (ushort)0x0005, _ => (ushort)0x1004 };
                return Reply((byte)(id >> 8), (byte)id, 0x00, 0x00);
            }
            if (h.FeatureIndex == NameIndex)
            {
                if (fn == 0x0) return Reply((byte)name.Length);
                if (fn == 0x1) return Reply(name);
                if (fn == 0x2) return Reply(0x03); // Mouse
            }
            if (h.FeatureIndex == BatteryIndex && fn == 0x1) // get_status
                return failure == Failure.Battery ? Busy(req) : Reply(72, 0x04, 0x00); // 72 %, Good, Discharging
            return null;
        };
        return raw;
    }

    /// <summary>A HID++ 2.0 error reply (feature index 0xff) echoing the request, code Busy.</summary>
    private static HidppMessage Busy(HidppMessage req)
    {
        var v10 = V10Message.FromHidpp(req); // [dev, featIdx, funcSw, …] read as a 1.0 header + payload
        var p = new byte[19];
        p[0] = v10.Header.DeviceIndex;
        p[1] = 0xff;
        p[2] = v10.Header.SubId;          // the request's feature index
        p[3] = v10.ExtendPayload()[0];     // the request's function/software id
        p[4] = (byte)V20ErrorType.Busy;
        return HidppMessage.Long(p);
    }

    private static async Task<PairedDevice> SweepAsync(Failure failure)
    {
        var source = new FixedNodeSource(new MockNode(Mouse(failure), @"\\?\hid#{00001124}_vid&0002046d_pid&b037&col02#bbb"));
        var inventory = Assert.Single(await HidInventory.EnumerateAsync(source));
        Assert.Equal("Direct device", inventory.Receiver.Name);
        return Assert.Single(inventory.Paired);
    }

    [Fact]
    public async Task HealthyProbeIsComplete()
    {
        var mouse = await SweepAsync(Failure.None);
        Assert.Equal("MX A3", mouse.Codename);
        Assert.Equal(DeviceKind.Mouse, mouse.Kind);
        Assert.Equal((byte)72, mouse.Battery?.Percentage);
        Assert.False(mouse.ProbeIncomplete);
    }

    [Fact]
    public async Task FailedFeatureTableIsIncomplete()
    {
        // The 2026-10-16 case: answered the ping, then nothing usable.
        var mouse = await SweepAsync(Failure.FeatureTable);
        Assert.True(mouse.Online);
        Assert.Null(mouse.Codename);
        Assert.Equal(DeviceKind.Unknown, mouse.Kind);
        Assert.Null(mouse.Battery);
        Assert.True(mouse.ProbeIncomplete);
    }

    [Fact]
    public async Task AdvertisedBatteryThatFailsToReadIsIncomplete()
    {
        var mouse = await SweepAsync(Failure.Battery);
        Assert.Equal("MX A3", mouse.Codename); // named, so the gallery looks fine…
        Assert.Null(mouse.Battery);             // …but the tray has nothing to draw
        Assert.True(mouse.ProbeIncomplete);
    }
}
