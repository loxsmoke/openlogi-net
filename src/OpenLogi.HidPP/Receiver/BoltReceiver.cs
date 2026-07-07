using System.Buffers.Binary;
using System.Threading.Channels;
using OpenLogi.HidPP.Channel;
using OpenLogi.HidPP.Feature;
using OpenLogi.HidPP.Protocol;

namespace OpenLogi.HidPP.Receiver;

/// <summary>The kind of a device paired to a Bolt receiver. Ported from Rust <c>receiver::bolt::DeviceKind</c>.</summary>
public enum BoltDeviceKind : byte
{
    Unknown = 0x00, Keyboard = 0x01, Mouse = 0x02, Numpad = 0x03, Presenter = 0x04,
    Remote = 0x07, Trackball = 0x08, Touchpad = 0x09, Tablet = 0x0a, Gamepad = 0x0b,
    Joystick = 0x0c, Headset = 0x0d,
}

/// <summary>A device connected to a Bolt receiver (from a device-arrival notification).</summary>
public readonly record struct BoltDeviceConnection(byte Index, BoltDeviceKind Kind, bool Encrypted, bool Online, ushort Wpid);

/// <summary>Pairing-register information about a paired device.</summary>
public readonly record struct BoltDevicePairingInformation(ushort Wpid, BoltDeviceKind Kind, bool Encrypted, bool Online, byte[] UnitId);

/// <summary>
/// The Logi Bolt receiver (HID++ 1.0 registers). Ported from Rust
/// <c>receiver::bolt</c>, focused on enumeration. Discovery/pairing event
/// decoding (sub-ids 0x4f/0x53/0x54/0x4d/0x4e) is deferred to the pairing UI work.
/// </summary>
public sealed class BoltReceiver : IDisposable
{
    /// <summary>USB VID/PID pairs identifying Bolt receivers.</summary>
    public static readonly (ushort Vid, ushort Pid)[] VpidPairs = [(0x046d, 0xc548)];

    private const byte ReceiverDeviceIndex = 0xff;
    private const byte RegNotifications = 0x00;
    private const byte RegConnections = 0x02;
    private const byte RegReceiverInfo = 0xb5;
    private const byte RegUniqueId = 0xfb;
    private const byte SubDevicePairingInformation = 0x50;
    private const byte SubDeviceCodename = 0x60;

    private readonly HidppChannel _channel;
    private readonly EventEmitter<BoltDeviceConnection> _emitter = new();
    private readonly IDisposable _listener;

