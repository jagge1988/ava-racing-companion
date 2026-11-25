using AvaRacingCompanion.Core.Versioning;
using Xunit;

namespace AvaRacingCompanion.Core.Tests;

public sealed class AppInfoTests
{
    [Theory]
    [InlineData("1.0.0", "https://github.com/jagge1988/ava-racing-companion/releases/tag/v1.0.0")]
    [InlineData("v2.3.4", "https://github.com/jagge1988/ava-racing-companion/releases/tag/v2.3.4")]
    [InlineData(" V0.2.1 ", "https://github.com/jagge1988/ava-racing-companion/releases/tag/v0.2.1")]
    public void GetReleaseTagUrl_NormalizesVersionPrefix(string input, string expected)
    {
        var actual = AppInfo.GetReleaseTagUrl(input);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void GetCurrentVersion_ReturnsNonEmptyVersionString()
    {
        var version = AppInfo.GetCurrentVersion(typeof(AppInfo).Assembly);

        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.DoesNotContain("+", version, StringComparison.Ordinal);
    }
}
