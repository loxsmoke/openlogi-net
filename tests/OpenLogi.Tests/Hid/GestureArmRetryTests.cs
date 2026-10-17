using OpenLogi.Core.Config;
using OpenLogi.Hid;
using OpenLogi.HidPP.Channel;
using OpenLogi.HidPP.Device;
using OpenLogi.HidPP.Protocol;
using OpenLogi.Tests.HidPP;

namespace OpenLogi.Tests.Hid;

/// <summary>
/// Gesture captures are armed seconds after a BLE mouse reconnects, when single
/// HID++ writes still time out. On 2026-10-16 one failed divert write left the MX
/// Anywhere 3S gesture button (DpiToggle, cid 0x00c4) native until the next
/// reconnect, because the capture was dropped and nothing retried it. These drive
/// the real arming path over a scripted mouse.
/// </summary>
public class GestureArmRetryTests
{
    private const byte FeatureSetIndex = 1, ReprogIndex = 2;
    private const ushort GestureCid = 0x00c4;

    private sealed class Mouse
    {
        public int CountCalls, DivertCalls, ReadCalls;
        public volatile bool Diverted; // the device's current divert state for the control
        public int FailCounts;   // how many GetCount calls answer Busy
        public int FailDiverts;  // how many divert writes answer Busy
        public bool RawXy = true;
    }

    private static MockRawHidChannel Script(Mouse m)
    {
        var raw = new MockRawHidChannel { VendorId = 0x046d, ProductId = 0xb037, Support = (true, true) };
        raw.OnWrite = req =>
        {
            var msg = V20Message.FromHidpp(req);
            var h = msg.Header;
            if (h.DeviceIndex != 0xff) return null;
            var fn = h.FunctionId.ToLo();
            HidppMessage Reply(params byte[] head)
            {
                var p = new byte[16];
                head.CopyTo(p.AsSpan());
                return V20Message.Long(h, p).ToHidpp();
            }

            if (h.FeatureIndex == 0x00 && fn == 0x1) return Reply(0x04, 0x00, msg.ExtendPayload()[2]); // ping → 2.0
            if (h.FeatureIndex == 0x00 && fn == 0x0) return Reply(FeatureSetIndex, 0x00, 0x02);       // FeatureSet index
            if (h.FeatureIndex == FeatureSetIndex)
            {
                if (fn == 0x0) return Reply(0x02);
                var id = msg.ExtendPayload()[0] == 1 ? (ushort)0x0001 : (ushort)0x1b04;
                return Reply((byte)(id >> 8), (byte)id, 0x00, 0x04);
            }
            if (h.FeatureIndex == ReprogIndex)
            {
                switch (fn)
                {
                    case 0x0: // getCount
                        return Interlocked.Increment(ref m.CountCalls) <= m.FailCounts ? Busy(req) : Reply(0x01);
                    case 0x1: // getCidInfo(0): cid 0x00c4, divertable (+ raw-XY)
                        return Reply((byte)(GestureCid >> 8), (byte)GestureCid, 0x00, 0x00,
                            0x20, 0x00, 0x00, 0x00, (byte)(m.RawXy ? 0x01 : 0x00));
                    case 0x2: // getCidReporting: current divert + raw-XY state
                        Interlocked.Increment(ref m.ReadCalls);
                        return Reply((byte)(GestureCid >> 8), (byte)GestureCid, (byte)(m.Diverted ? 0x11 : 0x00));
                    case 0x3: // setCidReporting: apply, then echo the request
                        if (Interlocked.Increment(ref m.DivertCalls) <= m.FailDiverts) return Busy(req);
                        var flags = msg.ExtendPayload()[2];
                        if ((flags & 0x02) != 0) m.Diverted = (flags & 0x01) != 0;
                        return V20Message.Long(h, msg.ExtendPayload()[..16].ToArray()).ToHidpp();
                }
            }
            return null;
        };
        return raw;
    }

