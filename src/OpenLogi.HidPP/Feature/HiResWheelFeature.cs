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
/// The `HiResWheel` / 0x2121 feature — controls the scroll wheel's resolution and,
/// crucially, its rotation direction (a native, firmware-level scroll invert).
/// Mirrors Solaar's HIRES_WHEEL get/setWheelMode (functions 1 and 2); function 0
/// (getWheelCapability) reports the high-res multiplier.
/// </summary>
public sealed class HiResWheelFeature(FeatureEndpoint endpoint) : ICreatableFeature<HiResWheelFeature>
{
    public static ushort Id => 0x2121;
    public static byte StartingVersion => 0;
    public static HiResWheelFeature Create(HidppChannel channel, byte deviceIndex, byte featureIndex) =>
        new(new FeatureEndpoint(channel, deviceIndex, featureIndex));

    /// <summary>The ratio between a high-res and a low-res wheel notch (getWheelCapability, fn 0).</summary>
    public async Task<byte> GetMultiplierAsync() =>
        (await endpoint.CallAsync(0, [0, 0, 0]).ConfigureAwait(false)).ExtendPayload()[0];

    /// <summary>The current wheel mode — report target, resolution and invert (getWheelMode, fn 1).</summary>
    public async Task<HiResWheelMode> GetModeAsync() =>
        HiResWheelMode.FromByte((await endpoint.CallAsync(1, [0, 0, 0]).ConfigureAwait(false)).ExtendPayload()[0]);

    /// <summary>Set the whole wheel mode byte (setWheelMode, fn 2).</summary>
    public async Task SetModeAsync(HiResWheelMode mode) =>
        await endpoint.CallAsync(2, [mode.ToByte(), 0, 0]).ConfigureAwait(false);
}
