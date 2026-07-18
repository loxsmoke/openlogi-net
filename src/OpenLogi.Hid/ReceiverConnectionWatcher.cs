using System.Threading.Channels;
using OpenLogi.Core.Logging;
using OpenLogi.HidPP.Channel;
using OpenLogi.HidPP.Protocol;
using OpenLogi.HidPP.Receiver;

namespace OpenLogi.Hid;

/// <summary>
/// Keeps a live HID++ channel open on every Logitech receiver and raises
/// <see cref="DeviceWoke"/> when a paired wireless device (re)connects — i.e. wakes
/// from sleep. A receiver-connected device sleeping/waking does NOT change the OS HID
/// node set (the receiver's node stays put), so <see cref="HidDeviceWatcher"/> can't
/// see it; this listens for the receiver's device-connection notification (HID++ v1.0
/// SubId 0x41, already parsed by <see cref="BoltReceiver"/> / <see cref="UnifyingReceiver"/>)
/// instead. It fires when a device comes online after a long quiet — a fresh Online well
/// after the last one means the device slept (or parked its link) and the user is back.
/// Offline announcements are deliberately ignored: the 0x41 link-established flag reports
/// instantaneous radio state, and a device that parks its RF link to save power (a G915
/// does so within ~1-2 s of the last keystroke, HARDWARE-OBSERVED) announces "offline"
/// while fully awake — most visibly in the re-announcements our own rescans trigger.
/// Keying wakes off offline→online transitions turned every keypress into a
/// rescan→re-announce→rescan loop; the quiet-gap rule alone can't feed back, because
/// rescan-triggered re-announcements arrive within seconds and only refresh the
/// last-seen time.
///
/// HARDWARE-VERIFIED: a G915 on a LIGHTSPEED dongle emits a spontaneous 0x41 Online on
/// wake (log tag "rxwatch").
/// </summary>
public sealed class ReceiverConnectionWatcher : IAsyncDisposable
{
    /// <summary>
    /// One coalesced wake burst. <see cref="KeyboardOnly"/> is true when every device
    /// that woke is a keyboard — the handler can then skip work that only matters for
    /// pointer devices (e.g. rebuilding mouse captures, which kills the diverts of a
    /// mouse that happens to be napping). <see cref="VendorId"/>/<see cref="ProductId"/>
    /// identify the one receiver that reported the burst, letting the handler rescan
    /// just that receiver's interfaces; null when several receivers woke at once.
    /// </summary>
    public readonly record struct ReceiverWake(bool KeyboardOnly, ushort? VendorId, ushort? ProductId);

    /// <summary>Raised (off the UI thread) shortly after a paired device transitions to online.</summary>
    public event Action<ReceiverWake>? DeviceWoke;

    private readonly object _gate = new();
    private readonly List<Binding> _bindings = [];
    private readonly TimeSpan _quietPeriod;
    private Timer? _debounce;
    private bool _wakeAllKeyboards = true; // accumulated over the debounce window
    private ushort? _wakeVid;              // the burst's single receiver, or…
    private ushort? _wakePid;
    private bool _wakeMixed;               // …several receivers woke together
    private bool _disposed;

