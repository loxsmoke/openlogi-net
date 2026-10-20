using OpenLogi.HidPP.Channel;

namespace OpenLogi.Tests.HidPP;

/// <summary>
/// Issue #19: Windows splits some receivers' HID++ into a short collection (col01)
/// and a long-only one (col02), and a reply arrives on the collection that owns its
/// report id. A request sent on one must accept its reply from the other.
/// </summary>
public class SplitReplyChannelTests
{
    private static HidppMessage LongMsg(byte marker)
    {
        var payload = new byte[HidppMessage.LongPayloadLength];
        payload[0] = 0xff;
        payload[1] = marker;
        return HidppMessage.Long(payload);
    }

    [Fact]
    public async Task ReplyOnSiblingCompletesSend()
    {
        var controlRaw = new MockRawHidChannel { Support = (true, false) };
        var deviceRaw = new MockRawHidChannel { Support = (false, true) };
        await using var control = await HidppChannel.FromRawChannelAsync(controlRaw);
        await using var device = await HidppChannel.FromRawChannelAsync(deviceRaw);

        var request = MockRawHidChannel.ShortMsg(0x10);
        var response = LongMsg(0x20);
        controlRaw.OnWrite = _ => { deviceRaw.SendIncoming(response); return null; };

        var actual = await control.SendWithTimeoutAsync(request, m => m == response, TimeSpan.FromSeconds(2), device);

        Assert.Equal(response, actual);
        Assert.Single(controlRaw.WrittenReports());
        Assert.Empty(deviceRaw.WrittenReports());
    }

    [Fact]
    public async Task ReplyOnSendingChannelStillCompletesSend()
    {
        // A short error reply (HID++ 1.0) comes back on the control collection itself.
        var controlRaw = new MockRawHidChannel { Support = (true, false) };
        var deviceRaw = new MockRawHidChannel { Support = (false, true) };
        await using var control = await HidppChannel.FromRawChannelAsync(controlRaw);
        await using var device = await HidppChannel.FromRawChannelAsync(deviceRaw);

        var response = MockRawHidChannel.ShortMsg(0x20);
        controlRaw.QueueResponse(response);

        var actual = await control.SendWithTimeoutAsync(
            MockRawHidChannel.ShortMsg(0x10), m => m == response, TimeSpan.FromSeconds(2), device);

        Assert.Equal(response, actual);
    }

    [Fact]
    public async Task SilenceOnBothTimesOutAndWithdrawsTheSiblingWait()
    {
        var controlRaw = new MockRawHidChannel { Support = (true, false) };
        var deviceRaw = new MockRawHidChannel { Support = (false, true) };
        await using var control = await HidppChannel.FromRawChannelAsync(controlRaw);
        await using var device = await HidppChannel.FromRawChannelAsync(deviceRaw);

        var late = LongMsg(0x20);
        var ex = await Assert.ThrowsAsync<ChannelException>(() => control.SendWithTimeoutAsync(
            MockRawHidChannel.ShortMsg(0x10), m => m == late, TimeSpan.FromMilliseconds(100), device));
        Assert.Equal(ChannelErrorKind.Timeout, ex.Kind);

        // The timed-out wait must not linger on the sibling and swallow a later report.
        var seen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var _ = device.AddMsgListenerGuarded((m, matched) => { if (m == late) seen.TrySetResult(matched); });
        deviceRaw.SendIncoming(late);
        Assert.False(await seen.Task.WaitAsync(TimeSpan.FromSeconds(2)));
    }
}
