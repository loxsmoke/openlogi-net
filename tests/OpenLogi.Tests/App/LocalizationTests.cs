using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia.Data;
using OpenLogi.App.Localization;
using OpenLogi.App.ViewModels;
using OpenLogi.App.Views;
using OpenLogi.Core.Config;
using OpenLogi.Core.DeviceInfo;
using OpenLogi.Core.Gestures;
using OpenLogi.Core.Localization;
using MouseAction = OpenLogi.Core.Actions.MouseAction;

namespace OpenLogi.Tests.App;

/// <summary>
/// The localization pipeline: resource lookup, the fallback chain behind the
/// Settings dropdown, and the key/resource consistency a missing-key fallback
/// would otherwise hide at runtime.
///
/// One class on purpose — xUnit runs tests within a class sequentially, and
/// <see cref="Loc"/> is a process-wide singleton.
/// </summary>
public class LocalizationTests
{
    [Fact]
    public void NeutralStringsResolve() => InCulture("en-US", () =>
    {
        Assert.Equal("Settings", Loc.Current["Settings_Title"]);
        Assert.Equal("Launch at login", Loc.Current["Settings_LaunchAtLogin"]);
    });

    [Fact]
    public void TranslatedStringsResolve() => InCulture("de", () =>
    {
        Assert.Equal("Einstellungen", Loc.Current["Settings_Title"]);
        // The satellite serves every de-* region, and Format works through it too.
        Assert.Equal("Profil 2", Loc.Current.Format("Profile_Numbered", 2));
    });

    [Fact]
    public void GesturePresetNameFollowsCurrentCulture()
    {
        var preset = new GesturePreset("Preset_Disabled",
            MouseAction.None, MouseAction.None, MouseAction.None, MouseAction.None);

        InCulture("en-US", () => Assert.Equal("Disabled", preset.Name));
        InCulture("de", () => Assert.Equal("Deaktiviert", preset.Name));
    }

    [Fact]
    public void BatteryStatusUsesLocalizedLabel() => InCulture("de", () =>
    {
        var device = new PairedDevice
        {
            Slot = 1,
            Kind = DeviceKind.Mouse,
            Online = true,
            Battery = new BatteryInfo
            {
                Percentage = 72,
                Level = BatteryLevel.Good,
                Status = BatteryStatus.Discharging,
            },
        };

        var vm = new DeviceViewModel("Receiver", device, route: null);

        Assert.Equal("72% · Entlädt", vm.Battery);
    });

    [Fact]
    public void DeviceStatusAndDirectConnectionUseLocalizedLabels() => InCulture("de", () =>
    {
        var device = new PairedDevice
        {
            Slot = 1,
            Kind = DeviceKind.Mouse,
            Online = true,
        };

        var vm = new DeviceViewModel("Direct device", device, route: null);

        Assert.Equal("Verbunden", vm.Status);
        Assert.Equal("Direktes Gerät", vm.ReceiverName);
    });

    [Fact]
    public void ExistingPickerAndHostRowsFollowCultureSwitches()
    {
        var action = new ActionChoice(MouseAction.Copy);
        var button = new ButtonBindingViewModel(ButtonId.Back, MouseAction.Copy,
            ButtonBindingViewModel.Catalog, (_, _) => { });
        var direction = new GestureDirectionBindingViewModel(GestureDirection.Left, MouseAction.Copy,
            ButtonBindingViewModel.Catalog, (_, _) => { });
        var host = new HostSlotViewModel(0, isCurrent: true, paired: true, busType: "ble", name: null, supportsDelete: true);

        InCulture("de", () =>
        {
            Assert.Equal("Kopieren", action.Label);
            Assert.Equal("Zurück", button.Label);
            Assert.Equal("Links", direction.Label);
            Assert.Equal("aktuell · ble", host.Status);
        });

        InCulture("en-US", () =>
        {
            Assert.Equal("Copy", action.Label);
            Assert.Equal("Back", button.Label);
            Assert.Equal("Left", direction.Label);
            Assert.Equal("current · ble", host.Status);
        });
    }

