namespace OpenLogi.Core;

/// <summary>
/// Brand constants shared across the app: public URLs and the
/// <c>openlogi://</c> deep-link command vocabulary. Ported from the Rust
/// <c>openlogi-core::brand</c> module.
/// </summary>
public static class Brand
{
    /// <summary>The product display name.</summary>
    public const string AppName = "OpenLogi.net";

    /// <summary>The OpenLogi.net GitHub repository.</summary>
    public const string RepoUrl = "https://github.com/LoxSmoke/openlogi-net";

    /// <summary>The README, used as the in-app "Help" link.</summary>
    public const string HelpUrl = "https://github.com/LoxSmoke/openlogi-net#readme";

    /// <summary>The "latest release" page.</summary>
    public const string ReleasesUrl = "https://github.com/LoxSmoke/openlogi-net/releases/latest";

    /// <summary>The GitHub API endpoint for the latest release (used by the launch-time update check).</summary>
    public const string LatestReleaseApiUrl = "https://api.github.com/repos/LoxSmoke/openlogi-net/releases/latest";

    /// <summary>The release page for a specific version tag (e.g. the running build).</summary>
    public static string ReleaseTagUrl(string version) => $"{RepoUrl}/releases/tag/v{version}";
}

/// <summary>
/// A GUI action requested by opening an <c>openlogi://&lt;name&gt;</c> URL.
/// Command names are defined once, in <see cref="AsName"/>, so producers and
/// consumers cannot disagree.
/// </summary>
public enum DeeplinkCommand
{
    /// <summary>Show / foreground the main window.</summary>
    Show,
    /// <summary>Open the Settings window.</summary>
    OpenSettings,
    /// <summary>Open the About window.</summary>
    OpenAbout,
    /// <summary>Run a manual update check.</summary>
    CheckForUpdates,
    /// <summary>Quit the GUI.</summary>
    Quit,
}

/// <summary>Wire-name mapping and URL parsing for <see cref="DeeplinkCommand"/>.</summary>
public static class Deeplink
{
    /// <summary>The URL scheme OpenLogi registers.</summary>
    public const string Scheme = "openlogi";

    /// <summary>The wire name for a command — the host component of its URL.</summary>
    public static string AsName(this DeeplinkCommand cmd) => cmd switch
    {
        DeeplinkCommand.Show => "show",
        DeeplinkCommand.OpenSettings => "open-settings",
        DeeplinkCommand.OpenAbout => "open-about",
        DeeplinkCommand.CheckForUpdates => "check-for-updates",
        DeeplinkCommand.Quit => "quit",
        _ => throw new ArgumentOutOfRangeException(nameof(cmd)),
    };

    /// <summary>Build the <c>openlogi://&lt;name&gt;</c> URL for a command.</summary>
    public static string ToUrl(this DeeplinkCommand cmd) => $"{Scheme}://{cmd.AsName()}";

    /// <summary>Parse a command from its wire name (the part after <c>openlogi://</c>).</summary>
    public static DeeplinkCommand? FromName(string name) => name switch
    {
        "show" => DeeplinkCommand.Show,
        "open-settings" => DeeplinkCommand.OpenSettings,
        "open-about" => DeeplinkCommand.OpenAbout,
        "check-for-updates" => DeeplinkCommand.CheckForUpdates,
        "quit" => DeeplinkCommand.Quit,
        _ => null,
    };

    /// <summary>
    /// Parse a full <c>openlogi://…</c> URL. The command lives in the host
    /// component, so any trailing path or query is ignored. Returns
    /// <c>null</c> for a foreign scheme or unknown command.
    /// </summary>
    public static DeeplinkCommand? ParseUrl(string url)
    {
        const string prefix = Scheme + "://";
        if (!url.StartsWith(prefix, StringComparison.Ordinal))
            return null;
        var rest = url[prefix.Length..];
        var name = rest.Split('/', '?')[0];
        return name.Length == 0 ? null : FromName(name);
    }
}
