using System.Diagnostics;
using OpenLogi.Core.Cursor;

namespace OpenLogi.Input;

/// <summary>
/// Shake-to-locate: watches pointer movement for the macOS-style wiggle, grows the
/// pointer while the shaking keeps up — to <see cref="ShakeZoom.MaxScale"/>× — and
/// eases it back down a moment after it stops.
///
/// <para>
/// <see cref="PointerMoved"/> runs on the low-level hook thread — Windows tears a
/// hook down if its callback stalls — so it does no more than arithmetic under a
/// briefly held lock, and never blocks or throws. The resizing itself belongs to a
/// frame timer: each tick asks <see cref="ShakeZoom"/> where the pointer should be
/// and draws it there, which is what makes the growth and the shrink smooth instead
/// of a jump each way. The timer only runs while something is animating.
/// </para>
/// </summary>
public sealed class CursorLocator : IDisposable
{
    /// <summary>How often the pointer is redrawn while growing or shrinking (~33 fps).</summary>
    public static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(30);

    private readonly ShakeDetector _detector = new();
    private readonly ShakeZoom _zoom = new();
    private readonly object _gate = new();
    private readonly Timer _frames;
    private readonly long _epoch = Stopwatch.GetTimestamp();

    private TimeSpan _lastFrame;
    private bool _running;
    private int _ticking;
    private int _disposed;

    public CursorLocator()
    {
        _frames = new Timer(_ => Tick(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        // A previous run may have died mid-shake, leaving the pointer enlarged.
        CursorSize.RecoverStaleCursors();
    }

    /// <summary>Feed a pointer position; a completed shake raises the target size and starts the frames.</summary>
    public void PointerMoved(int x, int y)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (!_detector.Feed(x, y)) return;

        var now = Stopwatch.GetElapsedTime(_epoch);
        lock (_gate)
        {
            _zoom.Shake(now);
            StartFrames(now);
        }
    }

    /// <summary>
    /// Forget any half-built shake — used when the feature is switched off, so the
    /// strokes made while it was on can't combine with later movement. An enlarged
    /// pointer is left to the frame timer, which shrinks it back as usual now that no
    /// further shakes arrive.
    /// </summary>
    public void Reset() => _detector.Reset();

    private void Tick()
    {
        // A frame that overruns its interval must not stack up behind itself.
        if (Interlocked.Exchange(ref _ticking, 1) != 0) return;
        try
        {
            double scale;
            lock (_gate)
            {
                if (Volatile.Read(ref _disposed) != 0) return;
                var now = Stopwatch.GetElapsedTime(_epoch);
                scale = _zoom.Advance(now, now - _lastFrame);
                _lastFrame = now;
                // The frame that lands back at 1× is the one that restores, so stopping
                // here still leaves the user's own cursors on screen.
                if (!_zoom.IsActive) StopFrames();
            }

            // Outside the lock: swapping every system cursor is the slow part, and the
            // hook thread must never wait on it.
            CursorSize.Apply(scale);
            // Dispose may have restored while this ran — it sets the flag before it
            // restores, so re-reading it here catches the race and undoes the swap.
            if (Volatile.Read(ref _disposed) != 0) CursorSize.Restore();
        }
        finally
        {
            Volatile.Write(ref _ticking, 0);
        }
    }

    private void StartFrames(TimeSpan now)
    {
        if (_running) return;
        _lastFrame = now;
        try { _frames.Change(Frame, Frame); }
        catch (ObjectDisposedException) { return; } // disposed mid-shake: nothing left to animate
        _running = true;
    }

    private void StopFrames()
    {
        if (!_running) return;
        _running = false;
        try { _frames.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { /* already torn down */ }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _frames.Dispose();
        // Never leave the user with a session-wide giant pointer.
        CursorSize.Restore();
    }
}
