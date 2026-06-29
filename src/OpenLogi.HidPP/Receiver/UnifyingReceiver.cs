using System.Buffers.Binary;
using System.Threading.Channels;
using OpenLogi.HidPP.Channel;
using OpenLogi.HidPP.Feature;
using OpenLogi.HidPP.Protocol;

namespace OpenLogi.HidPP.Receiver;

/// <summary>
/// The kind of a device paired to a Unifying receiver. Matches Bolt for 1–4;
/// from 5 it uses a shifted table. Ported from Rust <c>receiver::unifying::DeviceKind</c>.
/// </summary>
public enum UnifyingDeviceKind : byte
{
    Unknown = 0x00, Keyboard = 0x01, Mouse = 0x02, Numpad = 0x03, Presenter = 0x04,
    Remote = 0x05, Trackball = 0x06, Touchpad = 0x07,
}

/// <summary>General information about a Unifying receiver.</summary>
public readonly record struct UnifyingReceiverInfo(string SerialNumber, byte PairingSlots);

/// <summary>A device connected to a Unifying receiver.</summary>
public readonly record struct UnifyingDeviceConnection(byte Index, UnifyingDeviceKind Kind, bool Encrypted, bool Online, ushort Wpid);

/// <summary>Pairing-register information about a Unifying-paired device.</summary>
public readonly record struct UnifyingDevicePairingInformation(ushort Wpid, UnifyingDeviceKind Kind, bool Encrypted, bool Online, byte[] UnitId);

/// <summary>The Logitech Unifying receiver (HID++ 1.0). Ported from Rust <c>receiver::unifying</c>.</summary>
public sealed class UnifyingReceiver : IDisposable
{
    /// <summary>USB VID/PID pairs identifying Unifying receivers.</summary>
    public static readonly (ushort Vid, ushort Pid)[] VpidPairs = [(0x046d, 0xc52b), (0x046d, 0xc532)];

    private const byte ReceiverDeviceIndex = 0xff;
    private const byte RegConnections = 0x02;
    private const byte RegReceiverInfo = 0xb5;
    private const byte SubReceiverInfo = 0x03;
    private const byte SubDevicePairingInformation = 0x50;

    private readonly HidppChannel _channel;
    private readonly EventEmitter<UnifyingDeviceConnection> _emitter = new();
    private readonly IDisposable _listener;

