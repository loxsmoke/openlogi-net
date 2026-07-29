using System.Diagnostics;

namespace OpenLogi.Core.Cursor;

/// <summary>
/// Detects a deliberate "shake" of the mouse — the rapid back-and-forth wiggle
/// macOS uses to locate a lost pointer — from a stream of cursor positions.
///
/// <para>
/// A shake is a chain of direction reversals. Each stroke must be a flick: at least
/// <see cref="MinStrokeTravel"/> pixels on one axis, covered at <see cref="MinStrokeSpeed"/>
/// or better, and over within <see cref="MaxStrokeDuration"/> of the reversal that
/// began it — so a wide, slower wiggle qualifies on speed where a fixed time limit
/// would have thrown it out for being big — and it must not have stood still part-way
/// (<see cref="MaxStillWithinStroke"/>), which is what a there-and-back trip does on
/// arrival and a wiggle never does. Finally the chain must have stayed put: strokes that carried the pointer somewhere are
/// navigation, and are rejected on <see cref="MaxDriftPercent"/>. A path that keeps
/// turning the same way is the pointer being circled, and is rejected on
/// <see cref="MaxTurnPercent"/> however well it satisfies the rest.
/// </para>
///
/// <para>
/// The horizontal and vertical axes are tracked independently, so shaking either
/// way works, and the small cross-axis wobble of a horizontal shake simply never
/// builds a vertical chain of its own. Pure and unit-tested: the caller supplies
/// the positions, and the timestamped overload makes the timing deterministic.
/// </para>
/// </summary>
public sealed class ShakeDetector
{
    /// <summary>Pixels one stroke must cover on its axis before the reversal that ends it counts.</summary>
    public const int MinStrokeTravel = 60;

    /// <summary>
    /// Opposing travel that ends the current stroke. Below this a contrary sample is
    /// hand jitter mid-stroke, not a reversal — without the floor, a single wobbly
    /// pixel would end (and, being short, discard) an otherwise good stroke.
    /// </summary>
    public const int ReversalJitter = 10;

    /// <summary>Reversals that must chain together for a shake.</summary>
    public const int ReversalsForShake = 3;

    /// <summary>
    /// Reversals needed to report again once a shake has been reported. A wiggle that
    /// is being kept up has already proved what it is, so it re-reports every couple of
    /// strokes — which is what lets the pointer keep growing while the hand keeps going,
    /// rather than in slow lurches.
    /// </summary>
    public const int ReversalsToContinue = 2;

    /// <summary>
    /// A single sample larger than this is the pointer being warped — a window snap, a
    /// monitor hop, an app recentring it — rather than a hand movement, so nothing
    /// before it belongs with what comes after.
    /// </summary>
    public const int MaxSampleJump = 400;

    /// <summary>
    /// How far a chain may travel overall, as a percentage of the ground it covered. A
    /// shake ends where it started; moving across the screen with corrections does not,
    /// and this is what separates the two.
    /// </summary>
    public const int MaxDriftPercent = 40;

    /// <summary>
    /// How one-sided the recent turning may be, as a percentage of how much turning
    /// there was. Circling the pointer — an idle habit — swings both axes and reads as
    /// a wiggle on each, so it is rejected on the one thing a circle does and a shake
    /// never does: keep turning the same way.
    /// </summary>
    public const int MaxTurnPercent = 55;

    /// <summary>
    /// Turning below this is too little to judge a direction from: a shake is roughly
    /// straight, so its samples barely turn at all and the ratio would be noise.
    /// </summary>
    public const double MinTurnToJudge = 200;

    /// <summary>Weight kept per sample when averaging the turning, ≈100 ms of history.</summary>
    private const double TurnDecay = 0.94;

    /// <summary>
    /// How fast a stroke must travel to be a flick rather than ordinary aiming. Speed
    /// rather than duration is what separates them: a wide wiggle throws the pointer
    /// hundreds of pixels and so takes longer per stroke than a small one, while staying
    /// far quicker than the strokes of everyday use (MEASURED over half a minute of
    /// ordinary mousing: 100–1200 px/s, mostly at the bottom of that).
    /// </summary>
    public const double MinStrokeSpeed = 800;

    /// <summary>
    /// Longest a stroke may take however fast it was, measured from the reversal that
    /// began it — so a pause before the hand moves again counts against it.
    /// </summary>
    public static readonly TimeSpan MaxStrokeDuration = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// Longest the pointer may stand still inside a stroke. A wiggle is continuous
    /// motion — even at a turning point the axis only rests for a few milliseconds —
    /// while going somewhere and coming back pauses on arrival. That pause is what
    /// separates a fast there-and-back from a shake, since both are quick and wide.
    /// </summary>
    public static readonly TimeSpan MaxStillWithinStroke = TimeSpan.FromMilliseconds(50);


    private readonly Axis _horizontal = new();
    private readonly Axis _vertical = new();
    private readonly long _epoch = Stopwatch.GetTimestamp();

    private int _lastX;
    private int _lastY;
    private bool _hasLast;

    // Recent turning: the cross product of consecutive moves, summed signed and
    // unsigned. A circle keeps the same sign, so the two sums converge; a shake
    // reverses along its own path, so they cancel.
    private double _turn;
    private double _bend;
    private int _prevDx;
    private int _prevDy;

    /// <summary>
    /// Feed the current cursor position, timing it against the monotonic clock.
    /// Returns <c>true</c> on the sample that completes a shake.
    /// </summary>
    public bool Feed(int x, int y) => Feed(x, y, Stopwatch.GetElapsedTime(_epoch));

