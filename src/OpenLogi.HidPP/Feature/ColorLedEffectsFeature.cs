using OpenLogi.HidPP.Channel;

namespace OpenLogi.HidPP.Feature;

/// <summary>
/// The `ColorLedEffects` / 0x8070 per-zone RGB effect engine. Ported from Rust
/// <c>feature::color_led_effects</c> — the subset needed to drive a solid colour
/// (zone count + fixed-effect write). The event listener and the many read
/// functions are not ported (not needed for setting a keyboard colour).
/// </summary>
public sealed class ColorLedEffectsFeature(FeatureEndpoint endpoint) : ICreatableFeature<ColorLedEffectsFeature>
{
    public static ushort Id => 0x8070;
    public static byte StartingVersion => 0;
    public static ColorLedEffectsFeature Create(HidppChannel channel, byte deviceIndex, byte featureIndex) =>
        new(new FeatureEndpoint(channel, deviceIndex, featureIndex));

    /// <summary>The fixed/static single-colour effect index (EffectId::FixedColor).</summary>
    public const byte EffectFixedColor = 1;
    /// <summary>Apply to RAM only (Persistence::Volatile) — shows live, overrides the onboard profile, no flash wear.</summary>
    public const byte PersistenceVolatile = 0;
    /// <summary>Number of effect-parameter bytes.</summary>
    public const int ZoneEffectParamCount = 10;

    /// <summary>The number of LED zones the device exposes.</summary>
    public async Task<byte> GetZoneCountAsync() =>
        (await endpoint.CallAsync(0, [0, 0, 0]).ConfigureAwait(false)).ExtendPayload()[0];

    /// <summary>Apply <paramref name="effectIndex"/> to <paramref name="zone"/> with effect-specific params (RGB for FixedColor).</summary>
    public async Task SetZoneEffectAsync(byte zone, byte effectIndex, byte[] paramsBytes, byte persistence)
    {
        var args = new byte[16];
        args[0] = zone;
        args[1] = effectIndex;
        paramsBytes.AsSpan(0, Math.Min(paramsBytes.Length, ZoneEffectParamCount)).CopyTo(args.AsSpan(2));
        args[12] = persistence;
        await endpoint.CallLongAsync(3, args).ConfigureAwait(false);
    }
}
