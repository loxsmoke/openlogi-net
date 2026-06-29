using System.Buffers.Binary;
using System.Threading.Channels;
using OpenLogi.HidPP.Channel;

namespace OpenLogi.HidPP.Feature;

/// <summary>A HID++ control ID.</summary>
public readonly record struct ControlId(ushort Value)
{
    public static ControlId FromPayload(ReadOnlySpan<byte> bytes) =>
        new(BinaryPrimitives.ReadUInt16BigEndian(bytes));
}

/// <summary>A HID++ task ID.</summary>
public readonly record struct TaskId(ushort Value);

/// <summary>Group mask g1..g8 from getCidInfo.</summary>
public readonly record struct GroupMask(byte Value);

/// <summary>Capability and classification flags for one control ID. Ported from Rust <c>CidFlags</c>.</summary>
[Flags]
public enum CidFlags : ushort
{
    None = 0,
    Mouse = 1 << 0,
    FunctionKey = 1 << 1,
    Hotkey = 1 << 2,
    FnToggle = 1 << 3,
    Reprogrammable = 1 << 4,
    Divertable = 1 << 5,
    PersistentlyDivertable = 1 << 6,
    VirtualControl = 1 << 7,
    RawXy = 1 << 8,
    ForceRawXy = 1 << 9,
    AnalyticsKeyEvents = 1 << 10,
    RawWheel = 1 << 11,
}

public static class CidFlagsExtensions
{
    /// <summary>Combine the primary (low) and additional (high) flag bytes.</summary>
    public static CidFlags FromBytes(byte primary, byte additional) =>
        (CidFlags)(primary | (additional << 8));

    /// <summary>The raw 16-bit value (primary in the low byte, additional in the high byte).</summary>
    public static ushort Raw(this CidFlags flags) => (ushort)flags;

    public static bool IsMouse(this CidFlags f) => f.HasFlag(CidFlags.Mouse);
    public static bool IsDivertable(this CidFlags f) => f.HasFlag(CidFlags.Divertable);
    public static bool IsPersistentlyDivertable(this CidFlags f) => f.HasFlag(CidFlags.PersistentlyDivertable);
    public static bool IsVirtualControl(this CidFlags f) => f.HasFlag(CidFlags.VirtualControl);
    public static bool SupportsRawXy(this CidFlags f) => f.HasFlag(CidFlags.RawXy);
    public static bool SupportsForceRawXy(this CidFlags f) => f.HasFlag(CidFlags.ForceRawXy);
    public static bool SupportsAnalyticsKeyEvents(this CidFlags f) => f.HasFlag(CidFlags.AnalyticsKeyEvents);
    public static bool SupportsRawWheel(this CidFlags f) => f.HasFlag(CidFlags.RawWheel);
}

/// <summary>One getCidInfo row.</summary>
public readonly record struct CidInfo(
    ControlId Cid, TaskId TaskId, CidFlags Flags, byte Position, byte Group, GroupMask GroupMask)
{
    public static CidInfo FromPayload(ReadOnlySpan<byte> p) => new(
        ControlId.FromPayload(p[0..2]),
        new TaskId(BinaryPrimitives.ReadUInt16BigEndian(p[2..4])),
        CidFlagsExtensions.FromBytes(p[4], p[8]),
        p[5], p[6], new GroupMask(p[7]));
}

/// <summary>Current reporting/remapping state returned by getCidReporting.</summary>
public readonly record struct CidReporting(
    ControlId Cid, bool Diverted, bool PersistentlyDiverted, bool ForceRawXy, bool RawXy,
    ControlId? Remap, bool AnalyticsKeyEvents, bool RawWheel)
{
    public static CidReporting FromPayload(ReadOnlySpan<byte> p)
    {
        var remap = ControlId.FromPayload(p[3..5]);
        return new CidReporting(
            ControlId.FromPayload(p[0..2]),
            Diverted: (p[2] & (1 << 0)) != 0,
            PersistentlyDiverted: (p[2] & (1 << 2)) != 0,
            RawXy: (p[2] & (1 << 4)) != 0,
            ForceRawXy: (p[2] & (1 << 6)) != 0,
            Remap: remap.Value != 0 ? remap : null,
            AnalyticsKeyEvents: (p[5] & (1 << 0)) != 0,
            RawWheel: (p[5] & (1 << 2)) != 0);
    }
}

