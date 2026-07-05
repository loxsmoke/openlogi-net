using System.Buffers.Binary;
using System.Threading.Channels;
using OpenLogi.HidPP.Channel;

namespace OpenLogi.HidPP.Feature;

/// <summary>
/// The scroll wheel's mode, as reported/accepted by HiResWheel (0x2121). Encoded
/// as a single byte: bit 0 is the report target (set = diverted to HID++
/// notifications, clear = standard HID), bit 1 is the resolution (set = high-res
/// multiplier applied), bit 2 is the rotation direction (set = inverted).
/// </summary>
public readonly record struct HiResWheelMode(bool Diverted, bool HighResolution, bool Inverted)
{
    private const byte TargetBit = 0x01, ResolutionBit = 0x02, InvertBit = 0x04;

    public static HiResWheelMode FromByte(byte b) =>
        new((b & TargetBit) != 0, (b & ResolutionBit) != 0, (b & InvertBit) != 0);

    public byte ToByte() => (byte)(
        (Diverted ? TargetBit : 0) |
        (HighResolution ? ResolutionBit : 0) |
        (Inverted ? InvertBit : 0));
}

/// <summary>
/// Static wheel capability from getWheelCapability (fn 0): the hi-res multiplier
/// (hi-res ticks per physical notch), whether the wheel reports ratchet
/// engage/disengage (<see cref="HasSwitch"/>) and native invert
/// (<see cref="HasInvert"/>). Feature v1 adds the ratchet count per full rotation
/// and the wheel diameter; v0 devices report those bytes as 0.
/// </summary>
public readonly record struct HiResWheelCapability(
    byte Multiplier, bool HasSwitch, bool HasInvert, byte RatchetsPerRotation, byte WheelDiameterMm)
{
    private const byte SwitchBit = 0x02, InvertBit = 0x04;

    public static HiResWheelCapability FromPayload(ReadOnlySpan<byte> p) =>
        new(p[0], (p[1] & SwitchBit) != 0, (p[1] & InvertBit) != 0, p[2], p[3]);
}

/// <summary>An unsolicited event emitted by HiResWheel (0x2121).</summary>
public abstract record HiResWheelEvent
{
    private HiResWheelEvent() { }

    /// <summary>
    /// Wheel rotation while reporting is diverted (event 0). <see cref="DeltaV"/>
    /// is in hi-res ticks (multiplier units) when <see cref="HighResolution"/>,
    /// else in whole notches; positive is scroll-up unless the invert bit is set.
    /// </summary>
    public sealed record Movement(bool HighResolution, byte Periods, short DeltaV) : HiResWheelEvent;

    /// <summary>The ratchet engaged (true) or the wheel went free-spin (event 1, wheels with a switch).</summary>
    public sealed record RatchetSwitch(bool Ratchet) : HiResWheelEvent;
}

/// <summary>
/// The `HiResWheel` / 0x2121 feature — controls the wheel's report target
/// (standard HID vs diverted HID++ events), its resolution, and its rotation
/// direction (a native, firmware-level scroll invert); emits diverted wheel
/// movement / ratchet events. Mirrors Solaar's HIRES_WHEEL. Dispose to remove
/// the event listener.
/// </summary>
public sealed class HiResWheelFeature : ICreatableFeature<HiResWheelFeature>, IDisposable
{
    public static ushort Id => 0x2121;
    public static byte StartingVersion => 0;

    private readonly FeatureEndpoint _endpoint;
    private readonly EventEmitter<HiResWheelEvent> _emitter = new();
    private readonly IDisposable _listener;

    private HiResWheelFeature(HidppChannel channel, byte deviceIndex, byte featureIndex)
    {
        _endpoint = new FeatureEndpoint(channel, deviceIndex, featureIndex);
        _listener = channel.AddMsgListenerGuarded((raw, matched) =>
        {
            var ev = FeatureEndpoint.EventPayload(raw, matched, deviceIndex, featureIndex);
            if (ev is null) return;
            var decoded = DecodeEventPayload(ev.Value.SubId.ToLo(), ev.Value.Payload);
            if (decoded is not null) _emitter.Emit(decoded);
        });
    }

    public static HiResWheelFeature Create(HidppChannel channel, byte deviceIndex, byte featureIndex) =>
        new(channel, deviceIndex, featureIndex);

    /// <summary>Subscribe to diverted wheel-movement / ratchet events.</summary>
    public ChannelReader<HiResWheelEvent> Listen() => _emitter.CreateReceiver();

    /// <summary>The wheel's static capability — multiplier + flags (getWheelCapability, fn 0).</summary>
    public async Task<HiResWheelCapability> GetCapabilityAsync() =>
        HiResWheelCapability.FromPayload((await _endpoint.CallAsync(0, [0, 0, 0]).ConfigureAwait(false)).ExtendPayload());

    /// <summary>The current wheel mode — report target, resolution and invert (getWheelMode, fn 1).</summary>
    public async Task<HiResWheelMode> GetModeAsync() =>
        HiResWheelMode.FromByte((await _endpoint.CallAsync(1, [0, 0, 0]).ConfigureAwait(false)).ExtendPayload()[0]);

    /// <summary>Set the whole wheel mode byte (setWheelMode, fn 2).</summary>
    public async Task SetModeAsync(HiResWheelMode mode) =>
        await _endpoint.CallAsync(2, [mode.ToByte(), 0, 0]).ConfigureAwait(false);

    public void Dispose() => _listener.Dispose();

    /// <summary>
    /// Decode an unsolicited 0x2121 event payload by its sub-id. Movement (0):
    /// byte 0 carries the resolution flag (0x10) and a 4-bit report-period count;
    /// bytes 1–2 are the signed big-endian vertical delta. RatchetSwitch (1):
    /// byte 0 bit 0 is the ratchet state.
    /// </summary>
    public static HiResWheelEvent? DecodeEventPayload(byte functionId, ReadOnlySpan<byte> p) => functionId switch
    {
        0 => new HiResWheelEvent.Movement(
            (p[0] & 0x10) != 0,
            (byte)(p[0] & 0x0f),
            BinaryPrimitives.ReadInt16BigEndian(p[1..3])),
        1 => new HiResWheelEvent.RatchetSwitch((p[0] & 0x01) != 0),
        _ => null,
    };
}
