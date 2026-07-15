using OpenLogi.Core.DeviceInfo;
using OpenLogi.Core.Logging;
using OpenLogi.HidPP.Channel;
using OpenLogi.HidPP.Device;
using OpenLogi.HidPP.Feature;
using OpenLogi.HidPP.Receiver;

namespace OpenLogi.Hid;

/// <summary>
/// One-shot inventory of receivers + paired devices, building
/// <see cref="DeviceInventory"/> on top of the section-3 HID++ engine. The clean
/// rewrite of Rust <c>openlogi-hid::inventory</c> (the async-hid node-ledger /
/// route-reopen machinery is not reproduced).
///
/// HARDWARE-UNVERIFIED: this path has not been exercised against a real Logitech
/// device. Per-device probes are best-effort and defensive.
/// </summary>
public static class HidInventory
{
    /// <summary>
    /// Enumerate all receivers (with their paired devices) and directly-attached HID++
    /// devices. <paramref name="nodeSource"/> defaults to the local machine; tests pass a
    /// scripted source to model a specific receiver's interface layout without hardware.
    /// </summary>
    public static async Task<IReadOnlyList<DeviceInventory>> EnumerateAsync(IHidNodeSource? nodeSource = null)
    {
        var result = new List<DeviceInventory>();
        var nodes = (nodeSource ?? LocalHidNodeSource.Instance).Enumerate();
        DiagnosticLog.Info("sweep", $"{nodes.Count} Logitech HID++ node(s)");
        foreach (var hidNode in nodes)
        {
            var node = NodeTag(hidNode);
            HidppChannel channel;
            try
            {
                channel = await HidppChannel.FromRawChannelAsync(hidNode.Open()).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Node we can't open / doesn't speak HID++.
                DiagnosticLog.Warn("sweep", $"{node}: open failed: {ex.Message}");
                continue;
            }

            await using (channel)
            {
                // Receiver registers ride the short (7-byte) HID++ reports; a
                // receiver's long-only interface answers pings but times out on
                // every register op (3×5 s — HARDWARE-VERIFIED on 046d:c547).
                if (Receivers.IsReceiverPid(channel.VendorId, channel.ProductId) && !channel.SupportsShort)
                {
                    DiagnosticLog.Info("sweep", $"{node}: receiver interface without short reports — skipped");
                    continue;
                }
                try
                {
                    var detected = Receivers.Detect(channel);
                    var inventory = detected switch
                    {
                        DetectedReceiver.Bolt b => await BuildBoltInventoryAsync(channel, b.Receiver, DeviceInterfaceFor(nodes, channel)).ConfigureAwait(false),
                        // LIGHTSPEED shares Unifying's register set; only the name and
                        // the uid differ (no serial register → synthetic uid, no read).
                        DetectedReceiver.Unifying u => await BuildUnifyingInventoryAsync(channel, u.Receiver, u.Name).ConfigureAwait(false),
                        DetectedReceiver.Lightspeed l => await BuildUnifyingInventoryAsync(channel, l.Receiver, l.Name,
                            Receivers.LightspeedSyntheticUid(channel), DeviceInterfaceFor(nodes, channel)).ConfigureAwait(false),
                        _ => await BuildDirectInventoryAsync(channel).ConfigureAwait(false),
                    };
                    if (inventory is not null)
                    {
                        DiagnosticLog.Info("sweep",
                            $"{node} → {inventory.Receiver.Name}, {inventory.Paired.Count} device(s)");
                        foreach (var p in inventory.Paired)
                            DiagnosticLog.Info("sweep",
                                $"  slot {p.Slot}: {p.Codename ?? "(unnamed)"} ({p.Kind}, {(p.Online ? "online" : "offline")}"
                                + $"{(p.Wpid is { } w ? $", wpid {w:x4}" : "")}"
                                + $"{(p.Battery?.Percentage is { } b ? $", batt {b}%" : "")})");
                        result.Add(inventory);
                    }
                    else
                    {
                        DiagnosticLog.Info("sweep", $"{node}: no receiver match, no HID++ 2.0 device at 0xff");
                    }
                }
                catch (Exception ex)
                {
                    // A single unresponsive node must not abort the whole sweep.
                    DiagnosticLog.Error("sweep", $"{node}: sweep failed", ex);
                }
            }
        }
        var deduped = Deduplicate(result);
        DiagnosticLog.Info("sweep", $"done: {deduped.Count} receiver(s)/device(s) usable");
        return deduped;
    }

