using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenLogi.Camera;
using OpenLogi.Core.Logging;

namespace OpenLogi.App.ViewModels;

// Webcams: the timed camera scan that keeps their gallery tiles current, and the
// Camera tab's page (preview + controls) for the selected one. Cameras are UVC
// devices the HID stack never sees, so they are discovered on their own path and
// merged into the gallery beside the HID++ devices (Rust runtime.rs / devices.rs).
public partial class MainWindowViewModel
{
    /// <summary>
    /// How often cameras are re-enumerated. Nothing announces a UVC arrival to the
    /// app, so this is the one thing that still has to be asked on a timer.
    /// </summary>
    private static readonly TimeSpan CameraScanPeriod = TimeSpan.FromSeconds(2);

    /// <summary>Empty scans tolerated before camera tiles go: a USB control seize can blink a camera out of discovery.</summary>
    private const int CameraMissGrace = 2;

    private readonly List<DeviceViewModel> _cameraTiles = [];
    private DispatcherTimer? _cameraScanTimer;
    private bool _cameraScanRunning;
    private int _cameraMisses;
    private bool _windowOnScreen = true;

    /// <summary>The selected webcam's Camera tab; null unless a camera page is open.</summary>
    [ObservableProperty] private CameraViewModel? _camera;

    [ObservableProperty][NotifyPropertyChangedFor(nameof(InitialTabIndex))] private bool _hasCameraTab;

    /// <summary>Pushed from the Camera TabItem's IsSelected: the preview runs only while it is the tab on screen.</summary>
    [ObservableProperty] private bool _cameraTabSelected;

    /// <summary>
    /// Opens the exact-value dialog for a camera control. Installed by the window
    /// (it needs an owner for the modal); null until then.
    /// </summary>
    public Func<CameraValuePromptRequest, Task<int?>>? CameraValuePrompt { get; set; }

    /// <summary>Set by the window: false while it is hidden to the tray or minimized.</summary>
    public bool WindowOnScreen
    {
        get => _windowOnScreen;
        set
        {
            if (_windowOnScreen == value) return;
            _windowOnScreen = value;
            UpdateCameraActive();
        }
    }

    private void StartCameraScan()
    {
        if (!Cameras.Supported) return;
        _cameraScanTimer = new DispatcherTimer { Interval = CameraScanPeriod };
        _cameraScanTimer.Tick += (_, _) => _ = ScanCamerasAsync();
        _cameraScanTimer.Start();
        _ = ScanCamerasAsync();
        // Tells the Camera tab when another app controls a camera.
        _ = CameraUsage.StartAsync();
    }

    private async Task ScanCamerasAsync()
    {
        if (_cameraScanRunning) return;
        _cameraScanRunning = true;
        try { ApplyCameraScan(await Cameras.EnumerateAsync()); }
        catch (Exception e) { DiagnosticLog.Warn("camera", $"camera scan failed: {e.Message}"); }
        finally { _cameraScanRunning = false; }
    }

    /// <summary>Reconcile the camera tiles with a scan, keeping the tiles (and an open page) of cameras still there.</summary>
    private void ApplyCameraScan(IReadOnlyList<CameraInfo> scanned)
    {
        if (scanned.Count == 0 && _cameraTiles.Count > 0 && _cameraMisses < CameraMissGrace)
        {
            _cameraMisses++;
            return;
        }
        _cameraMisses = 0;

        var changed = false;
        // A camera moved to another port reappears under a new capture id: its old
        // tile goes rather than being kept beside the new one.
        for (var i = _cameraTiles.Count - 1; i >= 0; i--)
        {
            var tile = _cameraTiles[i];
            if (scanned.Any(c => c.UniqueId == tile.Camera!.UniqueId)) continue;
            _cameraTiles.RemoveAt(i);
            Devices.Remove(tile);
            changed = true;
            if (ReferenceEquals(SelectedDevice, tile))
            {
                ShowingDevice = false;
                SelectedDevice = null;
            }
        }
        foreach (var camera in scanned)
        {
            if (_cameraTiles.Any(t => t.Camera!.UniqueId == camera.UniqueId)) continue;
            var tile = new DeviceViewModel(camera);
            _cameraTiles.Add(tile);
            if (!IsScanning) Devices.Add(tile); // a running HID scan appends the tiles when it finishes
            _ = ResolveCardImageAsync(tile);
            changed = true;
        }
        if (!changed || IsScanning) return;
        NoDevices = Devices.Count == 0;
        if (Devices.Count == 0) SetStatus("Status_NoDevicesFound");
        else SetStatus("Status_DeviceCount", Devices.Count);
    }

    /// <summary>Open, keep or close the Camera page to match the selected device and navigation.</summary>
    private void SyncCameraPage()
    {
        var wanted = ShowingDevice && SelectedDevice?.Camera is { } c ? c : null;
        HasCameraTab = SelectedDevice?.IsCamera == true;
        if (Camera?.Info == wanted && (Camera is null) == (wanted is null))
        {
            UpdateCameraActive();
            return;
        }
        Camera?.Dispose();
        Camera = wanted is null ? null : new CameraViewModel(wanted, _config, SaveConfig, CameraValuePrompt);
        UpdateCameraActive();
    }

    partial void OnCameraTabSelectedChanged(bool value) => UpdateCameraActive();

    private void UpdateCameraActive() =>
        Camera?.SetActive(ShowingDevice && CameraTabSelected && _windowOnScreen);
}
