using OpenLogi.App.Tray;

namespace OpenLogi.Tests.App;

/// <summary>
/// When a vanished device's tray icon may be hidden: never while suspended, not until
/// the grace after resume has passed, otherwise straight away.
/// </summary>
public class TrayPowerGraceTests
{
    private static readonly DateTime T0 = new(2026, 11, 5, 7, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void HidesFreelyBeforeAnyPowerEvent()
    {
        var grace = new TrayPowerGrace();
        Assert.False(grace.HoldsHides(T0));
    }

    [Fact]
    public void HoldsWhileSuspended()
    {
        var grace = new TrayPowerGrace();
        grace.OnSuspend();
        Assert.True(grace.HoldsHides(T0));
        Assert.True(grace.HoldsHides(T0.AddHours(8)));
    }

    [Fact]
    public void HoldsForTheGraceAfterResumeThenReleases()
    {
        var grace = new TrayPowerGrace();
        grace.OnSuspend();
        grace.OnResume(T0);

        Assert.True(grace.HoldsHides(T0));
        Assert.True(grace.HoldsHides(T0 + TrayPowerGrace.Duration - TimeSpan.FromMilliseconds(1)));

        Assert.False(grace.HoldsHides(T0 + TrayPowerGrace.Duration));
    }

    [Fact]
    public void ResumeWithoutSeenSuspendStillGrants()
    {
        // The Suspend broadcast can be missed (e.g. app started mid-transition).
        var grace = new TrayPowerGrace();
        grace.OnResume(T0);
        Assert.True(grace.HoldsHides(T0.AddSeconds(5)));
    }

    [Fact]
    public void SecondSuspendDuringGraceHoldsAgain()
    {
        var grace = new TrayPowerGrace();
        grace.OnResume(T0);
        grace.OnSuspend();
        Assert.True(grace.HoldsHides(T0 + TrayPowerGrace.Duration + TimeSpan.FromMinutes(5)));
    }
}
