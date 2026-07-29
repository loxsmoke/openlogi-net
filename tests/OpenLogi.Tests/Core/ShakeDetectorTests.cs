using OpenLogi.Core.Cursor;

namespace OpenLogi.Tests.Core;

/// <summary>
/// The shake-to-locate detector. Positions and timestamps are fed explicitly, so
/// the timing gates are exercised without sleeping.
/// </summary>
public class ShakeDetectorTests
{
    private const int Stroke = ShakeDetector.MinStrokeTravel + 20;

    // A clock, a pointer and a heading that carry across the calls of one test, so two
    // Wiggle calls in a row are one continuous wiggle rather than a stitched-up one.
    private TimeSpan _at;
    private int _x = 500;
    private int _heading = 1;

    /// <summary>Samples a mouse reports across one stroke — it does not jump there.</summary>
    private const int PerStroke = 8;

    /// <summary>
    /// Wiggle horizontally: <paramref name="strokes"/> strokes of <paramref name="travel"/>
    /// pixels, each taking <paramref name="gapMs"/> and reported in even steps as a real
    /// mouse would. Returns whether a shake fired at any point.
    /// </summary>
    private bool Wiggle(ShakeDetector detector, int strokes, int travel = Stroke, int gapMs = 60)
    {
        var fired = false;
        detector.Feed(_x, 400, _at);
        for (var i = 0; i < strokes; i++)
        {
            for (var s = 0; s < PerStroke; s++)
            {
                _x += _heading * travel / PerStroke;
                _at += TimeSpan.FromMilliseconds((double)gapMs / PerStroke);
                fired |= detector.Feed(_x, 400, _at);
            }
            _heading = -_heading;
        }
        return fired;
    }

    [Fact]
    public void RapidWiggleIsAShake()
    {
        Assert.True(Wiggle(new ShakeDetector(), strokes: ShakeDetector.ReversalsForShake + 1));
    }

    [Fact]
    public void VerticalWiggleIsAShakeToo()
    {
        var detector = new ShakeDetector();
        var fired = false;
        var y = 400;
        var at = TimeSpan.Zero;
        var heading = 1;
        detector.Feed(500, y, at);
        for (var i = 0; i < ShakeDetector.ReversalsForShake + 1; i++)
        {
            for (var s = 0; s < PerStroke; s++)
            {
                y += heading * Stroke / PerStroke;
                at += TimeSpan.FromMilliseconds(60.0 / PerStroke);
                fired |= detector.Feed(500, y, at);
            }
            heading = -heading;
        }
        Assert.True(fired);
    }

    [Fact]
    public void TooFewReversalsIsNotAShake()
    {
        Assert.False(Wiggle(new ShakeDetector(), strokes: ShakeDetector.ReversalsForShake - 1));
    }

    [Fact]
    public void SlowBackAndForthIsNotAShake()
    {
        // Deliberate dragging: each stroke is long enough, but none is a flick, so the
        // count never builds.
        var gap = (int)ShakeDetector.MaxStrokeDuration.TotalMilliseconds + 50;
        Assert.False(Wiggle(new ShakeDetector(), strokes: 12, gapMs: gap));
    }

    [Fact]
    public void AmbleWithinTheStrokeLimitIsNotAShake()
    {
        // Long strokes, inside the time limit and never pausing, but ambled rather than
        // thrown: too slow to be someone hunting the pointer.
        var gap = (int)ShakeDetector.MaxStrokeDuration.TotalMilliseconds - 50;
        var travel = (int)(ShakeDetector.MinStrokeSpeed * gap / 1000 * 0.6); // 60% of flick speed
        Assert.True(travel > ShakeDetector.MinStrokeTravel); // rejected on speed, not size
        Assert.False(Wiggle(new ShakeDetector(), strokes: 12, travel: travel, gapMs: gap));
    }

    [Fact]
    public void GoingSomewhereAndComingBackIsNotAShake()
    {
        // A fast there-and-back is as quick and as wide as a shake. What gives it away is
        // the pause on arrival, which a wiggle never has.
        var detector = new ShakeDetector();
        var fired = false;
        var x = 400.0;
        var at = TimeSpan.Zero;
        var heading = 1;
        detector.Feed((int)x, 400, at);
        for (var hop = 0; hop < 8; hop++)
        {
            for (var s = 0; s < PerStroke; s++) // 400px in 120ms — a flick by speed alone
            {
                x += heading * 400.0 / PerStroke;
                at += TimeSpan.FromMilliseconds(120.0 / PerStroke);
                fired |= detector.Feed((int)x, 400, at);
            }
            heading = -heading;
            at += TimeSpan.FromMilliseconds(90); // and then it stops, as arriving does
            fired |= detector.Feed((int)x, 400, at);
        }
        Assert.False(fired);
    }

