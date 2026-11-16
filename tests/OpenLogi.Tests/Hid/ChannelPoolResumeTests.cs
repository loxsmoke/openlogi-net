using OpenLogi.Hid;
using OpenLogi.HidPP.Channel;
using OpenLogi.HidPP.Protocol;
using OpenLogi.Tests.HidPP;

namespace OpenLogi.Tests.Hid;

/// <summary>
/// Sleep/hibernation kills every HID handle that was open, and for months each
/// long-lived handle owner (mouse sessions, the page session, the lighting keepalive,
/// the receiver watcher) needed its own "drop the stale one" fix — a forgotten owner
/// left a read loop failing once a second forever. All of them now lease their channel
/// from <see cref="HidChannelPool"/>, and these tests pin the invariant that makes the
/// bug class impossible: after the handles go stale, no stale handle stays open, every
/// holder is told, and a reopen gets a fresh handle. Driven by a scripted HID world —
/// no hardware, no real sleep.
/// </summary>
public class ChannelPoolResumeTests
{
    private const ushort LightspeedPid = 0xc547; // G915 dongle
    private const ushort MousePid = 0xb034;      // a direct (Bluetooth) mouse

    // Short enough for a test, long enough that a healthy channel never trips it.
    private static readonly TimeSpan DeadThreshold = TimeSpan.FromMilliseconds(100);

    /// <summary>A HID node that hands out a new scripted handle on every open and remembers them all.</summary>
    private sealed class FakeNode(
        string path, ushort pid, Func<MockRawHidChannel, Func<HidppMessage, HidppMessage?>> script) : IHidNode
    {
        private readonly List<MockRawHidChannel> _handles = [];

        public ushort VendorId => 0x046d;
        public ushort ProductId => pid;
        public string DevicePath => path;
        public bool SupportsShort => true;
        public bool SupportsLong => true;

        /// <summary>Every handle ever opened on this node, oldest first.</summary>
        public IReadOnlyList<MockRawHidChannel> Handles
        {
            get { lock (_handles) return [.. _handles]; }
        }

        public IRawHidChannel Open()
        {
            var raw = new MockRawHidChannel { VendorId = 0x046d, ProductId = pid, Support = (true, true) };
            raw.OnWrite = script(raw);
            lock (_handles) _handles.Add(raw);
            return raw;
        }
    }

    /// <summary>The machine: its nodes, and the one thing sleep does to them.</summary>
    private sealed class FakeHidWorld(params FakeNode[] nodes) : IHidNodeSource
    {
        private readonly HashSet<MockRawHidChannel> _stale = [];

        public IReadOnlyList<IHidNode> Enumerate() => nodes;

        /// <summary>
        /// Sleep + resume: every handle open right now fails all reads from here on,
        /// instantly, the way a stale Windows HID handle does. Handles opened
        /// afterwards work.
        /// </summary>
        public void Hibernate()
        {
            foreach (var node in nodes)
                foreach (var raw in node.Handles.Where(h => !h.Disposed))
                {
                    lock (_stale) _stale.Add(raw);
                    raw.FailReads(new IOException("stale handle"));
                }
        }

        /// <summary>Stale handles somebody is still holding open — the leak this suite exists to prevent.</summary>
        public int StaleHandlesStillOpen()
        {
            lock (_stale) return _stale.Count(h => !h.Disposed);
        }
    }

    /// <summary>A HID++ 2.0 device answering at <paramref name="index"/>: ping, and "no FeatureSet" for everything else.</summary>
    private static HidppMessage? AnswerAsDevice(HidppMessage req, byte index)
    {
        var v20 = V20Message.FromHidpp(req);
        var h = v20.Header;
        if (h.DeviceIndex != index || h.FeatureIndex != 0x00) return null;
        if (h.FunctionId.ToLo() == 0x1) // root ping
        {
            var p = new byte[16];
            p[0] = 0x04; // protocol 4 → HID++ 2.0
            p[2] = v20.ExtendPayload()[2];
            return V20Message.Long(h, p).ToHidpp();
        }
        return V20Message.Long(h, new byte[16]).ToHidpp(); // root.get_feature → absent
    }

