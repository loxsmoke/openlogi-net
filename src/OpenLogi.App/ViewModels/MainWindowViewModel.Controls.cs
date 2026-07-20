using CommunityToolkit.Mvvm.Input;
using OpenLogi.Core.Config;
using OpenLogi.Hid;

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
        StatusText = $"Switching to host {slot.Number}…";
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
        StatusText = $"Forgetting host {slot.Number}…";
        if (!await _session.ClearHostAsync((byte)slot.Index))
        {
            StatusText = $"Could not forget host {slot.Number}.";
            return;
        }
        if (wasCurrent)
        {
            // The device just dropped off this computer — return to the gallery and rescan.
            StatusText = $"Host {slot.Number} forgotten — device disconnected.";
            await RefreshAsync();
            return;
        }
        // Refresh so the freed slot shows as empty.
        if (await _session.ReadHostsAsync() is { } hosts && ReferenceEquals(SelectedDevice, device))
            RebuildHosts(hosts);
        StatusText = $"Host {slot.Number} forgotten.";
    }

    private void RebuildHosts(HostSnapshot hosts)
    {
        Hosts.Clear();
        foreach (var hd in hosts.Hosts)
            Hosts.Add(new HostSlotViewModel(hd.Index, hd.IsCurrent, hd.Paired, hd.BusType, hd.Name, hosts.SupportsDelete));
        HostsSupportClear = hosts.SupportsDelete;
        ShowHosts = hosts.HostCount > 1;
    }

    partial void OnDpiValueChanged(double value)
    {
        if (_loadingControls) return;
        var snapped = Snap((uint)Math.Round(value));
        _ = ApplyDpiAsync(snapped);
    }

    private async Task ApplyDpiAsync(uint dpi)
    {
        if (_session is not null) await _session.ApplyDpiAsync((ushort)dpi);
        if (SelectedDevice?.ConfigKey is { } ck) { _config.SetDpi(ck, dpi); SaveConfig(); }
    }

    /// <summary>Snap a DPI value to the nearest device-supported value.</summary>
    private uint Snap(uint dpi)
    {
        if (_dpiOptions.Count == 0) return dpi;
        return _dpiOptions.MinBy(v => Math.Abs((long)v - dpi));
    }

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
        var dpi = Snap((uint)Math.Round(DpiValue));
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

    partial void OnInvertScrollChanged(bool value) { if (!_loadingControls) _ = ApplyScrollInvertAsync(value); }

    private async Task ApplyScrollInvertAsync(bool invert)
    {
        if (_session is not null) await _session.ApplyScrollInvertAsync(invert);
        if (SelectedDevice?.ConfigKey is { } ck) { _config.SetInvertScroll(ck, invert); SaveConfig(); }
    }

    partial void OnSmoothScrollChanged(bool value) { if (!_loadingControls) _ = ApplySmoothScrollAsync(value); }

    private async Task ApplySmoothScrollAsync(bool enabled)
    {
        if (SelectedDevice?.ConfigKey is { } ck) { _config.SetSmoothScroll(ck, enabled); SaveConfig(); }
        // Re-arm the persistent captures so the wheel is diverted (or released) now,
        // not on the next reconnect.
        await RestartGestureCaptureForSelectedAsync();
    }
}
