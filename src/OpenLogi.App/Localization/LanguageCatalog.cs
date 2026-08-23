using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;

namespace OpenLogi.App.Localization;

/// <summary>
/// One entry in the Settings language dropdown. <see cref="Code"/> is <c>null</c>
/// for the "System" entry, which is also how the choice is persisted: a missing
/// <c>language</c> key in config.toml means "follow the OS".
/// </summary>
public sealed class LanguageOption : INotifyPropertyChanged
{
    private readonly string? _endonym;

    internal LanguageOption(string? code, string? endonym)
    {
        Code = code;
        _endonym = endonym;

        // Only the System row is translated; language names stay in their own
        // language ("English US", "Deutsch"), which is what a user hunting for
        // their language in a list they cannot currently read needs to see.
        if (code is null)
            Loc.Current.PropertyChanged += (_, _) =>
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Display)));
    }

    /// <summary>Culture name to force, or <c>null</c> to follow the OS UI language.</summary>
    public string? Code { get; }

    /// <summary>
    /// Row text in the dropdown. The System row names the language it currently
    /// resolves to, so the choice is legible without selecting it first.
    /// </summary>
    public string Display => Code is null
        ? string.Format(CultureInfo.CurrentCulture,
            Loc.Current["Settings_Language_System"], LanguageCatalog.SystemLanguageName)
        : _endonym!;

    public event PropertyChangedEventHandler? PropertyChanged;

    public override string ToString() => Display;
}

/// <summary>
/// The languages the app ships and how a stored setting resolves to one of them.
///
/// English is the neutral resource set, so it is always available; adding a
/// language means adding a <c>Strings.&lt;culture&gt;.resx</c> and one row here.
/// Keep endonyms free of parentheses — they are nested inside the System row's
/// own parentheses ("System (English US)"), and a second pair reads as noise.
/// </summary>
public static class LanguageCatalog
{
    /// <summary>Used whenever nothing better matches — the neutral resource set.</summary>
    public static readonly CultureInfo Fallback = CultureInfo.GetCultureInfo("en-US");

    /// <summary>Cultures with a shipped translation. English only, for now.</summary>
    public static readonly IReadOnlyList<CultureInfo> Shipped = [Fallback];

    /// <summary>Follow the OS UI language.</summary>
    public static readonly LanguageOption System = new(null, null);

    /// <summary>Dropdown rows, in display order. Identity is stable so ComboBox selection matches.</summary>
    public static readonly IReadOnlyList<LanguageOption> All =
    [
        System,
        new("en-US", "English US"),
    ];

    /// <summary>
    /// The culture to render in, for a persisted <c>language</c> setting.
    ///
    /// A named language is honoured when we ship it. Otherwise — the System entry,
    /// a blank value, or a language we shipped once and no longer do — the OS UI
    /// language decides, and English is the last resort. Since English is the only
    /// shipped language today, every path currently lands on English; the fallback
    /// chain is what is being built here, not the outcome.
    /// </summary>
    public static CultureInfo Resolve(string? setting) =>
        Match(setting) ?? Match(Loc.SystemUiCulture.Name) ?? Fallback;

    /// <summary>
    /// The language the System row currently resolves to, named as its own row is.
    /// Falls back to the culture's name if a shipped culture somehow has no row.
    /// </summary>
    public static string SystemLanguageName
    {
        get
        {
            var resolved = Resolve(null);
            foreach (var option in All)
                if (string.Equals(option.Code, resolved.Name, StringComparison.OrdinalIgnoreCase))
                    return option.Display;
            return resolved.Name;
        }
    }

    /// <summary>The dropdown row for a persisted setting; unknown values show as System.</summary>
    public static LanguageOption OptionFor(string? setting)
    {
        if (!string.IsNullOrWhiteSpace(setting))
            foreach (var option in All)
                if (string.Equals(option.Code, setting, StringComparison.OrdinalIgnoreCase))
                    return option;
        return System;
    }

    /// <summary>
    /// The shipped culture serving <paramref name="name"/>: an exact match first,
    /// then the same language in another region — a <c>en-GB</c> desktop should
    /// read the <c>en-US</c> strings rather than fall through to a default that
    /// happens to be the same thing.
    /// </summary>
    private static CultureInfo? Match(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        foreach (var culture in Shipped)
            if (string.Equals(culture.Name, name, StringComparison.OrdinalIgnoreCase))
                return culture;

        var language = name.Split('-')[0];
        foreach (var culture in Shipped)
            if (string.Equals(culture.TwoLetterISOLanguageName, language, StringComparison.OrdinalIgnoreCase))
                return culture;

        return null;
    }
}