    private static FakeNode DirectMouse() => new(
        @"\\?\hid#vid_046d&pid_b034&col01#mouse", MousePid,
        _ => req => AnswerAsDevice(req, DeviceRoute.DirectDeviceIndex));

    /// <summary>A single-interface LIGHTSPEED receiver with one device on slot 1.</summary>
    private static FakeNode LightspeedReceiver() => new(
        @"\\?\hid#vid_046d&pid_c547&mi_02&col01#rx", LightspeedPid,
        _ => req =>
        {
            var v10 = V10Message.FromHidpp(req);
            if (v10.Header.DeviceIndex == 0xff && v10.Header.SubId == (byte)V10MessageType.SetRegister)
                return HidppMessage.Short([0xff, (byte)V10MessageType.SetRegister, v10.ExtendPayload()[0], 0, 0, 0]); // ACK
            return AnswerAsDevice(req, 1);
        });

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"timed out waiting for: {what}");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task UsersOfOneNodeShareOneHandleUntilTheLastLeaves()
    {
        var node = DirectMouse();
        var pool = new HidChannelPool(new FakeHidWorld(node));

        var first = await pool.AcquireAsync(node);
        var second = await pool.AcquireAsync(node);

        Assert.Same(first.Channel, second.Channel);
        var handle = Assert.Single(node.Handles);

        await first.DisposeAsync();
        Assert.False(handle.Disposed); // still in use by the second holder

        await second.DisposeAsync();
        Assert.True(handle.Disposed);
        Assert.Empty(pool.Snapshot());
    }

    [Fact]
    public async Task InvalidatingThePoolClosesEveryHandleAndTellsEveryHolder()
    {
        var mouse = DirectMouse();
        var receiver = LightspeedReceiver();
        var world = new FakeHidWorld(mouse, receiver);
        var pool = new HidChannelPool(world);
        var faults = 0;
        var mouseLease = await pool.AcquireAsync(mouse);
        var receiverLeaseA = await pool.AcquireAsync(receiver);
        var receiverLeaseB = await pool.AcquireAsync(receiver);
        foreach (var lease in new[] { mouseLease, receiverLeaseA, receiverLeaseB })
            lease.Faulted += () => Interlocked.Increment(ref faults);

        world.Hibernate();
        await pool.InvalidateAllAsync();

        Assert.Equal(3, faults);
        Assert.Equal(0, world.StaleHandlesStillOpen());
        Assert.All([mouseLease, receiverLeaseA, receiverLeaseB], l => Assert.True(l.IsFaulted));

        // A reopen gets a new, working handle — not the dead one.
        await using var fresh = await pool.AcquireAsync(mouse);
        Assert.Equal(2, mouse.Handles.Count);
        Assert.NotSame(mouseLease.Channel, fresh.Channel);
        Assert.False(mouse.Handles[1].Disposed);
    }

    [Fact]
    public async Task StaleHandleIsClosedEvenWhenNoResumeSignalArrives()
    {
        // Not every resume raises a power event, and not every stale handle comes
        // from a resume. The pool must clean up on the read failures alone.
        var node = DirectMouse();
        var world = new FakeHidWorld(node);
        var pool = new HidChannelPool(world, DeadThreshold);
        var lease = await pool.AcquireAsync(node);
        var faulted = false;
        lease.Faulted += () => Volatile.Write(ref faulted, true);

        world.Hibernate();

        await WaitForAsync(() => Volatile.Read(ref faulted), "the holder to be told its handle died");
        Assert.Equal(0, world.StaleHandlesStillOpen());
        Assert.Empty(pool.Snapshot());

        // And the failing read loop is gone, not merely backing off.
        var attempts = node.Handles[0].ReadAttempts;
        await Task.Delay(HidppChannel.MaxReadFailureBackoff + TimeSpan.FromMilliseconds(300));
        Assert.Equal(attempts, node.Handles[0].ReadAttempts);
    }

    [Fact]
    public async Task SessionLearnsItsHandleDiedAndReopensOnAFreshOne()
    {
        var node = DirectMouse();
        var world = new FakeHidWorld(node);
        var pool = new HidChannelPool(world, DeadThreshold);
        var route = new DeviceRoute.Direct(0x046d, MousePid);
        var session = await DeviceSession.OpenAsync(route, pool);
        Assert.NotNull(session);
        var faulted = false;
        session.Faulted += () => Volatile.Write(ref faulted, true);

        world.Hibernate();

        await WaitForAsync(() => Volatile.Read(ref faulted), "the session to fault");
        Assert.Equal(0, world.StaleHandlesStillOpen());
        await session.DisposeAsync(); // what an owner does on Faulted; harmless after the pool closed it

        await using var reopened = await DeviceSession.OpenAsync(route, pool);
        Assert.NotNull(reopened);
        Assert.Equal(2, node.Handles.Count);
        Assert.False(node.Handles[1].Disposed);
    }

    [Fact]
    public async Task SessionThroughAReceiverSharesTheWatchersHandle()
    {
        var receiver = LightspeedReceiver();
        var pool = new HidChannelPool(new FakeHidWorld(receiver));
        await using var watcher = new ReceiverConnectionWatcher(pool: pool);
        await watcher.RefreshAsync();
        Assert.Single(receiver.Handles);

        var session = await DeviceSession.OpenAsync(new DeviceRoute.Lightspeed("ls-046dc547", 1), pool);

        Assert.NotNull(session);
        var handle = Assert.Single(receiver.Handles); // no second handle on the receiver node
        await session.DisposeAsync();
        Assert.False(handle.Disposed); // the watcher still needs it
    }

    [Fact]
    public async Task ReceiverWatcherRebindsItselfAfterItsHandleGoesStale()
    {
        // The watcher was the owner every resume fix forgot: nothing outside it
        // refreshed it unless a HID node happened to re-enumerate.
        var receiver = LightspeedReceiver();
        var world = new FakeHidWorld(receiver);
        var pool = new HidChannelPool(world, DeadThreshold);
        await using var watcher = new ReceiverConnectionWatcher(pool: pool);
        await watcher.RefreshAsync();
        Assert.Single(receiver.Handles);

        world.Hibernate();

        await WaitForAsync(() => receiver.Handles.Count == 2, "the watcher to bind a fresh handle");
        Assert.Equal(0, world.StaleHandlesStillOpen());
        Assert.False(receiver.Handles[1].Disposed);
    }

    [Fact]
    public async Task WatcherAndSessionBothRecoverFromOneResume()
    {
        var receiver = LightspeedReceiver();
        var mouse = DirectMouse();
        var world = new FakeHidWorld(receiver, mouse);
        var pool = new HidChannelPool(world);
        await using var watcher = new ReceiverConnectionWatcher(pool: pool);
        await watcher.RefreshAsync();
        var keyboard = await DeviceSession.OpenAsync(new DeviceRoute.Lightspeed("ls-046dc547", 1), pool);
        var pointer = await DeviceSession.OpenAsync(new DeviceRoute.Direct(0x046d, MousePid), pool);
        Assert.NotNull(keyboard);
        Assert.NotNull(pointer);
        var faults = 0;
        keyboard.Faulted += () => Interlocked.Increment(ref faults);
        pointer.Faulted += () => Interlocked.Increment(ref faults);

        // What the app's resume handler does: one call, no list of owners.
        world.Hibernate();
        await pool.InvalidateAllAsync();

        Assert.Equal(2, faults);
        Assert.Equal(0, world.StaleHandlesStillOpen());
        await WaitForAsync(() => receiver.Handles.Count == 2, "the watcher to rebind");
        await keyboard.DisposeAsync();
        await pointer.DisposeAsync();
        await using var keyboardAgain = await DeviceSession.OpenAsync(new DeviceRoute.Lightspeed("ls-046dc547", 1), pool);
        Assert.NotNull(keyboardAgain);
        Assert.Equal(2, receiver.Handles.Count); // shares the watcher's fresh handle
    }
}
