using System.Threading.Channels;
using OpenLogi.Core.DeviceInfo;
using OpenLogi.HidPP.Feature;

namespace OpenLogi.Hid;

public sealed partial class DeviceSession
{
    // ── Battery (UnifiedBattery 0x1004) ──────────────────────────────────────

    /// <summary>
    /// Read the current battery via UnifiedBattery (0x1004), mapped to the core
    /// <see cref="Core.DeviceInfo.BatteryInfo"/>, or <c>null</c> if the device has no
    /// 0x1004 / the read fails. Unlike the startup-scan read, this runs on the live
    /// (wake-retried) session, so a sleeping keyboard's level resolves on open.
    /// </summary>
    public Task<BatteryInfo?> ReadBatteryAsync() => RetryAsync(ReadBatteryOnceAsync);

    private async Task<BatteryInfo?> ReadBatteryOnceAsync()
    {
        // Prefer UnifiedBattery (0x1004); fall back to BatteryVoltage (0x1001), which
        // Logitech G keyboards expose instead. Both normalize to HidppBatteryInfo.
        if (_device.GetFeature<UnifiedBatteryFeature>() is { } unified)
        {
            using (unified)
            {
                try { return MapBattery(await unified.GetBatteryInfoAsync().ConfigureAwait(false)); }
                catch { /* fall through to 0x1001 */ }
            }
        }
        if (_device.GetFeature<BatteryVoltageFeature>() is { } voltage)
        {
            try { return MapBattery(await voltage.GetBatteryInfoAsync().ConfigureAwait(false)); }
            catch { return null; }
        }
        return null;
    }

    /// <summary>
    /// Subscribe to live battery-change broadcasts (UnifiedBattery 0x1004); <paramref
    /// name="onUpdate"/> fires on each event. Returns a handle that stops listening on
    /// dispose, or <c>null</c> if the device has no 0x1004. The handle owns its own
    /// feature listener, so it is safe alongside a persistent session on the device.
    /// </summary>
    public IAsyncDisposable? StartBatteryMonitor(Action<BatteryInfo> onUpdate)
    {
        if (_device.GetFeature<UnifiedBatteryFeature>() is not { } batt) return null;
        return new BatteryMonitor(batt, onUpdate);
    }

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

    /// <summary>Listens for 0x1004 battery broadcasts and tears down the listener on dispose.</summary>
    private sealed class BatteryMonitor : IAsyncDisposable
    {
        private readonly UnifiedBatteryFeature _batt;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _pump;

        public BatteryMonitor(UnifiedBatteryFeature batt, Action<BatteryInfo> onUpdate)
        {
            _batt = batt;
            _pump = PumpAsync(batt.Listen(), onUpdate, _cts.Token);
        }

        private static async Task PumpAsync(ChannelReader<BatteryEvent> reader, Action<BatteryInfo> onUpdate, CancellationToken ct)
        {
            try
            {
                await foreach (var ev in reader.ReadAllAsync(ct).ConfigureAwait(false))
                    onUpdate(MapBattery(ev.Info));
            }
            catch (OperationCanceledException) { /* shutting down */ }
            catch { /* listener torn down with the channel */ }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { await _pump.ConfigureAwait(false); } catch { /* already faulted/cancelled */ }
            _batt.Dispose();
            _cts.Dispose();
        }
    }
}
