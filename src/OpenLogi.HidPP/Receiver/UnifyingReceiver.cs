using System.Buffers.Binary;
using System.Threading.Channels;
using OpenLogi.HidPP.Channel;
using OpenLogi.HidPP.Feature;
using OpenLogi.HidPP.Protocol;

namespace OpenLogi.HidPP.Receiver;

/// <summary>General information about a Unifying receiver.</summary>
public readonly record struct UnifyingReceiverInfo(string SerialNumber, byte PairingSlots);

/// <summary>A device connected to a Unifying receiver.</summary>
public readonly record struct UnifyingDeviceConnection(byte Index, UnifyingDeviceKind Kind, bool Encrypted, bool Online, ushort Wpid);

/// <summary>Pairing-register information about a Unifying-paired device.</summary>
public readonly record struct UnifyingDevicePairingInformation(ushort Wpid, UnifyingDeviceKind Kind, bool Encrypted, bool Online, byte[] UnitId);

/// <summary>
/// The Logitech Unifying receiver (HID++ 1.0). Ported from Rust <c>receiver::unifying</c>.
/// Also serves LIGHTSPEED receivers (G-series gaming dongles), which speak the same
/// HID++ 1.0 register set — only the USB PID differs.
/// </summary>
public sealed class UnifyingReceiver : IDisposable
{
    /// <summary>USB VID/PID pairs identifying Unifying receivers.</summary>
    public static readonly (ushort Vid, ushort Pid)[] VpidPairs = [(0x046d, 0xc52b), (0x046d, 0xc532)];

    /// <summary>
    /// USB VID/PID pairs identifying LIGHTSPEED receivers (per Solaar's receiver
    /// table). 0xc53a is the Powerplay charging mat's embedded receiver.
    /// </summary>
    public static readonly (ushort Vid, ushort Pid)[] LightspeedVpidPairs =
    [
        (0x046d, 0xc539), (0x046d, 0xc53a), (0x046d, 0xc53f),
        (0x046d, 0xc541), (0x046d, 0xc545), (0x046d, 0xc547),
    ];

    private const byte ReceiverDeviceIndex = 0xff;
    private const byte RegNotifications = 0x00;
    private const byte RegConnections = 0x02;
    private const byte RegReceiverInfo = 0xb5;
    private const byte SubReceiverInfo = 0x03;
    private const byte SubDeviceCodename = 0x40;
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

    /// <summary>Create a receiver for a LIGHTSPEED dongle (same register set) if the channel's VID/PID matches; otherwise <c>null</c>.</summary>
    public static UnifyingReceiver? TryCreateLightspeed(HidppChannel channel) =>
        LightspeedVpidPairs.Contains((channel.VendorId, channel.ProductId)) ? new UnifyingReceiver(channel) : null;

    /// <summary>Subscribe to device-connection events.</summary>
    public ChannelReader<UnifyingDeviceConnection> Listen() => _emitter.CreateReceiver();

    /// <summary>The number of devices currently paired.</summary>
    public async Task<byte> CountPairingsAsync() =>
        (await _channel.ReadRegisterAsync(ReceiverDeviceIndex, RegConnections, [0, 0, 0]).ConfigureAwait(false))[1];

    /// <summary>Enable/disable wireless device notifications (see <see cref="BoltReceiver.SetWirelessNotificationsAsync"/>).</summary>
    public Task SetWirelessNotificationsAsync(bool enabled) =>
        _channel.WriteRegisterAsync(ReceiverDeviceIndex, RegNotifications, [0, (byte)(enabled ? 1 : 0), 0]);

    /// <summary>Trigger device-arrival notifications for all connected devices.</summary>
    public Task TriggerDeviceArrivalAsync() =>
        _channel.WriteRegisterAsync(ReceiverDeviceIndex, RegConnections, [0x02, 0x00, 0x00]);

