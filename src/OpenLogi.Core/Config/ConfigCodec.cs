using OpenLogi.Core.Actions;
using OpenLogi.Core.DeviceInfo;
using OpenLogi.Core.Gestures;
using Tomlyn;
using Tomlyn.Model;

namespace OpenLogi.Core.Config;

/// <summary>
/// Reads and writes <see cref="Config"/> as TOML, reproducing the on-disk format
/// the Rust <c>openlogi-core</c> produced: enum variant names as keys, lowercase
/// <see cref="DeviceKind"/> / snake_case <see cref="WheelMode"/> scalars, tagged-union
/// <see cref="MouseAction"/> values (bare string for unit variants, single-key table for
/// payload variants), and untagged <see cref="Binding"/> (a string/payload-table for
/// <see cref="Binding.Single"/>, a direction-keyed table for <see cref="Binding.Gesture"/>).
/// Built on Tomlyn's DOM for exact control over the output.
/// </summary>
public static class ConfigCodec
{
    // ── Serialize ────────────────────────────────────────────────────────────

    public static string Serialize(Config config)
    {
        var root = new TomlTable
        {
            ["schema_version"] = (long)config.Version,
        };
        if (config.SelectedDevice is not null)
            root["selected_device"] = config.SelectedDevice;
        if (!config.AppSettings.IsDefault())
            root["app_settings"] = SerializeAppSettings(config.AppSettings);
        if (config.Devices.Count > 0)
        {
            var devices = new TomlTable();
            foreach (var (key, device) in config.Devices)
                devices[key] = SerializeDevice(device);
            root["devices"] = devices;
        }
        return Toml.FromModel(root);
    }

    private static TomlTable SerializeAppSettings(AppSettings s)
    {
        var t = new TomlTable
        {
            ["launch_at_login"] = s.LaunchAtLogin,
            ["check_for_updates"] = s.CheckForUpdates,
            ["update_prompt_seen"] = s.UpdatePromptSeen,
            ["show_in_menu_bar"] = s.ShowInMenuBar,
            ["auto_download_assets"] = s.AutoDownloadAssets,
            ["minimize_to_tray"] = s.MinimizeToTray,
            ["suppress_logging"] = s.SuppressLogging,
        };
        if (s.DismissedUpdate is not null)
            t["dismissed_update"] = s.DismissedUpdate;
        if (s.Language is not null)
            t["language"] = s.Language;
        t["thumbwheel_sensitivity"] = (long)s.ThumbwheelSensitivity;
        return t;
    }

    private static TomlTable SerializeDevice(DeviceConfig d)
    {
        var t = new TomlTable();
        // Scalars / arrays first (TOML requires them before sub-tables).
        if (d.GestureOwner is { } owner)
            t["gesture_owner"] = owner switch
            {
                GestureOwner.Off => "Off",
                GestureOwner.Button b => b.Id.ToString(),
                _ => "Off",
            };
        if (d.DpiPresets.Count > 0)
        {
            var arr = new TomlArray();
            foreach (var p in d.DpiPresets) arr.Add((long)p);
            t["dpi_presets"] = arr;
        }
        if (d.Dpi is { } dpi)
            t["dpi"] = (long)dpi;
        if (d.InvertScroll)
            t["invert_scroll"] = true;
        if (d.SmoothScroll)
            t["smooth_scroll"] = true;

        // Sub-tables.
        if (d.Identity is { } identity)
            t["identity"] = SerializeIdentity(identity);
        if (d.Lighting is { } lighting)
            t["lighting"] = SerializeLighting(lighting);
        if (d.SmartShift is { } ss)
            t["smartshift"] = new TomlTable
            {
                ["mode"] = ss.Mode.ToString().ToLowerInvariant(),
                ["auto_disengage"] = (long)ss.AutoDisengage,
                ["tunable_torque"] = (long)ss.TunableTorque,
            };
        if (d.Bindings.Count > 0)
        {
            var bindings = new TomlTable();
            foreach (var (button, binding) in d.Bindings)
                bindings[button.ToString()] = SerializeBinding(binding);
            t["bindings"] = bindings;
        }
        if (d.PerAppBindings.Count > 0)
            t["per_app_bindings"] = SerializePerAppBindings(d.PerAppBindings);
        return t;
    }

    private static TomlTable SerializeLighting(Lighting lighting)
    {
        var t = new TomlTable
        {
            ["enabled"] = lighting.Enabled,
            ["color"] = lighting.Color,
            ["brightness"] = (long)lighting.Brightness,
        };
        if (lighting.PaintColor is { } paint)
            t["paint_color"] = paint;
        if (lighting.Profile != 0)
            t["profile"] = (long)lighting.Profile;
        // Per-key overrides (zone → "RRGGBB") nest after the scalars; omitted when empty.
        if (lighting.PerKey.Count > 0)
        {
            var perKey = new TomlTable();
            foreach (var (zone, hex) in lighting.PerKey.OrderBy(p => p.Key))
                perKey[zone.ToString()] = hex;
            t["per_key"] = perKey;
        }
        return t;
    }

