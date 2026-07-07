using System.Threading.Channels;
using OpenLogi.HidPP.Channel;

namespace OpenLogi.Tests.HidPP;

/// <summary>
/// Test double for <see cref="IRawHidChannel"/>, mirroring the Rust
/// <c>MockRawHidChannel</c>: records written reports, can auto-respond on write,
/// and can push unsolicited incoming reports.
/// </summary>
public sealed class MockRawHidChannel : IRawHidChannel
{
    private readonly Channel<byte[]> _incoming = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
    private readonly List<byte[]> _written = [];
    private readonly Queue<byte[]> _responsesOnWrite = new();
    private readonly object _lock = new();

    public ushort VendorId => 0x046d;
    public ushort ProductId => 0xc539;

    /// <summary>
    /// Optional request-driven responder: invoked with each written message; a
    /// non-null result is delivered as the response. Takes precedence over the
    /// FIFO <see cref="QueueResponse"/> queue.
    /// </summary>
    public Func<HidppMessage, HidppMessage?>? OnWrite { get; set; }

    public Task<int> WriteReportAsync(ReadOnlyMemory<byte> src)
    {
        byte[]? response;
        lock (_lock)
        {
            _written.Add(src.ToArray());
            response = null;
            if (OnWrite is not null && HidppMessage.ReadRaw(src.Span) is { } request)
            {
                var reply = OnWrite(request);
                if (reply is not null) response = RawReport(reply);
            }
            if (response is null)
                _responsesOnWrite.TryDequeue(out response);
        }
        if (response is not null)
            _incoming.Writer.TryWrite(response);
        return Task.FromResult(src.Length);
    }

    public async Task<int> ReadReportAsync(Memory<byte> buf, CancellationToken cancellationToken)
    {
        var report = await _incoming.Reader.ReadAsync(cancellationToken);
        var len = Math.Min(report.Length, buf.Length);
        report.AsSpan(0, len).CopyTo(buf.Span);
        return len;
    }

    public (bool SupportsShort, bool SupportsLong)? SupportsShortLongHidpp() => (true, true);

    public Task<int> GetReportDescriptorAsync(Memory<byte> buf) =>
        throw new InvalidOperationException("mock declares HID++ support");

    // ── Test handle ──────────────────────────────────────────────────────────

    public void QueueResponse(HidppMessage msg)
    {
        lock (_lock) _responsesOnWrite.Enqueue(RawReport(msg));
    }

    public void SendIncoming(HidppMessage msg) => _incoming.Writer.TryWrite(RawReport(msg));

    /// <summary>Push an unsolicited raw report exactly as given (e.g. a Windows-style zero-padded read).</summary>
    public void SendIncomingRaw(byte[] report) => _incoming.Writer.TryWrite(report);

    public IReadOnlyList<byte[]> WrittenReports()
    {
        lock (_lock) return [.. _written];
    }

    public static HidppMessage ShortMsg(byte marker) =>
        HidppMessage.Short([0xff, marker, 0x10, marker, marker, marker]);

    private static byte[] RawReport(HidppMessage msg)
    {
        var buf = new byte[HidppMessage.LongReportLength];
        var len = msg.WriteRaw(buf);
        return buf[..len];
    }
}
