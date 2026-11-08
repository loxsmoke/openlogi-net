using System;

namespace OpenLogi.App.Tray;

/// <summary>
/// Decides when a tray icon whose device vanished may actually be hidden. Around
/// sleep/hibernate the device list briefly loses wireless devices, and that is also
/// when Explorer drops <c>Shell_NotifyIcon(NIM_DELETE)</c> calls (Avalonia ignores the
/// failure), which left orphaned battery icons behind. So hides are held from suspend
/// until <see cref="Duration"/> after resume; by then the device is usually back and
/// the icon never has to go.
/// </summary>
public sealed class TrayPowerGrace
{
    public static readonly TimeSpan Duration = TimeSpan.FromSeconds(30);

    private bool _suspended;
    private DateTime _graceUntilUtc = DateTime.MinValue;

    public void OnSuspend() => _suspended = true;

    public void OnResume(DateTime nowUtc)
    {
        _suspended = false;
        _graceUntilUtc = nowUtc + Duration;
    }

    /// <summary>True while hiding a vanished device's icon should wait.</summary>
    public bool HoldsHides(DateTime nowUtc) => _suspended || nowUtc < _graceUntilUtc;
}
