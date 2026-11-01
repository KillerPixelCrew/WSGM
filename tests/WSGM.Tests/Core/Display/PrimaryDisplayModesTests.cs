using WindowsDeviceControl;
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

    [Fact]
    public void TheRefreshRatesAreTheEnumeratedRatesAtTheCurrentModeWithNoTest()
    {
        var current = new PrimaryDisplayMode(1920, 1200, 120, 32);
        PrimaryDisplayMode[] modes =
        [
            new(1920, 1200, 48, 32), new(1920, 1200, 100, 32), new(1920, 1200, 60, 32), new(1920, 1200, 75, 32),
            new(1920, 1200, 60, 32), new(1280, 800, 90, 32), new(1920, 1200, 144, 16), new(1920, 1200, 1, 32)
        ];

        Assert.Equal([48, 60, 75, 100, 120], PrimaryDisplayModes.RefreshRatesOf(current, modes));
    }

    [Fact]
    public void TheResolutionsAreTheEnumeratedOnesAtTheCurrentRateAndKeepTheCurrentOne()
    {
        var current = new PrimaryDisplayMode(800, 480, 120, 32);
        PrimaryDisplayMode[] modes =
        [
            new(1920, 1200, 120, 32), new(1280, 800, 120, 32), new(1280, 800, 60, 32), new(640, 480, 120, 32)
        ];

        Assert.Equal([
                new DisplayResolution(800, 480), new DisplayResolution(1280, 800),
                new DisplayResolution(1920, 1200)
            ],
            PrimaryDisplayModes.ResolutionsOf(current, modes));
    }
}
