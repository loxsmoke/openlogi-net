using OpenLogi.HidPP.Channel;
using OpenLogi.HidPP.Receiver;

namespace OpenLogi.Tests.HidPP;

/// <summary>
/// Paired-device collection. The 0x41 device-arrival notifications land
/// <em>after</em> the trigger write's ACK (regression: collection returned as
/// soon as the ACK arrived, before any notification, so receivers reported
/// zero paired devices).
/// </summary>
public class ReceiverCollectTests
{
    // Short SetRegister ACK for the connections register (0x02): [device, sub-id, address, ...].
    private static readonly HidppMessage TriggerAck =
        HidppMessage.Short([0xff, 0x80, 0x02, 0x00, 0x00, 0x00]);

    /// <summary>A 0x41 device-arrival notification: [slot, 0x41, protocol, info, wpid lo, wpid hi].</summary>
    private static HidppMessage Arrival(byte slot, byte kind, ushort wpid) =>
        HidppMessage.Short([slot, 0x41, 0x04, kind, (byte)(wpid & 0xff), (byte)(wpid >> 8)]);

    [Fact]
    public async Task CollectsNotificationsArrivingAfterTriggerAck()
    {
        var raw = new MockRawHidChannel { OnWrite = _ => TriggerAck };
        await using var channel = await HidppChannel.FromRawChannelAsync(raw);
        // The mock reports PID 0xc539, a LIGHTSPEED dongle (Unifying register set).
        using var receiver = UnifyingReceiver.TryCreateLightspeed(channel);
        Assert.NotNull(receiver);

        var collect = receiver.CollectPairedDevicesAsync();
        await Task.Delay(100); // notifications trail the ACK by a few milliseconds
        raw.SendIncoming(Arrival(slot: 1, kind: (byte)UnifyingDeviceKind.Mouse, wpid: 0x407b)); // MX Vertical
        raw.SendIncoming(Arrival(slot: 2, kind: (byte)UnifyingDeviceKind.Keyboard, wpid: 0x4023));
        raw.SendIncoming(Arrival(slot: 1, kind: (byte)UnifyingDeviceKind.Mouse, wpid: 0x407b)); // duplicate slot

        var devices = await collect;

        Assert.Equal(2, devices.Count);
        var mouse = Assert.Single(devices, d => d.Index == 1);
        Assert.Equal(UnifyingDeviceKind.Mouse, mouse.Kind);
        Assert.Equal(0x407b, mouse.Wpid);
        Assert.True(mouse.Online);
        Assert.Equal(UnifyingDeviceKind.Keyboard, Assert.Single(devices, d => d.Index == 2).Kind);
    }

    [Fact]
    public async Task EmptyReceiverCollectsNothingAfterQuietPeriod()
    {
        var raw = new MockRawHidChannel { OnWrite = _ => TriggerAck };
        await using var channel = await HidppChannel.FromRawChannelAsync(raw);
        using var receiver = UnifyingReceiver.TryCreateLightspeed(channel);
        Assert.NotNull(receiver);

        Assert.Empty(await receiver.CollectPairedDevicesAsync());
    }
}