    /// <summary>
    /// Collapse entries that are the same physical unit seen over several paths.
    /// A device connected both through its receiver and directly (Bluetooth or
    /// USB cable), or one exposing several HID++ collections, answers on every
    /// path and would be listed once per path. The unit id from DeviceInformation
    /// (0x0003) identifies the physical unit; receiver-paired entries win over
    /// direct nodes, and only devices reporting a non-zero unit id participate.
    ///
    /// The exception where a receiver-paired entry loses: a slot the receiver
    /// reports <em>offline</em> while the same model is online over another
    /// transport (e.g. a G915 paired to its LIGHTSPEED dongle but currently on
    /// Bluetooth). An offline slot has no readable unit id, so it is matched by
    /// wpid against the per-transport model ids (0x0003) of online devices. A
    /// model id can't tell two identical units apart, so with twin devices the
    /// second unit's dormant pairing is hidden while its twin is online —
    /// benign, since the dormant slot offers nothing to configure.
    /// </summary>
    public static IReadOnlyList<DeviceInventory> Deduplicate(IReadOnlyList<DeviceInventory> inventories)
    {
        static string? UnitKey(PairedDevice d) =>
            d.ModelInfo is { UnitId: { } uid } && uid.Any(b => b != 0) ? Convert.ToHexString(uid) : null;

        static bool IsDirect(DeviceInventory inv) =>
            inv.Receiver.UniqueId is null
            && inv.Paired.All(d => d.Slot == DeviceRoute.DirectDeviceIndex);

        var seen = new HashSet<string>();
        foreach (var inv in inventories.Where(i => !IsDirect(i)))
            foreach (var d in inv.Paired)
                if (UnitKey(d) is { } key)
                    seen.Add(key);

        var onlineModelIds = inventories
            .SelectMany(i => i.Paired)
            .Where(d => d.Online)
            .SelectMany(d => d.ModelInfo?.ModelIds ?? [])
            .Where(id => id != 0)
            .ToHashSet();

        var result = new List<DeviceInventory>();
        foreach (var inv in inventories)
        {
            if (!IsDirect(inv))
            {
                var live = inv.Paired.Where(d =>
                {
                    if (d.Online || d.Wpid is not { } w || !onlineModelIds.Contains(w)) return true;
                    DiagnosticLog.Info("sweep",
                        $"dedup: dropped offline slot {d.Slot} (wpid {w:x4}) on {inv.Receiver.Name}"
                        + " — same model is online via another transport");
                    return false;
                }).ToList();
                result.Add(live.Count == inv.Paired.Count ? inv : inv with { Paired = live });
                continue;
            }

            var kept = inv.Paired.Where(d =>
            {
                if (UnitKey(d) is not { } key || seen.Add(key)) return true;
                DiagnosticLog.Info("sweep",
                    $"dedup: dropped direct {inv.Receiver.VendorId:x4}:{inv.Receiver.ProductId:x4}"
                    + $" {d.Codename ?? "(unnamed)"} — same unit as an already-listed device");
                return false;
            }).ToList();

            if (kept.Count == inv.Paired.Count)
                result.Add(inv);
            else if (kept.Count > 0)
                result.Add(inv with { Paired = kept });
        }
        return result;
    }

