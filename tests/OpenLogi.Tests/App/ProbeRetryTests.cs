using OpenLogi.App.ViewModels;
using OpenLogi.Core.DeviceInfo;

namespace OpenLogi.Tests.App;

/// <summary>
/// The re-scan schedule behind a half-answered probe: re-scan on a growing backoff
/// while any listed device is incomplete, and stop once every device probed fully or
/// the retries are spent.
/// </summary>
public class ProbeRetryTests
{
    private static PairedDevice Mouse(bool incomplete) => new()
    {
        Slot = 0xff, Kind = incomplete ? DeviceKind.Unknown : DeviceKind.Mouse, Online = true,
        Codename = incomplete ? null : "MX Anywhere 3S for Mac", ProbeIncomplete = incomplete,
    };

    private static PairedDevice Keyboard() => new() { Slot = 1, Kind = DeviceKind.Keyboard, Online = true, Codename = "G915" };

    [Fact]
    public void NoRetryWhenEveryProbeIsComplete() =>
        Assert.Null(MainWindowViewModel.ProbeRetryDelay(0, [Mouse(false), Keyboard()]));

    [Fact]
    public void NoRetryForAnEmptyGallery() =>
        Assert.Null(MainWindowViewModel.ProbeRetryDelay(0, []));

    [Fact]
    public void BacksOffThenGivesUp()
    {
        PairedDevice[] devices = [Keyboard(), Mouse(true)];
        var delays = Enumerable.Range(0, MainWindowViewModel.ProbeRetryDelays.Length + 1)
            .Select(n => MainWindowViewModel.ProbeRetryDelay(n, devices))
            .ToList();

        Assert.Equal(
            [TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(12), null],
            delays);
    }

    [Fact]
    public void BackoffOutlastsTheObservedReconnectDelay()
    {
        // The 2026-10-16 mouse still timed out ~6 s after its node reappeared; the
        // schedule must keep looking well past that.
        var total = MainWindowViewModel.ProbeRetryDelays.Aggregate(TimeSpan.Zero, (a, b) => a + b);
        Assert.True(total >= TimeSpan.FromSeconds(15), $"retries end after {total}");
    }
}
