using HidSharp;
using OpenLogi.Core.Logging;

namespace OpenLogi.Hid;

/// <summary>
/// Raises <see cref="Changed"/> shortly after the set of Logitech HID nodes on
/// the machine changes, so a waking Bluetooth mouse — whose HID node the OS
/// tears down while it sleeps and re-creates on reconnect — brings itself into
/// the gallery without the user pressing Refresh.
///
/// Backed by HidSharp's <see cref="DeviceList.Changed"/> (a DBT_DEVNODES_CHANGED
/// listener on Windows). Two filters keep it quiet:
/// <list type="bullet">
///   <item>a debounce, because a composite device announces each of its
///     collections as a separate event within a few milliseconds;</item>
///   <item>a signature check over the Logitech HID node paths, so unrelated
///     device churn (a USB stick, a webcam) doesn't trigger a rescan.</item>
/// </list>
/// Every path here is off the UI thread; the subscriber marshals as needed.
/// </summary>
public sealed class HidDeviceWatcher : IDisposable
{
    private readonly TimeSpan _quietPeriod;
    private readonly Func<string> _snapshot;
    private readonly object _gate = new();
    private Timer? _debounce;
    private string _lastSignature;
    private bool _disposed;

    /// <summary>Raised once per settled change to the Logitech HID node set.</summary>
    public event System.Action? Changed;

    /// <param name="quietPeriod">How long the device set must stay still before firing (default 750 ms).</param>
    public HidDeviceWatcher(TimeSpan? quietPeriod = null)
    {
        _quietPeriod = quietPeriod ?? TimeSpan.FromMilliseconds(750);
        _snapshot = LiveSignature;
        _lastSignature = _snapshot();
        DeviceList.Local.Changed += OnLocalChanged;
    }

    private void OnLocalChanged(object? sender, DeviceListChangedEventArgs e) => Bump();

    /// <summary>Register one raw device-change event; starts/extends the quiet-period timer.</summary>
    private void Bump()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _debounce ??= new Timer(_ => Settle(), null, Timeout.Infinite, Timeout.Infinite);
            _debounce.Change(_quietPeriod, Timeout.InfiniteTimeSpan);
        }
    }

    private void Settle()
    {
        // Snapshot (a SetupAPI enumeration) outside the lock; it must not block Bump.
        lock (_gate) { if (_disposed) return; }
        string current;
        try { current = _snapshot(); }
        catch (Exception ex) { DiagnosticLog.Warn("watch", $"node snapshot failed: {ex.Message}"); return; }

        lock (_gate)
        {
            if (_disposed || current == _lastSignature) return;
            _lastSignature = current;
        }
        DiagnosticLog.Info("watch", "Logitech HID node set changed — rescanning");
        try { Changed?.Invoke(); }
        catch (Exception ex) { DiagnosticLog.Warn("watch", $"rescan handler threw: {ex.Message}"); }
    }

    private static string LiveSignature() =>
        Signature(DeviceList.Local.GetHidDevices(HidDiscovery.LogitechVendorId).Select(d => d.DevicePath));

    /// <summary>
    /// A stable, order-independent signature of a set of device paths: the null
    /// state (no nodes) and any add/remove/rename yield a different string, while
    /// mere re-ordering or duplicate listings yield the same one.
    /// </summary>
    public static string Signature(IEnumerable<string> devicePaths) =>
        string.Join("\n", devicePaths
            .Select(p => p.ToLowerInvariant()) // Windows device paths are case-insensitive
            .Distinct()
            .OrderBy(p => p, StringComparer.Ordinal));

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            DeviceList.Local.Changed -= OnLocalChanged;
            _debounce?.Dispose();
            _debounce = null;
        }
    }
}
