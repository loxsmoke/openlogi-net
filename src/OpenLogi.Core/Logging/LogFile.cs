using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace OpenLogi.Core.Logging;

/// <summary>
/// A size-capped, non-blocking diagnostic log file. Writes go to a bounded
/// in-memory queue drained by one background task, so a caller is never stalled
/// by disk I/O — a full queue drops messages and reports the count instead.
/// The file rotates in place (log.txt → log.1.txt) at <c>maxBytes</c>, bounding
/// total footprint at ~2× the cap with no cleanup job. Consecutive identical
/// messages are suppressed and summarized ("repeated N×") so a flapping device
/// can't flood the file. Never logs from hot paths (mouse hook, event pumps) —
/// this type only makes that cheap, the call sites enforce it.
///
/// Line formats (see the drain loop): "====" session/rotation markers, the
/// standard "date time LEVEL area: text" line, "    | " exception detail lines,
/// and the dedup summary. Timestamps carry the full date because rotated logs
/// span weeks and get pasted into issues as fragments.
/// </summary>
public sealed class LogFile : IAsyncDisposable
{
    private const int QueueCapacity = 4096;
    private const int MaxDetailLines = 30;

    private readonly string _path;
    private readonly string _rotatedPath;
    private readonly long _maxBytes;
    private readonly string _appVersion;
    private readonly Channel<Entry> _queue;
    private readonly Task _drain;
    private int _dropped;
    private volatile string _endReason = "process exit";

    /// <summary>Whether Debug-level lines are recorded (default: only with OPENLOGI_DEBUG=1).</summary>
    public bool DebugEnabled { get; set; } = Environment.GetEnvironmentVariable("OPENLOGI_DEBUG") == "1";

    private readonly record struct Entry(
        DateTime At, LogLevel Level, string Area, string Text, string? Detail, TaskCompletionSource? Flushed);