    /// <summary>Collect all paired devices via device-arrival (see <see cref="BoltReceiver.CollectPairedDevicesAsync"/>).</summary>
    public async Task<List<UnifyingDeviceConnection>> CollectPairedDevicesAsync()
    {
        var rx = Listen();
        await TriggerDeviceArrivalAsync().ConfigureAwait(false);
        // The 0x41 arrival notifications land after (not before) the trigger
        // write's ACK, so returning on the ACK would collect nothing. Keep
        // draining until the receiver stays quiet; duplicate arrivals for a
        // slot are collapsed (last wins).
        var bySlot = new Dictionary<byte, UnifyingDeviceConnection>();
        while (true)
        {
            using var quiet = new CancellationTokenSource(Receivers.ArrivalQuietPeriod);
            try
            {
                var conn = await rx.ReadAsync(quiet.Token).ConfigureAwait(false);
                bySlot[conn.Index] = conn;
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        return [.. bySlot.Values];
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

    /// <summary>
    /// The codename of the device at <paramref name="deviceIndex"/> (e.g. "G915"),
    /// read from the extended pairing registers — available even while the
    /// device itself is offline.
    /// </summary>
    public async Task<string> GetDeviceCodenameAsync(byte deviceIndex)
    {
        var r = await _channel.ReadLongRegisterAsync(ReceiverDeviceIndex, RegReceiverInfo,
            [(byte)(SubDeviceCodename | (deviceIndex & 0x0f)), 0x00, 0x00]).ConfigureAwait(false);
        return ParseCodename(r) ?? throw Hidpp10Exception.UnsupportedResponse();
    }

    /// <summary>
    /// Extract the codename from an extended-pairing-information (0xb5/0x4n)
    /// register read: response[1] is the device-reported length, clamped to the
    /// bytes present from response[2]. Unlike Bolt's, the whole name sits in one
    /// packet (no chunking). Empty or non-UTF-8 yields <c>null</c>.
    /// </summary>
    public static string? ParseCodename(ReadOnlySpan<byte> response)
    {
        if (response.Length < 3) return null;
        var end = Math.Min(2 + response[1], response.Length);
        if (end <= 2) return null;
        var name = BoltReceiver.TryUtf8(response[2..end])?.TrimEnd('\0');
        return string.IsNullOrWhiteSpace(name) ? null : name;
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

    /// <summary>A LIGHTSPEED (G-series) receiver — Unifying's register set behind a different PID.</summary>
    public sealed record Lightspeed(UnifyingReceiver Receiver) : DetectedReceiver;

    /// <summary>A human-readable receiver name.</summary>
    public string Name => this switch
    {
        Bolt => "Logi Bolt Receiver",
        Unifying => "Unifying Receiver",
        Lightspeed => "Lightspeed Receiver",
        _ => "Receiver",
    };
}

/// <summary>Receiver detection by USB VID/PID. Ported from Rust <c>receiver::detect</c>.</summary>
public static class Receivers
{
    /// <summary>
    /// How long device-arrival collection lets the receiver stay silent before
    /// concluding all paired devices have announced themselves. Notifications
    /// for the whole pairing table arrive within a few milliseconds of each
    /// other, so this is generous while keeping a zero-device sweep fast.
    /// </summary>
    public static readonly TimeSpan ArrivalQuietPeriod = TimeSpan.FromMilliseconds(500);

    /// <summary>Detect the receiver on a channel, or <c>null</c> if none is recognised.</summary>
    public static DetectedReceiver? Detect(HidppChannel channel)
    {
        if (BoltReceiver.TryCreate(channel) is { } bolt) return new DetectedReceiver.Bolt(bolt);
        if (UnifyingReceiver.TryCreate(channel) is { } uni) return new DetectedReceiver.Unifying(uni);
        if (UnifyingReceiver.TryCreateLightspeed(channel) is { } ls) return new DetectedReceiver.Lightspeed(ls);
        return null;
    }

    /// <summary>Whether this VID/PID is a known wireless receiver (Bolt / Unifying / LIGHTSPEED).</summary>
    public static bool IsReceiverPid(ushort vid, ushort pid) =>
        BoltReceiver.VpidPairs.Contains((vid, pid))
        || UnifyingReceiver.VpidPairs.Contains((vid, pid))
        || UnifyingReceiver.LightspeedVpidPairs.Contains((vid, pid));

    /// <summary>
    /// Stable stand-in uid for LIGHTSPEED receivers, which don't implement the
    /// 0xb5/03 serial register — the read just times out (HARDWARE-VERIFIED on a
    /// G915 dongle, 046d:c547). VID/PID-based, so two identical dongles on one
    /// host aren't distinguishable; acceptable until a real serial source is found.
    /// </summary>
    public static string LightspeedSyntheticUid(HidppChannel channel) =>
        $"ls-{channel.VendorId:x4}{channel.ProductId:x4}";
}
