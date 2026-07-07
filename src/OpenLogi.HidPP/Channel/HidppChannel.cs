namespace OpenLogi.HidPP.Channel;

/// <summary>The kind of a <see cref="ChannelException"/>. Mirrors Rust <c>channel::ChannelError</c>.</summary>
public enum ChannelErrorKind
{
    /// <summary>The raw channel implementation returned an error.</summary>
    Implementation,
    /// <summary>The HID channel does not support HID++.</summary>
    HidppNotSupported,
    /// <summary>The channel does not support the given message type (short/long).</summary>
    MessageTypeNotSupported,
    /// <summary>No response was received following a request.</summary>
    NoResponse,
    /// <summary>The request timed out before the device responded.</summary>
    Timeout,
}

/// <summary>An error creating or interacting with a HID(++) channel.</summary>
public sealed class ChannelException(ChannelErrorKind kind, string? message = null, Exception? inner = null)
    : Exception(message ?? kind.ToString(), inner)
{
    public ChannelErrorKind Kind { get; } = kind;
}

/// <summary>
/// A HID communication channel supporting HID++. Maps incoming reports to
/// previously sent requests by predicate. Ported from Rust
/// <c>channel::HidppChannel</c>; the Rust read thread becomes an async read loop.
/// </summary>
public sealed class HidppChannel : IAsyncDisposable
{
    /// <summary>Default time budget for <see cref="SendAsync"/> (write + response wait).</summary>
    public static readonly TimeSpan SendResponseTimeout = TimeSpan.FromSeconds(5);

    public bool SupportsShort { get; }
    public bool SupportsLong { get; }
    public ushort VendorId { get; }
    public ushort ProductId { get; }

    private readonly IRawHidChannel _raw;
    private readonly object _swLock = new();
    private bool _rotateSwId;
    private byte _swId = 0x01;

    private readonly object _pendingLock = new();
    private readonly List<PendingMessage> _pending = [];
    private long _pendingId = 1;

    private readonly object _listenerLock = new();
    private readonly Dictionary<uint, Action<HidppMessage, bool>> _listeners = [];
    private readonly Random _rng = new();

    private readonly CancellationTokenSource _cts = new();
    private readonly Task _readLoop;

