using WSGM.Core;

namespace WSGM.Tests;

public sealed class PerformanceApplicationProfileTests
{
    private static PerformanceService Service(PerformancePolicy policy) =>
        new(new SimulatedRtssAdapter(), (_, _) => Task.CompletedTask, policy);

    private static PerformancePolicy Global(int frameLimit) => new(
        new PerformanceValues(frameLimit, 2),
        []);

    [Fact]
    public async Task WithNoRunningApplicationTheToggleIsRefused()
    {
        // There is no application to attach a profile to, and silently writing the global layer
        // instead is the wrong reading of a per-game toggle.
        PerformanceService service = Service(Global(60));

        Assert.False(await service.SetApplicationProfileEnabledAsync(true));
    }

    [Fact]
    public async Task EnablingSeedsTheProfileFromTheValuesInForce()
    {
        // A per-game profile that started empty would drop the user to the global defaults the
        // instant they created it, which reads as the toggle having reset their settings.
        PerformanceService service = Service(Global(60));
        using IDisposable observation = service.AcquireObservation();
        await service.SetTargetAsync(new RtssApplicationTarget("steam:42", "game.exe"));

        Assert.True(await service.SetApplicationProfileEnabledAsync(true));
        Assert.Equal(60, service.Current.Desired.FrameLimit);
    }

    [Fact]
    public async Task EnablingTwiceReportsNoSecondChange()
    {
        PerformanceService service = Service(Global(60));
        using IDisposable observation = service.AcquireObservation();
        await service.SetTargetAsync(new RtssApplicationTarget("steam:42", "game.exe"));

        Assert.True(await service.SetApplicationProfileEnabledAsync(true));
        Assert.False(await service.SetApplicationProfileEnabledAsync(true));
    }

    [Fact]
    public async Task DisablingReturnsTheApplicationToTheGlobalProfile()
    {
        PerformanceService service = Service(Global(60));
        using IDisposable observation = service.AcquireObservation();
        await service.SetTargetAsync(new RtssApplicationTarget("steam:42", "game.exe"));
        await service.SetApplicationProfileEnabledAsync(true);

        Assert.True(await service.SetApplicationProfileEnabledAsync(false));
        Assert.Equal(60, service.Current.Desired.FrameLimit);
    }

    [Fact]
    public async Task DisablingWhenItWasNeverEnabledReportsNoChange()
    {
        PerformanceService service = Service(Global(60));
        using IDisposable observation = service.AcquireObservation();
        await service.SetTargetAsync(new RtssApplicationTarget("steam:42", "game.exe"));

        Assert.False(await service.SetApplicationProfileEnabledAsync(false));
    }

    [Fact]
    public async Task AnotherApplicationsProfileIsLeftAlone()
    {
        PerformanceService service = Service(new PerformancePolicy(
            new PerformanceValues(60, 2),
            [new PerformanceApplicationPolicy("steam:1", "other.exe", new PerformanceValues(30, 1))]));
        using IDisposable observation = service.AcquireObservation();
        await service.SetTargetAsync(new RtssApplicationTarget("steam:42", "game.exe"));

        await service.SetApplicationProfileEnabledAsync(true);
        await service.SetTargetAsync(new RtssApplicationTarget("steam:1", "other.exe"));

        // The pre-existing entry still supplies its own cap rather than having been replaced.
        Assert.Equal(30, service.Current.Desired.FrameLimit);
    }
}
