using WSGM.DeviceLab.Application;

namespace WSGM.DeviceLab.Tests.Application;

public sealed class DeviceLabPathsTests
{
    [Fact]
    public void SafeName_DistinguishesIdentifiersThatSanitizeToSameText()
    {
        var slash = DeviceLabPaths.SafeName("pad/a", allowDot: false);
        var question = DeviceLabPaths.SafeName("pad?a", allowDot: false);

        Assert.StartsWith("pad-a-", slash, StringComparison.Ordinal);
        Assert.StartsWith("pad-a-", question, StringComparison.Ordinal);
        Assert.NotEqual(slash, question);
    }

    [Fact]
    public void SafeName_RetainsDotsOnlyWhenRequested()
    {
        Assert.StartsWith(
            "pad.axis-",
            DeviceLabPaths.SafeName("pad.axis", allowDot: true),
            StringComparison.Ordinal);
        Assert.StartsWith(
            "pad-axis-",
            DeviceLabPaths.SafeName("pad.axis", allowDot: false),
            StringComparison.Ordinal);
    }

    [Fact]
    public void SafeName_UsesFallbackAndStableSuffixForEmptyIdentifier()
    {
        Assert.Equal("unknown-e3b0c442", DeviceLabPaths.SafeName(string.Empty, allowDot: false));
    }
}
