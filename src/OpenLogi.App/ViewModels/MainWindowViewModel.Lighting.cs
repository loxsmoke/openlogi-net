using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using OpenLogi.Core;
using OpenLogi.Core.DeviceInfo;
using OpenLogi.Core.Logging;
using OpenLogi.Hid;

namespace OpenLogi.App.ViewModels;

// Keyboard lighting: the per-key editor bridge, live "No profile" colour,
// onboard profiles + G-keys, backlight brightness, and the keepalive that stops
// firmware from dropping host-mode lighting.
public partial class MainWindowViewModel
{
    /// <summary>
    /// Create a per-key color editor bound to the device's <em>current</em> session.
    /// A fresh instance each open (the session is replaced on every control reload,
    /// so a cached editor would hold a disposed handle); painted keys persist via
    /// config, seeded back in here, so nothing is lost by not caching.
    /// </summary>
    public PerKeyColorViewModel? CreatePerKeyEditor()
    {
        if (PerKeySession is not { } s) return null;
        var ck = SelectedDevice?.ConfigKey;
        var paint = ck is not null && _config.Lighting(ck)?.PaintColor is { } p
            ? (Avalonia.Media.Color?)ParseHexColor(p) : null;
        return new PerKeyColorViewModel(
            s, LightingColor, paint, LoadPerKeyColors(ck),
            (painted, baseColor, paintColor) => SavePerKeyColors(ck, painted, baseColor, paintColor));
    }

    /// <summary>Read a device's saved per-key colors (zone → color) from config; empty if none.</summary>
    private IReadOnlyDictionary<byte, Avalonia.Media.Color> LoadPerKeyColors(string? configKey)
    {
        var map = new Dictionary<byte, Avalonia.Media.Color>();
        if (configKey is not null && _config.Lighting(configKey)?.PerKey is { } stored)
            foreach (var (zone, hex) in stored) map[zone] = ParseHexColor(hex);
        return map;
    }

    private static string Hex(Avalonia.Media.Color c) => $"{c.R:x2}{c.G:x2}{c.B:x2}";

    /// <summary>Persist the editor's painted keys plus its base and paint colors into the device's lighting config.</summary>
    private void SavePerKeyColors(
        string? configKey, IReadOnlyDictionary<byte, Avalonia.Media.Color> painted,
        Avalonia.Media.Color baseColor, Avalonia.Media.Color paintColor)
    {
        if (configKey is null) return;
        var perKey = new Dictionary<byte, string>();
        foreach (var (zone, c) in painted) perKey[zone] = Hex(c);
        var existing = _config.Lighting(configKey);
        _config.SetLighting(configKey, new Lighting
        {
            Enabled = existing?.Enabled ?? LightingEnabled,
            Color = Hex(baseColor),       // base color == the unpainted-key (solid) color
            Brightness = existing?.Brightness ?? (byte)LightingBrightness,
            PaintColor = Hex(paintColor), // the editor's brush color
            PerKey = perKey,
        });
        SaveConfig();
    }

    /// <summary>Seed lighting controls from the saved config (device read is optional).</summary>
    private void SeedLightingFromConfig(string configKey)
    {
        _loadingControls = true;
        var lighting = _config.Lighting(configKey);
        LightingEnabled = lighting?.Enabled ?? true;
        LightingBrightness = lighting?.Brightness ?? 100;
        LightingColor = ParseHexColor(lighting?.Color);
        SelectedEffect = LightingEffect.Solid;
        _loadingControls = false;
    }

    partial void OnBacklightValueChanged(double value)
    {
        if (_loadingControls || _session is null) return;
        _ = _session.ApplyBrightnessAsync((ushort)Math.Round(value));
    }

    /// <summary>Hand lighting back to the keyboard's onboard profile (its built-in effect/cycle).</summary>
    [RelayCommand]
    private async Task RestoreKeyboardDefault()
    {
        if (_session is not null) await _session.SetOnboardModeAsync(host: false);
    }

    /// <summary>Rebuild the profile list: a "No profile" entry (custom colour) + each onboard profile.</summary>
    private void RebuildProfiles(byte current)
    {
        Profiles.Clear();
        Profiles.Add(new ProfileSlotViewModel(0, current == 0)); // "No profile" = custom colour
        for (var i = 1; i <= _profileCount; i++)
            Profiles.Add(new ProfileSlotViewModel(i, i == current));
        SelectedProfileForEdit = current;
        ProfileSelected = current >= 1;
    }

