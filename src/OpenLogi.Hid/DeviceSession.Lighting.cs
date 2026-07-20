using OpenLogi.HidPP.Feature;
using OpenLogi.HidPP.Protocol;

namespace OpenLogi.Hid;

public sealed partial class DeviceSession
{
    // ── Lighting ────────────────────────────────────────────────────────────

    /// <summary>Whether the device exposes a supported lighting engine (0x8070 or 0x8071).</summary>
    public bool SupportsLighting =>
        _device.GetFeature<ColorLedEffectsFeature>() is not null
        || _device.GetFeature<RgbEffectsFeature>() is not null;

    /// <summary>
    /// Set a solid keyboard colour: prefer ColorLedEffects (0x8070); fall back to
    /// PerKeyLighting v2 (0x8081, newer G-series like the G915 — HARDWARE-VERIFIED);
    /// else RgbEffects (0x8071). Returns false if the device exposes none.
    /// </summary>
    public async Task<bool> ApplyLightingAsync(byte r, byte g, byte b)
    {
        try
        {
            if (_device.GetFeature<ColorLedEffectsFeature>() is { } led)
                return await ApplyColorLedEffectsAsync(led, r, g, b).ConfigureAwait(false);

            // 0x8081 (PerKeyLighting v2) — works on the G915 via host mode; prefer it
            // over 0x8071 (which the onboard profile replays over).
            if (await ApplyPerKeyColorAsync(r, g, b).ConfigureAwait(false))
                return true;

            if (_device.GetFeature<RgbEffectsFeature>() is { } rgb)
                return await ApplyRgbEffectsAsync(rgb, r, g, b).ConfigureAwait(false);
        }
        catch { /* fall through */ }
        return false;
    }

    private static async Task<bool> ApplyColorLedEffectsAsync(ColorLedEffectsFeature led, byte r, byte g, byte b)
    {
        var zoneCount = await led.GetZoneCountAsync().ConfigureAwait(false);
        var zones = zoneCount == 0 ? (byte)4 : Math.Min(zoneCount, (byte)4);
        var p = new byte[ColorLedEffectsFeature.ZoneEffectParamCount];
        p[0] = r; p[1] = g; p[2] = b;
        for (byte zone = 0; zone < zones; zone++)
        {
            await led.SetZoneEffectAsync(zone, ColorLedEffectsFeature.EffectFixedColor, p,
                ColorLedEffectsFeature.PersistenceVolatile).ConfigureAwait(false);
            await Task.Delay(8).ConfigureAwait(false);
        }
        return true;
    }

    private static async Task<bool> ApplyRgbEffectsAsync(RgbEffectsFeature rgb, byte r, byte g, byte b)
    {
        // 0x8071 needs software control first; then set each real cluster
        // (0xff "all clusters" is a read-only selector — invalid for set).
        // NB: on G-series keyboards with an onboard profile (e.g. the G915)
        // the firmware replays the stored profile over these writes; live
        // colour there needs host mode + 0x8081 per-key streaming, not yet
        // implemented, so we don't switch host mode here (it would just go dark).
        await rgb.SetSwControlAsync(RgbEffectsFeature.SwControlAllClusters).ConfigureAwait(false);
        var p = new byte[RgbEffectsFeature.ClusterEffectParamCount];
        p[0] = r; p[1] = g; p[2] = b;
        var applied = false;
        for (byte cluster = 0; cluster < 4; cluster++)
        {
            try
            {
                await rgb.SetRgbClusterEffectAsync(cluster, RgbEffectsFeature.EffectFixed, p,
                    RgbEffectsFeature.PersistenceVolatile, RgbEffectsFeature.PowerFull).ConfigureAwait(false);
                applied = true;
                await Task.Delay(8).ConfigureAwait(false);
            }
            catch (Hidpp20Exception) { /* cluster doesn't exist on this device */ }
        }
        return applied;
    }

    // ── Brightness + per-key lighting + RGB effects ──────────────────────────

