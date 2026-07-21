using OpenLogi.App.Services;
using OpenLogi.Core;

namespace OpenLogi.Tests.App;

/// <summary>
/// Covers the pure guards in <see cref="UpdateInstaller"/> — the URL allowlist that stands
/// in for a signature check, and the non-clobbering download path. Nothing here touches the
/// network, the registry, or the shell.
/// </summary>
public class UpdateInstallerTests
{
    [Theory]
    [InlineData("https://github.com/LoxSmoke/openlogi-net/releases/download/v0.15.0/OpenLogi.net-0.15.0-setup.exe")]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset/abc")]
    [InlineData("https://GITHUB.COM/x/y/setup.exe")]   // host match is case-insensitive
    public void IsTrustedUrl_accepts_github_over_https(string url) =>
        Assert.True(UpdateInstaller.IsTrustedUrl(url));

    [Theory]
    [InlineData("http://github.com/x/y/setup.exe")]        // plaintext
    [InlineData("https://github.com.evil.com/setup.exe")]  // suffix must land on a label boundary
    [InlineData("https://notgithub.com/setup.exe")]
    [InlineData("https://evil.com/github.com/setup.exe")]  // host is what matters, not the path
    [InlineData("file:///C:/setup.exe")]
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData(null)]
    public void IsTrustedUrl_rejects_everything_else(string? url) =>
        Assert.False(UpdateInstaller.IsTrustedUrl(url));

    [Fact]
    public void UniquePath_returns_the_plain_name_when_free()
    {
        var dir = NewTempDir();
        try
        {
            Assert.Equal(Path.Combine(dir, "setup.exe"), UpdateInstaller.UniquePath(dir, "setup.exe"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void UniquePath_suffixes_rather_than_clobbering()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "setup.exe"), "existing");
            Assert.Equal(Path.Combine(dir, "setup (2).exe"), UpdateInstaller.UniquePath(dir, "setup.exe"));

            File.WriteAllText(Path.Combine(dir, "setup (2).exe"), "existing");
            Assert.Equal(Path.Combine(dir, "setup (3).exe"), UpdateInstaller.UniquePath(dir, "setup.exe"));

            // The original must still be intact — that's the whole point.
            Assert.Equal("existing", File.ReadAllText(Path.Combine(dir, "setup.exe")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task DownloadAsync_refuses_an_untrusted_url()
    {
        var dir = NewTempDir();
        try
        {
            var release = new ReleaseInfo("0.15.0", DateTimeOffset.UtcNow, "https://evil.com/setup.exe", 10);
            Assert.Null(await UpdateInstaller.DownloadAsync(release, dir));
            Assert.Empty(Directory.GetFiles(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task DownloadAsync_refuses_a_release_with_no_installer()
    {
        var dir = NewTempDir();
        try
        {
            var release = new ReleaseInfo("0.15.0", DateTimeOffset.UtcNow, null, 0);
            Assert.Null(await UpdateInstaller.DownloadAsync(release, dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void DownloadsFolder_is_rooted() =>
        Assert.True(Path.IsPathRooted(UpdateInstaller.DownloadsFolder()));

    [Fact]
    public void UpdateStagingDir_lives_under_the_app_data_dir() =>
        Assert.StartsWith(OpenLogi.Core.Paths.DataDir(), UpdateInstaller.UpdateStagingDir());

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "openlogi-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
