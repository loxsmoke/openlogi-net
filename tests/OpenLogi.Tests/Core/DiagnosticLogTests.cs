using System.Text.RegularExpressions;
using OpenLogi.Core.Logging;

// Not OpenLogi.Tests.Core: that sibling namespace would capture the `Core.…`
// qualified references other test files make into OpenLogi.Core.
namespace OpenLogi.Tests.Logging;

/// <summary>
/// Exercises the <see cref="LogFile"/> writer against real temp files: line
/// format, consecutive-duplicate suppression, exception detail, rotation and
/// the session markers. Each test gets its own directory, so tests parallelize.
/// </summary>
public class DiagnosticLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "openlogi-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private LogFile NewLog(long maxBytes = 256 * 1024) => new(_dir, maxBytes, appVersion: "1.2.3");

    private string[] ReadLines() => File.ReadAllLines(Path.Combine(_dir, "log.txt"));

    [Fact]
    public async Task StandardLineFormat()
    {
        var log = NewLog();
        log.Write(LogLevel.Info, "sweep", "3 Logitech HID++ node(s)");
        log.Write(LogLevel.Warn, "sweep", "uid read failed");
        await log.ShutdownAsync("clean exit");

        var lines = ReadLines();
        Assert.Matches(new Regex(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} INFO  sweep: 3 Logitech HID\+\+ node\(s\)$"), lines[1]);
        Assert.Matches(new Regex(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} WARN  sweep: uid read failed$"), lines[2]);
    }

    [Fact]
    public async Task SessionMarkersWrapTheLog()
    {
        var log = NewLog();
        log.Write(LogLevel.Info, "env", "hello");
        await log.ShutdownAsync("clean exit");

        var lines = ReadLines();
        Assert.StartsWith("==== OpenLogi.net v1.2.3 session start ", lines[0]);
        Assert.Contains("(UTC", lines[0]);
        Assert.StartsWith("==== session end ", lines[^1]);
        Assert.EndsWith("(clean exit) ====", lines[^1]);
    }

    [Fact]
    public async Task ConsecutiveDuplicatesAreSuppressedAndSummarized()
    {
        var log = NewLog();
        for (var i = 0; i < 5; i++)
            log.Write(LogLevel.Warn, "hid", "read timeout");
        log.Write(LogLevel.Info, "sweep", "next");
        await log.ShutdownAsync("clean exit");

        var lines = ReadLines();
        Assert.Single(lines, l => l.EndsWith("WARN  hid: read timeout"));
        Assert.Single(lines, l => Regex.IsMatch(l, @"WARN  hid: \(previous message repeated 4× since \d{2}:\d{2}:\d{2}\)$"));
        Assert.Single(lines, l => l.EndsWith("INFO  sweep: next"));
    }

    [Fact]
    public async Task PendingSuppressionIsFlushedAtShutdown()
    {
        var log = NewLog();
        log.Write(LogLevel.Warn, "hid", "read timeout");
        log.Write(LogLevel.Warn, "hid", "read timeout");
        await log.ShutdownAsync("clean exit");

        Assert.Single(ReadLines(), l => l.Contains("repeated 1× since"));
    }

    [Fact]
    public async Task ExceptionDetailIsIndented()
    {
        var log = NewLog();
        Exception caught;
        try { throw new InvalidOperationException("boom"); }
        catch (Exception ex) { caught = ex; }
        log.Write(LogLevel.Error, "sweep", "probe failed", caught);
        await log.ShutdownAsync("clean exit");

        var lines = ReadLines();
        Assert.Single(lines, l => l.EndsWith("ERROR sweep: probe failed"));
        Assert.Contains(lines, l => l.StartsWith("    | System.InvalidOperationException: boom"));
        Assert.Contains(lines, l => l.StartsWith("    |    at "));
    }

    [Fact]
    public async Task RotationCapsTheFileAndLeavesAMarker()
    {
        var log = NewLog(maxBytes: 2048);
        for (var i = 0; i < 200; i++)
            log.Write(LogLevel.Info, "sweep", $"line number {i} with some padding text to grow the file");
        await log.ShutdownAsync("clean exit");

        Assert.True(File.Exists(Path.Combine(_dir, "log.1.txt")), "rotated file should exist");
        var lines = ReadLines();
        Assert.StartsWith("==== rotated from log.txt ", lines[0]);
        // Current file stays near the cap: at most one line past it before rotating.
        Assert.True(new FileInfo(Path.Combine(_dir, "log.txt")).Length < 2048 + 256);
    }

    [Fact]
    public async Task DebugLinesAreGatedByTheFlag()
    {
        var log = NewLog();
        log.DebugEnabled = false;
        log.Write(LogLevel.Debug, "hid", "hidden");
        log.DebugEnabled = true;
        log.Write(LogLevel.Debug, "hid", "shown");
        await log.ShutdownAsync("clean exit");

        var lines = ReadLines();
        Assert.DoesNotContain(lines, l => l.Contains("hidden"));
        Assert.Single(lines, l => l.EndsWith("DEBUG hid: shown"));
    }

    [Fact]
    public async Task FlushMakesQueuedLinesVisibleWhileRunning()
    {
        await using var log = NewLog();
        log.Write(LogLevel.Info, "env", "visible before shutdown");
        await log.FlushAsync();

        // Shared-read open: the file is readable while the writer holds it.
        using var fs = new FileStream(Path.Combine(_dir, "log.txt"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        Assert.Contains("visible before shutdown", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task WriteNeverThrowsWhenDirectoryIsUnusable()
    {
        // A file standing where the directory should be makes Open() fail.
        Directory.CreateDirectory(Path.GetDirectoryName(_dir)!);
        await File.WriteAllTextAsync(_dir, "not a directory");
        try
        {
            var log = new LogFile(_dir + "\\sub", appVersion: "1.2.3");
            log.Write(LogLevel.Info, "env", "goes nowhere");
            await log.FlushAsync();
            await log.ShutdownAsync("clean exit"); // must not throw
        }
        finally
        {
            File.Delete(_dir);
        }
    }
}