    /// <summary>
    /// Populate the effect/colour/speed/brightness controls from a profile's stored
    /// lighting descriptor, so selecting a profile reflects what it's actually
    /// configured to do. No-op for "No profile" (0) or unreadable profiles.
    /// </summary>
    private async Task LoadProfileLightingAsync(int profileNumber)
    {
        if (_session is null || profileNumber < 1) return;
        if (await _session.ReadProfileEffectAsync((ushort)profileNumber) is not { } pl) return;
        var prev = _loadingControls;
        _loadingControls = true; // the partial handlers below would otherwise drive live lighting
        try
        {
            SelectedEffect = pl.Effect switch
            {
                DeviceSession.EffectOff => LightingEffect.Off,
                DeviceSession.EffectBreathing => LightingEffect.Breathing,
                DeviceSession.EffectCycle => LightingEffect.Cycle,
                _ => LightingEffect.Solid,
            };
            if (SelectedEffect is LightingEffect.Solid or LightingEffect.Breathing)
                LightingColor = Avalonia.Media.Color.FromRgb(pl.R, pl.G, pl.B);
            if (SelectedEffect == LightingEffect.Breathing && pl.PeriodMs > 0)
                LightingSpeed = pl.PeriodMs;
            if (SelectedEffect is LightingEffect.Breathing or LightingEffect.Cycle)
                LightingBrightness = pl.Brightness;
        }
        finally { _loadingControls = prev; }
    }

    /// <summary>Write a remapped G-key into the active onboard profile (persists on-device).</summary>
    private void OnGKeyChanged(int index, byte usage, byte modifier)
    {
        if (_session is null || _gkeyProfileSector < 1) return;
        StatusText = $"Remapping G{index + 1}…";
        _ = WriteGKeyAsync(index, usage, modifier);
    }

    private async Task WriteGKeyAsync(int index, byte usage, byte modifier)
    {
        var ok = _session is not null && await _session.SetGKeyUsageAsync(_gkeyProfileSector, index, usage, modifier);
        StatusText = ok ? $"G{index + 1} remapped (profile {_gkeyProfileSector})." : "G-key remap failed.";
    }

    /// <summary>Persist the selected effect + colour + speed/brightness into the profile's flash (device-side).</summary>
    [RelayCommand]
    private async Task SaveLightingToProfile()
    {
        if (!ProfileSelected || _session is null || SelectedProfileForEdit < 1) return;
        var c = LightingColor;
        var effect = SelectedEffect switch
        {
            LightingEffect.Off => DeviceSession.EffectOff,
            LightingEffect.Breathing => DeviceSession.EffectBreathing,
            LightingEffect.Cycle => DeviceSession.EffectCycle,
            _ => DeviceSession.EffectFixed,
        };
        StatusText = $"Saving lighting to profile {SelectedProfileForEdit}…";
        var ok = await _session.SetProfileEffectAsync((ushort)SelectedProfileForEdit, effect,
            c.R, c.G, c.B, (ushort)LightingSpeed, (byte)LightingBrightness);
        StatusText = ok ? $"Saved to profile {SelectedProfileForEdit}." : "Profile save failed.";
    }

    /// <summary>Mark "No profile" active (a custom colour/effect is driving the keyboard).</summary>
    private void ClearProfileSelection()
    {
        if (_profileCount > 0) RebuildProfiles(0);
    }

    /// <summary>
    /// Select a lighting source: an onboard profile (smooth, device-side, survives app
    /// close) or "No profile" (drop to host mode + apply the configured custom colour).
    /// </summary>
    [RelayCommand]
    private async Task SwitchProfile(ProfileSlotViewModel? slot)
    {
        if (slot is null || slot.IsCurrent || _session is null) return;
        _effectCts?.Cancel();
        _effectCts = null;

        if (slot.Number == 0)
        {
            RebuildProfiles(0);                  // "No profile" selected
            await ApplyNoProfileLightingAsync(); // saved per-key colors, else solid
            PersistLightingProfile(0);           // remember it so wake can restore it
            return;
        }

        await _session.SwitchProfileAsync((byte)slot.Number); // ensures onboard mode internally
        if (await _session.ReadProfilesAsync() is { } prof)
        {
            RebuildProfiles(prof.Current);
            await LoadProfileLightingAsync(prof.Current);
            PersistLightingProfile(prof.Current);
        }
    }

