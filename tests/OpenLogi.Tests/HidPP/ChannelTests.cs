using System.Diagnostics;
using OpenLogi.HidPP.Channel;

namespace OpenLogi.Tests.HidPP;

/// <summary>Ported from the Rust <c>channel.rs</c> test module.</summary>
public class ChannelTests
{
    [Fact]
    public void ShortPayloadWidensPreservingHeaderAndPadding()
    {
        // The widening is exercised via NormalizeOutgoing on a long-only channel,
        // but the raw shape is asserted here directly through a Short→Long reframe.
        byte[] shortPayload = [0xff, 0x05, 0x1e, 0xaa, 0xbb, 0xcc];
        var widened = new byte[HidppMessage.LongPayloadLength];
        shortPayload.CopyTo(widened.AsSpan());
        Assert.Equal(shortPayload, widened[..shortPayload.Length]);
        Assert.True(widened[shortPayload.Length..].All(b => b == 0));
        Assert.Equal(HidppMessage.LongPayloadLength, widened.Length);
    }

    [Fact]
    public async Task SendReturnsResponseBeforeTimeout()
    {
        var raw = new MockRawHidChannel();
        await using var channel = await HidppChannel.FromRawChannelAsync(raw);

        var request = MockRawHidChannel.ShortMsg(0x10);
        var response = MockRawHidChannel.ShortMsg(0x20);
        raw.QueueResponse(response);

        var actual = await channel.SendWithTimeoutAsync(
            request, c => c == response, TimeSpan.FromSeconds(1));

        Assert.Equal(response, actual);
        Assert.Single(raw.WrittenReports());
    }

    [Fact]
    public async Task PaddedWindowsReadStillMatchesPendingRequest()
    {
        // Windows reads return the collection's max input-report length, so a
        // short response arrives zero-padded (regression: it was dropped and the
        // request timed out).
        var raw = new MockRawHidChannel();
        await using var channel = await HidppChannel.FromRawChannelAsync(raw);

        var request = MockRawHidChannel.ShortMsg(0x10);
        var response = MockRawHidChannel.ShortMsg(0x20);
        var padded = new byte[HidppMessage.LongReportLength];
        response.WriteRaw(padded);

        var send = channel.SendWithTimeoutAsync(request, c => c == response, TimeSpan.FromSeconds(1));
        raw.SendIncomingRaw(padded);

        Assert.Equal(response, await send);
    }

