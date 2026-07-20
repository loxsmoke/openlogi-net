using OpenLogi.Core.Config;
using OpenLogi.Core.DeviceInfo;
using OpenLogi.Core.Logging;
using OpenLogi.Hid;

namespace OpenLogi.App.ViewModels;

// Persistent per-mouse capture sessions: HID++ button diverts (DPI/ModeShift,
// gesture owners, smooth scroll) that run for the app's lifetime.
public partial class MainWindowViewModel
{
    /// <summary>
    /// Open a persistent session for every connected mouse and divert its
    /// DPI/ModeShift button — and its dedicated gesture button (0x00c3) when that
    /// button owns gestures — so each mouse's HID++ overrides work whenever the app
    /// runs — independent of which page (if any) is open — and each dispatches its
    /// own bindings. The OS hook (Middle/Back/Forward) is global and can't tell mice
    /// apart, so it tracks the first mouse. Sessions are reused by
    /// <see cref="LoadControlsAsync"/> when viewing that mouse, so only one HID
    /// handle is ever held per device.
    /// </summary>
    private async Task ActivateAgentMiceAsync(IReadOnlyList<DeviceViewModel> mice)
    {
        // Keyboards report reprogrammable controls too (the G915 does, now that full
        // capabilities are persisted and receivers are swept first), but this path is
        // pointer-device overrides only: a keyboard in the list would grab the global
        // hook's "first mouse" slot — killing the real mouse's Middle/Back/Forward
        // bindings and gestures — and get pointless session opens while asleep.
        mice = [.. mice.Where(m => m.Device.Kind is not (DeviceKind.Keyboard or DeviceKind.Numpad))];
        var old = _mouseCaptures.ToArray();
        _mouseCaptures.Clear();
        foreach (var mc in old)
        {
            await mc.Capture.DisposeAsync();
            // Don't dispose a session the UI is still showing — LoadControlsAsync owns that.
            if (!ReferenceEquals(mc.Session, _session)) await mc.Session.DisposeAsync();
        }

        // The global OS hook applies one mouse's Middle/Back/Forward bindings.
        _agent.SetSelectedDevice(mice.FirstOrDefault()?.ConfigKey);

        var gen = ++_mouseActivationGen;
        var unreachable = new List<DeviceViewModel>();
        foreach (var mouse in mice)
            if (!await TryActivateMouseAsync(mouse, gen))
                unreachable.Add(mouse);
        if (unreachable.Count > 0) _ = RearmUnreachableMiceAsync(unreachable, gen);

        // Every (re)scan/reconnect/wake funnels through here, so this is where we (re)arm
        // the No-profile lighting keepalive — and re-apply on wake.
        PokeLightingKeepalive();
    }

    // Bumped by every ActivateAgentMiceAsync run; an older run's background re-arm
    // loop stops the moment a newer activation owns the capture set.
    private int _mouseActivationGen;

    /// <summary>
    /// Open one mouse's persistent capture session and arm its captures (restoring the
    /// volatile scroll-invert bit and persisted DPI on the way, since firmware resets
    /// both on wake). Returns <c>true</c> when there is nothing left to do for this
    /// mouse — captures armed, nothing capturable, unroutable, or a newer activation
    /// superseded this attempt — and <c>false</c> when the mouse was unreachable and
    /// the attempt is worth retrying.
    /// </summary>
    private async Task<bool> TryActivateMouseAsync(DeviceViewModel mouse, int gen)
    {
        if (mouse.Route is not { } route) return true;
        try
        {
            var session = await DeviceSession.OpenAsync(route);
            if (session is null) return false;
            var ck = mouse.ConfigKey; // capture this mouse's bindings, not the global selection
            if (ck is not null) await session.ApplyScrollInvertAsync(_config.InvertScroll(ck));
            if (ck is not null && _config.Dpi(ck) is { } savedDpi)
                await session.ApplyDpiAsync((ushort)savedDpi);

            var captures = await StartMouseCapturesAsync(session, ck);
            if (gen != _mouseActivationGen)
            {
                // A newer activation took over while this attempt was talking to the
                // device — its capture set owns the mouse now; discard this one's.
                foreach (var c in captures) await c.DisposeAsync();
                await session.DisposeAsync();
                return true;
            }
            if (captures.Count == 0) { await session.DisposeAsync(); return true; } // nothing capturable

            // The persistent capture session doubles as the gallery tile's live battery
            // feed. The scan-time snapshot goes stale the moment the user plugs in a
            // charge cable (no HID node changes, so nothing rescans) — seed a fresh
            // reading now and ride 0x1004 broadcasts for charger/level changes; the
            // monitor is disposed with the capture set. Events resolve the tile by
            // config key at delivery time, because keyboard-only rescans replace the
            // DeviceViewModels while the captures (and this monitor) live on.
            if (await session.ReadBatteryAsync() is { } liveBattery) mouse.LiveBattery = liveBattery;
            if (ck is not null && session.StartBatteryMonitor(info =>
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        if (Devices.FirstOrDefault(d => d.ConfigKey == ck) is { } tile)
                            tile.LiveBattery = info;
                    })) is { } monitor)
                captures.Add(monitor);

