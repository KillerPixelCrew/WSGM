using WSGM.Shell;

namespace WSGM.Tests;

public sealed class DeviceOverlayAutoTdpTests
{
    [Fact]
    public void SwitchedOffReadsAsOffAndStaysToggleable()
    {
        DeviceOverlayAutoTdp row = DeviceOverlayBridge.AutoTdpView(enabled: false, status: null);

        Assert.Equal("OFF", row.TrailingText);
        Assert.Equal(DeviceOverlayStatus.None, row.Status);
        Assert.True(row.CanToggle);
    }

    [Fact]
    public void SwitchedOnBeforeTheServiceReportsAnythingSaysSoRatherThanLookingIdle()
    {
        DeviceOverlayAutoTdp row = DeviceOverlayBridge.AutoTdpView(enabled: true, status: null);

        Assert.Equal("ON", row.TrailingText);
        Assert.Contains("Starting", row.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void ControllingShowsTheLimitItSettledOnAndHowFramesAreLanding()
    {
        DeviceOverlayAutoTdp row = DeviceOverlayBridge.AutoTdpView(
            enabled: true,
            new AutoTdpStatus(
                AutoTdpState.Controlling,
                17,
                14.2,
                16.6,
                "steam:70",
                "sustained-miss"));

        Assert.Equal("17 W", row.TrailingText);
        Assert.Equal(DeviceOverlayStatus.Available, row.Status);
        Assert.Contains("14.2 ms", row.Description, StringComparison.Ordinal);
        Assert.Contains("16.6 ms", row.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void APausedSwitchWarnsRatherThanLookingHealthy()
    {
        // A user who turned AutoTDP on and then moved the slider needs to see that it stopped.
        DeviceOverlayAutoTdp row = DeviceOverlayBridge.AutoTdpView(
            enabled: true,
            new AutoTdpStatus(AutoTdpState.Paused, 22, null, null, null, "Paused by a manual change."));

        Assert.Equal(DeviceOverlayStatus.Warning, row.Status);
        Assert.Equal("22 W", row.TrailingText);
    }

    [Fact]
    public void AMissingPrerequisiteReadsAsUnsupportedNotBroken()
    {
        DeviceOverlayAutoTdp row = DeviceOverlayBridge.AutoTdpView(
            enabled: true,
            new AutoTdpStatus(
                AutoTdpState.Unavailable,
                null,
                null,
                null,
                null,
                "No primary power limit is available."));

        Assert.Equal(DeviceOverlayStatus.Unsupported, row.Status);
        Assert.Equal("ON", row.TrailingText);
    }

    [Fact]
    public void WaitingForAGameIsDistinctFromControllingOne()
    {
        DeviceOverlayAutoTdp row = DeviceOverlayBridge.AutoTdpView(
            enabled: true,
            new AutoTdpStatus(AutoTdpState.Idle, 15, null, null, null, "No application is rendering."));

        Assert.Equal(DeviceOverlayStatus.Stale, row.Status);
    }

    [Fact]
    public async Task TheSimulatedSourceTogglesWithoutTouchingAnyDevice()
    {
        using SimulatedDeviceOverlaySource source = new();

        Assert.Equal("OFF", source.Snapshot().AutoTdp!.TrailingText);
        await source.ToggleAutoTdpAsync();

        DeviceOverlayAutoTdp row = source.Snapshot().AutoTdp!;
        Assert.Equal("15 W", row.TrailingText);
        Assert.Contains("Preview only", row.Description, StringComparison.Ordinal);
    }
}
