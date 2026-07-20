using Avalonia.Threading;
using OpenLogi.Core.DeviceInfo;
using OpenLogi.Core.Logging;
using OpenLogi.Hid;

namespace OpenLogi.App.ViewModels;

// Wake/resume + device-topology handling: power events, HID node changes,
// receiver wake notifications, and the scoped/full rescans they trigger.
public partial class MainWindowViewModel
{
    /// <summary>
    /// On resume from sleep, the HID handles opened before sleep are dead, so every
    /// live mouse session is stale — writes silently fail and the mouse keeps the
    /// firmware DPI it reset to on wake. Rebuild the persistent sessions from scratch
    /// (which re-pushes DPI + scroll-invert as part of (re)connect), then, if a device
    /// page is open, rebind its controls to the fresh session so the UI works again.
    /// The watcher's node-change path is suppressed while a page is open, so this
    /// resume signal is what recovers the on-screen device.
    /// </summary>
    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode != Microsoft.Win32.PowerModes.Resume) return;
        Dispatcher.UIThread.Post(async () =>
        {
            // Let USB/BLE links re-establish before we re-enumerate and talk to devices.
            await Task.Delay(TimeSpan.FromSeconds(2));
            await ReconnectAfterResumeAsync();
        });
    }

    /// <summary>
    /// Re-establish device connectivity after a wake. Reopens the persistent per-mouse
    /// sessions (their pre-sleep HID handles are dead) — this reapplies each mouse's
    /// persisted DPI + scroll-invert on connect — and, when a device page is showing,
    /// reloads its controls so they bind to the new session instead of the stale one.
    /// </summary>
    private async Task ReconnectAfterResumeAsync()
    {
        // Serialize reconnects: the resume event and the watcher's node-change event can
        // both fire on wake, and several node events may arrive. Coalesce overlapping
        // requests into one trailing re-run so the final state always reflects reality.
        if (_reconnecting) { _reconnectQueued = true; return; }
        _reconnecting = true;
        try
        {
            do
            {
                _reconnectQueued = false;
                var showing = ShowingDevice ? SelectedDevice : null;
                // The on-screen device's persistent session is the stale _session, which
                // ActivateAgentMiceAsync deliberately keeps alive. Drop the reference so it
                // disposes that dead HID handle too — no lingering handle to block the fresh
                // open, and LoadControlsAsync then rebinds the UI to the new session.
                _session = null;
                await ActivateAgentMiceAsync([.. Devices.Where(d => d.HasButtons)]);
                if (showing is not null && ReferenceEquals(SelectedDevice, showing))
                    await LoadControlsAsync(showing);
            } while (_reconnectQueued);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"resume reconnect failed: {ex.Message}"); }
        finally { _reconnecting = false; }
    }

    /// <summary>
    /// The Logitech HID node set changed (raised off the UI thread). Rescan now if
    /// we're on the gallery and idle; otherwise defer until the user returns home,
    /// so an open device page and its live session are left intact.
    /// </summary>
    private void OnHidDeviceSetChanged() => Dispatcher.UIThread.Post(async () =>
    {
        // The receiver set may have changed too (a dongle plugged/unplugged) — re-bind
        // the receiver watcher so it tracks the current receivers.
        if (_receiverWatcher is not null) _ = _receiverWatcher.RefreshAsync();
        await HandleTopologyChangeAsync();
    });

    /// <summary>
    /// A receiver reported a paired device coming online (a wireless keyboard/mouse woke
    /// on its receiver, which leaves the HID node set unchanged). Refresh so its gallery
    /// tile reflects the live state — same policy as a node-set change — and rush the
    /// "No profile" lighting restore into the wake window, since the keyboard shows a
    /// black backlight in host mode until the colours are streamed.
    /// </summary>
    private void OnReceiverDeviceWoke(ReceiverConnectionWatcher.ReceiverWake wake) => Dispatcher.UIThread.Post(async () =>
    {
        _ = RushLightingAfterWakeAsync();
        // A keyboard-only wake from one known receiver: refresh just that receiver's
        // tiles. The full sweep pays every other node's ping timeouts (~8-10 s), while
        // the waking keyboard's reachable window is seconds — scoping gets its probe,
        // identity and lighting restored in ~1-2 s and leaves everything else alone.
        if (wake is { KeyboardOnly: true, VendorId: { } vid, ProductId: { } pid } && !ShowingDevice)
        {
            await RescanReceiverAsync(vid, pid);
            return;
        }
        await HandleTopologyChangeAsync(wake.KeyboardOnly);
    });

    /// <summary>
    /// Scoped rescan of one receiver's interfaces: rebuild only the tiles routed
    /// through it (matched by receiver uid, in place, so the gallery doesn't
    /// reshuffle) and leave every other device — and all mouse captures — untouched.
    /// </summary>
    private async Task RescanReceiverAsync(ushort vid, ushort pid)
    {
        if (IsScanning) { _rescanPending = true; return; }
        IsScanning = true;
        try
        {
            var inventories = await HidInventory.EnumerateAsync(
                postProbe: OnReceiverDeviceProbedAsync,
                nodeFilter: n => n.VendorId == vid && n.ProductId == pid);
            foreach (var inv in inventories)
                MergeInventoryIntoGallery(inv);
            StatusText = $"{Devices.Count} device(s).";
            PokeLightingKeepalive();
        }
        catch (System.Exception e)
        {
            StatusText = $"Rescan failed: {e.Message}";
        }
        finally
        {
            IsScanning = false;
            // A broader change landed while we were scoped — settle it with a full scan.
            if (_rescanPending && !ShowingDevice) _ = LoadAsync();
        }
    }

    /// <summary>
    /// Replace the gallery tiles belonging to one receiver with the fresh inventory,
    /// keeping their position in the gallery.
    /// </summary>
    private void MergeInventoryIntoGallery(DeviceInventory inv)
    {
        var uid = inv.Receiver.UniqueId;
        if (uid is null) return; // direct-device inventory can't match a receiver route

        var insertAt = Devices.Count;
        for (var i = Devices.Count - 1; i >= 0; i--)
            if (string.Equals(RouteReceiverUid(Devices[i].Route), uid, StringComparison.OrdinalIgnoreCase))
            {
                Devices.RemoveAt(i);
                insertAt = i;
            }

        foreach (var paired in inv.Paired)
        {
            if (!IsConfigurable(paired)) continue;
            // The full sweep's dedup hides an offline slot whose model is online
            // over another transport; the scoped sweep can't see those, so apply
            // the same rule against the tiles already on the gallery.
            if (!paired.Online && paired.Wpid is { } w
                && Devices.Any(d => d.Device.Online && d.Device.ModelInfo?.ModelIds.Contains(w) == true))
                continue;
            var vm = new DeviceViewModel(inv.Receiver.Name, RememberIdentity(paired), DeviceRoute.DeviceRouteFor(inv, paired.Slot));
            Devices.Insert(Math.Min(insertAt, Devices.Count), vm);
            insertAt++;
            if (vm.ConfigKey is not null)
                _ = ResolveCardImageAsync(vm);
        }
    }

    private static string? RouteReceiverUid(DeviceRoute? route) => route switch
    {
        DeviceRoute.Bolt b => b.ReceiverUid,
        DeviceRoute.Unifying u => u.ReceiverUid,
        DeviceRoute.Lightspeed l => l.ReceiverUid,
        _ => null,
    };

    /// <summary>
    /// After a receiver wake, a "No profile" per-key keyboard sits dark: host-mode
    /// lighting is volatile, and the device only answers intermittently while its RF
    /// link parks/unparks after the wake keypress — the 30 s keepalive cadence misses
    /// that window (its one open lands seconds late and fails). So rush it: once the
    /// wake rescan is done, retry the keepalive tick every few hundred ms until the
    /// colours land (a keepalive session exists) or the window closes.
    /// </summary>
    private bool _lightingRushActive;
    private async Task RushLightingAfterWakeAsync()
    {
        if (_lightingRushActive) return;
        _lightingRushActive = true;
        try
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            var attempts = 0;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(400);
                if (IsScanning) continue; // let the wake rescan finish first — concurrent slot opens fail
                if (ShowingDevice) return; // an open device page owns its session; LoadControlsAsync restores there

                await LightingKeepaliveTickAsync();
                attempts++;
                if (_keepaliveSession is not null)
                {
                    DiagnosticLog.Info("lightwake", $"wake rush: colours restored (attempt {attempts})");
                    return;
                }
                // Nothing to restore (no No-profile per-key keyboard identified) — bail
                // rather than spin; a partial probe is already logged by the tick itself.
                if (!Devices.Any(d => d.ConfigKey is { } ck && d.Route is not null && d.HasLighting
                        && _config.Lighting(ck) is { Profile: 0 } l && l.PerKey.Count > 0))
                    return;
            }
            DiagnosticLog.Info("lightwake", "wake rush: device never reachable — colours not restored");
        }
        catch (Exception ex) { DiagnosticLog.Warn("lightwake", $"wake rush failed: {ex.Message}"); }
        finally { _lightingRushActive = false; }
    }

    /// <summary>
    /// Shared response to the device set changing (a HID node arriving/leaving, or a
    /// receiver reporting a wake): rescan the gallery when idle; reconnect the on-screen
    /// device in place when a page is open (its session may have just died); defer a full
    /// rescan for when the user returns home so nothing else is missed.
    /// </summary>
    private async Task HandleTopologyChangeAsync(bool keyboardOnly = false)
    {
        // Whatever else happens below, let the unreachable-mice loop retry now: a
        // returned device fires this event long before the loop's backoff expires,
        // and its re-arm needs no sweep — this is what gets gestures back within a
        // second of a BLE mouse reconnecting after sleep.
        PokeRearmLoop();
        if (IsScanning) { _rescanPending = true; return; }
        if (ShowingDevice)
        {
            _rescanPending = true;
            // A keyboard waking can't invalidate an open pointer-device page's session
            // — and the reconnect would tear down every mouse capture, killing the
            // diverts of a mouse that happens to be napping right then. Only reconnect
            // when the shown device is itself a keyboard (to rebind its page).
            if (keyboardOnly && SelectedDevice?.Device.Kind != DeviceKind.Keyboard) return;
            await ReconnectAfterResumeAsync();
            return;
        }
        _ = LoadAsync(reactivateMice: !keyboardOnly);
    }
}
