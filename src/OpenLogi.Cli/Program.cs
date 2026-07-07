using OpenLogi.Assets;
using OpenLogi.Core;
using OpenLogi.Core.DeviceInfo;
using OpenLogi.Hid;
using OpenLogi.HidPP.Channel;
using OpenLogi.HidPP.Device;

// Minimal CLI entry point (a richer System.CommandLine surface is a later polish).
var command = args.Length > 0 ? args[0] : "list";

switch (command)
{
    case "list":
        await ListAsync();
        break;
    case "diag":
        await DiagAsync();
        break;
    case "controls":
        await ControlsAsync();
        break;
    case "gestureprobe":
        await GestureProbeAsync(args.Length > 1 ? Convert.ToUInt16(args[1], 16) : (ushort)0x00c4);
        break;
    case "assets":
        await AssetsAsync();
        break;
    case "light":
        await LightAsync(args.Length > 1 ? args[1] : "ffffff");
        break;
    case "hosts":
        await HostsAsync();
        break;
    case "kbinfo":
        await KbInfoAsync();
        break;
    case "bright":
        await BrightAsync(args);
        break;
    case "kbmode":
        await KbModeAsync(args.Length > 1 ? args[1] : "onboard");
        break;
    case "profiles":
        await ProfilesAsync(args);
        break;
    case "dumpprofile":
        await DumpProfileAsync(args.Length > 1 && ushort.TryParse(args[1], out var sid) ? sid : (ushort)1);
        break;
    case "crccheck":
        await CrcCheckAsync(args.Length > 1 && ushort.TryParse(args[1], out var cs) ? cs : (ushort)1);
        break;
    case "writeprofile":
        await WriteProfileRoundTripAsync(args.Length > 1 && ushort.TryParse(args[1], out var ws) ? ws : (ushort)3);
        break;
    case "copyprofile":
        await CopyProfileAsync(ushort.Parse(args[1]), ushort.Parse(args[2]));
        break;
    case "patchprofile":
        await PatchProfileAsync(ushort.Parse(args[1]), Convert.ToInt32(args[2], 16),
            args[3..].Select(h => Convert.ToByte(h, 16)).ToArray());
        break;
    case "effect":
        await EffectAsync(args);
        break;
    case "perkey":
        await PerKeyAsync(args.Length > 1 ? args[1] : "ff0000");
        break;
    case "setkey":
        await SetKeyAsync(Convert.ToByte(args[1], 16), args.Length > 2 ? args[2] : "ff0000");
        break;
    case "zoneprobe":
        await ZoneProbeAsync(args.Length > 1 ? Convert.ToByte(args[1], 16) : (byte)0);
        break;
    case "anim":
        await AnimAsync(args.Length > 1 ? args[1] : "breathing", args.Length > 2 ? args[2] : "00ff00");
        break;
    default:
        Console.Error.WriteLine($"unknown command '{command}'. Available: list, diag, controls, assets, light <RRGGBB>, hosts, kbinfo, bright <0-100>, kbmode <onboard|host>, effect <idx> [params hex...]");
        return 1;
}

return 0;

