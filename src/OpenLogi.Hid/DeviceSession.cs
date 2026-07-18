using System.Threading.Channels;
using OpenLogi.Core.DeviceInfo;
using OpenLogi.Core.Gestures;
using OpenLogi.HidPP.Channel;
using OpenLogi.HidPP.Device;
using OpenLogi.HidPP.Feature;
using OpenLogi.HidPP.Protocol;
using OpenLogi.HidPP.Receiver;
// Alias only what's needed from Core: a blanket `using OpenLogi.Core` would make the
// unqualified `Action` (used by StartDpiButtonCaptureAsync) ambiguous with System.Action.
using ButtonId = OpenLogi.Core.Config.ButtonId;
using DiagnosticLog = OpenLogi.Core.Logging.DiagnosticLog;

namespace OpenLogi.Hid;

/// <summary>Current sensor DPI plus the values the device supports.</summary>
public sealed record DpiSnapshot(ushort Current, IReadOnlyList<ushort> Supported);

/// <summary>Current SmartShift wheel state.</summary>
public sealed record SmartShiftSnapshot(bool Ratchet, byte AutoDisengage, byte TunableTorque, bool TorqueSupported);

/// <summary>One host slot's detail.</summary>
public sealed record HostDetail(int Index, bool IsCurrent, bool Paired, string BusType, string? Name);

/// <summary>A profile's stored lighting descriptor (first LED zone): effect byte, colour, and Breathing/Cycle period+brightness.</summary>
public readonly record struct ProfileLighting(byte Effect, byte R, byte G, byte B, ushort PeriodMs, byte Brightness);

/// <summary>Host (EasySwitch) state: count, current slot (0-based), per-slot detail, and whether slots can be cleared.</summary>
public sealed record HostSnapshot(byte HostCount, byte CurrentHost, IReadOnlyList<HostDetail> Hosts, bool SupportsDelete);

