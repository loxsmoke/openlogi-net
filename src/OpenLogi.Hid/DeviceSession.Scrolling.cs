using System.Threading.Channels;
using OpenLogi.HidPP.Feature;
// Alias only what's needed from Core: a blanket `using OpenLogi.Core` would make the
// unqualified `Action` (used by StartDpiButtonCaptureAsync) ambiguous with System.Action.
using DiagnosticLog = OpenLogi.Core.Logging.DiagnosticLog;

namespace OpenLogi.Hid;

public sealed partial class DeviceSession
{
    // ── Scroll direction (HiResWheel 0x2121) ─────────────────────────────────

    /// <summary>
    /// Read whether the wheel is currently inverted via HiResWheel (0x2121), or
    /// <c>null</c> if the device has no native scroll-direction control.
    /// </summary>
    public async Task<bool?> ReadScrollInvertAsync()
    {
        if (_device.GetFeature<HiResWheelFeature>() is not { } wheel) return null;
        using (wheel)
        {
            try { return (await wheel.GetModeAsync().ConfigureAwait(false)).Inverted; }
            catch { return null; }
        }
    }

    /// <summary>
    /// Flip (or restore) the wheel's rotation direction natively in firmware,
    /// preserving the device's current resolution/target bits. Writes to the
    /// device; the bit is volatile, so reapply on connect. <c>false</c> if no 0x2121.
    /// </summary>
    public async Task<bool> ApplyScrollInvertAsync(bool invert)
    {
        if (_device.GetFeature<HiResWheelFeature>() is not { } wheel) return false;
        using (wheel)
        {
            try
            {
                var mode = await wheel.GetModeAsync().ConfigureAwait(false);
                await wheel.SetModeAsync(mode with { Inverted = invert }).ConfigureAwait(false);
                return true;
            }
            catch { return false; }
        }
    }

    // ── Smooth scrolling (HiResWheel 0x2121, hi-res + diverted) ──────────────

    /// <summary>
    /// Put the wheel into hi-res diverted mode and re-emit its motion as OS wheel
    /// data: rotation stops reaching the OS as native scrolling and instead
    /// arrives as 0x2121 movement events, scaled by the wheel's reported
    /// multiplier (<see cref="OpenLogi.Core.SmoothScrollScaler"/>) and handed to
    /// <paramref name="onScroll"/> (±120 = one physical notch). Returns a handle
    /// that restores standard HID reporting on dispose, or <c>null</c> if the
    /// device has no 0x2121. The invert bit is preserved both ways. Mirrors the
    /// diverted-button captures in <c>DeviceSession.Captures.cs</c>.
    ///
    /// HARDWARE-UNVERIFIED: the live divert→event→inject path needs a mouse;
    /// the event decode and tick scaling are pure + tested.
    /// </summary>
    public async Task<IAsyncDisposable?> StartSmoothScrollCaptureAsync(System.Action<int> onScroll)
    {
        if (_device.GetFeature<HiResWheelFeature>() is not { } wheel) return null;
        try
        {
            var capability = await wheel.GetCapabilityAsync().ConfigureAwait(false);
            var mode = await wheel.GetModeAsync().ConfigureAwait(false);
            await wheel.SetModeAsync(mode with { Diverted = true, HighResolution = true }).ConfigureAwait(false);
            // A zero multiplier would make ticks vanish in the scaler; 8 is the
            // typical hardware value when the capability read is blank.
            var multiplier = capability.Multiplier == 0 ? (byte)8 : capability.Multiplier;
            DiagnosticLog.Info("capture", $"smooth scroll: hi-res divert on, multiplier {multiplier}");
            return new SmoothScrollCapture(wheel, _device.GetFeature<WirelessDeviceStatusFeature>(), multiplier, onScroll);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Warn("capture", $"smooth scroll: divert failed: {ex.Message}");
            wheel.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Pumps diverted hi-res wheel movement through the tick scaler into the
    /// injection callback, and restores standard HID wheel reporting on dispose
    /// (a diverted wheel scrolls nothing once no one listens).
    /// </summary>
    private sealed class SmoothScrollCapture : IAsyncDisposable
    {
        private readonly HiResWheelFeature _wheel;
        private readonly WirelessDeviceStatusFeature? _wireless;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _pump;
        private readonly Task _keepalive;

        public SmoothScrollCapture(HiResWheelFeature wheel, WirelessDeviceStatusFeature? wireless, byte multiplier, System.Action<int> onScroll)
        {
            _wheel = wheel;
            _wireless = wireless;
            _pump = PumpAsync(wheel.Listen(), multiplier, onScroll, _cts.Token);
            _keepalive = ReassertDivertLoopAsync(wireless?.Listen(), async () =>
            {
                var mode = await _wheel.GetModeAsync().ConfigureAwait(false);
                if (!mode.Diverted || !mode.HighResolution)
                    await _wheel.SetModeAsync(mode with { Diverted = true, HighResolution = true }).ConfigureAwait(false);
            }, "smooth scroll", _cts.Token);
        }

        private static async Task PumpAsync(
            ChannelReader<HiResWheelEvent> reader, byte multiplier, System.Action<int> onScroll, CancellationToken ct)
        {
            var scaler = new OpenLogi.Core.SmoothScrollScaler(multiplier);
            try
            {
                await foreach (var ev in reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    if (ev is not HiResWheelEvent.Movement m) continue;
                    // A low-res event (hi-res bit cleared out from under us) is
                    // already in whole notches — pass it through unscaled.
                    var wheelData = m.HighResolution
                        ? scaler.Add(m.DeltaV)
                        : m.DeltaV * OpenLogi.Core.SmoothScrollScaler.WheelDelta;
                    if (wheelData != 0) onScroll(wheelData);
                }
            }
            catch (OperationCanceledException) { /* shutting down */ }
            catch (Exception ex)
            {
                // A dead pump means a diverted wheel that scrolls nothing — make it visible.
                DiagnosticLog.Warn("capture", $"smooth scroll pump ended: {ex.Message}");
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { await _pump.ConfigureAwait(false); } catch { /* already faulted/cancelled */ }
            try { await _keepalive.ConfigureAwait(false); } catch { /* cancellation */ }
            try
            {
                var mode = await _wheel.GetModeAsync().ConfigureAwait(false);
                await _wheel.SetModeAsync(mode with { Diverted = false, HighResolution = false }).ConfigureAwait(false);
                DiagnosticLog.Info("capture", "smooth scroll: restored standard wheel reporting");
            }
            catch { DiagnosticLog.Info("capture", "smooth scroll: released (device gone, nothing to restore)"); }
            _wireless?.Dispose();
            _wheel.Dispose();
            _cts.Dispose();
        }
    }
}
