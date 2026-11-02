using System.Runtime.InteropServices;
using OpenLogi.Camera.Interop;

namespace OpenLogi.Camera;

/// <summary>
/// Generic discovery and control of Logitech USB Video Class webcams on Windows.
/// Ported from Rust <c>openlogi-camera</c>.
///
/// Mice and keyboards speak HID++; webcams don't — every Logitech camera (Brio,
/// C920, StreamCam, C270, …) is a standard UVC device, so detection keys off the
/// USB vendor id alone, with no model table to maintain. Enumeration and capture
/// go through Media Foundation; the controls are the DirectShow
/// <c>IAMCameraControl</c> / <c>IAMVideoProcAmp</c> interfaces the capture source
/// implements. Control writes land in the camera's own registers, so every app
/// that opens the camera (Meet, Zoom, OBS) sees them.
/// </summary>
public static class Cameras
{
    public const ushort LogitechVendorId = UnnamedCameras.LogitechVendorId;

    // DirectShow property ids and flags.
    private const int CcPan = 0, CcTilt = 1, CcZoom = 3, CcExposure = 4, CcFocus = 6;
    private const int VpaBrightness = 0, VpaContrast = 1, VpaHue = 2, VpaSaturation = 3, VpaSharpness = 4;
    private const int VpaWhiteBalance = 7, VpaBacklightCompensation = 8, VpaGain = 9;
    private const int FlagAuto = 0x1, FlagManual = 0x2;

    /// <summary>Whether this platform has a capture and control backend.</summary>
    public static bool Supported => OperatingSystem.IsWindows();

    /// <summary>
    /// Enumerate every connected Logitech UVC camera and record each one's USB link.
    /// Non-Logitech cameras (built-in, virtual, other vendors) are filtered out.
    /// Empty off Windows or when Media Foundation is unavailable.
    /// </summary>
    public static Task<IReadOnlyList<CameraInfo>> EnumerateAsync()
    {
        if (!Supported) return Task.FromResult<IReadOnlyList<CameraInfo>>([]);
        return CameraWorker.Run<IReadOnlyList<CameraInfo>>(() =>
        {
            List<CameraInfo> cameras;
            try
            {
                cameras = [.. EnumerateAll()
                    .Where(c => c.VendorId == LogitechVendorId)
                    .Select(c => c.WithRegistryName())];
            }
            catch (Exception e) when (e is COMException or DllNotFoundException or EntryPointNotFoundException)
            {
                cameras = [];
            }
            var links = new Dictionary<string, UsbLink>();
            foreach (var camera in cameras)
                if (UsbHub.LinkOf(camera.UniqueId) is { } link)
                    links[camera.UniqueId] = link;
            UsbLinks.Remember(links);
            return cameras;
        });
    }

    /// <summary>How the camera is attached, as the latest enumeration found it. No I/O.</summary>
    public static UsbLink? UsbLinkFor(string uniqueId) => UsbLinks.For(uniqueId);

    /// <summary>Every supported control range and auto-toggle state, read in one device open.</summary>
    public static Task<CameraState> ReadStateAsync(string uniqueId) => CameraWorker.Run(() =>
    {
        using var dev = ControlDevice.Open(uniqueId);
        var controls = new List<(CameraControl, ControlRange)>();
        foreach (var control in CameraControlNames.AllControls)
            if (dev.Range(Prop(control)) is { } r)
                controls.Add((control, r.Range));
        var autos = new List<(AutoToggle, AutoState)>();
        foreach (var toggle in CameraControlNames.AllToggles)
        {
            var prop = Prop(toggle);
            if (dev.Range(prop) is { } r && (r.Caps & FlagAuto) != 0 && dev.Get(prop) is { } current)
                // DirectShow reports which modes exist but not a factory default;
                // auto-capable properties ship with auto engaged on every Logitech
                // camera, so that is the reset target.
                autos.Add((toggle, new AutoState((current.Flags & FlagAuto) != 0, true)));
        }
        return new CameraState(controls, autos);
    });

    /// <summary>Write a control's value (switching that property to manual).</summary>
    public static Task SetControlAsync(string uniqueId, CameraControl control, int value) => CameraWorker.Run(() =>
    {
        using var dev = ControlDevice.Open(uniqueId);
        dev.Set(Prop(control), value, FlagManual);
    });

    /// <summary>Switch an auto mode on or off, keeping the current value in place.</summary>
    public static Task SetAutoAsync(string uniqueId, AutoToggle toggle, bool on) => CameraWorker.Run(() =>
    {
        using var dev = ControlDevice.Open(uniqueId);
        dev.SetAuto(Prop(toggle), on);
    });