    /// <summary>Persist which lighting source is active (0 = No profile) so wake can restore it.</summary>
    private void PersistLightingProfile(int profile)
    {
        if (SelectedDevice?.ConfigKey is not { } ck) return;
        _config.SetLightingProfile(ck, profile);
        SaveConfig();
    }

    // Live host-mode lighting only applies in "No profile" mode; with a real profile
    // selected the colour picker instead edits that profile (saved via SaveColorToProfile).
    partial void OnLightingEnabledChanged(bool value) { if (!_loadingControls && !ProfileSelected) RestartLighting(); }
    partial void OnLightingColorChanged(Avalonia.Media.Color value) { if (!_loadingControls && !ProfileSelected) RestartLighting(); }
    partial void OnLightingBrightnessChanged(int value) { if (!_loadingControls && !ProfileSelected) RestartLighting(); }
    partial void OnSelectedEffectChanged(LightingEffect value) { if (!_loadingControls && !ProfileSelected) RestartLighting(); }

    /// <summary>Persist + apply the live "No profile" lighting (always a solid colour; app-driven animations are disabled).</summary>
    private void RestartLighting()
    {
        SaveLightingConfig();
        ClearProfileSelection(); // a live custom colour means no onboard profile is active
        _ = ApplySolidAsync();
    }

    /// <summary>
    /// Apply the "No profile" lighting: the saved per-key colors (base fill + painted
    /// keys) when the device supports per-key and any are saved, else the solid custom
    /// colour. Lets selecting "No profile" restore custom key colors without opening
    /// the editor. Per-key colors are applied at full value (matching the editor), so
    /// the Intensity slider doesn't scale them.
    /// </summary>
    private async Task ApplyNoProfileLightingAsync()
    {
        if (_session is null) return;
        var perKey = SelectedDevice?.ConfigKey is { } ck ? _config.Lighting(ck)?.PerKey : null;
        await ApplyNoProfileLightingToAsync(_session, LightingEnabled, LightingColor, (byte)LightingBrightness, perKey);
    }

    /// <summary>
    /// Push the "No profile" lighting onto <paramref name="session"/> from stored config:
    /// the saved per-key colours (base fill + painted keys) when the device supports
    /// per-key and any are saved, else the solid custom colour (scaled by brightness).
    /// Shared by the live editor path and the sleep/wake re-assert.
    /// </summary>
    private static async Task ApplyNoProfileLightingToAsync(
        DeviceSession session, bool enabled, Avalonia.Media.Color baseColor, byte brightness,
        IReadOnlyDictionary<byte, string>? perKey)
    {
        if (enabled && session.SupportsPerKey && perKey is { Count: > 0 })
        {
            var map = new Dictionary<byte, (byte R, byte G, byte B)>(perKey.Count);
            foreach (var (zone, hex) in perKey)
            {
                var c = ParseHexColor(hex);
                map[zone] = (c.R, c.G, c.B);
            }
            await session.ApplyPerKeyMapAsync(baseColor.R, baseColor.G, baseColor.B, map);
        }
        else
        {
            byte r = 0, g = 0, b = 0;
            if (enabled)
            {
                var scale = brightness / 100.0;
                r = (byte)Math.Clamp(baseColor.R * scale, 0, 255);
                g = (byte)Math.Clamp(baseColor.G * scale, 0, 255);
                b = (byte)Math.Clamp(baseColor.B * scale, 0, 255);
            }
            await session.ApplyLightingAsync(r, g, b);
        }
    }

