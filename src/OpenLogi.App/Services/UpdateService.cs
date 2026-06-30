using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using OpenLogi.Core;

namespace OpenLogi.App.Services;

/// <summary>
/// Launch-time update check: asks GitHub for the latest release tag and reports a
/// newer version, if any. Every failure mode (offline, rate-limited, malformed
/// response, foreign tag format) collapses to <c>null</c> so the check can never
/// disrupt startup.
/// </summary>
public static class UpdateService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>
    /// Return the latest release's normalized version string (e.g. "0.3.0") when it is
    /// newer than <paramref name="current"/>, else <c>null</c>.
    /// </summary>
    public static async Task<string?> CheckAsync(Version current)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Brand.LatestReleaseApiUrl);
            // GitHub's API rejects requests without a User-Agent; identify ourselves.
            request.Headers.UserAgent.ParseAdd($"{Brand.AppName}-update-check");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var response = await Http.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("tag_name", out var tagEl)) return null;

            var tag = tagEl.GetString();
            return UpdateCheck.IsNewer(current, tag) ? UpdateCheck.ParseTag(tag)!.ToString() : null;
        }
        catch { return null; }
    }
}
