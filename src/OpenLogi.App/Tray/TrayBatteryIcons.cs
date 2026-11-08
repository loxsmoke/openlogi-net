using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Threading;
using OpenLogi.App.ViewModels;
using OpenLogi.Core.Localization;
using OpenLogi.Core.Logging;

namespace OpenLogi.App.Tray;

/// <summary>
/// One system-tray icon per paired device that reports a battery, each drawn by
/// <see cref="TrayBatteryIcon"/> and kept current as the device list and battery
/// readings change. Icons are keyed by device identity rather than by view model
/// so a rescan (which recreates every <see cref="DeviceViewModel"/>) re-points the
/// existing native icons instead of flickering them off and on.
/// <para>
/// A device's icon is created once and only ever hidden and re-shown, never disposed
/// before the app exits. Avalonia's Win32 tray ignores a failed
/// <c>Shell_NotifyIcon(NIM_DELETE)</c> (Explorer drops them around sleep/resume), and a
/// lost delete on a disposed icon left an orphan in the tray for the rest of the run,
/// frozen at its last charge. Reusing the same native icon (same hWnd + uID) means
/// re-showing it repaints any such orphan instead of adding a duplicate, and hides are
/// also held across power transitions (<see cref="TrayPowerGrace"/>) and re-sent once.
/// </para>
/// </summary>
public sealed class TrayBatteryIcons : IDisposable
{
    private static readonly TimeSpan HideRetryDelay = TimeSpan.FromSeconds(10);

    private sealed class Entry
    {
        public required string Id { get; init; }
        public required TrayIcon Icon { get; init; }
        public required NativeMenuItem OpenItem { get; init; }
        public required NativeMenuItem QuitItem { get; init; }
        public DeviceViewModel? Device { get; set; }
        public TrayBatteryIconKey? Key { get; set; }
        public bool Shown { get; set; }
        // Bumped on every hide/show so a pending hide retry can tell it went stale.
        public int Generation { get; set; }
    }

    private readonly MainWindowViewModel _vm;
    private readonly Func<bool> _enabled;
    private readonly Func<double> _scaling;
    private readonly Action _open;
    private readonly Action _quit;
    private readonly Dictionary<string, Entry> _entries = new();
    private readonly Dictionary<TrayBatteryIconKey, WindowIcon> _cache = new();
    private readonly TrayPowerGrace _grace = new();
    private bool _subscribedToSystemEvents;
    private bool _disposed;

    /// <summary>Raised after the set of visible icons changed (from none to some, or back).</summary>
    public event Action? VisibilityChanged;

    public bool HasVisibleIcons => _entries.Values.Any(e => e.Shown);

    /// <param name="vm">Owner of the device list.</param>
    /// <param name="enabled">The "Show battery in tray" setting, read on every rebuild.</param>
    /// <param name="scaling">Taskbar scaling, read when an icon is first rendered.</param>
    /// <param name="open">Click / "Open" menu action: restore the window.</param>
    /// <param name="quit">"Quit" menu action.</param>
    public TrayBatteryIcons(MainWindowViewModel vm, Func<bool> enabled, Func<double> scaling, Action open, Action quit)
    {
        _vm = vm;
        _enabled = enabled;
        _scaling = scaling;
        _open = open;
        _quit = quit;

        _vm.Devices.CollectionChanged += OnDevicesChanged;
        Loc.Current.PropertyChanged += OnCultureChanged;
        try
        {
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
            _subscribedToSystemEvents = true;
        }
        catch { /* no message pump (tests); theme is re-read on every rebuild anyway */ }

        Rebuild();
    }

    /// <summary>Re-evaluates the setting and every device; call after the Settings dialog closes.</summary>
    public void Refresh() => Rebuild();

