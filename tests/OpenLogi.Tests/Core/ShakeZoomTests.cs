using OpenLogi.Core.Cursor;

namespace OpenLogi.Tests.Core;

/// <summary>
/// The arm/grow/hold/shrink animation behind shake-to-locate. The clock is supplied
/// by the test, so the easing is checked frame by frame without sleeping.
/// </summary>
public class ShakeZoomTests
{
    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(30);

    /// <summary>How often a wiggle that is kept up reports another shake.</summary>
    private static readonly TimeSpan Cadence = TimeSpan.FromMilliseconds(170);

    private TimeSpan _at;

    /// <summary>Run the animation forward, returning the scale reached.</summary>
    private double Run(ShakeZoom zoom, TimeSpan duration)
    {
        var end = _at + duration;
        while (_at < end)
        {
            _at += Frame;
            zoom.Advance(_at, Frame);
        }
        return zoom.Scale;
    }

    /// <summary>Keep wiggling for <paramref name="duration"/>, animating between reports.</summary>
    private double ShakeFor(ShakeZoom zoom, TimeSpan duration)
    {
        var end = _at + duration;
        while (_at < end)
        {
            zoom.Shake(_at);
            Run(zoom, Cadence);
        }
        return zoom.Scale;
    }

    [Fact]
    public void StartsAtTheUsersOwnSize()
    {
        var zoom = new ShakeZoom();
        Assert.Equal(ShakeZoom.MinScale, zoom.Scale);
        Assert.False(zoom.IsActive);
    }

    [Fact]
    public void ThePointerAnswersTheFirstShakeAtOnce()
    {
        // Reacting late feels broken, so the first shake does move the pointer — just
        // not far.
        var zoom = new ShakeZoom();
        zoom.Shake(_at);
        Run(zoom, ShakeZoom.GrowWindow / 2);
        Assert.True(zoom.Scale > ShakeZoom.MinScale);
    }

    [Fact]
    public void ALoneShakeBarelyGrowsThePointer()
    {
        // A stray detection buys a nudge and nothing more: it stops at the opening step,
        // which is a fraction of the way up, and stops climbing with the wiggle.
        var zoom = new ShakeZoom();
        zoom.Shake(_at);
        Run(zoom, ShakeZoom.GrowDuration + ShakeZoom.GrowWindow);
        Assert.True(zoom.Scale <= ShakeZoom.MinScale + ShakeZoom.FirstGrowth + 0.001);
        Assert.True(zoom.Scale < ShakeZoom.MinScale + ShakeZoom.GrowthPerShake);
    }

    [Fact]
    public void ShakesTooFarApartDoNotCompound()
    {
        // Two unrelated detections are two openings, not one sustained wiggle, so the
        // second is worth an opening step too rather than a full one.
        var zoom = new ShakeZoom();
        zoom.Shake(_at);
        Run(zoom, ShakeZoom.BurstWindow + Frame * 2);
        zoom.Shake(_at);
        Run(zoom, ShakeZoom.GrowDuration);
        Assert.True(zoom.Scale <= ShakeZoom.MinScale + ShakeZoom.FirstGrowth + 0.001);
    }

    [Fact]
    public void ASustainedWiggleGrowsThePointer()
    {
        var zoom = new ShakeZoom();
        var scale = ShakeFor(zoom, Cadence * 3);
        Assert.True(zoom.IsActive);
        // Well past what a lone detection could ever reach.
        Assert.True(scale > ShakeZoom.MinScale + ShakeZoom.FirstGrowth);
        Assert.True(scale <= ShakeZoom.MaxScale);
    }

    [Fact]
    public void GrowthIsGradualRatherThanAJump()
    {
        var zoom = new ShakeZoom();
        zoom.Shake(_at);
        zoom.Shake(_at);
        // The very next frame is on the way to the target, not already at it.
        _at += Frame;
        var first = zoom.Advance(_at, Frame);
        Assert.True(first > ShakeZoom.MinScale);
        Assert.True(first < ShakeZoom.MinScale + ShakeZoom.GrowthPerShake);
    }