    private static TomlTable SerializePerAppBindings(
        SortedDictionary<string, SortedDictionary<ButtonId, Actions.MouseAction>> perAppBindings)
    {
        var perApp = new TomlTable();
        foreach (var (bundle, map) in perAppBindings)
        {
            var inner = new TomlTable();
            foreach (var (button, action) in map)
                inner[button.ToString()] = SerializeAction(action);
            perApp[bundle] = inner;
        }
        return perApp;
    }

    private static TomlTable SerializeIdentity(DeviceIdentity i)
    {
        var t = new TomlTable
        {
            ["display_name"] = i.DisplayName,
        };
        if (i.Codename is not null)
            t["codename"] = i.Codename;
        t["kind"] = i.Kind.ToString().ToLowerInvariant();
        t["capabilities"] = new TomlTable
        {
            ["buttons"] = i.Capabilities.Buttons,
            ["pointer"] = i.Capabilities.Pointer,
            ["lighting"] = i.Capabilities.Lighting,
            ["scroll_inversion"] = i.Capabilities.ScrollInversion,
        };
        if (i.ModelInfo is { } m)
            t["model_info"] = SerializeModelInfo(m);
        return t;
    }

    private static TomlTable SerializeModelInfo(DeviceModelInfo m)
    {
        var t = new TomlTable
        {
            ["entity_count"] = (long)m.EntityCount,
        };
        if (m.SerialNumber is not null)
            t["serial_number"] = m.SerialNumber;
        t["unit_id"] = ToLongArray(m.UnitId.Select(b => (long)b));
        t["transports"] = new TomlTable
        {
            ["usb"] = m.Transports.Usb,
            ["equad"] = m.Transports.Equad,
            ["btle"] = m.Transports.Btle,
            ["bluetooth"] = m.Transports.Bluetooth,
        };
        t["model_ids"] = ToLongArray(m.ModelIds.Select(x => (long)x));
        t["extended_model_id"] = (long)m.ExtendedModelId;
        return t;
    }

    /// <summary>A <see cref="Binding"/>: a scalar/payload-table for Single, a direction-keyed table for Gesture.</summary>
    private static object SerializeBinding(Binding binding) => binding switch
    {
        Binding.Single s => SerializeAction(s.Action),
        Binding.Gesture g => SerializeGestureMap(g.Map),
        _ => throw new ConfigException("unknown binding variant"),
    };

    private static TomlTable SerializeGestureMap(SortedDictionary<GestureDirection, Actions.MouseAction> map)
    {
        var t = new TomlTable();
        foreach (var (dir, action) in map)
            t[dir.ToString()] = SerializeAction(action);
        return t;
    }

    /// <summary>An <see cref="Actions.MouseAction"/>: a bare string for unit variants, a single-key table for payloads.</summary>
    private static object SerializeAction(Actions.MouseAction action) => action.Kind switch
    {
        ActionKind.SetDpiPreset => new TomlTable { ["SetDpiPreset"] = (long)action.DpiPreset },
        ActionKind.CustomShortcut => new TomlTable
        {
            ["CustomShortcut"] = new TomlTable
            {
                ["modifiers"] = (long)action.Combo!.Modifiers,
                ["key_code"] = (long)action.Combo!.KeyCode,
                ["display"] = action.Combo!.Display,
            },
        },
        _ => action.Kind.ToString(),
    };

    private static TomlArray ToLongArray(IEnumerable<long> values)
    {
        var arr = new TomlArray();
        foreach (var v in values) arr.Add(v);
        return arr;
    }

    // ── Parse ────────────────────────────────────────────────────────────────

    public static Config Parse(string text, string path)
    {
        TomlTable root;
        try
        {
            root = Toml.ToModel(text);
        }
        catch (Exception e)
        {
            throw new ConfigException($"could not parse config at {path}", e);
        }

        var config = new Config
        {
            Version = (uint)GetLong(root, "schema_version", Config.SchemaVersion),
            SelectedDevice = GetString(root, "selected_device"),
        };
        if (root.TryGetValue("app_settings", out var appObj) && appObj is TomlTable app)
            config.AppSettings = ParseAppSettings(app);
        if (root.TryGetValue("devices", out var devObj) && devObj is TomlTable devices)
            foreach (var (key, value) in devices)
                if (value is TomlTable deviceTable)
                    config.Devices[key] = ParseDevice(deviceTable);
        return config;
    }

