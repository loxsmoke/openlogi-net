using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenLogi.Agent;
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
    [ObservableProperty] private bool _showDpi;
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
    [ObservableProperty][NotifyPropertyChangedFor(nameof(ShowLightingToggle), nameof(ShowEffect), nameof(ShowColor), nameof(ShowSpeed))] private bool _profileSelected;
    [ObservableProperty] private int _selectedProfileForEdit;
    private byte _profileCount;

    // G-keys (0x8010) — remappable per active onboard profile.
    [ObservableProperty] private int _gKeyProfile;
    private ushort _gkeyProfileSector;

    // Per-key color editor (PerKeyLighting 0x8081) — shown when the device supports it.
    [ObservableProperty] private bool _showPerKeyEditor;

    // Backlight brightness (BrightnessControl 0x8040) — hardware-verified.
    [ObservableProperty] private bool _showBacklight;
    [ObservableProperty] private double _backlightValue;
    [ObservableProperty] private double _backlightMax = 100;

    private DeviceSession? _session;
    private IAsyncDisposable? _dpiCapture;
    private bool _loadingControls;

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
        // Apply the selected device's bindings to the live remap hook.
        _agent.SetSelectedDevice(value?.ConfigKey);

        Buttons.Clear();
        Annotations.Clear();
        DiagramImage = null;
        _bindings = [];
        ShowDpi = false;
        ShowSmartShift = false;
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

    private async Task LoadControlsAsync(DeviceViewModel? device)
    {
        var old = _session;
        _session = null;
        if (_dpiCapture is { } cap) { _dpiCapture = null; await cap.DisposeAsync(); }
        if (old is not null) await old.DisposeAsync();

        if (device?.Route is not { } route) return;

        DeviceSession? session;
        try { session = await DeviceSession.OpenAsync(route); }
        catch { return; }
        if (session is null) return;
        if (!ReferenceEquals(SelectedDevice, device)) { await session.DisposeAsync(); return; }
        _session = session;
        ShowPerKeyEditor = session.SupportsPerKey;

        // Divert the DPI/ModeShift button over HID++ so it can be remapped — the
        // OS mouse hook never sees it. Dispatched through the same binding map.
        try { _dpiCapture = await session.StartDpiButtonCaptureAsync(() => _agent.DispatchDivertedButton(ButtonId.DpiToggle)); }
        catch { /* device without a divertable DPI button */ }

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
            if (await session.ReadHostsAsync() is { } hosts && ReferenceEquals(SelectedDevice, device))
                RebuildHosts(hosts);
            if (await session.ReadBrightnessAsync() is { } bright && ReferenceEquals(SelectedDevice, device))
            {
                BacklightMax = bright.Info.MaxBrightness == 0 ? 100 : bright.Info.MaxBrightness;
                BacklightValue = bright.Current;
                ShowBacklight = true;
            }
            if (await session.ReadProfilesAsync() is { } prof && ReferenceEquals(SelectedDevice, device))
            {
                _profileCount = prof.Info.ProfileCount;
                RebuildProfiles(prof.Current);
                ShowProfiles = prof.Info.ProfileCount >= 1;

                if (session.SupportsGKeys)
                {
                    _gkeyProfileSector = Math.Max(prof.Current, (byte)1);
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
        }
        finally { _loadingControls = false; }
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
            RebuildProfiles(0);          // "No profile" selected
            await ApplySolidAsync();     // custom colour via host mode
            return;
        }

        await _session.SwitchProfileAsync((byte)slot.Number); // ensures onboard mode internally
        if (await _session.ReadProfilesAsync() is { } prof)
            RebuildProfiles(prof.Current);
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
        _config.SetLighting(ck, new Lighting
        {
            Enabled = LightingEnabled,
            Color = $"{c.R:x2}{c.G:x2}{c.B:x2}",
            Brightness = (byte)LightingBrightness,
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
        _ = _dpiCapture?.DisposeAsync();
        _ = _session?.DisposeAsync();
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

