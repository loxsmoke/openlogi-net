using OpenLogi.HidPP.Channel;
using DiagnosticLog = OpenLogi.Core.Logging.DiagnosticLog;

namespace OpenLogi.Hid;

/// <summary>
/// The one place long-lived HID handles are opened. Each HID node (device path) gets
/// at most one <see cref="HidppChannel"/>, shared by everything that talks to it —
/// device sessions, the receiver watcher, the lighting keepalive — through
/// reference-counted <see cref="ChannelLease"/>s; the channel closes when its last
/// lease is released.
///
/// The point is sleep/resume. A handle opened before sleep or hibernation is dead
/// afterwards, and every feature that used to open its own handle needed its own
/// "forget the stale one" fix; a forgotten one left a read loop failing once a second
/// forever. Here a stale channel can't outlive its usefulness whoever holds it:
/// <see cref="InvalidateAllAsync"/> (the resume signal) and a channel's own
/// sustained-read-failure signal both close the handle and tell every lease holder
/// through <see cref="ChannelLease.Faulted"/>; the next acquire opens a fresh one.
/// </summary>
public sealed class HidChannelPool
{
    /// <summary>The process-wide pool over the local machine's HID nodes.</summary>
    public static HidChannelPool Shared { get; } = new(LocalHidNodeSource.Instance);

    private readonly IHidNodeSource _source;
    private readonly TimeSpan? _deadReadThreshold;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _openGate = new(1, 1);
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="source">Where nodes come from; tests pass scripted nodes.</param>
    /// <param name="deadReadThreshold">Override of <see cref="HidppChannel.DefaultDeadReadThreshold"/> (tests).</param>
    public HidChannelPool(IHidNodeSource source, TimeSpan? deadReadThreshold = null)
    {
        _source = source;
        _deadReadThreshold = deadReadThreshold;
    }

    /// <summary>The HID++ nodes currently present.</summary>
    public IReadOnlyList<IHidNode> Enumerate() => _source.Enumerate();

    /// <summary>
    /// Lease the shared channel for <paramref name="node"/>, opening it if nobody holds
    /// it yet. Throws if the node cannot be opened or carries no HID++.
    /// </summary>
    public async Task<ChannelLease> AcquireAsync(IHidNode node)
    {
        lock (_gate)
            if (_entries.TryGetValue(node.DevicePath, out var live)) return live.AddLease();

        // One open at a time, so two first users of a node can't both open it.
        await _openGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_gate)
                if (_entries.TryGetValue(node.DevicePath, out var raced)) return raced.AddLease();

