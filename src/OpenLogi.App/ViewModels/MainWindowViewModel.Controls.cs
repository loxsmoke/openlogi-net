using CommunityToolkit.Mvvm.Input;
using OpenLogi.Core.Config;
using OpenLogi.Core.Logging;
using OpenLogi.Hid;
using OpenLogi.Core.Localization;

namespace OpenLogi.App.ViewModels;

// Live pointer/wheel controls (DPI + presets, SmartShift, scroll direction,
// smooth scrolling) and the EasySwitch hosts section.
public partial class MainWindowViewModel
{
    /// <summary>Switch the device to another EasySwitch host (disconnects it from this PC).</summary>
    [RelayCommand]
    private async Task SwitchHost(HostSlotViewModel? slot)
    {
        if (slot is null || slot.IsCurrent || _session is null) return;
        SetStatus("Status_SwitchingHost", slot.Number);
        await _session.SwitchHostAsync((byte)slot.Index);
    }

    /// <summary>
    /// Forget (clear) an EasySwitch host so its slot is freed; that computer must
    /// re-pair. Confirmation is handled by the caller (the view shows a warning
    /// dialog, with a stronger one for the current host).
    /// </summary>
    public async Task ForgetHostAsync(HostSlotViewModel slot)
    {
        if (_session is null || SelectedDevice is not { } device) return;
        var wasCurrent = slot.IsCurrent;
        SetStatus("Status_ForgettingHost", slot.Number);
        if (!await _session.ClearHostAsync((byte)slot.Index))
        {
            SetStatus("Status_ForgetHostFailed", slot.Number);
            return;
        }
        if (wasCurrent)
        {
            // The device just dropped off this computer — return to the gallery and rescan.
            SetStatus("Status_HostForgotten", slot.Number);
            await RefreshAsync();
            return;
        }
        // Refresh so the freed slot shows as empty.
        if (await _session.ReadHostsAsync() is { } hosts && ReferenceEquals(SelectedDevice, device))
            RebuildHosts(hosts);
        SetStatus("Status_HostForgottenKeptConnected", slot.Number);
    }

    private void RebuildHosts(HostSnapshot hosts)
    {
        Hosts.Clear();
        foreach (var hd in hosts.Hosts)
            Hosts.Add(new HostSlotViewModel(hd.Index, hd.IsCurrent, hd.Paired, hd.BusType, hd.Name, hosts.SupportsDelete));
        HostsSupportClear = hosts.SupportsDelete;
        ShowHosts = hosts.HostCount > 1;
    }

    /// <summary>
    /// A drag fires one value change per 50 DPI tick (20+ per drag, HARDWARE-OBSERVED
    /// 2026-10-15), each of which used to be a config write and a HID++ round trip.
    /// Only the value the slider settles on matters, so changes are coalesced: the
    /// commit runs this long after the last change.
    /// </summary>
    private static readonly TimeSpan DpiDebounce = TimeSpan.FromMilliseconds(150);
    private CancellationTokenSource? _dpiDebounce;

    partial void OnDpiValueChanged(double value)
    {
        if (_loadingControls) return;
        var snapped = SnapDpi(_dpiOptions, (uint)Math.Round(value));
        // Resolve the target at the moment of the change, not when the commit or the
        // device round trip completes. The user can navigate to another device while
        // the debounce runs or a slow write (a napping BLE mouse) is in flight, and
        // reading SelectedDevice afterwards filed the DPI under that other device —
        // the mouse then kept its stale saved value and every reconnect re-pushed it
        // (gh-18). The commit saves the config first: it is the source of truth the
        // keepalive re-pushes when the write is rejected.
        var ck = SelectedDevice?.ConfigKey;
        var session = _session;
        _dpiDebounce?.Cancel();
        var cts = _dpiDebounce = new CancellationTokenSource();
        _ = CommitDpiAfterDebounceAsync(cts, ck, session, snapped);
    }

    private async Task CommitDpiAfterDebounceAsync(CancellationTokenSource cts, string? ck, DeviceSession? session, uint dpi)
    {
        try { await Task.Delay(DpiDebounce, cts.Token); }
        catch (OperationCanceledException) { cts.Dispose(); return; } // superseded by a newer change
        if (ReferenceEquals(_dpiDebounce, cts)) _dpiDebounce = null;
        cts.Dispose();
        // Back on the UI thread (Task.Delay resumed on the dispatcher context).
        if (ck is not null) { _config.SetDpi(ck, dpi); SaveConfig(); }
        await PushDpiAsync(session, ck, dpi);
    }

