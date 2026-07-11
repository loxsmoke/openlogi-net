using OpenLogi.Core.Actions;
using OpenLogi.Core.DeviceInfo;
using OpenLogi.Core.Gestures;

namespace OpenLogi.Core.Config;

/// <summary>
/// Top-level config document, persisted as TOML. Ported from Rust
/// <c>config::Config</c>. Per-device state is keyed by a stable physical-device
/// config key (see <see cref="DeviceModelInfo.ConfigKey"/>).
/// </summary>
public sealed class Config
{
    /// <summary>
    /// The schema version the current build produces. The Rust crate's v1→v2→v3
    /// legacy migration is intentionally NOT ported: this greenfield app has no
    /// pre-existing OpenLogi.net config files to migrate. Loading still rejects a
    /// file from a newer build.
    /// </summary>
    public const uint SchemaVersion = 3;

    public uint Version { get; set; } = SchemaVersion;
    public AppSettings AppSettings { get; set; } = new();
    public string? SelectedDevice { get; set; }
    public SortedDictionary<string, DeviceConfig> Devices { get; set; } = new(StringComparer.Ordinal);

    // ── Persistence ────────────────────────────────────────────────────────────

    /// <summary>Load from the default user path, or <see cref="Config"/> defaults if absent.</summary>
    public static Config LoadOrDefault() => LoadFromPath(Paths.ConfigPath());

    /// <summary>Load from <paramref name="path"/>, or defaults if the file does not exist.</summary>
    public static Config LoadFromPath(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (FileNotFoundException)
        {
            return new Config();
        }
        catch (DirectoryNotFoundException)
        {
            return new Config();
        }
        catch (IOException e)
        {
            throw new ConfigException($"could not read config at {path}", e);
        }

        var config = ConfigCodec.Parse(text, path);
        // Accept any version up to the current one; reject only a newer file,
        // loudly, so a downgraded build refuses to load (and silently wipe) it.
        if (config.Version > SchemaVersion)
            throw new ConfigException($"config at {path} has unsupported schema_version {config.Version}");
        config.Version = SchemaVersion;
        return config;
    }

    /// <summary>Write atomically to the default user path.</summary>
    public void SaveAtomic() => SaveToPath(Paths.ConfigPath());