    private static AppSettings ParseAppSettings(TomlTable t) => new()
    {
        LaunchAtLogin = GetBool(t, "launch_at_login", false),
        CheckForUpdates = GetBool(t, "check_for_updates", false),
        UpdatePromptSeen = GetBool(t, "update_prompt_seen", false),
        DismissedUpdate = GetString(t, "dismissed_update"),
        ShowInMenuBar = GetBool(t, "show_in_menu_bar", true),
        AutoDownloadAssets = GetBool(t, "auto_download_assets", true),
        MinimizeToTray = GetBool(t, "minimize_to_tray", false),
        SuppressLogging = GetBool(t, "suppress_logging", false),
        Language = GetString(t, "language"),
        ThumbwheelSensitivity = (int)GetLong(t, "thumbwheel_sensitivity", AppSettings.DefaultThumbwheelSensitivity),
    };

    private static DeviceConfig ParseDevice(TomlTable t)
    {
        var d = new DeviceConfig
        {
            GestureOwner = ParseGestureOwner(GetString(t, "gesture_owner")),
            Dpi = t.TryGetValue("dpi", out var dpi) ? (uint)Convert.ToInt64(dpi) : null,
            InvertScroll = GetBool(t, "invert_scroll", false),
            SmoothScroll = GetBool(t, "smooth_scroll", false),
        };
        if (t.TryGetValue("dpi_presets", out var dp) && dp is TomlArray presets)
            d.DpiPresets = [.. presets.Select(p => (uint)Convert.ToInt64(p!))];
        if (t.TryGetValue("identity", out var id) && id is TomlTable identity)
            d.Identity = ParseIdentity(identity);
        if (t.TryGetValue("lighting", out var l) && l is TomlTable lighting)
            d.Lighting = new Lighting
            {
                Enabled = GetBool(lighting, "enabled", true),
                Color = GetString(lighting, "color") ?? "ffffff",
                Brightness = (byte)GetLong(lighting, "brightness", 100),
                PaintColor = GetString(lighting, "paint_color"),
                Profile = (int)GetLong(lighting, "profile", 0),
                PerKey = ParsePerKey(lighting),
            };
        if (t.TryGetValue("smartshift", out var s) && s is TomlTable ss)
            d.SmartShift = new SmartShift
            {
                Mode = ParseWheelMode(GetString(ss, "mode")),
                AutoDisengage = (byte)GetLong(ss, "auto_disengage", 0),
                TunableTorque = (byte)GetLong(ss, "tunable_torque", 0),
            };
        if (t.TryGetValue("bindings", out var b) && b is TomlTable bindings)
            foreach (var (key, value) in bindings)
                if (Enum.TryParse<ButtonId>(key, out var button))
                    d.Bindings[button] = ParseBinding(value!);
        if (t.TryGetValue("per_app_bindings", out var pab) && pab is TomlTable perApp)
            foreach (var (bundle, value) in perApp)
                if (value is TomlTable inner)
                {
                    var map = new SortedDictionary<ButtonId, Actions.MouseAction>();
                    foreach (var (bk, bv) in inner)
                        if (Enum.TryParse<ButtonId>(bk, out var button))
                            map[button] = ParseAction(bv!);
                    d.PerAppBindings[bundle] = map;
                }
        return d;
    }

    private static DeviceIdentity ParseIdentity(TomlTable t)
    {
        var caps = t.TryGetValue("capabilities", out var c) && c is TomlTable capTable
            ? new Capabilities
            {
                Buttons = GetBool(capTable, "buttons", false),
                Pointer = GetBool(capTable, "pointer", false),
                Lighting = GetBool(capTable, "lighting", false),
                ScrollInversion = GetBool(capTable, "scroll_inversion", false),
            }
            : new Capabilities();
        return new DeviceIdentity
        {
            DisplayName = GetString(t, "display_name") ?? "",
            Codename = GetString(t, "codename"),
            Kind = ParseDeviceKind(GetString(t, "kind")),
            Capabilities = caps,
            ModelInfo = t.TryGetValue("model_info", out var m) && m is TomlTable mi ? ParseModelInfo(mi) : null,
        };
    }