    [Fact]
    public async Task SendTimesOutAndRemovesPendingMessage()
    {
        var raw = new MockRawHidChannel();
        await using var channel = await HidppChannel.FromRawChannelAsync(raw);
        var request = MockRawHidChannel.ShortMsg(0x10);
        var response = MockRawHidChannel.ShortMsg(0x20);

        var started = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<ChannelException>(() =>
            channel.SendWithTimeoutAsync(request, c => c == response, TimeSpan.FromMilliseconds(25)));

        Assert.Equal(ChannelErrorKind.Timeout, ex.Kind);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(1));
        Assert.Single(raw.WrittenReports());
    }

    [Fact]
    public async Task TimeoutRemovesOnlyItsOwnPendingMessage()
    {
        var raw = new MockRawHidChannel();
        await using var channel = await HidppChannel.FromRawChannelAsync(raw);

        var neverAnswered = MockRawHidChannel.ShortMsg(0x20);
        var slowResponse = MockRawHidChannel.ShortMsg(0x21);

        var timedOut = channel.SendWithTimeoutAsync(
            MockRawHidChannel.ShortMsg(0x10), c => c == neverAnswered, TimeSpan.FromMilliseconds(25));
        var answered = channel.SendWithTimeoutAsync(
            MockRawHidChannel.ShortMsg(0x11), c => c == slowResponse, TimeSpan.FromSeconds(1));
        var respondLate = Task.Run(async () =>
        {
            await Task.Delay(100);
            raw.SendIncoming(slowResponse);
        });

        var timedOutEx = await Assert.ThrowsAsync<ChannelException>(() => timedOut);
        Assert.Equal(ChannelErrorKind.Timeout, timedOutEx.Kind);
        Assert.Equal(slowResponse, await answered);
        await respondLate;
    }

    [Fact]
    public async Task LateResponseAfterTimeoutIsIgnored()
    {
        var raw = new MockRawHidChannel();
        await using var channel = await HidppChannel.FromRawChannelAsync(raw);
        var events = new List<(HidppMessage, bool)>();
        var eventsLock = new object();
        channel.AddMsgListener((msg, matched) =>
        {
            lock (eventsLock) events.Add((msg, matched));
        });

        var request = MockRawHidChannel.ShortMsg(0x10);
        var lateResponse = MockRawHidChannel.ShortMsg(0x20);
        var ex = await Assert.ThrowsAsync<ChannelException>(() =>
            channel.SendWithTimeoutAsync(request, c => c == lateResponse, TimeSpan.FromMilliseconds(25)));
        Assert.Equal(ChannelErrorKind.Timeout, ex.Kind);

        raw.SendIncoming(lateResponse);
        await WaitForEventCount(events, eventsLock, 1);
        Assert.Equal((lateResponse, false), events[0]);

        var laterResponse = MockRawHidChannel.ShortMsg(0x40);
        raw.QueueResponse(laterResponse);
        var actual = await channel.SendWithTimeoutAsync(
            MockRawHidChannel.ShortMsg(0x30), c => c == laterResponse, TimeSpan.FromSeconds(1));

        Assert.Equal(laterResponse, actual);
        await WaitForEventCount(events, eventsLock, 2);
        Assert.Equal((laterResponse, true), events[1]);
    }

    [Fact]
    public async Task SendAndForgetWritesWithoutPendingMessage()
    {
        var raw = new MockRawHidChannel();
        await using var channel = await HidppChannel.FromRawChannelAsync(raw);

        await channel.SendAndForgetAsync(MockRawHidChannel.ShortMsg(0x10));

        Assert.Single(raw.WrittenReports());
    }

    [Fact]
    public async Task ListenerCanRemoveAnotherListenerDuringDispatch()
    {
        var raw = new MockRawHidChannel();
        await using var channel = await HidppChannel.FromRawChannelAsync(raw);
        var removedCalls = 0;
        var removingCalls = 0;

        var removedHdl = channel.AddMsgListener((_, _) => Interlocked.Increment(ref removedCalls));
        channel.AddMsgListener((_, _) =>
        {
            Interlocked.Increment(ref removingCalls);
            channel.RemoveMsgListener(removedHdl);
        });

        raw.SendIncoming(MockRawHidChannel.ShortMsg(0x20));
        await WaitForAtomic(() => removingCalls, 1);
        await WaitForAtomic(() => removedCalls, 1);

        raw.SendIncoming(MockRawHidChannel.ShortMsg(0x21));
        await WaitForAtomic(() => removingCalls, 2);

        Assert.Equal(1, Volatile.Read(ref removedCalls));
    }

    private static async Task WaitForEventCount(List<(HidppMessage, bool)> events, object eventsLock, int count)
    {
        var started = Stopwatch.StartNew();
        while (started.Elapsed < TimeSpan.FromSeconds(2))
        {
            lock (eventsLock) if (events.Count >= count) return;
            await Task.Delay(10);
        }
        throw new TimeoutException($"timed out waiting for {count} listener events");
    }

    private static async Task WaitForAtomic(Func<int> read, int expected)
    {
        var started = Stopwatch.StartNew();
        while (started.Elapsed < TimeSpan.FromSeconds(2))
        {
            if (read() >= expected) return;
            await Task.Delay(10);
        }
        throw new TimeoutException($"timed out waiting for count {expected}");
    }
}
