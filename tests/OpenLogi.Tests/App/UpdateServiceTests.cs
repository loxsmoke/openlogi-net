using System.Net;
using System.Net.Http;
using System.Text;
using OpenLogi.App.Services;
using OpenLogi.Core;

namespace OpenLogi.Tests.App;

/// <summary>
/// Covers the GitHub-release JSON parsing in <see cref="UpdateService.CheckAsync"/> via the
/// HttpClient seam — no network. Version comparison itself is covered by UpdateCheckTests.
/// </summary>
public class UpdateServiceTests
{
    /// <summary>Returns one canned response for any request, and records the request URI.</summary>
    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static HttpClient ClientFor(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(new StubHandler(status, body));

    private const string FullRelease = """
    {
      "tag_name": "v0.15.0",
      "published_at": "2026-07-01T09:30:00Z",
      "assets": [
        { "name": "OpenLogi.net-0.15.0-win-x64-portable.zip",
          "browser_download_url": "https://github.com/LoxSmoke/openlogi-net/releases/download/v0.15.0/OpenLogi.net-0.15.0-win-x64-portable.zip",
          "size": 111 },
        { "name": "OpenLogi.net-0.15.0-setup.exe",
          "browser_download_url": "https://github.com/LoxSmoke/openlogi-net/releases/download/v0.15.0/OpenLogi.net-0.15.0-setup.exe",
          "size": 45678901 }
      ]
    }
    """;

    [Fact]
    public async Task CheckAsync_returns_version_published_at_and_setup_asset()
    {
        var release = await UpdateService.CheckAsync(new Version(0, 14, 1), ClientFor(FullRelease));

        Assert.NotNull(release);
        Assert.Equal("0.15.0", release!.Version);
        Assert.Equal(new DateTimeOffset(2026, 7, 1, 9, 30, 0, TimeSpan.Zero), release.PublishedAt);
        Assert.EndsWith("OpenLogi.net-0.15.0-setup.exe", release.SetupUrl);
        Assert.Equal(45678901, release.SetupSize);
    }

    [Fact]
    public async Task CheckAsync_null_when_current_is_already_latest() =>
        Assert.Null(await UpdateService.CheckAsync(new Version(0, 15, 0), ClientFor(FullRelease)));

    [Fact]
    public async Task CheckAsync_null_when_current_is_newer() =>
        Assert.Null(await UpdateService.CheckAsync(new Version(1, 0, 0), ClientFor(FullRelease)));

    [Fact]
    public async Task CheckAsync_setup_url_null_when_release_has_no_installer()
    {
        const string noSetup = """
        {
          "tag_name": "v0.15.0",
          "published_at": "2026-07-01T09:30:00Z",
          "assets": [
            { "name": "OpenLogi.net-0.15.0-win-x64-portable.zip",
              "browser_download_url": "https://github.com/x/y/releases/download/v0.15.0/portable.zip",
              "size": 111 }
          ]
        }
        """;
        var release = await UpdateService.CheckAsync(new Version(0, 14, 1), ClientFor(noSetup));

        Assert.NotNull(release);
        Assert.Equal("0.15.0", release!.Version);
        Assert.Null(release.SetupUrl);
    }

    [Fact]
    public async Task CheckAsync_ignores_an_asset_named_for_a_different_version()
    {
        const string mismatched = """
        {
          "tag_name": "v0.15.0",
          "published_at": "2026-07-01T09:30:00Z",
          "assets": [
            { "name": "OpenLogi.net-0.14.1-setup.exe",
              "browser_download_url": "https://github.com/x/y/releases/download/v0.14.1/old-setup.exe",
              "size": 999 }
          ]
        }
        """;
        var release = await UpdateService.CheckAsync(new Version(0, 14, 0), ClientFor(mismatched));

        Assert.NotNull(release);
        Assert.Null(release!.SetupUrl);
    }

    [Fact]
    public async Task CheckAsync_missing_assets_array_is_not_fatal()
    {
        var release = await UpdateService.CheckAsync(
            new Version(0, 14, 1),
            ClientFor("""{ "tag_name": "v0.15.0", "published_at": "2026-07-01T09:30:00Z" }"""));

        Assert.NotNull(release);
        Assert.Null(release!.SetupUrl);
        Assert.Equal(0, release.SetupSize);
    }

    [Fact]
    public async Task CheckAsync_missing_published_at_is_treated_as_now()
    {
        // Without a timestamp the release can't be shown to have soaked, so it must not
        // be back-dated into eligibility.
        var before = DateTimeOffset.UtcNow;
        var release = await UpdateService.CheckAsync(
            new Version(0, 14, 1), ClientFor("""{ "tag_name": "v0.15.0" }"""));

        Assert.NotNull(release);
        Assert.InRange(release!.PublishedAt, before, DateTimeOffset.UtcNow);
        Assert.False(UpdateCheck.IsEligible(release.PublishedAt, DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]        // rate-limited
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task CheckAsync_null_on_error_status(HttpStatusCode status) =>
        Assert.Null(await UpdateService.CheckAsync(new Version(0, 1, 0), ClientFor(FullRelease, status)));

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("""{ "tag_name": "not-a-version" }""")]
    public async Task CheckAsync_null_on_unusable_body(string body) =>
        Assert.Null(await UpdateService.CheckAsync(new Version(0, 1, 0), ClientFor(body)));
}
