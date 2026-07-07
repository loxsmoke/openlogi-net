using OpenLogi.Core.DeviceInfo;
using OpenLogi.Hid;

namespace OpenLogi.Tests.Hid;

/// <summary>
/// Physical-unit deduplication across inventory entries. A device reachable via
/// its receiver and directly (Bluetooth / USB cable), or exposing several HID++
/// collections, answers on every path (regression: each path was listed as its
/// own device, e.g. one keyboard shown twice).
/// </summary>
public class InventoryDedupeTests
{
    private static DeviceModelInfo Model(byte[] unitId, ushort[]? modelIds = null) => new()
    {
        EntityCount = 1,
        UnitId = unitId,
        Transports = new DeviceTransports { Usb = true, Bluetooth = true },
        ModelIds = modelIds ?? [0x407b, 0, 0],
        ExtendedModelId = 0,
    };

    private static DeviceInventory Receiver(params PairedDevice[] paired) => new()
    {
        Receiver = new ReceiverInfo { Name = "Unifying Receiver", VendorId = 0x046d, ProductId = 0xc52b, UniqueId = "AABBCCDD" },
        Paired = paired,
    };

    private static DeviceInventory Direct(PairedDevice device, ushort pid = 0xb020) => new()
    {
        Receiver = new ReceiverInfo { Name = "Direct device", VendorId = 0x046d, ProductId = pid, UniqueId = null },
        Paired = [device],
    };

    private static PairedDevice Device(byte slot, DeviceKind kind, byte[]? unitId, string? codename = null) => new()
    {
        Slot = slot,
        Codename = codename,
        Kind = kind,
        Online = true,
        ModelInfo = unitId is null ? null : Model(unitId),
    };

    [Fact]
    public void DirectNodeMatchingReceiverPairedUnitIsDropped()
    {
        var viaReceiver = Receiver(Device(1, DeviceKind.Keyboard, [1, 2, 3, 4], "MX Keys"));
        var viaBluetooth = Direct(Device(DeviceRoute.DirectDeviceIndex, DeviceKind.Keyboard, [1, 2, 3, 4], "MX Keys"));

        var result = HidInventory.Deduplicate([viaReceiver, viaBluetooth]);

        var inv = Assert.Single(result);
        Assert.Equal("Unifying Receiver", inv.Receiver.Name);
    }

    [Fact]
    public void SecondCollectionOfSameDirectDeviceIsDropped()
    {
        var col1 = Direct(Device(DeviceRoute.DirectDeviceIndex, DeviceKind.Mouse, [9, 9, 9, 9], "MX Vertical"));
        var col2 = Direct(Device(DeviceRoute.DirectDeviceIndex, DeviceKind.Mouse, [9, 9, 9, 9], "MX Vertical"));

        var result = HidInventory.Deduplicate([col1, col2]);

        Assert.Single(result);
    }

    [Fact]
    public void DistinctUnitsAndUnidentifiedDevicesAreKept()
    {
        var viaReceiver = Receiver(Device(1, DeviceKind.Mouse, [1, 1, 1, 1], "MX Vertical"));
        var otherUnit = Direct(Device(DeviceRoute.DirectDeviceIndex, DeviceKind.Keyboard, [2, 2, 2, 2], "MX Keys"));
        var zeroUnitId = Direct(Device(DeviceRoute.DirectDeviceIndex, DeviceKind.Mouse, [0, 0, 0, 0]), pid: 0x4090);
        var noModelInfo = Direct(Device(DeviceRoute.DirectDeviceIndex, DeviceKind.Mouse, null), pid: 0x4091);

        var result = HidInventory.Deduplicate([viaReceiver, otherUnit, zeroUnitId, noModelInfo]);

        Assert.Equal(4, result.Count);
    }

