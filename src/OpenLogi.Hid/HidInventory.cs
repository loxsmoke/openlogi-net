using OpenLogi.Core;
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
    /// <summary>Enumerate all receivers (with their paired devices) and directly-attached HID++ devices.</summary>
    public static async Task<IReadOnlyList<DeviceInventory>> EnumerateAsync()
    {
        var result = new List<DeviceInventory>();
        foreach (var hidDevice in HidDiscovery.EnumerateHidppDevices())
        {
            HidppChannel channel;
            try
            {
                channel = await HidppChannel.FromRawChannelAsync(WindowsRawHidChannel.Open(hidDevice)).ConfigureAwait(false);
            }
            catch
            {
                continue; // node we can't open / doesn't speak HID++
            }

            await using (channel)
            {
                try
                {
                    var inventory = Receivers.Detect(channel) switch
                    {
                        DetectedReceiver.Bolt b => await BuildBoltInventoryAsync(channel, b.Receiver).ConfigureAwait(false),
                        DetectedReceiver.Unifying u => await BuildUnifyingInventoryAsync(channel, u.Receiver).ConfigureAwait(false),
                        _ => await BuildDirectInventoryAsync(channel).ConfigureAwait(false),
                    };
                    if (inventory is not null) result.Add(inventory);
                }
                catch
                {
                    // A single unresponsive node must not abort the whole sweep.
                }
            }
        }
        return result;
    }

    private static async Task<DeviceInventory> BuildBoltInventoryAsync(HidppChannel channel, BoltReceiver receiver)
    {
        var uid = await TryAsync(receiver.GetUniqueIdAsync).ConfigureAwait(false);
        try { await receiver.SetWirelessNotificationsAsync(true).ConfigureAwait(false); } catch { /* best effort */ }

        var paired = new List<PairedDevice>();
        foreach (var conn in await receiver.CollectPairedDevicesAsync().ConfigureAwait(false))
        {
            var codename = await TryAsync(() => receiver.GetDeviceCodenameAsync(conn.Index)).ConfigureAwait(false);
            paired.Add(await ProbeAsync(channel, conn.Index, MapBolt(conn.Kind), conn.Online, conn.Wpid, codename).ConfigureAwait(false));
        }

        return new DeviceInventory
        {
            Receiver = new ReceiverInfo { Name = "Logi Bolt Receiver", VendorId = channel.VendorId, ProductId = channel.ProductId, UniqueId = uid },
            Paired = paired,
        };
    }

    private static async Task<DeviceInventory> BuildUnifyingInventoryAsync(HidppChannel channel, UnifyingReceiver receiver)
    {
        var uid = await TryAsync(receiver.GetUniqueIdAsync).ConfigureAwait(false);
        var paired = new List<PairedDevice>();
        foreach (var conn in await receiver.CollectPairedDevicesAsync().ConfigureAwait(false))
            paired.Add(await ProbeAsync(channel, conn.Index, MapUnifying(conn.Kind), conn.Online, conn.Wpid, codename: null).ConfigureAwait(false));

        return new DeviceInventory
        {
            Receiver = new ReceiverInfo { Name = "Unifying Receiver", VendorId = channel.VendorId, ProductId = channel.ProductId, UniqueId = uid },
            Paired = paired,
        };
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
        catch
        {
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
