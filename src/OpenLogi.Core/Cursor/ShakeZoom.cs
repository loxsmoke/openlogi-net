namespace OpenLogi.Core.Cursor;

/// <summary>
/// The size the pointer should be at, moment by moment, while shake-to-locate runs.
///
/// <para>
/// Growth belongs to the shaking, not to what follows it. The pointer answers the
/// first detected shake straight away, but that one is only worth
/// <see cref="FirstGrowth"/> — a brief nudge is all a stray detection can ever buy.
/// Each further shake of the same burst, meaning one arriving within
/// <see cref="BurstWindow"/> of the last, is worth <see cref="GrowthPerShake"/>, up
/// to <see cref="MaxScale"/>× and no further. The climb stops
/// <see cref="GrowWindow"/> after the last shake: stop wiggling and the pointer
/// settles at the size it had reached rather than carrying on up.
/// </para>
///
/// <para>
/// <see cref="Hold"/> after the last shake the target drops back to 1× and the
/// pointer eases down over <see cref="ShrinkDuration"/> rather than snapping, which
/// is what makes the shrink read as smooth.
/// </para>
///
/// <para>
/// Pure and clock-free: the caller passes the time, so the animation is driven by a
/// timer in production and by plain arithmetic in tests.
/// </para>
/// </summary>
public sealed class ShakeZoom
{
    /// <summary>The pointer's own size — nothing is applied at this scale.</summary>
    public const double MinScale = 1.0;

    /// <summary>The largest the pointer grows to, however long the shaking lasts.</summary>
    public const double MaxScale = 3.0;

    /// <summary>
    /// How much a shake that continues the burst adds to the target scale. Enough to
    /// reach the cap on its own: once a wiggle is going, the pointer should be big
    /// almost at once, the way macOS does it.
    /// </summary>
    public const double GrowthPerShake = 2.0;

    /// <summary>
    /// What the shake that opens a burst is worth on its own — a real jump, so the
    /// pointer is obviously answering, but short of the cap that continuing earns.
    /// </summary>
    public const double FirstGrowth = 1.0;

    /// <summary>
    /// How long a burst waits for its next shake. Continued wiggling reports again every
    /// couple of strokes, well inside this; two detections further apart are unrelated
    /// events rather than one sustained shake, and the second opens a burst of its own.
    /// </summary>
    public static readonly TimeSpan BurstWindow = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// How long after the last shake the pointer keeps climbing towards its target.
    /// Shorter than the time a step takes, so stopping mid-climb settles the pointer
    /// below the size the shaking was aiming for rather than letting it coast there —
    /// which is what keeps growth tied to the wiggle however fast the climb is.
    /// </summary>
    public static readonly TimeSpan GrowWindow = TimeSpan.FromMilliseconds(80);

    /// <summary>How long the pointer stays at its full size after the last shake.</summary>
    public static readonly TimeSpan Hold = TimeSpan.FromMilliseconds(1000);

    /// <summary>
    /// Time the pointer takes to cover the whole 1×–3× range while growing. Deliberately
    /// far quicker than <see cref="ShrinkDuration"/>: growing is the answer to a
    /// question and wants to be immediate, while shrinking is just tidying up and reads
    /// better slow.
    /// </summary>
    public static readonly TimeSpan GrowDuration = TimeSpan.FromMilliseconds(150);

    /// <summary>Time the pointer takes to ease back down over the whole range.</summary>
    public static readonly TimeSpan ShrinkDuration = TimeSpan.FromMilliseconds(700);

    private static readonly double GrowRate = (MaxScale - MinScale) / GrowDuration.TotalMilliseconds;
    private static readonly double ShrinkRate = (MaxScale - MinScale) / ShrinkDuration.TotalMilliseconds;

    private double _scale = MinScale;
    private double _target = MinScale;
    private int _shakes;
    private TimeSpan _lastShake;

    /// <summary>The scale the pointer should be drawn at right now.</summary>
    public double Scale => _scale;

    /// <summary>Whether anything is still to be drawn or undone — i.e. the timer must keep ticking.</summary>
    public bool IsActive => _scale > MinScale || _target > MinScale;

    /// <summary>
    /// Record a detected shake at <paramref name="now"/>. Once the wiggle has proved
    /// itself, this raises the target one step — taken from whichever is higher, where
    /// the pointer is or where it was already heading, so a shake mid-shrink resumes
    /// the climb rather than restarting it from the size it happens to have shrunk to.
    /// </summary>
    public void Shake(TimeSpan now)
    {
        // Too long since the last one: whatever that was, it wasn't this wiggle, so
        // this shake opens a burst of its own and is worth only an opening step.
        if (now - _lastShake > BurstWindow) _shakes = 0;
        _shakes++;
        _lastShake = now;

        // The step is taken from whichever is higher — where the pointer is or where it
        // was already heading — so a shake mid-shrink carries on from there rather than
        // from the size it happens to have shrunk to. The shake that opens a burst can
        // never lift the pointer past its opening step, however many bursts have opened
        // before it: stray detections nudge, they do not accumulate.
        var from = Math.Max(_target, _scale);
        var wanted = _shakes == 1
            ? Math.Max(from, MinScale + FirstGrowth)
            : from + GrowthPerShake;
        _target = Math.Min(MaxScale, wanted);
    }

    /// <summary>
    /// Advance the animation by <paramref name="elapsed"/>, given the time
    /// <paramref name="now"/>, and return the scale to draw. Moves at a fixed rate,
    /// so a late or coalesced tick covers exactly the ground it missed.
    /// </summary>
    public double Advance(TimeSpan now, TimeSpan elapsed)
    {
        var since = now - _lastShake;
        // The wiggle stopped: settle where the pointer got to, rather than climbing on
        // towards a size the shaking never sustained.
        if (_target > _scale && since > GrowWindow) _target = _scale;
        if (_target > MinScale && since > Hold) _target = MinScale;

        var ms = Math.Max(0, elapsed.TotalMilliseconds);
        if (_scale < _target) _scale = Math.Min(_target, _scale + GrowRate * ms);
        else if (_scale > _target) _scale = Math.Max(_target, _scale - ShrinkRate * ms);
        return _scale;
    }

    /// <summary>Drop straight back to 1×, skipping the animation (shutdown, or the feature switched off).</summary>
    public void Reset()
    {
        _scale = MinScale;
        _target = MinScale;
        _shakes = 0;
        _lastShake = default;
    }
}