    private static async Task PushDpiAsync(DeviceSession? session, string? ck, uint dpi)
    {
        if (session is null) return;
        var ok = await session.ApplyDpiAsync((ushort)dpi);
        DiagnosticLog.Info("dpi", $"{ck ?? "?"}: slider push {dpi} {(ok ? "ok" : "failed — keepalive will retry")}");
    }

    /// <summary>Snap a DPI value to the nearest device-supported value (the raw value when none are known).</summary>
    internal static uint SnapDpi(IReadOnlyList<uint> options, uint dpi) =>
        options.Count == 0 ? dpi : options.MinBy(v => Math.Abs((long)v - dpi));

    private static double SmallestGap(IReadOnlyList<uint> options)
    {
        var sorted = options.OrderBy(v => v).ToArray();
        var gap = uint.MaxValue;
        for (var i = 1; i < sorted.Length; i++)
            gap = Math.Min(gap, sorted[i] - sorted[i - 1]);
        return sorted.Length < 2 || gap == 0 ? 50 : gap;
    }

    private void LoadDpiPresets(string? configKey)
    {
        DpiPresets.Clear();
        if (configKey is null) return;
        foreach (var p in _config.DpiPresets(configKey)) DpiPresets.Add(p);
    }

    [RelayCommand]
    private void AddDpiPreset()
    {
        var dpi = SnapDpi(_dpiOptions, (uint)Math.Round(DpiValue));
        DpiPresets.Add(dpi);
        SaveDpiPresets();
    }

    [RelayCommand]
    private void ApplyDpiPreset(uint dpi)
    {
        DpiValue = dpi; // triggers snap + apply via OnDpiValueChanged
    }

    [RelayCommand]
    private void RemoveDpiPreset(uint dpi)
    {
        DpiPresets.Remove(dpi);
        SaveDpiPresets();
    }

    private void SaveDpiPresets()
    {
        if (SelectedDevice?.ConfigKey is { } ck) { _config.SetDpiPresets(ck, [.. DpiPresets]); SaveConfig(); }
    }

    partial void OnWheelModeChoiceChanged(WheelModeChoice value) { if (!_loadingControls) _ = ApplySmartShiftAsync(); }
    partial void OnAutoDisengageChanged(int value) { if (!_loadingControls) _ = ApplySmartShiftAsync(); }

    private async Task ApplySmartShiftAsync()
    {
        // Free spin → wheel mode Free. SmartShift → Ratchet with a finite
        // auto-disengage threshold. Ratchet → Ratchet that never disengages (0xFF).
        var ratchet = WheelModeChoice != WheelModeChoice.FreeSpin;
        var auto = WheelModeChoice switch
        {
            WheelModeChoice.SmartShift => (byte)AutoDisengage,
            WheelModeChoice.Ratchet => (byte)0xFF,
            _ => (byte)0,
        };
        if (_session is not null) await _session.ApplySmartShiftAsync(ratchet, auto);
        if (SelectedDevice?.ConfigKey is { } ck)
        {
            _config.SetSmartShift(ck, new SmartShift
            {
                Mode = ratchet ? WheelMode.Ratchet : WheelMode.Free,
                AutoDisengage = auto,
                TunableTorque = 0,
            });
            SaveConfig();
        }
    }

    partial void OnInvertScrollChanged(bool value)
    {
        if (_loadingControls) return;
        // Same target-before-await rule as the DPI slider (gh-18): the invert bit is
        // pushed alongside the DPI on reconnect, so it must be filed under the right device.
        var ck = SelectedDevice?.ConfigKey;
        var session = _session;
        if (ck is not null) { _config.SetInvertScroll(ck, value); SaveConfig(); }
        if (session is not null) _ = session.ApplyScrollInvertAsync(value);
    }

    partial void OnSmoothScrollChanged(bool value) { if (!_loadingControls) _ = ApplySmoothScrollAsync(value); }

    private async Task ApplySmoothScrollAsync(bool enabled)
    {
        if (SelectedDevice?.ConfigKey is { } ck) { _config.SetSmoothScroll(ck, enabled); SaveConfig(); }
        // Re-arm the persistent captures so the wheel is diverted (or released) now,
        // not on the next reconnect.
        await RestartGestureCaptureForSelectedAsync();
    }

    /// <summary>
    /// Shake-to-locate is app-wide, so it needs no device call — the running agent
    /// reads the setting per pointer event, and the guard makes the read-back that
    /// seeds the checkbox at startup a no-op rather than a config write.
    /// </summary>
    partial void OnShakeToLocateChanged(bool value)
    {
        if (value == _config.AppSettings.ShakeToLocate) return;
        _config.AppSettings.ShakeToLocate = value;
        SaveConfig();
    }
}
