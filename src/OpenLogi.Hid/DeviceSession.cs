using OpenLogi.HidPP.Channel;
using OpenLogi.HidPP.Device;
using OpenLogi.HidPP.Feature;
using OpenLogi.HidPP.Receiver;
// Alias only what's needed from Core: a blanket `using OpenLogi.Core` would make the
// unqualified `Action` (used by StartDpiButtonCaptureAsync) ambiguous with System.Action.
using DiagnosticLog = OpenLogi.Core.Logging.DiagnosticLog;

namespace OpenLogi.Hid;

/// <summary>
/// An open HID++ session to one device, used by the GUI to read/apply DPI and
/// SmartShift. Holds the channel + an enumerated <see cref="HidppDevice"/> for the
/// device's lifetime in the UI; dispose when the selection changes.
///
/// HARDWARE-VERIFIED read path uses the same engine the CLI exercises; apply
/// writes to the physical device (only on explicit user action).
///
/// The feature surface is split across partials: scrolling
/// (<c>DeviceSession.Scrolling.cs</c>), lighting (<c>.Lighting.cs</c>), hosts
/// (<c>.Hosts.cs</c>), onboard profiles (<c>.Profiles.cs</c>), battery
/// (<c>.Battery.cs</c>) and diverted-button captures (<c>.Captures.cs</c>).
/// </summary>
public sealed partial class DeviceSession : IAsyncDisposable
{
    private readonly HidppChannel _channel;
    private readonly HidppDevice _device;
    private readonly bool _ownsChannel;

    private DeviceSession(HidppChannel channel, HidppDevice device, bool ownsChannel = true)
    {
        _channel = channel;
        _device = device;
        _ownsChannel = ownsChannel;
    }

    /// <summary>
    /// Wrap an already-open channel + enumerated device in a session WITHOUT taking
    /// ownership — so lighting can be applied at the one moment a napping wireless
    /// keyboard is known reachable: during the inventory sweep's probe, before its
    /// channel closes. Disposing an attached session leaves the channel open.
    /// </summary>
    public static DeviceSession Attach(HidppChannel channel, HidppDevice device) =>
        new(channel, device, ownsChannel: false);