    private UnifyingReceiver(HidppChannel channel)
    {
        _channel = channel;
        _listener = channel.AddMsgListenerGuarded((raw, matched) =>
        {
            if (matched) return;
            var msg = V10Message.FromHidpp(raw);
            if (msg.Header.SubId != 0x41) return;
            var p = msg.ExtendPayload();
            if (!Enum.IsDefined(typeof(UnifyingDeviceKind), (byte)(p[1] & 0x0f))) return;
            _emitter.Emit(new UnifyingDeviceConnection(
                msg.Header.DeviceIndex,
                (UnifyingDeviceKind)(p[1] & 0x0f),
                Encrypted: (p[1] & (1 << 4)) != 0,
                Online: (p[1] & (1 << 6)) == 0,
                Wpid: BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(2, 2))));
        });
    }

    /// <summary>Create a Unifying receiver if the channel's VID/PID matches; otherwise <c>null</c>.</summary>
    public static UnifyingReceiver? TryCreate(HidppChannel channel) =>
        VpidPairs.Contains((channel.VendorId, channel.ProductId)) ? new UnifyingReceiver(channel) : null;

    /// <summary>Subscribe to device-connection events.</summary>
    public ChannelReader<UnifyingDeviceConnection> Listen() => _emitter.CreateReceiver();

    /// <summary>The number of devices currently paired.</summary>
    public async Task<byte> CountPairingsAsync() =>
        (await _channel.ReadRegisterAsync(ReceiverDeviceIndex, RegConnections, [0, 0, 0]).ConfigureAwait(false))[1];

    /// <summary>Trigger device-arrival notifications for all connected devices.</summary>
    public Task TriggerDeviceArrivalAsync() =>
        _channel.WriteRegisterAsync(ReceiverDeviceIndex, RegConnections, [0x02, 0x00, 0x00]);

    /// <summary>Collect all paired devices via device-arrival (see <see cref="BoltReceiver.CollectPairedDevicesAsync"/>).</summary>
    public async Task<List<UnifyingDeviceConnection>> CollectPairedDevicesAsync()
    {
        var rx = Listen();
        var devices = new List<UnifyingDeviceConnection>();
        var trigger = TriggerDeviceArrivalAsync();
        while (true)
        {
            var recv = rx.ReadAsync().AsTask();
            var done = await Task.WhenAny(trigger, recv).ConfigureAwait(false);
            if (done == trigger)
            {
                await trigger.ConfigureAwait(false);
                while (rx.TryRead(out var c)) devices.Add(c);
                break;
            }
            devices.Add(await recv.ConfigureAwait(false));
        }
        return devices;
    }

    /// <summary>General receiver info (serial number + pairing slot count).</summary>
    public async Task<UnifyingReceiverInfo> GetReceiverInfoAsync()
    {
        var r = await _channel.ReadLongRegisterAsync(ReceiverDeviceIndex, RegReceiverInfo, [SubReceiverInfo, 0, 0]).ConfigureAwait(false);
        return new UnifyingReceiverInfo(Convert.ToHexString(r.AsSpan(1, 4)), r[6]);
    }

    /// <summary>Pairing information for the device at <paramref name="deviceIndex"/>.</summary>
    public async Task<UnifyingDevicePairingInformation> GetDevicePairingInformationAsync(byte deviceIndex)
    {
        var r = await _channel.ReadLongRegisterAsync(ReceiverDeviceIndex, RegReceiverInfo,
            [(byte)(SubDevicePairingInformation | (deviceIndex & 0x0f)), 0x00, 0x00]).ConfigureAwait(false);
        if (!Enum.IsDefined(typeof(UnifyingDeviceKind), (byte)(r[1] & 0x0f)))
            throw Hidpp10Exception.UnsupportedResponse();
        return new UnifyingDevicePairingInformation(
            BinaryPrimitives.ReadUInt16LittleEndian(r.AsSpan(2, 2)),
            (UnifyingDeviceKind)(r[1] & 0x0f),
            Encrypted: (r[1] & (1 << 4)) != 0,
            Online: (r[1] & (1 << 6)) == 0,
            UnitId: r[4..8]);
    }

    /// <summary>The receiver's unique ID (its serial number).</summary>
    public async Task<string> GetUniqueIdAsync() => (await GetReceiverInfoAsync().ConfigureAwait(false)).SerialNumber;

    public void Dispose() => _listener.Dispose();
}

/// <summary>A detected wireless receiver on a channel.</summary>
public abstract record DetectedReceiver
{
    private DetectedReceiver() { }
    public sealed record Bolt(BoltReceiver Receiver) : DetectedReceiver;
    public sealed record Unifying(UnifyingReceiver Receiver) : DetectedReceiver;

    /// <summary>A human-readable receiver name.</summary>
    public string Name => this switch
    {
        Bolt => "Logi Bolt Receiver",
        Unifying => "Unifying Receiver",
        _ => "Receiver",
    };
}

/// <summary>Receiver detection by USB VID/PID. Ported from Rust <c>receiver::detect</c>.</summary>
public static class Receivers
{
    /// <summary>Detect the receiver on a channel, or <c>null</c> if none is recognised.</summary>
    public static DetectedReceiver? Detect(HidppChannel channel)
    {
        if (BoltReceiver.TryCreate(channel) is { } bolt) return new DetectedReceiver.Bolt(bolt);
        if (UnifyingReceiver.TryCreate(channel) is { } uni) return new DetectedReceiver.Unifying(uni);
        return null;
    }
}
