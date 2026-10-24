using WSGM.Core;

namespace WSGM.Tests.Core;

public sealed class PrimaryDisplayModesTests
{
    [Theory]
    [InlineData(960, 540)]
    [InlineData(1024, 576)]
    [InlineData(540, 960)]
    [InlineData(800, 600)]
    public void UsefulDriverModesAreNotExcludedByAspectRatio(int width, int height)
    {
        Assert.True(PrimaryDisplayModes.IsUsableResolution(width, height));
    }

    [Theory]
    [InlineData(640, 480)]
    [InlineData(720, 480)]
    [InlineData(800, 480)]
    public void LegacyModesRemainFiltered(int width, int height)
    {
        Assert.False(PrimaryDisplayModes.IsUsableResolution(width, height));
    }
}
