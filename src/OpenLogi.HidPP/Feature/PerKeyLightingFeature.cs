using OpenLogi.HidPP.Channel;

namespace OpenLogi.HidPP.Feature;

/// <summary>One inclusive zone-id range filled with a single colour.</summary>
public readonly record struct RgbZoneRange(byte FirstZoneId, byte LastZoneId, byte R, byte G, byte B);

/// <summary>
/// The `PerKeyLighting` v2 / 0x8081 feature (newer G-series, e.g. the G915).
/// Unlike the v1 0x8080 (which streams 64-byte 0x12 reports), v2 uses standard
/// 20-byte long reports: set zones, then commit with frameEnd. Ported from Rust
/// <c>feature::per_key_lighting</c> — the subset needed to drive a solid colour.
/// </summary>
public sealed class PerKeyLightingFeature(FeatureEndpoint endpoint) : ICreatableFeature<PerKeyLightingFeature>
{
    public static ushort Id => 0x8081;
    public static byte StartingVersion => 0;
    public static PerKeyLightingFeature Create(HidppChannel channel, byte deviceIndex, byte featureIndex) =>
        new(new FeatureEndpoint(channel, deviceIndex, featureIndex));

    public const byte FramePersistenceVolatile = 0;
    public const byte FramePersistenceVolatileAndNonVolatile = 1;
    private const int MaxRanges = 3;
    private const int MaxSingleValueZones = 13;

    /// <summary>Set up to three inclusive zone ranges, each filled with one colour (function 5).</summary>
    public async Task SetRangeRgbZonesAsync(IReadOnlyList<RgbZoneRange> ranges)
    {
        var args = new byte[16];
        for (var slot = 0; slot < Math.Min(ranges.Count, MaxRanges); slot++)
        {
            var b = slot * 5;
            args[b] = ranges[slot].FirstZoneId;
            args[b + 1] = ranges[slot].LastZoneId;
            args[b + 2] = ranges[slot].R;
            args[b + 3] = ranges[slot].G;
            args[b + 4] = ranges[slot].B;
        }
        await endpoint.CallLongAsync(5, args).ConfigureAwait(false);
    }

    /// <summary>Apply one colour to up to thirteen individually addressed zones (function 6).</summary>
    public async Task SetSingleValueAsync(byte r, byte g, byte b, byte[] zoneIds)
    {
        var args = new byte[16];
        args[0] = r; args[1] = g; args[2] = b;
        for (var i = 0; i < Math.Min(zoneIds.Length, MaxSingleValueZones); i++)
            args[3 + i] = zoneIds[i];
        await endpoint.CallLongAsync(6, args).ConfigureAwait(false);
    }

    /// <summary>Commit pending zone changes and update the display (function 7).</summary>
    public async Task FrameEndAsync(byte persistence, ushort currentFrame = 0, ushort framesTillNextChange = 0)
    {
        var args = new byte[16];
        args[0] = persistence;
        args[1] = (byte)(currentFrame >> 8);
        args[2] = (byte)(currentFrame & 0xff);
        args[3] = (byte)(framesTillNextChange >> 8);
        args[4] = (byte)(framesTillNextChange & 0xff);
        await endpoint.CallLongAsync(7, args).ConfigureAwait(false);
    }

    // Fire-and-forget variants for high-rate animation (don't await the device's
    // reply — the BLE response round-trip otherwise caps the frame rate at ~3 fps).

    /// <summary>Fill the whole zone range with one colour, fire-and-forget.</summary>
    public Task SetRangeAllFastAsync(byte r, byte g, byte b)
    {
        var args = new byte[16];
        args[0] = 0x00; args[1] = 0xfe; args[2] = r; args[3] = g; args[4] = b;
        return endpoint.NotifyLongAsync(5, args);
    }

    /// <summary>Commit the frame, fire-and-forget.</summary>
    public Task FrameEndFastAsync(byte persistence)
    {
        var args = new byte[16];
        args[0] = persistence;
        return endpoint.NotifyLongAsync(7, args);
    }
}
