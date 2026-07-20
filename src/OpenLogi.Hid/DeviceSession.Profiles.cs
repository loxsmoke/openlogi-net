using OpenLogi.HidPP.Feature;

namespace OpenLogi.Hid;

public sealed partial class DeviceSession
{
    // ── Onboard profiles (0x8100): mode, sector I/O, G-keys, stored lighting ─

    /// <summary>
    /// Switch a G-series device to host (software) mode via OnboardProfiles
    /// (0x8100) if present, so software lighting writes aren't overridden by the
    /// replayed onboard profile. No-op for devices without 0x8100.
    /// </summary>
    public async Task EnsureHostModeAsync()
    {
        if (_device.GetFeature<OnboardProfilesFeature>() is not { } ob) return;
        try { await ob.SetModeAsync(OnboardProfilesFeature.ModeHost).ConfigureAwait(false); }
        catch { /* not all 0x8100 implementations allow the switch */ }
    }

    /// <summary>Read onboard-profile info (count + current), or <c>null</c> if no 0x8100.</summary>
    public Task<(OnboardProfilesInfo Info, byte Current)?> ReadProfilesAsync() => RetryStructAsync(ReadProfilesOnceAsync);

    private async Task<(OnboardProfilesInfo Info, byte Current)?> ReadProfilesOnceAsync()
    {
        if (_device.GetFeature<OnboardProfilesFeature>() is not { } ob) return null;

        // getCurrentProfile only answers in onboard mode; default to 0 ("No profile")
        // when it can't be read so it doesn't sink the whole read.
        async Task<(OnboardProfilesInfo Info, byte Current)> ReadOnceAsync()
        {
            var info = await ob.GetInfoAsync().ConfigureAwait(false);
            byte current;
            try { current = await ob.GetCurrentProfileAsync().ConfigureAwait(false); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"ReadProfiles: getCurrentProfile failed: {ex}"); current = 0; }
            return (info, current);
        }