    private void OnDevicesChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void OnCultureChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(Loc.Culture) or "Item[]")) return;
        foreach (var entry in _entries.Values)
        {
            entry.OpenItem.Header = Loc.Current["Tray_Open"];
            entry.QuitItem.Header = Loc.Current["Tray_Quit"];
            if (entry.Shown && entry.Device is { } device) entry.Icon.ToolTipText = TooltipFor(device);
        }
    }

    // Theme switches arrive on a system-events thread; the key carries the taskbar
    // theme, so a plain rebuild re-renders everything in the new colours.
    private void OnUserPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category == Microsoft.Win32.UserPreferenceCategory.General)
            Dispatcher.UIThread.Post(Rebuild);
    }

    private void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        switch (e.Mode)
        {
            case Microsoft.Win32.PowerModes.Suspend:
                Dispatcher.UIThread.Post(() =>
                {
                    _grace.OnSuspend();
                    DiagnosticLog.Info("tray", "suspend: holding battery icon hides until after resume");
                });
                break;
            case Microsoft.Win32.PowerModes.Resume:
                Dispatcher.UIThread.Post(() =>
                {
                    _grace.OnResume(DateTime.UtcNow);
                    DiagnosticLog.Info("tray", $"resume: holding battery icon hides for {TrayPowerGrace.Duration.TotalSeconds:0} s");
                    // Apply whatever was held once the grace ends (small margin past it).
                    DispatcherTimer.RunOnce(Rebuild, TrayPowerGrace.Duration + TimeSpan.FromSeconds(1));
                });
                break;
        }
    }

    private void OnDevicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(DeviceViewModel.LiveBattery) or nameof(DeviceViewModel.HasBattery)
            or nameof(DeviceViewModel.BatteryPercent) or nameof(DeviceViewModel.BatteryCharging))) return;
        if (sender is not DeviceViewModel device) return;

        var entry = _entries.Values.FirstOrDefault(x => x.Shown && ReferenceEquals(x.Device, device));
        if (entry is null || !device.HasBattery) { Rebuild(); return; }
        Update(entry, device);
    }

    private void Rebuild()
    {
        if (_disposed) return;
        var hadIcons = HasVisibleIcons;
        var enabled = _enabled();
        var wanted = enabled
            ? _vm.Devices.Where(d => d.HasBattery).ToDictionary(IdentityOf, d => d)
            : new Dictionary<string, DeviceViewModel>();

        // A device that vanished around sleep/resume keeps its icon (last reading)
        // until the grace ends; turning the setting off still hides at once.
        var holdHides = enabled && _grace.HoldsHides(DateTime.UtcNow);
        foreach (var entry in _entries.Values.Where(x => x.Shown && !wanted.ContainsKey(x.Id)).ToList())
        {
            if (holdHides)
            {
                DiagnosticLog.Info("tray", $"battery icon for {entry.Id} kept through power transition");
                continue;
            }
            Hide(entry);
        }

        foreach (var (id, device) in wanted)
        {
            if (!_entries.TryGetValue(id, out var entry))
            {
                entry = Create(id);
                _entries[id] = entry;
                DiagnosticLog.Info("tray", $"battery icon added for {device.Name} ({device.Device.Kind})");
            }
            else if (!entry.Shown)
            {
                DiagnosticLog.Info("tray", $"battery icon shown again for {device.Name} ({device.Device.Kind})");
            }
            Attach(entry, device);
            Update(entry, device);
        }

        if (hadIcons != HasVisibleIcons) VisibilityChanged?.Invoke();
    }

    private Entry Create(string id)
    {
        var open = new NativeMenuItem { Header = Loc.Current["Tray_Open"] };
        open.Click += (_, _) => Dispatcher.UIThread.Post(_open);
        var quit = new NativeMenuItem { Header = Loc.Current["Tray_Quit"] };
        quit.Click += (_, _) => Dispatcher.UIThread.Post(_quit);

        var icon = new TrayIcon
        {
            IsVisible = false,
            Menu = new NativeMenu { Items = { open, quit } },
        };
        icon.Clicked += (_, _) => Dispatcher.UIThread.Post(_open);
        return new Entry { Id = id, Icon = icon, OpenItem = open, QuitItem = quit };
    }

    private void Attach(Entry entry, DeviceViewModel device)
    {
        if (ReferenceEquals(entry.Device, device)) return;
        Detach(entry);
        entry.Device = device;
        device.PropertyChanged += OnDevicePropertyChanged;
    }

    private void Detach(Entry entry)
    {
        if (entry.Device is { } old) old.PropertyChanged -= OnDevicePropertyChanged;
        entry.Device = null;
    }

    private void Update(Entry entry, DeviceViewModel device)
    {
        var reveal = !entry.Shown;
        var key = TrayBatteryIconKey.For(device.Device.Kind, device.BatteryPercent, device.BatteryCharging, TaskbarTheme.IsDark);
        if (entry.Key != key)
        {
            entry.Icon.Icon = IconFor(key);
            entry.Key = key;
        }
        if (reveal)
        {
            entry.Shown = true;
            entry.Generation++;
            entry.Icon.IsVisible = true;
            // If an earlier hide was lost, the old icon is still in the tray under this
            // same uID and the NIM_ADD above failed; a tooltip change is a NIM_MODIFY,
            // which repaints that orphan with the current icon and text.
            entry.Icon.ToolTipText = null;
        }
        entry.Icon.ToolTipText = TooltipFor(device);
    }

    private void Hide(Entry entry)
    {
        Detach(entry);
        entry.Shown = false;
        var generation = ++entry.Generation;
        entry.Icon.IsVisible = false;
        DiagnosticLog.Info("tray", $"battery icon hidden for {entry.Id}");

        // Send the delete once more in case Explorer dropped it. While the icon is not
        // added, Avalonia turns a tooltip change into NIM_DELETE (a no-op if it is gone).
        DispatcherTimer.RunOnce(() =>
        {
            if (_disposed || entry.Shown || entry.Generation != generation) return;
            var tip = entry.Icon.ToolTipText;
            entry.Icon.ToolTipText = null;
            entry.Icon.ToolTipText = tip;
        }, HideRetryDelay);
    }

    private WindowIcon IconFor(TrayBatteryIconKey key)
    {
        if (!_cache.TryGetValue(key, out var icon))
            _cache[key] = icon = TrayBatteryIcon.Render(key, TrayBatteryIcon.PixelSizeFor(_scaling()));
        return icon;
    }

    /// <summary>"MX Keys · 72% · Discharging", plus the asleep note for a napping wireless device.</summary>
    public static string TooltipFor(DeviceViewModel device) =>
        device.IsAsleep ? $"{device.Name} · {device.Battery} · {device.Status}" : $"{device.Name} · {device.Battery}";

    /// <summary>Identity that survives a rescan: receiver, slot, codename and kind.</summary>
    public static string IdentityOf(DeviceViewModel device) =>
        $"{device.ReceiverName}/{device.Device.Slot}/{device.Device.Codename}/{device.Device.Kind}";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _vm.Devices.CollectionChanged -= OnDevicesChanged;
        Loc.Current.PropertyChanged -= OnCultureChanged;
        if (_subscribedToSystemEvents)
        {
            try
            {
                Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
                Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            }
            catch { /* already gone */ }
        }
        foreach (var entry in _entries.Values)
        {
            Detach(entry);
            entry.Icon.IsVisible = false;
            entry.Icon.Dispose();
        }
        _entries.Clear();
    }
}
