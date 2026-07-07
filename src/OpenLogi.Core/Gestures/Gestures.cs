namespace OpenLogi.Core.Gestures;

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