            var channel = await HidppChannel.FromRawChannelAsync(node.Open(), _deadReadThreshold).ConfigureAwait(false);
            var entry = new Entry(this, node.DevicePath, channel);
            // Raised from inside the channel's read loop: retire on another thread,
            // since closing the channel waits for that loop to end.
            channel.ReadsDead += () => _ = Task.Run(() => RetireAsync(entry, "reads dead"));
            lock (_gate)
            {
                _entries[node.DevicePath] = entry;
                return entry.AddLease();
            }
        }
        finally { _openGate.Release(); }
    }

    /// <summary>
    /// Close every pooled channel and fault every lease. Call when all open handles
    /// are presumed dead (resume from sleep/hibernation): lease holders reopen through
    /// <see cref="AcquireAsync"/> and get fresh handles.
    /// </summary>
    public async Task InvalidateAllAsync()
    {
        Entry[] entries;
        lock (_gate) entries = [.. _entries.Values];
        if (entries.Length > 0)
            DiagnosticLog.Info("pool", $"invalidating {entries.Length} pooled HID channel(s)");
        await Task.WhenAll(entries.Select(e => RetireAsync(e, "invalidated"))).ConfigureAwait(false);
    }

    /// <summary>The open channels and how many leases each has (diagnostics, tests).</summary>
    public IReadOnlyList<(string DevicePath, int Leases)> Snapshot()
    {
        lock (_gate) return [.. _entries.Values.Select(e => (e.DevicePath, e.Leases.Count))];
    }

    /// <summary>Take a channel out of service: no new leases, handle closed, holders told.</summary>
    private async Task RetireAsync(Entry entry, string reason)
    {
        ChannelLease[] holders;
        lock (_gate)
        {
            if (entry.Retired) return;
            entry.Retired = true;
            if (_entries.TryGetValue(entry.DevicePath, out var current) && ReferenceEquals(current, entry))
                _entries.Remove(entry.DevicePath);
            holders = [.. entry.Leases];
        }
        DiagnosticLog.Info("pool",
            $"{entry.Channel.VendorId:x4}:{entry.Channel.ProductId:x4}: channel retired ({reason}), {holders.Length} holder(s) notified");
        // Close first, so a holder that reopens from its handler never races the dead handle.
        await CloseAsync(entry.Channel).ConfigureAwait(false);
        foreach (var lease in holders) lease.RaiseFaulted();
    }

    private async ValueTask ReleaseAsync(Entry entry, ChannelLease lease)
    {
        lock (_gate)
        {
            entry.Leases.Remove(lease);
            // Still in use, or already closed by RetireAsync.
            if (entry.Leases.Count > 0 || entry.Retired) return;
            entry.Retired = true;
            if (_entries.TryGetValue(entry.DevicePath, out var current) && ReferenceEquals(current, entry))
                _entries.Remove(entry.DevicePath);
        }
        // The last lease went: nobody is left to use the handle.
        await CloseAsync(entry.Channel).ConfigureAwait(false);
    }

    private static async Task CloseAsync(HidppChannel channel)
    {
        try { await channel.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { DiagnosticLog.Warn("pool", $"channel close failed: {ex.GetType().Name}: {ex.Message}"); }
    }

    private sealed class Entry(HidChannelPool pool, string devicePath, HidppChannel channel)
    {
        public string DevicePath => devicePath;
        public HidppChannel Channel => channel;
        public List<ChannelLease> Leases { get; } = [];
        public bool Retired { get; set; }

        /// <summary>Caller holds the pool gate.</summary>
        public ChannelLease AddLease()
        {
            var lease = new ChannelLease(channel, l => pool.ReleaseAsync(this, l));
            Leases.Add(lease);
            return lease;
        }
    }
}

/// <summary>
/// One user's hold on a pooled <see cref="HidppChannel"/>. Dispose to release it; the
/// channel closes with its last lease. Never dispose <see cref="Channel"/> directly.
/// </summary>
public sealed class ChannelLease : IAsyncDisposable
{
    private readonly Func<ChannelLease, ValueTask> _release;
    private int _released;
    private int _faulted;

    internal ChannelLease(HidppChannel channel, Func<ChannelLease, ValueTask> release)
    {
        Channel = channel;
        _release = release;
    }

    public HidppChannel Channel { get; }

    /// <summary>Whether the channel was closed under this lease (see <see cref="Faulted"/>).</summary>
    public bool IsFaulted => Volatile.Read(ref _faulted) != 0;

    /// <summary>
    /// Raised once (off the UI thread) when the pool closed the channel under this
    /// lease: its handle went stale, or the pool was invalidated. The channel is
    /// unusable from then on — release this lease and acquire a new one.
    /// </summary>
    public event Action? Faulted;

    internal void RaiseFaulted()
    {
        if (Interlocked.Exchange(ref _faulted, 1) != 0) return;
        if (Volatile.Read(ref _released) != 0) return;
        try { Faulted?.Invoke(); }
        catch (Exception ex) { DiagnosticLog.Warn("pool", $"lease fault handler threw: {ex.Message}"); }
    }

    public ValueTask DisposeAsync() =>
        Interlocked.Exchange(ref _released, 1) != 0 ? ValueTask.CompletedTask : _release(this);
}
