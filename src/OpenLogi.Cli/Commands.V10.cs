using OpenLogi.Hid;
using OpenLogi.HidPP.Channel;
using OpenLogi.HidPP.Protocol;
using OpenLogi.HidPP.Receiver;

/// <summary>HID++ 1.0 register dump for devices behind a receiver (issue #13 diagnostics).</summary>
static partial class Commands
{
    // Registers worth seeing on a HID++ 1.0 device. Names per Solaar's hidpp10 map.
    private static readonly (byte Address, string Name)[] V10Registers =
    [
        (0x00, "notifications"),
        (0x01, "mouse button flags (smooth/side scroll)"),
        (0x07, "battery status"),
        (0x0d, "battery charge"),
        (0x63, "mouse DPI"),
    ];

    /// <summary>
    /// For every receiver: ping each paired slot to pin its protocol version, then read
    /// the common HID++ 1.0 registers on it — first over the control interface, then
    /// (when the receiver has one) over the long-only sibling interface, since which
    /// collection a 1.0 device's replies land on has only been observed second-hand.
    /// Meant to be run by a reporter and pasted into an issue.
    /// </summary>
    public static async Task V10ProbeAsync()
    {
        var nodes = HidDiscovery.EnumerateHidppDevices().ToList();
        var any = false;
        foreach (var hid in nodes)
        {
            if (!Receivers.IsReceiverPid((ushort)hid.VendorID, (ushort)hid.ProductID)) continue;
            if (WindowsRawHidChannel.DetectSupport(hid) is not { SupportsShort: true }) continue;

            HidppChannel control;
            try { control = await HidppChannel.FromRawChannelAsync(WindowsRawHidChannel.Open(hid)); }
            catch (Exception e) { Console.Error.WriteLine($"cannot open receiver {hid.VendorID:x4}:{hid.ProductID:x4}: {e.Message}"); continue; }

            await using (control)
            {
                if (Receivers.Detect(control) is not { } detected) continue;
                any = true;
                Console.WriteLine($"{detected.Name} ({control.VendorId:x4}:{control.ProductId:x4})");

                List<byte> slots;
                switch (detected)
                {
                    case DetectedReceiver.Unifying u:
                        using (u.Receiver) slots = [.. (await u.Receiver.CollectPairedDevicesAsync()).Select(c => c.Index)];
                        break;
                    case DetectedReceiver.Lightspeed l:
                        using (l.Receiver) slots = [.. (await l.Receiver.CollectPairedDevicesAsync()).Select(c => c.Index)];
                        break;
                    case DetectedReceiver.Bolt b:
                        using (b.Receiver) slots = [.. (await b.Receiver.CollectPairedDevicesAsync()).Select(c => c.Index)];
                        break;
                    default:
                        continue;
                }
                if (slots.Count == 0) { Console.WriteLine("  (no paired devices announced)"); continue; }

                HidppChannel? sibling = null;
                foreach (var other in nodes)
                {
                    if (other.VendorID != hid.VendorID || other.ProductID != hid.ProductID) continue;
                    if (WindowsRawHidChannel.DetectSupport(other) is not { SupportsShort: false, SupportsLong: true }) continue;
                    try { sibling = await HidppChannel.FromRawChannelAsync(WindowsRawHidChannel.Open(other)); }
                    catch (Exception e) { Console.WriteLine($"  long-only sibling interface open failed: {e.Message}"); }
                    break;
                }

                try
                {
                    foreach (var slot in slots.Order())
                    {
                        await DumpSlotAsync(control, slot, "control interface");
                        if (sibling is not null) await DumpSlotAsync(sibling, slot, "long-only sibling interface");
                    }
                }
                finally
                {
                    if (sibling is not null) await sibling.DisposeAsync();
                }
            }
        }
        if (!any) Console.WriteLine("No Logitech receivers found.");
    }

    private static async Task DumpSlotAsync(HidppChannel channel, byte slot, string via)
    {
        Console.WriteLine($"  slot {slot} via {via}:");
        ProtocolVersion? version = null;
        try { version = await V20.DetermineVersionAsync(channel, slot, pingAttempts: 1); }
        catch (Exception e) { Console.WriteLine($"    ping failed: {e.Message}"); }
        Console.WriteLine(version switch
        {
            ProtocolVersion.V10 => "    protocol: HID++ 1.0 (answered the 2.0 ping with a 1.0 error)",
            ProtocolVersion.V20 v => $"    protocol: HID++ 2.0 (protocol {v.ProtocolNum}) — 1.0 registers below are expected to fail",
            _ => "    protocol: no answer to the ping",
        });
        if (version is null) return;

        foreach (var (address, name) in V10Registers)
        {
            string result;
            try
            {
                var r = await channel.ReadRegisterAsync(slot, address, [0, 0, 0]);
                result = Convert.ToHexString(r);
                if (address == V10Battery.RegBatteryStatus && V10Battery.ParseBatteryStatus(r) is { } bs)
                    result += $"  → {bs.Level}, {bs.Status}";
                if (address == V10Battery.RegBatteryCharge && V10Battery.ParseBatteryCharge(r) is { } bc)
                    result += $"  → {bc.ChargingPercentage}%, {bc.Status}";
            }
            catch (Hidpp10Exception e) when (e.RegisterError is { } err) { result = $"error {err}"; }
            catch (Exception e) { result = $"failed: {e.Message}"; }
            Console.WriteLine($"    reg 0x{address:x2} {name,-42} {result}");
        }
    }
}
