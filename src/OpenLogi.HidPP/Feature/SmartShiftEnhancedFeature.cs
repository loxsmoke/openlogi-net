using OpenLogi.HidPP.Channel;

namespace OpenLogi.HidPP.Feature;

/// <summary>Capabilities reported by SmartShiftWheelEnhanced (0x2111).</summary>
[Flags]
public enum SmartShiftEnhancedCapabilities : byte
{
    None = 0,
    TunableTorque = 1 << 0,
}

/// <summary>Capability and default values for enhanced SmartShift.</summary>
public readonly record struct SmartShiftEnhancedInfo(
    SmartShiftEnhancedCapabilities Capabilities, byte AutoDisengageDefault, byte DefaultTunableTorque, byte MaxForce);

/// <summary>Current enhanced SmartShift status.</summary>
public readonly record struct SmartShiftEnhancedStatus(
    SmartShiftWheelMode WheelMode, byte AutoDisengage, byte CurrentTunableTorque)
{
    /// <summary>Parse a status payload; an unknown wheel mode falls back to Ratchet (matches Rust).</summary>
    public static SmartShiftEnhancedStatus FromPayload(ReadOnlySpan<byte> payload) => new(
        Enum.IsDefined(typeof(SmartShiftWheelMode), payload[0]) ? (SmartShiftWheelMode)payload[0] : SmartShiftWheelMode.Ratchet,
        payload[1],
        payload[2]);
}

/// <summary>
/// Selected fields to apply via <see cref="SmartShiftEnhancedFeature.SetRatchetControlModeAsync"/>.
/// HID++ encodes 0 as "do not change", so writable thresholds must be non-zero.
/// </summary>
public readonly record struct SmartShiftEnhancedStatusChange(
    SmartShiftWheelMode? WheelMode = null, byte? AutoDisengage = null, byte? TunableTorque = null);

/// <summary>The `SmartShiftWheelEnhanced` / 0x2111 feature. Ported from Rust <c>feature::smartshift_enhanced</c>.</summary>
public sealed class SmartShiftEnhancedFeature(FeatureEndpoint endpoint) : ICreatableFeature<SmartShiftEnhancedFeature>
{
    public static ushort Id => 0x2111;
    public static byte StartingVersion => 0;
    public static SmartShiftEnhancedFeature Create(HidppChannel channel, byte deviceIndex, byte featureIndex) =>
        new(new FeatureEndpoint(channel, deviceIndex, featureIndex));

    /// <summary>Enhanced SmartShift capabilities and defaults.</summary>
    public async Task<SmartShiftEnhancedInfo> GetCapabilitiesAsync()
    {
        var p = (await endpoint.CallAsync(0, [0, 0, 0]).ConfigureAwait(false)).ExtendPayload();
        return new SmartShiftEnhancedInfo((SmartShiftEnhancedCapabilities)p[0], p[1], p[2], p[3]);
    }

    /// <summary>The current enhanced SmartShift ratchet control mode.</summary>
    public async Task<SmartShiftEnhancedStatus> GetRatchetControlModeAsync() =>
        SmartShiftEnhancedStatus.FromPayload((await endpoint.CallAsync(1, [0, 0, 0]).ConfigureAwait(false)).ExtendPayload());

    /// <summary>Apply selected fields and return the resulting status. A null field sends 0 ("do not change").</summary>
    public async Task<SmartShiftEnhancedStatus> SetRatchetControlModeAsync(SmartShiftEnhancedStatusChange change)
    {
        var payload = (await endpoint.CallAsync(2,
        [
            change.WheelMode is { } m ? (byte)m : (byte)0,
            change.AutoDisengage ?? 0,
            change.TunableTorque ?? 0,
        ]).ConfigureAwait(false)).ExtendPayload();
        return SmartShiftEnhancedStatus.FromPayload(payload);
    }
}