    /// <summary>A HID++ 2.0 Busy error echoing the request (the fast form of "not ready yet").</summary>
    private static HidppMessage Busy(HidppMessage req)
    {
        var v10 = V10Message.FromHidpp(req);
        var p = new byte[19];
        p[0] = v10.Header.DeviceIndex;
        p[1] = 0xff;
        p[2] = v10.Header.SubId;
        p[3] = v10.ExtendPayload()[0];
        p[4] = (byte)V20ErrorType.Busy;
        return HidppMessage.Long(p);
    }

    private static async Task<(DeviceSession Session, HidppChannel Channel)> OpenAsync(Mouse m)
    {
        var channel = await HidppChannel.FromRawChannelAsync(Script(m));
        var device = await HidppDevice.NewAsync(channel, 0xff);
        await device.EnumerateFeaturesAsync();
        return (DeviceSession.Attach(channel, device), channel);
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(20);
    }

    [Fact]
    public async Task HealthyArmDivertsOnce()
    {
        var m = new Mouse();
        var (session, channel) = await OpenAsync(m);
        await using var _ = channel;

        var capture = await session.StartGestureCaptureAsync(ButtonId.DpiToggle, _ => { });

        Assert.NotNull(capture);
        Assert.Equal(1, Volatile.Read(ref m.DivertCalls));
        await capture!.DisposeAsync();
    }

    [Fact]
    public async Task FailedDivertKeepsTheCaptureAndRetriesSoon()
    {
        var m = new Mouse { FailDiverts = 1 };
        var (session, channel) = await OpenAsync(m);
        await using var _ = channel;

        var capture = await session.StartGestureCaptureAsync(ButtonId.DpiToggle, _ => { });

        // Before the fix this was null, and the button stayed native until the next reconnect.
        Assert.NotNull(capture);
        Assert.Equal(1, Volatile.Read(ref m.DivertCalls));

        // The retry lands on the first post-arm re-check, well before the 20 s keepalive.
        await WaitForAsync(() => Volatile.Read(ref m.DivertCalls) >= 2,
            DeviceSession.PostArmRechecks[0] + TimeSpan.FromSeconds(3));
        Assert.True(Volatile.Read(ref m.DivertCalls) >= 2, "the failed divert was not retried");
        Assert.True(m.Diverted);
        await capture!.DisposeAsync();
    }

    [Fact]
    public async Task DivertDroppedByTheDeviceAfterArmingIsRestoredWithinSeconds()
    {
        var m = new Mouse();
        var (session, channel) = await OpenAsync(m);
        await using var _ = channel;

        var capture = await session.StartGestureCaptureAsync(ButtonId.DpiToggle, _ => { });
        Assert.NotNull(capture);
        Assert.True(m.Diverted);

        // The 2026-10-16 power-cycle case: the arm-time write lands, then the mouse
        // resets its volatile state as the link settles.
        m.Diverted = false;

        await WaitForAsync(() => m.Diverted, DeviceSession.PostArmRechecks[0] + TimeSpan.FromSeconds(3));
        Assert.True(m.Diverted, "the dropped divert was not restored by the first re-check");
        Assert.True(Volatile.Read(ref m.ReadCalls) >= 1); // state was read back (that's what the log line reports)
        await capture!.DisposeAsync();
    }

    [Fact]
    public async Task FailedLookupIsRetried()
    {
        var m = new Mouse { FailCounts = 1 };
        var (session, channel) = await OpenAsync(m);
        await using var _ = channel;

        var capture = await session.StartGestureCaptureAsync(ButtonId.DpiToggle, _ => { });

        Assert.NotNull(capture);
        Assert.Equal(2, Volatile.Read(ref m.CountCalls));
        Assert.Equal(1, Volatile.Read(ref m.DivertCalls));
        await capture!.DisposeAsync();
    }

    [Fact]
    public async Task ControlWithoutRawXyIsStillNotCaptured()
    {
        var m = new Mouse { RawXy = false };
        var (session, channel) = await OpenAsync(m);
        await using var _ = channel;

        var capture = await session.StartGestureCaptureAsync(ButtonId.DpiToggle, _ => { });

        Assert.Null(capture);
        Assert.Equal(0, Volatile.Read(ref m.DivertCalls));
    }
}