/// <summary>Changes for setCidReporting. A null boolean means "leave unchanged".</summary>
public readonly record struct CidReportingChange(
    bool? Diverted = null, bool? PersistentlyDiverted = null, bool? ForceRawXy = null, bool? RawXy = null,
    ControlId? Remap = null, bool? AnalyticsKeyEvents = null, bool? RawWheel = null)
{
    /// <summary>Change only the temporary diverted / raw-XY bits.</summary>
    public static CidReportingChange TemporaryDiversion(bool diverted, bool rawXy) =>
        new(Diverted: diverted, RawXy: rawXy);

    public byte[] ToPayload(ControlId cid)
    {
        var payload = new byte[16];
        BinaryPrimitives.WriteUInt16BigEndian(payload, cid.Value);
        if (Diverted is { } d) { payload[2] |= 1 << 1; payload[2] |= (byte)(d ? 1 : 0); }
        if (PersistentlyDiverted is { } pd) { payload[2] |= 1 << 3; payload[2] |= (byte)((pd ? 1 : 0) << 2); }
        if (RawXy is { } rx) { payload[2] |= 1 << 5; payload[2] |= (byte)((rx ? 1 : 0) << 4); }
        if (ForceRawXy is { } frx) { payload[2] |= 1 << 7; payload[2] |= (byte)((frx ? 1 : 0) << 6); }
        if (Remap is { } remap) BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(3), remap.Value);
        if (AnalyticsKeyEvents is { } ak) { payload[5] |= 1 << 1; payload[5] |= (byte)(ak ? 1 : 0); }
        if (RawWheel is { } rw) { payload[5] |= 1 << 3; payload[5] |= (byte)((rw ? 1 : 0) << 2); }
        return payload;
    }
}

/// <summary>Echo returned by setCidReporting.</summary>
public readonly record struct CidReportingChangeEcho(
    ControlId Cid, bool? Diverted, bool? PersistentlyDiverted, bool? ForceRawXy, bool? RawXy,
    ControlId? Remap, bool? AnalyticsKeyEvents, bool? RawWheel)
{
    private static bool? Bit(byte b, int validBit, int valueBit) =>
        (b & (1 << validBit)) != 0 ? (b & (1 << valueBit)) != 0 : null;

    public static CidReportingChangeEcho FromPayload(ReadOnlySpan<byte> p)
    {
        var remap = ControlId.FromPayload(p[3..5]);
        return new CidReportingChangeEcho(
            ControlId.FromPayload(p[0..2]),
            Diverted: Bit(p[2], 1, 0),
            PersistentlyDiverted: Bit(p[2], 3, 2),
            RawXy: Bit(p[2], 5, 4),
            ForceRawXy: Bit(p[2], 7, 6),
            Remap: remap.Value != 0 ? remap : null,
            AnalyticsKeyEvents: Bit(p[5], 1, 0),
            RawWheel: Bit(p[5], 3, 2));
    }
}

/// <summary>Feature-level capabilities returned by getCapabilities on v6 devices.</summary>
public readonly record struct ReprogControlsCapabilities(bool ResetAllCidReportSettings);

/// <summary>One analytics key event entry.</summary>
public readonly record struct AnalyticsKeyEvent(ControlId Cid, byte Event)
{
    public static AnalyticsKeyEvent FromPayload(ReadOnlySpan<byte> bytes) =>
        new(ControlId.FromPayload(bytes[0..2]), bytes[2]);
}

/// <summary>Raw wheel movement resolution.</summary>
public enum RawWheelResolution : byte { Low = 0, High = 1 }

/// <summary>An event emitted by 0x1b04. Ported from Rust <c>reprog_controls::event</c>.</summary>
public abstract record ReprogControlsEvent
{
    private ReprogControlsEvent() { }

    /// <summary>Up to four currently pressed diverted controls.</summary>
    public sealed record DivertedButtons(ControlId[] Controls) : ReprogControlsEvent
    {
        public bool Equals(DivertedButtons? other) => other is not null && Controls.AsSpan().SequenceEqual(other.Controls);
        public override int GetHashCode() { var h = new HashCode(); foreach (var c in Controls) h.Add(c); return h.ToHashCode(); }
    }

    /// <summary>Raw pointer movement while a diverted control is held.</summary>
    public sealed record DivertedRawMouseXy(short Dx, short Dy) : ReprogControlsEvent;

    /// <summary>Batch of analytics key event entries.</summary>
    public sealed record AnalyticsKeyEvents(AnalyticsKeyEvent[] Events) : ReprogControlsEvent
    {
        public bool Equals(AnalyticsKeyEvents? other) => other is not null && Events.AsSpan().SequenceEqual(other.Events);
        public override int GetHashCode() { var h = new HashCode(); foreach (var e in Events) h.Add(e); return h.ToHashCode(); }
    }

    /// <summary>Raw wheel movement while a diverted control is held.</summary>
    public sealed record DivertedRawWheel(RawWheelResolution Resolution, U4 Periods, short DeltaVertical) : ReprogControlsEvent;

    /// <summary>Whether <paramref name="cid"/> is currently pressed in a diverted-buttons event.</summary>
    public bool IsPressed(ControlId cid) => this is DivertedButtons db && db.Controls.Contains(cid);
}