    public LogFile(string directory, long maxBytes = 256 * 1024, string? appVersion = null)
    {
        _path = Path.Combine(directory, "log.txt");
        _rotatedPath = Path.Combine(directory, "log.1.txt");
        _maxBytes = maxBytes;
        _appVersion = appVersion ?? "?";
        _queue = Channel.CreateBounded<Entry>(new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropWrite,
        });
        _drain = Task.Run(DrainAsync);
    }

    /// <summary>Queue one line; costs a channel push, never blocks, never throws.</summary>
    public void Write(LogLevel level, string area, string text, string? detail = null)
    {
        if (level == LogLevel.Debug && !DebugEnabled) return;
        if (!_queue.Writer.TryWrite(new Entry(DateTime.Now, level, area, text, detail, null)))
            Interlocked.Increment(ref _dropped);
    }

    /// <summary>Queue one line with an exception rendered as indented detail lines.</summary>
    public void Write(LogLevel level, string area, string text, Exception exception) =>
        Write(level, area, text, exception.ToString());

    /// <summary>Completes when everything queued so far is on disk. For tests and shutdown.</summary>
    public Task FlushAsync()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.Writer.TryWrite(new Entry(DateTime.Now, LogLevel.Debug, "", "", null, tcs)))
            tcs.TrySetResult();
        return tcs.Task;
    }

    /// <summary>Drain the queue, write the session-end marker and close the file.</summary>
    public async ValueTask ShutdownAsync(string reason)
    {
        _endReason = reason;
        _queue.Writer.TryComplete();
        try { await _drain.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch { /* drain wedged on I/O — abandon rather than hang shutdown */ }
    }

    public ValueTask DisposeAsync() => ShutdownAsync(_endReason);

    // ── Drain (single reader; owns the file) ─────────────────────────────────

    private async Task DrainAsync()
    {
        StreamWriter? writer = null;
        FileStream? stream = null;

        // Consecutive-duplicate suppression state.
        string? lastKey = null;
        LogLevel lastLevel = default;
        string lastArea = "";
        var suppressed = 0;
        DateTime suppressedSince = default;

        void WriteRaw(string line)
        {
            if (writer is null) return;
            if (stream!.Length > _maxBytes)
            {
                writer.Dispose();
                File.Move(_path, _rotatedPath, overwrite: true);
                (stream, writer) = Open();
                writer?.WriteLine($"==== rotated from log.txt {Stamp(DateTime.Now)} ====");
                if (writer is null) return;
            }
            writer.WriteLine(line);
        }

        void FlushSuppressed(DateTime at)
        {
            if (suppressed == 0) return;
            WriteRaw(Format(at, lastLevel, lastArea,
                $"(previous message repeated {suppressed}× since {suppressedSince.ToString("HH:mm:ss", CultureInfo.InvariantCulture)})"));
            suppressed = 0;
        }

        try
        {
            (stream, writer) = Open();
            // Guarded separately: a failed header (disk full at startup) must not
            // kill the drain — flush completions below depend on it staying alive.
            try
            {
                writer?.WriteLine(
                    $"==== OpenLogi.net v{_appVersion} session start {Stamp(DateTime.Now)} (UTC{DateTimeOffset.Now.ToString("zzz", CultureInfo.InvariantCulture)}) ====");
            }
            catch { /* keep draining; later writes retry the stream */ }

            await foreach (var e in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (e.Flushed is not null)
                {
                    try { writer?.Flush(); } catch { /* keep draining */ }
                    e.Flushed.TrySetResult();
                    continue;
                }
                if (writer is null) continue; // file unavailable — swallow silently
                try
                {
                    var droppedNow = Interlocked.Exchange(ref _dropped, 0);
                    if (droppedNow > 0)
                    {
                        FlushSuppressed(e.At);
                        lastKey = null;
                        WriteRaw(Format(e.At, LogLevel.Warn, "log", $"queue overflowed, dropped {droppedNow} message(s)"));
                    }

                    var key = $"{e.Level}|{e.Area}|{e.Text}";
                    if (key == lastKey)
                    {
                        if (suppressed++ == 0) suppressedSince = e.At;
                        continue;
                    }
                    FlushSuppressed(e.At);
                    WriteRaw(Format(e.At, e.Level, e.Area, e.Text));
                    if (e.Detail is not null)
                    {
                        var lines = e.Detail.Split('\n');
                        foreach (var line in lines.Take(MaxDetailLines))
                            WriteRaw("    | " + line.TrimEnd('\r'));
                        if (lines.Length > MaxDetailLines)
                            WriteRaw($"    | … {lines.Length - MaxDetailLines} more line(s)");
                    }
                    lastKey = key;
                    lastLevel = e.Level;
                    lastArea = e.Area;
                }
                catch { /* one bad write must not kill the drain */ }
            }

            try
            {
                FlushSuppressed(DateTime.Now);
                writer?.WriteLine($"==== session end {Stamp(DateTime.Now)} ({_endReason}) ====");
            }
            catch { /* disk gone at shutdown — nothing left to protect */ }
        }
        catch { /* logging must never take the app down */ }
        finally
        {
            try { writer?.Dispose(); } catch { /* already broken */ }
        }
    }

    /// <summary>
    /// Open for append with shared read, so "Open logs" can copy the file while
    /// the app runs. UTF-8 without BOM, CRLF, flushed per line (volume is tiny).
    /// Returns (null, null) when the directory/file is unusable — logging then
    /// degrades to a silent no-op rather than erroring anywhere.
    /// </summary>
    private (FileStream?, StreamWriter?) Open()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
            return (stream, new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\r\n" });
        }
        catch
        {
            return (null, null);
        }
    }

    private static string Stamp(DateTime at) => at.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static string Format(DateTime at, LogLevel level, string area, string text)
    {
        var tag = level switch
        {
            LogLevel.Debug => "DEBUG",
            LogLevel.Info => "INFO ",
            LogLevel.Warn => "WARN ",
            _ => "ERROR",
        };
        return $"{at.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} {tag} {area}: {text}";
    }
}
