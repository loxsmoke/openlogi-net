using OpenLogi.HidPP.Channel;
using OpenLogi.HidPP.Protocol;

namespace OpenLogi.HidPP.Feature;

/// <summary>The ratchet mode of the scroll wheel. Ported from Rust <c>smartshift::WheelMode</c>.</summary>
public enum SmartShiftWheelMode : byte { Freespin = 1, Ratchet = 2 }

/// <summary>The ratchet control mode of the mouse wheel. Ported from Rust <c>RatchetControlMode</c>.</summary>
public readonly record struct RatchetControlMode(SmartShiftWheelMode WheelMode, byte AutoDisengage, byte AutoDisengageDefault);

/// <summary>The `SmartShift` / 0x2110 feature. Ported from Rust <c>feature::smartshift</c>.</summary>
public sealed class SmartShiftFeature(FeatureEndpoint endpoint) : ICreatableFeature<SmartShiftFeature>
{
    public static ushort Id => 0x2110;
    public static byte StartingVersion => 0;
    public static SmartShiftFeature Create(HidppChannel channel, byte deviceIndex, byte featureIndex) =>
        new(new FeatureEndpoint(channel, deviceIndex, featureIndex));

    /// <summary>The current ratchet control mode (does not reflect auto-disengaged state).</summary>
    public async Task<RatchetControlMode> GetRatchetControlModeAsync()
    {
        var payload = (await endpoint.CallAsync(0, [0, 0, 0]).ConfigureAwait(false)).ExtendPayload();
        if (!Enum.IsDefined(typeof(SmartShiftWheelMode), payload[0]))
            throw Hidpp20Exception.UnsupportedResponse();
        return new RatchetControlMode((SmartShiftWheelMode)payload[0], payload[1], payload[2]);
    }

    /// <summary>
    /// Set the ratchet control mode. Each argument is optional (<c>null</c> keeps
    /// the current value); for the disengage thresholds, 0 also means "unchanged".
    /// 0x01–0xFE is quarter-turns/sec to disengage; 0xFF is permanent ratchet.
    /// </summary>
    public async Task SetRatchetControlModeAsync(
        SmartShiftWheelMode? wheelMode = null, byte? autoDisengage = null, byte? autoDisengageDefault = null)
    {
        await endpoint.CallAsync(1,
        [
            wheelMode is { } m ? (byte)m : (byte)0,
            autoDisengage ?? 0,
            autoDisengageDefault ?? 0,
        ]).ConfigureAwait(false);
    }
}