    /// <summary>
    /// Runs while the inventory sweep still holds a live HID++ channel to a
    /// just-probed receiver device. A G915 on LIGHTSPEED naps ~1 s after the user
    /// stops typing and then ignores all host traffic, so this — right after a
    /// successful probe, typically while the wake keypress is still fresh — is the
    /// one reliable moment to restore the persisted "No profile" per-key colours.
    /// Also logs which power/backlight features exist, for the nap diagnosis.
    /// </summary>
    private async Task OnReceiverDeviceProbedAsync(PairedDevice paired, HidPP.Channel.HidppChannel channel, HidPP.Device.HidppDevice device)
    {
        if (paired.ModelInfo is not { } mi) return;
        var key = mi.ConfigKey();
        bool Has(ushort id) => device.FeatureIndex(id) is not null;
        DiagnosticLog.Info("probe",
            $"{key}: features — power 0x1830:{Has(0x1830)} backlight2 0x1982:{Has(0x1982)} illumination 0x1990:{Has(0x1990)}"
            + $" onboard 0x8100:{Has(0x8100)} perkey 0x8081:{Has(0x8081)} rgb 0x8071:{Has(0x8071)}");

        var session = DeviceSession.Attach(channel, device);
        if (Has(0x8100)) await DumpProfileSectorsOnceAsync(session, key);

        if (_config.Lighting(key) is not { Profile: 0 } lighting || lighting.PerKey.Count == 0) return;
        await ReassertNoProfileLightingAsync(session, key);
        DiagnosticLog.Info("lightwake", $"{key}: No-profile colours restored during the sweep's probe");
    }

    // One-shot diagnostic: dump onboard profile sector 1 next to its factory ROM twin
    // (sector 0x0101) and log which bytes differ — to locate the corrupted power-save
    // timeout that makes the G915 blank its lights ~1 s after the last keystroke
    // (state written into the keyboard around the 2026-07 Windows update; survives
    // power-cycles). Retried on later wakes until one full dump succeeds.
    private bool _profileDumpDone;
    private async Task DumpProfileSectorsOnceAsync(DeviceSession session, string key)
    {
        if (_profileDumpDone) return;
        var live = await session.ReadFullSectorAsync(0x0001, 255);
        if (live is null) return;
        DiagnosticLog.Info("probe", $"{key}: profile sector 0x0001: {Convert.ToHexString(live)}");
        var rom = await session.ReadFullSectorAsync(0x0101, 255);
        if (rom is not null)
        {
            DiagnosticLog.Info("probe", $"{key}: ROM sector 0x0101: {Convert.ToHexString(rom)}");
            var diffs = new List<string>();
            for (var i = 0; i < 253 && diffs.Count < 200; i++)
                if (live[i] != rom[i]) diffs.Add($"[{i}]{rom[i]:x2}→{live[i]:x2}");
            DiagnosticLog.Info("probe", $"{key}: profile-vs-ROM diff ({diffs.Count} bytes): {string.Join(" ", diffs)}");
        }
        _profileDumpDone = true;
    }

    /// <summary>Re-apply a device's persisted "No profile" custom lighting onto a session (config-driven).</summary>
    private async Task ReassertNoProfileLightingAsync(DeviceSession session, string? configKey)
    {
        var lighting = configKey is not null ? _config.Lighting(configKey) : null;
        await ApplyNoProfileLightingToAsync(
            session, lighting?.Enabled ?? true, ParseHexColor(lighting?.Color), lighting?.Brightness ?? 100, lighting?.PerKey);
    }

    // Host-mode "No profile" lighting is volatile: the firmware drops it back to onboard
    // profile 1 after ~1-3 min without a refresh (HARDWARE-OBSERVED on the G915). So for a
    // gallery keyboard the user left in that mode, hold a session open and re-stream the
    // frame on this interval — a keepalive, like the official software streams continuously.
    private System.Threading.Timer? _lightingKeepaliveTimer;
    private DeviceSession? _keepaliveSession;
    private string? _keepaliveKey;
    private bool _keepaliveBusy;
    private static readonly TimeSpan LightingKeepaliveInterval = TimeSpan.FromSeconds(30);

    /// <summary>Set while the per-key colour editor window is open, so the keepalive doesn't overwrite live painting.</summary>
    public bool PerKeyEditorOpen { get; set; }

    /// <summary>
    /// Ensure the keepalive timer is running and refresh the lighting now. Called after every
    /// scan/activation/wake and when the viewed page changes, so a fresh apply lands promptly.
    /// </summary>
    private void PokeLightingKeepalive()
    {
        _lightingKeepaliveTimer ??= new System.Threading.Timer(
            _ => Dispatcher.UIThread.Post(() => _ = LightingKeepaliveTickAsync()),
            null, LightingKeepaliveInterval, LightingKeepaliveInterval);
        _ = LightingKeepaliveTickAsync();
    }