/// <summary>
/// The `SpecialKeysMseButtons` / 0x1b04 feature — enumerate, divert, and remap
/// controls; emit diverted-button / raw-XY / analytics / raw-wheel events.
/// Ported from Rust <c>feature::reprog_controls</c>. Dispose to remove the listener.
///
/// Note: the Rust control_ids/task_ids human-label tables are diagnostics-only
/// and not ported.
/// </summary>
public sealed class ReprogControlsFeature : ICreatableFeature<ReprogControlsFeature>, IDisposable
{
    public static ushort Id => 0x1b04;
    public static byte StartingVersion => 0;

    private readonly FeatureEndpoint _endpoint;
    private readonly EventEmitter<ReprogControlsEvent> _emitter = new();
    private readonly IDisposable _listener;

    private ReprogControlsFeature(HidppChannel channel, byte deviceIndex, byte featureIndex)
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

    public static ReprogControlsFeature Create(HidppChannel channel, byte deviceIndex, byte featureIndex) =>
        new(channel, deviceIndex, featureIndex);

    /// <summary>Subscribe to diverted-control events.</summary>
    public ChannelReader<ReprogControlsEvent> Listen() => _emitter.CreateReceiver();

    /// <summary>The number of rows in the control ID table.</summary>
    public async Task<byte> GetCountAsync() =>
        (await _endpoint.CallAsync(0, [0, 0, 0]).ConfigureAwait(false)).ExtendPayload()[0];

    /// <summary>One row from the control ID table.</summary>
    public async Task<CidInfo> GetCidInfoAsync(byte index)
    {
        var args = new byte[16];
        args[0] = index;
        return CidInfo.FromPayload((await _endpoint.CallLongAsync(1, args).ConfigureAwait(false)).ExtendPayload());
    }

    /// <summary>The current reporting/remapping state for <paramref name="cid"/>.</summary>
    public async Task<CidReporting> GetCidReportingAsync(ControlId cid)
    {
        Span<byte> args = stackalloc byte[3];
        BinaryPrimitives.WriteUInt16BigEndian(args, cid.Value);
        return CidReporting.FromPayload((await _endpoint.CallAsync(2, args.ToArray()).ConfigureAwait(false)).ExtendPayload());
    }

    /// <summary>Apply reporting/remapping changes for <paramref name="cid"/>.</summary>
    public async Task<CidReportingChangeEcho> SetCidReportingAsync(ControlId cid, CidReportingChange change) =>
        CidReportingChangeEcho.FromPayload(
            (await _endpoint.CallLongAsync(3, change.ToPayload(cid)).ConfigureAwait(false)).ExtendPayload());

    /// <summary>Feature-level capabilities (v6 devices; older firmware may return InvalidFunctionId).</summary>
    public async Task<ReprogControlsCapabilities> GetCapabilitiesAsync()
    {
        var p = (await _endpoint.CallAsync(4, [0, 0, 0]).ConfigureAwait(false)).ExtendPayload();
        return new ReprogControlsCapabilities((p[0] & 1) != 0);
    }

    /// <summary>Reset all diverted or remapped control settings (v6 devices).</summary>
    public async Task ResetAllCidReportSettingsAsync() =>
        await _endpoint.CallAsync(5, [0, 0, 0]).ConfigureAwait(false);

    public void Dispose() => _listener.Dispose();

    /// <summary>Decode an unsolicited 0x1b04 event payload by its sub-id.</summary>
    public static ReprogControlsEvent? DecodeEventPayload(byte functionId, ReadOnlySpan<byte> p)
    {
        switch (functionId)
        {
            case 0:
                return new ReprogControlsEvent.DivertedButtons(
                [
                    ControlId.FromPayload(p[0..2]), ControlId.FromPayload(p[2..4]),
                    ControlId.FromPayload(p[4..6]), ControlId.FromPayload(p[6..8]),
                ]);
            case 1:
                return new ReprogControlsEvent.DivertedRawMouseXy(
                    BinaryPrimitives.ReadInt16BigEndian(p[0..2]),
                    BinaryPrimitives.ReadInt16BigEndian(p[2..4]));
            case 2:
                return new ReprogControlsEvent.AnalyticsKeyEvents(
                [
                    AnalyticsKeyEvent.FromPayload(p[0..3]), AnalyticsKeyEvent.FromPayload(p[3..6]),
                    AnalyticsKeyEvent.FromPayload(p[6..9]), AnalyticsKeyEvent.FromPayload(p[9..12]),
                    AnalyticsKeyEvent.FromPayload(p[12..15]),
                ]);
            case 4:
                if (!Enum.IsDefined(typeof(RawWheelResolution), (byte)((p[0] >> 4) & 1))) return null;
                return new ReprogControlsEvent.DivertedRawWheel(
                    (RawWheelResolution)((p[0] >> 4) & 1),
                    U4.FromLo(p[0]),
                    BinaryPrimitives.ReadInt16BigEndian(p[1..3]));
            default:
                return null;
        }
    }
}
