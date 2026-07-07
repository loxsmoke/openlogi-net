using System.Reflection;

namespace OpenLogi.Core.Logging;

/// <summary>
/// The app-wide diagnostic log at <see cref="Paths.LogDir"/>. A thin static
/// facade over one lazily-created <see cref="LogFile"/>; all methods are
/// no-throw and safe from any thread. Call <see cref="ShutdownAsync"/> on clean
/// exit so the session-end marker distinguishes exits from crashes (a
/// ProcessExit fallback writes "process exit" otherwise).
/// </summary>
public static class DiagnosticLog
{
    private static readonly object InitLock = new();
    private static LogFile? _file;
    private static bool _initTried;

    private static LogFile? File_
    {
        get
        {
            if (_file is not null || _initTried) return _file;
            lock (InitLock)
            {
                if (_initTried) return _file;
                _initTried = true;
                try
                {
                    var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "?";
                    _file = new LogFile(Paths.LogDir(), appVersion: version);
                    AppDomain.CurrentDomain.ProcessExit += (_, _) =>
                        _file?.ShutdownAsync("process exit").AsTask().Wait(1000);
                }
                catch { /* pathing failed — stay a no-op */ }
                return _file;
            }
        }
    }

    /// <summary>
    /// Suppress all writes (Settings → "Suppress diagnostic logs"). Checked before
    /// the lazy init, so a session that starts suppressed never creates or opens
    /// the log file at all. Off by default: logging runs on a fresh install and
    /// on upgraded configs missing the key.
    /// </summary>
    public static bool Suppressed { get; set; }

    /// <summary>Whether Debug-level lines are recorded (default: only with OPENLOGI_DEBUG=1).</summary>
    public static bool DebugEnabled
    {
        get => File_?.DebugEnabled ?? false;
        set { if (File_ is { } f) f.DebugEnabled = value; }
    }

    public static void Debug(string area, string text)
    {
        if (!Suppressed) File_?.Write(LogLevel.Debug, area, text);
    }

    public static void Info(string area, string text)
    {
        if (!Suppressed) File_?.Write(LogLevel.Info, area, text);
    }

    public static void Warn(string area, string text)
    {
        if (!Suppressed) File_?.Write(LogLevel.Warn, area, text);
    }

    public static void Error(string area, string text, Exception? exception = null)
    {
        if (Suppressed) return;
        if (exception is null) File_?.Write(LogLevel.Error, area, text);
        else File_?.Write(LogLevel.Error, area, text, exception);
    }

    /// <summary>Completes when everything queued so far is on disk.</summary>
    public static Task FlushAsync() => File_?.FlushAsync() ?? Task.CompletedTask;

    /// <summary>Write the session-end marker and close the file.</summary>
    public static ValueTask ShutdownAsync(string reason = "clean exit") =>
        _file?.ShutdownAsync(reason) ?? ValueTask.CompletedTask;
}
