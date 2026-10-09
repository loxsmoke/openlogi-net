using OpenLogi.Core.Actions;
using OpenLogi.Core.Config;
using OpenLogi.Core.Gestures;

namespace OpenLogi.Tests;

/// <summary>
/// Guards the untagged <see cref="Binding"/> routing and tagged-union
/// <see cref="MouseAction"/> encoding through a full TOML round-trip via the config
/// codec. Mirrors the Rust <c>binding</c> serde tests.
/// </summary>
public class BindingSerializationTests
{
    private static SortedDictionary<ButtonId, Binding> Roundtrip(SortedDictionary<ButtonId, Binding> bindings)
    {
        var cfg = new Config();
        foreach (var (button, binding) in bindings)
            cfg.SetBinding("dev", button, binding);

        var dir = Directory.CreateTempSubdirectory("openlogi-bind");
        try
        {
            var path = Path.Combine(dir.FullName, "config.toml");
            cfg.SaveToPath(path);
            return Config.LoadFromPath(path).BindingsFor("dev");
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void SingleRoundtripsIncludingPayloadVariants()
    {
        var bindings = new SortedDictionary<ButtonId, Binding>
        {
            [ButtonId.Back] = new Binding.Single(MouseAction.BrowserBack),
            [ButtonId.DpiToggle] = new Binding.Single(MouseAction.SetDpiPreset(2)),
            [ButtonId.Forward] = new Binding.Single(MouseAction.CustomShortcut(KeyCombo.Parse("Ctrl+P"))),
        };
        var back = Roundtrip(bindings);
        Assert.Equal(new Binding.Single(MouseAction.BrowserBack), back[ButtonId.Back]);
        Assert.Equal(new Binding.Single(MouseAction.SetDpiPreset(2)), back[ButtonId.DpiToggle]);
        var forward = Assert.IsType<Binding.Single>(back[ButtonId.Forward]);
        Assert.Equal(ActionKind.CustomShortcut, forward.Action.Kind);
    }

    [Fact]
    public void GestureRoundtrips()
    {
        var map = new SortedDictionary<GestureDirection, MouseAction>
        {
            [GestureDirection.Up] = MouseAction.Copy,
            [GestureDirection.Click] = MouseAction.Paste,
        };
        var bindings = new SortedDictionary<ButtonId, Binding>
        {
            [ButtonId.GestureButton] = new Binding.Gesture(map),
        };
        var back = Roundtrip(bindings);
        Assert.Equal(new Binding.Gesture(map), back[ButtonId.GestureButton]);
    }

    [Fact]
    public void DirectionKeyedTableRoutesToGesture()
    {
        foreach (var dir in GestureDirectionExtensions.All)
        {
            var toml = $"schema_version = 3\n[devices.dev.bindings.GestureButton]\n{dir} = \"None\"\n";
            var dirPath = Directory.CreateTempSubdirectory("openlogi-bind");
            try
            {
                var path = Path.Combine(dirPath.FullName, "config.toml");
                File.WriteAllText(path, toml);
                var binding = Config.LoadFromPath(path).BindingsFor("dev")[ButtonId.GestureButton];
                Assert.IsType<Binding.Gesture>(binding);
            }
            finally { dirPath.Delete(recursive: true); }
        }
    }

    [Fact]
    public void PayloadActionStaysSingle()
    {
        var toml = "schema_version = 3\n[devices.dev.bindings.DpiToggle]\nSetDpiPreset = 2\n";
        var dir = Directory.CreateTempSubdirectory("openlogi-bind");
        try
        {
            var path = Path.Combine(dir.FullName, "config.toml");
            File.WriteAllText(path, toml);
            var binding = Config.LoadFromPath(path).BindingsFor("dev")[ButtonId.DpiToggle];
            Assert.Equal(new Binding.Single(MouseAction.SetDpiPreset(2)), binding);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void CaptureRegionRoundtripsAsSingleString()
    {
        var toml = "schema_version = 3\n[devices.dev.bindings]\nBack = \"CaptureRegion\"\n";
        var dir = Directory.CreateTempSubdirectory("openlogi-bind");
        try
        {
            var path = Path.Combine(dir.FullName, "config.toml");
            File.WriteAllText(path, toml);
            var binding = Config.LoadFromPath(path).BindingsFor("dev")[ButtonId.Back];
            Assert.Equal(new Binding.Single(MouseAction.CaptureRegion), binding);
        }
        finally { dir.Delete(recursive: true); }

        Assert.Equal("Capture Region", MouseAction.CaptureRegion.Label());
        Assert.Equal(Category.System, MouseAction.CaptureRegion.Category());
        Assert.Contains(MouseAction.CaptureRegion, MouseAction.Catalog());
    }

    [Fact]
    public void AllCatalogVariantsRoundtripToml()
    {
        var bindings = new SortedDictionary<ButtonId, Binding>();
        // Map each catalog action onto Back, one at a time, and round-trip it.
        foreach (var action in MouseAction.Catalog())
        {
            bindings[ButtonId.Back] = new Binding.Single(action);
            var back = Roundtrip(bindings);
            Assert.Equal(new Binding.Single(action), back[ButtonId.Back]);
        }
    }

    [Theory]
    [InlineData("Ctrl+Shift+P")]
    [InlineData("Win+Left")]
    [InlineData("Alt+VK0xE7")]
    [InlineData("")] // cleared
    public void CustomShortcutRoundtripsToml(string chord)
    {
        var action = MouseAction.CustomShortcut(KeyCombo.Parse(chord));
        var bindings = new SortedDictionary<ButtonId, Binding> { [ButtonId.Back] = new Binding.Single(action) };
        Assert.Equal(new Binding.Single(action), Roundtrip(bindings)[ButtonId.Back]);
    }

    [Fact]
    public void CustomShortcutInsideGestureMapStaysAGesture()
    {
        var map = new SortedDictionary<GestureDirection, MouseAction>
        {
            [GestureDirection.Up] = MouseAction.CustomShortcut(KeyCombo.Parse("Win+Up")),
            [GestureDirection.Click] = MouseAction.Paste,
        };
        var bindings = new SortedDictionary<ButtonId, Binding> { [ButtonId.Back] = new Binding.Gesture(map) };
        Assert.Equal(new Binding.Gesture(map), Roundtrip(bindings)[ButtonId.Back]);
    }

    [Fact]
    public void CustomShortcutIsStoredAsText()
    {
        var cfg = new Config();
        cfg.SetBinding("dev", ButtonId.Back, new Binding.Single(MouseAction.CustomShortcut(KeyCombo.Parse("ctrl+shift+p"))));
        var body = ConfigCodec.Serialize(cfg);
        Assert.Contains("CustomShortcut", body);
        Assert.Contains("\"Ctrl+Shift+P\"", body); // canonical spelling, whatever was typed
        Assert.DoesNotContain("key_code", body);
    }

    [Fact]
    public void HandWrittenShortcutTextParsesAsSingle()
    {
        var toml = "schema_version = 3\n[devices.dev.bindings]\nBack = { CustomShortcut = \"alt+f4\" }\n";
        var binding = LoadBindings(toml)[ButtonId.Back];
        Assert.Equal(new Binding.Single(MouseAction.CustomShortcut(KeyCombo.Parse("Alt+F4"))), binding);
    }

    [Theory]
    [InlineData("Back = { CustomShortcut = \"Ctrl+Bogus\" }")]
    [InlineData("Back = \"CustomShortcut\"")]
    public void MalformedShortcutIsAConfigError(string line)
    {
        var toml = $"schema_version = 3\n[devices.dev.bindings]\n{line}\n";
        Assert.Throws<ConfigException>(() => LoadBindings(toml));
    }

    private static SortedDictionary<ButtonId, Binding> LoadBindings(string toml)
    {
        var dir = Directory.CreateTempSubdirectory("openlogi-bind");
        try
        {
            var path = Path.Combine(dir.FullName, "config.toml");
            File.WriteAllText(path, toml);
            return Config.LoadFromPath(path).BindingsFor("dev");
        }
        finally { dir.Delete(recursive: true); }
    }
}