    [Fact]
    public void ReceiverEntriesAreNeverDropped()
    {
        // Same unit on two receivers (e.g. multi-host pairing) is genuine state.
        var a = Receiver(Device(1, DeviceKind.Mouse, [5, 5, 5, 5]));
        var b = Receiver(Device(2, DeviceKind.Mouse, [5, 5, 5, 5]));

        var result = HidInventory.Deduplicate([a, b]);

        Assert.Equal(2, result.Count);
    }

    // ── Offline receiver slots vs. live transports ───────────────────────────
    // A device paired to its receiver but currently connected over Bluetooth is
    // reported by the receiver as an offline slot — unit id unreadable, so the
    // match runs on the slot's wpid against online devices' model ids (0x0003).
    // Regression: the G915's dormant LIGHTSPEED slot showed as an empty tile
    // next to the live Bluetooth entry.

    private static PairedDevice OfflineSlot(byte slot, DeviceKind kind, ushort wpid) => new()
    {
        Slot = slot,
        Kind = kind,
        Online = false,
        Wpid = wpid,
    };

    [Fact]
    public void OfflineSlotOfModelOnlineElsewhereIsDropped()
    {
        var dormantPairing = Receiver(OfflineSlot(1, DeviceKind.Keyboard, wpid: 0x407c));
        var viaBluetooth = Direct(Device(DeviceRoute.DirectDeviceIndex, DeviceKind.Keyboard, [7, 7, 7, 7], "G915")
            with { ModelInfo = Model([7, 7, 7, 7], modelIds: [0xb354, 0x407c, 0xc33e]) }, pid: 0xb354);

        var result = HidInventory.Deduplicate([dormantPairing, viaBluetooth]);

        Assert.Equal(2, result.Count);
        Assert.Empty(result[0].Paired); // receiver kept, dormant slot gone
        Assert.Single(result[1].Paired);
    }

    [Fact]
    public void OfflineSlotWithNoLiveTwinIsKept()
    {
        // Powered-off device: its offline slot is the only trace — must stay listed.
        var dormantPairing = Receiver(OfflineSlot(1, DeviceKind.Keyboard, wpid: 0x407c));
        var unrelatedMouse = Direct(Device(DeviceRoute.DirectDeviceIndex, DeviceKind.Mouse, [1, 1, 1, 1], "MX Anywhere 3S")
            with { ModelInfo = Model([1, 1, 1, 1], modelIds: [0xb037, 0x4090, 0]) });

        var result = HidInventory.Deduplicate([dormantPairing, unrelatedMouse]);

        Assert.Equal(2, result.Count);
        Assert.Single(result[0].Paired);
    }

    [Fact]
    public void OfflineSlotIsNotMatchedAgainstOfflineDevices()
    {
        // Only *online* devices prove the model is live on another transport.
        var dormantPairing = Receiver(OfflineSlot(1, DeviceKind.Keyboard, wpid: 0x407c));
        var alsoOffline = Direct(Device(DeviceRoute.DirectDeviceIndex, DeviceKind.Keyboard, [7, 7, 7, 7], "G915")
            with { ModelInfo = Model([7, 7, 7, 7], modelIds: [0xb354, 0x407c, 0]), Online = false }, pid: 0xb354);

        var result = HidInventory.Deduplicate([dormantPairing, alsoOffline]);

        Assert.Single(result[0].Paired);
    }

    [Fact]
    public void OnlineReceiverSlotIsKeptEvenWhenModelIdsOverlap()
    {
        // An online slot is genuine state regardless of wpid overlap; only its
        // direct twin is collapsed (by unit id, the first rule).
        var viaReceiver = Receiver(Device(1, DeviceKind.Keyboard, [7, 7, 7, 7], "G915")
            with { Wpid = 0x407c, ModelInfo = Model([7, 7, 7, 7], modelIds: [0xb354, 0x407c, 0]) });

        var result = HidInventory.Deduplicate([viaReceiver]);

        Assert.Single(Assert.Single(result).Paired);
    }
}
