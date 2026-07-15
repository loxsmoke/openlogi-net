using System.Diagnostics;

namespace OpenLogi.Core.Gestures;

/// <summary>
/// The mid-swipe state machine shared by both gesture-capture paths (the HID++
/// dedicated gesture button and the OS-hook Middle/Back/Forward buttons). A hold
/// accumulates travel; the instant the dominant axis commits — after the hold
/// passes <see cref="Gestures.HoldForSwipe"/> — <see cref="Accumulate"/> returns that
/// direction. A hold that never commits is a plain click, reported by
/// <see cref="End"/>. Ported from Rust <c>binding::SwipeAccumulator</c>.
/// <para>
/// A single hold can chain gestures: after one commits, reversing (or turning onto
/// the other axis) commits the next, so press → left → right fires both without
/// releasing. Re-arming requires a *change* of direction, which is what keeps one
/// long sweep worth exactly one gesture no matter how far it runs.
/// </para>
/// </summary>
public sealed class SwipeAccumulator
{
    private long? _heldSinceTicks;
    private int _dx;
    private int _dy;

    /// <summary>
    /// The last direction committed during this hold, or <c>null</c> before the first.
    /// Doubles as the click fallback's record of whether any swipe fired at all, and
    /// as the re-arm gate: the next commit must differ from it.
    /// </summary>
    private GestureDirection? _lastFired;

    /// <summary>Begin a fresh hold, resetting travel and commit state.</summary>
    public void Begin()
    {
        _heldSinceTicks = Stopwatch.GetTimestamp();
        _dx = 0;
        _dy = 0;
        _lastFired = null;
    }

    /// <summary>Whether a hold is in progress (between <see cref="Begin"/> and <see cref="End"/>).</summary>
    public bool IsHolding => _heldSinceTicks is not null;

    /// <summary>
    /// Feed a pointer-move / raw-XY delta into the current hold. Returns a direction
    /// the instant travel commits — only after the hold passes
    /// <see cref="Gestures.HoldForSwipe"/>, and only when it differs from the previous
    /// commit of this hold — else <c>null</c>.
    /// </summary>
    public GestureDirection? Accumulate(int dx, int dy)
    {
        if (_heldSinceTicks is null) return null;

        // Still traveling the way we just fired? Then the stroke that fired hasn't
        // ended — it's follow-through, so hold the pivot at the current position.
        // Zeroing *both* axes matters: an arcing sweep (say left, drifting upward)
        // would otherwise bank its drift on the free axis and fire a phantom Up.
        // Only reachable once something has fired, so a first swipe accumulates
        // exactly as it always has.
        if (_lastFired is { } last && Continues(dx, dy, last))
        {
            _dx = 0;
            _dy = 0;
            return null;
        }

        _dx = SaturatingAddPublic(_dx, dx);
        _dy = SaturatingAddPublic(_dy, dy);
        var elapsed = Stopwatch.GetElapsedTime(_heldSinceTicks.Value);
        if (elapsed >= Gestures.HoldForSwipe && Gestures.DetectSwipe(_dx, _dy) is { } dir && dir != _lastFired)
        {
            _lastFired = dir;
            _dx = 0;
            _dy = 0;
            return dir;
        }
        return null;
    }

    /// <summary>
    /// Whether a single delta is still traveling <paramref name="direction"/> — its
    /// dominant axis pointing the same way. Judged per delta rather than on the running
    /// total, so mouse jitter opposing the stroke can never be mistaken for a reversal.
    /// </summary>
    private static bool Continues(int dx, int dy, GestureDirection direction) =>
        Math.Abs(dx) >= Math.Abs(dy)
            ? direction == (dx > 0 ? GestureDirection.Right : dx < 0 ? GestureDirection.Left : null)
            : direction == (dy > 0 ? GestureDirection.Down : GestureDirection.Up);

    /// <summary>
    /// End the current hold, returning the direction to fire — or <c>null</c> when a
    /// swipe already committed mid-hold, or there was no hold at all.
    /// <para>
    /// The travel is classified once more here, because <see cref="Accumulate"/> can
    /// only commit while raw-XY events are arriving. A quick flick finishes inside
    /// <see cref="Gestures.HoldForSwipe"/> and then the mouse sits still, so no further
    /// event ever comes to re-check the (already sufficient) travel: without this the
    /// swipe would be silently downgraded to <see cref="GestureDirection.Click"/> —
    /// gestures appearing to "work only sometimes", depending on flick speed.
    /// </para>
    /// A hold whose travel never resolves to a direction is a plain Click, as is one
    /// released before the hold gate (a quick click that happened to drift).
    /// </summary>
    public GestureDirection? End()
    {
        if (_heldSinceTicks is null) return null;
        var elapsed = Stopwatch.GetElapsedTime(_heldSinceTicks.Value);
        _heldSinceTicks = null;

        // Settle the stroke in progress — including the last one of a chain, which is
        // just as likely as a lone swipe to have been flicked out inside the gate.
        if (elapsed >= Gestures.HoldForSwipe && Gestures.DetectSwipe(_dx, _dy) is { } dir && dir != _lastFired)
        {
            _lastFired = dir;
            return dir;
        }
        // Click only when the whole hold produced no swipe at all.
        return _lastFired is null ? GestureDirection.Click : null;
    }

    /// <summary>
    /// Test-only seam: backdate the current hold so its hold gate is already
    /// satisfied, letting a test exercise a committed swipe without sleeping.
    /// A no-op when not currently holding.
    /// </summary>
    public void BackdateHoldForTest()
    {
        if (_heldSinceTicks is not null)
            _heldSinceTicks = Stopwatch.GetTimestamp() - 2 * (long)(Gestures.HoldForSwipe.TotalSeconds * Stopwatch.Frequency);
    }

    private static int SaturatingAddPublic(int a, int b)
    {
        long sum = (long)a + b;
        return sum > int.MaxValue ? int.MaxValue : sum < int.MinValue ? int.MinValue : (int)sum;
    }
}
