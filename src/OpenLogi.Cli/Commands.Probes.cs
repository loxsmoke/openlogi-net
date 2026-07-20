using OpenLogi.Hid;
using OpenLogi.HidPP.Channel;
using OpenLogi.HidPP.Protocol;

/// <summary>Low-level probes: receiver pairing, hidden power-mode feature, raw feature calls.</summary>
static partial class Commands
{
    // Open the receiver's pairing lock (HID++ 1.0 register 0xB2) so a device that is
    // power-cycled while the lock is open performs a FRESH pairing — rewriting the
    // link keys and negotiation state on both ends. Used to heal a pairing record
    // suspected of driving the G915's ~1 s eQuad power-save. A device that merely
    // reconnects (no fresh pairing) leaves the lock open until its timeout — that
    // outcome is reported too, and is harmless.
    public static async Task PairAsync(byte timeoutSec)
    {
        foreach (var hid in OpenLogi.Hid.HidDiscovery.EnumerateHidppDevices())
        {
            if (!OpenLogi.HidPP.Receiver.Receivers.IsReceiverPid((ushort)hid.VendorID, (ushort)hid.ProductID)) continue;
            // Receiver registers ride the short report — skip the long-only sibling interface.
            if (OpenLogi.Hid.WindowsRawHidChannel.DetectSupport(hid) is not { SupportsShort: true }) continue;

            HidppChannel channel;
            try { channel = await HidppChannel.FromRawChannelAsync(OpenLogi.Hid.WindowsRawHidChannel.Open(hid)); }
            catch (Exception e) { Console.Error.WriteLine($"cannot open receiver: {e.Message}"); continue; }

            await using (channel)
            {
                if (OpenLogi.HidPP.Receiver.Receivers.Detect(channel) is not { } detected) continue;
                var receiver = detected switch
                {
                    OpenLogi.HidPP.Receiver.DetectedReceiver.Unifying u => u.Receiver,
                    OpenLogi.HidPP.Receiver.DetectedReceiver.Lightspeed l => l.Receiver,
                    _ => null, // Bolt pairs via passkey — out of scope here
                };
                if (receiver is null) continue;
                using var _ = receiver;

                var lockClosed = new TaskCompletionSource<byte>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var lockListener = channel.AddMsgListenerGuarded((raw, matched) =>
                {
                    if (matched) return;
                    var msg = OpenLogi.HidPP.Protocol.V10Message.FromHidpp(raw);
                    if (msg.Header.SubId != 0x4a) return; // pairing-lock status
                    var p = msg.ExtendPayload();
                    if ((p[0] & 0x01) != 0) Console.WriteLine($"{detected.Name}: pairing lock OPEN");
                    else lockClosed.TrySetResult(p[1]);
                });
                var connections = receiver.Listen();

                await channel.WriteRegisterAsync(0xff, 0xb2, [0x01, 0x00, timeoutSec]);
                Console.WriteLine($"{detected.Name} ({channel.VendorId:x4}:{channel.ProductId:x4}): pairing window open for {timeoutSec}s.");
                Console.WriteLine("  >>> Now power the keyboard OFF, wait 2s, power it ON (LIGHTSPEED mode selected). <<<");

                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSec + 10);
                while (DateTime.UtcNow < deadline)
                {
                    var remaining = deadline - DateTime.UtcNow;
                    var connTask = connections.ReadAsync(new CancellationTokenSource(remaining).Token).AsTask();
                    var done = await Task.WhenAny(connTask, lockClosed.Task, Task.Delay(remaining));
                    if (done == lockClosed.Task)
                    {
                        var err = await lockClosed.Task;
                        Console.WriteLine(err switch
                        {
                            0x00 => "  pairing lock closed: SUCCESS — fresh pairing written",
                            0x01 => "  pairing lock closed: timeout (device reconnected with its existing pairing, or never showed up)",
                            0x02 => "  pairing lock closed: device not supported by this receiver",
                            0x03 => "  pairing lock closed: too many devices",
                            0x06 => "  pairing lock closed: connection sequence timeout",
                            _ => $"  pairing lock closed: code 0x{err:x2}",
                        });
                        return;
                    }
                    if (done == connTask && connTask.IsCompletedSuccessfully)
                    {
                        var c = connTask.Result;
                        Console.WriteLine($"  slot {c.Index}: {(c.Online ? "online" : "offline")} ({c.Kind}, wpid {c.Wpid:x4}, encrypted {c.Encrypted}) — waiting for lock close…");
                    }
                }
                Console.WriteLine("  no lock-close notification before deadline — check `list` for the keyboard's state");
                return;
            }
        }
        Console.Error.WriteLine("no pairable receiver found");
    }

    // Bounded probe of the Logitech-internal Power Modes feature (0x1830), which
    // rejects all calls with LogitechInternal until hidden features are enabled via
    // 0x1E00. Per device: read the 0x1E00 gate, enable it (VOLATILE — resets on
    // device power-cycle), read 0x1830 fn0, then restore the gate. No 0x1830 setters
    // are called; the healthy mouse doubles as a baseline for the G915's values.
    public static async Task PowerProbeAsync()
    {
        foreach (var inv in await Scan.Async())
            foreach (var dev in inv.Paired)
            {
                var route = DeviceRoute.DeviceRouteFor(inv, dev.Slot);
                if (route is null) continue;
                var label = dev.Codename ?? dev.Kind.ToString();
                await using var session = await DeviceSession.OpenAsync(route);
                if (session is null) continue;

                async Task<string> Call(ushort feat, byte fn, params byte[] a)
                {
                    try
                    {
                        var r = await session.CallRawFeatureAsync(feat, fn, a);
                        return r is null ? "absent" : Convert.ToHexString(r);
                    }
                    catch (Exception e) { return e.Message; }
                }

                Console.WriteLine($"{label}:");
                Console.WriteLine($"  1e00 fn0 (gate state):   {await Call(0x1e00, 0)}");
                Console.WriteLine($"  1e00 fn1 (enable gate):  {await Call(0x1e00, 1, 0x01)}");
                Console.WriteLine($"  1830 fn0:                {await Call(0x1830, 0)}");
                Console.WriteLine($"  1e00 fn1 (restore gate): {await Call(0x1e00, 1, 0x00)}");
            }
    }

    // The calculated poke: unlock the 0x1E00 gate, read 0x1830 fn0, call fn1(mode)
    // (fn1 is setPowerMode if the get/set convention holds), read fn0 again to see
    // whether the value moved, restore the gate. Recovery from any weirdness = the
    // device's power switch (gated internal state is volatile).
    public static async Task PowerSetAsync(byte mode)
    {
        foreach (var inv in await Scan.Async())
            foreach (var dev in inv.Paired)
            {
                var route = DeviceRoute.DeviceRouteFor(inv, dev.Slot);
                if (route is null) continue;
                var label = dev.Codename ?? dev.Kind.ToString();
                await using var session = await DeviceSession.OpenAsync(route);
                if (session is null) continue;

                async Task<string> Call(ushort feat, byte fn, params byte[] a)
                {
                    try
                    {
                        var r = await session.CallRawFeatureAsync(feat, fn, a);
                        return r is null ? "absent" : Convert.ToHexString(r);
                    }
                    catch (Exception e) { return e.Message; }
                }

                Console.WriteLine($"{label}:");
                Console.WriteLine($"  1e00 fn1 (enable gate):  {await Call(0x1e00, 1, 0x01)}");
                Console.WriteLine($"  1830 fn0 (before):       {await Call(0x1830, 0)}");
                Console.WriteLine($"  1830 fn1({mode:x2}) (set):     {await Call(0x1830, 1, mode)}");
                Console.WriteLine($"  1830 fn0 (after):        {await Call(0x1830, 0)}");
                Console.WriteLine($"  1e00 fn1 (restore gate): {await Call(0x1e00, 1, 0x00)}");
            }
    }

    // Raw probe of an arbitrary HID++ 2.0 feature function on every device exposing it
    // (e.g. `rawfeat 1830 0` = function 0 of the undocumented Power Modes feature).
    // Function 0 is a getter by HID++ convention; pass args only when you know the layout.
    public static async Task RawFeatureAsync(ushort featureId, byte function, byte[] fnArgs)
    {
        foreach (var inv in await Scan.Async())
            foreach (var dev in inv.Paired)
            {
                var route = DeviceRoute.DeviceRouteFor(inv, dev.Slot);
                if (route is null) continue;
                var label = dev.Codename ?? dev.Kind.ToString();
                await using var session = await DeviceSession.OpenAsync(route);
                if (session is null) continue;
                try
                {
                    var resp = await session.CallRawFeatureAsync(featureId, function, fnArgs);
                    Console.WriteLine(resp is null
                        ? $"{label}: no feature 0x{featureId:x4}"
                        : $"{label}: 0x{featureId:x4} fn{function}({Convert.ToHexString(fnArgs)}) -> {Convert.ToHexString(resp)}");
                }
                catch (Exception e)
                {
                    Console.WriteLine($"{label}: 0x{featureId:x4} fn{function}: {e.Message}");
                }
            }
    }
}