    [Fact]
    public void ShakingOnKeepsGrowingUpToTheCap()
    {
        var zoom = new ShakeZoom();
        Assert.Equal(ShakeZoom.MaxScale, ShakeFor(zoom, TimeSpan.FromSeconds(2)), 3);
    }

    [Fact]
    public void GrowthStopsWhenTheWiggleDoes()
    {
        // Wiggle briefly and stop while the pointer is still climbing: it settles where
        // it reached rather than coasting on to the size the shaking was aiming for.
        var zoom = new ShakeZoom();
        zoom.Shake(_at);
        zoom.Shake(_at);
        var target = ShakeZoom.MinScale + ShakeZoom.FirstGrowth + ShakeZoom.GrowthPerShake;

        var settled = Run(zoom, ShakeZoom.GrowWindow + Frame);
        Assert.True(settled > ShakeZoom.MinScale);
        Assert.True(settled < target); // the climb was cut short with the wiggle

        // Still inside the hold, so nothing is shrinking yet — and nothing is growing.
        Run(zoom, ShakeZoom.Hold / 2);
        Assert.Equal(settled, zoom.Scale, 3);
    }

    [Fact]
    public void StaysBigWhileTheHoldLasts()
    {
        var zoom = new ShakeZoom();
        ShakeFor(zoom, Cadence * 4);
        // Growth carries on briefly past the last shake; the plateau is what follows it,
        // and it lasts until the hold — measured from that shake — runs out.
        var grown = Run(zoom, ShakeZoom.GrowWindow + Frame);
        Run(zoom, TimeSpan.FromMilliseconds(300));
        Assert.Equal(grown, zoom.Scale, 3);
    }

    [Fact]
    public void ShrinksSmoothlyOnceTheShakingStops()
    {
        var zoom = new ShakeZoom();
        Assert.Equal(ShakeZoom.MaxScale, ShakeFor(zoom, TimeSpan.FromSeconds(2)), 3);

        Run(zoom, ShakeZoom.Hold + Frame);
        // Part-way down: shrinking, but nowhere near done — that is what makes it smooth.
        var midway = Run(zoom, ShakeZoom.ShrinkDuration / 2);
        Assert.True(midway < ShakeZoom.MaxScale);
        Assert.True(midway > ShakeZoom.MinScale);

        Run(zoom, ShakeZoom.ShrinkDuration);
        Assert.Equal(ShakeZoom.MinScale, zoom.Scale, 3);
        Assert.False(zoom.IsActive); // the timer can stop
    }

    [Fact]
    public void ShakingAgainMidShrinkResumesTheClimb()
    {
        var zoom = new ShakeZoom();
        ShakeFor(zoom, Cadence * 3);
        Run(zoom, ShakeZoom.Hold + ShakeZoom.ShrinkDuration / 4);
        var shrinking = zoom.Scale;
        Assert.True(shrinking > ShakeZoom.MinScale);

        // Shaking again halts the retreat, but an opening shake never lifts the pointer
        // past its opening step — wherever the shrink had got to.
        zoom.Shake(_at);
        Run(zoom, ShakeZoom.GrowWindow);
        var opened = zoom.Scale;
        Assert.True(opened >= shrinking); // the retreat stopped
        Assert.True(opened <= Math.Max(shrinking, ShakeZoom.MinScale + ShakeZoom.FirstGrowth) + 0.001);

        // Keeping it up is what climbs past that.
        zoom.Shake(_at);
        Run(zoom, ShakeZoom.GrowWindow);
        Assert.True(zoom.Scale > opened);
    }

    [Fact]
    public void ResetDropsStraightBackToNormal()
    {
        var zoom = new ShakeZoom();
        ShakeFor(zoom, Cadence * 3);
        zoom.Reset();
        Assert.Equal(ShakeZoom.MinScale, zoom.Scale);
        Assert.False(zoom.IsActive);
    }
}
