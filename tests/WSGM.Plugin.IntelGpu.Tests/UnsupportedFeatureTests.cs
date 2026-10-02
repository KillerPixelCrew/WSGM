using WSGM.Plugin.IntelGpu.Igcl;
using Xunit;

namespace WSGM.Plugin.IntelGpu.Tests;

public sealed class UnsupportedFeatureTests
{
    [Theory]
    [InlineData(IgclResult.UnsupportedFeature)]
    [InlineData(IgclResult.NotImplemented)]
    [InlineData(IgclResult.PlatformNotSupported)]
    [InlineData(IgclResult.SetFbcNotSupported)]
    public void ExplicitUnsupportedFeaturesAreHiddenAndRefused(int result)
    {
        Assert.True(IgclResult.IsUnsupportedFeature(result));
        Assert.True(IgclResult.IsRefusal(result));
    }

    [Theory]
    [InlineData(0x40000017)]
    [InlineData(IgclResult.DeviceLost)]
    [InlineData(IgclResult.InvalidArgument)]
    [InlineData(IgclResult.InsufficientPermissions)]
    [InlineData(IgclResult.UnsupportedVersion)]
    public void ReadFailuresAndValueRefusalsDoNotHideAFeature(int result)
    {
        Assert.False(IgclResult.IsUnsupportedFeature(result));
    }
}
