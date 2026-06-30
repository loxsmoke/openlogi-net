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
}