    [Fact]
    public void MainWindowTitleKeepsVersionWhenLocalized()
    {
        Assert.Equal("OpenLogi.net 0.16.1", MainWindow.AppTitle("OpenLogi.net", "0.16.1"));
        Assert.Equal("OpenLogi.net", MainWindow.AppTitle("OpenLogi.net", null));
    }

    [Fact]
    public void RegionalVariantUsesTheSatelliteForItsLanguage() => InCulture("de-AT", () =>
        Assert.Equal("Einstellungen", Loc.Current["Settings_Title"]));

    [Fact]
    public void MissingKeyRendersTheKeyItself()
    {
        // A gap has to be visible in the UI, not a blank label.
        Assert.Equal("Settings_NoSuchKey", Loc.Current["Settings_NoSuchKey"]);
        Assert.Equal("", Loc.Current[""]);
    }

    [Fact]
    public void SystemSettingResolvesThroughTheOsLanguage()
    {
        // Asserted against the OS language rather than a fixed answer: this must
        // hold on a German machine too, where System means German.
        var system = LanguageCatalog.Resolve(null);
        Assert.Contains(system, LanguageCatalog.Shipped);
        Assert.Equal(system, LanguageCatalog.Resolve(""));
        Assert.Equal(system, LanguageCatalog.Resolve("   "));
    }

    [Fact]
    public void ShippedLanguageIsHonoured()
    {
        Assert.Equal("en-US", LanguageCatalog.Resolve("en-US").Name);
        Assert.Equal("de", LanguageCatalog.Resolve("de").Name);
    }

    [Fact]
    public void RegionalVariantUsesTheShippedLanguage()
    {
        // Neither has resources of its own; the shipped language for it serves.
        Assert.Equal("en-US", LanguageCatalog.Resolve("en-GB").Name);
        Assert.Equal("de", LanguageCatalog.Resolve("de-CH").Name);
    }

    [Fact]
    public void UnshippedLanguageFallsBackToTheSystemChoice()
    {
        // A hand-edited config.toml, or a language dropped from a later build:
        // treated as if nothing was set, which is English unless the OS says otherwise.
        Assert.Equal(LanguageCatalog.Resolve(null), LanguageCatalog.Resolve("fr-FR"));
        Assert.Equal(LanguageCatalog.Resolve(null), LanguageCatalog.Resolve("not-a-culture"));
    }

    [Fact]
    public void OptionForMapsThePersistedSetting()
    {
        Assert.Same(LanguageCatalog.System, LanguageCatalog.OptionFor(null));
        Assert.Same(LanguageCatalog.System, LanguageCatalog.OptionFor("fr-FR"));
        Assert.Equal("en-US", LanguageCatalog.OptionFor("en-US").Code);
        Assert.Equal("en-US", LanguageCatalog.OptionFor("EN-us").Code);
        Assert.Equal("de", LanguageCatalog.OptionFor("de").Code);
    }

    [Fact]
    public void OptionForMapsRegionalSettingsToTheShippedLanguage()
    {
        Assert.Equal("en-US", LanguageCatalog.OptionFor("en-GB").Code);
        Assert.Equal("de", LanguageCatalog.OptionFor("de-DE").Code);
    }

    [Fact]
    public void SystemRowNamesTheLanguageItResolvesTo()
    {
        // Named against what System actually resolves to on this machine, so the
        // row stays honest on a German desktop as well as an English one.
        var resolved = LanguageCatalog.Resolve(null).Name;
        var expected = LanguageCatalog.All.Single(o => o.Code == resolved).Display;

        Assert.Null(LanguageCatalog.System.Code);
        Assert.Equal(expected, LanguageCatalog.SystemLanguageName);
        Assert.Equal($"System ({expected})", LanguageCatalog.System.Display);
    }

    [Fact]
    public void LanguageRowsAreNotTranslated() => InCulture("de", () =>
    {
        // Endonyms: each language names itself, whatever the UI language is.
        Assert.Equal("English US", LanguageCatalog.All.Single(o => o.Code == "en-US").Display);
        Assert.Equal("Deutsch", LanguageCatalog.All.Single(o => o.Code == "de").Display);
    });

