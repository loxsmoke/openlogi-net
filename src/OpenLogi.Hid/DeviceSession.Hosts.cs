using OpenLogi.HidPP.Feature;

namespace OpenLogi.Hid;

public sealed partial class DeviceSession
{
    // ── Hosts (EasySwitch / multi-host) ──────────────────────────────────────

    /// <summary>
    /// Read host count, current host and per-host detail (name + paired status +
    /// bus) — preferring HostsInfo (0x1815) for names, falling back to ChangeHost
    /// (0x1814) for just count/current. <c>null</c> if neither is supported.
    /// </summary>
    public Task<HostSnapshot?> ReadHostsAsync() => RetryAsync(ReadHostsOnceAsync);

    private async Task<HostSnapshot?> ReadHostsOnceAsync()
    {
        if (_device.GetFeature<HostsInfoFeature>() is { } hi)
        {
            try
            {
                var info = await hi.GetFeatureInfoAsync().ConfigureAwait(false);
                var current = info.CurrentHost is HostIndex.Slot s ? s.Index : 0;
                var details = new List<HostDetail>(info.HostCount);
                for (byte i = 0; i < info.HostCount; i++)
                {
                    var h = await hi.GetHostInfoAsync(new HostIndex.Slot(i)).ConfigureAwait(false);
                    var paired = h.Status == HostSlotStatus.Paired;
                    string? name = null;
                    if (paired && h.NameLen > 0)
                        try { name = await hi.GetHostFriendlyNameAsync(new HostIndex.Slot(i), h.NameLen).ConfigureAwait(false); }
                        catch { /* name read unsupported */ }
                    details.Add(new HostDetail(i, i == current, paired,
                        h.BusType.ToString(), string.IsNullOrWhiteSpace(name) ? null : name));
                }
                var canDelete = info.Capabilities.HasFlag(HostsInfoCapabilities.DeleteHost);
                return new HostSnapshot(info.HostCount, (byte)Math.Max(current, 0), details, canDelete);
            }
            catch { /* fall back */ }
        }

        if (_device.GetFeature<ChangeHostFeature>() is { } ch)
        {
            try
            {
                var info = await ch.GetHostInfoAsync().ConfigureAwait(false);
                var details = Enumerable.Range(0, info.HostCount)
                    .Select(i => new HostDetail(i, i == info.CurrentHost, false, "", null)).ToList();
                return new HostSnapshot(info.HostCount, info.CurrentHost, details, SupportsDelete: false);
            }
            catch { /* ignore */ }
        }
        return null;
    }

    /// <summary>
    /// Switch the device to <paramref name="host"/> (0-based). Fire-and-forget — the
    /// device drops off the current host, so this disconnects it from this computer.
    /// </summary>
    public async Task<bool> SwitchHostAsync(byte host)
    {
        if (_device.GetFeature<ChangeHostFeature>() is not { } ch) return false;
        try { await ch.SetCurrentHostAsync(host).ConfigureAwait(false); return true; }
        catch { return false; }
    }

    /// <summary>
    /// Forget the pairing in EasySwitch slot <paramref name="host"/> (0-based) via
    /// HostsInfo (0x1815), freeing it. Returns false if the device lacks delete
    /// support or refuses (e.g. the current host).
    /// </summary>
    public async Task<bool> ClearHostAsync(byte host)
    {
        if (_device.GetFeature<HostsInfoFeature>() is not { } hi) return false;
        try { await hi.DeleteHostAsync(new HostIndex.Slot(host)).ConfigureAwait(false); return true; }
        catch { return false; }
    }
}
