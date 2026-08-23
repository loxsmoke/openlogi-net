using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia.Data;
using OpenLogi.App.Localization;
using OpenLogi.Core.Localization;

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
    public void NeutralStringsResolve()
    {
        Assert.Equal("Settings", Loc.Current["Settings_Title"]);
        Assert.Equal("Launch at login", Loc.Current["Settings_LaunchAtLogin"]);
    }

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
        // English is all we ship, so every OS language lands there today.
        Assert.Equal("en-US", LanguageCatalog.Resolve(null).Name);
        Assert.Equal("en-US", LanguageCatalog.Resolve("").Name);
        Assert.Equal("en-US", LanguageCatalog.Resolve("   ").Name);
    }

    [Fact]
    public void ShippedLanguageIsHonoured() =>
        Assert.Equal("en-US", LanguageCatalog.Resolve("en-US").Name);

    [Fact]
    public void RegionalVariantUsesTheShippedLanguage() =>
        // en-GB has no resources of its own; en-US serves it.
        Assert.Equal("en-US", LanguageCatalog.Resolve("en-GB").Name);

    [Fact]
    public void UnshippedLanguageFallsBackToEnglish()
    {
        // Covers a hand-edited config.toml and a language dropped from a later build.
        Assert.Equal("en-US", LanguageCatalog.Resolve("de-DE").Name);
        Assert.Equal("en-US", LanguageCatalog.Resolve("not-a-culture").Name);
    }

    [Fact]
    public void OptionForMapsThePersistedSetting()
    {
        Assert.Same(LanguageCatalog.System, LanguageCatalog.OptionFor(null));
        Assert.Same(LanguageCatalog.System, LanguageCatalog.OptionFor("de-DE"));
        Assert.Equal("en-US", LanguageCatalog.OptionFor("en-US").Code);
        Assert.Equal("en-US", LanguageCatalog.OptionFor("EN-us").Code);
    }

    [Fact]
    public void SystemRowNamesTheLanguageItResolvesTo()
    {
        // English is all we ship, so System lands there whatever the OS is set to.
        Assert.Null(LanguageCatalog.System.Code);
        Assert.Equal("English US", LanguageCatalog.SystemLanguageName);
        Assert.Equal("System (English US)", LanguageCatalog.System.Display);
    }

    [Fact]
    public void LanguageRowsAreNotTranslated() =>
        Assert.Equal("English US", LanguageCatalog.All.Single(o => o.Code == "en-US").Display);

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
        Assert.Equal("Settings",
            binding.Converter!.Convert(null, typeof(string), binding.ConverterParameter, CultureInfo.InvariantCulture));
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