    /// <summary>
    /// Apply a batch of control values and auto toggles in a single device open.
    /// Every write is attempted; the first failure is rethrown afterwards so callers
    /// never persist a batch the hardware didn't take.
    ///
    /// Values go first: a DirectShow value write carries the manual flag, so writing
    /// it after an auto toggle would switch that auto mode straight back off (a
    /// profile that engages auto exposure would land with it disengaged). Toggles
    /// last leave each auto mode exactly as asked; turning one off keeps the value
    /// just written, since <c>SetAuto</c> re-asserts the current value.
    /// </summary>
    public static Task ApplySettingsAsync(
        string uniqueId,
        IReadOnlyList<(AutoToggle Toggle, bool On)> autos,
        IReadOnlyList<(CameraControl Control, int Value)> values) => CameraWorker.Run(() =>
    {
        using var dev = ControlDevice.Open(uniqueId);
        CameraControlException? first = null;
        foreach (var (control, value) in values)
            try { dev.Set(Prop(control), value, FlagManual); }
            catch (CameraControlException e) { first ??= e; }
        foreach (var (toggle, on) in autos)
            try { dev.SetAuto(Prop(toggle), on); }
            catch (CameraControlException e) { first ??= e; }
        if (first is not null) throw first;
    });

    /// <summary>
    /// Start a live stream (blocks until the first decoded frame, or throws
    /// <see cref="CaptureException"/>). Shared by default: it then runs at the mode
    /// another app is streaming, or the camera's default when none is. Exclusive
    /// streams at <paramref name="mode"/> (null: the camera's default mode) and keeps
    /// other apps out of the camera while it runs.
    /// </summary>
    public static CameraStream StartStream(string uniqueId, bool exclusive = false, StreamMode? mode = null) =>
        CameraStream.Start(uniqueId, exclusive, mode);

    /// <summary>The modes the camera offered when it was last opened (empty until a stream has opened).</summary>
    public static StreamModes StreamModesFor(string uniqueId) => CameraStream.KnownModes(uniqueId);