    [Fact]
    public void MovingAcrossTheScreenWithCorrectionsIsNotAShake()
    {
        // Fast navigation that overshoots and comes back: the reversals are there and
        // they are quick, but the pointer ends up somewhere else, so it is not a shake.
        var detector = new ShakeDetector();
        var fired = false;
        var x = 200.0;
        var at = TimeSpan.Zero;
        detector.Feed((int)x, 400, at);
        for (var i = 0; i < 8; i++)
        {
            var leg = i % 2 == 0 ? 220.0 : -70.0; // net rightward drift, both legs fast
            for (var s = 0; s < PerStroke; s++)
            {
                x += leg / PerStroke;
                at += TimeSpan.FromMilliseconds(60.0 / PerStroke);
                fired |= detector.Feed((int)x, 400, at);
            }
        }
        Assert.False(fired);
    }

    [Fact]
    public void CirclingThePointerIsNotAShake()
    {
        // Circling swings both axes, so each one on its own looks like a wiggle. What
        // it never does is reverse: it keeps turning the same way.
        Assert.False(Trace(new ShakeDetector(), turns: 8, radius: 90, msPerTurn: 250));
    }

    [Fact]
    public void ATightFastCircleIsNotAShakeEither()
    {
        Assert.False(Trace(new ShakeDetector(), turns: 12, radius: 70, msPerTurn: 170));
    }

    /// <summary>Circle the pointer, sampled as a mouse reports. Returns whether a shake fired.</summary>
    private static bool Trace(ShakeDetector detector, int turns, double radius, double msPerTurn)
    {
        const double sampleMs = 8;
        var fired = false;
        for (var t = 0.0; t < turns * msPerTurn; t += sampleMs)
        {
            var angle = 2 * Math.PI * t / msPerTurn;
            fired |= detector.Feed(
                (int)Math.Round(800 + radius * Math.Cos(angle)),
                (int)Math.Round(500 + radius * Math.Sin(angle)),
                TimeSpan.FromMilliseconds(t));
        }
        return fired;
    }

    [Fact]
    public void AWarpDoesNotJoinTheStrokesAroundIt()
    {
        // Two half-chains either side of the pointer being thrown across the screen
        // (a monitor hop, a window snap) must not add up to a shake.
        var detector = new ShakeDetector();
        Assert.False(Wiggle(detector, strokes: ShakeDetector.ReversalsForShake - 1));
        _x += ShakeDetector.MaxSampleJump + 100;
        _at += TimeSpan.FromMilliseconds(60);
        Assert.False(detector.Feed(_x, 400, _at));
        Assert.False(Wiggle(detector, strokes: 2));
    }

    [Fact]
    public void SmallJitterIsNotAShake()
    {
        // Hand tremor while resting on the mouse: fast, but nowhere near far enough.
        Assert.False(Wiggle(new ShakeDetector(), strokes: 20, travel: ShakeDetector.MinStrokeTravel - 5));
    }

    [Fact]
    public void SteadyMovementIsNotAShake()
    {
        var detector = new ShakeDetector();
        var at = TimeSpan.Zero;
        for (var x = 0; x < 4000; x += 40)
        {
            at += TimeSpan.FromMilliseconds(8);
            Assert.False(detector.Feed(x, 400 + x / 8, at));
        }
    }

    [Fact]
    public void JitterWithinAStrokeDoesNotBreakIt()
    {
        // A stroke rarely arrives as one clean run: a pixel of contrary wobble mid-way
        // must not end it (and so discard it as too short).
        var detector = new ShakeDetector();
        var fired = false;
        var x = 500;
        var at = TimeSpan.Zero;
        detector.Feed(x, 400, at);
        for (var i = 0; i < ShakeDetector.ReversalsForShake + 1; i++)
        {
            var direction = i % 2 == 0 ? 1 : -1;
            foreach (var step in new[] { direction * 30, -direction * 2, direction * 40 })
            {
                x += step;
                at += TimeSpan.FromMilliseconds(15);
                fired |= detector.Feed(x, 400, at);
            }
        }
        Assert.True(fired);
    }

    [Fact]
    public void ContinuedShakingKeepsReporting()
    {
        var detector = new ShakeDetector();
        Assert.True(Wiggle(detector, strokes: ShakeDetector.ReversalsForShake + 1));
        // Same detector, still shaking: it must re-arm so the pointer stays enlarged.
        Assert.True(Wiggle(detector, strokes: ShakeDetector.ReversalsForShake + 1));
    }

    [Fact]
    public void ResetDropsThePartialChain()
    {
        var detector = new ShakeDetector();
        Assert.False(Wiggle(detector, strokes: ShakeDetector.ReversalsForShake - 1));
        detector.Reset();
        // Two more strokes would have completed the chain; after the reset they can't.
        Assert.False(Wiggle(detector, strokes: 2));
    }
}
