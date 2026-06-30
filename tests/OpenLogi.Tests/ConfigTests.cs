using OpenLogi.Core;

namespace OpenLogi.Tests;

public class ConfigTests
{
    private static Config WriteAndRead(Config config)
    {
        var dir = Directory.CreateTempSubdirectory("openlogi-cfg");
        try
        {
            var path = Path.Combine(dir.FullName, "config.toml");
            config.SaveToPath(path);
            return Config.LoadFromPath(path);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void MissingFileYieldsDefault()
    {
        var dir = Directory.CreateTempSubdirectory("openlogi-cfg");
        try
        {
            var path = Path.Combine(dir.FullName, "nonexistent.toml");
            var cfg = Config.LoadFromPath(path);
            Assert.Equal(Config.SchemaVersion, cfg.Version);
            Assert.Empty(cfg.Devices);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void LightingRoundtripsPerDevice()
    {
        var cfg = new Config();
        cfg.SetLighting("g513", new Lighting { Enabled = true, Color = "00aabb", Brightness = 75 });
        var restored = WriteAndRead(cfg);
        Assert.Equal(new Lighting { Enabled = true, Color = "00aabb", Brightness = 75 }, restored.Lighting("g513"));
        Assert.Null(restored.Lighting("absent"));
    }

    [Fact]
    public void PerKeyColorsRoundtripPerDevice()
    {
        var cfg = new Config();
        var perKey = new Dictionary<byte, string> { [0x68] = "ff0000", [0x43] = "00ff00" };
        cfg.SetLighting("g915", new Lighting { Color = "1e90ff", Brightness = 100, PaintColor = "abcdef", PerKey = perKey });
        var restored = WriteAndRead(cfg);
        var lighting = restored.Lighting("g915");
        Assert.NotNull(lighting);
        Assert.Equal(2, lighting!.PerKey.Count);
        Assert.Equal("ff0000", lighting.PerKey[0x68]);
        Assert.Equal("00ff00", lighting.PerKey[0x43]);
        Assert.Equal("abcdef", lighting.PaintColor);
    }

    [Fact]
    public void DefaultPerKeyIsOmittedFromToml()
    {
        var cfg = new Config();
        cfg.SetLighting("g915", new Lighting { Color = "1e90ff", Brightness = 100 });
        var body = ConfigCodec.Serialize(cfg);
        Assert.DoesNotContain("per_key", body);
    }

    [Fact]
    public void DpiRoundtripsPerDevice()
    {
        var cfg = new Config();
        cfg.SetDpi("2b042", 1600);
        var restored = WriteAndRead(cfg);
        Assert.Equal(1600u, restored.Dpi("2b042"));
        Assert.Null(restored.Dpi("absent"));
    }

    [Fact]
    public void SmartShiftRoundtripsPerDevice()
    {
        var cfg = new Config();
        cfg.SetSmartShift("2b042", new SmartShift { Mode = WheelMode.Ratchet, AutoDisengage = 16, TunableTorque = 30 });
        var restored = WriteAndRead(cfg);
        Assert.Equal(new SmartShift { Mode = WheelMode.Ratchet, AutoDisengage = 16, TunableTorque = 30 }, restored.SmartShift("2b042"));
        Assert.Null(restored.SmartShift("absent"));
    }

    [Fact]
    public void InvertScrollRoundtripsPerDevice()
    {
        var cfg = new Config();
        Assert.False(cfg.InvertScroll("2b042"));
        cfg.SetInvertScroll("2b042", true);
        var restored = WriteAndRead(cfg);
        Assert.True(restored.InvertScroll("2b042"));
        Assert.False(restored.InvertScroll("absent"));
    }

    [Fact]
    public void DefaultInvertScrollIsOmittedFromToml()
    {
        var cfg = new Config();
        cfg.SetBinding("2b042", ButtonId.Back, new Binding.Single(Action.Copy));
        cfg.SetInvertScroll("2b042", false);
        var body = ConfigCodec.Serialize(cfg);
        Assert.DoesNotContain("invert_scroll", body);
    }

    [Fact]
    public void BindingsRoundtripPerDevice()
    {
        var cfg = new Config();
        cfg.SetBinding("2b042", ButtonId.Back, new Binding.Single(Action.Copy));
        cfg.SetBinding("2b042", ButtonId.DpiToggle, new Binding.Single(Action.CustomShortcut(new KeyCombo
        {
            Modifiers = KeyCombo.ModCmd,
            KeyCode = 0x23,
            Display = "⌘P",
        })));
        cfg.SetBinding("4082d", ButtonId.Back, new Binding.Single(Action.Paste));

        var parsed = WriteAndRead(cfg);

        var a = parsed.BindingsFor("2b042");
        Assert.Equal(new Binding.Single(Action.Copy), a[ButtonId.Back]);
        Assert.Equal(
            new Binding.Single(Action.CustomShortcut(new KeyCombo { Modifiers = KeyCombo.ModCmd, KeyCode = 0x23, Display = "⌘P" })),
            a[ButtonId.DpiToggle]);

        var b = parsed.BindingsFor("4082d");
        Assert.Equal(new Binding.Single(Action.Paste), b[ButtonId.Back]);
        Assert.Single(b);

        Assert.Empty(parsed.BindingsFor("deadbeef"));
    }

    [Fact]
    public void HumanReadableTomlLayout()
    {
        var cfg = new Config();
        cfg.SetBinding("2b042", ButtonId.Back, new Binding.Single(Action.BrowserBack));
        var body = ConfigCodec.Serialize(cfg);
        Assert.Contains("schema_version = 3", body);
        Assert.Contains("[devices.2b042.bindings]", body);
        Assert.Contains("Back = \"BrowserBack\"", body);
    }

    [Fact]
    public void DpiPresetsRoundtripPerDevice()
    {
        var cfg = new Config();
        cfg.SetDpiPresets("2b042", [800, 1600, 3200]);
        cfg.SetDpiPresets("4082d", [400, 1600]);
        var parsed = WriteAndRead(cfg);
        Assert.Equal([800u, 1600u, 3200u], parsed.DpiPresets("2b042"));
        Assert.Equal([400u, 1600u], parsed.DpiPresets("4082d"));
        Assert.Empty(parsed.DpiPresets("unknown"));
    }

    [Fact]
    public void EmptyDpiPresetsSkipSerialization()
    {
        var cfg = new Config();
        cfg.SetBinding("2b042", ButtonId.Back, new Binding.Single(Action.Copy));
        cfg.SetDpiPresets("2b042", [800]);
        cfg.SetDpiPresets("2b042", []);
        var body = ConfigCodec.Serialize(cfg);
        Assert.DoesNotContain("dpi_presets", body);
    }

    [Fact]
    public void DeviceIdentityRoundtripsAndIsIterable()
    {
        var cfg = new Config();
        var mouse = new DeviceIdentity
        {
            DisplayName = "MX Master 3S",
            Kind = DeviceKind.Mouse,
            Capabilities = new Capabilities { Buttons = true, Pointer = true },
        };
        cfg.SetDeviceIdentity("2b034", mouse);
        cfg.SetBinding("2b034", ButtonId.Back, new Binding.Single(Action.BrowserBack));

        var parsed = WriteAndRead(cfg);
        Assert.Equal(mouse, parsed.DeviceIdentity("2b034"));
        Assert.Null(parsed.DeviceIdentity("absent"));
        Assert.Equal(new Binding.Single(Action.BrowserBack), parsed.BindingsFor("2b034")[ButtonId.Back]);
        Assert.Equal([("2b034", mouse)], parsed.KnownIdentities().ToArray());
    }

    [Fact]
    public void DeviceModelInfoRoundtripsInsideIdentity()
    {
        var cfg = new Config();
        var identity = new DeviceIdentity
        {
            DisplayName = "MX Master 4",
            Codename = "MX4",
            Kind = DeviceKind.Mouse,
            Capabilities = new Capabilities { Buttons = true, Pointer = true },
            ModelInfo = new DeviceModelInfo
            {
                EntityCount = 5,
                SerialNumber = "ABC123",
                UnitId = [0xde, 0xad, 0xbe, 0xef],
                Transports = new DeviceTransports { Usb = true, Bluetooth = true },
                ModelIds = [0xb042, 0xc000, 0],
                ExtendedModelId = 0x02,
            },
        };
        cfg.SetDeviceIdentity("2b042", identity);
        var parsed = WriteAndRead(cfg);
        Assert.Equal(identity, parsed.DeviceIdentity("2b042"));
        Assert.Equal("2b042", parsed.DeviceIdentity("2b042")!.ModelInfo!.ConfigKey());
    }

    [Fact]
    public void SelectedDeviceRoundtrips()
    {
        var cfg = new Config();
        Assert.Null(cfg.SelectedDeviceKey());
        cfg.SetSelectedDevice("2b042");
        var parsed = WriteAndRead(cfg);
        Assert.Equal("2b042", parsed.SelectedDeviceKey());
    }

    [Fact]
    public void ClearedSelectedDeviceOmitsField()
    {
        var cfg = new Config();
        cfg.SetSelectedDevice("2b042");
        cfg.SetSelectedDevice(null);
        Assert.DoesNotContain("selected_device", ConfigCodec.Serialize(cfg));
    }

    [Fact]
    public void PerAppOverlayTakesPrecedence()
    {
        var cfg = new Config();
        cfg.SetBinding("2b042", ButtonId.Back, new Binding.Single(Action.BrowserBack));
        cfg.SetBinding("2b042", ButtonId.Forward, new Binding.Single(Action.BrowserForward));
        cfg.SetPerAppBinding("2b042", "com.microsoft.VSCode", ButtonId.Back, Action.Undo);

        var global = cfg.EffectiveBindings("2b042", null);
        Assert.Equal(new Binding.Single(Action.BrowserBack), global[ButtonId.Back]);
        Assert.Equal(new Binding.Single(Action.BrowserForward), global[ButtonId.Forward]);

        var vscode = cfg.EffectiveBindings("2b042", "com.microsoft.VSCode");
        Assert.Equal(new Binding.Single(Action.Undo), vscode[ButtonId.Back]);
        Assert.Equal(new Binding.Single(Action.BrowserForward), vscode[ButtonId.Forward]);

        var other = cfg.EffectiveBindings("2b042", "com.apple.Safari");
        Assert.Equal(new Binding.Single(Action.BrowserBack), other[ButtonId.Back]);
    }

    [Fact]
    public void PerAppBindingRemovalPrunesEmptyApp()
    {
        var cfg = new Config();
        cfg.SetPerAppBinding("2b042", "com.example.App", ButtonId.Back, Action.Copy);
        cfg.SetPerAppBinding("2b042", "com.example.App", ButtonId.Back, null);
        Assert.Empty(cfg.Devices["2b042"].PerAppBindings);
    }

    [Fact]
    public void AppSettingsDefaultOmitsBlock()
    {
        Assert.DoesNotContain("app_settings", ConfigCodec.Serialize(new Config()));
    }

    [Fact]
    public void AppSettingsLaunchAtLoginRoundtrips()
    {
        var cfg = new Config();
        cfg.AppSettings.LaunchAtLogin = true;
        var parsed = WriteAndRead(cfg);
        Assert.True(parsed.AppSettings.LaunchAtLogin);
    }

    [Fact]
    public void AppSettingsDismissedUpdateRoundtrips()
    {
        var cfg = new Config();
        cfg.AppSettings.DismissedUpdate = "0.3.0";
        var parsed = WriteAndRead(cfg);
        Assert.Equal("0.3.0", parsed.AppSettings.DismissedUpdate);
    }

    [Fact]
    public void AppSettingsMinimizeToTrayRoundtrips()
    {
        var cfg = new Config();
        cfg.AppSettings.MinimizeToTray = true;
        var parsed = WriteAndRead(cfg);
        Assert.True(parsed.AppSettings.MinimizeToTray);
    }

    [Fact]
    public void UnsupportedNewerSchemaIsRejected()
    {
        var dir = Directory.CreateTempSubdirectory("openlogi-cfg");
        try
        {
            var path = Path.Combine(dir.FullName, "config.toml");
            File.WriteAllText(path, "schema_version = 999\n");
            Assert.Throws<ConfigException>(() => Config.LoadFromPath(path));
        }
        finally { dir.Delete(recursive: true); }
    }
}
