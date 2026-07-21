namespace OpenLogi.Core;

/// <summary>
/// Pure version-comparison helpers for the launch-time update check. The network
/// fetch lives in the app layer (it needs an <c>HttpClient</c>); this stays here so
/// the parsing/comparison can be unit-tested without touching GitHub.
/// </summary>
public static class UpdateCheck
{
    /// <summary>
    /// How long a release must have been public before we offer it. Fresh releases are
    /// the ones most likely to be pulled or hot-fixed, so the banner stays quiet for a
    /// day and users aren't pushed onto a build nobody has shaken out yet.
    /// </summary>
    public static readonly TimeSpan SoakPeriod = TimeSpan.FromHours(24);

    /// <summary>
    /// True once <paramref name="publishedAt"/> is at least <see cref="SoakPeriod"/> in the
    /// past. <paramref name="now"/> is a parameter rather than <c>DateTimeOffset.UtcNow</c>
    /// so the boundary is unit-testable; a future timestamp (clock skew) is never eligible.
    /// </summary>
    public static bool IsEligible(DateTimeOffset publishedAt, DateTimeOffset now) =>
        now - publishedAt >= SoakPeriod;

    /// <summary>
    /// Name of the installer asset attached to a release — must stay in step with
    /// <c>OutputBaseFilename</c> in <c>build/OpenLogi.iss</c>.
    /// </summary>
    public static string SetupAssetName(string version) => $"{Brand.AppName}-{version}-setup.exe";

    /// <summary>
    /// What the update banner should do in response to a check.
    /// </summary>
    public enum BannerState
    {
        /// <summary>No usable answer — leave whatever is on screen alone.</summary>
        Unchanged,
        /// <summary>Nothing should be offered right now; retract any existing offer.</summary>
        Hidden,
        /// <summary>Offer the release.</summary>
        Shown,
    }

    /// <summary>
    /// Decide what the banner shows, given the single release GitHub calls "latest".
    /// </summary>
    /// <remarks>
    /// <see cref="BannerState.Hidden"/> retracts unconditionally, which is what makes a
    /// rapid follow-up release safe: when 0.66.1 lands three hours after 0.66.0, the next
    /// check sees an ineligible 0.66.1 and pulls the already-showing 0.66.0 offer, rather
    /// than leaving a long-running session pointed at the build 0.66.1 replaced. A null
    /// <paramref name="latest"/> means "no newer release" or a transient outage — the two
    /// are indistinguishable from here, and neither justifies retracting a real offer.
    /// </remarks>
    public static BannerState Decide(ReleaseInfo? latest, string? dismissed, DateTimeOffset now)
    {
        if (latest is null) return BannerState.Unchanged;
        if (latest.Version == dismissed) return BannerState.Hidden;
        return IsEligible(latest.PublishedAt, now) ? BannerState.Shown : BannerState.Hidden;
    }

    /// <summary>
    /// Parse a release tag like "v0.3.0", "0.3.0", or "v0.3.0-beta" into a 3-part
    /// <see cref="Version"/> (any pre-release / build-metadata suffix is dropped), or
    /// <c>null</c> if it has no dotted-numeric core.
    /// </summary>
    public static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var s = tag.Trim();
        if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V')) s = s[1..];
        // Drop any pre-release / build-metadata suffix ("-beta", "+sha", trailing junk).
        var cut = s.IndexOfAny(['-', '+', ' ']);
        if (cut >= 0) s = s[..cut];
        return Version.TryParse(s, out var v) ? Normalize(v) : null;
    }

    /// <summary>
    /// True when the release <paramref name="latestTag"/> parses to a version strictly
    /// newer than <paramref name="current"/>. A null/garbage tag is never "newer".
    /// </summary>
    public static bool IsNewer(Version current, string? latestTag)
    {
        var latest = ParseTag(latestTag);
        return latest is not null && latest > Normalize(current);
    }

    // Compare on major.minor.build only, treating unspecified components as 0, so a
    // 2-part tag ("0.3"), a 3-part tag, and a 4-part assembly version (which carries a
    // revision) all compare consistently.
    private static Version Normalize(Version v) =>
        new(v.Major, v.Minor, v.Build < 0 ? 0 : v.Build);
}

/// <summary>
/// The facts about a newer release the update banner needs: its normalized version, when
/// it went public (for the soak gate), and where to get the installer. <see cref="SetupUrl"/>
/// is null when the release has no matching setup.exe asset — the Install action is then
/// unavailable and only the GitHub link makes sense.
/// </summary>
public sealed record ReleaseInfo(string Version, DateTimeOffset PublishedAt, string? SetupUrl, long SetupSize);