            _mouseCaptures.Add(new MouseCapture(mouse, session, new CompositeCapture(captures)));
            return true;
        }
        catch { return false; } // unreachable — worth retrying
    }

    // Wakes the unreachable-mice retry loop early: a returning device raises a
    // node/receiver event long before the loop's backoff expires (observed
    // 2026-07-19: a BLE mouse came back mid-backoff and gestures stayed dead for
    // the ~5 s sweep the node event kicked off, while the loop sat in a 40 s wait).
    private TaskCompletionSource? _rearmPoke;

    /// <summary>Skip the re-arm loop's remaining backoff — a topology event means the missing device may be back.</summary>
    private void PokeRearmLoop() => _rearmPoke?.TrySetResult();

    /// <summary>
    /// Re-arm captures for mice that were unreachable when the capture set was rebuilt.
    /// A mouse can be gone at exactly the wrong moment — e.g. a low-battery brown-out
    /// during the rescan that tore its captures down (observed 2026-07-17: captures
    /// released "device gone", the rebuild's open failed, gestures stayed dead until a
    /// manual refresh). A device that returns without a node change never triggers a
    /// rescan, so nothing external retries — this loop does, with backoff, until the
    /// mouse answers or a newer activation supersedes it. A topology event
    /// (<see cref="PokeRearmLoop"/>) cuts the wait short: the returned device can be
    /// re-armed in under a second, without waiting for the sweep that event started.
    /// </summary>
    private async Task RearmUnreachableMiceAsync(List<DeviceViewModel> mice, int gen)
    {
        DiagnosticLog.Warn("capture",
            $"{mice.Count} pointer device(s) unreachable during capture activation — retrying in the background");
        var delay = TimeSpan.FromSeconds(5);
        var poke = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _rearmPoke = poke;
        try
        {
            while (mice.Count > 0 && gen == _mouseActivationGen)
            {
                if (await Task.WhenAny(Task.Delay(delay), poke.Task) == poke.Task)
                {
                    // Poked: the device may just have returned — retry immediately and
                    // restart the backoff so a false alarm degrades gracefully.
                    poke = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _rearmPoke = poke;
                    delay = TimeSpan.FromSeconds(5);
                    DiagnosticLog.Info("capture", "device-set change poked the capture re-arm loop — retrying now");
                }
                else
                {
                    delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, TimeSpan.FromSeconds(60).Ticks));
                }
                foreach (var mouse in mice.ToArray())
                {
                    if (gen != _mouseActivationGen) return;
                    if (!await TryActivateMouseAsync(mouse, gen)) continue;
                    mice.Remove(mouse);
                    if (gen == _mouseActivationGen)
                        DiagnosticLog.Info("capture", $"{mouse.Name}: captures re-armed after the device returned");
                }
            }
        }
        finally
        {
            if (ReferenceEquals(_rearmPoke, poke)) _rearmPoke = null;
        }
    }

    /// <summary>
    /// Start a mouse's HID++ captures on an open <paramref name="session"/>, honouring
    /// its gesture owner: the DPI/ModeShift button (unless that button is itself the
    /// gesture owner) plus the gesture owner's control diverted with raw-XY. Shared by
    /// the initial activation and the owner-change restart.
    /// </summary>
    private async Task<List<IAsyncDisposable>> StartMouseCapturesAsync(DeviceSession session, string? ck)
    {
        var gestureButtons = ck is not null ? _config.GestureButtons(ck) : [];
        var captures = new List<IAsyncDisposable>();
        // Capture the DPI/ModeShift button — unless it has gestures configured, in
        // which case the gesture capture below diverts the same control instead.
        if (!gestureButtons.Contains(ButtonId.DpiToggle)
            && await session.StartDpiButtonCaptureAsync(() => _agent.DispatchDivertedButton(ck, ButtonId.DpiToggle)) is { } dpi)
            captures.Add(dpi);
        // Divert every gesture-configured button's control with raw-XY — several may
        // gesture at once (including Middle/Back/Forward, which on Windows also
        // gesture over HID++). A button the device can't divert is a null no-op.
        if (ck is not null)
            foreach (var button in gestureButtons)
            {
                var b = button; // each capture dispatches its own button's map
                if (await session.StartGestureCaptureAsync(b, dir => _agent.DispatchGesture(ck, b, dir)) is { } gesture)
                    captures.Add(gesture);
            }
        // Divert the wheel into hi-res mode and re-inject its motion as smooth OS
        // scrolling when the user turned it on (a null capture = no 0x2121).
        if (ck is not null && _config.SmoothScroll(ck)
            && await session.StartSmoothScrollCaptureAsync(w => _agent.DispatchSmoothScroll(w)) is { } smooth)
            captures.Add(smooth);
        return captures;
    }

    /// <summary>
    /// Re-arm the currently-viewed mouse's captures on its existing session after a
    /// divert-affecting setting changed (gesture owner, smooth scrolling) — so the
    /// newly-chosen control gets diverted (and the old one released). Runs on the
    /// persistent session the UI already holds, so no second HID handle is opened.
    /// A no-op for a mouse with no persistent capture session.
    /// </summary>
    private async Task RestartGestureCaptureForSelectedAsync()
    {
        if (SelectedDevice is not { } device || device.ConfigKey is null) return;
        var mc = _mouseCaptures.FirstOrDefault(m => ReferenceEquals(m.Device, device));
        if (mc is null)
        {
            // No persistent capture yet (nothing was capturable at activation) — a
            // newly-enabled capture can still ride the session the UI holds open;
            // adding it to _mouseCaptures makes that session persistent.
            if (_session is null) return;
            var fresh = await StartMouseCapturesAsync(_session, device.ConfigKey);
            if (fresh.Count > 0)
                _mouseCaptures.Add(new MouseCapture(device, _session, new CompositeCapture(fresh)));
            return;
        }

        await mc.Capture.DisposeAsync();
        _mouseCaptures.Remove(mc);
        var captures = await StartMouseCapturesAsync(mc.Session, device.ConfigKey);
        if (captures.Count > 0)
            _mouseCaptures.Add(mc with { Capture = new CompositeCapture(captures) });
    }
}