    /// <summary>Write atomically to <paramref name="path"/> (temp file + rename).</summary>
    public void SaveToPath(string path)
    {
        try
        {
            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);
            var body = ConfigCodec.Serialize(this);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, body);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e) when (e is not ConfigException)
        {
            throw new ConfigException($"could not write config at {path}", e);
        }
    }

    // ── Bindings ─────────────────────────────────────────────────────────────

    /// <summary>The bindings stored for <paramref name="deviceKey"/>, or an empty map.</summary>
    public SortedDictionary<ButtonId, Binding> BindingsFor(string deviceKey) =>
        Devices.TryGetValue(deviceKey, out var d)
            ? new SortedDictionary<ButtonId, Binding>(d.Bindings)
            : new SortedDictionary<ButtonId, Binding>();

    /// <summary>Record <paramref name="binding"/> for <paramref name="button"/>, replacing the whole binding.</summary>
    public void SetBinding(string deviceKey, ButtonId button, Binding binding) =>
        Device(deviceKey).Bindings[button] = binding;

    /// <summary>
    /// The gesture sub-bindings stored under <paramref name="button"/>, or an empty
    /// map when that button has no gesture binding. Purely the stored map — global
    /// on/off and defaults are the caller's concern.
    /// </summary>
    public SortedDictionary<GestureDirection, Actions.MouseAction> GestureBindingsFor(string deviceKey, ButtonId button) =>
        Devices.TryGetValue(deviceKey, out var d)
            && d.Bindings.TryGetValue(button, out var b)
            && b is Binding.Gesture g
                ? new SortedDictionary<GestureDirection, Actions.MouseAction>(g.Map)
                : new SortedDictionary<GestureDirection, Actions.MouseAction>();

    /// <summary>
    /// Every button with gestures configured (its stored binding is a gesture map).
    /// The dedicated gesture button counts even when unconfigured — its default
    /// binding is a full gesture map. Empty when gestures are globally off
    /// (<see cref="DisableGestures"/>); several buttons may gesture at once.
    /// </summary>
    public IReadOnlyList<ButtonId> GestureButtons(string deviceKey)
    {
        if (Devices.TryGetValue(deviceKey, out var device)
            && (((device.GestureOwner is GestureOwner.Off))))
            return [];
        var result = new List<ButtonId>();
        var bindings = Devices.TryGetValue(deviceKey, out var d) ? d.Bindings : null;
        if (bindings is not null)
            foreach (var (id, b) in bindings)
                if (b.IsGesture())
                    result.Add(id);
        if (bindings is null || !bindings.ContainsKey(ButtonId.GestureButton))
            result.Add(ButtonId.GestureButton);
        return result;
    }

    /// <summary>
    /// Record <paramref name="button"/> as the gesture-editing selection and turn
    /// gestures globally back on, without touching any button's bindings — unlike
    /// <see cref="SetGestureOwner"/>, merely selecting a button must not create a
    /// gesture map on it.
    /// </summary>
    public void SetGestureSelection(string deviceKey, ButtonId button) =>
        Device(deviceKey).GestureOwner = new GestureOwner.Button(button);

    /// <summary>Whether gestures are globally enabled (i.e. not explicitly Off).</summary>
    public bool GesturesEnabled(string deviceKey) =>
        !(Devices.TryGetValue(deviceKey, out var d) && (((d.GestureOwner is GestureOwner.Off))));

    /// <summary>
    /// Turn gestures globally back on by clearing an explicit Off (leaving any
    /// recorded button selection or inference untouched otherwise).
    /// </summary>
    public void EnableGestures(string deviceKey)
    {
        if (Devices.TryGetValue(deviceKey, out var d) && (((d.GestureOwner is GestureOwner.Off))))
            d.GestureOwner = null;
    }

    /// <summary>Record <paramref name="action"/> for one <paramref name="direction"/> of a button's gesture binding.</summary>
    public void SetGestureDirection(string deviceKey, ButtonId button, GestureDirection direction, Actions.MouseAction action)
    {
        var binding = EnsureGestureBinding(deviceKey, button);
        if (binding is Binding.Gesture g)
            g.Map[direction] = action;
    }

    private Binding EnsureGestureBinding(string deviceKey, ButtonId button)
    {
        var device = Device(deviceKey);
        if (!device.Bindings.TryGetValue(button, out var entry))
            entry = Bindings.DefaultBindingFor(button);
        entry = entry.UpgradeToGesture();
        device.Bindings[button] = entry;
        return entry;
    }

    /// <summary>The button that owns the device's single gesture role, or <c>null</c> when off.</summary>
    public ButtonId? GestureOwner(string deviceKey)
    {
        if (!Devices.TryGetValue(deviceKey, out var device))
            return ButtonId.GestureButton;
        return device.GestureOwner switch
        {
            Gestures.GestureOwner.Off => null,
            GestureOwner.Button b => b.Id,
            _ => InferGestureOwner(device.Bindings),
        };
    }

    private static ButtonId? InferGestureOwner(SortedDictionary<ButtonId, Binding> bindings)
    {
        foreach (var (id, b) in bindings)
            if (id != ButtonId.GestureButton && b.IsGesture())
                return id;
        if (bindings.TryGetValue(ButtonId.GestureButton, out var gb) && gb is Binding.Single)
            return null;
        return ButtonId.GestureButton;
    }

    /// <summary>Make <paramref name="button"/> the device's sole gesture button.</summary>
    public void SetGestureOwner(string deviceKey, ButtonId button)
    {
        Device(deviceKey).GestureOwner = new GestureOwner.Button(button);
        var binding = EnsureGestureBinding(deviceKey, button).FillGestureDefaults();
        Device(deviceKey).Bindings[button] = binding;
    }

    /// <summary>Turn gestures off for <paramref name="deviceKey"/> (keeping every gesture map intact).</summary>
    public void DisableGestures(string deviceKey) =>
        Device(deviceKey).GestureOwner = Gestures.GestureOwner.OffValue;

    /// <summary>
    /// The effective binding map, overlaying the per-app entry for
    /// <paramref name="bundleId"/> (if any) on top of the global bindings.
    /// </summary>
    public SortedDictionary<ButtonId, Binding> EffectiveBindings(string deviceKey, string? bundleId)
    {
        if (!Devices.TryGetValue(deviceKey, out var device))
            return new SortedDictionary<ButtonId, Binding>();
        var outMap = new SortedDictionary<ButtonId, Binding>(device.Bindings);
        if (bundleId is not null && device.PerAppBindings.TryGetValue(bundleId, out var overlay))
            foreach (var (k, v) in overlay)
                outMap[k] = new Binding.Single(v);
        return outMap;
    }

    /// <summary>Record a per-app override. Passing <c>null</c> removes it and prunes the empty app map.</summary>
    public void SetPerAppBinding(string deviceKey, string bundleId, ButtonId button, Actions.MouseAction? action)
    {
        var device = Device(deviceKey);
        if (!device.PerAppBindings.TryGetValue(bundleId, out var entry))
        {
            entry = new SortedDictionary<ButtonId, Actions.MouseAction>();
            device.PerAppBindings[bundleId] = entry;
        }
        if (action is not null)
            entry[button] = action;
        else
            entry.Remove(button);
        foreach (var key in device.PerAppBindings.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToList())
            device.PerAppBindings.Remove(key);
    }

    // ── Per-device scalars ───────────────────────────────────────────────────

    public string? SelectedDeviceKey() => SelectedDevice;
    public void SetSelectedDevice(string? key) => SelectedDevice = key;

    public List<uint> DpiPresets(string deviceKey) =>
        Devices.TryGetValue(deviceKey, out var d) ? [.. d.DpiPresets] : [];

    public void SetDpiPresets(string deviceKey, List<uint> presets) =>
        Device(deviceKey).DpiPresets = presets;

    public DeviceIdentity? DeviceIdentity(string deviceKey) =>
        Devices.TryGetValue(deviceKey, out var d) ? d.Identity : null;

    public void SetDeviceIdentity(string deviceKey, DeviceIdentity identity) =>
        Device(deviceKey).Identity = identity;

    public IEnumerable<(string Key, DeviceIdentity Identity)> KnownIdentities() =>
        Devices.Where(kv => kv.Value.Identity is not null).Select(kv => (kv.Key, kv.Value.Identity!));

    public Lighting? Lighting(string deviceKey) =>
        Devices.TryGetValue(deviceKey, out var d) ? d.Lighting : null;

    public void SetLighting(string deviceKey, Lighting lighting) =>
        Device(deviceKey).Lighting = lighting;

    /// <summary>
    /// Record which lighting source is active (0 = "No profile" custom/host, N ≥ 1 =
    /// onboard profile N) without disturbing the stored colours, so the custom colours
    /// can be re-applied after the keyboard sleeps. Creates a default Lighting if none.
    /// </summary>
    public void SetLightingProfile(string deviceKey, int profile)
    {
        var device = Device(deviceKey);
        device.Lighting = (device.Lighting ?? new Lighting()) with { Profile = profile };
    }

    public uint? Dpi(string deviceKey) =>
        Devices.TryGetValue(deviceKey, out var d) ? d.Dpi : null;

    public void SetDpi(string deviceKey, uint dpi) => Device(deviceKey).Dpi = dpi;

    public SmartShift? SmartShift(string deviceKey) =>
        Devices.TryGetValue(deviceKey, out var d) ? d.SmartShift : null;

    public void SetSmartShift(string deviceKey, SmartShift smartshift) =>
        Device(deviceKey).SmartShift = smartshift;

    public bool InvertScroll(string deviceKey) =>
        Devices.TryGetValue(deviceKey, out var d) && d.InvertScroll;

    public void SetInvertScroll(string deviceKey, bool invert) =>
        Device(deviceKey).InvertScroll = invert;

    public bool SmoothScroll(string deviceKey) =>
        Devices.TryGetValue(deviceKey, out var d) && d.SmoothScroll;

    public void SetSmoothScroll(string deviceKey, bool enabled) =>
        Device(deviceKey).SmoothScroll = enabled;

    private DeviceConfig Device(string deviceKey)
    {
        if (!Devices.TryGetValue(deviceKey, out var d))
        {
            d = new DeviceConfig();
            Devices[deviceKey] = d;
        }
        return d;
    }
}