/// <summary>
/// An open HID++ session to one device, used by the GUI to read/apply DPI and
/// SmartShift. Holds the channel + an enumerated <see cref="HidppDevice"/> for the
/// device's lifetime in the UI; dispose when the selection changes.
///
/// HARDWARE-VERIFIED read path uses the same engine the CLI exercises; apply
/// writes to the physical device (only on explicit user action).
/// </summary>
public sealed class DeviceSession : IAsyncDisposable
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
    /// diverted-button captures below.
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
            }, _cts.Token);
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

    // ── Lighting ────────────────────────────────────────────────────────────

    /// <summary>Whether the device exposes a supported lighting engine (0x8070 or 0x8071).</summary>
    public bool SupportsLighting =>
        _device.GetFeature<ColorLedEffectsFeature>() is not null
        || _device.GetFeature<RgbEffectsFeature>() is not null;

    /// <summary>
    /// Set a solid keyboard colour: prefer ColorLedEffects (0x8070); fall back to
    /// PerKeyLighting v2 (0x8081, newer G-series like the G915 — HARDWARE-VERIFIED);
    /// else RgbEffects (0x8071). Returns false if the device exposes none.
    /// </summary>
    public async Task<bool> ApplyLightingAsync(byte r, byte g, byte b)
    {
        try
        {
            if (_device.GetFeature<ColorLedEffectsFeature>() is { } led)
                return await ApplyColorLedEffectsAsync(led, r, g, b).ConfigureAwait(false);

            // 0x8081 (PerKeyLighting v2) — works on the G915 via host mode; prefer it
            // over 0x8071 (which the onboard profile replays over).
            if (await ApplyPerKeyColorAsync(r, g, b).ConfigureAwait(false))
                return true;

            if (_device.GetFeature<RgbEffectsFeature>() is { } rgb)
                return await ApplyRgbEffectsAsync(rgb, r, g, b).ConfigureAwait(false);
        }
        catch { /* fall through */ }
        return false;
    }

    private static async Task<bool> ApplyColorLedEffectsAsync(ColorLedEffectsFeature led, byte r, byte g, byte b)
    {
        var zoneCount = await led.GetZoneCountAsync().ConfigureAwait(false);
        var zones = zoneCount == 0 ? (byte)4 : Math.Min(zoneCount, (byte)4);
        var p = new byte[ColorLedEffectsFeature.ZoneEffectParamCount];
        p[0] = r; p[1] = g; p[2] = b;
        for (byte zone = 0; zone < zones; zone++)
        {
            await led.SetZoneEffectAsync(zone, ColorLedEffectsFeature.EffectFixedColor, p,
                ColorLedEffectsFeature.PersistenceVolatile).ConfigureAwait(false);
            await Task.Delay(8).ConfigureAwait(false);
        }
        return true;
    }

    private static async Task<bool> ApplyRgbEffectsAsync(RgbEffectsFeature rgb, byte r, byte g, byte b)
    {
        // 0x8071 needs software control first; then set each real cluster
        // (0xff "all clusters" is a read-only selector — invalid for set).
        // NB: on G-series keyboards with an onboard profile (e.g. the G915)
        // the firmware replays the stored profile over these writes; live
        // colour there needs host mode + 0x8081 per-key streaming, not yet
        // implemented, so we don't switch host mode here (it would just go dark).
        await rgb.SetSwControlAsync(RgbEffectsFeature.SwControlAllClusters).ConfigureAwait(false);
        var p = new byte[RgbEffectsFeature.ClusterEffectParamCount];
        p[0] = r; p[1] = g; p[2] = b;
        var applied = false;
        for (byte cluster = 0; cluster < 4; cluster++)
        {
            try
            {
                await rgb.SetRgbClusterEffectAsync(cluster, RgbEffectsFeature.EffectFixed, p,
                    RgbEffectsFeature.PersistenceVolatile, RgbEffectsFeature.PowerFull).ConfigureAwait(false);
                applied = true;
                await Task.Delay(8).ConfigureAwait(false);
            }
            catch (Hidpp20Exception) { /* cluster doesn't exist on this device */ }
        }
        return applied;
    }

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

    // ── Brightness + lighting effects ────────────────────────────────────────

    /// <summary>
    /// Switch a G-series device to host (software) mode via OnboardProfiles
    /// (0x8100) if present, so software lighting writes aren't overridden by the
    /// replayed onboard profile. No-op for devices without 0x8100.
    /// </summary>
    public async Task EnsureHostModeAsync()
    {
        if (_device.GetFeature<OnboardProfilesFeature>() is not { } ob) return;
        try { await ob.SetModeAsync(OnboardProfilesFeature.ModeHost).ConfigureAwait(false); }
        catch { /* not all 0x8100 implementations allow the switch */ }
    }

    /// <summary>Read onboard-profile info (count + current), or <c>null</c> if no 0x8100.</summary>
    public Task<(OnboardProfilesInfo Info, byte Current)?> ReadProfilesAsync() => RetryStructAsync(ReadProfilesOnceAsync);

    private async Task<(OnboardProfilesInfo Info, byte Current)?> ReadProfilesOnceAsync()
    {
        if (_device.GetFeature<OnboardProfilesFeature>() is not { } ob) return null;

        // getCurrentProfile only answers in onboard mode; default to 0 ("No profile")
        // when it can't be read so it doesn't sink the whole read.
        async Task<(OnboardProfilesInfo Info, byte Current)> ReadOnceAsync()
        {
            var info = await ob.GetInfoAsync().ConfigureAwait(false);
            byte current;
            try { current = await ob.GetCurrentProfileAsync().ConfigureAwait(false); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"ReadProfiles: getCurrentProfile failed: {ex}"); current = 0; }
            return (info, current);
        }

        // Try in the current mode first (no side effects if it answers).
        try
        {
            if (await ReadOnceAsync().ConfigureAwait(false) is { Info.ProfileCount: > 0 } ok)
                return ok;
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"ReadProfiles: read in current mode failed, trying onboard: {ex}"); }

        // The device may be stuck in host mode (after the editor / solid lighting),
        // where the profile read can fail or report nothing. Switch to onboard, read,
        // then restore host mode so the lighting mode is left as it was.
        try
        {
            byte mode;
            try { mode = await ob.GetModeAsync().ConfigureAwait(false); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"ReadProfiles: getMode failed: {ex}"); return null; }
            if (mode != OnboardProfilesFeature.ModeHost) return null; // already onboard; nothing else to try

            await ob.SetModeAsync(OnboardProfilesFeature.ModeOnboard).ConfigureAwait(false);
            try { return await ReadOnceAsync().ConfigureAwait(false); }
            finally
            {
                try { await ob.SetModeAsync(OnboardProfilesFeature.ModeHost).ConfigureAwait(false); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"ReadProfiles: restore host mode failed: {ex}"); }
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"ReadProfiles: onboard-mode read failed: {ex}"); return null; }
    }

    /// <summary>Read 16 bytes at sector/offset (OnboardProfiles readMemory), or <c>null</c> if no 0x8100.</summary>
    public async Task<byte[]?> ReadMemoryRawAsync(ushort sector, ushort offset)
    {
        if (_device.GetFeature<OnboardProfilesFeature>() is not { } ob) return null;
        return await ob.ReadMemoryAsync(sector, offset).ConfigureAwait(false);
    }

    /// <summary>
    /// Read a profile sector, tolerant of the final partial 16-byte chunk past the
    /// sector size. Returns whatever was read (the device's readable range), or
    /// <c>null</c> if no 0x8100 / nothing read. Groundwork for profile editing.
    /// </summary>
    public async Task<byte[]?> ReadProfileSectorAsync(ushort sectorId)
    {
        if (_device.GetFeature<OnboardProfilesFeature>() is not { } ob) return null;
        var data = new byte[256];
        var got = 0;
        for (var off = 0; off < 256; off += 16)
        {
            try { Array.Copy(await ob.ReadMemoryAsync(sectorId, (ushort)off).ConfigureAwait(false), 0, data, off, 16); got = off + 16; }
            catch { break; }
        }
        return got == 0 ? null : data[..got];
    }

    /// <summary>
    /// Read a full sector of <paramref name="size"/> bytes, clamping the final read
    /// offset so it never runs past the sector (which errors). <c>null</c> if no 0x8100.
    /// </summary>
    public async Task<byte[]?> ReadFullSectorAsync(ushort sectorId, int size)
    {
        if (_device.GetFeature<OnboardProfilesFeature>() is not { } ob) return null;
        var data = new byte[size];
        var off = 0;
        while (off < size)
        {
            var readOff = Math.Min(off, size - 16);
            var chunk = await ob.ReadMemoryAsync(sectorId, (ushort)readOff).ConfigureAwait(false);
            var copyStart = off - readOff;
            var copyLen = Math.Min(16 - copyStart, size - off);
            Array.Copy(chunk, copyStart, data, off, copyLen);
            off += copyLen;
        }
        return data;
    }

    // Format-4 (G915) G-key bindings: five 4-byte entries [0x80, 0x02, modifier,
    // HID-usage] starting at sector offset 0x20 (G1..G5). HARDWARE-VERIFIED:
    // patching the usage byte remapped G1 (F1 → 'a').
    private const int GKeyBindingsOffset = 0x20;
    public const int GKeyCount = 5;

    /// <summary>Whether the device exposes the GKeys feature (0x8010).</summary>
    public bool SupportsGKeys => _device.FeatureIndex(0x8010) is not null;

    /// <summary>Whether the device exposes OnboardProfiles (0x8100) — a keyboard's real control interface.</summary>
    public bool SupportsOnboardProfiles => _device.FeatureIndex(0x8100) is not null;

    /// <summary>Read the five G-keys' current (modifier, usage) from a profile sector, or <c>null</c>.</summary>
    public Task<(byte Modifier, byte Usage)[]?> ReadGKeyBindingsAsync(ushort profileSector) =>
        RetryAsync(() => ReadGKeyBindingsOnceAsync(profileSector));

    private async Task<(byte Modifier, byte Usage)[]?> ReadGKeyBindingsOnceAsync(ushort profileSector)
    {
        var data = await ReadFullSectorAsync(profileSector, 255).ConfigureAwait(false);
        if (data is null) return null;
        var bindings = new (byte, byte)[GKeyCount];
        for (var i = 0; i < GKeyCount; i++)
        {
            var off = GKeyBindingsOffset + i * 4;
            bindings[i] = (data[off + 2], data[off + 3]);
        }
        return bindings;
    }

    /// <summary>Remap one G-key (0-based) to a HID keyboard usage in a profile (persists on-device).</summary>
    public async Task<bool> SetGKeyUsageAsync(ushort profileSector, int gkeyIndex, byte usage, byte modifier = 0)
    {
        if (gkeyIndex < 0 || gkeyIndex >= GKeyCount) return false;
        var data = await ReadFullSectorAsync(profileSector, 255).ConfigureAwait(false);
        if (data is null) return false;
        var off = GKeyBindingsOffset + gkeyIndex * 4;
        data[off] = 0x80; data[off + 1] = 0x02; data[off + 2] = modifier; data[off + 3] = usage;
        return await WriteProfileSectorAsync(profileSector, data).ConfigureAwait(false);
    }

    // Format-4 (G915) profile lighting: three 11-byte effect descriptors
    // [effect, R, G, B, …] at these sector offsets. effect 0x01=fixed, 0x03=cycle,
    // 0x0a=breathing. HARDWARE-VERIFIED: patching these to 01/RGB sets a persistent
    // device-side solid colour. (Offsets are device-format-specific.)
    private static readonly int[] ProfileLedOffsets = [0xD0, 0xDB, 0xE6];

    /// <summary>
    /// Set a profile's stored lighting to a solid colour (persists on the device,
    /// runs even after the app closes). Reads the sector, patches the LED
    /// descriptors, recomputes the CRC and writes. <c>false</c> if no 0x8100.
    /// </summary>
    public async Task<bool> SetProfileSolidColorAsync(ushort profileSector, byte r, byte g, byte b) =>
        await SetProfileEffectAsync(profileSector, 0x01, r, g, b, 0, 100).ConfigureAwait(false);

    /// <summary>Effect bytes for a profile lighting descriptor.</summary>
    public const byte EffectOff = 0x00, EffectFixed = 0x01, EffectCycle = 0x03, EffectBreathing = 0x0a;

    /// <summary>
    /// Write a lighting effect into a profile's three descriptors (persists,
    /// device-side). Descriptor layout (hardware-probed): byte0=effect; Fixed
    /// [r,g,b]; Breathing [r,g,b, period(2 BE ms), brightness]; Cycle keeps the
    /// device's default speed + brightness. <paramref name="brightness"/> is 0–100.
    /// </summary>
    public async Task<bool> SetProfileEffectAsync(ushort profileSector, byte effect, byte r, byte g, byte b, ushort periodMs, byte brightness)
    {
        var data = await ReadFullSectorAsync(profileSector, 255).ConfigureAwait(false);
        if (data is null) return false;
        var desc = new byte[11];
        desc[0] = effect;
        switch (effect)
        {
            case EffectFixed:
                desc[1] = r; desc[2] = g; desc[3] = b;
                break;
            case EffectBreathing:
                desc[1] = r; desc[2] = g; desc[3] = b;
                desc[4] = (byte)(periodMs >> 8); desc[5] = (byte)(periodMs & 0xff);
                desc[6] = brightness;
                break;
            case EffectCycle:
                desc[6] = 0x08; desc[7] = 0x34; // device default cycle period
                desc[8] = brightness;
                break;
            // EffectOff: all zero.
        }
        foreach (var off in ProfileLedOffsets)
            Array.Copy(desc, 0, data, off, 11);
        return await WriteProfileSectorAsync(profileSector, data).ConfigureAwait(false);
    }

    /// <summary>
    /// Read a profile's stored lighting descriptor (the first LED zone), the inverse
    /// of <see cref="SetProfileEffectAsync"/>. <c>null</c> if no 0x8100 / unreadable.
    /// Period/brightness are only meaningful for Breathing/Cycle (see the write layout).
    /// </summary>
    public async Task<ProfileLighting?> ReadProfileEffectAsync(ushort profileSector)
    {
        var data = await ReadFullSectorAsync(profileSector, 255).ConfigureAwait(false);
        if (data is null) return null;
        var off = ProfileLedOffsets[0];
        var effect = data[off];
        var (period, brightness) = effect switch
        {
            EffectBreathing => ((ushort)((data[off + 4] << 8) | data[off + 5]), data[off + 6]),
            EffectCycle => ((ushort)((data[off + 6] << 8) | data[off + 7]), data[off + 8]),
            _ => ((ushort)0, (byte)100),
        };
        return new ProfileLighting(effect, data[off + 1], data[off + 2], data[off + 3], period, brightness);
    }

    /// <summary>
    /// Write a 255-byte profile sector back to flash, recomputing the trailing
    /// CRC-16 over [0..253] first. Streams in 16-byte chunks (startWrite →
    /// writeMemory… → endWrite). DESTRUCTIVE: corrupts the profile if the layout
    /// is wrong (recoverable by rewriting from G HUB). <c>false</c> if no 0x8100.
    /// </summary>
    public async Task<bool> WriteProfileSectorAsync(ushort sectorId, byte[] sector)
    {
        if (_device.GetFeature<OnboardProfilesFeature>() is not { } ob) return false;
        if (sector.Length < 255) return false;
        var crc = OpenLogi.HidPP.Crc16.Ccitt(sector.AsSpan(0, 253));
        sector[253] = (byte)(crc >> 8);
        sector[254] = (byte)(crc & 0xff);
        try
        {
            await ob.StartWriteAsync(sectorId, 0, 255).ConfigureAwait(false);
            for (var off = 0; off < 255; off += 16)
                await ob.WriteMemoryAsync(sector.AsMemory(off, Math.Min(16, 255 - off))).ConfigureAwait(false);
            await ob.EndWriteAsync().ConfigureAwait(false);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Switch the active onboard profile (1-based), or false if unsupported.</summary>
    public async Task<bool> SwitchProfileAsync(byte profileIndex)
    {
        if (_device.GetFeature<OnboardProfilesFeature>() is not { } ob) return false;
        try
        {
            // setCurrentProfile only works (and getCurrentProfile only reads) in onboard mode.
            await ob.SetModeAsync(OnboardProfilesFeature.ModeOnboard).ConfigureAwait(false);
            await ob.SetCurrentProfileAsync(profileIndex).ConfigureAwait(false);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Set the device to host or onboard mode via OnboardProfiles (0x8100).</summary>
    public async Task<bool> SetOnboardModeAsync(bool host)
    {
        if (_device.GetFeature<OnboardProfilesFeature>() is not { } ob) return false;
        try
        {
            await ob.SetModeAsync(host ? OnboardProfilesFeature.ModeHost : OnboardProfilesFeature.ModeOnboard).ConfigureAwait(false);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Whether the device exposes BrightnessControl (0x8040).</summary>
    public bool SupportsBrightness => _device.GetFeature<BrightnessControlFeature>() is not null;

    /// <summary>Read the backlight brightness range + current value, or <c>null</c> if unsupported.</summary>
    public async Task<(BrightnessInfo Info, ushort Current)?> ReadBrightnessAsync()
    {
        if (_device.GetFeature<BrightnessControlFeature>() is not { } bc) return null;
        try { return (await bc.GetInfoAsync().ConfigureAwait(false), await bc.GetBrightnessAsync().ConfigureAwait(false)); }
        catch { return null; }
    }

    /// <summary>Set the backlight brightness (0..max).</summary>
    public async Task<bool> ApplyBrightnessAsync(ushort brightness)
    {
        if (_device.GetFeature<BrightnessControlFeature>() is not { } bc) return false;
        try { await bc.SetBrightnessAsync(brightness).ConfigureAwait(false); return true; }
        catch { return false; }
    }

    /// <summary>Whether the device exposes PerKeyLighting v2 (0x8081).</summary>
    public bool SupportsPerKey => _device.GetFeature<PerKeyLightingFeature>() is not null;

    /// <summary>
    /// Set a solid colour via PerKeyLighting v2 (0x8081): switch to host mode (so
    /// the onboard profile stops replaying), fill the whole zone range with the
    /// colour, then commit the frame. The session must stay open to hold it.
    /// </summary>
    public async Task<bool> ApplyPerKeyColorAsync(byte r, byte g, byte b)
    {
        if (_device.GetFeature<PerKeyLightingFeature>() is not { } pk) return false;
        try
        {
            await EnsureHostModeAsync().ConfigureAwait(false);
            // One range covering every addressable zone; absent zones are ignored.
            await pk.SetRangeRgbZonesAsync([new RgbZoneRange(0x00, 0xfe, r, g, b)]).ConfigureAwait(false);
            await pk.FrameEndAsync(PerKeyLightingFeature.FramePersistenceVolatile).ConfigureAwait(false);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Fast per-frame colour fill for animations: fill all zones + commit, WITHOUT
    /// the host-mode switch (call <see cref="ApplyPerKeyColorAsync"/> once first to
    /// enter host mode). Returns false if the device has no 0x8081.
    /// </summary>
    public async Task<bool> ApplyPerKeyColorFastAsync(byte r, byte g, byte b)
    {
        if (_device.GetFeature<PerKeyLightingFeature>() is not { } pk) return false;
        try
        {
            await pk.SetRangeAllFastAsync(r, g, b).ConfigureAwait(false);
            await pk.FrameEndFastAsync(PerKeyLightingFeature.FramePersistenceVolatile).ConfigureAwait(false);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Light a single per-key zone (host mode, others left dark). Used to probe the
    /// zone-id → key mapping. Returns false if no 0x8081.
    /// </summary>
    public async Task<bool> SetKeyZoneAsync(byte zoneId, byte r, byte g, byte b)
    {
        if (_device.GetFeature<PerKeyLightingFeature>() is not { } pk) return false;
        try
        {
            await EnsureHostModeAsync().ConfigureAwait(false);
            await pk.SetSingleValueAsync(r, g, b, [zoneId]).ConfigureAwait(false);
            await pk.FrameEndAsync(PerKeyLightingFeature.FramePersistenceVolatile).ConfigureAwait(false);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Update a single per-key zone and commit, WITHOUT re-entering host mode or
    /// clearing — other keys keep their colors (call <see cref="ApplyPerKeyColorAsync"/>
    /// once first to set a base + enter host mode). For the press-to-color editor.
    /// </summary>
    public async Task<bool> SetZoneAsync(byte zone, byte r, byte g, byte b)
    {
        if (_device.GetFeature<PerKeyLightingFeature>() is not { } pk) return false;
        try
        {
            await pk.SetSingleValueAsync(r, g, b, [zone]).ConfigureAwait(false);
            await pk.FrameEndAsync(PerKeyLightingFeature.FramePersistenceVolatile).ConfigureAwait(false);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Apply a full per-key color map (zone id → RGB) live via 0x8081: host mode,
    /// fill all zones dark, then set each requested zone, then commit. Returns false
    /// if no 0x8081.
    /// </summary>
    public async Task<bool> ApplyPerKeyMapAsync(IReadOnlyDictionary<byte, (byte R, byte G, byte B)> map)
    {
        if (_device.GetFeature<PerKeyLightingFeature>() is not { } pk) return false;
        try
        {
            await EnsureHostModeAsync().ConfigureAwait(false);
            await pk.SetRangeRgbZonesAsync([new RgbZoneRange(0x00, 0xfe, 0, 0, 0)]).ConfigureAwait(false); // clear to dark (awaited)
            foreach (var (zone, c) in map)
                await pk.SetSingleValueAsync(c.R, c.G, c.B, [zone]).ConfigureAwait(false);
            await pk.FrameEndAsync(PerKeyLightingFeature.FramePersistenceVolatile).ConfigureAwait(false);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Apply a per-key color map over a solid base via 0x8081: host mode, fill every
    /// zone with the base color, then set each mapped (painted) zone, then commit.
    /// Restores the saved "No profile" per-key look (base = unpainted-key color)
    /// without opening the editor. Returns false if no 0x8081.
    /// </summary>
    public async Task<bool> ApplyPerKeyMapAsync(byte baseR, byte baseG, byte baseB, IReadOnlyDictionary<byte, (byte R, byte G, byte B)> map)
    {
        if (_device.GetFeature<PerKeyLightingFeature>() is not { } pk) return false;
        try
        {
            await EnsureHostModeAsync().ConfigureAwait(false);
            await pk.SetRangeRgbZonesAsync([new RgbZoneRange(0x00, 0xfe, baseR, baseG, baseB)]).ConfigureAwait(false); // base fill
            foreach (var (zone, c) in map)
                await pk.SetSingleValueAsync(c.R, c.G, c.B, [zone]).ConfigureAwait(false);
            await pk.FrameEndAsync(PerKeyLightingFeature.FramePersistenceVolatile).ConfigureAwait(false);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Enumerate cluster 0's effects as (index, effectId), or <c>null</c> if unsupported.</summary>
    public async Task<IReadOnlyList<(byte Index, ushort EffectId)>?> EnumerateEffectsAsync()
    {
        if (_device.GetFeature<RgbEffectsFeature>() is not { } rgb) return null;
        try
        {
            var count = await rgb.GetEffectCountAsync(0).ConfigureAwait(false);
            var list = new List<(byte, ushort)>(count);
            for (byte i = 0; i < count; i++)
                list.Add((i, await rgb.GetEffectIdAsync(0, i).ConfigureAwait(false)));
            return list;
        }
        catch { return null; }
    }

    /// <summary>
    /// Apply an RGB effect by its cluster-effect index with raw params, to every
    /// cluster (0..3). Takes software control first. Returns false if no 0x8071.
    /// </summary>
    public async Task<bool> ApplyEffectAsync(byte effectIndex, byte[] effectParams, byte persistence = RgbEffectsFeature.PersistenceVolatile, bool takeSwControl = true)
    {
        if (_device.GetFeature<RgbEffectsFeature>() is not { } rgb) return false;
        try
        {
            await EnsureHostModeAsync().ConfigureAwait(false);
            if (takeSwControl)
                await rgb.SetSwControlAsync(RgbEffectsFeature.SwControlAllClusters).ConfigureAwait(false);
            var applied = false;
            for (byte cluster = 0; cluster < 4; cluster++)
            {
                try
                {
                    await rgb.SetRgbClusterEffectAsync(cluster, effectIndex, effectParams,
                        persistence, RgbEffectsFeature.PowerFull).ConfigureAwait(false);
                    applied = true;
                    await Task.Delay(8).ConfigureAwait(false);
                }
                catch (Hidpp20Exception) { /* cluster doesn't exist */ }
            }
            return applied;
        }
        catch { return false; }
    }

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

    public async ValueTask DisposeAsync()
    {
        if (_ownsChannel) await _channel.DisposeAsync().ConfigureAwait(false);
    }

    // ── Diverted-button capture (ReprogControls 0x1b04) ─────────────────────────

    /// <summary>
    /// The "DPI / ModeShift" button control-ID family. Whichever a device exposes
    /// and can divert is captured and surfaced as <see cref="ButtonId.DpiToggle"/>.
    /// Values from the 0x1b04 control list (ported from the Rust original).
    /// </summary>
    private static readonly ushort[] DpiModeShiftCids = [0x00c4, 0x00ed, 0x00fd];

    /// <summary>
    /// Divert the device's DPI/ModeShift button over 0x1b04 so it stops doing its
    /// native function and instead notifies us when pressed; <paramref name="onPressed"/>
    /// fires on each rising edge. Returns a handle that restores the button's
    /// native behaviour when disposed, or <c>null</c> if the device has no
    /// divertable DPI button. Unlike Middle/Back/Forward (seen by the OS mouse
    /// hook), the DPI button is only reachable this way.
    /// </summary>
    public async Task<IAsyncDisposable?> StartDpiButtonCaptureAsync(Action onPressed)
    {
        if (_device.GetFeature<ReprogControlsFeature>() is not { } rc) return null;

        var diverted = new List<ushort>();
        try
        {
            var present = new HashSet<ushort>();
            var count = await rc.GetCountAsync().ConfigureAwait(false);
            for (byte i = 0; i < count; i++)
            {
                var info = await rc.GetCidInfoAsync(i).ConfigureAwait(false);
                if (DpiModeShiftCids.Contains(info.Cid.Value) && info.Flags.IsDivertable())
                    present.Add(info.Cid.Value);
            }
            foreach (var cid in present)
            {
                await rc.SetCidReportingAsync(new ControlId(cid),
                    CidReportingChange.TemporaryDiversion(diverted: true, rawXy: false)).ConfigureAwait(false);
                diverted.Add(cid);
            }
        }
        catch { /* best-effort; whatever diverted is handed back on dispose */ }

        if (diverted.Count == 0) { rc.Dispose(); return null; }
        DiagnosticLog.Info("capture",
            $"dpi button diverted: {string.Join(", ", diverted.Select(c => $"0x{c:x4}"))}");
        return new DpiButtonCapture(rc, diverted, _device.GetFeature<WirelessDeviceStatusFeature>(), onPressed);
    }

    /// <summary>
    /// Control-ID candidates for each button that can drive HID++ gestures, in the
    /// order they are offered as gesture owners. A device rarely exposes more than
    /// one candidate per button, so the first that is present and raw-XY-capable is
    /// used. <see cref="ButtonId.GestureButton"/> is the dedicated MX gesture button
    /// ("App Switch Gesture", 0x00c3); any other divertable raw-XY button — Middle /
    /// Back / Forward or the DPI/wheel-mode button — can be repurposed the same way:
    /// the swipe mechanism is identical, only the diverted control differs. (On
    /// Windows even the OS-hook buttons gesture over HID++, since the WH_MOUSE_LL
    /// hook carries no per-hold move deltas.)
    /// </summary>
    private static readonly (ButtonId Button, ushort[] Cids)[] GestureCandidates =
    [
        (ButtonId.GestureButton, [0x00c3]),
        (ButtonId.MiddleClick, [0x0052]),
        (ButtonId.Back, [0x0053]),
        (ButtonId.Forward, [0x0056]),
        (ButtonId.DpiToggle, [0x00c4, 0x00ed, 0x00fd]),
    ];

    /// <summary>
    /// Which of the gesture-capable buttons this device actually exposes as a
    /// present, raw-XY-capable, divertable control — the set offered as gesture
    /// owners in the UI, in <see cref="GestureCandidates"/> order. Empty when the
    /// device has no 0x1b04 feature or no eligible control.
    /// </summary>
    public async Task<IReadOnlyList<ButtonId>> GestureCapableButtonsAsync()
    {
        if (_device.GetFeature<ReprogControlsFeature>() is not { } rc) return [];
        using (rc)
        {
            var eligible = new List<ButtonId>();
            try
            {
                var count = await rc.GetCountAsync().ConfigureAwait(false);
                var present = new Dictionary<ushort, CidFlags>();
                for (byte i = 0; i < count; i++)
                {
                    var info = await rc.GetCidInfoAsync(i).ConfigureAwait(false);
                    present[info.Cid.Value] = info.Flags;
                }
                foreach (var (button, cids) in GestureCandidates)
                    if (cids.Any(c => present.TryGetValue(c, out var f) && f.SupportsRawXy() && f.IsDivertable()))
                        eligible.Add(button);
            }
            catch { /* device went away mid-scan — return what we have */ }
            return eligible;
        }
    }

    /// <summary>
    /// Divert the control behind <paramref name="owner"/> (see <see cref="GestureCandidates"/>)
    /// over 0x1b04 with raw-XY reporting, so a hold-and-swipe on that button is
    /// captured instead of moving the cursor. <paramref name="onGesture"/> fires once
    /// per committed swipe — the instant the direction commits mid-motion — and once
    /// with <see cref="GestureDirection.Click"/> for a plain press that
    /// never swiped. Returns a handle that restores the control's native behaviour
    /// when disposed, or <c>null</c> if the device exposes no raw-XY-capable control
    /// for that button. Mirrors the DPI-button capture; ported from the Rust gesture
    /// path in <c>openlogi-hid::gesture</c>.
    /// </summary>
    public async Task<IAsyncDisposable?> StartGestureCaptureAsync(
        ButtonId owner, System.Action<GestureDirection> onGesture)
    {
        var candidates = GestureCandidates.FirstOrDefault(gc => gc.Button == owner).Cids;
        if (candidates is null) return null;
        if (_device.GetFeature<ReprogControlsFeature>() is not { } rc) return null;

        ushort? cid = null;
        try
        {
            var count = await rc.GetCountAsync().ConfigureAwait(false);
            for (byte i = 0; i < count && cid is null; i++)
            {
                var info = await rc.GetCidInfoAsync(i).ConfigureAwait(false);
                // Only divert a control that actually reports raw-XY — without it
                // there is no swipe travel to read, only a plain click.
                if (candidates.Contains(info.Cid.Value) && info.Flags.SupportsRawXy())
                    cid = info.Cid.Value;
            }
            if (cid is { } c)
                await rc.SetCidReportingAsync(new ControlId(c),
                    CidReportingChange.TemporaryDiversion(diverted: true, rawXy: true)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Warn("capture", $"gesture: divert for {owner} failed: {ex.Message}");
            cid = null;
        }

        if (cid is not { } gestureCid)
        {
            DiagnosticLog.Info("capture", $"gesture: no raw-XY-capable control for {owner}");
            rc.Dispose();
            return null;
        }
        DiagnosticLog.Info("capture", $"gesture capture on {owner} (cid 0x{gestureCid:x4})");
        return new GestureCapture(rc, gestureCid, _device.GetFeature<WirelessDeviceStatusFeature>(), onGesture);
    }

    /// <summary>
    /// Interval between periodic divert re-assertions. Diversion is volatile device
    /// state — a napping/reconnecting device drops it (the same reset class as the
    /// DPI/scroll-invert loss on wake), and a BT-direct mouse's wake is invisible to
    /// the app, so the diverted buttons would silently act native ("gestures do
    /// nothing") until an unrelated rescan re-armed them. Each capture re-asserts its
    /// divert instantly on the device's 0x1d4b reconnect broadcast, and on this tick
    /// as insurance; both are idempotent, and a write to a napping device just fails
    /// until a later try.
    /// </summary>
    private static readonly TimeSpan DivertKeepalive = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Run <paramref name="reassert"/> whenever the device announces a wireless
    /// reconnect (instant heal) and every <see cref="DivertKeepalive"/> (insurance) —
    /// see <see cref="DivertKeepalive"/> for why. Failures are swallowed: the device
    /// is napping/unreachable and a later round retries.
    /// </summary>
    private static async Task ReassertDivertLoopAsync(
        ChannelReader<WirelessDeviceStatusFeature.StatusBroadcast>? wake,
        Func<Task> reassert, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (wake is not null)
                {
                    using var round = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    round.CancelAfter(DivertKeepalive);
                    try { await wake.ReadAsync(round.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* periodic tick */ }
                }
                else
                {
                    await Task.Delay(DivertKeepalive, ct).ConfigureAwait(false);
                }
                try { await reassert().ConfigureAwait(false); }
                catch { /* napping/unreachable — retried next round */ }
            }
        }
        catch (OperationCanceledException) { /* disposing */ }
    }

    /// <summary>
    /// Listens for diverted gesture-button events, runs the shared mid-swipe state
    /// machine, and restores the control on dispose. The device streams raw-XY
    /// deltas only while the button is held (raw-XY divert), so ordinary pointer
    /// motion never reaches the accumulator.
    /// </summary>
    private sealed class GestureCapture : IAsyncDisposable
    {
        private readonly ReprogControlsFeature _rc;
        private readonly ushort _cid;
        private readonly WirelessDeviceStatusFeature? _wireless;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _pump;
        private readonly Task _keepalive;

        public GestureCapture(
            ReprogControlsFeature rc, ushort cid, WirelessDeviceStatusFeature? wireless,
            System.Action<GestureDirection> onGesture)
        {
            _rc = rc;
            _cid = cid;
            _wireless = wireless;
            _pump = PumpAsync(rc.Listen(), onGesture, _cts.Token);
            _keepalive = ReassertDivertLoopAsync(wireless?.Listen(),
                () => _rc.SetCidReportingAsync(new ControlId(_cid),
                    CidReportingChange.TemporaryDiversion(diverted: true, rawXy: true)),
                _cts.Token);
        }

        private async Task PumpAsync(
            ChannelReader<ReprogControlsEvent> reader,
            System.Action<GestureDirection> onGesture,
            CancellationToken ct)
        {
            var swipe = new SwipeAccumulator();
            try
            {
                await foreach (var ev in reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    switch (ev)
                    {
                        case ReprogControlsEvent.DivertedButtons db:
                        {
                            // Begin a hold on the rising edge of the gesture button. On the
                            // falling edge End() settles the hold: a swipe whose travel never
                            // got a post-gate event to commit on (a quick flick), else Click.
                            var held = db.Controls.Any(c => c.Value == _cid);
                            if (held && !swipe.IsHolding)
                                swipe.Begin();
                            else if (!held && swipe.IsHolding && swipe.End() is { } end)
                                onGesture(end);
                            break;
                        }
                        case ReprogControlsEvent.DivertedRawMouseXy xy:
                        {
                            // Commit the instant a clean direction emerges (mid-swipe, once
                            // per hold); the accumulator gates on hold duration internally.
                            if (swipe.Accumulate(xy.Dx, xy.Dy) is { } dir)
                                onGesture(dir);
                            break;
                        }
                    }
                }
            }
            catch (OperationCanceledException) { /* shutting down */ }
            catch (Exception ex)
            {
                // A dead pump means gestures silently stop — make it visible.
                DiagnosticLog.Warn("capture", $"gesture pump (cid 0x{_cid:x4}) ended: {ex.Message}");
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { await _pump.ConfigureAwait(false); } catch { /* already faulted/cancelled */ }
            try { await _keepalive.ConfigureAwait(false); } catch { /* cancellation */ }
            try
            {
                await _rc.SetCidReportingAsync(new ControlId(_cid),
                    CidReportingChange.TemporaryDiversion(diverted: false, rawXy: false)).ConfigureAwait(false);
                DiagnosticLog.Info("capture", $"gesture capture released (cid 0x{_cid:x4})");
            }
            catch { DiagnosticLog.Info("capture", $"gesture capture released (cid 0x{_cid:x4}, device gone)"); }
            _wireless?.Dispose();
            _rc.Dispose();
            _cts.Dispose();
        }
    }

    /// <summary>Listens for diverted DPI/ModeShift presses and restores the controls on dispose.</summary>
    private sealed class DpiButtonCapture : IAsyncDisposable
    {
        private readonly ReprogControlsFeature _rc;
        private readonly ushort[] _cids;
        private readonly WirelessDeviceStatusFeature? _wireless;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _pump;
        private readonly Task _keepalive;
        private bool _down;

        public DpiButtonCapture(
            ReprogControlsFeature rc, IEnumerable<ushort> cids, WirelessDeviceStatusFeature? wireless, Action onPressed)
        {
            _rc = rc;
            _cids = [.. cids];
            _wireless = wireless;
            _pump = PumpAsync(rc.Listen(), onPressed, _cts.Token);
            _keepalive = ReassertDivertLoopAsync(wireless?.Listen(), async () =>
            {
                foreach (var cid in _cids)
                    await _rc.SetCidReportingAsync(new ControlId(cid),
                        CidReportingChange.TemporaryDiversion(diverted: true, rawXy: false)).ConfigureAwait(false);
            }, _cts.Token);
        }

        private async Task PumpAsync(ChannelReader<ReprogControlsEvent> reader, Action onPressed, CancellationToken ct)
        {
            try
            {
                await foreach (var ev in reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    if (ev is not ReprogControlsEvent.DivertedButtons db) continue;
                    // The event carries the set of currently-held diverted controls;
                    // fire once on the press (rising) edge, not while held or on release.
                    var held = db.Controls.Any(c => _cids.Contains(c.Value));
                    if (held && !_down) onPressed();
                    _down = held;
                }
            }
            catch (OperationCanceledException) { /* shutting down */ }
            catch (Exception ex)
            {
                // A dead pump means the diverted button silently stops — make it visible.
                DiagnosticLog.Warn("capture", $"dpi button pump ended: {ex.Message}");
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { await _pump.ConfigureAwait(false); } catch { /* already faulted/cancelled */ }
            try { await _keepalive.ConfigureAwait(false); } catch { /* cancellation */ }
            foreach (var cid in _cids)
            {
                try
                {
                    await _rc.SetCidReportingAsync(new ControlId(cid),
                        CidReportingChange.TemporaryDiversion(diverted: false, rawXy: false)).ConfigureAwait(false);
                }
                catch { /* device gone — nothing to restore */ }
            }
            _wireless?.Dispose();
            _rc.Dispose();
            _cts.Dispose();
        }
    }
}
