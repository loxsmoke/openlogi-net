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
            wake.Reader, () => { Interlocked.Increment(ref pushes); return Task.FromResult(true); }, "test", cts.Token);

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
        }, "test", cts.Token);

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
}