    /// <summary>
    /// <see cref="Feed(int,int)"/> with an explicit monotonic <paramref name="now"/>,
    /// so tests can drive the timing without sleeping.
    /// </summary>
    public bool Feed(int x, int y, TimeSpan now)
    {
        if (!_hasLast)
        {
            _lastX = x;
            _lastY = y;
            _hasLast = true;
            return false;
        }

        var dx = x - _lastX;
        var dy = y - _lastY;
        _lastX = x;
        _lastY = y;

        // A warp is a warp on both axes at once, and neither chain should survive it.
        if (Math.Abs(dx) > MaxSampleJump || Math.Abs(dy) > MaxSampleJump)
        {
            _horizontal.Reset();
            _vertical.Reset();
            return false;
        }

        var cross = (double)_prevDx * dy - (double)_prevDy * dx;
        _turn = _turn * TurnDecay + cross;
        _bend = _bend * TurnDecay + Math.Abs(cross);
        _prevDx = dx;
        _prevDy = dy;

        // Both axes are fed every sample; a shake on either one counts. The bitwise
        // form would be fine too, but || short-circuiting must not skip an axis, so
        // the two results are computed first.
        var horizontal = _horizontal.Feed(dx, now);
        var vertical = _vertical.Feed(dy, now);
        if (!horizontal && !vertical) return false;

        // The strokes are there — but a pointer being circled makes them too, and that
        // is not someone hunting for it.
        return !Circling;
    }

    /// <summary>Whether the recent path has been turning consistently one way.</summary>
    private bool Circling => _bend > MinTurnToJudge && Math.Abs(_turn) * 100 > _bend * MaxTurnPercent;

    /// <summary>
    /// Forget the current chain and position — e.g. after the pointer teleports, or
    /// when the feature is switched off mid-wiggle.
    /// </summary>
    public void Reset()
    {
        _hasLast = false;
        _turn = 0;
        _bend = 0;
        _prevDx = 0;
        _prevDy = 0;
        _horizontal.Reset();
        _vertical.Reset();
    }

    /// <summary>The reversal chain of a single axis.</summary>
    private sealed class Axis
    {
        private int _direction;       // -1 / 0 / +1: the way the current stroke travels
        private int _travel;          // distance covered by the current stroke
        private int _against;         // contrary travel banked since the stroke's last forward sample
        private int _reversals;
        private int _drift;           // signed ground covered since the chain began
        private int _path;            // unsigned ground covered since the chain began
        private bool _stalled;        // the current stroke stood still part-way
        private TimeSpan _strokeStart;
        private TimeSpan _lastMove;

        public bool Feed(int delta, TimeSpan now)
        {
            if (delta == 0) return false;

            // A gap between two moving samples is the pointer standing still on this
            // axis. Judged here rather than from the stroke's average speed, which a
            // long enough throw would hide.
            if (_direction != 0 && now - _lastMove > MaxStillWithinStroke) _stalled = true;
            _lastMove = now;

            var direction = Math.Sign(delta);
            var distance = Math.Abs(delta);
            _drift += delta;
            _path += distance;

            if (_direction == 0)
            {
                _direction = direction;
                _travel = distance;
                _strokeStart = now;
                return false;
            }

            if (direction == _direction)
            {
                _travel += distance;
                _against = 0; // the stroke resumed: whatever wobbled back was jitter
                return false;
            }

            _against += distance;
            if (_against < ReversalJitter) return false;

            // The stroke has ended. It counts towards a shake only if it was a flick —
            // far enough to be deliberate, and thrown rather than aimed — and it extends
            // the chain only if the burst is still going. The speed test is written as a
            // product so a stroke inside one sample, with no duration, still passes.
            var ms = (now - _strokeStart).TotalMilliseconds;
            var flick = !_stalled
                && _travel >= MinStrokeTravel
                && now - _strokeStart <= MaxStrokeDuration
                && _travel * 1000 >= MinStrokeSpeed * ms;
            // Each stroke is already bounded in time, so a chain of them is a burst by
            // construction — there is nothing further to check about its span.
            var chains = flick && _reversals > 0;

            if (chains)
            {
                _reversals++;
            }
            else
            {
                _reversals = flick ? 1 : 0;
                StartChain(delta, distance);
            }

            _direction = direction;
            _travel = _against;
            _against = 0;
            _stalled = false;
            _strokeStart = now;

            if (_reversals < ReversalsForShake) return false;

            // Enough reversals — but a shake also has to have stayed where it was.
            if (Math.Abs(_drift) * 100 > _path * MaxDriftPercent)
            {
                _reversals = 0;
                StartChain(delta, distance);
                return false;
            }

            // Re-arm part-way, so continued shaking keeps reporting promptly. Stop and
            // the credit lapses on its own: the next reversal either fails the flick
            // test or arrives outside the chain window, and starts a fresh count.
            _reversals = ReversalsForShake - ReversalsToContinue;
            StartChain(delta, distance);
            return true;
        }

        public void Reset()
        {
            _direction = 0;
            _travel = 0;
            _against = 0;
            _reversals = 0;
            _drift = 0;
            _path = 0;
            _stalled = false;
            _strokeStart = default;
            _lastMove = default;
        }

        /// <summary>Begin measuring a chain's locality again from this reversal.</summary>
        private void StartChain(int delta, int distance)
        {
            _drift = delta;
            _path = distance;
        }
    }
}