        // Try in the current mode first (no side effects if it answers).
        try
        {
            if (await ReadOnceAsync().ConfigureAwait(false) is { Info.ProfileCount: > 0 } ok)
                return ok;
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"ReadProfiles: read in current mode failed, trying onboard: {ex}"); }

        // The device may be stuck in host mode (after the editor / solid lighting),
        // where the profile read can fail or report nothing. Switch to onboard, read,
        // then restore host mode so the lighting mode is left as it was.
        try
        {
            byte mode;
            try { mode = await ob.GetModeAsync().ConfigureAwait(false); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"ReadProfiles: getMode failed: {ex}"); return null; }
            if (mode != OnboardProfilesFeature.ModeHost) return null; // already onboard; nothing else to try

            await ob.SetModeAsync(OnboardProfilesFeature.ModeOnboard).ConfigureAwait(false);
            try { return await ReadOnceAsync().ConfigureAwait(false); }
            finally
            {
                try { await ob.SetModeAsync(OnboardProfilesFeature.ModeHost).ConfigureAwait(false); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"ReadProfiles: restore host mode failed: {ex}"); }
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"ReadProfiles: onboard-mode read failed: {ex}"); return null; }
    }

    /// <summary>
    /// Raw diagnostic call into an arbitrary feature: resolves <paramref name="featureId"/>'s
    /// index and invokes <paramref name="function"/> with a short payload, returning the
    /// extended response payload — <c>null</c> if the device lacks the feature. CLI-only
    /// escape hatch for probing undocumented features (e.g. 0x1830 Power Modes).
    /// </summary>
    public async Task<byte[]?> CallRawFeatureAsync(ushort featureId, byte function, byte[] args)
    {
        if (_device.FeatureIndex(featureId) is not { } idx) return null;
        var ep = new FeatureEndpoint(_channel, _device.DeviceIndex, idx);
        var padded = new byte[3]; // short calls carry exactly 3 payload bytes
        args.AsSpan(0, Math.Min(args.Length, 3)).CopyTo(padded);
        return (await ep.CallAsync(function, padded).ConfigureAwait(false)).ExtendPayload();
    }

    /// <summary>Read 16 bytes at sector/offset (OnboardProfiles readMemory), or <c>null</c> if no 0x8100.</summary>
    public async Task<byte[]?> ReadMemoryRawAsync(ushort sector, ushort offset)
    {
        if (_device.GetFeature<OnboardProfilesFeature>() is not { } ob) return null;
        return await ob.ReadMemoryAsync(sector, offset).ConfigureAwait(false);
    }

    /// <summary>
    /// Read a profile sector, tolerant of the final partial 16-byte chunk past the
    /// sector size. Returns whatever was read (the device's readable range), or
    /// <c>null</c> if no 0x8100 / nothing read. Groundwork for profile editing.
    /// </summary>
    public async Task<byte[]?> ReadProfileSectorAsync(ushort sectorId)
    {
        if (_device.GetFeature<OnboardProfilesFeature>() is not { } ob) return null;
        var data = new byte[256];
        var got = 0;
        for (var off = 0; off < 256; off += 16)
        {
            try { Array.Copy(await ob.ReadMemoryAsync(sectorId, (ushort)off).ConfigureAwait(false), 0, data, off, 16); got = off + 16; }
            catch { break; }
        }
        return got == 0 ? null : data[..got];
    }

    /// <summary>
    /// Read a full sector of <paramref name="size"/> bytes, clamping the final read
    /// offset so it never runs past the sector (which errors). <c>null</c> if no 0x8100.
    /// </summary>
    public async Task<byte[]?> ReadFullSectorAsync(ushort sectorId, int size)
    {
        if (_device.GetFeature<OnboardProfilesFeature>() is not { } ob) return null;
        var data = new byte[size];
        var off = 0;
        while (off < size)
        {
            var readOff = Math.Min(off, size - 16);
            var chunk = await ob.ReadMemoryAsync(sectorId, (ushort)readOff).ConfigureAwait(false);
            var copyStart = off - readOff;
            var copyLen = Math.Min(16 - copyStart, size - off);
            Array.Copy(chunk, copyStart, data, off, copyLen);
            off += copyLen;
        }
        return data;
    }

    // Format-4 (G915) G-key bindings: five 4-byte entries [0x80, 0x02, modifier,
    // HID-usage] starting at sector offset 0x20 (G1..G5). HARDWARE-VERIFIED:
    // patching the usage byte remapped G1 (F1 → 'a').
    private const int GKeyBindingsOffset = 0x20;
    public const int GKeyCount = 5;

    /// <summary>Whether the device exposes the GKeys feature (0x8010).</summary>
    public bool SupportsGKeys => _device.FeatureIndex(0x8010) is not null;

    /// <summary>Whether the device exposes OnboardProfiles (0x8100) — a keyboard's real control interface.</summary>
    public bool SupportsOnboardProfiles => _device.FeatureIndex(0x8100) is not null;

    /// <summary>Read the five G-keys' current (modifier, usage) from a profile sector, or <c>null</c>.</summary>
    public Task<(byte Modifier, byte Usage)[]?> ReadGKeyBindingsAsync(ushort profileSector) =>
        RetryAsync(() => ReadGKeyBindingsOnceAsync(profileSector));

    private async Task<(byte Modifier, byte Usage)[]?> ReadGKeyBindingsOnceAsync(ushort profileSector)
    {
        var data = await ReadFullSectorAsync(profileSector, 255).ConfigureAwait(false);
        if (data is null) return null;
        var bindings = new (byte, byte)[GKeyCount];
        for (var i = 0; i < GKeyCount; i++)
        {
            var off = GKeyBindingsOffset + i * 4;
            bindings[i] = (data[off + 2], data[off + 3]);
        }
        return bindings;
    }

    /// <summary>Remap one G-key (0-based) to a HID keyboard usage in a profile (persists on-device).</summary>
    public async Task<bool> SetGKeyUsageAsync(ushort profileSector, int gkeyIndex, byte usage, byte modifier = 0)
    {
        if (gkeyIndex < 0 || gkeyIndex >= GKeyCount) return false;
        var data = await ReadFullSectorAsync(profileSector, 255).ConfigureAwait(false);
        if (data is null) return false;
        var off = GKeyBindingsOffset + gkeyIndex * 4;
        data[off] = 0x80; data[off + 1] = 0x02; data[off + 2] = modifier; data[off + 3] = usage;
        return await WriteProfileSectorAsync(profileSector, data).ConfigureAwait(false);
    }

    // Format-4 (G915) profile lighting: three 11-byte effect descriptors
    // [effect, R, G, B, …] at these sector offsets. effect 0x01=fixed, 0x03=cycle,
    // 0x0a=breathing. HARDWARE-VERIFIED: patching these to 01/RGB sets a persistent
    // device-side solid colour. (Offsets are device-format-specific.)
    private static readonly int[] ProfileLedOffsets = [0xD0, 0xDB, 0xE6];

    /// <summary>
    /// Set a profile's stored lighting to a solid colour (persists on the device,
    /// runs even after the app closes). Reads the sector, patches the LED
    /// descriptors, recomputes the CRC and writes. <c>false</c> if no 0x8100.
    /// </summary>
    public async Task<bool> SetProfileSolidColorAsync(ushort profileSector, byte r, byte g, byte b) =>
        await SetProfileEffectAsync(profileSector, 0x01, r, g, b, 0, 100).ConfigureAwait(false);

    /// <summary>Effect bytes for a profile lighting descriptor.</summary>
    public const byte EffectOff = 0x00, EffectFixed = 0x01, EffectCycle = 0x03, EffectBreathing = 0x0a;

    /// <summary>
    /// Write a lighting effect into a profile's three descriptors (persists,
    /// device-side). Descriptor layout (hardware-probed): byte0=effect; Fixed
    /// [r,g,b]; Breathing [r,g,b, period(2 BE ms), brightness]; Cycle keeps the
    /// device's default speed + brightness. <paramref name="brightness"/> is 0–100.
    /// </summary>
    public async Task<bool> SetProfileEffectAsync(ushort profileSector, byte effect, byte r, byte g, byte b, ushort periodMs, byte brightness)
    {
        var data = await ReadFullSectorAsync(profileSector, 255).ConfigureAwait(false);
        if (data is null) return false;
        var desc = new byte[11];
        desc[0] = effect;
        switch (effect)
        {
            case EffectFixed:
                desc[1] = r; desc[2] = g; desc[3] = b;
                break;
            case EffectBreathing:
                desc[1] = r; desc[2] = g; desc[3] = b;
                desc[4] = (byte)(periodMs >> 8); desc[5] = (byte)(periodMs & 0xff);
                desc[6] = brightness;
                break;
            case EffectCycle:
                desc[6] = 0x08; desc[7] = 0x34; // device default cycle period
                desc[8] = brightness;
                break;
            // EffectOff: all zero.
        }
        foreach (var off in ProfileLedOffsets)
            Array.Copy(desc, 0, data, off, 11);
        return await WriteProfileSectorAsync(profileSector, data).ConfigureAwait(false);
    }

    /// <summary>
    /// Read a profile's stored lighting descriptor (the first LED zone), the inverse
    /// of <see cref="SetProfileEffectAsync"/>. <c>null</c> if no 0x8100 / unreadable.
    /// Period/brightness are only meaningful for Breathing/Cycle (see the write layout).
    /// </summary>
    public async Task<ProfileLighting?> ReadProfileEffectAsync(ushort profileSector)
    {
        var data = await ReadFullSectorAsync(profileSector, 255).ConfigureAwait(false);
        if (data is null) return null;
        var off = ProfileLedOffsets[0];
        var effect = data[off];
        var (period, brightness) = effect switch
        {
            EffectBreathing => ((ushort)((data[off + 4] << 8) | data[off + 5]), data[off + 6]),
            EffectCycle => ((ushort)((data[off + 6] << 8) | data[off + 7]), data[off + 8]),
            _ => ((ushort)0, (byte)100),
        };
        return new ProfileLighting(effect, data[off + 1], data[off + 2], data[off + 3], period, brightness);
    }

    /// <summary>
    /// Write a 255-byte profile sector back to flash, recomputing the trailing
    /// CRC-16 over [0..253] first. Streams in 16-byte chunks (startWrite →
    /// writeMemory… → endWrite). DESTRUCTIVE: corrupts the profile if the layout
    /// is wrong (recoverable by rewriting from G HUB). <c>false</c> if no 0x8100.
    /// </summary>
    public async Task<bool> WriteProfileSectorAsync(ushort sectorId, byte[] sector)
    {
        if (_device.GetFeature<OnboardProfilesFeature>() is not { } ob) return false;
        if (sector.Length < 255) return false;
        var crc = OpenLogi.HidPP.Crc16.Ccitt(sector.AsSpan(0, 253));
        sector[253] = (byte)(crc >> 8);
        sector[254] = (byte)(crc & 0xff);
        try
        {
            await ob.StartWriteAsync(sectorId, 0, 255).ConfigureAwait(false);
            for (var off = 0; off < 255; off += 16)
                await ob.WriteMemoryAsync(sector.AsMemory(off, Math.Min(16, 255 - off))).ConfigureAwait(false);
            await ob.EndWriteAsync().ConfigureAwait(false);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Switch the active onboard profile (1-based), or false if unsupported.</summary>
    public async Task<bool> SwitchProfileAsync(byte profileIndex)
    {
        if (_device.GetFeature<OnboardProfilesFeature>() is not { } ob) return false;
        try
        {
            // setCurrentProfile only works (and getCurrentProfile only reads) in onboard mode.
            await ob.SetModeAsync(OnboardProfilesFeature.ModeOnboard).ConfigureAwait(false);
            await ob.SetCurrentProfileAsync(profileIndex).ConfigureAwait(false);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Set the device to host or onboard mode via OnboardProfiles (0x8100).</summary>
    public async Task<bool> SetOnboardModeAsync(bool host)
    {
        if (_device.GetFeature<OnboardProfilesFeature>() is not { } ob) return false;
        try
        {
            await ob.SetModeAsync(host ? OnboardProfilesFeature.ModeHost : OnboardProfilesFeature.ModeOnboard).ConfigureAwait(false);
            return true;
        }
        catch { return false; }
    }
}