static async Task ListAsync()
{
    IReadOnlyList<DeviceInventory> inventories;
    try
    {
        inventories = await HidInventory.EnumerateAsync();
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

static async Task DiagAsync()
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
static async Task ControlsAsync()
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

// Divert one control with raw-XY (the exact mechanism the gesture capture uses) and
// print its events for ~8 seconds so a gesture button + swipe can be confirmed live,
// then restore the control. Defaults to 0x00c4 (wheel/DPI button on the MX Anywhere 3S).
static async Task GestureProbeAsync(ushort cid)
{
    foreach (var hid in HidDiscovery.EnumerateHidppDevices())
    {
        HidppChannel channel;
        try { channel = await HidppChannel.FromRawChannelAsync(WindowsRawHidChannel.Open(hid)); }
        catch { continue; }

        await using (channel)
        {
            HidppDevice device;
            try { device = await HidppDevice.NewAsync(channel, DeviceRoute.DirectDeviceIndex); }
            catch { continue; }
            if (await device.EnumerateFeaturesAsync() is null) continue;
            if (device.GetFeature<OpenLogi.HidPP.Feature.ReprogControlsFeature>() is not { } rc) continue;

            using (rc)
            {
                Console.WriteLine($"{hid.GetFriendlyName()} ({hid.VendorID:x4}:{hid.ProductID:x4})");
                Console.WriteLine($"Diverting cid 0x{cid:x4} with raw-XY. HOLD that button and swipe now…");
                var control = new OpenLogi.HidPP.Feature.ControlId(cid);
                await rc.SetCidReportingAsync(control,
                    OpenLogi.HidPP.Feature.CidReportingChange.TemporaryDiversion(diverted: true, rawXy: true));

                var reader = rc.Listen();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                var events = 0;
                try
                {
                    await foreach (var ev in reader.ReadAllAsync(cts.Token))
                    {
                        events++;
                        switch (ev)
                        {
                            case OpenLogi.HidPP.Feature.ReprogControlsEvent.DivertedButtons db:
                                Console.WriteLine($"  buttons: [{string.Join(", ", db.Controls.Select(c => $"0x{c.Value:x4}"))}]");
                                break;
                            case OpenLogi.HidPP.Feature.ReprogControlsEvent.DivertedRawMouseXy xy:
                                Console.WriteLine($"  raw-xy: dx={xy.Dx} dy={xy.Dy}");
                                break;
                        }
                    }
                }
                catch (OperationCanceledException) { }

                await rc.SetCidReportingAsync(control,
                    OpenLogi.HidPP.Feature.CidReportingChange.TemporaryDiversion(diverted: false, rawXy: false));
                Console.WriteLine(events > 0
                    ? $"Restored. Captured {events} event(s) — gesture divert works on 0x{cid:x4}."
                    : "Restored. No events — was the button held/swiped during the window?");
            }
            return;
        }
    }
    Console.WriteLine("No direct HID++ device found.");
}

static async Task AssetsAsync()
{
    var resolver = new AssetResolver(Paths.AssetCacheDir());
    var inventories = await HidInventory.EnumerateAsync();
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

// Safest write test: read a sector and write it back UNCHANGED (CRC recomputed),
// then read again and confirm the body is byte-identical. Validates the write path
// with no functional change.
static async Task WriteProfileRoundTripAsync(ushort sectorId)
{
    foreach (var inv in await HidInventory.EnumerateAsync())
        foreach (var dev in inv.Paired)
        {
            var route = DeviceRoute.DeviceRouteFor(inv, dev.Slot);
            if (route is null) continue;
            await using var session = await DeviceSession.OpenAsync(route);
            if (session is null) continue;
            var before = await session.ReadFullSectorAsync(sectorId, 255);
            if (before is null) continue;
            Console.WriteLine($"{dev.Codename}: sector {sectorId} read {before.Length} bytes, writing back unchanged…");
            var ok = await session.WriteProfileSectorAsync(sectorId, (byte[])before.Clone());
            var after = await session.ReadFullSectorAsync(sectorId, 255);
            var identical = after is not null && before.AsSpan(0, 253).SequenceEqual(after.AsSpan(0, 253));
            Console.WriteLine($"  write={ok}, body identical after read-back={identical}");
        }
}

static async Task PatchProfileAsync(ushort sector, int offset, byte[] bytes)
{
    foreach (var inv in await HidInventory.EnumerateAsync())
        foreach (var dev in inv.Paired)
        {
            var route = DeviceRoute.DeviceRouteFor(inv, dev.Slot);
            if (route is null) continue;
            await using var session = await DeviceSession.OpenAsync(route);
            if (session is null) continue;
            var data = await session.ReadFullSectorAsync(sector, 255);
            if (data is null) continue;
            bytes.CopyTo(data, offset);
            var ok = await session.WriteProfileSectorAsync(sector, data);
            Console.WriteLine($"{dev.Codename}: patched sector {sector} @0x{offset:x} with {Convert.ToHexString(bytes)} → write={ok}");
        }
}

static async Task CopyProfileAsync(ushort src, ushort dst)
{
    foreach (var inv in await HidInventory.EnumerateAsync())
        foreach (var dev in inv.Paired)
        {
            var route = DeviceRoute.DeviceRouteFor(inv, dev.Slot);
            if (route is null) continue;
            await using var session = await DeviceSession.OpenAsync(route);
            if (session is null) continue;
            var data = await session.ReadFullSectorAsync(src, 255);
            if (data is null) continue;
            var ok = await session.WriteProfileSectorAsync(dst, (byte[])data.Clone());
            var back = await session.ReadFullSectorAsync(dst, 255);
            var same = back is not null && data.AsSpan(0, 253).SequenceEqual(back.AsSpan(0, 253));
            Console.WriteLine($"{dev.Codename}: copied sector {src} → {dst}: write={ok}, verified={same}");
        }
}

static async Task CrcCheckAsync(ushort sectorId)
{
    foreach (var inv in await HidInventory.EnumerateAsync())
        foreach (var dev in inv.Paired)
        {
            var route = DeviceRoute.DeviceRouteFor(inv, dev.Slot);
            if (route is null) continue;
            await using var session = await DeviceSession.OpenAsync(route);
            if (session is null) continue;
            var data = await session.ReadFullSectorAsync(sectorId, 255);
            if (data is null) continue;
            Console.WriteLine($"{dev.Codename}: sector {sectorId} last 6 bytes = {Convert.ToHexString(data.AsSpan(249).ToArray())}");
            var crc = OpenLogi.HidPP.Crc16.Ccitt(data.AsSpan(0, 253));
            var stored = (ushort)((data[253] << 8) | data[254]);
            Console.WriteLine($"  crc[0..253]={crc:x4}  stored[253..255]BE={stored:x4}  {(crc == stored ? "*** MATCH ***" : "MISMATCH")}");
        }
}

static async Task DumpProfileAsync(ushort sectorId)
{
    foreach (var inv in await HidInventory.EnumerateAsync())
        foreach (var dev in inv.Paired)
        {
            var route = DeviceRoute.DeviceRouteFor(inv, dev.Slot);
            if (route is null) continue;
            await using var session = await DeviceSession.OpenAsync(route);
            if (session is null) continue;
            var data = new byte[256];
            var got = 0;
            for (var off = 0; off < 256; off += 16)
            {
                try { Array.Copy(await session.ReadMemoryRawAsync(sectorId, (ushort)off) ?? new byte[16], 0, data, off, 16); got = off + 16; }
                catch (Exception ex) { Console.WriteLine($"  stop at offset {off}: {ex.Message}"); break; }
            }
            if (got == 0) continue;
            Console.WriteLine($"{dev.Codename}: sector {sectorId} ({got} bytes):");
            for (var row = 0; row < got; row += 16)
            {
                var hex = Convert.ToHexString(data.AsSpan(row, 16).ToArray());
                var ascii = new string(data.AsSpan(row, 16).ToArray().Select(b => b is >= 0x20 and < 0x7f ? (char)b : '.').ToArray());
                Console.WriteLine($"  {row:x3}: {hex}  {ascii}");
            }
            var utf16 = System.Text.Encoding.Unicode.GetString(data, 0, got).Replace('\0', '.');
            Console.WriteLine($"  utf16-le: '{utf16}'");
        }
}

static async Task ProfilesAsync(string[] args)
{
    byte? switchTo = args.Length > 1 && byte.TryParse(args[1], out var pi) ? pi : null;
    foreach (var inv in await HidInventory.EnumerateAsync())
        foreach (var dev in inv.Paired)
        {
            var route = DeviceRoute.DeviceRouteFor(inv, dev.Slot);
            if (route is null) continue;
            var label = dev.Codename ?? dev.Kind.ToString();
            await using var session = await DeviceSession.OpenAsync(route);
            if (session is null) continue;
            if (await session.ReadProfilesAsync() is not { } p) continue;
            Console.WriteLine($"{label}: {p.Info.ProfileCount} onboard profiles (current {p.Current}), " +
                $"{p.Info.ButtonCount} buttons, {p.Info.SectorCount} sectors x {p.Info.SectorSize}B, fmt {p.Info.ProfileFormat}");
            if (switchTo is { } target)
            {
                var ok = await session.SwitchProfileAsync(target);
                Console.WriteLine($"  switch to profile {target}: {(ok ? "ok" : "failed")}");
                await Task.Delay(6000);
            }
        }
}

static async Task KbModeAsync(string mode)
{
    var host = mode.Equals("host", StringComparison.OrdinalIgnoreCase);
    foreach (var inv in await HidInventory.EnumerateAsync())
    {
        foreach (var dev in inv.Paired)
        {
            var route = DeviceRoute.DeviceRouteFor(inv, dev.Slot);
            if (route is null) continue;
            var label = dev.Codename ?? dev.Kind.ToString();
            await using var session = await DeviceSession.OpenAsync(route);
            if (session is null) continue;
            var ok = await session.SetOnboardModeAsync(host);
            if (ok) Console.WriteLine($"{label}: set {(host ? "host" : "onboard")} mode");
        }
    }
}

static async Task BrightAsync(string[] args)
{
    var target = args.Length > 1 && ushort.TryParse(args[1], out var v) ? v : (ushort)100;
    foreach (var inv in await HidInventory.EnumerateAsync())
    {
        foreach (var dev in inv.Paired)
        {
            var route = DeviceRoute.DeviceRouteFor(inv, dev.Slot);
            if (route is null) continue;
            var label = dev.Codename ?? dev.Kind.ToString();
            await using var session = await DeviceSession.OpenAsync(route);
            if (session is null) continue;
            if (await session.ReadBrightnessAsync() is not { } before) continue;
            await session.ApplyBrightnessAsync(target);
            var after = await session.ReadBrightnessAsync();
            Console.WriteLine($"{label}: brightness {before.Current} → set {target} → read {after?.Current} (max {before.Info.MaxBrightness})");
        }
    }
}

static async Task KbInfoAsync()
{
    foreach (var inv in await HidInventory.EnumerateAsync())
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

static async Task AnimAsync(string mode, string hex)
{
    byte.TryParse(hex.AsSpan(0, 2), System.Globalization.NumberStyles.HexNumber, null, out var br);
    byte.TryParse(hex.AsSpan(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var bg);
    byte.TryParse(hex.AsSpan(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var bb);
    var cycle = mode.Equals("cycle", StringComparison.OrdinalIgnoreCase);

    (byte, byte, byte) Frame(double tsec)
    {
        if (cycle)
        {
            var h = tsec / 6.0 * 360.0 % 360.0;
            var x = 1 - Math.Abs(h / 60.0 % 2 - 1);
            var (r, g, b) = (h / 60) switch
            { < 1 => (1.0, x, 0.0), < 2 => (x, 1.0, 0.0), < 3 => (0.0, 1.0, x), < 4 => (0.0, x, 1.0), < 5 => (x, 0.0, 1.0), _ => (1.0, 0.0, x) };
            return ((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
        }
        var k = 0.08 + 0.92 * (0.5 + 0.5 * Math.Sin(2 * Math.PI * tsec / 3.0));
        return ((byte)(br * k), (byte)(bg * k), (byte)(bb * k));
    }

    foreach (var inv in await HidInventory.EnumerateAsync())
        foreach (var dev in inv.Paired)
        {
            if (dev.Capabilities?.Lighting != true) continue;
            var route = DeviceRoute.DeviceRouteFor(inv, dev.Slot);
            if (route is null) continue;
            await using var session = await DeviceSession.OpenAsync(route);
            if (session is null) continue;
            var (r0, g0, b0) = Frame(0);
            if (!await session.ApplyPerKeyColorAsync(r0, g0, b0)) { Console.WriteLine("no 0x8081"); continue; }
            Console.WriteLine($"{dev.Codename}: {mode} for 12s — watch the keyboard");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var frames = 0;
            while (sw.Elapsed.TotalSeconds < 12)
            {
                var (r, g, b) = Frame(sw.Elapsed.TotalSeconds);
                await session.ApplyPerKeyColorFastAsync(r, g, b);
                frames++;
                await Task.Delay(45);
            }
            Console.WriteLine($"  done ({frames} frames, {frames / 12.0:0.0} fps)");
        }
}

static async Task ZoneProbeAsync(byte start)
{
    // 8 distinct, namable colors for zones start..start+7.
    var colors = new (string Name, byte R, byte G, byte B)[]
    {
        ("RED", 255, 0, 0), ("GREEN", 0, 255, 0), ("BLUE", 0, 0, 255), ("YELLOW", 255, 255, 0),
        ("CYAN", 0, 255, 255), ("MAGENTA", 255, 0, 255), ("WHITE", 255, 255, 255), ("ORANGE", 255, 100, 0),
    };
    var map = new Dictionary<byte, (byte, byte, byte)>();
    for (byte i = 0; i < 8; i++) map[(byte)(start + i)] = (colors[i].R, colors[i].G, colors[i].B);

    foreach (var inv in await HidInventory.EnumerateAsync())
        foreach (var dev in inv.Paired)
        {
            if (dev.Capabilities?.Lighting != true) continue;
            var route = DeviceRoute.DeviceRouteFor(inv, dev.Slot);
            if (route is null) continue;
            await using var session = await DeviceSession.OpenAsync(route);
            if (session is null) continue;
            if (!await session.ApplyPerKeyMapAsync(map)) continue;
            for (byte i = 0; i < 8; i++) Console.WriteLine($"  zone 0x{start + i:x2} = {colors[i].Name}");
            Console.WriteLine("  (holding 12s)");
            await Task.Delay(12000);
        }
}

static async Task SetKeyAsync(byte zoneId, string hex)
{
    byte.TryParse(hex.AsSpan(0, 2), System.Globalization.NumberStyles.HexNumber, null, out var r);
    byte.TryParse(hex.AsSpan(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var g);
    byte.TryParse(hex.AsSpan(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var b);
    foreach (var inv in await HidInventory.EnumerateAsync())
        foreach (var dev in inv.Paired)
        {
            if (dev.Capabilities?.Lighting != true) continue;
            var route = DeviceRoute.DeviceRouteFor(inv, dev.Slot);
            if (route is null) continue;
            await using var session = await DeviceSession.OpenAsync(route);
            if (session is null) continue;
            var ok = await session.SetKeyZoneAsync(zoneId, r, g, b);
            Console.WriteLine($"{dev.Codename}: zone 0x{zoneId:x2} = #{hex} → {(ok ? "set (holding 8s)" : "no 0x8081")}");
            if (ok) await Task.Delay(8000);
        }
}

static async Task PerKeyAsync(string hex)
{
    if (hex.Length != 6
        || !byte.TryParse(hex.AsSpan(0, 2), System.Globalization.NumberStyles.HexNumber, null, out var r)
        || !byte.TryParse(hex.AsSpan(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var g)
        || !byte.TryParse(hex.AsSpan(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
    {
        Console.Error.WriteLine("usage: perkey <RRGGBB>");
        return;
    }
    foreach (var inv in await HidInventory.EnumerateAsync())
    {
        foreach (var dev in inv.Paired)
        {
            if (dev.Capabilities?.Lighting != true) continue;
            var route = DeviceRoute.DeviceRouteFor(inv, dev.Slot);
            if (route is null) continue;
            var label = dev.Codename ?? dev.Kind.ToString();
            await using var session = await DeviceSession.OpenAsync(route);
            if (session is null) continue;
            var ok = await session.ApplyPerKeyColorAsync(r, g, b);
            Console.WriteLine($"{label}: per-key #{hex} → {(ok ? "applied (holding 8s)" : "no 0x8081")}");
            if (ok) await Task.Delay(8000);
            Console.WriteLine("  released.");
        }
    }
}

static async Task EffectAsync(string[] args)
{
    // Diagnostic: apply an RgbEffects (0x8071) cluster-effect by index. Note that
    // on G-series keyboards with an onboard profile this is overridden unless the
    // device is in host mode (see `kbmode`) — kept as a low-level probe.
    if (args.Length < 2 || !byte.TryParse(args[1], out var idx))
    {
        Console.Error.WriteLine("usage: effect <index> [paramByteHex ...]   e.g. effect 1 ff 00 00");
        return;
    }
    byte persistence = 1; // OPENLOGI_PERSIST=2 (non-volatile) / 3 (both) to write the onboard profile
    if (byte.TryParse(Environment.GetEnvironmentVariable("OPENLOGI_PERSIST"), out var pv)) persistence = pv;

    var pars = new byte[10];
    for (var i = 2; i < args.Length && i - 2 < 10; i++)
        pars[i - 2] = Convert.ToByte(args[i], 16);

    foreach (var inv in await HidInventory.EnumerateAsync())
    {
        foreach (var dev in inv.Paired)
        {
            if (dev.Capabilities?.Lighting != true) continue;
            var route = DeviceRoute.DeviceRouteFor(inv, dev.Slot);
            if (route is null) continue;
            var label = dev.Codename ?? dev.Kind.ToString();
            await using var session = await DeviceSession.OpenAsync(route);
            if (session is null) continue;
            var ok = await session.ApplyEffectAsync(idx, pars, persistence);
            Console.WriteLine($"{label}: effect {idx} [{Convert.ToHexString(pars)}] persist={persistence} → {(ok ? "applied (host mode, holding 12s)" : "no 0x8071")}");
            if (ok) await Task.Delay(12000);
            Console.WriteLine("  released.");
        }
    }
}

static async Task HostsAsync()
{
    foreach (var inv in await HidInventory.EnumerateAsync())
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

static async Task LightAsync(string hex)
{
    if (hex.Length != 6
        || !byte.TryParse(hex.AsSpan(0, 2), System.Globalization.NumberStyles.HexNumber, null, out var r)
        || !byte.TryParse(hex.AsSpan(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var g)
        || !byte.TryParse(hex.AsSpan(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
    {
        Console.Error.WriteLine("usage: light <RRGGBB>  (e.g. light 00ff00)");
        return;
    }

    foreach (var inv in await HidInventory.EnumerateAsync())
    {
        foreach (var dev in inv.Paired)
        {
            if (dev.Capabilities?.Lighting != true) continue;
            var route = DeviceRoute.DeviceRouteFor(inv, dev.Slot);
            if (route is null) continue;

            var label = dev.Codename ?? dev.Kind.ToString();
            await using var session = await DeviceSession.OpenAsync(route);
            if (session is null) { Console.WriteLine($"{label}: could not open"); continue; }
            var ok = await session.ApplyLightingAsync(r, g, b);
            Console.WriteLine($"{label}: {(ok ? $"set to #{hex}" : "no supported lighting feature")}");
        }
    }
}
