using System.Threading.Channels;
using OpenLogi.Core.Logging;
using OpenLogi.HidPP.Channel;
using OpenLogi.HidPP.Receiver;

namespace OpenLogi.Hid;

/// <summary>
/// Keeps a live HID++ channel open on every Logitech receiver and raises
/// <see cref="DeviceWoke"/> when a paired wireless device (re)connects — i.e. wakes
/// from sleep. A receiver-connected device sleeping/waking does NOT change the OS HID
/// node set (the receiver's node stays put), so <see cref="HidDeviceWatcher"/> can't
/// see it; this listens for the receiver's device-connection notification (HID++ v1.0
/// SubId 0x41, already parsed by <see cref="BoltReceiver"/> / <see cref="UnifyingReceiver"/>)
/// instead. It fires when a device comes online after being offline OR after a long quiet
/// (some receivers announce a connect but not a sleep, so a fresh Online well after the
/// last one means the device slept and woke in between). Re-announcements our own rescans
/// trigger arrive within seconds and only refresh the last-seen time, so they can't feed
/// back into a rescan loop.
///
/// HARDWARE-VERIFIED: a G915 on a LIGHTSPEED dongle emits a spontaneous 0x41 Online on
/// wake (log tag "rxwatch").
/// </summary>
public sealed class ReceiverConnectionWatcher : IAsyncDisposable
{
    /// <summary>Raised (off the UI thread) shortly after a paired device transitions to online.</summary>
    public event Action? DeviceWoke;

    private readonly object _gate = new();
    private readonly List<Binding> _bindings = [];
    private readonly TimeSpan _quietPeriod;
    private Timer? _debounce;
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

        foreach (var hid in HidDiscovery.EnumerateHidppDevices())
        {
            if (!Receivers.IsReceiverPid((ushort)hid.VendorID, (ushort)hid.ProductID)) continue;

            HidppChannel channel;
            try { channel = await HidppChannel.FromRawChannelAsync(WindowsRawHidChannel.Open(hid)).ConfigureAwait(false); }
            catch { continue; }

            // Receiver registers ride the short (7-byte) report; the long-only interface
            // answers pings but times out on every register op — not the control node.
            if (!channel.SupportsShort || Receivers.Detect(channel) is not { } detected)
            {
                await channel.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            Binding binding;
            try { binding = await Binding.StartAsync(channel, detected, OnDeviceOnline).ConfigureAwait(false); }
            catch { await channel.DisposeAsync().ConfigureAwait(false); continue; }

            bool keep;
            lock (_gate) { keep = !_disposed; if (keep) _bindings.Add(binding); }
            if (!keep) await binding.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void OnDeviceOnline()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _debounce ??= new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
            _debounce.Change(_quietPeriod, Timeout.InfiniteTimeSpan);
        }
    }

    private void Fire()
    {
        lock (_gate) { if (_disposed) return; }
        DiagnosticLog.Info("rxwatch", "receiver reported a device online — signalling wake");
        try { DeviceWoke?.Invoke(); }
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
        // wake even without an intervening "offline": some receivers announce a device
        // connecting but stay silent when it sleeps, so we'd otherwise miss the next wake.
        // Comfortably longer than the burst of re-announcements our own rescans trigger
        // (a few seconds) and shorter than a real idle-sleep (minutes), so neither a
        // feedback loop nor a genuine wake is misclassified.
        private static readonly TimeSpan WakeGap = TimeSpan.FromSeconds(30);

        private readonly HidppChannel _channel;
        private readonly IDisposable _receiver; // Bolt/UnifyingReceiver — owns the 0x41 channel listener
        private readonly CancellationTokenSource _cts = new();
        private readonly Dictionary<byte, (bool Online, DateTime LastOnline)> _state = []; // per device index
        private readonly object _stateLock = new();
        private Task _pump = Task.CompletedTask;

        private Binding(HidppChannel channel, IDisposable receiver)
        {
            _channel = channel;
            _receiver = receiver;
        }

        public static async Task<Binding> StartAsync(HidppChannel channel, DetectedReceiver detected, Action onWake)
        {
            Binding b = detected switch
            {
                DetectedReceiver.Bolt r => Make(channel, r.Receiver, r.Receiver.Listen(), c => c.Index, c => c.Online, onWake),
                DetectedReceiver.Unifying r => Make(channel, r.Receiver, r.Receiver.Listen(), c => c.Index, c => c.Online, onWake),
                DetectedReceiver.Lightspeed r => Make(channel, r.Receiver, r.Receiver.Listen(), c => c.Index, c => c.Online, onWake),
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
            HidppChannel channel, IDisposable receiver, ChannelReader<T> reader,
            Func<T, byte> index, Func<T, bool> online, Action onWake)
        {
            var b = new Binding(channel, receiver);
            b._pump = b.PumpAsync(reader, index, online, onWake, b._cts.Token);
            return b;
        }

        private async Task PumpAsync<T>(
            ChannelReader<T> reader, Func<T, byte> index, Func<T, bool> online, Action onWake, CancellationToken ct)
        {
            try
            {
                while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
                    while (reader.TryRead(out var ev))
                    {
                        // Fire only when a device that wasn't known-online becomes online.
                        // Our own arrival triggers re-announce already-online devices, which
                        // stay online → no transition → no feedback rescan loop.
                        var idx = index(ev);
                        var isOnline = online(ev);
                        bool wake = false;
                        lock (_stateLock)
                        {
                            var now = DateTime.UtcNow;
                            _state.TryGetValue(idx, out var prev);
                            if (isOnline)
                            {
                                // Wake = came online after being offline, or after a long
                                // quiet (a silent sleep). Re-announcements our rescans cause
                                // arrive within seconds, so they refresh LastOnline and don't
                                // re-fire.
                                wake = !prev.Online || now - prev.LastOnline > WakeGap;
                                _state[idx] = (true, now);
                            }
                            else
                            {
                                _state[idx] = (false, prev.LastOnline);
                            }
                        }
                        DiagnosticLog.Info("rxwatch",
                            $"0x41 slot {idx}: {(isOnline ? "online" : "offline")}{(wake ? " (wake→signal)" : "")}");
                        if (wake) onWake();
                    }
            }
            catch (OperationCanceledException) { /* disposing */ }
            catch (Exception ex) { DiagnosticLog.Warn("rxwatch", $"connection pump ended: {ex.Message}"); }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            try { await _pump.ConfigureAwait(false); } catch { /* cancellation */ }
            _cts.Dispose();
            _receiver.Dispose();               // removes the 0x41 listener from the channel
            await _channel.DisposeAsync().ConfigureAwait(false);
        }
    }
}
