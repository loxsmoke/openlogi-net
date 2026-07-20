using OpenLogi.Core.DeviceInfo;
using OpenLogi.Hid;

namespace OpenLogi.App.ViewModels;

// Loading a device page: session acquisition (with the wake retry) and the
// per-capability control sections (battery, DPI, wheel, scroll, backlight,
// profiles, G-keys).
public partial class MainWindowViewModel
{
    /// <summary>
    /// Open a session for a device that may be waking from power-saving. A wireless
    /// keyboard that's asleep enumerates only partially, so the first open can land on
    /// an interface missing the control features (OnboardProfiles 0x8100 / G-keys
    /// 0x8010 / per-key 0x8081) — leaving the Profiles, G-Keys and battery readouts
    /// empty. So for a keyboard we keep re-opening (with backoff) until the session
    /// actually exposes a control feature, or the open fails outright. This keys off
    /// the SESSION's own features, not the flaky startup-scan capability guess, so it
    /// fires even when the scan itself missed the control interface. The repeated
    /// traffic wakes the device — automating the manual Refresh that used to be
    /// needed. Zero extra latency for an already-awake device (returns on first open).
    /// </summary>
    private async Task<DeviceSession?> OpenWokenAsync(DeviceViewModel device, DeviceRoute route)
    {
        const int attempts = 6;
        var isKeyboard = device.Device.Kind == DeviceKind.Keyboard;
        DeviceSession? session = null;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try { session = await DeviceSession.OpenAsync(route); }
            catch { session = null; }

            // The user navigated away while we were opening — discard and bail.
            if (!ReferenceEquals(SelectedDevice, device))
            {
                if (session is not null) await session.DisposeAsync();
                return null;
            }

            // Good when the open succeeded and either it isn't a keyboard or this
            // interface actually exposes a control feature (profiles/G-keys/per-key).
            var hasControl = session is not null
                && (session.SupportsOnboardProfiles || session.SupportsGKeys || session.SupportsPerKey);
            if (session is not null && (!isKeyboard || hasControl))
                return session;
            if (attempt == attempts - 1)
                return session;

            // Wrong/partial interface (or no answer at all): wake it and retry.
            if (session is not null) await session.DisposeAsync();
            session = null;
            await Task.Delay(Math.Min(200 * (attempt + 1), 600)).ConfigureAwait(false);
        }
        return session;
    }

    private async Task LoadControlsAsync(DeviceViewModel? device)
    {
        // Stop the previous device's live battery listener before switching.
        if (_batteryMonitor is not null) { await _batteryMonitor.DisposeAsync(); _batteryMonitor = null; }

        var old = _session;
        _session = null;
        // Persistent per-mouse sessions (which run the DPI-button capture) are owned by
        // ActivateAgentMiceAsync — never dispose them here, only throwaway sessions.
        if (old is not null && !IsPersistentSession(old)) await old.DisposeAsync();

        if (device?.Route is not { } route) { IsLoadingDevice = false; return; }
        IsLoadingDevice = true; // show the thin loading line while the device's controls load

        var session = await AcquireSessionAsync(device, route);
        if (session is null) { IsLoadingDevice = false; return; }
        _session = session;
        ShowPerKeyEditor = session.SupportsPerKey;

        await LoadBatterySectionAsync(session, device);

        _loadingControls = true;
        try
        {
            await LoadDpiSectionAsync(session, device);
            await LoadSmartShiftSectionAsync(session, device);
            await LoadScrollSectionAsync(session, device);
            await BuildGestureSectionAsync(session, device);
            if (await session.ReadHostsAsync() is { } hosts && ReferenceEquals(SelectedDevice, device))
                RebuildHosts(hosts);
            await LoadBacklightSectionAsync(session, device);
            var gkeyProfile = await LoadProfilesSectionAsync(session, device);
            await LoadGKeysSectionAsync(session, device, gkeyProfile);
        }
        finally { _loadingControls = false; IsLoadingDevice = false; }
    }

    /// <summary>
    /// Resolve the session for a device page: reuse the mouse's already-open persistent
    /// session when there is one, otherwise open a fresh one (with the wake retry).
    /// <c>null</c> when the open fails or the selection moved on meanwhile.
    /// </summary>
    private async Task<DeviceSession?> AcquireSessionAsync(DeviceViewModel device, DeviceRoute route)
    {
        // If the lighting keepalive holds a dedicated session for this device (it was on the
        // gallery), release it now so we don't open a second session to the same slot below —
        // concurrent opens to a LIGHTSPEED slot make the profile/lighting reads fail.
        if (device.ConfigKey is { } dck && _keepaliveKey == dck)
            await ReleaseKeepaliveSessionAsync();

        // Reuse this mouse's already-open persistent session instead of opening a
        // second HID handle to the same device.
        var persistent = _mouseCaptures.FirstOrDefault(mc => ReferenceEquals(mc.Device, device))?.Session;
        var session = persistent ?? await OpenWokenAsync(device, route);
        if (session is null) return null;
        if (!ReferenceEquals(SelectedDevice, device))
        {
            if (!IsPersistentSession(session)) await session.DisposeAsync();
            return null;
        }
        return session;
    }

    /// <summary>
    /// Battery: read once on open (benefits from the wake-retry), then subscribe to
    /// 0x1004 broadcasts so the card/detail update live while the app is open.
    /// </summary>
    private async Task LoadBatterySectionAsync(DeviceSession session, DeviceViewModel device)
    {
        if (await session.ReadBatteryAsync() is { } battery && ReferenceEquals(SelectedDevice, device))
            device.LiveBattery = battery;
        if (ReferenceEquals(SelectedDevice, device))
            _batteryMonitor = session.StartBatteryMonitor(info =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (ReferenceEquals(SelectedDevice, device)) device.LiveBattery = info;
                }));
    }

    private async Task LoadDpiSectionAsync(DeviceSession session, DeviceViewModel device)
    {
        if (await session.ReadDpiAsync() is not { } dpi || !ReferenceEquals(SelectedDevice, device)
            || dpi.Supported.Count == 0)
            return;
        _dpiOptions = [.. dpi.Supported.Select(s => (uint)s)];
        DpiMin = _dpiOptions.Min();
        DpiMax = _dpiOptions.Max();
        DpiStep = SmallestGap(_dpiOptions);
        DpiValue = dpi.Current;
        DpiRangeText = $"{(uint)DpiMin}–{(uint)DpiMax} · step {(uint)DpiStep}";
        LoadDpiPresets(device.ConfigKey);
        ShowDpi = true;
    }

    private async Task LoadSmartShiftSectionAsync(DeviceSession session, DeviceViewModel device)
    {
        if (await session.ReadSmartShiftAsync() is not { } ss || !ReferenceEquals(SelectedDevice, device))
            return;
        WheelModeChoice = !ss.Ratchet ? WheelModeChoice.FreeSpin
            : ss.AutoDisengage == 0xFF ? WheelModeChoice.Ratchet
            : WheelModeChoice.SmartShift;
        AutoDisengage = ss.AutoDisengage is 0 or 0xFF ? 16 : ss.AutoDisengage;
        ShowSmartShift = true;
    }

    private async Task LoadScrollSectionAsync(DeviceSession session, DeviceViewModel device)
    {
        if (await session.ReadScrollInvertAsync() is not { } inverted || !ReferenceEquals(SelectedDevice, device))
            return;
        // The device's live state reflects the persisted preference (reapplied
        // on connect, since the 0x2121 invert bit is volatile).
        InvertScroll = inverted;
        ShowScrollInvert = true;
        // Smooth scrolling rides the same 0x2121 feature; its on/off is the
        // persisted choice (the capture applies it, so the config is the truth).
        SmoothScroll = device.ConfigKey is { } sck && _config.SmoothScroll(sck);
        ShowSmoothScroll = true;
    }

    private async Task LoadBacklightSectionAsync(DeviceSession session, DeviceViewModel device)
    {
        if (await session.ReadBrightnessAsync() is not { } bright || !ReferenceEquals(SelectedDevice, device))
            return;
        BacklightMax = bright.Info.MaxBrightness == 0 ? 100 : bright.Info.MaxBrightness;
        BacklightValue = bright.Current;
        ShowBacklight = true;
    }

    /// <summary>Load the onboard-profile section; returns the active profile sector for the G-key read (1 when unknown).</summary>
    private async Task<byte> LoadProfilesSectionAsync(DeviceSession session, DeviceViewModel device)
    {
        if (await session.ReadProfilesAsync() is not { } prof || !ReferenceEquals(SelectedDevice, device))
            return 1;
        _profileCount = prof.Info.ProfileCount;
        ShowProfiles = prof.Info.ProfileCount >= 1;
        // If the user's last choice was "No profile" (custom/host colours) but the
        // device is now on an onboard profile — the classic sleep/wake revert, since
        // host mode is volatile — restore the custom colours and show "No profile"
        // instead of reflecting the firmware's fallback.
        var wantNoProfile = device.ConfigKey is { } lck && _config.Lighting(lck) is { Profile: 0 };
        if (wantNoProfile && prof.Current >= 1)
        {
            await ReassertNoProfileLightingAsync(session, device.ConfigKey);
            RebuildProfiles(0);
        }
        else
        {
            RebuildProfiles(prof.Current);
            if (prof.Current >= 1 && ReferenceEquals(SelectedDevice, device))
                await LoadProfileLightingAsync(prof.Current);
        }
        return Math.Max(prof.Current, (byte)1);
    }

    /// <summary>
    /// Read G-keys independently of the profile read: a failed/empty profile
    /// read must not also hide the G-keys (they share the control interface but
    /// not the read). They only need the active profile sector, which defaults
    /// to 1 when the profile read didn't yield one.
    /// </summary>
    private async Task LoadGKeysSectionAsync(DeviceSession session, DeviceViewModel device, byte gkeyProfile)
    {
        if (!session.SupportsGKeys || !ReferenceEquals(SelectedDevice, device))
            return;
        _gkeyProfileSector = gkeyProfile;
        GKeyProfile = _gkeyProfileSector;
        if (await session.ReadGKeyBindingsAsync(_gkeyProfileSector) is not { } bindings
            || !ReferenceEquals(SelectedDevice, device))
            return;
        GKeys.Clear();
        for (var i = 0; i < bindings.Length; i++)
            GKeys.Add(new GKeyViewModel(i, bindings[i].Usage, bindings[i].Modifier, OnGKeyChanged));
        HasGKeysTab = true;
    }
}