    private static async Task<DeviceInventory> BuildBoltInventoryAsync(
        HidppChannel channel, BoltReceiver receiver, IHidNode? deviceInterface = null)
    {
        var uid = await TryAsync(receiver.GetUniqueIdAsync).ConfigureAwait(false);
        if (uid is null)
            DiagnosticLog.Warn("sweep", "receiver uid read (0xfb) failed — devices will be visible but not controllable");
        try { await receiver.SetWirelessNotificationsAsync(true).ConfigureAwait(false); }
        catch (Exception ex) { DiagnosticLog.Warn("sweep", $"enable wireless notifications failed: {ex.Message}"); }

        // Newer Bolt receivers carry device HID++ 2.0 on a long-only *device* interface
        // (col02), separate from the short *control* interface (col01) where the paired
        // device is unreachable at its slot — the same split LIGHTSPEED has (issue #6,
        // HARDWARE-CONFIRMED on 046d:c548 with an MX Master 4 / Lift). Probe on the device
        // interface when one is present so the slot yields a real name/model/capabilities;
        // fall back to the control channel for the classic single-interface Bolt receiver.
        HidppChannel? deviceChannel = null;
        if (deviceInterface is not null)
        {
            try { deviceChannel = await HidppChannel.FromRawChannelAsync(deviceInterface.Open()).ConfigureAwait(false); }
            catch (Exception ex) { DiagnosticLog.Warn("sweep", $"device interface open failed: {ex.Message} — probing on the control interface"); }
        }
        var probeChannel = deviceChannel ?? channel;

        try
        {
            var paired = new List<PairedDevice>();
            foreach (var conn in await receiver.CollectPairedDevicesAsync().ConfigureAwait(false))
            {
                // Codename comes from the receiver's pairing registers (control channel).
                var codename = await TryAsync(() => receiver.GetDeviceCodenameAsync(conn.Index)).ConfigureAwait(false);
                paired.Add(await ProbeAsync(probeChannel, conn.Index, MapBolt(conn.Kind), conn.Online, conn.Wpid, codename).ConfigureAwait(false));
            }

            return new DeviceInventory
            {
                Receiver = new ReceiverInfo { Name = "Logi Bolt Receiver", VendorId = channel.VendorId, ProductId = channel.ProductId, UniqueId = uid },
                Paired = paired,
            };
        }
        finally
        {
            if (deviceChannel is not null) await deviceChannel.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<DeviceInventory> BuildUnifyingInventoryAsync(
        HidppChannel channel, UnifyingReceiver receiver, string name, string? uidOverride = null,
        IHidNode? deviceInterface = null)
    {
        var uid = uidOverride ?? await TryAsync(receiver.GetUniqueIdAsync).ConfigureAwait(false);
        if (uid is null)
            DiagnosticLog.Warn("sweep", "receiver uid read (0xb5/03) failed — devices will be visible but not controllable");
        // Without the wireless-notification flag the arrival trigger below stays
        // silent on a cold-booted receiver (same reason the Bolt path sets it).
        try { await receiver.SetWirelessNotificationsAsync(true).ConfigureAwait(false); }
        catch (Exception ex) { DiagnosticLog.Warn("sweep", $"enable wireless notifications failed: {ex.Message}"); }

        // A LIGHTSPEED receiver carries device HID++ 2.0 on a long-only *device*
        // interface (col02) that's separate from the short *control* interface
        // (col01) we enumerate on — the paired device is unreachable via HID++ 2.0 at
        // its slot on the control channel. Probe on the device interface when one was
        // found, so the slot yields a real name/model/capabilities (and the tile isn't
        // just a bare "Keyboard"). Falls back to the control channel when there's no
        // separate device interface (the combined Bolt/Unifying case).
        HidppChannel? deviceChannel = null;
        if (deviceInterface is not null)
        {
            try { deviceChannel = await HidppChannel.FromRawChannelAsync(deviceInterface.Open()).ConfigureAwait(false); }
            catch (Exception ex) { DiagnosticLog.Warn("sweep", $"device interface open failed: {ex.Message} — probing on the control interface"); }
        }
        var probeChannel = deviceChannel ?? channel;

        try
        {
            var paired = new List<PairedDevice>();
            foreach (var conn in await receiver.CollectPairedDevicesAsync().ConfigureAwait(false))
            {
                // Name from the pairing registers, so offline slots aren't blank tiles;
                // the online probe still supplies the marketing name as a fallback.
                // (A G915 LIGHTSPEED dongle answers a fast ResourceError for its
                // offline slot instead of a name — HARDWARE-VERIFIED on 046d:c547.)
                string? codename = null;
                try { codename = await receiver.GetDeviceCodenameAsync(conn.Index).ConfigureAwait(false); }
                catch (Exception ex) { DiagnosticLog.Debug("sweep", $"  slot {conn.Index}: codename read (0xb5/0x4n) failed: {ex.Message}"); }
                paired.Add(await ProbeAsync(probeChannel, conn.Index, MapUnifying(conn.Kind), conn.Online, conn.Wpid, codename).ConfigureAwait(false));
            }

            return new DeviceInventory
            {
                Receiver = new ReceiverInfo { Name = name, VendorId = channel.VendorId, ProductId = channel.ProductId, UniqueId = uid },
                Paired = paired,
            };
        }
        finally
        {
            if (deviceChannel is not null) await deviceChannel.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Find a receiver's long-only *device* interface — the sibling HID node that
    /// carries device HID++ 2.0 traffic, separate from the short *control* interface
    /// <paramref name="control"/> we enumerate on. LIGHTSPEED and newer Bolt receivers
    /// (issue #6) split these; older Bolt/Unifying combine them into one interface, so
    /// there's no sibling to find and this returns <c>null</c>. Matched by
    /// VID/PID, consistent with the VID/PID-based LIGHTSPEED synthetic uid — two
    /// identical dongles on one host aren't distinguished, which is already accepted.
    /// </summary>
    private static IHidNode? DeviceInterfaceFor(IReadOnlyList<IHidNode> nodes, HidppChannel control)
    {
        foreach (var n in nodes)
        {
            if (n.VendorId != control.VendorId || n.ProductId != control.ProductId) continue;
            if (n is { SupportsShort: false, SupportsLong: true })
                return n;
        }
        return null;
    }

    private static async Task<DeviceInventory?> BuildDirectInventoryAsync(HidppChannel channel)
    {
        // A directly-attached device answers at the HID++ self-index 0xff.
        HidppDevice device;
        try { device = await HidppDevice.NewAsync(channel, DeviceRoute.DirectDeviceIndex).ConfigureAwait(false); }
        catch { return null; }

        var paired = await ProbeOnDeviceAsync(device, DeviceRoute.DirectDeviceIndex, DeviceKind.Unknown, online: true, wpid: null, codename: null).ConfigureAwait(false);
        return new DeviceInventory
        {
            // The "receiver" stands in for the direct node (no UID → Direct route).
            Receiver = new ReceiverInfo { Name = "Direct device", VendorId = channel.VendorId, ProductId = channel.ProductId, UniqueId = null },
            Paired = [paired],
        };
    }

    private static async Task<PairedDevice> ProbeAsync(
        HidppChannel channel, byte slot, DeviceKind kind, bool online, ushort? wpid, string? codename)
    {
        if (!online)
            return new PairedDevice { Slot = slot, Codename = codename, Wpid = wpid, Kind = kind, Online = false };

        HidppDevice device;
        try { device = await HidppDevice.NewAsync(channel, slot).ConfigureAwait(false); }
        catch (Exception ex)
        {
            DiagnosticLog.Warn("sweep", $"  slot {slot}: HID++ 2.0 probe failed ({ex.Message}) — listed without capabilities");
            return new PairedDevice { Slot = slot, Codename = codename, Wpid = wpid, Kind = kind, Online = online };
        }
        return await ProbeOnDeviceAsync(device, slot, kind, online, wpid, codename).ConfigureAwait(false);
    }

    private static async Task<PairedDevice> ProbeOnDeviceAsync(
        HidppDevice device, byte slot, DeviceKind kind, bool online, ushort? wpid, string? codename)
    {
        Capabilities? capabilities = null;
        DeviceModelInfo? modelInfo = null;
        BatteryInfo? battery = null;

        IReadOnlyList<FeatureInformation>? features = null;
        try { features = await device.EnumerateFeaturesAsync().ConfigureAwait(false); } catch { /* best effort */ }
        if (features is not null)
        {
            capabilities = Capabilities.FromFeatureIds([.. features.Select(f => f.Id)]);

            if (device.GetFeature<DeviceInformationFeature>() is { } info)
            {
                var di = await TryAsync(info.GetDeviceInfoAsync).ConfigureAwait(false);
                if (di is not null) modelInfo = MapModelInfo(di);
            }
            if (device.GetFeature<DeviceTypeAndNameFeature>() is { } typeName)
            {
                codename ??= await TryAsync(typeName.GetWholeDeviceNameAsync).ConfigureAwait(false);
                if (kind == DeviceKind.Unknown
                    && await TryStructAsync(typeName.GetDeviceTypeAsync).ConfigureAwait(false) is { } dt)
                    kind = MapDeviceType(dt);
            }
            if (device.GetFeature<UnifiedBatteryFeature>() is { } batt)
            {
                using (batt)
                {
                    var bi = await TryStructAsync(batt.GetBatteryInfoAsync).ConfigureAwait(false);
                    if (bi is { } info2) battery = MapBattery(info2);
                }
            }
            // Fall back to BatteryVoltage (0x1001) — Logitech G keyboards expose this
            // instead of UnifiedBattery — so the gallery shows their charge immediately,
            // not only after the device page is opened.
            if (battery is null && device.GetFeature<BatteryVoltageFeature>() is { } volt)
            {
                var bv = await TryStructAsync(volt.GetBatteryInfoAsync).ConfigureAwait(false);
                if (bv is { } info3) battery = MapBattery(info3);
            }
        }

        return new PairedDevice
        {
            Slot = slot,
            Codename = codename,
            Wpid = wpid,
            Kind = kind,
            Online = online,
            Battery = battery,
            ModelInfo = modelInfo,
            Capabilities = capabilities ?? Capabilities.PresumedFromKind(kind),
        };
    }

    /// <summary>
    /// Log tag for a HID node, with the interface/collection part of the device
    /// path so a composite device's nodes are distinguishable in the log
    /// (e.g. "node 046d:c547 [mi_02&amp;col01]" — without it, two interfaces of
    /// the same receiver log identically).
    /// </summary>
    private static string NodeTag(IHidNode device)
    {
        var tag = $"node {device.VendorId:x4}:{device.ProductId:x4}";
        try
        {
            // \\?\hid#vid_046d&pid_c547&mi_01&col02#8&2de4…#{guid} → "mi_01&col02"
            var hw = device.DevicePath.Split('#');
            if (hw.Length > 1)
            {
                var detail = string.Join("&", hw[1].Split('&')
                    .Where(s => s.StartsWith("mi_", StringComparison.OrdinalIgnoreCase)
                             || s.StartsWith("col", StringComparison.OrdinalIgnoreCase)));
                if (detail.Length > 0) tag += $" [{detail}]";
            }
        }
        catch { /* path shape unexpected — plain tag is fine */ }
        return tag;
    }

    // ── Mapping helpers ──────────────────────────────────────────────────────

    private static DeviceKind MapBolt(BoltDeviceKind k) => k switch
    {
        BoltDeviceKind.Mouse => DeviceKind.Mouse,
        BoltDeviceKind.Keyboard => DeviceKind.Keyboard,
        BoltDeviceKind.Numpad => DeviceKind.Numpad,
        BoltDeviceKind.Presenter => DeviceKind.Presenter,
        BoltDeviceKind.Remote => DeviceKind.Remote,
        BoltDeviceKind.Trackball => DeviceKind.Trackball,
        BoltDeviceKind.Touchpad => DeviceKind.Touchpad,
        BoltDeviceKind.Tablet => DeviceKind.Tablet,
        BoltDeviceKind.Gamepad => DeviceKind.Gamepad,
        BoltDeviceKind.Joystick => DeviceKind.Joystick,
        BoltDeviceKind.Headset => DeviceKind.Headset,
        _ => DeviceKind.Unknown,
    };

    private static DeviceKind MapUnifying(UnifyingDeviceKind k) => k switch
    {
        UnifyingDeviceKind.Mouse => DeviceKind.Mouse,
        UnifyingDeviceKind.Keyboard => DeviceKind.Keyboard,
        UnifyingDeviceKind.Numpad => DeviceKind.Numpad,
        UnifyingDeviceKind.Presenter => DeviceKind.Presenter,
        UnifyingDeviceKind.Remote => DeviceKind.Remote,
        UnifyingDeviceKind.Trackball => DeviceKind.Trackball,
        UnifyingDeviceKind.Touchpad => DeviceKind.Touchpad,
        _ => DeviceKind.Unknown,
    };

    private static DeviceKind MapDeviceType(DeviceType t) => t switch
    {
        DeviceType.Mouse => DeviceKind.Mouse,
        DeviceType.Keyboard => DeviceKind.Keyboard,
        DeviceType.Numpad => DeviceKind.Numpad,
        DeviceType.Trackpad => DeviceKind.Touchpad,
        DeviceType.Trackball => DeviceKind.Trackball,
        DeviceType.Presenter => DeviceKind.Presenter,
        DeviceType.RemoteControl => DeviceKind.Remote,
        DeviceType.Headset => DeviceKind.Headset,
        DeviceType.Gamepad => DeviceKind.Gamepad,
        DeviceType.Joystick => DeviceKind.Joystick,
        _ => DeviceKind.Unknown,
    };

    private static DeviceModelInfo MapModelInfo(DeviceInformation di) => new()
    {
        EntityCount = di.EntityCount,
        SerialNumber = null,
        UnitId = di.UnitId,
        Transports = new DeviceTransports { Usb = di.Transport.Usb, Equad = di.Transport.EQuad, Btle = di.Transport.Btle, Bluetooth = di.Transport.Bluetooth },
        ModelIds = di.ModelId,
        ExtendedModelId = di.ExtendedModelId,
    };

    private static BatteryInfo MapBattery(HidppBatteryInfo b) => new()
    {
        Percentage = b.ChargingPercentage,
        Level = b.Level switch
        {
            HidppBatteryLevel.Critical => BatteryLevel.Critical,
            HidppBatteryLevel.Low => BatteryLevel.Low,
            HidppBatteryLevel.Good => BatteryLevel.Good,
            HidppBatteryLevel.Full => BatteryLevel.Full,
            _ => BatteryLevel.Unknown,
        },
        Status = b.Status switch
        {
            HidppBatteryStatus.Discharging => BatteryStatus.Discharging,
            HidppBatteryStatus.Charging => BatteryStatus.Charging,
            HidppBatteryStatus.ChargingSlow => BatteryStatus.ChargingSlow,
            HidppBatteryStatus.Full => BatteryStatus.Full,
            HidppBatteryStatus.Error => BatteryStatus.Error,
            _ => BatteryStatus.Unknown,
        },
    };

    private static async Task<T?> TryAsync<T>(Func<Task<T>> op) where T : class
    {
        try { return await op().ConfigureAwait(false); } catch { return null; }
    }

    private static async Task<T?> TryStructAsync<T>(Func<Task<T>> op) where T : struct
    {
        try { return await op().ConfigureAwait(false); } catch { return null; }
    }
}