    /// <summary>
    /// Re-stream a "No profile" keyboard's custom colours so the firmware doesn't drop host
    /// mode back to onboard profile 1. Uses the viewed keyboard's live session when its page
    /// is open, otherwise a held dedicated session (for the gallery case). Skipped while a
    /// sweep runs or the per-key editor is open.
    /// </summary>
    private async Task LightingKeepaliveTickAsync()
    {
        if (_keepaliveBusy || IsScanning || PerKeyEditorOpen) return;
        _keepaliveBusy = true;
        try
        {
            var target = Devices.FirstOrDefault(d =>
                d.ConfigKey is { } ck && d.Route is not null && d.HasLighting
                && _config.Lighting(ck) is { Profile: 0 } l && l.PerKey.Count > 0);
            if (target?.ConfigKey is not { } key || target.Route is not { } route)
            {
                // A keyboard the sweep only partially probed has no config key, so it can
                // never match above and its colours silently stay unrestored — call it out.
                if (Devices.FirstOrDefault(d => d.ConfigKey is null && d.Device.Kind == DeviceKind.Keyboard) is { } partial)
                    DiagnosticLog.Info("lightwake",
                        $"keepalive: {partial.Device.Codename ?? "keyboard"} has no config key (partial probe) — colours not restored");
                await ReleaseKeepaliveSessionAsync();
                return;
            }

            // While its page is open, drive lighting on the live session — never open a
            // second session to the same (LIGHTSPEED) slot. On the gallery, hold a dedicated one.
            DeviceSession? session;
            if (ShowingDevice && ReferenceEquals(target, SelectedDevice))
            {
                await ReleaseKeepaliveSessionAsync();
                if (_session is null) return; // live session not ready yet — refresh on a later tick
                session = _session;
            }
            else
            {
                if (_keepaliveKey != key || _keepaliveSession is null)
                {
                    await ReleaseKeepaliveSessionAsync();
                    var opened = await DeviceSession.OpenAsync(route);
                    if (opened is null) return; // asleep/unreachable — retry next tick
                    _keepaliveSession = opened;
                    _keepaliveKey = key;
                    DiagnosticLog.Info("lightwake", $"{key}: No-profile lighting keepalive session opened ({LightingKeepaliveInterval.TotalSeconds:0}s)");
                }
                session = _keepaliveSession;
            }

            if (session is null || !session.SupportsPerKey) return;
            try { await ReassertNoProfileLightingAsync(session, key); }
            catch { /* asleep/unreachable — resumes on the next tick */ }
        }
        catch (Exception ex) { DiagnosticLog.Warn("lightwake", $"keepalive tick failed: {ex.Message}"); }
        finally { _keepaliveBusy = false; }
    }

    private async Task ReleaseKeepaliveSessionAsync()
    {
        if (_keepaliveSession is { } s)
        {
            _keepaliveSession = null;
            _keepaliveKey = null;
            await s.DisposeAsync();
        }
    }

    private async Task ApplySolidAsync()
    {
        if (_session is null) return;
        byte r = 0, g = 0, b = 0;
        if (LightingEnabled)
        {
            var c = LightingColor;
            var scale = LightingBrightness / 100.0;
            r = (byte)Math.Clamp(c.R * scale, 0, 255);
            g = (byte)Math.Clamp(c.G * scale, 0, 255);
            b = (byte)Math.Clamp(c.B * scale, 0, 255);
        }
        await _session.ApplyLightingAsync(r, g, b);
    }

    private void SaveLightingConfig()
    {
        if (SelectedDevice?.ConfigKey is not { } ck) return;
        var c = LightingColor;
        var existing = _config.Lighting(ck);
        _config.SetLighting(ck, new Lighting
        {
            Enabled = LightingEnabled,
            Color = $"{c.R:x2}{c.G:x2}{c.B:x2}",
            Brightness = (byte)LightingBrightness,
            PaintColor = existing?.PaintColor,
            PerKey = existing?.PerKey ?? new Dictionary<byte, string>(),
        });
        SaveConfig();
    }
}
