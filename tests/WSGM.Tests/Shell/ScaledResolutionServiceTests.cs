using LibGPUDriverInteract;
using WSGM.Shell;
using Xunit;

namespace WSGM.Tests.Shell;

public sealed class ScaledResolutionServiceTests
{
    [Fact]
    public void FourKPreviewUsesWidthAndHeightPercentagesRatherThanPixelArea()
    {
        var preview = ScaledResolutionService.PercentagePreview(new GpuScaledResolution(3840, 2160));
        Assert.Equal(11, preview.Count);
        Assert.Equal(new GpuScaledResolution(3840, 2160), preview[0]);
        Assert.Equal(new GpuScaledResolution(3648, 2052), preview[1]);
        Assert.Equal(new GpuScaledResolution(2880, 1620), preview[5]);
        Assert.Equal(new GpuScaledResolution(1920, 1080), preview[10]);
    }
}
