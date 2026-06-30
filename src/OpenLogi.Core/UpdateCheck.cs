namespace OpenLogi.Core;

/// <summary>
/// Pure version-comparison helpers for the launch-time update check. The network
/// fetch lives in the app layer (it needs an <c>HttpClient</c>); this stays here so
/// the parsing/comparison can be unit-tested without touching GitHub.
/// </summary>
public static class UpdateCheck
{
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
