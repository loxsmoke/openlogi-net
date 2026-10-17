using System.Threading.Channels;
using OpenLogi.Hid;
using OpenLogi.HidPP.Feature;

namespace OpenLogi.Tests.Hid;

/// <summary>
/// The re-assert loop that keeps diverts alive now also carries the volatile sensor
/// settings (gh-18). These pin the two behaviours the DPI keepalive relies on: a
/// 0x1d4b reconnect broadcast triggers an immediate push (no 20 s wait), and a push
/// the device rejects (the callback returns false — never a throw, which would stop a
/// debugger every 20 s for a whole nap) does not end the loop — the next
/// broadcast/tick retries and succeeds.
/// </summary>
public class VolatileSettingsKeepaliveTests
{
    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
    }

    [Fact]
    public async Task ReconnectBroadcastPushesImmediately()
    {
        var wake = Channel.CreateUnbounded<WirelessDeviceStatusFeature.StatusBroadcast>();
        var pushes = 0;
        using var cts = new CancellationTokenSource();
        var loop = DeviceSession.ReassertDivertLoopAsync(
            wake.Reader, () => { Interlocked.Increment(ref pushes); return Task.FromResult(true); }, "test", cts.Token, rechecks: []);

        // Status 1 = reconnection, Request 1 = software reconfiguration needed.
        wake.Writer.TryWrite(new WirelessDeviceStatusFeature.StatusBroadcast(1, 1, 0));
        await WaitForAsync(() => Volatile.Read(ref pushes) >= 1, TimeSpan.FromSeconds(5));
        Assert.Equal(1, Volatile.Read(ref pushes));

        cts.Cancel();
        await loop;
    }

    [Fact]
    public async Task RejectedPushIsRetriedOnTheNextBroadcast()
    {
        var wake = Channel.CreateUnbounded<WirelessDeviceStatusFeature.StatusBroadcast>();
        var attempts = 0;
        var succeeded = 0;
        using var cts = new CancellationTokenSource();
        var loop = DeviceSession.ReassertDivertLoopAsync(wake.Reader, () =>
        {
            // First attempt: the napping mouse rejects the write. Second: it lands.
            if (Interlocked.Increment(ref attempts) == 1) return Task.FromResult(false);
            Interlocked.Increment(ref succeeded);
            return Task.FromResult(true);
        }, "test", cts.Token, rechecks: []);

        wake.Writer.TryWrite(new WirelessDeviceStatusFeature.StatusBroadcast(1, 1, 0));
        await WaitForAsync(() => Volatile.Read(ref attempts) >= 1, TimeSpan.FromSeconds(5));
        Assert.Equal(0, Volatile.Read(ref succeeded));

        wake.Writer.TryWrite(new WirelessDeviceStatusFeature.StatusBroadcast(1, 1, 0));
        await WaitForAsync(() => Volatile.Read(ref succeeded) >= 1, TimeSpan.FromSeconds(5));
        Assert.Equal(2, Volatile.Read(ref attempts));
        Assert.Equal(1, Volatile.Read(ref succeeded));

        cts.Cancel();
        await loop;
    }

    [Fact]
    public async Task RechecksRunFirstThenTheSteadyCadence()
    {
        // No broadcast source: only the schedule drives rounds. Two quick re-checks,
        // then the next round is the 20 s keepalive — so the count stops at two.
        var pushes = 0;
        using var cts = new CancellationTokenSource();
        var loop = DeviceSession.ReassertDivertLoopAsync(null,
            () => { Interlocked.Increment(ref pushes); return Task.FromResult(true); }, "test", cts.Token,
            rechecks: [TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50)]);

        await WaitForAsync(() => Volatile.Read(ref pushes) >= 2, TimeSpan.FromSeconds(5));
        await Task.Delay(300);
        Assert.Equal(2, Volatile.Read(ref pushes));

        cts.Cancel();
        await loop;
    }

    [Fact]
    public void PostArmRechecksCoverTheReconnectWindowBeforeTheKeepalive()
    {
        // Gestures came back 10-20 s after a power cycle on the 20 s cadence alone;
        // the re-checks must land well inside that window.
        var schedule = DeviceSession.PostArmRechecks;
        Assert.True(schedule[0] <= TimeSpan.FromSeconds(2));
        Assert.True(schedule.Aggregate(TimeSpan.Zero, (a, b) => a + b) >= TimeSpan.FromSeconds(10));
    }
}

/// <summary>
/// The keepalive loops wait for a reconnect broadcast with a timeout. If that
/// timeout is a cancellation, every quiet round throws a first-chance
/// OperationCanceledException, and the debugger reports a burst of them each time
/// a mouse is armed (startup, power-on) once the post-arm re-checks run every few
/// seconds. A quiet round must not throw.
/// </summary>
public class KeepaliveFirstChanceTests
{
    [Fact]
    public async Task QuietRoundsThrowNothing()
    {
        var wake = Channel.CreateUnbounded<WirelessDeviceStatusFeature.StatusBroadcast>();
        var rounds = 0;
        var thrown = new List<string>();
        void OnFirstChance(object? s, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
        {
            lock (thrown) thrown.Add($"{e.Exception.GetType().Name}: {e.Exception.Message}");
        }

        using var cts = new CancellationTokenSource();
        AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;
        try
        {
            var loop = DeviceSession.ReassertDivertLoopAsync(wake.Reader,
                () => { Interlocked.Increment(ref rounds); return Task.FromResult(true); }, "test", cts.Token,
                rechecks: [TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(30)]);

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (Volatile.Read(ref rounds) < 3 && DateTime.UtcNow < deadline) await Task.Delay(10);
            Assert.Equal(3, Volatile.Read(ref rounds));
            lock (thrown) Assert.True(thrown.Count == 0, string.Join("; ", thrown));

            cts.Cancel();
            await loop;
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= OnFirstChance;
        }
    }
}
