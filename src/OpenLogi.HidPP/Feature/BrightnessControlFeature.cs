using OpenLogi.HidPP.Channel;

namespace OpenLogi.HidPP.Feature;

/// <summary>Capabilities reported by BrightnessControl (0x8040).</summary>
[Flags]
public enum BrightnessCapabilities : byte
{
    None = 0,
    HardwareBrightness = 1 << 0,
    Events = 1 << 1,
    Illumination = 1 << 2,
    HardwareOnOff = 1 << 3,
    Transient = 1 << 4,
}

/// <summary>Brightness range and capability info.</summary>
public readonly record struct BrightnessInfo(
    ushort MinBrightness, ushort MaxBrightness, ushort Steps, BrightnessCapabilities Capabilities);

/// <summary>
/// The `BrightnessControl` / 0x8040 feature — backlight brightness level and an
/// on/off illumination toggle. Ported from Rust <c>feature::brightness_control</c>.
/// </summary>
public sealed class BrightnessControlFeature(FeatureEndpoint endpoint) : ICreatableFeature<BrightnessControlFeature>
{
    public static ushort Id => 0x8040;
    public static byte StartingVersion => 1;
    public static BrightnessControlFeature Create(HidppChannel channel, byte deviceIndex, byte featureIndex) =>
        new(new FeatureEndpoint(channel, deviceIndex, featureIndex));

    private static ushort Be16(byte[] p, int i) => (ushort)((p[i] << 8) | p[i + 1]);

    /// <summary>Brightness range + capabilities.</summary>
    public async Task<BrightnessInfo> GetInfoAsync()
    {
        var p = (await endpoint.CallAsync(0, [0, 0, 0]).ConfigureAwait(false)).ExtendPayload();
        return new BrightnessInfo(Be16(p, 4), Be16(p, 0), Be16(p, 6), (BrightnessCapabilities)p[3]);
    }

    /// <summary>Current brightness value.</summary>
    public async Task<ushort> GetBrightnessAsync() =>
        Be16((await endpoint.CallAsync(1, [0, 0, 0]).ConfigureAwait(false)).ExtendPayload(), 0);

    /// <summary>Set the brightness value (0..MaxBrightness).</summary>
    public async Task SetBrightnessAsync(ushort brightness) =>
        await endpoint.CallAsync(2, [(byte)(brightness >> 8), (byte)(brightness & 0xff), 0]).ConfigureAwait(false);

    /// <summary>Whether illumination is currently on.</summary>
    public async Task<bool> GetIlluminationAsync() =>
        ((await endpoint.CallAsync(3, [0, 0, 0]).ConfigureAwait(false)).ExtendPayload()[0] & 1) != 0;

    /// <summary>Turn illumination on/off.</summary>
    public async Task SetIlluminationAsync(bool enabled) =>
        await endpoint.CallAsync(4, [(byte)(enabled ? 1 : 0), 0, 0]).ConfigureAwait(false);
}