    /// <param name="quietPeriod">How long to coalesce a burst of connection events before firing (default 400 ms).</param>
    public ReceiverConnectionWatcher(TimeSpan? quietPeriod = null)
        => _quietPeriod = quietPeriod ?? TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// (Re)bind to the receivers currently present: drop every existing channel and open
    /// a fresh one per receiver node. Cheap — receivers rarely come and go — and safe to
    /// call alongside a device rescan. Failures per receiver are non-fatal (no wake
    /// detection for that one, nothing else breaks).
    /// </summary>
    public async Task RefreshAsync()
    {
        Binding[] old;
        lock (_gate)
        {
            if (_disposed) return;
            old = [.. _bindings];
            _bindings.Clear();
        }
        foreach (var b in old) await b.DisposeAsync().ConfigureAwait(false);

        var receiverNodes = HidDiscovery.EnumerateHidppDevices()
            .Where(h => Receivers.IsReceiverPid((ushort)h.VendorID, (ushort)h.ProductID))
            .ToList();

        foreach (var hid in receiverNodes)
        {
            // Receiver registers ride the short (7-byte) report; the long-only interface
            // answers pings but times out on every register op — not the control node.
            if (WindowsRawHidChannel.DetectSupport(hid) is not { SupportsShort: true }) continue;

            HidppChannel channel;
            try { channel = await HidppChannel.FromRawChannelAsync(WindowsRawHidChannel.Open(hid)).ConfigureAwait(false); }
            catch { continue; }

            if (!channel.SupportsShort || Receivers.Detect(channel) is not { } detected)
            {
                await channel.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            // On split receivers (LIGHTSPEED / newer Bolt) the paired devices' HID++ 2.0
            // rides a separate long-only sibling interface — the wake-hold pings must go
            // there, since the control interface can't reach the device slots. Combined
            // receivers have no sibling and the control channel serves both roles.
            HidppChannel? deviceChannel = null;
            foreach (var sibling in receiverNodes)
            {
                if (sibling.VendorID != hid.VendorID || sibling.ProductID != hid.ProductID) continue;
                if (WindowsRawHidChannel.DetectSupport(sibling) is not { SupportsShort: false, SupportsLong: true }) continue;
                try { deviceChannel = await HidppChannel.FromRawChannelAsync(WindowsRawHidChannel.Open(sibling)).ConfigureAwait(false); }
                catch { /* wake-hold falls back to the control channel */ }
                break;
            }

            Binding binding;
            var (vid, pid) = (channel.VendorId, channel.ProductId);
            try { binding = await Binding.StartAsync(channel, deviceChannel, detected, isKb => OnDeviceOnline(isKb, vid, pid)).ConfigureAwait(false); }
            catch
            {
                await channel.DisposeAsync().ConfigureAwait(false);
                if (deviceChannel is not null) await deviceChannel.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            bool keep;
            lock (_gate) { keep = !_disposed; if (keep) _bindings.Add(binding); }
            if (!keep) await binding.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void OnDeviceOnline(bool isKeyboard, ushort vid, ushort pid)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (!isKeyboard) _wakeAllKeyboards = false;
            if (_wakeVid is null) { _wakeVid = vid; _wakePid = pid; }
            else if (_wakeVid != vid || _wakePid != pid) _wakeMixed = true;
            _debounce ??= new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
            _debounce.Change(_quietPeriod, Timeout.InfiniteTimeSpan);
        }
    }

    private void Fire()
    {
        ReceiverWake wake;
        lock (_gate)
        {
            if (_disposed) return;
            wake = new ReceiverWake(_wakeAllKeyboards, _wakeMixed ? null : _wakeVid, _wakeMixed ? null : _wakePid);
            _wakeAllKeyboards = true;
            _wakeVid = null;
            _wakePid = null;
            _wakeMixed = false;
        }
        DiagnosticLog.Info("rxwatch",
            $"receiver reported a device online — signalling wake{(wake.KeyboardOnly ? " (keyboard only)" : "")}"
            + (wake.VendorId is { } v ? $" [{v:x4}:{wake.ProductId:x4}]" : ""));
        try { DeviceWoke?.Invoke(wake); }
        catch (Exception ex) { DiagnosticLog.Warn("rxwatch", $"wake handler threw: {ex.Message}"); }
    }

    public async ValueTask DisposeAsync()
    {
        Binding[] bindings;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _debounce?.Dispose();
            _debounce = null;
            bindings = [.. _bindings];
            _bindings.Clear();
        }
        foreach (var b in bindings) await b.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>One receiver: its open channel, the parsed receiver, and the pump reading connection events.</summary>
    private sealed class Binding : IAsyncDisposable
    {
        // A fresh "online" this long after the device was last seen online counts as a
        // wake; anything sooner is a re-announcement (our own rescans trigger those) or a
        // power-saving link park/unpark cycle, neither of which is the user returning.
        // Comfortably longer than the burst of re-announcements a rescan triggers (a few
        // seconds) and shorter than a real idle-sleep (minutes), so neither a feedback
        // loop nor a genuine wake is misclassified. Offline announcements never factor
        // in — see the class comment.
        private static readonly TimeSpan WakeGap = TimeSpan.FromSeconds(30);

        // A just-woken G915 parks its RF link within ~300 ms of the wake keypress
        // (HARDWARE-OBSERVED: 0x41 offline 337 ms after the online), and the receiver
        // NACKs pings to a parked slot — so the rescan, arriving seconds later, finds
        // nothing. Pinging the slot the instant it announces online keeps traffic on
        // the fresh link so it stays up until the rescan can probe it properly.
        private static readonly TimeSpan HoldDuration = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan HoldInterval = TimeSpan.FromMilliseconds(250);

        private readonly HidppChannel _channel;
        private readonly HidppChannel? _deviceChannel; // long-only sibling: device slots' HID++ 2.0 on split receivers
        private readonly IDisposable _receiver; // Bolt/UnifyingReceiver — owns the 0x41 channel listener
        private readonly CancellationTokenSource _cts = new();
        private readonly Dictionary<byte, DateTime> _lastOnline = []; // per device index
        private readonly HashSet<byte> _holding = []; // slots with a wake-hold ping loop running
        private readonly object _stateLock = new();
        private Task _pump = Task.CompletedTask;

        private Binding(HidppChannel channel, HidppChannel? deviceChannel, IDisposable receiver)
        {
            _channel = channel;
            _deviceChannel = deviceChannel;
            _receiver = receiver;
        }

        public static async Task<Binding> StartAsync(HidppChannel channel, HidppChannel? deviceChannel, DetectedReceiver detected, Action<bool> onWake)
        {
            Binding b = detected switch
            {
                DetectedReceiver.Bolt r => Make(channel, deviceChannel, r.Receiver, r.Receiver.Listen(),
                    c => c.Index, c => c.Online, c => c.Kind == BoltDeviceKind.Keyboard, onWake),
                DetectedReceiver.Unifying r => Make(channel, deviceChannel, r.Receiver, r.Receiver.Listen(),
                    c => c.Index, c => c.Online, c => c.Kind == UnifyingDeviceKind.Keyboard, onWake),
                DetectedReceiver.Lightspeed r => Make(channel, deviceChannel, r.Receiver, r.Receiver.Listen(),
                    c => c.Index, c => c.Online, c => c.Kind == UnifyingDeviceKind.Keyboard, onWake),
                _ => throw new InvalidOperationException("channel carries no recognised receiver"),
            };
            // Make the receiver push arrival/wake notifications unsolicited (idempotent).
            try
            {
                switch (detected)
                {
                    case DetectedReceiver.Bolt r: await r.Receiver.SetWirelessNotificationsAsync(true).ConfigureAwait(false); break;
                    case DetectedReceiver.Unifying r: await r.Receiver.SetWirelessNotificationsAsync(true).ConfigureAwait(false); break;
                    case DetectedReceiver.Lightspeed r: await r.Receiver.SetWirelessNotificationsAsync(true).ConfigureAwait(false); break;
                }
            }
            catch (Exception ex) { DiagnosticLog.Warn("rxwatch", $"enable notifications failed: {ex.Message}"); }
            return b;
        }

        private static Binding Make<T>(
            HidppChannel channel, HidppChannel? deviceChannel, IDisposable receiver, ChannelReader<T> reader,
            Func<T, byte> index, Func<T, bool> online, Func<T, bool> isKeyboard, Action<bool> onWake)
        {
            var b = new Binding(channel, deviceChannel, receiver);
            b._pump = b.PumpAsync(reader, index, online, isKeyboard, onWake, b._cts.Token);
            return b;
        }

        private async Task PumpAsync<T>(
            ChannelReader<T> reader, Func<T, byte> index, Func<T, bool> online, Func<T, bool> isKeyboard,
            Action<bool> onWake, CancellationToken ct)
        {
            try
            {
                while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
                    while (reader.TryRead(out var ev))
                    {
                        // Wake = online after a long quiet. Offline announcements are
                        // logged but never change the wake state: they can just mean the
                        // link parked for power saving (or our own rescan re-announcing a
                        // parked link), and letting them re-arm the online edge made every
                        // keypress fire twice and loop rescans (see the class comment).
                        var idx = index(ev);
                        var isOnline = online(ev);
                        bool wake = false;
                        if (isOnline)
                        {
                            lock (_stateLock)
                            {
                                var now = DateTime.UtcNow;
                                wake = !_lastOnline.TryGetValue(idx, out var last) || now - last > WakeGap;
                                _lastOnline[idx] = now;
                            }
                        }
                        DiagnosticLog.Info("rxwatch",
                            $"0x41 slot {idx}: {(isOnline ? "online" : "offline")}{(wake ? " (wake→signal)" : "")}");
                        if (isOnline) StartWakeHold(idx);
                        if (wake) onWake(isKeyboard(ev));
                    }
            }
            catch (OperationCanceledException) { /* disposing */ }
            catch (Exception ex) { DiagnosticLog.Warn("rxwatch", $"connection pump ended: {ex.Message}"); }
        }

        /// <summary>Start (at most one per slot) the wake-hold ping loop for a slot that just announced online.</summary>
        private void StartWakeHold(byte idx)
        {
            lock (_stateLock)
            {
                if (_cts.IsCancellationRequested || !_holding.Add(idx)) return;
            }
            _ = HoldAsync(idx);
        }

        /// <summary>
        /// Ping <paramref name="idx"/> every <see cref="HoldInterval"/> for
        /// <see cref="HoldDuration"/> on the device interface. The traffic keeps the
        /// just-established link from parking until the rescan probes the device; the
        /// log line also settles whether the device is reachable right after its wake.
        /// </summary>
        private async Task HoldAsync(byte idx)
        {
            var ct = _cts.Token; // snapshot: outlives a disposed _cts safely
            var pingChannel = _deviceChannel ?? _channel;
            var deadline = DateTime.UtcNow + HoldDuration;
            var answered = 0;
            var pings = 0;
            var reportedFirst = false;
            try
            {
                while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
                {
                    bool ok;
                    try { ok = await V20.DetermineVersionAsync(pingChannel, idx, pingAttempts: 1).ConfigureAwait(false) is not null; }
                    catch { ok = false; }
                    pings++;
                    if (ok) answered++;
                    if (!reportedFirst)
                    {
                        reportedFirst = true;
                        DiagnosticLog.Info("rxwatch", $"slot {idx}: wake-hold first ping {(ok ? "answered" : "unanswered")}");
                    }
                    await Task.Delay(HoldInterval, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { /* disposing */ }
            catch (Exception ex) { DiagnosticLog.Warn("rxwatch", $"slot {idx}: wake-hold failed: {ex.Message}"); }
            finally
            {
                DiagnosticLog.Info("rxwatch", $"slot {idx}: wake-hold ended ({answered}/{pings} pings answered)");
                lock (_stateLock) _holding.Remove(idx);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            try { await _pump.ConfigureAwait(false); } catch { /* cancellation */ }
            _cts.Dispose();
            _receiver.Dispose();               // removes the 0x41 listener from the channel
            await _channel.DisposeAsync().ConfigureAwait(false);
            if (_deviceChannel is not null) await _deviceChannel.DisposeAsync().ConfigureAwait(false);
        }
    }
}
