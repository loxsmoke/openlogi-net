using OpenLogi.Core;

namespace OpenLogi.Tests;

public class UpdateCheckTests
{
    [Theory]
    [InlineData("v0.3.0", "0.3.0")]
    [InlineData("0.3.0", "0.3.0")]
    [InlineData("V1.2", "1.2.0")]      // 2-part tag normalizes to 3 parts
    [InlineData("v0.3.0-beta", "0.3.0")] // pre-release suffix dropped
    [InlineData("v1.4.2+build7", "1.4.2")] // build metadata dropped
    public void ParseTag_normalizes(string tag, string expected) =>
        Assert.Equal(expected, UpdateCheck.ParseTag(tag)!.ToString());

    [Theory]
    [InlineData("not-a-version")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("v")]
    public void ParseTag_rejects_non_versions(string? tag) =>
        Assert.Null(UpdateCheck.ParseTag(tag));

    [Fact]
    public void IsNewer_true_when_tag_is_greater() =>
        Assert.True(UpdateCheck.IsNewer(new Version(0, 2, 0), "v0.3.0"));

    [Fact]
    public void IsNewer_false_when_equal() =>
        Assert.False(UpdateCheck.IsNewer(new Version(0, 3, 0), "v0.3.0"));

    [Fact]
    public void IsNewer_false_when_tag_is_older() =>
        Assert.False(UpdateCheck.IsNewer(new Version(1, 0, 0), "v0.9.9"));

    [Fact]
    public void IsNewer_ignores_assembly_revision() =>
        Assert.False(UpdateCheck.IsNewer(new Version(0, 3, 0, 5), "v0.3.0"));

    [Fact]
    public void IsNewer_false_for_garbage_or_null_tag()
    {
        Assert.False(UpdateCheck.IsNewer(new Version(0, 1, 0), "garbage"));
        Assert.False(UpdateCheck.IsNewer(new Version(0, 1, 0), null));
    }

    // The soak gate: a release is offered only once it has been public for a day.
    private static readonly DateTimeOffset Now = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void IsEligible_false_just_under_the_soak_period() =>
        Assert.False(UpdateCheck.IsEligible(Now - TimeSpan.FromHours(23.99), Now));

    [Fact]
    public void IsEligible_true_at_exactly_the_soak_period() =>
        Assert.True(UpdateCheck.IsEligible(Now - UpdateCheck.SoakPeriod, Now));

    [Fact]
    public void IsEligible_true_well_past_the_soak_period() =>
        Assert.True(UpdateCheck.IsEligible(Now - TimeSpan.FromDays(30), Now));

    [Fact]
    public void IsEligible_false_for_a_future_publish_time() =>
        Assert.False(UpdateCheck.IsEligible(Now + TimeSpan.FromHours(1), Now));

    [Fact]
    public void IsEligible_compares_across_offsets()
    {
        // Same instant, different offset — the gate must work on absolute time.
        var published = new DateTimeOffset(2026, 7, 19, 8, 0, 0, TimeSpan.FromHours(-4));
        Assert.True(UpdateCheck.IsEligible(published, Now));
    }

    [Fact]
    public void SetupAssetName_matches_the_inno_output_name() =>
        Assert.Equal("OpenLogi.net-0.15.0-setup.exe", UpdateCheck.SetupAssetName("0.15.0"));

    private static ReleaseInfo Release(string version, TimeSpan age) =>
        new(version, Now - age, $"https://github.com/x/y/{version}/setup.exe", 100);

    [Fact]
    public void Decide_shows_a_release_that_has_soaked() =>
        Assert.Equal(UpdateCheck.BannerState.Shown,
            UpdateCheck.Decide(Release("0.66.0", TimeSpan.FromDays(2)), null, Now));

    [Fact]
    public void Decide_hides_a_release_still_inside_its_soak_period() =>
        Assert.Equal(UpdateCheck.BannerState.Hidden,
            UpdateCheck.Decide(Release("0.66.0", TimeSpan.FromHours(3)), null, Now));

    [Fact]
    public void Decide_hides_a_dismissed_release_even_once_it_has_soaked() =>
        Assert.Equal(UpdateCheck.BannerState.Hidden,
            UpdateCheck.Decide(Release("0.66.0", TimeSpan.FromDays(2)), "0.66.0", Now));

    [Fact]
    public void Decide_leaves_the_banner_alone_when_the_check_returns_nothing() =>
        Assert.Equal(UpdateCheck.BannerState.Unchanged, UpdateCheck.Decide(null, null, Now));

    // The succession case: 0.66.0 soaked and is on screen, then 0.66.1 hotfixes it three
    // hours later. The check now sees only 0.66.1 (that is what /releases/latest returns),
    // which is too fresh — so the banner must retract rather than keep offering 0.66.0.
    [Fact]
    public void Decide_retracts_a_shown_release_when_a_fresh_hotfix_supersedes_it()
    {
        var soaked = Release("0.66.0", TimeSpan.FromDays(2));
        Assert.Equal(UpdateCheck.BannerState.Shown, UpdateCheck.Decide(soaked, null, Now));

        var hotfix = Release("0.66.1", TimeSpan.FromHours(3));
        Assert.Equal(UpdateCheck.BannerState.Hidden, UpdateCheck.Decide(hotfix, null, Now));
    }

    [Fact]
    public void Decide_shows_the_hotfix_once_it_has_soaked_in_its_own_right()
    {
        var hotfix = Release("0.66.1", TimeSpan.FromHours(3));
        Assert.Equal(UpdateCheck.BannerState.Hidden, UpdateCheck.Decide(hotfix, null, Now));

        // 22 hours later the same release has aged past the gate.
        Assert.Equal(UpdateCheck.BannerState.Shown,
            UpdateCheck.Decide(hotfix, null, Now + TimeSpan.FromHours(22)));
    }

    [Fact]
    public void Decide_offers_a_hotfix_that_supersedes_a_dismissed_release()
    {
        // Dismissing 0.66.0 must not suppress 0.66.1 — dismissal is per-version.
        var hotfix = Release("0.66.1", TimeSpan.FromDays(2));
        Assert.Equal(UpdateCheck.BannerState.Shown, UpdateCheck.Decide(hotfix, "0.66.0", Now));
    }
}
