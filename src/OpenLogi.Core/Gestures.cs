using System.Diagnostics;

namespace OpenLogi.Core;

/// <summary>
/// Gesture swipe classification and the shared mid-swipe state machine. Ported
/// from Rust <c>binding</c> (<c>detect_swipe</c>, <c>SwipeAccumulator</c>). All
/// arithmetic saturates so an arbitrarily long diagonal hold can never overflow
/// or throw — a crash in the input-hook callback is a freeze hazard.
/// </summary>
public static class Gestures
{
    /// <summary>Minimum dominant-axis travel (raw-XY units) before a held gesture commits.</summary>
    public const int SwipeThreshold = 50;

    /// <summary>Maximum cross-axis travel allowed at the threshold (floor; grows as max(deadzone, 35%)).</summary>
    public const int SwipeDeadzone = 40;

    /// <summary>Minimum hold before travel can commit to a swipe (vs. a quick click that drifted).</summary>
    public static readonly TimeSpan HoldForSwipe = TimeSpan.FromMilliseconds(160);

    private static int SaturatingAbs(int x) => x == int.MinValue ? int.MaxValue : Math.Abs(x);

    private static int SaturatingAdd(int a, int b)
    {
        long sum = (long)a + b;
        return sum > int.MaxValue ? int.MaxValue : sum < int.MinValue ? int.MinValue : (int)sum;
    }

    private static int SaturatingMul(int a, int b)
    {
        long product = (long)a * b;
        return product > int.MaxValue ? int.MaxValue : product < int.MinValue ? int.MinValue : (int)product;
    }

    /// <summary>
    /// Classify the running raw-XY travel of a held gesture into a directional
    /// swipe the instant it commits, or <c>null</c> while still too short or too
    /// diagonal. Coordinates follow the device convention (+x right, +y down),
    /// so an upward swipe (negative dy) maps to <see cref="GestureDirection.Up"/>.
    /// </summary>
    public static GestureDirection? DetectSwipe(int dx, int dy)
    {
        var absX = SaturatingAbs(dx);
        var absY = SaturatingAbs(dy);
        var dominant = Math.Max(absX, absY);
        if (dominant < SwipeThreshold) return null;
        var crossLimit = Math.Max(SwipeDeadzone, SaturatingMul(dominant, 35) / 100);
        if (absX > absY)
        {
            if (absY > crossLimit) return null;
            return dx > 0 ? GestureDirection.Right : GestureDirection.Left;
        }
        if (absX > crossLimit) return null;
        return dy > 0 ? GestureDirection.Down : GestureDirection.Up;
    }
}

/// <summary>
/// The mid-swipe state machine shared by both gesture-capture paths (the HID++
/// dedicated gesture button and the OS-hook Middle/Back/Forward buttons). A hold
/// accumulates travel; the instant the dominant axis commits — after the hold
/// passes <see cref="Gestures.HoldForSwipe"/> — <see cref="Accumulate"/> returns that
/// direction exactly once. A hold that never commits is a plain click, reported
/// by <see cref="End"/>. Ported from Rust <c>binding::SwipeAccumulator</c>.
/// </summary>
public sealed class SwipeAccumulator
{
    private long? _heldSinceTicks;
    private int _dx;
    private int _dy;
    private bool _fired;

    /// <summary>Begin a fresh hold, resetting travel and commit state.</summary>
    public void Begin()
    {
        _heldSinceTicks = Stopwatch.GetTimestamp();
        _dx = 0;
        _dy = 0;
        _fired = false;
    }

    /// <summary>Whether a hold is in progress (between <see cref="Begin"/> and <see cref="End"/>).</summary>
    public bool IsHolding => _heldSinceTicks is not null;

    /// <summary>
    /// Feed a pointer-move / raw-XY delta into the current hold. Returns a
    /// direction exactly once per hold — the instant travel commits, and only
    /// after the hold passes <see cref="Gestures.HoldForSwipe"/> — else <c>null</c>.
    /// </summary>
    public GestureDirection? Accumulate(int dx, int dy)
    {
        if (_fired || _heldSinceTicks is null) return null;
        _dx = SaturatingAddPublic(_dx, dx);
        _dy = SaturatingAddPublic(_dy, dy);
        var elapsed = Stopwatch.GetElapsedTime(_heldSinceTicks.Value);
        if (elapsed >= Gestures.HoldForSwipe && Gestures.DetectSwipe(_dx, _dy) is { } dir)
        {
            _fired = true;
            return dir;
        }
        return null;
    }

    /// <summary>
    /// End the current hold. Returns <c>true</c> when an in-progress hold ended
    /// without committing a swipe (the caller should fire the plain Click action),
    /// and <c>false</c> when a swipe already fired or there was no hold.
    /// </summary>
    public bool End()
    {
        var wasClick = _heldSinceTicks is not null && !_fired;
        _heldSinceTicks = null;
        return wasClick;
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
