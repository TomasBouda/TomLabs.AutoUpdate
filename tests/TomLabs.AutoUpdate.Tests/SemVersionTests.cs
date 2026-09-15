using TomLabs.AutoUpdate;
using Xunit;

namespace TomLabs.AutoUpdate.Tests;

public class SemVersionTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3, null, null)]
    [InlineData("v0.5.0", 0, 5, 0, null, null)]
    [InlineData("0.5.0-nightly.78a63f7+78a63f718b8f", 0, 5, 0, "nightly.78a63f7", "78a63f718b8f")]
    [InlineData("1.0", 1, 0, 0, null, null)]
    [InlineData("2.1.0.4", 2, 1, 0, null, null)]
    public void Parses(string text, int major, int minor, int patch, string? prerelease, string? build)
    {
        var v = SemVersion.Parse(text);
        Assert.Equal(major, v.Major);
        Assert.Equal(minor, v.Minor);
        Assert.Equal(patch, v.Patch);
        Assert.Equal(prerelease, v.Prerelease);
        Assert.Equal(build, v.BuildMetadata);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1.x.0")]
    public void RejectsGarbage(string text) => Assert.False(SemVersion.TryParse(text, out _));

    [Theory]
    [InlineData("0.5.0", "0.6.0")]
    [InlineData("0.5.0-nightly.abc", "0.5.0")]
    [InlineData("0.5.0", "0.5.1-nightly.abc")]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.2")]
    [InlineData("1.0.0-alpha", "1.0.0-beta")]
    public void Orders(string lower, string higher)
    {
        Assert.True(SemVersion.Parse(lower) < SemVersion.Parse(higher));
        Assert.True(SemVersion.Parse(higher) > SemVersion.Parse(lower));
    }

    [Fact]
    public void BuildMetadataDoesNotAffectEquality()
        => Assert.Equal(SemVersion.Parse("1.2.3+aaa"), SemVersion.Parse("1.2.3+bbb"));

    [Fact]
    public void ToStringDropsBuildMetadata()
        => Assert.Equal("0.5.0-nightly.abc", SemVersion.Parse("0.5.0-nightly.abc+full").ToString());
}
