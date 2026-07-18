namespace OpenLogi.Core.Gestures;

/// <summary>
/// Gesture swipe classification and the shared mid-swipe state machine. Ported
/// from Rust <c>binding</c> (<c>detect_swipe</c>, <c>SwipeAccumulator</c>). All
/// arithmetic saturates so an arbitrarily long diagonal hold can never overflow
/// or throw — a crash in the input-hook callback is a freeze hazard.
/// </summary>
public static class Gestures
{
    /// <summary>
    /// Minimum dominant-axis travel (raw-XY units) before a held gesture commits.
    /// This is the click tolerance: a press that outlasts <see cref="HoldForSwipe"/>
    /// with accidental hand drift below this stays a Click. The Rust original's 50
    /// counts is only 1–2 mm on a modern sensor — real clicks drift that far, firing
    /// phantom gestures — while a deliberate swipe travels 10+ mm (hundreds of
    /// counts), so 150 keeps swipes effortless and clicks safe.
    /// </summary>
    public const int SwipeThreshold = 150;

    /// <summary>
    /// How many times <see cref="SwipeThreshold"/> a follow-up stroke of the same hold
    /// (a chain: left→right without releasing) must travel to commit. The first stroke's
    /// 50 counts is 1–2 mm on a modern sensor — the wobble of releasing the button
    /// covers that, so a same-threshold re-arm made "swipe, release" fire the reverse
    /// gesture too (HARDWARE-OBSERVED on an MX Anywhere 3S). A deliberate return
    /// stroke travels far more than 3×.
    /// </summary>
    public const int RearmTravelFactor = 3;

    /// <summary>
    /// Maximum cross-axis travel allowed at the threshold (floor; grows as
    /// max(deadzone, 35%)). Scaled with <see cref="SwipeThreshold"/> to keep the
    /// original geometry at the commit point (80% of the threshold): a natural hand
    /// arc curves substantially, and a tighter band would reject it as diagonal.
    /// </summary>
    public const int SwipeDeadzone = 120;

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
    public static GestureDirection? DetectSwipe(int dx, int dy) => DetectSwipe(dx, dy, SwipeThreshold);

    /// <summary>
    /// <see cref="DetectSwipe(int,int)"/> with a caller-chosen minimum dominant-axis
    /// travel — the accumulator demands more for a chained stroke than a first one.
    /// </summary>
    public static GestureDirection? DetectSwipe(int dx, int dy, int minTravel)
    {
        var absX = SaturatingAbs(dx);
        var absY = SaturatingAbs(dy);
        var dominant = Math.Max(absX, absY);
        if (dominant < minTravel) return null;
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
