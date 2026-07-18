using OpenLogi.Core.DeviceInfo;

namespace OpenLogi.Hid;

/// <summary>How a device is currently reached — drives the gallery tile's connection icon.</summary>
public enum ConnectionKind
{
    Unknown,
    /// <summary>Plugged in over a USB cable (or a non-wireless direct device).</summary>
    UsbCable,
    /// <summary>Bluetooth / Bluetooth LE direct connection.</summary>
    Bluetooth,
    /// <summary>Paired to a LIGHTSPEED (G-series) dongle.</summary>
    LightspeedDongle,
    /// <summary>Paired to a Unifying dongle.</summary>
    UnifyingDongle,
    /// <summary>Paired to a Logi Bolt dongle.</summary>
    BoltDongle,
}

public static class ConnectionKinds
{
    /// <summary>The connection kind for a device reached via <paramref name="route"/>.</summary>
    public static ConnectionKind For(DeviceRoute? route, PairedDevice device) => route switch
    {
        DeviceRoute.Lightspeed => ConnectionKind.LightspeedDongle,
        DeviceRoute.Unifying => ConnectionKind.UnifyingDongle,
        DeviceRoute.Bolt => ConnectionKind.BoltDongle,
        DeviceRoute.Direct d => DirectKind(d.ProductId, device.ModelInfo),
        _ => ConnectionKind.Unknown,
    };

    /// <summary>
    /// Classify a directly-attached device as Bluetooth or cable. DeviceInformation
    /// (0x0003) fills <see cref="DeviceModelInfo.ModelIds"/> with the per-transport
    /// PIDs in ascending transport-bit order (Bluetooth, BTLE, eQuad, USB —
    /// HARDWARE-VERIFIED on a G915: transports usb+equad+btle ↔ ids [b354, 407c, c33e]),
    /// so the node's PID pinpoints the transport it arrived on. Devices without model
    /// info fall back to Logitech's PID banding, where Bluetooth models live in 0xBxxx.
    /// </summary>
    private static ConnectionKind DirectKind(ushort pid, DeviceModelInfo? modelInfo)
    {
        if (modelInfo is { ModelIds: { } ids, Transports: { } t })
        {
            var slot = 0;
            foreach (var (present, isBluetooth) in (ReadOnlySpan<(bool, bool)>)
                     [(t.Bluetooth, true), (t.Btle, true), (t.Equad, false), (t.Usb, false)])
            {
                if (!present) continue;
                if (slot < ids.Length && ids[slot] == pid)
                    return isBluetooth ? ConnectionKind.Bluetooth : ConnectionKind.UsbCable;
                slot++;
            }
        }
        return (pid & 0xF000) == 0xB000 ? ConnectionKind.Bluetooth : ConnectionKind.UsbCable;
    }
}
