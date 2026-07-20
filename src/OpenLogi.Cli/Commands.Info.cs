using OpenLogi.Assets;
using OpenLogi.Core;
using OpenLogi.Core.DeviceInfo;
using OpenLogi.Hid;
using OpenLogi.HidPP.Channel;
using OpenLogi.HidPP.Device;

/// <summary>Read-only inspection commands: device lists, feature dumps, asset lookups.</summary>
static partial class Commands
{
    public static async Task ListAsync()
    {
        IReadOnlyList<DeviceInventory> inventories;
        try
        {
            inventories = await Scan.Async();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"enumeration failed: {e.Message}");
            return;
        }

        if (inventories.Count == 0)
        {
            Console.WriteLine("No Logitech HID++ receivers or devices found.");
            return;
        }

        foreach (var inv in inventories)
        {
            var uid = inv.Receiver.UniqueId is { } u ? $" [{u}]" : "";
            Console.WriteLine($"{inv.Receiver.Name} ({inv.Receiver.VendorId:x4}:{inv.Receiver.ProductId:x4}){uid}");
            if (inv.Paired.Count == 0)
            {
                Console.WriteLine("  (no paired devices)");
                continue;
            }
            foreach (var dev in inv.Paired)
            {
                var name = dev.Codename ?? dev.Kind.ToString();
                var state = dev.Online ? "online" : "offline";
                var battery = dev.Battery is { } b ? $", battery {b.Percentage}% ({b.Status})" : "";
                Console.WriteLine($"  slot {dev.Slot}: {name} [{dev.Kind}] — {state}{battery}");
            }
        }
    }

    public static async Task DiagAsync()
    {
        var devices = HidDiscovery.EnumerateHidppDevices();
        if (devices.Count == 0)
        {
            Console.WriteLine("No Logitech HID++ devices found.");
            return;
        }

        foreach (var hid in devices)
        {
            Console.WriteLine($"{hid.GetFriendlyName()} ({hid.VendorID:x4}:{hid.ProductID:x4})");
            HidppChannel channel;
            try { channel = await HidppChannel.FromRawChannelAsync(WindowsRawHidChannel.Open(hid)); }
            catch (Exception e) { Console.WriteLine($"  cannot open: {e.Message}"); continue; }

            await using (channel)
            {
                HidppDevice device;
                try { device = await HidppDevice.NewAsync(channel, DeviceRoute.DirectDeviceIndex); }
                catch { Console.WriteLine("  not a direct HID++2.0 device (likely a receiver)"); continue; }

                var features = await device.EnumerateFeaturesAsync();
                if (features is null) { Console.WriteLine("  FeatureSet unsupported"); continue; }
                Console.WriteLine($"  protocol {device.ProtocolVersion}, {features.Count} features:");
                foreach (var f in features)
                    Console.WriteLine($"    0x{f.Id:x4} v{f.Version}");
            }
        }
    }

    // Dump the 0x1b04 reprogrammable-controls table for each direct device, so we can
    // see which control IDs a mouse exposes and whether the gesture button (0x00c3)
    // is present and raw-XY capable (required for hold-and-swipe gestures).
    public static async Task ControlsAsync()
    {
        foreach (var hid in HidDiscovery.EnumerateHidppDevices())
        {
            Console.WriteLine($"{hid.GetFriendlyName()} ({hid.VendorID:x4}:{hid.ProductID:x4})");
            HidppChannel channel;
            try { channel = await HidppChannel.FromRawChannelAsync(WindowsRawHidChannel.Open(hid)); }
            catch (Exception e) { Console.WriteLine($"  cannot open: {e.Message}"); continue; }

            await using (channel)
            {
                HidppDevice device;
                try { device = await HidppDevice.NewAsync(channel, DeviceRoute.DirectDeviceIndex); }
                catch { Console.WriteLine("  not a direct HID++2.0 device (likely a receiver)"); continue; }

                if (await device.EnumerateFeaturesAsync() is null)
                {
                    Console.WriteLine("  FeatureSet unsupported");
                    continue;
                }

                if (device.GetFeature<OpenLogi.HidPP.Feature.ReprogControlsFeature>() is not { } rc)
                {
                    Console.WriteLine("  no 0x1b04 (reprogrammable controls) feature");
                    continue;
                }

                using (rc)
                {
                    var count = await rc.GetCountAsync();
                    Console.WriteLine($"  0x1b04: {count} controls");
                    for (byte i = 0; i < count; i++)
                    {
                        var info = await rc.GetCidInfoAsync(i);
                        var tags = new List<string>();
                        if (OpenLogi.HidPP.Feature.CidFlagsExtensions.IsDivertable(info.Flags)) tags.Add("divertable");
                        if (OpenLogi.HidPP.Feature.CidFlagsExtensions.SupportsRawXy(info.Flags)) tags.Add("raw-xy");
                        if (OpenLogi.HidPP.Feature.CidFlagsExtensions.IsVirtualControl(info.Flags)) tags.Add("virtual");
                        if (OpenLogi.HidPP.Feature.CidFlagsExtensions.IsMouse(info.Flags)) tags.Add("mouse");
                        var gesture = info.Cid.Value is 0x00c3 or 0x00d0 or 0x00d7 ? "  <-- gesture control" : "";
                        var report = await rc.GetCidReportingAsync(info.Cid);
                        var state = new List<string>();
                        if (report.Diverted) state.Add("diverted");
                        if (report.RawXy) state.Add("raw-xy-on");
                        if (report.Remap is { } r) state.Add($"remap->0x{r.Value:x4}");
                        var stateStr = state.Count > 0 ? $" {{{string.Join(", ", state)}}}" : "";
                        Console.WriteLine($"    cid 0x{info.Cid.Value:x4} task 0x{info.TaskId.Value:x4} " +
                            $"flags 0x{(ushort)info.Flags:x4} [{string.Join(", ", tags)}]{stateStr}{gesture}");
                    }
                }
            }
        }
    }

    public static async Task AssetsAsync()
    {
        var resolver = new AssetResolver(Paths.AssetCacheDir());
        var inventories = await Scan.Async();
        var any = false;
        foreach (var inv in inventories)
        {
            foreach (var dev in inv.Paired)
            {
                any = true;
                var configKey = dev.ModelInfo?.ConfigKey();
                var label = dev.Codename ?? configKey ?? dev.Kind.ToString();
                try
                {
                    var path = configKey is not null
                        ? await resolver.ResolveFrontRenderAsync(configKey, dev.Codename, dev.ModelInfo!.ExtendedModelId)
                        : null;
                    Console.WriteLine(path is not null
                        ? $"{label}: {path}"
                        : $"{label}: no asset found");
                }
                catch (Exception e)
                {
                    Console.WriteLine($"{label}: fetch failed — {e.Message}");
                }
            }
        }
        if (!any) Console.WriteLine("No devices to resolve assets for.");
    }

    public static async Task HostsAsync()
    {
        foreach (var inv in await Scan.Async())
        {
            foreach (var dev in inv.Paired)
            {
                var route = DeviceRoute.DeviceRouteFor(inv, dev.Slot);
                if (route is null) continue;
                var label = dev.Codename ?? dev.Kind.ToString();
                await using var session = await DeviceSession.OpenAsync(route);
                if (session is null) continue;
                var hosts = await session.ReadHostsAsync();
                if (hosts is not { } h) { Console.WriteLine($"{label}: no host-switching feature"); continue; }
                Console.WriteLine($"{label}: current host {h.CurrentHost + 1} of {h.HostCount} — clear/delete supported: {h.SupportsDelete}");
                foreach (var host in h.Hosts)
                {
                    var marker = host.IsCurrent ? "*" : " ";
                    var state = host.Paired ? (host.Name ?? "(paired, no name)") : "(empty)";
                    var bus = string.IsNullOrEmpty(host.BusType) ? "" : $" [{host.BusType}]";
                    Console.WriteLine($"  {marker} Host {host.Index + 1}: {state}{bus}");
                }
            }
        }
    }

    public static async Task KbInfoAsync()
    {
        foreach (var inv in await Scan.Async())
        {
            foreach (var dev in inv.Paired)
            {
                var route = DeviceRoute.DeviceRouteFor(inv, dev.Slot);
                if (route is null) continue;
                var label = dev.Codename ?? dev.Kind.ToString();
                await using var session = await DeviceSession.OpenAsync(route);
                if (session is null) continue;

                if (await session.ReadBrightnessAsync() is { } b)
                    Console.WriteLine($"{label}: brightness {b.Current}/{b.Info.MaxBrightness} (min {b.Info.MinBrightness}, steps {b.Info.Steps}, caps {b.Info.Capabilities})");
                if (await session.EnumerateEffectsAsync() is { } effects)
                {
                    Console.WriteLine($"{label}: {effects.Count} RGB effects (cluster 0):");
                    foreach (var (index, effectId) in effects)
                        Console.WriteLine($"    index {index}: effectId 0x{effectId:x4}");
                }
            }
        }
    }
}
