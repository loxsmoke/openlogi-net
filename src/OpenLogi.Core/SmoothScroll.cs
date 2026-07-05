namespace OpenLogi.Core;

/// <summary>
/// Converts diverted hi-res wheel ticks (HiResWheel 0x2121 movement events) into
/// OS wheel data, where <see cref="WheelDelta"/> equals one physical notch: each
/// tick becomes <c>WheelDelta / multiplier</c>. The division remainder carries
/// over between calls, so no rotation is lost with a multiplier that doesn't
/// divide <see cref="WheelDelta"/> evenly.
/// </summary>
public sealed class SmoothScrollScaler(int multiplier)
{
    /// <summary>One physical wheel notch in OS wheel-data units (Windows WHEEL_DELTA).</summary>
    public const int WheelDelta = 120;

    private readonly int _multiplier = Math.Max(1, multiplier);
    private int _carry;

    /// <summary>Scale a hi-res tick delta; returns the OS wheel data to inject now (0 = nothing yet).</summary>
    public int Add(int delta)
    {
        _carry += delta * WheelDelta;
        var emit = _carry / _multiplier;
        _carry -= emit * _multiplier;
        return emit;
    }
}
