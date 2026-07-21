using System;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenLogi.Core;

namespace OpenLogi.App.Services;

/// <summary>
/// Update check: asks GitHub for the latest release and reports it when newer than the
/// running build. Every failure mode (offline, rate-limited, malformed response, foreign
/// tag format) collapses to <c>null</c> so the check can never disrupt startup.
/// </summary>
/// <remarks>
/// This reports facts only — the soak gate (<see cref="UpdateCheck.IsEligible"/>) and the
/// dismissed-version check are the view model's call.
/// </remarks>
public static class UpdateService
{
    private static readonly HttpClient Default = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>
    /// Return the latest release when it is newer than <paramref name="current"/>, else
    /// <c>null</c>. <paramref name="http"/> is an injection seam for tests (same pattern as
    /// <c>AssetClient</c>); production passes nothing and gets the shared client.
    /// </summary>
    public static async Task<ReleaseInfo?> CheckAsync(Version current, HttpClient? http = null, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Brand.LatestReleaseApiUrl);
            // GitHub's API rejects requests without a User-Agent; identify ourselves.
            request.Headers.UserAgent.ParseAdd($"{Brand.AppName}-update-check");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var response = await (http ?? Default).SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var root = doc.RootElement;

            if (!root.TryGetProperty("tag_name", out var tagEl)) return null;
            var tag = tagEl.GetString();
            if (!UpdateCheck.IsNewer(current, tag)) return null;
            var version = UpdateCheck.ParseTag(tag)!.ToString();

            // A release with no usable timestamp can't clear the soak gate, so treat a
            // missing/garbage published_at as "just now" rather than offering it early.
            // Invariant culture + RoundtripKind: published_at is ISO-8601, and the current
            // culture must not get a say — a non-Gregorian default calendar (ar-SA uses
            // Umm al-Qura) would otherwise parse it to a wildly wrong date and either
            // suppress updates forever or skip the soak entirely.
            var publishedAt = root.TryGetProperty("published_at", out var pubEl)
                              && pubEl.ValueKind == JsonValueKind.String
                              && DateTimeOffset.TryParse(pubEl.GetString(), CultureInfo.InvariantCulture,
                                                         DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : DateTimeOffset.UtcNow;

            var (setupUrl, setupSize) = FindSetupAsset(root, version);
            return new ReleaseInfo(version, publishedAt, setupUrl, setupSize);
        }
        catch { return null; }
    }

    /// <summary>
    /// Locate the release's installer asset by exact name. Anything else attached to the
    /// release (portable zip, checksums) is ignored.
    /// </summary>
    private static (string? Url, long Size) FindSetupAsset(JsonElement root, string version)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return (null, 0);

        var wanted = UpdateCheck.SetupAssetName(version);
        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out var nameEl)) continue;
            if (!string.Equals(nameEl.GetString(), wanted, StringComparison.OrdinalIgnoreCase)) continue;
            if (!asset.TryGetProperty("browser_download_url", out var urlEl)) continue;

            var url = urlEl.GetString();
            if (string.IsNullOrWhiteSpace(url)) continue;
            var size = asset.TryGetProperty("size", out var sizeEl) && sizeEl.TryGetInt64(out var s) ? s : 0;
            return (url, size);
        }
        return (null, 0);
    }
}
