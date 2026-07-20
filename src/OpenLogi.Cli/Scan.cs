using OpenLogi.Core.DeviceInfo;
using OpenLogi.Hid;

/// <summary>
/// Shared enumeration entry for every CLI command. With <see cref="ReceiversOnly"/>
/// (the global `--rx` flag) the sweep is scoped to receiver nodes, skipping the
/// multi-second ping timeouts of direct devices that aren't the target.
/// </summary>
static class Scan
{
    public static bool ReceiversOnly;

    public static Task<IReadOnlyList<DeviceInventory>> Async() =>
        HidInventory.EnumerateAsync(nodeFilter: ReceiversOnly
            ? n => OpenLogi.HidPP.Receiver.Receivers.IsReceiverPid(n.VendorId, n.ProductId)
            : null);
}