    private static DeviceModelInfo ParseModelInfo(TomlTable t)
    {
        var transports = t.TryGetValue("transports", out var tr) && tr is TomlTable trans
            ? new DeviceTransports
            {
                Usb = GetBool(trans, "usb", false),
                Equad = GetBool(trans, "equad", false),
                Btle = GetBool(trans, "btle", false),
                Bluetooth = GetBool(trans, "bluetooth", false),
            }
            : new DeviceTransports();
        return new DeviceModelInfo
        {
            EntityCount = (byte)GetLong(t, "entity_count", 0),
            SerialNumber = GetString(t, "serial_number"),
            UnitId = t.TryGetValue("unit_id", out var u) && u is TomlArray ua
                ? [.. ua.Select(x => (byte)Convert.ToInt64(x!))]
                : new byte[4],
            Transports = transports,
            ModelIds = t.TryGetValue("model_ids", out var mids) && mids is TomlArray ma
                ? [.. ma.Select(x => (ushort)Convert.ToInt64(x!))]
                : new ushort[3],
            ExtendedModelId = (byte)GetLong(t, "extended_model_id", 0),
        };
    }

    /// <summary>Parse a lighting table's optional <c>per_key</c> sub-table: decimal zone key → "RRGGBB".</summary>
    private static IReadOnlyDictionary<byte, string> ParsePerKey(TomlTable lighting)
    {
        var map = new Dictionary<byte, string>();
        if (lighting.TryGetValue("per_key", out var pk) && pk is TomlTable perKey)
            foreach (var (key, value) in perKey)
                if (byte.TryParse(key, out var zone) && value is string hex)
                    map[zone] = hex;
        return map;
    }

    /// <summary>Untagged routing, matching Rust: Single is tried before Gesture.</summary>
    private static Binding ParseBinding(object value)
    {
        if (value is string)
            return new Binding.Single(ParseAction(value));
        if (value is TomlTable table)
        {
            // A single-key payload table (SetDpiPreset / CustomShortcut) is a Single.
            if (table.Count == 1 && (table.ContainsKey("SetDpiPreset") || table.ContainsKey("CustomShortcut")))
                return new Binding.Single(ParseAction(table));
            // Otherwise a direction-keyed table is a Gesture.
            var map = new SortedDictionary<GestureDirection, Actions.MouseAction>();
            foreach (var (key, v) in table)
                if (Enum.TryParse<GestureDirection>(key, out var dir))
                    map[dir] = ParseAction(v!);
            return new Binding.Gesture(map);
        }
        throw new ConfigException("unexpected binding value shape");
    }

    private static Actions.MouseAction ParseAction(object value)
    {
        if (value is string name)
        {
            // Legacy macOS action names that collapsed onto TaskView on Windows.
            if (name is "MissionControl" or "AppExpose")
                name = nameof(ActionKind.TaskView);
            // Renamed to the Windows term (it injects the Win key → Start menu).
            if (name is "LaunchpadShow")
                name = nameof(ActionKind.StartMenu);
            if (Enum.TryParse<ActionKind>(name, out var kind)
                && kind is not (ActionKind.SetDpiPreset or ActionKind.CustomShortcut))
                return Actions.MouseAction.Unit(kind);
            throw new ConfigException($"unknown action '{name}'");
        }
        if (value is TomlTable table)
        {
            if (table.TryGetValue("SetDpiPreset", out var idx))
                return Actions.MouseAction.SetDpiPreset((byte)Convert.ToInt64(idx));
            if (table.TryGetValue("CustomShortcut", out var cs) && cs is TomlTable combo)
                return Actions.MouseAction.CustomShortcut(new KeyCombo
                {
                    Modifiers = (byte)GetLong(combo, "modifiers", 0),
                    KeyCode = (ushort)GetLong(combo, "key_code", 0),
                    Display = GetString(combo, "display") ?? "",
                });
        }
        throw new ConfigException("unknown action payload shape");
    }

    /// <summary>Lenient: "Off" → Off; a valid button name → Button; anything else → null (infer).</summary>
    private static GestureOwner? ParseGestureOwner(string? raw)
    {
        if (raw is null) return null;
        if (raw == "Off") return GestureOwner.OffValue;
        return Enum.TryParse<ButtonId>(raw, out var id) ? new GestureOwner.Button(id) : null;
    }

    private static DeviceKind ParseDeviceKind(string? raw) =>
        raw is not null && Enum.TryParse<DeviceKind>(raw, ignoreCase: true, out var k) ? k : DeviceKind.Unknown;

    private static WheelMode ParseWheelMode(string? raw) =>
        raw is not null && Enum.TryParse<WheelMode>(raw, ignoreCase: true, out var m) ? m : WheelMode.Free;

    // ── DOM helpers ──────────────────────────────────────────────────────────

    private static string? GetString(TomlTable t, string key) =>
        t.TryGetValue(key, out var v) && v is string s ? s : null;

    private static bool GetBool(TomlTable t, string key, bool fallback) =>
        t.TryGetValue(key, out var v) && v is bool b ? b : fallback;

    private static long GetLong(TomlTable t, string key, long fallback) =>
        t.TryGetValue(key, out var v) && v is not null ? Convert.ToInt64(v) : fallback;
}