    /// <summary>Capture a single frame.</summary>
    public static Frame CaptureFrame(string uniqueId, TimeSpan timeout)
    {
        using var stream = CameraStream.Start(uniqueId, exclusive: false, mode: null);
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (stream.Snapshot() is { } frame) return frame;
            if (DateTime.UtcNow >= deadline) throw new CaptureException(CaptureError.Timeout);
            Thread.Sleep(30);
        }
    }

    // ── Enumeration ─────────────────────────────────────────────────────────

    private static List<CameraInfo> EnumerateAll()
    {
        var found = new List<CameraInfo>();
        foreach (var (activate, link) in VideoCaptureDevices())
        {
            try
            {
                if (link is null || DevicePath.ParseIds(link) is not { } ids) continue;
                var name = Com.AllocatedString(activate, Mf.DevSourceAttributeFriendlyName) ?? "Camera";
                found.Add(new CameraInfo(name, link, DevicePath.UsbSerial(link), ids.Vid, ids.Pid));
            }
            finally { Com.Release(activate); }
        }
        return found;
    }

    /// <summary>Every Media Foundation video-capture activate, with its symbolic link. Caller releases each.</summary>
    internal static List<(IMFActivate Activate, string? Link)> VideoCaptureDevices()
    {
        MfNative.EnsureStarted();
        var attributes = Com.CreateAttributes(1);
        var result = new List<(IMFActivate, string?)>();
        try
        {
            Com.Check(attributes.SetGUID(Mf.DevSourceAttributeSourceType, Mf.DevSourceAttributeSourceTypeVidcapGuid), "SetGUID");
            var attrPtr = Com.Pointer(attributes, Mf.IidMfAttributes);
            int hr;
            nint array;
            uint count;
            try { hr = MfNative.MFEnumDeviceSources(attrPtr, out array, out count); }
            finally { Marshal.Release(attrPtr); }
            Com.Check(hr, "MFEnumDeviceSources");
            try
            {
                for (var i = 0; i < count; i++)
                {
                    var p = Marshal.ReadIntPtr(array, i * IntPtr.Size);
                    if (p == 0) continue;
                    var activate = Com.Wrap<IMFActivate>(p);
                    result.Add((activate, Com.AllocatedString(activate, Mf.DevSourceAttributeSourceTypeVidcapSymbolicLink)));
                }
            }
            finally { Marshal.FreeCoTaskMem(array); }
        }
        finally { Com.Release(attributes); }
        return result;
    }

    /// <summary>
    /// Activate the media source of the camera whose symbolic link identifies the
    /// same physical device as <paramref name="uniqueId"/>. Exact device match only —
    /// guessing another camera could adjust the wrong hardware.
    /// </summary>
    /// <param name="shared">
    /// Open in Frame Server sharing mode, which leaves the camera's single
    /// controlling slot free for another app (Camera, Teams, …). A sharing source
    /// can still set the legacy camera / video-proc-amp controls, but must stream
    /// at whatever media type the camera is already running.
    /// </param>
    /// <returns>The source, or the failing HRESULT (0 when nothing matched).</returns>
    internal static (IMFMediaSource? Source, int Hr) ActivateSource(string uniqueId, bool shared)
    {
        IMFMediaSource? source = null;
        var hr = 0;
        foreach (var (activate, link) in VideoCaptureDevices())
        {
            try
            {
                if (source is not null || hr != 0 || link is null || !DevicePath.SameDevice(link, uniqueId)) continue;
                if (shared) _ = activate.SetUINT32(Mf.DevSourceAttributeFrameServerShareMode, 1);
                hr = activate.ActivateObject(Mf.IidMfMediaSource, out var p);
                if (!Com.Failed(hr)) { source = Com.Wrap<IMFMediaSource>(p); hr = 0; }
            }
            finally { Com.Release(activate); }
        }
        return (source, hr);
    }

    // ── Controls ────────────────────────────────────────────────────────────

    /// <summary>Which DirectShow interface carries a control, plus its property id (null: unsupported on Windows).</summary>
    private readonly record struct DsProp(bool Lens, int Id, bool Supported = true);

    private static DsProp Prop(CameraControl control) => control switch
    {
        CameraControl.Zoom => new(true, CcZoom),
        CameraControl.Pan => new(true, CcPan),
        CameraControl.Tilt => new(true, CcTilt),
        CameraControl.Focus => new(true, CcFocus),
        CameraControl.Exposure => new(true, CcExposure),
        CameraControl.Brightness => new(false, VpaBrightness),
        CameraControl.Contrast => new(false, VpaContrast),
        CameraControl.Saturation => new(false, VpaSaturation),
        CameraControl.Sharpness => new(false, VpaSharpness),
        CameraControl.Gain => new(false, VpaGain),
        CameraControl.BacklightCompensation => new(false, VpaBacklightCompensation),
        CameraControl.WhiteBalance => new(false, VpaWhiteBalance),
        CameraControl.Tint => new(false, VpaHue),
        // Power-line frequency and low-light compensation are UVC selectors that
        // DirectShow's two interfaces don't carry.
        _ => new(false, -1, Supported: false),
    };

    private static DsProp Prop(AutoToggle toggle) => toggle switch
    {
        AutoToggle.Focus => new(true, CcFocus),
        AutoToggle.Exposure => new(true, CcExposure),
        _ => new(false, VpaWhiteBalance),
    };

    /// <summary>A camera's activated capture source with the two control interfaces it may implement.</summary>
    private sealed class ControlDevice : IDisposable
    {
        private readonly IMFMediaSource _source;
        private readonly IAMCameraControl? _camera;
        private readonly IAMVideoProcAmp? _procAmp;

        private ControlDevice(IMFMediaSource source)
        {
            _source = source;
            // A camera without lens motors typically lacks IAMCameraControl.
            _camera = source as IAMCameraControl;
            _procAmp = source as IAMVideoProcAmp;
        }

        public static ControlDevice Open(string uniqueId)
        {
            // Sharing mode: a control write from a controlling source would be refused
            // while another app is streaming, and would lock that app out after.
            var (source, hr) = ActivateSource(uniqueId, shared: true);
            if (source is null)
                throw hr == 0
                    ? new CameraControlException(ControlError.NotFound)
                    : new CameraControlException(ControlError.Io, $"0x{hr:X8}");
            return new ControlDevice(source);
        }

        public (ControlRange Range, int Caps)? Range(DsProp prop)
        {
            if (!prop.Supported) return null;
            int hr, min, max, def, caps;
            if (prop.Lens)
            {
                if (_camera is null) return null;
                hr = _camera.GetRange(prop.Id, out min, out max, out _, out def, out caps);
            }
            else
            {
                if (_procAmp is null) return null;
                hr = _procAmp.GetRange(prop.Id, out min, out max, out _, out def, out caps);
            }
            if (Com.Failed(hr)) return null;
            var current = Get(prop)?.Value ?? def;
            return (new ControlRange(min, max, def, current), caps);
        }

        public (int Value, int Flags)? Get(DsProp prop)
        {
            if (!prop.Supported) return null;
            int hr, value, flags;
            if (prop.Lens)
            {
                if (_camera is null) return null;
                hr = _camera.Get(prop.Id, out value, out flags);
            }
            else
            {
                if (_procAmp is null) return null;
                hr = _procAmp.Get(prop.Id, out value, out flags);
            }
            return Com.Failed(hr) ? null : (value, flags);
        }

        public void Set(DsProp prop, int value, int flags)
        {
            if (!prop.Supported) throw new CameraControlException(ControlError.Unsupported);
            var hr = prop.Lens
                ? _camera?.Set(prop.Id, value, flags)
                : _procAmp?.Set(prop.Id, value, flags);
            if (hr is null || Com.Failed(hr.Value))
                throw new CameraControlException(ControlError.Unsupported);
        }

        public void SetAuto(DsProp prop, bool on) =>
            Set(prop, Get(prop)?.Value ?? 0, on ? FlagAuto : FlagManual);

        public void Dispose()
        {
            _ = _source.Shutdown();
            Com.Release(_source);
        }
    }
}
