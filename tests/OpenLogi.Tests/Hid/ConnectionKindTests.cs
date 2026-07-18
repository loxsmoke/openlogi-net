using OpenLogi.Core.DeviceInfo;
using OpenLogi.Hid;

namespace OpenLogi.Tests.Hid;

/// <summary>
/// Gallery connection badges: receiver routes map straight to their dongle kind;
/// direct devices resolve Bluetooth vs cable via the 0x0003 per-transport model-id
/// table (filled in ascending transport-bit order), falling back to Logitech's
/// 0xBxxx Bluetooth PID band when no model info was probed.
/// </summary>
public class ConnectionKindTests
{
    // The G915's real 0x0003 answer: transports usb+equad+btle, ids [b354, 407c, c33e].
    private static DeviceModelInfo G915ModelInfo => new()
    {
        EntityCount = 5,
        UnitId = [0x71, 0xb6, 0x0f, 0x82],
        Transports = new DeviceTransports { Usb = true, Equad = true, Btle = true, Bluetooth = false },
        ModelIds = [0xb354, 0x407c, 0xc33e],
        ExtendedModelId = 2,
    };

    private static PairedDevice Device(DeviceModelInfo? modelInfo = null) => new()
    {
        Slot = 0xff,
        Kind = DeviceKind.Keyboard,
        Online = true,
        ModelInfo = modelInfo,
    };

    [Theory]
    [InlineData((ushort)0xb354, ConnectionKind.Bluetooth)] // BTLE slot
    [InlineData((ushort)0xc33e, ConnectionKind.UsbCable)]  // USB slot
    public void DirectPidResolvesViaModelIdTable(ushort pid, ConnectionKind expected) =>
        Assert.Equal(expected, ConnectionKinds.For(new DeviceRoute.Direct(0x046d, pid), Device(G915ModelInfo)));

    [Theory]
    [InlineData((ushort)0xb037, ConnectionKind.Bluetooth)] // MX Anywhere 3S BLE pid
    [InlineData((ushort)0x0acf, ConnectionKind.UsbCable)]  // Yeti mic
    public void DirectPidWithoutModelInfoFallsBackToPidBand(ushort pid, ConnectionKind expected) =>
        Assert.Equal(expected, ConnectionKinds.For(new DeviceRoute.Direct(0x046d, pid), Device()));

    [Fact]
    public void ReceiverRoutesMapToTheirDongleKind()
    {
        Assert.Equal(ConnectionKind.LightspeedDongle, ConnectionKinds.For(new DeviceRoute.Lightspeed("ls-046dc547", 1), Device()));
        Assert.Equal(ConnectionKind.UnifyingDongle, ConnectionKinds.For(new DeviceRoute.Unifying("uid", 1), Device()));
        Assert.Equal(ConnectionKind.BoltDongle, ConnectionKinds.For(new DeviceRoute.Bolt("uid", 1), Device()));
    }

    [Fact]
    public void UnroutableDeviceHasUnknownConnection() =>
        Assert.Equal(ConnectionKind.Unknown, ConnectionKinds.For(null, Device()));
}