    private BoltReceiver(HidppChannel channel)
    {
        _channel = channel;
        _listener = channel.AddMsgListenerGuarded((raw, matched) =>
        {
            if (matched) return;
            var msg = V10Message.FromHidpp(raw);
            if (msg.Header.SubId != 0x41) return; // device connection
            var p = msg.ExtendPayload();
            if (!Enum.IsDefined(typeof(BoltDeviceKind), (byte)(p[1] & 0x0f))) return;
            _emitter.Emit(new BoltDeviceConnection(
                msg.Header.DeviceIndex,
                (BoltDeviceKind)(p[1] & 0x0f),
                Encrypted: (p[1] & (1 << 5)) != 0,
                Online: (p[1] & (1 << 6)) == 0,
                Wpid: BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(2, 2))));
        });
    }

    /// <summary>Create a Bolt receiver if the channel's VID/PID matches; otherwise <c>null</c>.</summary>
    public static BoltReceiver? TryCreate(HidppChannel channel) =>
        VpidPairs.Contains((channel.VendorId, channel.ProductId)) ? new BoltReceiver(channel) : null;

    /// <summary>Subscribe to device-connection events.</summary>
    public ChannelReader<BoltDeviceConnection> Listen() => _emitter.CreateReceiver();

    /// <summary>The number of devices currently paired (offline devices included).</summary>
    public async Task<byte> CountPairingsAsync() =>
        (await _channel.ReadRegisterAsync(ReceiverDeviceIndex, RegConnections, [0, 0, 0]).ConfigureAwait(false))[1];

    /// <summary>Whether wireless device-arrival/removal notifications are enabled.</summary>
    public async Task<bool> GetWirelessNotificationsAsync() =>
        ((await _channel.ReadRegisterAsync(ReceiverDeviceIndex, RegNotifications, [0, 0, 0]).ConfigureAwait(false))[1] & 1) != 0;

    /// <summary>Enable/disable wireless device notifications.</summary>
    public Task SetWirelessNotificationsAsync(bool enabled) =>
        _channel.WriteRegisterAsync(ReceiverDeviceIndex, RegNotifications, [0, (byte)(enabled ? 1 : 0), 0]);

    /// <summary>Trigger device-arrival notifications for all currently connected devices.</summary>
    public Task TriggerDeviceArrivalAsync() =>
        _channel.WriteRegisterAsync(ReceiverDeviceIndex, RegConnections, [0x02, 0x00, 0x00]);

    /// <summary>
    /// Collect all paired devices by triggering device arrival and draining the
    /// connection notifications until the receiver stays quiet.
    /// </summary>
    public async Task<List<BoltDeviceConnection>> CollectPairedDevicesAsync()
    {
        var rx = Listen();
        await TriggerDeviceArrivalAsync().ConfigureAwait(false);
        // The 0x41 arrival notifications land after (not before) the trigger
        // write's ACK, so returning on the ACK would collect nothing. Keep
        // draining until the receiver stays quiet; duplicate arrivals for a
        // slot are collapsed (last wins).
        var bySlot = new Dictionary<byte, BoltDeviceConnection>();
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

    /// <summary>The receiver's unique ID (not the serial number).</summary>
    public async Task<string> GetUniqueIdAsync()
    {
        var response = await _channel.ReadLongRegisterAsync(ReceiverDeviceIndex, RegUniqueId, [0, 0, 0]).ConfigureAwait(false);
        return System.Text.Encoding.ASCII.GetString(response).TrimEnd('\0');
    }

    /// <summary>Pairing information for the device at <paramref name="deviceIndex"/>.</summary>
    public async Task<BoltDevicePairingInformation> GetDevicePairingInformationAsync(byte deviceIndex)
    {
        var r = await _channel.ReadLongRegisterAsync(ReceiverDeviceIndex, RegReceiverInfo,
            [(byte)(SubDevicePairingInformation + (deviceIndex & 0x0f)), 0x00, 0x00]).ConfigureAwait(false);
        if (!Enum.IsDefined(typeof(BoltDeviceKind), (byte)(r[1] & 0x0f)))
            throw Hidpp10Exception.UnsupportedResponse();
        return new BoltDevicePairingInformation(
            BinaryPrimitives.ReadUInt16LittleEndian(r.AsSpan(2, 2)),
            (BoltDeviceKind)(r[1] & 0x0f),
            Encrypted: (r[1] & (1 << 5)) != 0,
            Online: (r[1] & (1 << 6)) == 0,
            UnitId: r[4..8]);
    }

    /// <summary>The codename of the device at <paramref name="deviceIndex"/>.</summary>
    public async Task<string> GetDeviceCodenameAsync(byte deviceIndex)
    {
        var r = await _channel.ReadLongRegisterAsync(ReceiverDeviceIndex, RegReceiverInfo,
            [(byte)(SubDeviceCodename + (deviceIndex & 0x0f)), 0x01, 0x00]).ConfigureAwait(false);
        return ParseCodename(r) ?? throw Hidpp10Exception.UnsupportedResponse();
    }

    public void Dispose() => _listener.Dispose();

    /// <summary>
    /// Extract the codename chunk from a DeviceCodename register read. response[2]
    /// is the device-reported length, clamped to the 13-byte chunk present; non-UTF-8
    /// yields <c>null</c>. Ported from Rust <c>parse_codename</c>.
    /// </summary>
    public static string? ParseCodename(ReadOnlySpan<byte> response)
    {
        var end = Math.Min(3 + response[2], response.Length);
        if (end < 3) return null;
        return TryUtf8(response[3..end]);
    }

    /// <summary>
    /// Parse a device-discovery name notification. payload[3] is the name length;
    /// a length past the packet or non-UTF-8 bytes drop the event. Ported from
    /// Rust <c>parse_discovery_name</c>.
    /// </summary>
    public static (ushort Counter, string Name)? ParseDiscoveryName(ReadOnlySpan<byte> payload)
    {
        int len = payload[3];
        var end = 4 + len;
        if (end > payload.Length) return null;
        var name = TryUtf8(payload[4..end]);
        return name is null ? null : ((ushort)(payload[0] + payload[1] * 256), name);
    }

    private static readonly System.Text.UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static string? TryUtf8(ReadOnlySpan<byte> bytes)
    {
        try { return StrictUtf8.GetString(bytes); }
        catch (System.Text.DecoderFallbackException) { return null; }
    }
}