    /// <summary>Whether the device exposes BrightnessControl (0x8040).</summary>
    public bool SupportsBrightness => _device.GetFeature<BrightnessControlFeature>() is not null;

    /// <summary>Read the backlight brightness range + current value, or <c>null</c> if unsupported.</summary>
    public async Task<(BrightnessInfo Info, ushort Current)?> ReadBrightnessAsync()
    {
        if (_device.GetFeature<BrightnessControlFeature>() is not { } bc) return null;
        try { return (await bc.GetInfoAsync().ConfigureAwait(false), await bc.GetBrightnessAsync().ConfigureAwait(false)); }
        catch { return null; }
    }

    /// <summary>Set the backlight brightness (0..max).</summary>
    public async Task<bool> ApplyBrightnessAsync(ushort brightness)
    {
        if (_device.GetFeature<BrightnessControlFeature>() is not { } bc) return false;
        try { await bc.SetBrightnessAsync(brightness).ConfigureAwait(false); return true; }
        catch { return false; }
    }

    /// <summary>Whether the device exposes PerKeyLighting v2 (0x8081).</summary>
    public bool SupportsPerKey => _device.GetFeature<PerKeyLightingFeature>() is not null;

    /// <summary>
    /// Set a solid colour via PerKeyLighting v2 (0x8081): switch to host mode (so
    /// the onboard profile stops replaying), fill the whole zone range with the
    /// colour, then commit the frame. The session must stay open to hold it.
    /// </summary>
    public async Task<bool> ApplyPerKeyColorAsync(byte r, byte g, byte b)
    {
        if (_device.GetFeature<PerKeyLightingFeature>() is not { } pk) return false;
        try
        {
            await EnsureHostModeAsync().ConfigureAwait(false);
            // One range covering every addressable zone; absent zones are ignored.
            await pk.SetRangeRgbZonesAsync([new RgbZoneRange(0x00, 0xfe, r, g, b)]).ConfigureAwait(false);
            await pk.FrameEndAsync(PerKeyLightingFeature.FramePersistenceVolatile).ConfigureAwait(false);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Fast per-frame colour fill for animations: fill all zones + commit, WITHOUT
    /// the host-mode switch (call <see cref="ApplyPerKeyColorAsync"/> once first to
    /// enter host mode). Returns false if the device has no 0x8081.
    /// </summary>
    public async Task<bool> ApplyPerKeyColorFastAsync(byte r, byte g, byte b)
    {
        if (_device.GetFeature<PerKeyLightingFeature>() is not { } pk) return false;
        try
        {
            await pk.SetRangeAllFastAsync(r, g, b).ConfigureAwait(false);
            await pk.FrameEndFastAsync(PerKeyLightingFeature.FramePersistenceVolatile).ConfigureAwait(false);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Light a single per-key zone (host mode, others left dark). Used to probe the
    /// zone-id → key mapping. Returns false if no 0x8081.
    /// </summary>
    public async Task<bool> SetKeyZoneAsync(byte zoneId, byte r, byte g, byte b)
    {
        if (_device.GetFeature<PerKeyLightingFeature>() is not { } pk) return false;
        try
        {
            await EnsureHostModeAsync().ConfigureAwait(false);
            await pk.SetSingleValueAsync(r, g, b, [zoneId]).ConfigureAwait(false);
            await pk.FrameEndAsync(PerKeyLightingFeature.FramePersistenceVolatile).ConfigureAwait(false);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Update a single per-key zone and commit, WITHOUT re-entering host mode or
    /// clearing — other keys keep their colors (call <see cref="ApplyPerKeyColorAsync"/>
    /// once first to set a base + enter host mode). For the press-to-color editor.
    /// </summary>
    public async Task<bool> SetZoneAsync(byte zone, byte r, byte g, byte b)
    {
        if (_device.GetFeature<PerKeyLightingFeature>() is not { } pk) return false;
        try
        {
            await pk.SetSingleValueAsync(r, g, b, [zone]).ConfigureAwait(false);
            await pk.FrameEndAsync(PerKeyLightingFeature.FramePersistenceVolatile).ConfigureAwait(false);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Apply a full per-key color map (zone id → RGB) live via 0x8081: host mode,
    /// fill all zones dark, then set each requested zone, then commit. Returns false
    /// if no 0x8081.
    /// </summary>
    public async Task<bool> ApplyPerKeyMapAsync(IReadOnlyDictionary<byte, (byte R, byte G, byte B)> map)
    {
        if (_device.GetFeature<PerKeyLightingFeature>() is not { } pk) return false;
        try
        {
            await EnsureHostModeAsync().ConfigureAwait(false);
            await pk.SetRangeRgbZonesAsync([new RgbZoneRange(0x00, 0xfe, 0, 0, 0)]).ConfigureAwait(false); // clear to dark (awaited)
            foreach (var (zone, c) in map)
                await pk.SetSingleValueAsync(c.R, c.G, c.B, [zone]).ConfigureAwait(false);
            await pk.FrameEndAsync(PerKeyLightingFeature.FramePersistenceVolatile).ConfigureAwait(false);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Apply a per-key color map over a solid base via 0x8081: host mode, fill every
    /// zone with the base color, then set each mapped (painted) zone, then commit.
    /// Restores the saved "No profile" per-key look (base = unpainted-key color)
    /// without opening the editor. Returns false if no 0x8081.
    /// </summary>
    public async Task<bool> ApplyPerKeyMapAsync(byte baseR, byte baseG, byte baseB, IReadOnlyDictionary<byte, (byte R, byte G, byte B)> map)
    {
        if (_device.GetFeature<PerKeyLightingFeature>() is not { } pk) return false;
        try
        {
            await EnsureHostModeAsync().ConfigureAwait(false);
            await pk.SetRangeRgbZonesAsync([new RgbZoneRange(0x00, 0xfe, baseR, baseG, baseB)]).ConfigureAwait(false); // base fill
            foreach (var (zone, c) in map)
                await pk.SetSingleValueAsync(c.R, c.G, c.B, [zone]).ConfigureAwait(false);
            await pk.FrameEndAsync(PerKeyLightingFeature.FramePersistenceVolatile).ConfigureAwait(false);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Enumerate cluster 0's effects as (index, effectId), or <c>null</c> if unsupported.</summary>
    public async Task<IReadOnlyList<(byte Index, ushort EffectId)>?> EnumerateEffectsAsync()
    {
        if (_device.GetFeature<RgbEffectsFeature>() is not { } rgb) return null;
        try
        {
            var count = await rgb.GetEffectCountAsync(0).ConfigureAwait(false);
            var list = new List<(byte, ushort)>(count);
            for (byte i = 0; i < count; i++)
                list.Add((i, await rgb.GetEffectIdAsync(0, i).ConfigureAwait(false)));
            return list;
        }
        catch { return null; }
    }

    /// <summary>
    /// Apply an RGB effect by its cluster-effect index with raw params, to every
    /// cluster (0..3). Takes software control first. Returns false if no 0x8071.
    /// </summary>
    public async Task<bool> ApplyEffectAsync(byte effectIndex, byte[] effectParams, byte persistence = RgbEffectsFeature.PersistenceVolatile, bool takeSwControl = true)
    {
        if (_device.GetFeature<RgbEffectsFeature>() is not { } rgb) return false;
        try
        {
            await EnsureHostModeAsync().ConfigureAwait(false);
            if (takeSwControl)
                await rgb.SetSwControlAsync(RgbEffectsFeature.SwControlAllClusters).ConfigureAwait(false);
            var applied = false;
            for (byte cluster = 0; cluster < 4; cluster++)
            {
                try
                {
                    await rgb.SetRgbClusterEffectAsync(cluster, effectIndex, effectParams,
                        persistence, RgbEffectsFeature.PowerFull).ConfigureAwait(false);
                    applied = true;
                    await Task.Delay(8).ConfigureAwait(false);
                }
                catch (Hidpp20Exception) { /* cluster doesn't exist */ }
            }
            return applied;
        }
        catch { return false; }
    }
}
