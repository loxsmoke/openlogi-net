using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenLogi.Agent;
using OpenLogi.Assets;
using OpenLogi.Core;
using OpenLogi.Core.Config;
using OpenLogi.Core.DeviceInfo;
using OpenLogi.Hid;

namespace OpenLogi.App.ViewModels;

/// <summary>
/// Root view-model: live device inventory + the interactive mouse diagram
/// (device render with clickable button hotspots) and the per-button picker.
///
/// GUI builds with compiled bindings; behaviour confirmed by running the app.
///
/// This file holds the state (fields/observables), constructor, gallery
/// scan/selection and lifecycle; the behaviour groups live in the partials:
/// wake/topology (<c>MainWindowViewModel.Wake.cs</c>), update + Logitech-software
/// banners (<c>.Updates.cs</c>), diagram + button bindings (<c>.Diagram.cs</c>),
/// per-mouse captures (<c>.Captures.cs</c>), device-page loading
/// (<c>.DeviceLoad.cs</c>), pointer/wheel/hosts controls (<c>.Controls.cs</c>),
/// lighting + profiles (<c>.Lighting.cs</c>) and gestures (<c>.Gestures.cs</c>).
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
    private readonly Stack<(ButtonId Owner, Core.Actions.MouseAction[] Actions)> _gestureUndo = new();
    private (ButtonId Owner, Core.Actions.MouseAction[] Actions)? _lastGestureState;

    /// <summary>The Custom sentinel: selected when the swipes match no preset; applies nothing.</summary>
    private static readonly GesturePreset CustomGesturePreset = new("Custom", null, null, null, null);

    /// <summary>
    /// "Disabled": all four swipes Do Nothing — the default for an unconfigured
    /// button, and the way to turn one button's swipes off (its Click keeps working).
    /// </summary>
    private static readonly GesturePreset DisabledGesturePreset = new("Disabled",
        Core.Actions.MouseAction.None, Core.Actions.MouseAction.None,
        Core.Actions.MouseAction.None, Core.Actions.MouseAction.None);

    /// <summary>Category presets for the four swipes (mirrors the Options+ gesture sets).</summary>
    public IReadOnlyList<GesturePreset> GestureCategories { get; } =
    [
        DisabledGesturePreset,
        new("Windows & Desktops", Core.Actions.MouseAction.TaskView, Core.Actions.MouseAction.ShowDesktop,
            Core.Actions.MouseAction.PreviousDesktop, Core.Actions.MouseAction.NextDesktop),
        new("Media & Volume", Core.Actions.MouseAction.VolumeUp, Core.Actions.MouseAction.VolumeDown,
            Core.Actions.MouseAction.PrevTrack, Core.Actions.MouseAction.NextTrack),
        new("Arrange Windows", Core.Actions.MouseAction.MaximizeWindow, Core.Actions.MouseAction.MinimizeWindow,
            Core.Actions.MouseAction.SnapWindowLeft, Core.Actions.MouseAction.SnapWindowRight),
        new("Browser Tabs", Core.Actions.MouseAction.NewTab, Core.Actions.MouseAction.CloseTab,
            Core.Actions.MouseAction.PrevTab, Core.Actions.MouseAction.NextTab),
        new("Scrolling", Core.Actions.MouseAction.ScrollUp, Core.Actions.MouseAction.ScrollDown,
            Core.Actions.MouseAction.HorizontalScrollLeft, Core.Actions.MouseAction.HorizontalScrollRight),
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

    // Receiver-contention banner: Logitech software running alongside us can take
    // over the receiver (same warning the About window shows). Re-evaluated on each
    // scan; the signature of the running set drives dismissal, mirroring updates.
    [ObservableProperty] private bool _logiWarningVisible;
    [ObservableProperty] private string _logiWarningText = "";
    private string _logiWarningSignature = "";
    private string? _dismissedLogiWarning;

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

    // Watches for Logitech HID nodes arriving/leaving (e.g. a Bluetooth mouse
    // waking) and auto-rescans, so devices appear without a manual Refresh.
    private HidDeviceWatcher? _deviceWatcher;
    // Watches receivers for a paired wireless device (re)connecting — a keyboard/mouse
    // waking on a receiver doesn't change the HID node set, so _deviceWatcher can't see
    // it; this hears the receiver's 0x41 connection notification and triggers a rescan.
    private ReceiverConnectionWatcher? _receiverWatcher;
    // Set when a device-set change lands while the user is mid-configuration on a
    // device page (or a scan is already running); the rescan then runs when they
    // return to the gallery, so the open page is never disturbed.
    private bool _rescanPending;
    // Guards ReconnectAfterResumeAsync so the resume event and the watcher's node-change
    // event (both fire on wake) don't run overlapping reconnects; a request landing mid-run
    // is coalesced into one trailing re-run.
    private bool _reconnecting;
    private bool _reconnectQueued;

    // Last-known identity (marketing name + model + capabilities) per wireless product
    // id, remembered from a sweep where the device was awake and persisted to config
    // (DeviceIdentity), so it survives app restarts. A wireless keyboard that has gone
    // to sleep is reported offline by its receiver with no readable name (a
    // LIGHTSPEED-paired G915's codename register even ResourceErrors), and its
    // wake-time HID++ probes fail more often than not — without the durable backfill
    // such a device has no config key, so none of its saved settings (per-key
    // lighting!) can ever be applied. Model-level: identical twins share an identity,
    // harmlessly.
    private readonly Dictionary<ushort, DeviceIdentity> _identityCache = [];

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

            // A failed watcher (no HID stack) just means no auto-rescan; Refresh still works.
            try
            {
                _deviceWatcher = new HidDeviceWatcher();
                _deviceWatcher.Changed += OnHidDeviceSetChanged;
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"device watcher unavailable: {ex.Message}"); }

            // A wireless device waking on its receiver leaves the HID node set unchanged,
            // so the node watcher above stays silent. Listen to the receiver's connection
            // notifications instead and rescan when a device comes online (e.g. a keyboard
            // woken by a keypress, so its gallery tile flips from "Asleep" to "Online").
            try
            {
                _receiverWatcher = new ReceiverConnectionWatcher();
                _receiverWatcher.DeviceWoke += OnReceiverDeviceWoke;
                _ = _receiverWatcher.RefreshAsync();
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"receiver watcher unavailable: {ex.Message}"); }

            // Wake from sleep resets each mouse's volatile hardware state (sensor DPI,
            // scroll-invert) to firmware defaults. A mouse that re-enumerates on wake is
            // caught by the device watcher above; one that stays enumerated across sleep
            // needs this resume signal to get its persisted preferences re-pushed.
            try { Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged; }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"power events unavailable: {ex.Message}"); }
        }
    }

    partial void OnShowingDeviceChanged(bool value)
    {
        if (!value && _rescanPending && !IsScanning)
            _ = LoadAsync();
        // Entering/leaving a keyboard's page switches which session the keepalive uses.
        PokeLightingKeepalive();
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
    /// Cache a device's identity (name + model + capabilities) when it's seen with one
    /// — persisting it to config keyed by the model's config key — or backfill it when
    /// a sweep finds the same wireless unit with a partial/failed probe. Keyed by wpid
    /// (present in both the online and offline receiver arrival notifications), matched
    /// against the persisted identities' model ids on a cold start. Fields present on
    /// the fresh reading always win; a full probe's capabilities replace remembered
    /// ones, a partial probe's kind-presumed guess never does.
    /// </summary>
    private PairedDevice RememberIdentity(PairedDevice d)
    {
        if (d.Wpid is not { } wpid) return d;
        var prev = _identityCache.TryGetValue(wpid, out var c) ? c : FindPersistedIdentity(wpid);

        if (d.Codename is not null || d.ModelInfo is not null)
        {
            var identity = new DeviceIdentity
            {
                DisplayName = d.Codename ?? prev?.DisplayName ?? string.Empty,
                Codename = d.Codename ?? prev?.Codename,
                Kind = d.Kind != DeviceKind.Unknown ? d.Kind : prev?.Kind ?? DeviceKind.Unknown,
                Capabilities = d.ModelInfo is not null
                    ? d.Capabilities ?? new Capabilities()
                    : prev?.Capabilities ?? d.Capabilities ?? new Capabilities(),
                ModelInfo = d.ModelInfo ?? prev?.ModelInfo,
            };
            _identityCache[wpid] = identity;
            if (identity.ModelInfo is { } mi && !identity.Equals(_config.DeviceIdentity(mi.ConfigKey())))
            {
                _config.SetDeviceIdentity(mi.ConfigKey(), identity);
                SaveConfig();
            }
            // A codename-only reading (receiver register worked, HID++ probe failed)
            // still gets the remembered model + capabilities, so the config key works.
            return d.ModelInfo is null && identity.ModelInfo is not null
                ? d with { ModelInfo = identity.ModelInfo, Capabilities = identity.Capabilities }
                : d;
        }

        if (prev is null) return d;
        _identityCache[wpid] = prev;
        return d with
        {
            Codename = prev.Codename ?? (prev.DisplayName.Length > 0 ? prev.DisplayName : null),
            ModelInfo = prev.ModelInfo,
            Capabilities = prev.ModelInfo is not null ? prev.Capabilities : d.Capabilities,
        };
    }

    /// <summary>
    /// The persisted identity whose model ids contain <paramref name="wpid"/> — the
    /// same wpid↔model-id match the offline dedup uses. Lets a fresh app session
    /// identify a keyboard whose wake-time probe failed, so its saved settings
    /// (per-key lighting, DPI…) still apply.
    /// </summary>
    private DeviceIdentity? FindPersistedIdentity(ushort wpid)
    {
        foreach (var (_, identity) in _config.KnownIdentities())
            if (identity.ModelInfo is { } mi && mi.ModelIds.Contains(wpid))
                return identity;
        return null;
    }

    /// <param name="reactivateMice">
    /// False for a keyboard-only receiver wake: the gallery is refreshed but the
    /// existing mouse captures are left untouched — rebuilding them while a mouse
    /// naps kills its diverts (the un-divert and re-arm writes both time out), and a
    /// keyboard waking can't have invalidated them in the first place.
    /// </param>
    private async Task LoadAsync(bool reactivateMice = true)
    {
        _rescanPending = false; // this scan reflects the current device set
        StatusText = "Scanning for Logitech devices…";
        IsScanning = true;
        NoDevices = false;
        Devices.Clear();
        // Re-evaluate receiver contention alongside the scan (independent of its result).
        _ = RefreshLogiWarningAsync();
        try
        {
            var inventories = await HidInventory.EnumerateAsync(postProbe: OnReceiverDeviceProbedAsync);
            foreach (var inv in inventories)
                foreach (var paired in inv.Paired)
                    if (IsConfigurable(paired))
                        Devices.Add(new DeviceViewModel(inv.Receiver.Name, RememberIdentity(paired), DeviceRoute.DeviceRouteFor(inv, paired.Slot)));

            // Stay on the home gallery; opening a card navigates to its detail.
            SelectedDevice = null;
            ShowingDevice = false;
            StatusText = Devices.Count == 0 ? "No Logitech HID++ devices found." : $"{Devices.Count} device(s).";

            // Activate every connected mouse's overrides as soon as the app is running,
            // without opening any page: the OS hook for Middle/Back/Forward (global) and
            // a per-mouse session that diverts each mouse's DPI button.
            if (reactivateMice)
            {
                _ = ActivateAgentMiceAsync([.. Devices.Where(d => d.HasButtons)]);
            }
            else
            {
                // Captures stay as they are, but their DeviceViewModels were just
                // replaced by this rescan — re-point each capture at the fresh VM so
                // LoadControlsAsync's session reuse (ReferenceEquals on Device) still
                // matches when that mouse's page is opened.
                for (var i = 0; i < _mouseCaptures.Count; i++)
                    if (Devices.FirstOrDefault(d =>
                            d.ConfigKey is not null && d.ConfigKey == _mouseCaptures[i].Device.ConfigKey) is { } fresh)
                        _mouseCaptures[i] = _mouseCaptures[i] with { Device = fresh };
                PokeLightingKeepalive(); // Activate normally does this at its end
            }

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

        ResetDeviceControls();
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

        SeedLightingFromConfig(configKey);
        RebuildButtonBindings(configKey);

        // Only surface the button pickers / diagram for devices that actually have
        // remappable buttons (a mouse), not e.g. a HID++ microphone or a keyboard.
        // The button list is derived from the device's real buttons in BuildDiagramAsync.
        if (value.HasButtons)
            _ = BuildDiagramAsync(value, configKey);
    }

    /// <summary>Clear every per-device control back to its blank state before a new device loads.</summary>
    private void ResetDeviceControls()
    {
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
        try { Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged; } catch { /* never subscribed */ }
        _agent.Dispose();
        if (_deviceWatcher is not null)
        {
            _deviceWatcher.Changed -= OnHidDeviceSetChanged;
            _deviceWatcher.Dispose();
        }
        if (_receiverWatcher is not null)
        {
            _receiverWatcher.DeviceWoke -= OnReceiverDeviceWoke;
            _ = _receiverWatcher.DisposeAsync();
        }
        _lightingKeepaliveTimer?.Dispose();
        _ = _keepaliveSession?.DisposeAsync();
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
