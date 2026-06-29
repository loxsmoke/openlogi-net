using OpenLogi.Core;
using OpenLogi.Hid;

namespace OpenLogi.Tests.Hid;

/// <summary>Ported from the Rust <c>openlogi-hid::route</c> test module.</summary>
public class DeviceRouteTests
{
    private static DeviceInventory Inv(ushort productId, string? uniqueId) => new()
    {
        Receiver = new ReceiverInfo
        {
            Name = "test",
            VendorId = 0x046d,
            ProductId = productId,
            UniqueId = uniqueId,
        },
        Paired = [],
    };

    [Fact]
    public void UnifyingPidsCreateUnifyingRoute()
    {
        foreach (var pid in DeviceRoute.UnifyingPids)
        {
            var route = DeviceRoute.DeviceRouteFor(Inv(pid, "A1B2"), 2);
            var u = Assert.IsType<DeviceRoute.Unifying>(route);
            Assert.Equal("A1B2", u.ReceiverUid);
            Assert.Equal((byte)2, u.Slot);
        }
    }

    [Fact]
    public void BoltPidCreatesBoltRoute()
    {
        var route = DeviceRoute.DeviceRouteFor(Inv(0xc548, "UID"), 1);
        var b = Assert.IsType<DeviceRoute.Bolt>(route);
        Assert.Equal("UID", b.ReceiverUid);
        Assert.Equal((byte)1, b.Slot);
    }

    [Fact]
    public void UnknownReceiverPidDefaultsToBolt()
    {
        var route = DeviceRoute.DeviceRouteFor(Inv(0x9999, "UID"), 3);
        Assert.IsType<DeviceRoute.Bolt>(route);
    }

    [Fact]
    public void DirectWhenNoUidAndDirectSlot()
    {
        var route = DeviceRoute.DeviceRouteFor(Inv(0xb025, null), DeviceRoute.DirectDeviceIndex);
        var d = Assert.IsType<DeviceRoute.Direct>(route);
        Assert.Equal((ushort)0x046d, d.VendorId);
        Assert.Equal((ushort)0xb025, d.ProductId);
    }

    [Fact]
    public void NoneWhenNoUidAndNonDirectSlot() =>
        Assert.Null(DeviceRoute.DeviceRouteFor(Inv(0xc52b, null), 1));

    [Fact]
    public void UnifyingDeviceIndexIsTheSlot() =>
        Assert.Equal((byte)4, new DeviceRoute.Unifying("X", 4).DeviceIndex());

    [Fact]
    public void DirectDeviceIndexIsSelfIndex() =>
        Assert.Equal(DeviceRoute.DirectDeviceIndex, new DeviceRoute.Direct(0x046d, 0xb025).DeviceIndex());

    [Fact]
    public void UnifyingDisplayMatchesBoltFormat() =>
        Assert.Equal("slot 3 on receiver AABBCC", new DeviceRoute.Unifying("AABBCC", 3).ToString());

    [Fact]
    public void DirectDisplayFormat() =>
        Assert.Equal("direct 046d:b025", new DeviceRoute.Direct(0x046d, 0xb025).ToString());
}
