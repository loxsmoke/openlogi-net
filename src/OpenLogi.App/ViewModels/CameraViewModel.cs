using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenLogi.Camera;
using OpenLogi.Core.Config;
using OpenLogi.Core.Localization;
using OpenLogi.Core.Logging;

namespace OpenLogi.App.ViewModels;

/// <summary>
/// The Camera tab of one webcam: live preview and the UVC controls with profiles.
/// Ported from the Rust <c>features/camera</c> (preview.rs, controls.rs).
///
/// The preview runs only while the tab is on screen (<see cref="SetActive"/>), so
/// the LED is off and the camera costs nothing otherwise. By default it shares the
/// camera, so other apps keep working while it shows; a sharing preview runs at
/// whatever mode the camera is in. Ticking "Use the camera exclusively" (offered
/// only while no other app controls the camera) makes the preview the camera's
/// controlling client, which unlocks the resolution / frame-rate picker of the
/// Rust app and keeps other apps out until it is unticked or the tab is left.
/// Control values are
/// written straight to the device — every app that opens the camera sees them —
/// and persisted per camera, then re-applied when the page next opens, since the
/// hardware only holds them until it loses power.
/// </summary>
public sealed partial class CameraViewModel : ObservableObject, IDisposable
{
    /// <summary>Wait after a failed controls read before trying again (camera seized or replugging).</summary>
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);

    /// <summary>A slider writes once it has been still this long, not on every intermediate value.</summary>
    private static readonly TimeSpan CommitDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Built-in profiles: values are fractions of each control's own range, so they
    /// scale to whatever the camera reports. Auto modes all engage — the point of a
    /// preset is a good picture without babysitting. Default restores device defaults.
    /// </summary>
    private static readonly (string Id, (CameraControl Control, double Fraction)[] Values)[] BuiltinProfiles =
    [
        ("default", []),
        ("streaming",
        [
            (CameraControl.Brightness, 0.50), (CameraControl.Contrast, 0.58),
            (CameraControl.Saturation, 0.62), (CameraControl.Sharpness, 0.60),
        ]),
        ("video_call",
        [
            (CameraControl.Brightness, 0.55), (CameraControl.Contrast, 0.52),
            (CameraControl.Saturation, 0.55), (CameraControl.Sharpness, 0.48),
        ]),
    ];

    private readonly Config _config;
    private readonly Action _save;
    private readonly Func<CameraValuePromptRequest, Task<int?>>? _valuePrompt;
    private readonly CancellationTokenSource _life = new();

    private readonly List<CameraControlRowViewModel> _rows = [];
    private readonly Dictionary<AutoToggle, AutoState> _autos = [];
    private readonly Dictionary<CameraControlRowViewModel, DispatcherTimer> _pendingCommits = [];
    private bool _controlsReady;

    private CameraStream? _stream;
    private int _streamGeneration;
    private bool _active;
    private int _framePostPending;
    private WriteableBitmap? _bufferA, _bufferB;
    private StreamMode? _currentMode;
    private bool _suppressModeEvents;
    private bool _suppressExclusive;

    public CameraViewModel(CameraInfo camera, Config config, Action save, Func<CameraValuePromptRequest, Task<int?>>? valuePrompt)
    {
        Info = camera;
        _config = config;
        _save = save;
        _valuePrompt = valuePrompt;
        UsbNotice = BuildUsbNotice();
        RebuildProfileChips();
        CameraUsage.Changed += OnCameraUsageChanged;
        UpdateOtherApp();
        _ = LoadControlsAsync();
    }

    public CameraInfo Info { get; }
    public string ConfigKey => Info.ConfigKey;

    // ── Preview state ───────────────────────────────────────────────────────

    /// <summary>The latest frame. Two bitmaps alternate so each frame is a new reference the Image redraws.</summary>
    [ObservableProperty] private Bitmap? _previewImage;

    /// <summary>A placeholder message over the preview (starting / failed); null while streaming.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreviewMessage))]
    private string? _previewMessage;

    public bool HasPreviewMessage => PreviewMessage is not null;

    [ObservableProperty] private string? _usbNotice;

    /// <summary>"1280 × 720, 30 fps" under the preview — what the camera is actually running.</summary>
    [ObservableProperty] private string? _activeModeText;

    /// <summary>
    /// The preview holds the camera exclusively (controlling mode), which lets it
    /// choose the resolution and frame rate and keeps other apps out meanwhile.
    /// Per page visit: every visit starts shared.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowModePicker), nameof(CanUseExclusive), nameof(ShowOtherAppMessage))]
    private bool _exclusiveAccess;

    /// <summary>Another app controls the camera (from the Windows sensor activity monitor).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUseExclusive), nameof(ShowOtherAppMessage))]
    private bool _otherAppControls;

    /// <summary>The checkbox is enabled while no other app controls the camera (or to untick it).</summary>
    public bool CanUseExclusive => ExclusiveAccess || !OtherAppControls;

    public bool ShowOtherAppMessage => OtherAppControls && !ExclusiveAccess;

    public ObservableCollection<CameraResolutionOption> Resolutions { get; } = [];
    public ObservableCollection<CameraRateOption> Rates { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowModePicker))]
    private bool _hasModes;

    /// <summary>The pickers appear only with exclusive access: a shared preview cannot choose.</summary>
    public bool ShowModePicker => ExclusiveAccess && HasModes;

    [ObservableProperty] private CameraResolutionOption? _selectedResolution;
    [ObservableProperty] private CameraRateOption? _selectedRate;

    // ── Controls state ──────────────────────────────────────────────────────

    public ObservableCollection<CameraControlRowViewModel> LensRows { get; } = [];
    public ObservableCollection<CameraControlRowViewModel> ImageRows { get; } = [];
    public ObservableCollection<CameraProfileChipViewModel> ProfileChips { get; } = [];

    /// <summary>The rows are built (the panel shows nothing while the first read runs).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowControls), nameof(ShowControlsUnavailable))]
    private bool _controlsLoaded;

    public bool ShowControls => ControlsLoaded && _rows.Count > 0;
    public bool ShowControlsUnavailable => ControlsLoaded && _rows.Count == 0;
    /// <summary>Section headers appear only when both sections have rows.</summary>
    public bool ShowSectionHeaders => LensRows.Count > 0 && ImageRows.Count > 0;

    // ── Preview lifecycle ───────────────────────────────────────────────────

    /// <summary>
    /// The single lifecycle switch: true while the Camera tab is on screen. Turning
    /// it off drops the stream, so the LED goes off and the camera leaves no CPU or
    /// memory use behind.
    /// </summary>
    public void SetActive(bool active)
    {
        if (_active == active) return;
        _active = active;
        if (active) StartPreview();
        else StopPreview();
    }

    private void StartPreview()
    {
        StopPreview();
        var generation = ++_streamGeneration;
        PreviewMessage = Loc.Current["Camera_StartingPreview"];
        var uid = Info.UniqueId;
        var exclusive = ExclusiveAccess;
        // Exclusive opens at the saved choice, else at the mode already showing, so
        // ticking the box doesn't jump the picture.
        var mode = exclusive ? SavedMode() ?? _currentMode : null;
        _ = Task.Run(() => OpenStream(uid, exclusive, mode)).ContinueWith(t =>
        {
            Dispatcher.UIThread.Post(() => OnStreamOpened(generation, t));
        }, TaskScheduler.Default);
    }

    /// <summary>Open, retrying a camera that is still busy releasing the previous stream.</summary>
    private static CameraStream OpenStream(string uid, bool exclusive, StreamMode? mode)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return Cameras.StartStream(uid, exclusive, mode); }
            catch (CaptureException e) when (attempt < 2 && e.Error is CaptureError.ResourcesUnavailable or CaptureError.Setup)
            {
                Thread.Sleep(400);
            }
        }
    }

    private void OnStreamOpened(int generation, Task<CameraStream> opened)
    {
        if (generation != _streamGeneration || !_active || _life.IsCancellationRequested)
        {
            // Disposing joins the reader thread: never on the UI thread.
            if (opened.IsCompletedSuccessfully) _ = Task.Run(opened.Result.Dispose);
            return;
        }
        if (!opened.IsCompletedSuccessfully)
        {
            var error = (opened.Exception?.InnerException as CaptureException)?.Error;
            DiagnosticLog.Warn("camera", $"preview start failed: {opened.Exception?.InnerException?.Message}");
            // Exclusive access refused: another app took control first. Share instead.
            if (ExclusiveAccess && error is CaptureError.InUse or CaptureError.ResourcesUnavailable)
            {
                OtherAppControls = true;
                SetExclusiveSilently(false);
                StartPreview();
                return;
            }
            PreviewImage = null;
            PreviewMessage = MessageFor(error);
            return;
        }
        var stream = opened.Result;
        _stream = stream;
        DiagnosticLog.Info("camera", $"preview open: {(stream.Exclusive ? "exclusive" : "shared")} at {stream.CurrentMode?.ToString() ?? "unknown mode"}");
        ShowMode(stream.CurrentMode);
        RebuildModePicker();
        stream.FrameArrived += () => OnFrameArrived(stream);
        stream.Failed += e => Dispatcher.UIThread.Post(() => OnStreamFailed(stream, e));
        // The scan that found the link may predate this open.
        UsbNotice = BuildUsbNotice();
    }

    private void OnStreamFailed(CameraStream stream, CaptureException e)
    {
        if (!ReferenceEquals(stream, _stream)) return;
        DiagnosticLog.Warn("camera", $"preview stream ended: {e.Message}");
        StopPreview();
        PreviewMessage = MessageFor(e.Error);
    }

    private static string MessageFor(CaptureError? error) => error switch
    {
        CaptureError.AccessDenied => Loc.Current["Camera_PreviewAccessDenied"],
        CaptureError.ResourcesUnavailable => Loc.Current["Camera_ResourcesUnavailable"],
        _ => Loc.Current["Camera_PreviewStartFailed"],
    };

    private void StopPreview()
    {
        _streamGeneration++;
        var stream = _stream;
        _stream = null;
        if (stream is not null) _ = Task.Run(stream.Dispose);
        PreviewImage = null;
        _bufferA = _bufferB = null;
    }

    /// <summary>Reader thread: coalesce frames into at most one pending UI update.</summary>
    private void OnFrameArrived(CameraStream stream)
    {
        if (Interlocked.Exchange(ref _framePostPending, 1) == 1) return;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _framePostPending, 0);
            if (ReferenceEquals(stream, _stream)) PresentFrame(stream);
        }, DispatcherPriority.Render);
    }

    private unsafe void PresentFrame(CameraStream stream)
    {
        var target = ReferenceEquals(PreviewImage, _bufferA) ? _bufferB : _bufferA;
        stream.WithLatest((bytes, width, height) =>
        {
            if (target is null || target.PixelSize.Width != width || target.PixelSize.Height != height)
            {
                target = new WriteableBitmap(new PixelSize((int)width, (int)height), new Vector(96, 96),
                    PixelFormat.Bgra8888, AlphaFormat.Opaque);
            }
            using var fb = target.Lock();
            var rowBytes = (int)width * 4;
            fixed (byte* src = bytes)
            {
                for (var y = 0; y < height; y++)
                    Buffer.MemoryCopy(src + (long)y * rowBytes, (byte*)fb.Address + (long)y * fb.RowBytes, fb.RowBytes, rowBytes);
            }
        });
        if (target is null) return;
        if (ReferenceEquals(PreviewImage, _bufferA)) _bufferB = target; else _bufferA = target;
        PreviewImage = target;
        PreviewMessage = null;
        // A controlling app can switch the camera's mode under a shared preview.
        if (stream.CurrentMode != _currentMode) ShowMode(stream.CurrentMode);
    }

    private void ShowMode(StreamMode? mode)
    {
        _currentMode = mode;
        ActiveModeText = mode is { } m ? Loc.Current.Format("Camera_ActiveMode", m.Width, m.Height, m.Fps) : null;
    }

    // ── Exclusive access, resolution and frame rate ─────────────────────────

    private void OnCameraUsageChanged() => Dispatcher.UIThread.Post(UpdateOtherApp);

    private void UpdateOtherApp()
    {
        if (_life.IsCancellationRequested) return;
        OtherAppControls = CameraUsage.OtherClients(Info.UniqueId).Any(c => c.Controlling);
    }

    partial void OnExclusiveAccessChanged(bool value)
    {
        DiagnosticLog.Info("camera", $"exclusive access {(value ? "on" : "off")}{(_suppressExclusive ? " (automatic)" : "")}, other app controls: {OtherAppControls}");
        if (_suppressExclusive) return;
        // Never take over a camera another app is already controlling.
        if (value && OtherAppControls)
        {
            SetExclusiveSilently(false);
            return;
        }
        if (_active) StartPreview();
    }

    private void SetExclusiveSilently(bool value)
    {
        _suppressExclusive = true;
        try { ExclusiveAccess = value; }
        finally { _suppressExclusive = false; }
    }

    private StreamMode? SavedMode() =>
        _config.CameraStreamMode(ConfigKey) is { } m ? new StreamMode(m.Width, m.Height, m.Fps) : null;

    private void RebuildModePicker()
    {
        var modes = Cameras.StreamModesFor(Info.UniqueId);
        _suppressModeEvents = true;
        try
        {
            Resolutions.Clear();
            Rates.Clear();
            HasModes = !modes.IsEmpty;
            if (modes.IsEmpty || modes.Resolve(_currentMode) is not { } mode) return;
            foreach (var (w, h) in modes.Resolutions())
                Resolutions.Add(new CameraResolutionOption(w, h));
            SelectedResolution = Resolutions.FirstOrDefault(r => r.Width == mode.Width && r.Height == mode.Height);
            foreach (var fps in modes.RatesAt(mode.Resolution))
                Rates.Add(new CameraRateOption(fps));
            SelectedRate = Rates.FirstOrDefault(r => r.Fps == mode.Fps);
        }
        finally { _suppressModeEvents = false; }
    }

    partial void OnSelectedResolutionChanged(CameraResolutionOption? value)
    {
        if (_suppressModeEvents || value is null) return;
        var modes = Cameras.StreamModesFor(Info.UniqueId);
        var current = _currentMode ?? modes.DefaultMode();
        if (current is not { } c || modes.WithResolution(c, (value.Width, value.Height)) is not { } mode) return;
        CommitStreamMode(mode);
    }

    partial void OnSelectedRateChanged(CameraRateOption? value)
    {
        if (_suppressModeEvents || value is null || SelectedResolution is not { } res) return;
        CommitStreamMode(new StreamMode(res.Width, res.Height, value.Fps));
    }

    /// <summary>
    /// Save the mode for this camera (as the Rust app does) and reopen the exclusive
    /// stream at it. Shapes this preview only: other apps choose their own.
    /// </summary>
    private void CommitStreamMode(StreamMode mode)
    {
        if (!ExclusiveAccess || mode == _currentMode || mode.Width == 0 || mode.Height == 0 || mode.Fps == 0) return;
        DiagnosticLog.Info("camera", $"exclusive preview mode {mode} (was {_currentMode?.ToString() ?? "unknown"})");
        var saved = new CameraStreamMode(mode.Width, mode.Height, mode.Fps);
        if (_config.CameraStreamMode(ConfigKey) != saved)
        {
            _config.SetCameraStreamMode(ConfigKey, saved);
            _save();
        }
        if (_active) StartPreview();
    }

    private string? BuildUsbNotice() =>
        Cameras.UsbLinkFor(Info.UniqueId) is { FasterAvailable: { } capable } link
            ? Loc.Current.Format("Camera_UsbLinkLimited", link.Speed.Label(), capable.Label())
            : null;

    // ── Controls load ───────────────────────────────────────────────────────

    /// <summary>
    /// Read the camera's controls, re-apply the saved state over USB, and build the
    /// rows. An unreachable camera builds nothing and retries, rather than caching
    /// an empty panel that never rebuilds once the device returns.
    /// </summary>
    private async Task LoadControlsAsync()
    {
        _controlsReady = false;
        while (!_life.IsCancellationRequested)
        {
            var (state, reapplyFailed) = await LoadWithSavedAsync();
            if (_life.IsCancellationRequested) return;
            if (reapplyFailed)
            {
                // Clear the active profile so a later edit can't overwrite the saved
                // profile with values the hardware didn't take.
                SetActiveProfile(null);
            }
            if (state is not null)
            {
                BuildRows(state);
                return;
            }
            try { await Task.Delay(RetryInterval, _life.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>The live state with the saved values laid over it, after pushing those that differ.</summary>
    private async Task<(CameraState? State, bool ReapplyFailed)> LoadWithSavedAsync()
    {
        CameraState read;
        try { read = await Cameras.ReadStateAsync(Info.UniqueId); }
        catch (Exception e)
        {
            DiagnosticLog.Warn("camera", $"controls read failed: {e.Message}");
            return (null, false);
        }
        var saved = _config.CameraControls(ConfigKey);

        var autos = new List<(AutoToggle Toggle, AutoState State)>();
        var applyAutos = new List<(AutoToggle, bool)>();
        foreach (var (toggle, st) in read.Autos)
        {
            var on = saved is not null && saved.TryGetValue(toggle.Name(), out var v) ? v != 0 : st.Current;
            if (on != st.Current) applyAutos.Add((toggle, on));
            autos.Add((toggle, st with { Current = on }));
        }
        var controls = new List<(CameraControl Control, ControlRange Range)>();
        var applyValues = new List<(CameraControl, int)>();
        foreach (var (control, range) in read.Controls)
        {
            int? savedValue = saved is not null && saved.TryGetValue(control.Name(), out var v) ? v : null;
            var initial = range.Clamp(savedValue ?? range.Current);
            var autoOn = control.AutoToggleFor() is { } t && autos.Any(a => a.Toggle == t && a.State.Current);
            if (savedValue is { } sv && sv != range.Current && !autoOn)
                applyValues.Add((control, initial));
            controls.Add((control, range with { Current = initial }));
        }
        var state = new CameraState(controls, autos);
        if (applyAutos.Count == 0 && applyValues.Count == 0) return (state, false);
        if (_life.IsCancellationRequested) return (null, false);

        try
        {
            await Cameras.ApplySettingsAsync(Info.UniqueId, applyAutos, applyValues);
            return (state, false);
        }
        catch (Exception e)
        {
            DiagnosticLog.Warn("camera", $"saved camera state reapply failed: {e.Message}");
        }
        // Keep the first read's rows, taking each current value from a live re-read.
        try
        {
            var live = await Cameras.ReadStateAsync(Info.UniqueId);
            var mergedAutos = state.Autos.Select(a =>
            {
                foreach (var l in live.Autos)
                    if (l.Toggle == a.Toggle) return (a.Toggle, a.State with { Current = l.State.Current });
                return a;
            }).ToList();
            var mergedControls = read.Controls.Select(c =>
            {
                foreach (var l in live.Controls)
                    if (l.Control == c.Control) return (c.Control, c.Range with { Current = l.Range.Current });
                return c;
            }).ToList();
            return (new CameraState(mergedControls, mergedAutos), true);
        }
        catch
        {
            return (null, true);
        }
    }

    private void BuildRows(CameraState state)
    {
        foreach (var timer in _pendingCommits.Values) timer.Stop();
        _pendingCommits.Clear();
        _rows.Clear();
        LensRows.Clear();
        ImageRows.Clear();
        _autos.Clear();
        foreach (var (toggle, st) in state.Autos)
            _autos[toggle] = st;
        foreach (var (control, range) in state.Controls)
        {
            var row = new CameraControlRowViewModel(this, control, range);
            if (control.AutoToggleFor() is { } t && _autos.TryGetValue(t, out var auto))
            {
                row.HasAuto = true;
                row.AutoOn = auto.Current;
            }
            _rows.Add(row);
            (control.IsLens() ? LensRows : ImageRows).Add(row);
        }
        _controlsReady = true;
        ControlsLoaded = true;
        OnPropertyChanged(nameof(ShowControls));
        OnPropertyChanged(nameof(ShowControlsUnavailable));
        OnPropertyChanged(nameof(ShowSectionHeaders));
        RebuildProfileChips();
    }

    /// <summary>
    /// Recover after a device write failed: partial writes may have landed, so
    /// rebuild from the live state and drop the active profile, so a later edit
    /// can't persist a half-applied profile.
    /// </summary>
    private void ResyncAfterFailedWrite()
    {
        SetActiveProfile(null);
        ControlsLoaded = false;
        _ = LoadControlsAsync();
    }

    // ── Control edits ───────────────────────────────────────────────────────

    /// <summary>A slider moved: write once it settles.</summary>
    internal void OnRowMoved(CameraControlRowViewModel row)
    {
        if (!_controlsReady) return;
        if (!_pendingCommits.TryGetValue(row, out var timer))
        {
            timer = new DispatcherTimer { Interval = CommitDelay };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                _pendingCommits.Remove(row);
                _ = CommitValueAsync(row, row.IntValue);
            };
            _pendingCommits[row] = timer;
        }
        timer.Stop();
        timer.Start();
    }

    /// <summary>
    /// Write a value — taking the control over to manual first when its auto mode
    /// owns it (the camera rejects gated values, and grabbing the slider is the
    /// take-over gesture) — then persist exactly what the device took.
    /// </summary>
    private async Task<bool> CommitValueAsync(CameraControlRowViewModel row, int value)
    {
        var takeover = row.Control.AutoToggleFor() is { } toggle && _autos.TryGetValue(toggle, out var st) && st.Current
            ? toggle
            : (AutoToggle?)null;
        try
        {
            if (takeover is { } t)
                await Cameras.ApplySettingsAsync(Info.UniqueId, [(t, false)], [(row.Control, value)]);
            else
                await Cameras.SetControlAsync(Info.UniqueId, row.Control, value);
        }
        catch (Exception e)
        {
            DiagnosticLog.Warn("camera", $"control write failed: {row.Control.Name()}={value}: {e.Message}");
            ResyncAfterFailedWrite();
            return false;
        }
        if (takeover is { } off)
        {
            SetAuto(off, false);
            _config.SetCameraControl(ConfigKey, off.Name(), 0);
        }
        _config.SetCameraControl(ConfigKey, row.Control.Name(), value);
        SyncActiveCustom();
        _save();
        return true;
    }

    /// <summary>Flip one auto mode. Turning auto off re-asserts the slider's value in the same open.</summary>
    internal async void ToggleAuto(CameraControlRowViewModel row)
    {
        if (row.Control.AutoToggleFor() is not { } toggle || !_autos.TryGetValue(toggle, out var st)) return;
        var on = !st.Current;
        List<(CameraControl, int)> values = on ? [] : [(row.Control, row.IntValue)];
        try { await Cameras.ApplySettingsAsync(Info.UniqueId, [(toggle, on)], values); }
        catch (Exception e)
        {
            DiagnosticLog.Warn("camera", $"auto write failed: {toggle.Name()}={on}: {e.Message}");
            ResyncAfterFailedWrite();
            return;
        }
        SetAuto(toggle, on);
        _config.SetCameraControl(ConfigKey, toggle.Name(), on ? 1 : 0);
        SyncActiveCustom();
        _save();
    }

    /// <summary>Reset one control to its device default, its auto mode back to the device default too.</summary>
    internal async void ResetControl(CameraControlRowViewModel row)
    {
        if (_pendingCommits.Remove(row, out var timer)) timer.Stop();
        var autos = new List<(AutoToggle, bool)>();
        if (row.Control.AutoToggleFor() is { } toggle && _autos.TryGetValue(toggle, out var st))
            autos.Add((toggle, st.Default));
        try { await Cameras.ApplySettingsAsync(Info.UniqueId, autos, [(row.Control, row.Range.Default)]); }
        catch (Exception e)
        {
            DiagnosticLog.Warn("camera", $"control reset failed: {row.Control.Name()}: {e.Message}");
            ResyncAfterFailedWrite();
            return;
        }
        CommitBatch(autos, [(row.Control, row.Range.Default)]);
        SyncActiveCustom();
        _save();
    }

    /// <summary>
    /// Exact-value editor for a control whose slider is awkward to position. The
    /// device's range stays authoritative: unsupported values keep the dialog open.
    /// </summary>
    internal async Task EditValueAsync(CameraControlRowViewModel row)
    {
        if (_valuePrompt is null) return;
        var request = new CameraValuePromptRequest(
            Loc.Current.Format("Camera_SetValueTitle", row.Label),
            Loc.Current.Format("Camera_SupportedRange", row.Range.Min, row.Range.Max),
            row.IntValue,
            row.Range.Supports);
        if (await _valuePrompt(request) is not { } value || !_rows.Contains(row)) return;
        if (_pendingCommits.Remove(row, out var timer)) timer.Stop();
        if (await CommitValueAsync(row, value))
            row.Seat(value);
    }

    /// <summary>Reset every control and auto mode to the device defaults in one batched open.</summary>
    [RelayCommand]
    private async Task ResetAllAsync()
    {
        CancelPendingCommits();
        var autos = _autos.Select(a => (a.Key, a.Value.Default)).ToList();
        var values = _rows.Select(r => (r.Control, r.Range.Default)).ToList();
        try { await Cameras.ApplySettingsAsync(Info.UniqueId, autos, values); }
        catch (Exception e)
        {
            DiagnosticLog.Warn("camera", $"camera reset failed: {e.Message}");
            ResyncAfterFailedWrite();
            return;
        }
        CommitBatch(autos, values);
        SyncActiveCustom();
        _save();
    }

    /// <summary>After a successful batch: mirror it onto the rows and persist it.</summary>
    private void CommitBatch(IReadOnlyList<(AutoToggle Toggle, bool On)> autos, IReadOnlyList<(CameraControl Control, int Value)> values)
    {
        foreach (var (toggle, on) in autos)
        {
            SetAuto(toggle, on);
            _config.SetCameraControl(ConfigKey, toggle.Name(), on ? 1 : 0);
        }
        foreach (var (control, value) in values)
        {
            _rows.FirstOrDefault(r => r.Control == control)?.Seat(value);
            _config.SetCameraControl(ConfigKey, control.Name(), value);
        }
    }

    private void SetAuto(AutoToggle toggle, bool on)
    {
        if (_autos.TryGetValue(toggle, out var st)) _autos[toggle] = st with { Current = on };
        foreach (var row in _rows)
            if (row.Control.AutoToggleFor() == toggle)
                row.AutoOn = on;
    }

    private void CancelPendingCommits()
    {
        foreach (var timer in _pendingCommits.Values) timer.Stop();
        _pendingCommits.Clear();
    }

    // ── Profiles ────────────────────────────────────────────────────────────

    private void RebuildProfileChips()
    {
        ProfileChips.Clear();
        var active = _config.CameraActiveProfile(ConfigKey);
        foreach (var (id, _) in BuiltinProfiles)
            ProfileChips.Add(new CameraProfileChipViewModel(id, BuiltinLabel(id), false, false) { IsActive = active == id });
        foreach (var name in _config.CameraProfiles(ConfigKey).Keys)
            ProfileChips.Add(new CameraProfileChipViewModel(name, name, true, false) { IsActive = active == name });
        ProfileChips.Add(new CameraProfileChipViewModel("", $"+ {Loc.Current["Camera_NewProfile"]}", false, true));
    }

    private static string BuiltinLabel(string id) => id switch
    {
        "streaming" => Loc.Current["Camera_ProfileStreaming"],
        "video_call" => Loc.Current["Camera_ProfileVideoCall"],
        _ => Loc.Current["Camera_ProfileDefault"],
    };

    [RelayCommand]
    private async Task ProfileChipAsync(CameraProfileChipViewModel? chip)
    {
        if (chip is null || !_controlsReady) return;
        if (chip.IsNew) SaveProfile();
        else await ApplyProfileAsync(chip.Id);
    }

    /// <summary>
    /// Apply a built-in or saved profile: compute each control's target, push it all
    /// in one batched open, re-seat the sliders, persist, and remember the selection.
    /// </summary>
    private async Task ApplyProfileAsync(string id)
    {
        CancelPendingCommits();
        var autos = new List<(AutoToggle, bool)>();
        var values = new List<(CameraControl, int)>();
        var builtin = BuiltinProfiles.FirstOrDefault(p => p.Id == id);
        if (builtin.Id is not null)
        {
            foreach (var (toggle, st) in _autos)
                autos.Add((toggle, builtin.Id == "default" ? st.Default : true));
            foreach (var row in _rows)
            {
                var fallback = builtin.Id != "default"
                    && row.Control is CameraControl.PowerLineFrequency or CameraControl.LowLightCompensation
                    ? row.IntValue
                    : row.Range.Default;
                var target = fallback;
                foreach (var (control, fraction) in builtin.Values)
                    if (control == row.Control)
                        target = row.Range.Min + (int)Math.Round((row.Range.Max - row.Range.Min) * fraction);
                values.Add((row.Control, row.Range.Clamp(target)));
            }
        }
        else if (_config.CameraProfiles(ConfigKey).TryGetValue(id, out var snap))
        {
            foreach (var (toggle, st) in _autos)
                autos.Add((toggle, snap.TryGetValue(toggle.Name(), out var v) ? v != 0 : st.Current));
            foreach (var row in _rows)
                if (snap.TryGetValue(row.Control.Name(), out var v))
                    values.Add((row.Control, row.Range.Clamp(v)));
        }
        else return;

        try { await Cameras.ApplySettingsAsync(Info.UniqueId, autos, values); }
        catch (Exception e)
        {
            DiagnosticLog.Warn("camera", $"camera profile apply failed: {id}: {e.Message}");
            ResyncAfterFailedWrite();
            return;
        }
        CommitBatch(autos, values);
        SetActiveProfile(id);
        _save();
    }

    /// <summary>Save the current values + auto states as a new custom profile ("Custom N") and select it.</summary>
    private void SaveProfile()
    {
        var existing = _config.CameraProfiles(ConfigKey);
        var n = existing.Count + 1;
        var name = Loc.Current.Format("Camera_CustomProfileNumber", n);
        while (existing.ContainsKey(name))
            name = Loc.Current.Format("Camera_CustomProfileNumber", ++n);
        _config.SaveCameraProfile(ConfigKey, name, Snapshot());
        SetActiveProfile(name);
        _save();
    }

    /// <summary>Delete a saved custom profile. The hardware keeps whatever it's set to.</summary>
    [RelayCommand]
    private void DeleteProfile(CameraProfileChipViewModel? chip)
    {
        if (chip is not { IsCustom: true }) return;
        _config.DeleteCameraProfile(ConfigKey, chip.Id);
        _save();
        RebuildProfileChips();
    }

    private void SetActiveProfile(string? id)
    {
        if (_config.CameraActiveProfile(ConfigKey) != id)
        {
            _config.SetCameraActiveProfile(ConfigKey, id);
            _save();
        }
        RebuildProfileChips();
    }

    /// <summary>The current control values + auto states as a profile snapshot.</summary>
    private SortedDictionary<string, int> Snapshot()
    {
        var snap = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in _rows) snap[row.Control.Name()] = row.IntValue;
        foreach (var (toggle, st) in _autos) snap[toggle.Name()] = st.Current ? 1 : 0;
        return snap;
    }

    /// <summary>
    /// Keep the active custom profile tracking live edits, so a profile is always
    /// what was last seen while it was selected. Built-ins are never edited.
    /// </summary>
    private void SyncActiveCustom()
    {
        if (_config.CameraActiveProfile(ConfigKey) is { } active && _config.CameraProfiles(ConfigKey).ContainsKey(active))
            _config.SaveCameraProfile(ConfigKey, active, Snapshot());
    }

    /// <summary>Re-read localized labels (profile chips, rates) after a language switch.</summary>
    public void RefreshLocalizedText()
    {
        RebuildProfileChips();
        UsbNotice = BuildUsbNotice();
        ShowMode(_currentMode);
        if (HasModes) RebuildModePicker();
    }

    public void Dispose()
    {
        CameraUsage.Changed -= OnCameraUsageChanged;
        _life.Cancel();
        CancelPendingCommits();
        _active = false;
        StopPreview();
    }
}