    private HidppChannel(IRawHidChannel raw, bool supportsShort, bool supportsLong)
    {
        _raw = raw;
        SupportsShort = supportsShort;
        SupportsLong = supportsLong;
        VendorId = raw.VendorId;
        ProductId = raw.ProductId;
        _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token));
    }

    /// <summary>Construct a HID++ channel from a raw channel, or throw if HID++ is unsupported.</summary>
    public static async Task<HidppChannel> FromRawChannelAsync(IRawHidChannel raw)
    {
        var support = raw.SupportsShortLongHidpp()
            ?? throw new NotSupportedException(
                "report-descriptor parsing is not implemented yet; the raw channel must report short/long support directly (section 4)");
        if (!support.SupportsShort && !support.SupportsLong)
            throw new ChannelException(ChannelErrorKind.HidppNotSupported);
        return new HidppChannel(raw, support.SupportsShort, support.SupportsLong);
    }

    // ── Software id ──────────────────────────────────────────────────────────

    /// <summary>Set the software id returned by the next <see cref="GetSwId"/>. Avoid 0 (device notifications).</summary>
    public void SetSwId(U4 swId)
    {
        lock (_swLock) _swId = swId.ToLo();
    }

    /// <summary>Set whether <see cref="GetSwId"/> increments after each call (eases response→request mapping).</summary>
    public void SetRotatingSwId(bool enable)
    {
        lock (_swLock) _rotateSwId = enable;
    }

    /// <summary>Provide a software id for the next message; may rotate (skipping 0, reserved for notifications).</summary>
    public U4 GetSwId()
    {
        lock (_swLock)
        {
            if (!_rotateSwId)
                return U4.FromLo(_swId);
            var current = _swId;
            _swId = (byte)((current & 0x0f) == 0x0f ? 0x01 : current + 1);
            return U4.FromLo(current);
        }
    }

    // ── Sending ──────────────────────────────────────────────────────────────

    /// <summary>Send a message and wait for a matching response, bounded by <see cref="SendResponseTimeout"/>.</summary>
    public Task<HidppMessage> SendAsync(HidppMessage msg, Func<HidppMessage, bool> responsePredicate) =>
        SendWithTimeoutAsync(msg, responsePredicate, SendResponseTimeout);

    /// <summary>Send a message and wait for a matching response, bounded by <paramref name="timeout"/>.</summary>
    public async Task<HidppMessage> SendWithTimeoutAsync(
        HidppMessage msg, Func<HidppMessage, bool> responsePredicate, TimeSpan timeout)
    {
        msg = NormalizeOutgoing(msg);
        if (!SupportsMsg(msg))
            throw new ChannelException(ChannelErrorKind.MessageTypeNotSupported);

        var tcs = new TaskCompletionSource<HidppMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = Interlocked.Increment(ref _pendingId);

        lock (_pendingLock)
        {
            // Prune give-ups (an outer cancellation that abandoned the wait) so a
            // reused channel can't accumulate pending entries unboundedly.
            _pending.RemoveAll(p => p.Tcs.Task.IsCompleted);
            _pending.Add(new PendingMessage(id, responsePredicate, tcs));
        }

        try
        {
            await SendAndForgetAsync(msg).ConfigureAwait(false);
        }
        catch
        {
            RemovePending(id);
            throw;
        }

        using var delayCts = new CancellationTokenSource();
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeout, delayCts.Token)).ConfigureAwait(false);
        if (completed == tcs.Task)
        {
            delayCts.Cancel();
            return await tcs.Task.ConfigureAwait(false);
        }

        RemovePending(id);
        throw new ChannelException(ChannelErrorKind.Timeout);
    }

    /// <summary>Send a message without waiting for a response.</summary>
    public async Task SendAndForgetAsync(HidppMessage msg)
    {
        msg = NormalizeOutgoing(msg);
        if (!SupportsMsg(msg))
            throw new ChannelException(ChannelErrorKind.MessageTypeNotSupported);

        var buf = new byte[HidppMessage.LongReportLength];
        var len = msg.WriteRaw(buf);
        try
        {
            await _raw.WriteReportAsync(buf.AsMemory(0, len)).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            throw new ChannelException(ChannelErrorKind.Implementation, "raw write failed", e);
        }
    }

    /// <summary>Whether the channel supports the given message's report kind.</summary>
    public bool SupportsMsg(HidppMessage msg) =>
        msg.Kind == HidppReportKind.Short ? SupportsShort : SupportsLong;

    /// <summary>
    /// Re-frame a short message as long on a long-only channel. The HID++ header
    /// bytes sit at the same offsets in both widths, so only the report id and
    /// zero-padding change. A no-op on channels that advertise short support.
    /// </summary>
    private HidppMessage NormalizeOutgoing(HidppMessage msg)
    {
        if (msg.Kind == HidppReportKind.Short && !SupportsShort && SupportsLong)
        {
            var widened = new byte[HidppMessage.LongPayloadLength];
            msg.Payload.CopyTo(widened);
            return HidppMessage.Long(widened);
        }
        return msg;
    }

    private void RemovePending(long id)
    {
        lock (_pendingLock)
        {
            var pos = _pending.FindIndex(p => p.Id == id);
            if (pos >= 0) _pending.RemoveAt(pos);
        }
    }

    // ── Listeners ────────────────────────────────────────────────────────────

    /// <summary>Register a listener called for every incoming message (with whether it matched a request). Returns a handle.</summary>
    public uint AddMsgListener(Action<HidppMessage, bool> listener)
    {
        lock (_listenerLock)
        {
            uint hdl;
            do { hdl = (uint)_rng.Next(int.MinValue, int.MaxValue); } while (_listeners.ContainsKey(hdl));
            _listeners[hdl] = listener;
            return hdl;
        }
    }

    /// <summary>Register a listener removed automatically when the returned guard is disposed.</summary>
    public IDisposable AddMsgListenerGuarded(Action<HidppMessage, bool> listener)
    {
        var hdl = AddMsgListener(listener);
        return new ListenerGuard(this, hdl);
    }

    /// <summary>Remove a previously registered listener; returns whether one was found.</summary>
    public bool RemoveMsgListener(uint hdl)
    {
        lock (_listenerLock) return _listeners.Remove(hdl);
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        // Sized past the longest HID++ report: receiver interfaces also carry DJ
        // reports (ids 0x20/0x21, up to 32 bytes) and Windows reads always return
        // the collection maximum, so a 20-byte buffer would truncate real traffic.
        var buf = new byte[64];
        while (!ct.IsCancellationRequested)
        {
            int len;
            try
            {
                len = await _raw.ReadReportAsync(buf, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                continue;
            }

            var msg = HidppMessage.ReadRaw(buf.AsSpan(0, len));
            if (msg is null) continue;

            var matched = false;
            lock (_pendingLock)
            {
                var pos = _pending.FindIndex(p => p.Predicate(msg));
                if (pos >= 0)
                {
                    var waiting = _pending[pos];
                    _pending.RemoveAt(pos);
                    waiting.Tcs.TrySetResult(msg);
                    matched = true;
                }
            }

            List<Action<HidppMessage, bool>> snapshot;
            lock (_listenerLock) snapshot = [.. _listeners.Values];
            foreach (var listener in snapshot)
                listener(msg, matched);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try { await _readLoop.ConfigureAwait(false); }
        catch (OperationCanceledException) { /* expected */ }
        _cts.Dispose();
    }

    private sealed record PendingMessage(long Id, Func<HidppMessage, bool> Predicate, TaskCompletionSource<HidppMessage> Tcs);

    private sealed class ListenerGuard(HidppChannel channel, uint hdl) : IDisposable
    {
        private HidppChannel? _channel = channel;
        public void Dispose()
        {
            _channel?.RemoveMsgListener(hdl);
            _channel = null;
        }
    }
}
