using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class NativeQamRefreshRateWriteTests
{
    private static PerformanceServiceNativeQamAdapter Adapter(Func<int, bool>? applyRefresh)
    {
        PerformanceService service = new(
            new SimulatedRtssAdapter(),
            (_, _) => Task.CompletedTask,
            PerformancePolicy.Empty);
        return new PerformanceServiceNativeQamAdapter(service) { ApplyRefreshRate = applyRefresh };
    }

    [Fact]
    public async Task ARefreshRateChangeReachesTheDisplay()
    {
        List<int> applied = [];
        PerformanceServiceNativeQamAdapter adapter = Adapter(hz =>
        {
            applied.Add(hz);
            return true;
        });

        NativeQamCommandResult result = await adapter.ApplyPerfChangeAsync(
            new NativeQamPerfChange(NativeQamPerfSetting.RefreshRateHz, 60),
            "test",
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(60, Assert.Single(applied));
    }

    [Fact]
    public async Task ADisplayThatRefusesTheRateIsReportedAsAFailure()
    {
        PerformanceServiceNativeQamAdapter adapter = Adapter(_ => false);

        NativeQamCommandResult result = await adapter.ApplyPerfChangeAsync(
            new NativeQamPerfChange(NativeQamPerfSetting.RefreshRateHz, 48),
            "test",
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("48", result.Error);
    }

    [Fact]
    public async Task WithNoApplierTheChangeIsRefusedByNameRatherThanDropped()
    {
        // Under the pairing strategies the frame cap owns the refresh rate, so the session supplies
        // no applier and the write must say so rather than appear to succeed.
        PerformanceServiceNativeQamAdapter adapter = Adapter(null);

        NativeQamCommandResult result = await adapter.ApplyPerfChangeAsync(
            new NativeQamPerfChange(NativeQamPerfSetting.RefreshRateHz, 60),
            "test",
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("RefreshRateHz", result.Error);
    }

    [Fact]
    public async Task AnUnbackedSettingIsStillRefusedByName()
    {
        PerformanceServiceNativeQamAdapter adapter = Adapter(_ => true);

        NativeQamCommandResult result = await adapter.ApplyPerfChangeAsync(
            new NativeQamPerfChange(NativeQamPerfSetting.VariableRefreshRate, 1),
            "test",
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("VariableRefreshRate", result.Error);
    }
}