    /// <summary>
    /// Open a session for <paramref name="route"/>, or <c>null</c> if unreachable.
    /// A composite device (e.g. the G915 keyboard) exposes several HID++ interfaces
    /// with different feature subsets; we keep the best-scoring one (preferring the
    /// interface that carries the control features over a mere feature-count lead),
    /// since enumeration order isn't stable and only one interface is the real
    /// control interface.
    /// </summary>
    public static async Task<DeviceSession?> OpenAsync(DeviceRoute route)
    {
        DeviceSession? best = null;
        var bestScore = -1;

        // The interface carrying OnboardProfiles (0x8100) is the keyboard's real
        // control interface — needed both to read profiles AND to enter host mode for
        // per-key lighting. A composite device exposes several HID++ interfaces, so
        // strongly prefer that one (then PerKeyLighting), then raw feature count.
        static int Score(HidppDevice device, int featureCount) =>
            (device.FeatureIndex(0x8100) is not null ? 1000 : 0)
            + (device.FeatureIndex(0x8081) is not null ? 500 : 0)
            + featureCount;

        foreach (var hid in HidDiscovery.EnumerateHidppDevices())
        {
            if (route is DeviceRoute.Direct d && (hid.VendorID != d.VendorId || hid.ProductID != d.ProductId))
                continue;

            HidppChannel channel;
            try { channel = await HidppChannel.FromRawChannelAsync(WindowsRawHidChannel.Open(hid)).ConfigureAwait(false); }
            catch { continue; }

            if (!await MatchesReceiverAsync(channel, route).ConfigureAwait(false))
            {
                await channel.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            HidppDevice device;
            int score;
            try
            {
                device = await HidppDevice.NewAsync(channel, route.DeviceIndex()).ConfigureAwait(false);
                var features = await device.EnumerateFeaturesAsync().ConfigureAwait(false);
                score = Score(device, features?.Count ?? 0);
            }
            catch
            {
                await channel.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            // Keep the best-scoring interface among all matching nodes. (Both direct
            // and receiver-routed composite devices can expose several interfaces;
            // picking by score avoids landing on one missing 0x8100/0x8081.)
            if (score > bestScore)
            {
                if (best is not null) await best.DisposeAsync().ConfigureAwait(false);
                best = new DeviceSession(channel, device);
                bestScore = score;
            }
            else
            {
                await channel.DisposeAsync().ConfigureAwait(false);
            }
        }
        if (best is null)
            DiagnosticLog.Warn("session", $"open failed: no usable interface for {route}");
        else
            DiagnosticLog.Info("session", $"open {route}: interface score {bestScore}");
        return best;
    }

    private static async Task<bool> MatchesReceiverAsync(HidppChannel channel, DeviceRoute route)
    {
        switch (route)
        {
            case DeviceRoute.Direct:
                return true;
            case DeviceRoute.Bolt b:
                return Receivers.Detect(channel) is DetectedReceiver.Bolt rb
                       && string.Equals(await rb.Receiver.GetUniqueIdAsync().ConfigureAwait(false), b.ReceiverUid, StringComparison.OrdinalIgnoreCase);
            case DeviceRoute.Unifying u:
                return Receivers.Detect(channel) is DetectedReceiver.Unifying ru
                       && string.Equals(await ru.Receiver.GetUniqueIdAsync().ConfigureAwait(false), u.ReceiverUid, StringComparison.OrdinalIgnoreCase);
            case DeviceRoute.Lightspeed l:
                // LIGHTSPEED has no serial register — match on the same synthetic
                // uid the inventory assigned; no register I/O (each read is a 5 s timeout).
                // Don't gate on short support: a LIGHTSPEED receiver carries device
                // HID++ 2.0 on a long-only *device* interface (col02), separate from the
                // short *control* interface (col01) where the slot is unreachable. The
                // scored open below tries every matching interface and keeps whichever one
                // actually answers HID++ 2.0 for the slot.
                return Receivers.Detect(channel) is DetectedReceiver.Lightspeed
                       && string.Equals(Receivers.LightspeedSyntheticUid(channel), l.ReceiverUid, StringComparison.OrdinalIgnoreCase);
            default:
                return false;
        }
    }

    // ── Read retry ────────────────────────────────────────────────────────────
    // A wireless device waking from power-saving, or contention on the shared HID++
    // engine, makes the first read or two return null / throw. A few quick re-reads
    // ride that out — HARDWARE-VERIFIED: 4 concurrent reads all fail without this and
    // recover with it. (null = "not ready, retry"; a non-null result is returned.)

    private static async Task<T?> RetryAsync<T>(Func<Task<T?>> read, int attempts = 4, int delayMs = 120) where T : class
    {
        for (var i = 0; ; i++)
        {
            try { if (await read().ConfigureAwait(false) is { } r) return r; }
            catch { /* transient — retry */ }
            if (i >= attempts - 1) return null;
            await Task.Delay(delayMs).ConfigureAwait(false);
        }
    }

    private static async Task<T?> RetryStructAsync<T>(Func<Task<T?>> read, int attempts = 4, int delayMs = 120) where T : struct
    {
        for (var i = 0; ; i++)
        {
            try { if (await read().ConfigureAwait(false) is { } r) return r; }
            catch { /* transient — retry */ }
            if (i >= attempts - 1) return null;
            await Task.Delay(delayMs).ConfigureAwait(false);
        }
    }

    // ── DPI ──────────────────────────────────────────────────────────────────

    /// <summary>Read current + supported DPI (sensor 0) via AdjustableDpi (0x2201).</summary>
    public async Task<DpiSnapshot?> ReadDpiAsync()
    {
        if (_device.GetFeature<AdjustableDpiFeature>() is not { } dpi) return null;
        try
        {
            var current = await dpi.GetSensorDpiAsync(0).ConfigureAwait(false);
            var supported = await dpi.GetSensorDpiListAsync(0).ConfigureAwait(false);
            return new DpiSnapshot(current, supported);
        }
        catch { return null; }
    }

    /// <summary>Apply a sensor DPI (sensor 0). Writes to the device.</summary>
    public async Task<bool> ApplyDpiAsync(ushort dpi)
    {
        if (_device.GetFeature<AdjustableDpiFeature>() is not { } feature) return false;
        try { await feature.SetSensorDpiAsync(0, dpi).ConfigureAwait(false); return true; }
        catch { return false; }
    }

    // ── SmartShift ─────────────────────────────────────────────────────────────

    /// <summary>Read SmartShift state, preferring the enhanced feature (0x2111), else 0x2110.</summary>
    public async Task<SmartShiftSnapshot?> ReadSmartShiftAsync()
    {
        try
        {
            if (_device.GetFeature<SmartShiftEnhancedFeature>() is { } enhanced)
            {
                var status = await enhanced.GetRatchetControlModeAsync().ConfigureAwait(false);
                var caps = await enhanced.GetCapabilitiesAsync().ConfigureAwait(false);
                return new SmartShiftSnapshot(
                    status.WheelMode == SmartShiftWheelMode.Ratchet,
                    status.AutoDisengage, status.CurrentTunableTorque,
                    caps.Capabilities.HasFlag(SmartShiftEnhancedCapabilities.TunableTorque));
            }
            if (_device.GetFeature<SmartShiftFeature>() is { } basic)
            {
                var mode = await basic.GetRatchetControlModeAsync().ConfigureAwait(false);
                return new SmartShiftSnapshot(mode.WheelMode == SmartShiftWheelMode.Ratchet, mode.AutoDisengage, 0, false);
            }
        }
        catch { /* fall through */ }
        return null;
    }

    /// <summary>Apply SmartShift wheel mode + auto-disengage threshold. Writes to the device.</summary>
    public async Task<bool> ApplySmartShiftAsync(bool ratchet, byte autoDisengage)
    {
        var mode = ratchet ? SmartShiftWheelMode.Ratchet : SmartShiftWheelMode.Freespin;
        try
        {
            if (_device.GetFeature<SmartShiftEnhancedFeature>() is { } enhanced)
            {
                await enhanced.SetRatchetControlModeAsync(
                    new SmartShiftEnhancedStatusChange(mode, autoDisengage == 0 ? null : autoDisengage)).ConfigureAwait(false);
                return true;
            }
            if (_device.GetFeature<SmartShiftFeature>() is { } basic)
            {
                await basic.SetRatchetControlModeAsync(mode, autoDisengage == 0 ? null : autoDisengage).ConfigureAwait(false);
                return true;
            }
        }
        catch { /* ignore */ }
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownsChannel) await _channel.DisposeAsync().ConfigureAwait(false);
    }
}