    /// <summary>Run <paramref name="body"/> with the UI language pinned, then restore it.</summary>
    private static void InCulture(string name, System.Action body)
    {
        var original = Loc.Current.Culture;
        Loc.Current.SetCulture(CultureInfo.GetCultureInfo(name));
        try { body(); }
        finally { Loc.Current.SetCulture(original); }
    }

    [Fact]
    public void SystemRowSurvivesAMissingFormatString()
    {
        // Get() returns the key when a translation drops the entry; string.Format
        // must not be handed a placeholder-free template it then chokes on.
        var format = Loc.Current["Settings_Language_System"];
        Assert.Contains("{0}", format);
        Assert.Equal("Settings_Language_System",
            string.Format(CultureInfo.InvariantCulture, "Settings_Language_System", "English US"));
    }

    [Fact]
    public void TrExtensionBindsToTheCultureAndLooksUpItsKey()
    {
        var binding = Assert.IsType<Binding>(new TrExtension("Settings_Title").ProvideValue(null!));

        Assert.Same(Loc.Current, binding.Source);
        Assert.Equal(nameof(Loc.Culture), binding.Path);
        Assert.Equal(BindingMode.OneWay, binding.Mode);
        Assert.Equal("Settings_Title",
            Assert.IsType<string>(binding.ConverterParameter));
        InCulture("en-US", () => Assert.Equal("Settings",
            binding.Converter!.Convert(null, typeof(string), binding.ConverterParameter, CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void CultureChangeNotifiesEveryLocalizedBinding()
    {
        var original = Loc.Current.Culture;
        var changed = new List<string?>();
        void OnChanged(object? _, System.ComponentModel.PropertyChangedEventArgs e) => changed.Add(e.PropertyName);

        Loc.Current.PropertyChanged += OnChanged;
        try
        {
            // Any culture works as the signal; with only neutral resources present,
            // ResourceManager keeps returning the English strings.
            Loc.Current.SetCulture(CultureInfo.GetCultureInfo("de-DE"));
            Assert.Contains(nameof(Loc.Culture), changed);
            Assert.Contains("Item[]", changed);

            changed.Clear();
            Loc.Current.SetCulture(CultureInfo.GetCultureInfo("de-DE"));
            Assert.Empty(changed); // no-op re-selection must not churn every binding
        }
        finally
        {
            Loc.Current.PropertyChanged -= OnChanged;
            Loc.Current.SetCulture(original);
        }
    }

    [Fact]
    public void EveryKeyUsedInXamlExists()
    {
        var defined = ResourceKeys();
        foreach (var file in Directory.EnumerateFiles(SrcRoot(), "*.axaml", SearchOption.AllDirectories))
            foreach (Match match in Regex.Matches(File.ReadAllText(file), @"\{loc:Tr\s+(?:Key=)?([A-Za-z0-9_]+)"))
            {
                var key = match.Groups[1].Value;
                Assert.True(defined.Contains(key),
                    $"{Path.GetFileName(file)} uses {{loc:Tr {key}}}, which is not in Strings.resx");
            }
    }

    [Fact]
    public void NoOrphanedResourceKeys()
    {
        var source = string.Concat(Directory
            .EnumerateFiles(SrcRoot(), "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".axaml") || f.EndsWith(".cs"))
            .Select(File.ReadAllText));

        foreach (var key in ResourceKeys())
            Assert.True(source.Contains(key, StringComparison.Ordinal),
                $"Strings.resx defines {key}, which nothing references");
    }

    private static HashSet<string> ResourceKeys() =>
    [
        .. XDocument.Load(Path.Combine(SrcRoot(), "OpenLogi.Core", "Localization", "Strings.resx"))
            .Root!.Elements("data")
            .Select(d => d.Attribute("name")!.Value),
    ];

    /// <summary>The <c>src</c> tree — resource keys are used from Core as well as the app.</summary>
    internal static string SrcRoot() => Directory.GetParent(SourceRoot("OpenLogi.Core"))!.FullName;

    /// <summary>Walk up from the test binaries to a project directory under <c>src</c>.</summary>
    internal static string SourceRoot(string project)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", project);
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException($"could not locate src/{project} from the test output directory");
    }
}
