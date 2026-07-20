using System.Diagnostics;
using System.Threading.Channels;
using OpenLogi.Core.Gestures;
using OpenLogi.HidPP.Feature;
// Alias only what's needed from Core: a blanket `using OpenLogi.Core` would make the
// unqualified `Action` (used by StartDpiButtonCaptureAsync) ambiguous with System.Action.
using ButtonId = OpenLogi.Core.Config.ButtonId;
using DiagnosticLog = OpenLogi.Core.Logging.DiagnosticLog;

namespace OpenLogi.Hid;

public sealed partial class DeviceSession
{
    // ── Diverted-button capture (ReprogControls 0x1b04) ─────────────────────────

    /// <summary>
    /// The "DPI / ModeShift" button control-ID family. Whichever a device exposes
    /// and can divert is captured and surfaced as <see cref="ButtonId.DpiToggle"/>.
    /// Values from the 0x1b04 control list (ported from the Rust original).
    /// </summary>
    private static readonly ushort[] DpiModeShiftCids = [0x00c4, 0x00ed, 0x00fd];

    /// <summary>
    /// Divert the device's DPI/ModeShift button over 0x1b04 so it stops doing its
    /// native function and instead notifies us when pressed; <paramref name="onPressed"/>
    /// fires on each rising edge. Returns a handle that restores the button's
    /// native behaviour when disposed, or <c>null</c> if the device has no
    /// divertable DPI button. Unlike Middle/Back/Forward (seen by the OS mouse
    /// hook), the DPI button is only reachable this way.
    /// </summary>
    public async Task<IAsyncDisposable?> StartDpiButtonCaptureAsync(Action onPressed)
    {
        if (_device.GetFeature<ReprogControlsFeature>() is not { } rc) return null;

        var diverted = new List<ushort>();
        try
        {
            var present = new HashSet<ushort>();
            var count = await rc.GetCountAsync().ConfigureAwait(false);
            for (byte i = 0; i < count; i++)
            {
                var info = await rc.GetCidInfoAsync(i).ConfigureAwait(false);
                if (DpiModeShiftCids.Contains(info.Cid.Value) && info.Flags.IsDivertable())
                    present.Add(info.Cid.Value);
            }
            foreach (var cid in present)
            {
                await rc.SetCidReportingAsync(new ControlId(cid),
                    CidReportingChange.TemporaryDiversion(diverted: true, rawXy: false)).ConfigureAwait(false);
                diverted.Add(cid);
            }
        }
        catch { /* best-effort; whatever diverted is handed back on dispose */ }

        if (diverted.Count == 0) { rc.Dispose(); return null; }
        DiagnosticLog.Info("capture",
            $"dpi button diverted: {string.Join(", ", diverted.Select(c => $"0x{c:x4}"))}");
        return new DpiButtonCapture(rc, diverted, _device.GetFeature<WirelessDeviceStatusFeature>(), onPressed);
    }

    /// <summary>
    /// Control-ID candidates for each button that can drive HID++ gestures, in the
    /// order they are offered as gesture owners. A device rarely exposes more than
    /// one candidate per button, so the first that is present and raw-XY-capable is
    /// used. <see cref="ButtonId.GestureButton"/> is the dedicated MX gesture button
    /// ("App Switch Gesture", 0x00c3); any other divertable raw-XY button — Middle /
    /// Back / Forward or the DPI/wheel-mode button — can be repurposed the same way:
    /// the swipe mechanism is identical, only the diverted control differs. (On
    /// Windows even the OS-hook buttons gesture over HID++, since the WH_MOUSE_LL
    /// hook carries no per-hold move deltas.)
    /// </summary>
    private static readonly (ButtonId Button, ushort[] Cids)[] GestureCandidates =
    [
        (ButtonId.GestureButton, [0x00c3]),
        (ButtonId.MiddleClick, [0x0052]),
        (ButtonId.Back, [0x0053]),
        (ButtonId.Forward, [0x0056]),
        (ButtonId.DpiToggle, [0x00c4, 0x00ed, 0x00fd]),
    ];

