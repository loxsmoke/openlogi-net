using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenLogi.Agent;
using OpenLogi.App.Services;
using OpenLogi.Assets;
using OpenLogi.Core;
using OpenLogi.Hid;

namespace OpenLogi.App.ViewModels;

/// <summary>
/// Root view-model: live device inventory + the interactive mouse diagram
/// (device render with clickable button hotspots) and the per-button picker.
///
/// GUI builds with compiled bindings; behaviour confirmed by running the app.
/// </summary>
public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private const double DiagramHeightPx = 300;

    private readonly Config _config = LoadConfig();
    private readonly AssetResolver _resolver = new(Paths.AssetCacheDir());

    /// <summary>The shared config, exposed so the Settings window edits the same instance.</summary>
    public Config Configuration => _config;

    /// <summary>The open session if the current device supports per-key lighting (for the secret editor).</summary>
    public DeviceSession? PerKeySession => _session is { SupportsPerKey: true } s ? s : null;

    /// <summary>
    /// Create a per-key color editor bound to the device's <em>current</em> session.
    /// A fresh instance each open (the session is replaced on every control reload,
    /// so a cached editor would hold a disposed handle); painted keys persist via
    /// config, seeded back in here, so nothing is lost by not caching.
    /// </summary>
    public PerKeyColorViewModel? CreatePerKeyEditor()
    {
        if (PerKeySession is not { } s) return null;
        var ck = SelectedDevice?.ConfigKey;
        var paint = ck is not null && _config.Lighting(ck)?.PaintColor is { } p
            ? (Avalonia.Media.Color?)ParseHexColor(p) : null;
        return new PerKeyColorViewModel(
            s, LightingColor, paint, LoadPerKeyColors(ck),
            (painted, baseColor, paintColor) => SavePerKeyColors(ck, painted, baseColor, paintColor));
    }

    /// <summary>Read a device's saved per-key colors (zone → color) from config; empty if none.</summary>
    private IReadOnlyDictionary<byte, Avalonia.Media.Color> LoadPerKeyColors(string? configKey)
    {
        var map = new Dictionary<byte, Avalonia.Media.Color>();
        if (configKey is not null && _config.Lighting(configKey)?.PerKey is { } stored)
            foreach (var (zone, hex) in stored) map[zone] = ParseHexColor(hex);
        return map;
    }

    private static string Hex(Avalonia.Media.Color c) => $"{c.R:x2}{c.G:x2}{c.B:x2}";

    /// <summary>Persist the editor's painted keys plus its base and paint colors into the device's lighting config.</summary>
    private void SavePerKeyColors(
        string? configKey, IReadOnlyDictionary<byte, Avalonia.Media.Color> painted,
        Avalonia.Media.Color baseColor, Avalonia.Media.Color paintColor)
    {
        if (configKey is null) return;
        var perKey = new Dictionary<byte, string>();
        foreach (var (zone, c) in painted) perKey[zone] = Hex(c);
        var existing = _config.Lighting(configKey);
        _config.SetLighting(configKey, new Lighting
        {
            Enabled = existing?.Enabled ?? LightingEnabled,
            Color = Hex(baseColor),       // base color == the unpainted-key (solid) color
            Brightness = existing?.Brightness ?? (byte)LightingBrightness,
            PaintColor = Hex(paintColor), // the editor's brush color
            PerKey = perKey,
        });
        SaveConfig();
    }

    private Dictionary<ButtonId, ButtonBindingViewModel> _bindings = [];

    public ObservableCollection<DeviceViewModel> Devices { get; } = [];
    public ObservableCollection<ButtonBindingViewModel> Buttons { get; } = [];
    public ObservableCollection<DiagramAnnotationViewModel> Annotations { get; } = [];
    public ObservableCollection<HostSlotViewModel> Hosts { get; } = [];
    public ObservableCollection<ProfileSlotViewModel> Profiles { get; } = [];
    public ObservableCollection<GKeyViewModel> GKeys { get; } = [];

    // Gestures section (Buttons tab): choose which button drives gestures, its
    // plain-tap Click action, whether the four swipes are active, and a category
    // preset (or per-direction custom actions). Shown for mice that expose a
    // HID++-capturable gesture control, even without a gesture-button hotspot.
    public ObservableCollection<GestureOwnerChoice> GestureOwnerChoices { get; } = [];
    public ObservableCollection<GestureDirectionBindingViewModel> GestureDirections { get; } = [];
    [ObservableProperty] private bool _showGestures;
    [ObservableProperty] private GestureOwnerChoice? _selectedGestureOwner;
    /// <summary>The Click (plain tap) editor row; null while no button is selected.</summary>
    [ObservableProperty] private GestureDirectionBindingViewModel? _gestureClick;
    /// <summary>Whether a button is selected for editing (shows Click + Category rows).</summary>
    [ObservableProperty][NotifyPropertyChangedFor(nameof(GestureSwipesVisible))]
    private bool _gestureOwnerSelected;
    /// <summary>The panel-header checkbox: gestures on/off for ALL buttons of this device.</summary>
    [ObservableProperty] private bool _gesturesEnabled;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(GestureSwipesVisible))]
    private GesturePreset? _selectedGestureCategory;

    /// <summary>The four swipe rows show only for a selected button whose category isn't Disabled.</summary>
    public bool GestureSwipesVisible =>
        GestureOwnerSelected && SelectedGestureCategory is { } p && !ReferenceEquals(p, DisabledGesturePreset);

    // Suppresses the owner-changed handler while the section is populated programmatically.
    private bool _suppressGestureOwner;
    // Suppresses the checkbox/category/direction handlers during programmatic fills.
    private bool _suppressGesturePanel;

    // Ctrl+Z history for the gesture editors (click, category, and the four swipes).
    // Each entry is the full five-action state before one user edit; a category fill
    // is a single entry. Cleared whenever the edited button changes.
    private readonly Stack<(ButtonId Owner, OpenLogi.Core.Action[] Actions)> _gestureUndo = new();
    private (ButtonId Owner, OpenLogi.Core.Action[] Actions)? _lastGestureState;

    /// <summary>The Custom sentinel: selected when the swipes match no preset; applies nothing.</summary>
    private static readonly GesturePreset CustomGesturePreset = new("Custom", null, null, null, null);

    /// <summary>
    /// "Disabled": all four swipes Do Nothing — the default for an unconfigured
    /// button, and the way to turn one button's swipes off (its Click keeps working).
    /// </summary>
    private static readonly GesturePreset DisabledGesturePreset = new("Disabled",
        OpenLogi.Core.Action.None, OpenLogi.Core.Action.None,
        OpenLogi.Core.Action.None, OpenLogi.Core.Action.None);

    /// <summary>Category presets for the four swipes (mirrors the Options+ gesture sets).</summary>
    public IReadOnlyList<GesturePreset> GestureCategories { get; } =
    [
        DisabledGesturePreset,
        new("Windows & Desktops", OpenLogi.Core.Action.TaskView, OpenLogi.Core.Action.ShowDesktop,
            OpenLogi.Core.Action.PreviousDesktop, OpenLogi.Core.Action.NextDesktop),
        new("Media & Volume", OpenLogi.Core.Action.VolumeUp, OpenLogi.Core.Action.VolumeDown,
            OpenLogi.Core.Action.PrevTrack, OpenLogi.Core.Action.NextTrack),
        new("Arrange Windows", OpenLogi.Core.Action.MaximizeWindow, OpenLogi.Core.Action.MinimizeWindow,
            OpenLogi.Core.Action.SnapWindowLeft, OpenLogi.Core.Action.SnapWindowRight),
        new("Browser Tabs", OpenLogi.Core.Action.NewTab, OpenLogi.Core.Action.CloseTab,
            OpenLogi.Core.Action.PrevTab, OpenLogi.Core.Action.NextTab),
        new("Scrolling", OpenLogi.Core.Action.ScrollUp, OpenLogi.Core.Action.ScrollDown,
            OpenLogi.Core.Action.HorizontalScrollLeft, OpenLogi.Core.Action.HorizontalScrollRight),
        CustomGesturePreset,
    ];

    [ObservableProperty] private DeviceViewModel? _selectedDevice;
    [ObservableProperty] private string _statusText = "Loading devices…";

    // Home-gallery states: scanning (loading) and "no devices found".
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _noDevices;

    // Drives the thin indeterminate loading line under the device-detail header.
    [ObservableProperty] private bool _isLoadingDevice;

    // Launch-time update check: banner shown when GitHub reports a newer release.
    [ObservableProperty] private bool _updateAvailable;
    [ObservableProperty] private string _updateBannerText = "";
    private string? _latestUpdate;

    // Navigation: false = home gallery, true = device detail.
    [ObservableProperty] private bool _showingDevice;
    // Capability-gated detail tabs (mirrors the Rust DetailTab::tabs_for).
    [ObservableProperty][NotifyPropertyChangedFor(nameof(InitialTabIndex))] private bool _hasButtonsTab;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(InitialTabIndex))] private bool _hasPointerTab;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(InitialTabIndex))] private bool _hasLightingTab;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(InitialTabIndex))] private bool _hasGKeysTab;

    /// <summary>The first visible tab to select for the current device (Buttons → Pointer → Lighting → G-Keys → Device).</summary>
    public int InitialTabIndex => HasButtonsTab ? 0 : HasPointerTab ? 1 : HasLightingTab ? 2 : HasGKeysTab ? 3 : 4;

    [ObservableProperty] private Bitmap? _diagramImage;
    [ObservableProperty] private double _diagramWidth;
    [ObservableProperty] private double _diagramHeight;
    [ObservableProperty] private double _imageX;
    [ObservableProperty] private double _imageWidth;

    // DPI live controls (slider over the device's supported range + presets).
    [ObservableProperty][NotifyPropertyChangedFor(nameof(ShowPointerTuning))] private bool _showDpi;

    /// <summary>The Pointer tuning card is shown when the device offers DPI control or native scroll-direction invert.</summary>
    public bool ShowPointerTuning => ShowDpi || ShowScrollInvert;
    [ObservableProperty] private double _dpiMin;
    [ObservableProperty] private double _dpiMax;
    [ObservableProperty] private double _dpiStep = 50;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(DpiText))] private double _dpiValue;
    [ObservableProperty] private string _dpiRangeText = "";
    private IReadOnlyList<uint> _dpiOptions = [];
    public ObservableCollection<uint> DpiPresets { get; } = [];
    public string DpiText => $"{(uint)Math.Round(DpiValue)} DPI";

    // SmartShift live controls. The wheel is really a single three-way choice —
    // Free spin, SmartShift (auto-disengage on a fast flick), or permanent
    // Ratchet — so we model it that way to avoid contradictory combinations.
    [ObservableProperty] private bool _showSmartShift;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFreeSpin), nameof(IsSmartShift), nameof(IsRatchet), nameof(ShowSensitivity))]
    private WheelModeChoice _wheelModeChoice = WheelModeChoice.SmartShift;
    [ObservableProperty] private int _autoDisengage = 16;

    /// <summary>Sensitivity only applies in SmartShift mode (it's the auto-disengage threshold).</summary>
    public bool ShowSensitivity => WheelModeChoice == WheelModeChoice.SmartShift;

    // RadioButton-friendly wrappers over the single enum. Setters act only on the
    // selected (true) edge — a radio group also reports the deselected one as false.
    public bool IsFreeSpin { get => WheelModeChoice == WheelModeChoice.FreeSpin; set { if (value) WheelModeChoice = WheelModeChoice.FreeSpin; } }
    public bool IsSmartShift { get => WheelModeChoice == WheelModeChoice.SmartShift; set { if (value) WheelModeChoice = WheelModeChoice.SmartShift; } }
    public bool IsRatchet { get => WheelModeChoice == WheelModeChoice.Ratchet; set { if (value) WheelModeChoice = WheelModeChoice.Ratchet; } }

    // Native scroll-direction invert (HiResWheel 0x2121).
    [ObservableProperty][NotifyPropertyChangedFor(nameof(ShowPointerTuning))] private bool _showScrollInvert;
    [ObservableProperty] private bool _invertScroll;

    // Hi-res smooth scrolling (also HiResWheel 0x2121): the wheel is diverted to
    // HID++ events and re-injected as fine-grained OS scrolling while the app runs.
    [ObservableProperty] private bool _showSmoothScroll;
    [ObservableProperty] private bool _smoothScroll;

    // Hosts (EasySwitch / multi-host).
    [ObservableProperty] private bool _showHosts;
    /// <summary>Whether the selected device can clear/forget host slots (drives the host note + Forget buttons).</summary>
    [ObservableProperty] private bool _hostsSupportClear;

    // Lighting (keyboards).
    [ObservableProperty][NotifyPropertyChangedFor(nameof(ShowColor))] private bool _lightingEnabled = true;
    [ObservableProperty] private Avalonia.Media.Color _lightingColor = Avalonia.Media.Colors.White;
    [ObservableProperty] private int _lightingBrightness = 100;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(ShowColor), nameof(ShowSpeed))] private LightingEffect _selectedEffect = LightingEffect.Solid;
    [ObservableProperty] private int _lightingSpeed = 3000; // breathing period (ms)
    public System.Array LightingEffects { get; } = System.Enum.GetValues<LightingEffect>();
    private System.Threading.CancellationTokenSource? _effectCts;

    // Lighting control visibility:
    //   No profile  → just the "Lighting on" toggle; if on, Color + Intensity (live solid).
    //   Profile     → Effect picker; Color + Intensity for Solid/Breathing; Speed for Breathing.
    public bool ShowLightingToggle => !ProfileSelected;
    public bool ShowEffect => ProfileSelected;
    public bool ShowColor => ProfileSelected
        ? SelectedEffect is LightingEffect.Solid or LightingEffect.Breathing
        : LightingEnabled;
    public bool ShowSpeed => ProfileSelected && SelectedEffect == LightingEffect.Breathing;

    // Onboard profiles (0x8100) — smooth device-side lighting that survives app close.
    [ObservableProperty] private bool _showProfiles;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(ShowLightingToggle), nameof(ShowEffect), nameof(ShowColor), nameof(ShowSpeed), nameof(ShowPerKeyButton))] private bool _profileSelected;
    [ObservableProperty] private int _selectedProfileForEdit;
    private byte _profileCount;

    // G-keys (0x8010) — remappable per active onboard profile.
    [ObservableProperty] private int _gKeyProfile;
    private ushort _gkeyProfileSector;

    // Per-key color editor (PerKeyLighting 0x8081) — shown when the device supports it.
    [ObservableProperty][NotifyPropertyChangedFor(nameof(ShowPerKeyButton))] private bool _showPerKeyEditor;

    /// <summary>
    /// The per-key editor drives lighting live via host mode, which only applies in
    /// "No profile" mode — with an onboard profile selected the keyboard replays its
    /// own stored lighting, so the button is hidden there.
    /// </summary>
    public bool ShowPerKeyButton => ShowPerKeyEditor && !ProfileSelected;

    // Backlight brightness (BrightnessControl 0x8040) — hardware-verified.
    [ObservableProperty] private bool _showBacklight;
    [ObservableProperty] private double _backlightValue;
    [ObservableProperty] private double _backlightMax = 100;

    private DeviceSession? _session;
    // Live battery listener for the currently-selected device; replaced on each selection.
    private IAsyncDisposable? _batteryMonitor;
    // One persistent session per connected mouse, kept open for the app lifetime so
    // each mouse's HID++ button overrides (DPI/ModeShift) work without opening its
    // page — and dispatch that mouse's own bindings, so multiple mice differ.
    private readonly List<MouseCapture> _mouseCaptures = [];
    private bool _loadingControls;

    private sealed record MouseCapture(DeviceViewModel Device, DeviceSession Session, IAsyncDisposable Capture);

    /// <summary>Disposes several per-mouse captures (DPI button, gesture button) as one handle.</summary>
    private sealed class CompositeCapture(IReadOnlyList<IAsyncDisposable> parts) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            foreach (var part in parts)
                await part.DisposeAsync();
        }
    }

    private bool IsPersistentSession(DeviceSession s) =>
        _mouseCaptures.Any(mc => ReferenceEquals(mc.Session, s));

    // The OS mouse hook that remaps Middle/Back/Forward to bound actions and
    // injects keystrokes (e.g. Task View = Win+Tab). Runs for the app's lifetime.
    private readonly AgentRuntime _agent;

    public MainWindowViewModel()
    {
        _agent = new AgentRuntime(_config);
        if (!Design.IsDesignMode)
        {
            // A failed hook install (e.g. no interactive desktop) must not crash
            // the UI — button remapping is simply inactive in that case.
            try { _agent.Start(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"mouse hook unavailable: {ex.Message}"); }
            _ = LoadAsync();
        }
    }

    /// <summary>
    /// Fallback button set when a device has no asset metadata to derive its real
    /// buttons from. Primary L/R clicks are intentionally excluded — Logitech never
    /// exposes them as remappable (matching the Rust geometry).
    /// </summary>
    private static readonly ButtonId[] DefaultMouseButtons =
    [
        ButtonId.MiddleClick, ButtonId.Back, ButtonId.Forward, ButtonId.DpiToggle,
    ];

    [RelayCommand]
    private async Task RefreshAsync()
    {
        ShowingDevice = false;
        await LoadAsync();
    }

    /// <summary>Open a device's detail screen (from a gallery card).</summary>
    [RelayCommand]
    private void OpenDevice(DeviceViewModel? device)
    {
        if (device is null) return;
        SelectedDevice = device;
        ShowingDevice = true;
    }

    /// <summary>Return to the home gallery.</summary>
    [RelayCommand]
    private void GoHome() => ShowingDevice = false;

    /// <summary>
    /// Record the user's answer to the first-run "check for updates?" prompt (so it's
    /// asked only once), then run the check immediately if they opted in.
    /// </summary>
    public async Task ApplyUpdateConsentAsync(bool enable)
    {
        _config.AppSettings.CheckForUpdates = enable;
        _config.AppSettings.UpdatePromptSeen = true;
        SaveConfig();
        await CheckForUpdatesAsync();
    }

    /// <summary>
    /// When the user has opted in, ask GitHub for the latest release and show the
    /// update banner if it's newer than this build (and not a version already
    /// dismissed). Silent on every failure; a no-op when update checks are off.
    /// </summary>
    public async Task CheckForUpdatesAsync()
    {
        if (!_config.AppSettings.CheckForUpdates) return;
        if (Assembly.GetExecutingAssembly().GetName().Version is not { } current) return;
        var newer = await UpdateService.CheckAsync(current);
        if (newer is null || newer == _config.AppSettings.DismissedUpdate) return;
        _latestUpdate = newer;
        UpdateBannerText = $"Update available: v{newer}";
        UpdateAvailable = true;
    }

    /// <summary>Open the releases page to download the available update.</summary>
    [RelayCommand]
    private void DownloadUpdate()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Brand.ReleasesUrl) { UseShellExecute = true }); }
        catch { /* no browser / blocked — ignore */ }
    }

    /// <summary>Hide the banner and remember this version so it won't reappear for it.</summary>
    [RelayCommand]
    private void DismissUpdate()
    {
        if (_latestUpdate is not null)
        {
            _config.AppSettings.DismissedUpdate = _latestUpdate;
            SaveConfig();
        }
        UpdateAvailable = false;
    }

    private async Task LoadAsync()
    {
        StatusText = "Scanning for Logitech devices…";
        IsScanning = true;
        NoDevices = false;
        Devices.Clear();
        try
        {
            var inventories = await HidInventory.EnumerateAsync();
            foreach (var inv in inventories)
                foreach (var paired in inv.Paired)
                    if (IsConfigurable(paired))
                        Devices.Add(new DeviceViewModel(inv.Receiver.Name, paired, DeviceRoute.DeviceRouteFor(inv, paired.Slot)));

            // Stay on the home gallery; opening a card navigates to its detail.
            SelectedDevice = null;
            ShowingDevice = false;
            StatusText = Devices.Count == 0 ? "No Logitech HID++ devices found." : $"{Devices.Count} device(s).";

            // Activate every connected mouse's overrides as soon as the app is running,
            // without opening any page: the OS hook for Middle/Back/Forward (global) and
            // a per-mouse session that diverts each mouse's DPI button.
            _ = ActivateAgentMiceAsync([.. Devices.Where(d => d.HasButtons)]);

            foreach (var vm in Devices)
                if (vm.ConfigKey is not null)
                    _ = ResolveCardImageAsync(vm);
        }
        catch (System.Exception e)
        {
            StatusText = $"Enumeration failed: {e.Message}";
        }
        finally
        {
            IsScanning = false;
            NoDevices = Devices.Count == 0;
        }
    }

    partial void OnSelectedDeviceChanged(DeviceViewModel? value)
    {
        // Point the remap hook at the selected device only when it's a mouse — the OS
        // hook handles mouse buttons, so viewing a keyboard must not deactivate the
        // mouse's overrides. The connected mouse is also activated at startup (LoadAsync).
        if (value?.HasButtons == true)
            _agent.SetSelectedDevice(value.ConfigKey);

        Buttons.Clear();
        Annotations.Clear();
        DiagramImage = null;
        _bindings = [];
        ShowDpi = false;
        ShowSmartShift = false;
        ShowScrollInvert = false;
        ShowSmoothScroll = false;
        ShowGestures = false;
        ShowHosts = false;
        ShowBacklight = false;
        ShowPerKeyEditor = false;
        ShowProfiles = false;
        ProfileSelected = false;
        SelectedProfileForEdit = 0;
        GKeys.Clear();
        _effectCts?.Cancel();
        _effectCts = null;
        Hosts.Clear();
        Profiles.Clear();
        _profileCount = 0;
        DpiPresets.Clear();
        // Capability-gated tabs, set synchronously from the scan-time feature set so
        // they appear immediately (Buttons needs a mouse/trackball; Pointer needs DPI;
        // G-keys content still loads in the background).
        HasButtonsTab = value?.HasButtons == true
            && value.Device.Kind is DeviceKind.Mouse or DeviceKind.Trackball or DeviceKind.Unknown;
        HasPointerTab = value?.HasPointer == true;
        HasLightingTab = value?.HasLighting == true;
        HasGKeysTab = value?.HasGKeys == true;
        _ = LoadControlsAsync(value);

        if (value?.ConfigKey is not { } configKey)
            return;

        // Seed lighting controls from the saved config (device read is optional).
        _loadingControls = true;
        var lighting = _config.Lighting(configKey);
        LightingEnabled = lighting?.Enabled ?? true;
        LightingBrightness = lighting?.Brightness ?? 100;
        LightingColor = ParseHexColor(lighting?.Color);
        SelectedEffect = LightingEffect.Solid;
        _loadingControls = false;

        var current = BindingMaps.BindingsFor(_config, configKey, null);
        foreach (var button in ButtonIdExtensions.All)
        {
            if (button == ButtonId.GestureButton)
            {
                _bindings[button] = BuildGestureBinding(configKey);
                continue;
            }
            var action = current.TryGetValue(button, out var a) ? a : Core.Bindings.DefaultBinding(button);
            _bindings[button] = new ButtonBindingViewModel(button, action, ButtonBindingViewModel.Catalog,
                (b, act) => Persist(configKey, b, act));
        }
        RefreshGestureSummaries(configKey);

        // Only surface the button pickers / diagram for devices that actually have
        // remappable buttons (a mouse), not e.g. a HID++ microphone or a keyboard.
        // The button list is derived from the device's real buttons in BuildDiagramAsync.
        if (value.HasButtons)
            _ = BuildDiagramAsync(value, configKey);
    }

    private async Task BuildDiagramAsync(DeviceViewModel device, string configKey)
    {
        ResolvedAsset? resolved = null;
        try { resolved = await _resolver.ResolveAsync(configKey, device.Device.Codename, device.Ext); }
        catch { /* offline / no depot — fall back to the default button set below */ }

        if (!ReferenceEquals(SelectedDevice, device)) // selection moved on while we awaited
            return;

        IReadOnlyList<Hotspot> hotspots = [];
        if (resolved?.ButtonsImagePath is { } buttonsPath && File.Exists(buttonsPath))
        {
            try
            {
                var bitmap = new Bitmap(buttonsPath);
                var pngW = bitmap.PixelSize.Width;
                var pngH = bitmap.PixelSize.Height;
                var displayH = DiagramHeightPx;
                var displayW = pngH > 0 ? DiagramHeightPx * pngW / pngH : DiagramHeightPx;

                hotspots = MouseGeometry.HotspotsForPng(resolved.Metadata, displayW, displayH, pngW, pngH);
                BuildAnnotations(hotspots, displayW, displayH, configKey);

                DiagramImage = bitmap;
            }
            catch { hotspots = []; }
        }

        // Derive the side button list from the device's actual buttons (the mapped
        // hotspots), falling back to a default mouse set when no metadata is available.
        var buttonIds = hotspots.Count > 0
            ? hotspots.Select(h => h.Id).Distinct().OrderBy(b => (int)b).ToArray()
            : DefaultMouseButtons;

        Buttons.Clear();
        foreach (var id in buttonIds)
            Buttons.Add(BindingFor(id, configKey));

        // Annotations may be (re)built after the gesture section chose its owner.
        RefreshGestureHighlight();
    }

    // Leader-line layout: a left label column, a gap, then the render. Each
    // hotspot gets a marker over the render and a label connected by a polyline.
    private const double LabelColumnWidth = 168;
    private const double LabelGap = 28;
    private const double LeaderStub = 10;

    private void BuildAnnotations(IReadOnlyList<Hotspot> hotspots, double displayW, double displayH, string configKey)
    {
        var labelYs = MouseGeometry.LabelYs(hotspots, displayH);
        var mouseX = LabelColumnWidth + LabelGap;

        Annotations.Clear();
        for (var i = 0; i < hotspots.Count; i++)
        {
            var h = hotspots[i];
            var markerX = mouseX + h.X;
            var markerY = h.Y;
            var centerX = markerX + h.Size / 2;
            var centerY = markerY + h.Size / 2;
            var anchorY = labelYs[i];

            var center = new Avalonia.Point(centerX, centerY);
            var stub = new Avalonia.Point(mouseX - LeaderStub, centerY);
            var anchor = new Avalonia.Point(LabelColumnWidth, anchorY);

            Annotations.Add(new DiagramAnnotationViewModel(
                BindingFor(h.Id, configKey),
                markerX, markerY, h.Size,
                labelX: 0, labelY: anchorY - 16, labelWidth: LabelColumnWidth - 6,
                center, stub, anchor));
        }

        ImageX = mouseX;
        ImageWidth = displayW;
        DiagramWidth = mouseX + displayW;
        DiagramHeight = displayH;
    }

    private ButtonBindingViewModel BindingFor(ButtonId id, string configKey)
    {
        if (!_bindings.TryGetValue(id, out var binding))
        {
            binding = new ButtonBindingViewModel(id, Core.Bindings.DefaultBinding(id), ButtonBindingViewModel.Catalog,
                (b, act) => Persist(configKey, b, act));
            _bindings[id] = binding;
        }
        return binding;
    }

    /// <summary>
    /// Open a persistent session for every connected mouse and divert its
    /// DPI/ModeShift button — and its dedicated gesture button (0x00c3) when that
    /// button owns gestures — so each mouse's HID++ overrides work whenever the app
    /// runs — independent of which page (if any) is open — and each dispatches its
    /// own bindings. The OS hook (Middle/Back/Forward) is global and can't tell mice
    /// apart, so it tracks the first mouse. Sessions are reused by
    /// <see cref="LoadControlsAsync"/> when viewing that mouse, so only one HID
    /// handle is ever held per device.
    /// </summary>
    private async Task ActivateAgentMiceAsync(IReadOnlyList<DeviceViewModel> mice)
    {
        var old = _mouseCaptures.ToArray();
        _mouseCaptures.Clear();
        foreach (var mc in old)
        {
            await mc.Capture.DisposeAsync();
            // Don't dispose a session the UI is still showing — LoadControlsAsync owns that.
            if (!ReferenceEquals(mc.Session, _session)) await mc.Session.DisposeAsync();
        }

        // The global OS hook applies one mouse's Middle/Back/Forward bindings.
        _agent.SetSelectedDevice(mice.FirstOrDefault()?.ConfigKey);

        foreach (var mouse in mice)
        {
            if (mouse.Route is not { } route) continue;
            try
            {
                var session = await DeviceSession.OpenAsync(route);
                if (session is null) continue;
                var ck = mouse.ConfigKey; // capture this mouse's bindings, not the global selection
                // The native scroll-invert bit (0x2121) is volatile — restore the
                // persisted preference whenever the mouse (re)connects. No-op if unsupported.
                if (ck is not null) await session.ApplyScrollInvertAsync(_config.InvertScroll(ck));

                var captures = await StartMouseCapturesAsync(session, ck);
                if (captures.Count == 0) { await session.DisposeAsync(); continue; } // nothing capturable
                _mouseCaptures.Add(new MouseCapture(mouse, session, new CompositeCapture(captures)));
            }
            catch { /* mouse unreachable */ }
        }
    }

    /// <summary>
    /// Start a mouse's HID++ captures on an open <paramref name="session"/>, honouring
    /// its gesture owner: the DPI/ModeShift button (unless that button is itself the
    /// gesture owner) plus the gesture owner's control diverted with raw-XY. Shared by
    /// the initial activation and the owner-change restart.
    /// </summary>
    private async Task<List<IAsyncDisposable>> StartMouseCapturesAsync(DeviceSession session, string? ck)
    {
        var gestureButtons = ck is not null ? _config.GestureButtons(ck) : [];
        var captures = new List<IAsyncDisposable>();
        // Capture the DPI/ModeShift button — unless it has gestures configured, in
        // which case the gesture capture below diverts the same control instead.
        if (!gestureButtons.Contains(ButtonId.DpiToggle)
            && await session.StartDpiButtonCaptureAsync(() => _agent.DispatchDivertedButton(ck, ButtonId.DpiToggle)) is { } dpi)
            captures.Add(dpi);
        // Divert every gesture-configured button's control with raw-XY — several may
        // gesture at once (including Middle/Back/Forward, which on Windows also
        // gesture over HID++). A button the device can't divert is a null no-op.
        if (ck is not null)
            foreach (var button in gestureButtons)
            {
                var b = button; // each capture dispatches its own button's map
                if (await session.StartGestureCaptureAsync(b, dir => _agent.DispatchGesture(ck, b, dir)) is { } gesture)
                    captures.Add(gesture);
            }
        // Divert the wheel into hi-res mode and re-inject its motion as smooth OS
        // scrolling when the user turned it on (a null capture = no 0x2121).
        if (ck is not null && _config.SmoothScroll(ck)
            && await session.StartSmoothScrollCaptureAsync(w => _agent.DispatchSmoothScroll(w)) is { } smooth)
            captures.Add(smooth);
        return captures;
    }

    /// <summary>
    /// Re-arm the currently-viewed mouse's captures on its existing session after a
    /// divert-affecting setting changed (gesture owner, smooth scrolling) — so the
    /// newly-chosen control gets diverted (and the old one released). Runs on the
    /// persistent session the UI already holds, so no second HID handle is opened.
    /// A no-op for a mouse with no persistent capture session.
    /// </summary>
    private async Task RestartGestureCaptureForSelectedAsync()
    {
        if (SelectedDevice is not { } device || device.ConfigKey is null) return;
        var mc = _mouseCaptures.FirstOrDefault(m => ReferenceEquals(m.Device, device));
        if (mc is null)
        {
            // No persistent capture yet (nothing was capturable at activation) — a
            // newly-enabled capture can still ride the session the UI holds open;
            // adding it to _mouseCaptures makes that session persistent.
            if (_session is null) return;
            var fresh = await StartMouseCapturesAsync(_session, device.ConfigKey);
            if (fresh.Count > 0)
                _mouseCaptures.Add(new MouseCapture(device, _session, new CompositeCapture(fresh)));
            return;
        }

        await mc.Capture.DisposeAsync();
        _mouseCaptures.Remove(mc);
        var captures = await StartMouseCapturesAsync(mc.Session, device.ConfigKey);
        if (captures.Count > 0)
            _mouseCaptures.Add(mc with { Capture = new CompositeCapture(captures) });
    }

    /// <summary>
    /// Open a session for a device that may be waking from power-saving. A wireless
    /// keyboard that's asleep enumerates only partially, so the first open can land on
    /// an interface missing the control features (OnboardProfiles 0x8100 / G-keys
    /// 0x8010 / per-key 0x8081) — leaving the Profiles, G-Keys and battery readouts
    /// empty. So for a keyboard we keep re-opening (with backoff) until the session
    /// actually exposes a control feature, or the open fails outright. This keys off
    /// the SESSION's own features, not the flaky startup-scan capability guess, so it
    /// fires even when the scan itself missed the control interface. The repeated
    /// traffic wakes the device — automating the manual Refresh that used to be
    /// needed. Zero extra latency for an already-awake device (returns on first open).
    /// </summary>
    private async Task<DeviceSession?> OpenWokenAsync(DeviceViewModel device, DeviceRoute route)
    {
        const int attempts = 6;
        var isKeyboard = device.Device.Kind == DeviceKind.Keyboard;
        DeviceSession? session = null;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try { session = await DeviceSession.OpenAsync(route); }
            catch { session = null; }

            // The user navigated away while we were opening — discard and bail.
            if (!ReferenceEquals(SelectedDevice, device))
            {
                if (session is not null) await session.DisposeAsync();
                return null;
            }

            // Good when the open succeeded and either it isn't a keyboard or this
            // interface actually exposes a control feature (profiles/G-keys/per-key).
            var hasControl = session is not null
                && (session.SupportsOnboardProfiles || session.SupportsGKeys || session.SupportsPerKey);
            if (session is not null && (!isKeyboard || hasControl))
                return session;
            if (attempt == attempts - 1)
                return session;

            // Wrong/partial interface (or no answer at all): wake it and retry.
            if (session is not null) await session.DisposeAsync();
            session = null;
            await Task.Delay(Math.Min(200 * (attempt + 1), 600)).ConfigureAwait(false);
        }
        return session;
    }

    private async Task LoadControlsAsync(DeviceViewModel? device)
    {
        // Stop the previous device's live battery listener before switching.
        if (_batteryMonitor is not null) { await _batteryMonitor.DisposeAsync(); _batteryMonitor = null; }

        var old = _session;
        _session = null;
        // Persistent per-mouse sessions (which run the DPI-button capture) are owned by
        // ActivateAgentMiceAsync — never dispose them here, only throwaway sessions.
        if (old is not null && !IsPersistentSession(old)) await old.DisposeAsync();

        if (device?.Route is not { } route) { IsLoadingDevice = false; return; }
        IsLoadingDevice = true; // show the thin loading line while the device's controls load

        // Reuse this mouse's already-open persistent session instead of opening a
        // second HID handle to the same device.
        DeviceSession? session;
        var persistent = _mouseCaptures.FirstOrDefault(mc => ReferenceEquals(mc.Device, device))?.Session;
        if (persistent is not null)
            session = persistent;
        else
            session = await OpenWokenAsync(device, route);
        if (session is null) { IsLoadingDevice = false; return; }
        if (!ReferenceEquals(SelectedDevice, device))
        {
            if (!IsPersistentSession(session)) await session.DisposeAsync();
            IsLoadingDevice = false;
            return;
        }
        _session = session;
        ShowPerKeyEditor = session.SupportsPerKey;

        // Battery: read once on open (benefits from the wake-retry), then subscribe to
        // 0x1004 broadcasts so the card/detail update live while the app is open.
        if (await session.ReadBatteryAsync() is { } battery && ReferenceEquals(SelectedDevice, device))
            device.LiveBattery = battery;
        if (ReferenceEquals(SelectedDevice, device))
            _batteryMonitor = session.StartBatteryMonitor(info =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (ReferenceEquals(SelectedDevice, device)) device.LiveBattery = info;
                }));

        _loadingControls = true;
        try
        {
            if (await session.ReadDpiAsync() is { } dpi && ReferenceEquals(SelectedDevice, device) && dpi.Supported.Count > 0)
            {
                _dpiOptions = [.. dpi.Supported.Select(s => (uint)s)];
                DpiMin = _dpiOptions.Min();
                DpiMax = _dpiOptions.Max();
                DpiStep = SmallestGap(_dpiOptions);
                DpiValue = dpi.Current;
                DpiRangeText = $"{(uint)DpiMin}–{(uint)DpiMax} · step {(uint)DpiStep}";
                LoadDpiPresets(device.ConfigKey);
                ShowDpi = true;
            }
            if (await session.ReadSmartShiftAsync() is { } ss && ReferenceEquals(SelectedDevice, device))
            {
                WheelModeChoice = !ss.Ratchet ? WheelModeChoice.FreeSpin
                    : ss.AutoDisengage == 0xFF ? WheelModeChoice.Ratchet
                    : WheelModeChoice.SmartShift;
                AutoDisengage = ss.AutoDisengage is 0 or 0xFF ? 16 : ss.AutoDisengage;
                ShowSmartShift = true;
            }
            if (await session.ReadScrollInvertAsync() is { } inverted && ReferenceEquals(SelectedDevice, device))
            {
                // The device's live state reflects the persisted preference (reapplied
                // on connect, since the 0x2121 invert bit is volatile).
                InvertScroll = inverted;
                ShowScrollInvert = true;
                // Smooth scrolling rides the same 0x2121 feature; its on/off is the
                // persisted choice (the capture applies it, so the config is the truth).
                SmoothScroll = device.ConfigKey is { } sck && _config.SmoothScroll(sck);
                ShowSmoothScroll = true;
            }
            await BuildGestureSectionAsync(session, device);
            if (await session.ReadHostsAsync() is { } hosts && ReferenceEquals(SelectedDevice, device))
                RebuildHosts(hosts);
            if (await session.ReadBrightnessAsync() is { } bright && ReferenceEquals(SelectedDevice, device))
            {
                BacklightMax = bright.Info.MaxBrightness == 0 ? 100 : bright.Info.MaxBrightness;
                BacklightValue = bright.Current;
                ShowBacklight = true;
            }
            byte gkeyProfile = 1;
            if (await session.ReadProfilesAsync() is { } prof && ReferenceEquals(SelectedDevice, device))
            {
                _profileCount = prof.Info.ProfileCount;
                RebuildProfiles(prof.Current);
                ShowProfiles = prof.Info.ProfileCount >= 1;
                if (prof.Current >= 1 && ReferenceEquals(SelectedDevice, device))
                    await LoadProfileLightingAsync(prof.Current);
                gkeyProfile = Math.Max(prof.Current, (byte)1);
            }

            // Read G-keys independently of the profile read: a failed/empty profile
            // read must not also hide the G-keys (they share the control interface but
            // not the read). They only need the active profile sector, which defaults
            // to 1 when the profile read didn't yield one.
            if (session.SupportsGKeys && ReferenceEquals(SelectedDevice, device))
            {
                _gkeyProfileSector = gkeyProfile;
                GKeyProfile = _gkeyProfileSector;
                if (await session.ReadGKeyBindingsAsync(_gkeyProfileSector) is { } bindings
                    && ReferenceEquals(SelectedDevice, device))
                {
                    GKeys.Clear();
                    for (var i = 0; i < bindings.Length; i++)
                        GKeys.Add(new GKeyViewModel(i, bindings[i].Usage, bindings[i].Modifier, OnGKeyChanged));
                    HasGKeysTab = true;
                }
            }
        }
        finally { _loadingControls = false; IsLoadingDevice = false; }
    }

    /// <summary>Switch the device to another EasySwitch host (disconnects it from this PC).</summary>
    [RelayCommand]
    private async Task SwitchHost(HostSlotViewModel? slot)
    {
        if (slot is null || slot.IsCurrent || _session is null) return;
        StatusText = $"Switching to host {slot.Number}…";
        await _session.SwitchHostAsync((byte)slot.Index);
    }

    /// <summary>
    /// Forget (clear) an EasySwitch host so its slot is freed; that computer must
    /// re-pair. Confirmation is handled by the caller (the view shows a warning
    /// dialog, with a stronger one for the current host).
    /// </summary>
    public async Task ForgetHostAsync(HostSlotViewModel slot)
    {
        if (_session is null || SelectedDevice is not { } device) return;
        var wasCurrent = slot.IsCurrent;
        StatusText = $"Forgetting host {slot.Number}…";
        if (!await _session.ClearHostAsync((byte)slot.Index))
        {
            StatusText = $"Could not forget host {slot.Number}.";
            return;
        }
        if (wasCurrent)
        {
            // The device just dropped off this computer — return to the gallery and rescan.
            StatusText = $"Host {slot.Number} forgotten — device disconnected.";
            await RefreshAsync();
            return;
        }
        // Refresh so the freed slot shows as empty.
        if (await _session.ReadHostsAsync() is { } hosts && ReferenceEquals(SelectedDevice, device))
            RebuildHosts(hosts);
        StatusText = $"Host {slot.Number} forgotten.";
    }

    private void RebuildHosts(HostSnapshot hosts)
    {
        Hosts.Clear();
        foreach (var hd in hosts.Hosts)
            Hosts.Add(new HostSlotViewModel(hd.Index, hd.IsCurrent, hd.Paired, hd.BusType, hd.Name, hosts.SupportsDelete));
        HostsSupportClear = hosts.SupportsDelete;
        ShowHosts = hosts.HostCount > 1;
    }

    partial void OnDpiValueChanged(double value)
    {
        if (_loadingControls) return;
        var snapped = Snap((uint)Math.Round(value));
        _ = ApplyDpiAsync(snapped);
    }

    private async Task ApplyDpiAsync(uint dpi)
    {
        if (_session is not null) await _session.ApplyDpiAsync((ushort)dpi);
        if (SelectedDevice?.ConfigKey is { } ck) { _config.SetDpi(ck, dpi); SaveConfig(); }
    }

    /// <summary>Snap a DPI value to the nearest device-supported value.</summary>
    private uint Snap(uint dpi)
    {
        if (_dpiOptions.Count == 0) return dpi;
        return _dpiOptions.MinBy(v => Math.Abs((long)v - dpi));
    }

    private static double SmallestGap(IReadOnlyList<uint> options)
    {
        var sorted = options.OrderBy(v => v).ToArray();
        var gap = uint.MaxValue;
        for (var i = 1; i < sorted.Length; i++)
            gap = Math.Min(gap, sorted[i] - sorted[i - 1]);
        return sorted.Length < 2 || gap == 0 ? 50 : gap;
    }

    private void LoadDpiPresets(string? configKey)
    {
        DpiPresets.Clear();
        if (configKey is null) return;
        foreach (var p in _config.DpiPresets(configKey)) DpiPresets.Add(p);
    }

    [RelayCommand]
    private void AddDpiPreset()
    {
        var dpi = Snap((uint)Math.Round(DpiValue));
        DpiPresets.Add(dpi);
        SaveDpiPresets();
    }

    [RelayCommand]
    private void ApplyDpiPreset(uint dpi)
    {
        DpiValue = dpi; // triggers snap + apply via OnDpiValueChanged
    }

    [RelayCommand]
    private void RemoveDpiPreset(uint dpi)
    {
        DpiPresets.Remove(dpi);
        SaveDpiPresets();
    }

    private void SaveDpiPresets()
    {
        if (SelectedDevice?.ConfigKey is { } ck) { _config.SetDpiPresets(ck, [.. DpiPresets]); SaveConfig(); }
    }

    partial void OnWheelModeChoiceChanged(WheelModeChoice value) { if (!_loadingControls) _ = ApplySmartShiftAsync(); }
    partial void OnAutoDisengageChanged(int value) { if (!_loadingControls) _ = ApplySmartShiftAsync(); }

    private async Task ApplySmartShiftAsync()
    {
        // Free spin → wheel mode Free. SmartShift → Ratchet with a finite
        // auto-disengage threshold. Ratchet → Ratchet that never disengages (0xFF).
        var ratchet = WheelModeChoice != WheelModeChoice.FreeSpin;
        var auto = WheelModeChoice switch
        {
            WheelModeChoice.SmartShift => (byte)AutoDisengage,
            WheelModeChoice.Ratchet => (byte)0xFF,
            _ => (byte)0,
        };
        if (_session is not null) await _session.ApplySmartShiftAsync(ratchet, auto);
        if (SelectedDevice?.ConfigKey is { } ck)
        {
            _config.SetSmartShift(ck, new SmartShift
            {
                Mode = ratchet ? WheelMode.Ratchet : WheelMode.Free,
                AutoDisengage = auto,
                TunableTorque = 0,
            });
            SaveConfig();
        }
    }

    partial void OnInvertScrollChanged(bool value) { if (!_loadingControls) _ = ApplyScrollInvertAsync(value); }

    private async Task ApplyScrollInvertAsync(bool invert)
    {
        if (_session is not null) await _session.ApplyScrollInvertAsync(invert);
        if (SelectedDevice?.ConfigKey is { } ck) { _config.SetInvertScroll(ck, invert); SaveConfig(); }
    }

    partial void OnSmoothScrollChanged(bool value) { if (!_loadingControls) _ = ApplySmoothScrollAsync(value); }

    private async Task ApplySmoothScrollAsync(bool enabled)
    {
        if (SelectedDevice?.ConfigKey is { } ck) { _config.SetSmoothScroll(ck, enabled); SaveConfig(); }
        // Re-arm the persistent captures so the wheel is diverted (or released) now,
        // not on the next reconnect.
        await RestartGestureCaptureForSelectedAsync();
    }

    partial void OnBacklightValueChanged(double value)
    {
        if (_loadingControls || _session is null) return;
        _ = _session.ApplyBrightnessAsync((ushort)Math.Round(value));
    }

    /// <summary>Hand lighting back to the keyboard's onboard profile (its built-in effect/cycle).</summary>
    [RelayCommand]
    private async Task RestoreKeyboardDefault()
    {
        if (_session is not null) await _session.SetOnboardModeAsync(host: false);
    }

    /// <summary>Rebuild the profile list: a "No profile" entry (custom colour) + each onboard profile.</summary>
    private void RebuildProfiles(byte current)
    {
        Profiles.Clear();
        Profiles.Add(new ProfileSlotViewModel(0, current == 0)); // "No profile" = custom colour
        for (var i = 1; i <= _profileCount; i++)
            Profiles.Add(new ProfileSlotViewModel(i, i == current));
        SelectedProfileForEdit = current;
        ProfileSelected = current >= 1;
    }

    /// <summary>
    /// Populate the effect/colour/speed/brightness controls from a profile's stored
    /// lighting descriptor, so selecting a profile reflects what it's actually
    /// configured to do. No-op for "No profile" (0) or unreadable profiles.
    /// </summary>
    private async Task LoadProfileLightingAsync(int profileNumber)
    {
        if (_session is null || profileNumber < 1) return;
        if (await _session.ReadProfileEffectAsync((ushort)profileNumber) is not { } pl) return;
        var prev = _loadingControls;
        _loadingControls = true; // the partial handlers below would otherwise drive live lighting
        try
        {
            SelectedEffect = pl.Effect switch
            {
                DeviceSession.EffectOff => LightingEffect.Off,
                DeviceSession.EffectBreathing => LightingEffect.Breathing,
                DeviceSession.EffectCycle => LightingEffect.Cycle,
                _ => LightingEffect.Solid,
            };
            if (SelectedEffect is LightingEffect.Solid or LightingEffect.Breathing)
                LightingColor = Avalonia.Media.Color.FromRgb(pl.R, pl.G, pl.B);
            if (SelectedEffect == LightingEffect.Breathing && pl.PeriodMs > 0)
                LightingSpeed = pl.PeriodMs;
            if (SelectedEffect is LightingEffect.Breathing or LightingEffect.Cycle)
                LightingBrightness = pl.Brightness;
        }
        finally { _loadingControls = prev; }
    }

    /// <summary>Write a remapped G-key into the active onboard profile (persists on-device).</summary>
    private void OnGKeyChanged(int index, byte usage, byte modifier)
    {
        if (_session is null || _gkeyProfileSector < 1) return;
        StatusText = $"Remapping G{index + 1}…";
        _ = WriteGKeyAsync(index, usage, modifier);
    }

    private async Task WriteGKeyAsync(int index, byte usage, byte modifier)
    {
        var ok = _session is not null && await _session.SetGKeyUsageAsync(_gkeyProfileSector, index, usage, modifier);
        StatusText = ok ? $"G{index + 1} remapped (profile {_gkeyProfileSector})." : "G-key remap failed.";
    }

    /// <summary>Persist the selected effect + colour + speed/brightness into the profile's flash (device-side).</summary>
    [RelayCommand]
    private async Task SaveLightingToProfile()
    {
        if (!ProfileSelected || _session is null || SelectedProfileForEdit < 1) return;
        var c = LightingColor;
        var effect = SelectedEffect switch
        {
            LightingEffect.Off => DeviceSession.EffectOff,
            LightingEffect.Breathing => DeviceSession.EffectBreathing,
            LightingEffect.Cycle => DeviceSession.EffectCycle,
            _ => DeviceSession.EffectFixed,
        };
        StatusText = $"Saving lighting to profile {SelectedProfileForEdit}…";
        var ok = await _session.SetProfileEffectAsync((ushort)SelectedProfileForEdit, effect,
            c.R, c.G, c.B, (ushort)LightingSpeed, (byte)LightingBrightness);
        StatusText = ok ? $"Saved to profile {SelectedProfileForEdit}." : "Profile save failed.";
    }

    /// <summary>Mark "No profile" active (a custom colour/effect is driving the keyboard).</summary>
    private void ClearProfileSelection()
    {
        if (_profileCount > 0) RebuildProfiles(0);
    }

    /// <summary>
    /// Select a lighting source: an onboard profile (smooth, device-side, survives app
    /// close) or "No profile" (drop to host mode + apply the configured custom colour).
    /// </summary>
    [RelayCommand]
    private async Task SwitchProfile(ProfileSlotViewModel? slot)
    {
        if (slot is null || slot.IsCurrent || _session is null) return;
        _effectCts?.Cancel();
        _effectCts = null;

        if (slot.Number == 0)
        {
            RebuildProfiles(0);                  // "No profile" selected
            await ApplyNoProfileLightingAsync(); // saved per-key colors, else solid
            return;
        }

        await _session.SwitchProfileAsync((byte)slot.Number); // ensures onboard mode internally
        if (await _session.ReadProfilesAsync() is { } prof)
        {
            RebuildProfiles(prof.Current);
            await LoadProfileLightingAsync(prof.Current);
        }
    }

    // Live host-mode lighting only applies in "No profile" mode; with a real profile
    // selected the colour picker instead edits that profile (saved via SaveColorToProfile).
    partial void OnLightingEnabledChanged(bool value) { if (!_loadingControls && !ProfileSelected) RestartLighting(); }
    partial void OnLightingColorChanged(Avalonia.Media.Color value) { if (!_loadingControls && !ProfileSelected) RestartLighting(); }
    partial void OnLightingBrightnessChanged(int value) { if (!_loadingControls && !ProfileSelected) RestartLighting(); }
    partial void OnSelectedEffectChanged(LightingEffect value) { if (!_loadingControls && !ProfileSelected) RestartLighting(); }

    /// <summary>Persist + apply the live "No profile" lighting (always a solid colour; app-driven animations are disabled).</summary>
    private void RestartLighting()
    {
        SaveLightingConfig();
        ClearProfileSelection(); // a live custom colour means no onboard profile is active
        _ = ApplySolidAsync();
    }

    /// <summary>
    /// Apply the "No profile" lighting: the saved per-key colors (base fill + painted
    /// keys) when the device supports per-key and any are saved, else the solid custom
    /// colour. Lets selecting "No profile" restore custom key colors without opening
    /// the editor. Per-key colors are applied at full value (matching the editor), so
    /// the Intensity slider doesn't scale them.
    /// </summary>
    private async Task ApplyNoProfileLightingAsync()
    {
        if (_session is null) return;
        var ck = SelectedDevice?.ConfigKey;
        var perKey = ck is not null ? _config.Lighting(ck)?.PerKey : null;
        if (LightingEnabled && _session.SupportsPerKey && perKey is { Count: > 0 })
        {
            var b = LightingColor; // base = the unpainted-key colour
            var map = new Dictionary<byte, (byte R, byte G, byte B)>(perKey.Count);
            foreach (var (zone, hex) in perKey)
            {
                var c = ParseHexColor(hex);
                map[zone] = (c.R, c.G, c.B);
            }
            await _session.ApplyPerKeyMapAsync(b.R, b.G, b.B, map);
        }
        else
        {
            await ApplySolidAsync();
        }
    }

    private async Task ApplySolidAsync()
    {
        if (_session is null) return;
        byte r = 0, g = 0, b = 0;
        if (LightingEnabled)
        {
            var c = LightingColor;
            var scale = LightingBrightness / 100.0;
            r = (byte)Math.Clamp(c.R * scale, 0, 255);
            g = (byte)Math.Clamp(c.G * scale, 0, 255);
            b = (byte)Math.Clamp(c.B * scale, 0, 255);
        }
        await _session.ApplyLightingAsync(r, g, b);
    }

    private void SaveLightingConfig()
    {
        if (SelectedDevice?.ConfigKey is not { } ck) return;
        var c = LightingColor;
        var existing = _config.Lighting(ck);
        _config.SetLighting(ck, new Lighting
        {
            Enabled = LightingEnabled,
            Color = $"{c.R:x2}{c.G:x2}{c.B:x2}",
            Brightness = (byte)LightingBrightness,
            PaintColor = existing?.PaintColor,
            PerKey = existing?.PerKey ?? new Dictionary<byte, string>(),
        });
        SaveConfig();
    }

    private static Avalonia.Media.Color ParseHexColor(string? hex)
    {
        if (hex is { Length: 6 }
            && byte.TryParse(hex[..2], System.Globalization.NumberStyles.HexNumber, null, out var r)
            && byte.TryParse(hex[2..4], System.Globalization.NumberStyles.HexNumber, null, out var g)
            && byte.TryParse(hex[4..6], System.Globalization.NumberStyles.HexNumber, null, out var b))
            return Avalonia.Media.Color.FromRgb(r, g, b);
        return Avalonia.Media.Colors.White;
    }

    private void SaveConfig()
    {
        try { _config.SaveAtomic(); } catch { /* keep editing fluid */ }
    }

    /// <summary>Tear down the remap hook and any open device session on app exit.</summary>
    public void Dispose()
    {
        _agent.Dispose();
        _ = _batteryMonitor?.DisposeAsync();
        foreach (var mc in _mouseCaptures)
        {
            _ = mc.Capture.DisposeAsync();
            if (!ReferenceEquals(mc.Session, _session)) _ = mc.Session.DisposeAsync();
        }
        if (_session is not null && !IsPersistentSession(_session))
            _ = _session.DisposeAsync();
    }

    private async Task ResolveCardImageAsync(DeviceViewModel vm)
    {
        try
        {
            var path = await _resolver.ResolveFrontRenderAsync(vm.ConfigKey!, vm.Device.Codename, vm.Ext);
            if (path is not null && File.Exists(path))
                vm.Image = new Bitmap(path);
        }
        catch { /* no render available — UI shows none */ }
    }

    private void Persist(string configKey, ButtonId button, OpenLogi.Core.Action action)
    {
        _config.SetBinding(configKey, button, new Binding.Single(action));
        try { _config.SaveAtomic(); }
        catch { /* keep editing fluid */ }
    }

    /// <summary>
    /// Build the gesture button's five-direction editor. Seeds each direction from
    /// the device's stored gesture map (with built-in defaults filled in) so the
    /// picker always shows the full ↑ ↓ ← → + Click set.
    /// </summary>
    private ButtonBindingViewModel BuildGestureBinding(string configKey)
    {
        var directions = BuildDirectionEditors(configKey, ButtonId.GestureButton);
        return new ButtonBindingViewModel(ButtonId.GestureButton, directions, ButtonBindingViewModel.Catalog);
    }

    /// <summary>
    /// One <see cref="GestureDirectionBindingViewModel"/> per direction for
    /// <paramref name="owner"/>'s editor, persisting to that button. Seeds: the
    /// button's stored gesture map; for an unconfigured button, Click shows its
    /// current effective action and the swipes show Do Nothing (nothing is active
    /// until the user actually configures something). The dedicated gesture button
    /// falls back to the built-in defaults instead — it gestures out of the box.
    /// </summary>
    private List<GestureDirectionBindingViewModel> BuildDirectionEditors(string configKey, ButtonId owner)
    {
        var stored = _config.GestureBindingsFor(configKey, owner);
        var clickSeed = BindingMaps.BindingsFor(_config, configKey, null).TryGetValue(owner, out var click)
            ? click
            : Core.Bindings.DefaultBinding(owner);
        return GestureDirectionExtensions.All
            .Select(d =>
            {
                var action = stored.TryGetValue(d, out var ga) ? ga
                    : owner == ButtonId.GestureButton ? Core.Bindings.DefaultGestureBinding(d)
                    : d == GestureDirection.Click ? clickSeed
                    : OpenLogi.Core.Action.None;
                return new GestureDirectionBindingViewModel(d, action, ButtonBindingViewModel.Catalog,
                    (dir, act) => PersistGesture(configKey, owner, dir, act));
            })
            .ToList();
    }

    private void PersistGesture(string configKey, ButtonId owner, GestureDirection direction, OpenLogi.Core.Action action)
    {
        // Writes one direction into the owner button's gesture map (upgrading it to a
        // gesture binding if needed). A button's FIRST gesture edit is what activates
        // it, so re-arm the captures to divert its control.
        var wasActive = _config.GestureButtons(configKey).Contains(owner);
        _config.SetGestureDirection(configKey, owner, direction, action);
        try { _config.SaveAtomic(); }
        catch { /* keep editing fluid */ }
        if (!wasActive)
            _ = RestartGestureCaptureForSelectedAsync();
        RefreshGestureSummaries(configKey);
        if (!_suppressGesturePanel)
        {
            // One user edit (click or a single swipe) = one undo step.
            PushGestureUndo();
            // A hand-edited swipe leaves any preset it deviates from: reflect Custom
            // (or the preset it happens to complete) in the Gestures dropdown.
            if (direction != GestureDirection.Click)
            {
                _suppressGesturePanel = true;
                SelectedGestureCategory = MatchGestureCategory();
                _suppressGesturePanel = false;
            }
        }
    }

    /// <summary>Friendly label for a button offered as a gesture trigger.</summary>
    private static string GestureOwnerLabel(ButtonId button) => button switch
    {
        ButtonId.GestureButton => "Gesture button",
        ButtonId.DpiToggle => "Wheel / DPI button",
        _ => button.Label(),
    };

    /// <summary>
    /// Populate the Gestures section for the selected mouse: the owner dropdown (Off +
    /// the device's HID++-capturable gesture buttons) and the five-direction editor.
    /// Hidden when the device exposes no eligible gesture control.
    /// </summary>
    private async Task BuildGestureSectionAsync(DeviceSession session, DeviceViewModel device)
    {
        var eligible = await session.GestureCapableButtonsAsync();
        if (!ReferenceEquals(SelectedDevice, device)) return;
        if (device.ConfigKey is not { } ck || eligible.Count == 0)
        {
            ShowGestures = false;
            return;
        }

        _suppressGestureOwner = true;
        _suppressGesturePanel = true;
        GestureOwnerChoices.Clear();
        foreach (var b in eligible)
            GestureOwnerChoices.Add(new GestureOwnerChoice(b, GestureOwnerLabel(b)));

        GesturesEnabled = _config.GesturesEnabled(ck);
        var owner = _config.GestureOwner(ck);
        SelectedGestureOwner = GesturesEnabled && owner is { } o && eligible.Contains(o)
            ? GestureOwnerChoices.First(c => c.Button == o)
            : null;
        _suppressGesturePanel = false;
        RebuildGestureDirections(ck);
        ShowGestures = true;
        _suppressGestureOwner = false;
        RefreshGestureHighlight();
    }

    private void RebuildGestureDirections(string configKey)
    {
        _suppressGesturePanel = true;
        GestureDirections.Clear();
        // A (re)built editor edits a different button (or none): undo starts fresh.
        _gestureUndo.Clear();
        _lastGestureState = null;
        if (SelectedGestureOwner?.Button is not { } owner)
        {
            // No button selected — no click editor, no category, no swipes.
            GestureClick = null;
            GestureOwnerSelected = false;
            SelectedGestureCategory = null;
            _suppressGesturePanel = false;
            return;
        }
        GestureOwnerSelected = true;
        var editors = BuildDirectionEditors(configKey, owner);
        // Click (the plain tap) is its own row under the owner dropdown; the four
        // swipes live in the list below the Gestures (category) dropdown.
        GestureClick = editors.First(v => v.Direction == GestureDirection.Click);
        foreach (var vm in editors.Where(v => v.Direction != GestureDirection.Click))
            GestureDirections.Add(vm);
        SelectedGestureCategory = MatchGestureCategory();
        _suppressGesturePanel = false;
        _lastGestureState = CurrentGestureSnapshot();
    }

    /// <summary>The five current editor actions (click first, then ↑↓←→), or null with no editor.</summary>
    private (ButtonId Owner, OpenLogi.Core.Action[] Actions)? CurrentGestureSnapshot()
    {
        if (SelectedGestureOwner?.Button is not { } owner || GestureClick is null) return null;
        var actions = new List<OpenLogi.Core.Action> { GestureClick.Selected.Action };
        actions.AddRange(GestureDirections.Select(v => v.Selected.Action));
        return (owner, [.. actions]);
    }

    /// <summary>Record the pre-edit state as one undo step (no-op during programmatic fills).</summary>
    private void PushGestureUndo()
    {
        if (_lastGestureState is { } previous)
            _gestureUndo.Push(previous);
        _lastGestureState = CurrentGestureSnapshot();
    }

    /// <summary>
    /// Ctrl+Z in the Gestures panel: restore the five editor actions to the state
    /// before the most recent click / category / swipe edit. History is cleared
    /// whenever a different button is selected.
    /// </summary>
    [RelayCommand]
    private void UndoGestureEdit()
    {
        if (_gestureUndo.Count == 0) return;
        var (owner, actions) = _gestureUndo.Pop();
        if (SelectedGestureOwner?.Button != owner || GestureClick is null)
        {
            _gestureUndo.Clear(); // stale entries for a previously edited button
            return;
        }
        _suppressGesturePanel = true;
        var editors = new List<GestureDirectionBindingViewModel> { GestureClick };
        editors.AddRange(GestureDirections);
        for (var i = 0; i < editors.Count && i < actions.Length; i++)
        {
            var choice = ButtonBindingViewModel.Catalog.FirstOrDefault(c => c.Action.Equals(actions[i]));
            if (choice is not null)
                editors[i].Selected = choice; // persists via the editor's own callback
        }
        SelectedGestureCategory = MatchGestureCategory();
        _suppressGesturePanel = false;
        _lastGestureState = CurrentGestureSnapshot();
    }

    partial void OnSelectedGestureOwnerChanged(GestureOwnerChoice? value)
    {
        RefreshGestureHighlight();
        if (_suppressGestureOwner || SelectedDevice?.ConfigKey is not { } ck) return;
        // Selecting a button only retargets the editor — it must not create or
        // clear any button's gesture map. Global on/off is the checkbox's job.
        if (value?.Button is { } button)
        {
            _config.SetGestureSelection(ck, button);
            try { _config.SaveAtomic(); } catch { /* keep editing fluid */ }
        }
        RebuildGestureDirections(ck);
        RefreshGestureSummaries(ck);
    }

    partial void OnGesturesEnabledChanged(bool value)
    {
        if (_suppressGesturePanel || SelectedDevice?.ConfigKey is not { } ck) return;
        if (value)
        {
            // Globally back on: every configured button's map comes back to life.
            _config.EnableGestures(ck);
        }
        else
        {
            // Globally off: silence every button, keep all maps, and clear the
            // editing selection so the per-button rows collapse.
            _config.DisableGestures(ck);
            _suppressGestureOwner = true;
            SelectedGestureOwner = null;
            _suppressGestureOwner = false;
            RebuildGestureDirections(ck);
            RefreshGestureHighlight();
        }
        try { _config.SaveAtomic(); } catch { /* keep editing fluid */ }
        RefreshGestureSummaries(ck);
        _ = RestartGestureCaptureForSelectedAsync(); // arm or release every gesture divert
    }

    partial void OnSelectedGestureCategoryChanged(GesturePreset? value)
    {
        if (_suppressGesturePanel || value is null || value.IsCustom) return;
        // Applies Disabled (all swipes → Do Nothing) exactly like any other preset.
        // The whole fill is one undo step.
        ApplyGesturePreset(value);
        PushGestureUndo();
    }

    /// <summary>
    /// Fill the four swipe dropdowns from <paramref name="preset"/> (persisting via
    /// each editor's own path) and, unless <paramref name="updateCategory"/> is off,
    /// reflect the preset in the Category dropdown.
    /// </summary>
    private void ApplyGesturePreset(GesturePreset preset, bool updateCategory = true)
    {
        _suppressGesturePanel = true;
        foreach (var vm in GestureDirections)
        {
            if (preset.For(vm.Direction) is not { } action) continue;
            var choice = ButtonBindingViewModel.Catalog.FirstOrDefault(c => c.Action.Equals(action));
            if (choice is not null)
                vm.Selected = choice; // fires the editor's persist callback
        }
        if (updateCategory)
            SelectedGestureCategory = preset;
        _suppressGesturePanel = false;
    }

    /// <summary>The preset the current four swipe selections equal, else Custom.</summary>
    private GesturePreset MatchGestureCategory() =>
        GestureCategories.FirstOrDefault(p =>
            !p.IsCustom && GestureDirections.All(vm => vm.Selected.Action.Equals(p.For(vm.Direction))))
        ?? CustomGesturePreset;

    /// <summary>Accent the gesture owner's label + leader line on the mouse diagram.</summary>
    private void RefreshGestureHighlight()
    {
        var owner = SelectedGestureOwner?.Button;
        foreach (var a in Annotations)
            a.Highlighted = owner is { } o && a.Binding.Button == o;
    }

    /// <summary>
    /// Select <paramref name="button"/> in the Gestures panel (clicking its diagram
    /// label). A no-op for buttons that can't gesture — selection alone never
    /// creates or clears a gesture map, so this is always safe.
    /// </summary>
    public void SelectGestureOwnerFor(ButtonId button)
    {
        var choice = GestureOwnerChoices.FirstOrDefault(c => c.Button == button);
        if (choice is not null)
            SelectedGestureOwner = choice;
    }

    /// <summary>Longest "Gestures: …" detail the diagram label column can carry.</summary>
    private const int GestureSummaryBudget = 20;

    /// <summary>
    /// The "Gestures: …" line for <paramref name="button"/>'s diagram label, or
    /// <c>null</c> when it is not the gesture owner. The detail is the shared
    /// category name when all swipe actions belong to one category, else as many
    /// action names as fit <see cref="GestureSummaryBudget"/> characters.
    /// </summary>
    private string? GestureSummaryText(string configKey, ButtonId button)
    {
        if (!_config.GestureButtons(configKey).Contains(button)) return null;
        var map = BindingMaps.GestureBindingsFor(_config, configKey, button);
        var actions = new[] { GestureDirection.Up, GestureDirection.Down, GestureDirection.Left, GestureDirection.Right }
            .Select(d => map.TryGetValue(d, out var a) ? a : Core.Bindings.DefaultGestureBinding(d))
            .Where(a => a.Kind != ActionKind.None)
            .ToList();
        if (actions.Count == 0) return null; // swipes disabled — no label line

        var categories = actions.Select(a => a.Category()).Distinct().ToList();
        if (categories.Count == 1)
            return $"Gestures: {categories[0].Label()}";

        var detail = "";
        foreach (var label in actions.Select(a => a.Label()).Distinct())
        {
            var candidate = detail.Length == 0 ? label : $"{detail}, {label}";
            if (candidate.Length > GestureSummaryBudget)
            {
                detail += "…";
                break;
            }
            detail = candidate;
        }
        return $"Gestures: {detail}";
    }

    /// <summary>Recompute every button's "Gestures: …" label line (owner or map changed).</summary>
    private void RefreshGestureSummaries(string configKey)
    {
        foreach (var (button, vm) in _bindings)
            vm.GestureSummary = GestureSummaryText(configKey, button);
    }

    private static Config LoadConfig()
    {
        try { return Config.LoadOrDefault(); }
        catch { return new Config(); }
    }

    /// <summary>Input device kinds OpenLogi configures (excludes headsets/mics/webcams/unknown).</summary>
    private static readonly DeviceKind[] InputKinds =
    [
        DeviceKind.Mouse, DeviceKind.Keyboard, DeviceKind.Trackball,
        DeviceKind.Touchpad, DeviceKind.Presenter, DeviceKind.Numpad, DeviceKind.Remote,
    ];

    /// <summary>
    /// Whether a device belongs in the UI: it exposes a configurable capability
    /// (buttons / pointer / lighting) or is a recognised input device kind. A
    /// HID++ microphone (Yeti Nano) has none of these and is hidden.
    /// </summary>
    private static bool IsConfigurable(PairedDevice d)
    {
        if (d.Capabilities is { } c && (c.Buttons || c.Pointer || c.Lighting))
            return true;
        return InputKinds.Contains(d.Kind);
    }
}

/// <summary>
/// The user-facing scroll-wheel mode: the single coherent choice behind the
/// device's <c>wheelMode</c> + <c>autoDisengage</c> pair.
/// </summary>
public enum WheelModeChoice
{
    /// <summary>Smooth, frictionless free-spin (<c>wheelMode = Free</c>).</summary>
    FreeSpin,
    /// <summary>Ratchet that free-spins on a fast flick (<c>Ratchet</c> + finite auto-disengage).</summary>
    SmartShift,
    /// <summary>Always clicks line-by-line (<c>Ratchet</c> + auto-disengage 0xFF).</summary>
    Ratchet,
}

