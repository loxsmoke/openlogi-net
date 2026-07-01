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
            var action = current.TryGetValue(button, out var a) ? a : Core.Bindings.DefaultBinding(button);
            _bindings[button] = new ButtonBindingViewModel(button, action, ButtonBindingViewModel.Catalog,
                (b, act) => Persist(configKey, b, act));
        }

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
    /// DPI/ModeShift button, so each mouse's HID++ overrides work whenever the app
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
                var capture = await session.StartDpiButtonCaptureAsync(() => _agent.DispatchDivertedButton(ck, ButtonId.DpiToggle));
                if (capture is null) { await session.DisposeAsync(); continue; } // no divertable DPI button
                _mouseCaptures.Add(new MouseCapture(mouse, session, capture));
            }
            catch { /* mouse unreachable */ }
        }
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
            }
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