    /// <summary>
    /// Which of the gesture-capable buttons this device actually exposes as a
    /// present, raw-XY-capable, divertable control — the set offered as gesture
    /// owners in the UI, in <see cref="GestureCandidates"/> order. Empty when the
    /// device has no 0x1b04 feature or no eligible control.
    /// </summary>
    public async Task<IReadOnlyList<ButtonId>> GestureCapableButtonsAsync()
    {
        if (_device.GetFeature<ReprogControlsFeature>() is not { } rc) return [];
        using (rc)
        {
            var eligible = new List<ButtonId>();
            try
            {
                var count = await rc.GetCountAsync().ConfigureAwait(false);
                var present = new Dictionary<ushort, CidFlags>();
                for (byte i = 0; i < count; i++)
                {
                    var info = await rc.GetCidInfoAsync(i).ConfigureAwait(false);
                    present[info.Cid.Value] = info.Flags;
                }
                foreach (var (button, cids) in GestureCandidates)
                    if (cids.Any(c => present.TryGetValue(c, out var f) && f.SupportsRawXy() && f.IsDivertable()))
                        eligible.Add(button);
            }
            catch { /* device went away mid-scan — return what we have */ }
            return eligible;
        }
    }

    /// <summary>
    /// Divert the control behind <paramref name="owner"/> (see <see cref="GestureCandidates"/>)
    /// over 0x1b04 with raw-XY reporting, so a hold-and-swipe on that button is
    /// captured instead of moving the cursor. <paramref name="onGesture"/> fires once
    /// per committed swipe — the instant the direction commits mid-motion — and once
    /// with <see cref="GestureDirection.Click"/> for a plain press that
    /// never swiped. Returns a handle that restores the control's native behaviour
    /// when disposed, or <c>null</c> if the device exposes no raw-XY-capable control
    /// for that button. Mirrors the DPI-button capture; ported from the Rust gesture
    /// path in <c>openlogi-hid::gesture</c>.
    /// </summary>
    public async Task<IAsyncDisposable?> StartGestureCaptureAsync(
        ButtonId owner, System.Action<GestureDirection> onGesture)
    {
        var candidates = GestureCandidates.FirstOrDefault(gc => gc.Button == owner).Cids;
        if (candidates is null) return null;
        if (_device.GetFeature<ReprogControlsFeature>() is not { } rc) return null;

        ushort? cid = null;
        try
        {
            var count = await rc.GetCountAsync().ConfigureAwait(false);
            for (byte i = 0; i < count && cid is null; i++)
            {
                var info = await rc.GetCidInfoAsync(i).ConfigureAwait(false);
                // Only divert a control that actually reports raw-XY — without it
                // there is no swipe travel to read, only a plain click.
                if (candidates.Contains(info.Cid.Value) && info.Flags.SupportsRawXy())
                    cid = info.Cid.Value;
            }
            if (cid is { } c)
                await rc.SetCidReportingAsync(new ControlId(c),
                    CidReportingChange.TemporaryDiversion(diverted: true, rawXy: true)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Warn("capture", $"gesture: divert for {owner} failed: {ex.Message}");
            cid = null;
        }

        if (cid is not { } gestureCid)
        {
            DiagnosticLog.Info("capture", $"gesture: no raw-XY-capable control for {owner}");
            rc.Dispose();
            return null;
        }
        DiagnosticLog.Info("capture", $"gesture capture on {owner} (cid 0x{gestureCid:x4})");
        return new GestureCapture(rc, gestureCid, _device.GetFeature<WirelessDeviceStatusFeature>(), onGesture);
    }

    /// <summary>
    /// Interval between periodic divert re-assertions. Diversion is volatile device
    /// state — a napping/reconnecting device drops it (the same reset class as the
    /// DPI/scroll-invert loss on wake), and a BT-direct mouse's wake is invisible to
    /// the app, so the diverted buttons would silently act native ("gestures do
    /// nothing") until an unrelated rescan re-armed them. Each capture re-asserts its
    /// divert instantly on the device's 0x1d4b reconnect broadcast, and on this tick
    /// as insurance; both are idempotent, and a write to a napping device just fails
    /// until a later try.
    /// </summary>
    private static readonly TimeSpan DivertKeepalive = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Run <paramref name="reassert"/> whenever the device announces a wireless
    /// reconnect (instant heal) and every <see cref="DivertKeepalive"/> (insurance) —
    /// see <see cref="DivertKeepalive"/> for why. Failures are swallowed (the device
    /// is napping/unreachable and a later round retries), but the fail/recover
    /// *transitions* are logged under <paramref name="what"/>: a gap in divert
    /// coverage is the prime suspect when a diverted button acts native.
    /// </summary>
    private static async Task ReassertDivertLoopAsync(
        ChannelReader<WirelessDeviceStatusFeature.StatusBroadcast>? wake,
        Func<Task> reassert, string what, CancellationToken ct)
    {
        var failing = false;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var wokeByBroadcast = false;
                if (wake is not null)
                {
                    using var round = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    round.CancelAfter(DivertKeepalive);
                    try
                    {
                        await wake.ReadAsync(round.Token).ConfigureAwait(false);
                        wokeByBroadcast = true;
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* periodic tick */ }
                }
                else
                {
                    await Task.Delay(DivertKeepalive, ct).ConfigureAwait(false);
                }
                try
                {
                    await reassert().ConfigureAwait(false);
                    if (failing || wokeByBroadcast)
                        DiagnosticLog.Info("capture",
                            $"{what}: divert reasserted ({(wokeByBroadcast ? "reconnect broadcast" : "keepalive recovered")})");
                    failing = false;
                }
                catch (Exception ex)
                {
                    if (!failing)
                        DiagnosticLog.Info("capture", $"{what}: divert reassert failing: {ex.Message} (will retry)");
                    failing = true;
                }
            }
        }
        catch (OperationCanceledException) { /* disposing */ }
    }

    /// <summary>
    /// Listens for diverted gesture-button events, runs the shared mid-swipe state
    /// machine, and restores the control on dispose. The device streams raw-XY
    /// deltas only while the button is held (raw-XY divert), so ordinary pointer
    /// motion never reaches the accumulator.
    /// </summary>
    private sealed class GestureCapture : IAsyncDisposable
    {
        private readonly ReprogControlsFeature _rc;
        private readonly ushort _cid;
        private readonly WirelessDeviceStatusFeature? _wireless;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _pump;
        private readonly Task _keepalive;

        public GestureCapture(
            ReprogControlsFeature rc, ushort cid, WirelessDeviceStatusFeature? wireless,
            System.Action<GestureDirection> onGesture)
        {
            _rc = rc;
            _cid = cid;
            _wireless = wireless;
            _pump = PumpAsync(rc.Listen(), onGesture, _cts.Token);
            _keepalive = ReassertDivertLoopAsync(wireless?.Listen(),
                () => _rc.SetCidReportingAsync(new ControlId(_cid),
                    CidReportingChange.TemporaryDiversion(diverted: true, rawXy: true)),
                $"gesture (cid 0x{cid:x4})", _cts.Token);
        }

        /// <summary>
        /// A hold's first raw-XY event arriving this soon after the press is the
        /// device's motion-counter backlog, not hold motion: the counter integrates
        /// the whole time between holds and dumps the accumulated total in the first
        /// diverted report (HARDWARE-OBSERVED on an MX Anywhere 3S: ≤1 ms after the
        /// press, magnitude tracking idle time — up to ~9000 counts after minutes).
        /// Feeding it to the accumulator banked pre-press travel that a real swipe
        /// could never overcome — "the first gesture after inactivity only clicks"
        /// — or, when the backlog happened to be axis-clean, fired a phantom swipe
        /// on the spot. Genuine motion reports start only once the hand actually
        /// moves, well past this window.
        /// </summary>
        private static readonly TimeSpan BacklogWindow = TimeSpan.FromMilliseconds(20);

        private async Task PumpAsync(
            ChannelReader<ReprogControlsEvent> reader,
            System.Action<GestureDirection> onGesture,
            CancellationToken ct)
        {
            var swipe = new SwipeAccumulator();
            long pressTicks = 0;
            var backlogChecked = false;
            try
            {
                await foreach (var ev in reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    switch (ev)
                    {
                        case ReprogControlsEvent.DivertedButtons db:
                        {
                            // Begin a hold on the rising edge of the gesture button. On the
                            // falling edge End() settles the hold: a swipe whose travel never
                            // got a post-gate event to commit on (a quick flick), else Click.
                            var held = db.Controls.Any(c => c.Value == _cid);
                            if (held && !swipe.IsHolding)
                            {
                                swipe.Begin();
                                pressTicks = Stopwatch.GetTimestamp();
                                backlogChecked = false;
                            }
                            else if (!held && swipe.IsHolding)
                            {
                                if (swipe.End() is { } e) onGesture(e);
                            }
                            break;
                        }
                        case ReprogControlsEvent.DivertedRawMouseXy xy:
                        {
                            if (!swipe.IsHolding) break;
                            if (!backlogChecked)
                            {
                                backlogChecked = true;
                                // The first raw-XY within the backlog window is the device's
                                // pre-press motion-counter dump, not hold motion — discard it.
                                if (Stopwatch.GetElapsedTime(pressTicks) < BacklogWindow)
                                    break;
                            }
                            // Commit the instant a clean direction emerges (mid-swipe, once
                            // per hold); the accumulator gates on hold duration internally.
                            if (swipe.Accumulate(xy.Dx, xy.Dy) is { } dir)
                                onGesture(dir);
                            break;
                        }
                    }
                }
            }
            catch (OperationCanceledException) { /* shutting down */ }
            catch (Exception ex)
            {
                // A dead pump means gestures silently stop — make it visible.
                DiagnosticLog.Warn("capture", $"gesture pump (cid 0x{_cid:x4}) ended: {ex.Message}");
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { await _pump.ConfigureAwait(false); } catch { /* already faulted/cancelled */ }
            try { await _keepalive.ConfigureAwait(false); } catch { /* cancellation */ }
            try
            {
                await _rc.SetCidReportingAsync(new ControlId(_cid),
                    CidReportingChange.TemporaryDiversion(diverted: false, rawXy: false)).ConfigureAwait(false);
                DiagnosticLog.Info("capture", $"gesture capture released (cid 0x{_cid:x4})");
            }
            catch { DiagnosticLog.Info("capture", $"gesture capture released (cid 0x{_cid:x4}, device gone)"); }
            _wireless?.Dispose();
            _rc.Dispose();
            _cts.Dispose();
        }
    }

    /// <summary>Listens for diverted DPI/ModeShift presses and restores the controls on dispose.</summary>
    private sealed class DpiButtonCapture : IAsyncDisposable
    {
        private readonly ReprogControlsFeature _rc;
        private readonly ushort[] _cids;
        private readonly WirelessDeviceStatusFeature? _wireless;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _pump;
        private readonly Task _keepalive;
        private bool _down;

        public DpiButtonCapture(
            ReprogControlsFeature rc, IEnumerable<ushort> cids, WirelessDeviceStatusFeature? wireless, Action onPressed)
        {
            _rc = rc;
            _cids = [.. cids];
            _wireless = wireless;
            _pump = PumpAsync(rc.Listen(), onPressed, _cts.Token);
            _keepalive = ReassertDivertLoopAsync(wireless?.Listen(), async () =>
            {
                foreach (var cid in _cids)
                    await _rc.SetCidReportingAsync(new ControlId(cid),
                        CidReportingChange.TemporaryDiversion(diverted: true, rawXy: false)).ConfigureAwait(false);
            }, "dpi button", _cts.Token);
        }

        private async Task PumpAsync(ChannelReader<ReprogControlsEvent> reader, Action onPressed, CancellationToken ct)
        {
            try
            {
                await foreach (var ev in reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    if (ev is not ReprogControlsEvent.DivertedButtons db) continue;
                    // The event carries the set of currently-held diverted controls;
                    // fire once on the press (rising) edge, not while held or on release.
                    var held = db.Controls.Any(c => _cids.Contains(c.Value));
                    if (held && !_down) onPressed();
                    _down = held;
                }
            }
            catch (OperationCanceledException) { /* shutting down */ }
            catch (Exception ex)
            {
                // A dead pump means the diverted button silently stops — make it visible.
                DiagnosticLog.Warn("capture", $"dpi button pump ended: {ex.Message}");
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { await _pump.ConfigureAwait(false); } catch { /* already faulted/cancelled */ }
            try { await _keepalive.ConfigureAwait(false); } catch { /* cancellation */ }
            foreach (var cid in _cids)
            {
                try
                {
                    await _rc.SetCidReportingAsync(new ControlId(cid),
                        CidReportingChange.TemporaryDiversion(diverted: false, rawXy: false)).ConfigureAwait(false);
                }
                catch { /* device gone — nothing to restore */ }
            }
            _wireless?.Dispose();
            _rc.Dispose();
            _cts.Dispose();
        }
    }
}
