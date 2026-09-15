using WSGM.Core;
using WSGM.Overlay;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class AutoTdpLimiterAvailabilityTests
{
    [Theory]
    [InlineData(null, 60, (int)PerformanceReadbackQuality.Unavailable, 0)]
    [InlineData(0, 60, (int)PerformanceReadbackQuality.Verified, 0)]
    [InlineData(60, 0, (int)PerformanceReadbackQuality.Verified, 0)]
    [InlineData(60, 60, (int)PerformanceReadbackQuality.AppliedUnverified, 0)]
    [InlineData(60, 60, (int)PerformanceReadbackQuality.Verified, 60)]
    [InlineData(30, 30, (int)PerformanceReadbackQuality.Verified, 30)]
    public void OnlyAnActiveVerifiedLimiterProvidesTheControlTarget(
        int? observed, int? desired, int quality, int expectedFps)
    {
        PerformanceState state = new(new RtssProbe(RtssAvailability.Ready, null, null, 1, null, null),
            null, false, PerformancePolicyLayer.Global, PerformancePolicyLayer.Global,
            new PerformanceValues(desired, 0), new PerformanceValues(observed, 0), (PerformanceReadbackQuality)quality,
            PerformanceReadbackQuality.Verified, DateTimeOffset.UtcNow, PerformanceCommandState.Idle);
        Assert.Equal(expectedFps == 0 ? 0 : 1000d / expectedFps, AutoTdpService.TargetFrametime(state));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QamAndOverlayUseTheSameUnavailableReasonEvenWhenTheSettingWasOn(bool enabled)
    {
        AutoTdpAvailability availability = new(false, "Requires frame-rate limit.", null);
        SteamAutoTdpState qam = DeviceCoordinatorNativeQamAutoTdpService.Project(enabled, null, true, availability);
        DescriptorRow overlay = DeviceOverlayBridge.AutoTdpView(enabled, null, availability);
        Assert.False(qam.Available);
        Assert.False(qam.Enabled);
        Assert.False(overlay.CanInvoke);
        Assert.Equal(availability.Detail, qam.StatusText);
        Assert.Equal(availability.Detail, overlay.Description);
    }
}
