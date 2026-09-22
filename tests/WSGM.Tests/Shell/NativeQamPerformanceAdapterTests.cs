using System.Text.Json;
using WSGM.Core;
using WSGM.Shell;
using static WSGM.Tests.Builders.PerformanceBuilders;

namespace WSGM.Tests.Shell;

/// <summary>The Native QAM performance adapter's refresh-rate and per-application writes.</summary>
public sealed class NativeQamPerformanceAdapterTests
{
    private static PerformanceServiceNativeQamAdapter Adapter(Func<int, bool>? applyRefresh)
    {
        var service = Service();
        return new PerformanceServiceNativeQamAdapter(service) { ApplyRefreshRate = applyRefresh };
    }

    [Fact]
    public async Task ARefreshRateChangeReachesTheDisplay()
    {
        List<int> applied = [];
        var adapter = Adapter(hz =>
        {
            applied.Add(hz);
            return true;
        });

        var result = await adapter.ApplyPerfChangeAsync(
            new SteamPerformanceChange(SteamPerformanceSetting.RefreshRateHz, 60),
            "test",
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(60, Assert.Single(applied));
    }

    [Fact]
    public async Task ADisplayThatRefusesTheRateIsReportedAsAFailure()
    {
        var adapter = Adapter(_ => false);

        var result = await adapter.ApplyPerfChangeAsync(
            new SteamPerformanceChange(SteamPerformanceSetting.RefreshRateHz, 48),
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
        var adapter = Adapter(null);

        var result = await adapter.ApplyPerfChangeAsync(
            new SteamPerformanceChange(SteamPerformanceSetting.RefreshRateHz, 60),
            "test",
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("RefreshRateHz", result.Error);
    }

    [Fact]
    public async Task AnUnbackedSettingIsStillRefusedByName()
    {
        // AdvancedSettingsEnabled is the last performance setting with no WSGM backend: it is
        // Steam's own Basic/Advanced view state, which the store holds and WSGM does not drive.
        // This test has already had to move twice as settings were implemented — if it moves again,
        // check whether anything is genuinely unbacked before repointing it rather than deleting
        // the coverage, because the refusal path is what keeps a dead control from looking alive.
        var adapter = Adapter(_ => true);

        var result = await adapter.ApplyPerfChangeAsync(
            new SteamPerformanceChange(SteamPerformanceSetting.AdvancedSettingsEnabled, 1),
            "test",
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("AdvancedSettingsEnabled", result.Error);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public async Task AVrrToggleReachesTheDeviceWithTheRequestedState(int value, bool expected)
    {
        List<bool> applied = [];
        var adapter = Adapter(null);
        adapter.ApplyVariableRefreshRate = (enabled, _) =>
        {
            applied.Add(enabled);
            return Task.FromResult(true);
        };

        var result = await adapter.ApplyPerfChangeAsync(
            new SteamPerformanceChange(SteamPerformanceSetting.VariableRefreshRate, value),
            "test",
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(expected, Assert.Single(applied));
    }

    [Fact]
    public async Task ADeviceThatRefusesVrrIsNotReportedAsApplied()
    {
        // Steam's toggle is controlled, so reporting success before the device answered would show
        // it moved and then snap it back on the next publish.
        var adapter = Adapter(null);
        adapter.ApplyVariableRefreshRate = (_, _) => Task.FromResult(false);

        var result = await adapter.ApplyPerfChangeAsync(
            new SteamPerformanceChange(SteamPerformanceSetting.VariableRefreshRate, 1),
            "test",
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("variable refresh rate", result.Error);
    }

    [Fact]
    public async Task WithNoDeviceVrrIsRefusedByNameRatherThanDropped()
    {
        var adapter = Adapter(null);

        var result = await adapter.ApplyPerfChangeAsync(
            new SteamPerformanceChange(SteamPerformanceSetting.VariableRefreshRate, 1),
            "test",
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("VariableRefreshRate", result.Error);
    }

    [Fact]
    public async Task SteamHeaderKeepsTheAppIdBeforeTheExecutableIsKnown()
    {
        var profiles = Profiles(Config(60, 1));
        await using var service = Service(profiles);
        await service.RunAsync(profiles, new PerformanceApplicationTarget("steam:42", 42, null));
        PerformanceServiceNativeQamAdapter adapter = new(service) { Profiles = profiles };

        var global = adapter.PerfState;

        Assert.Equal("42", global.CurrentGameId);
        Assert.Equal("769", global.ActiveProfileGameId);
        Assert.False(global.PerApp?.IsGamePerfProfileEnabled);

        Assert.True(await profiles.SetGameEnabledAsync(true));
        await service.ApplyProfilesAsync(profiles.Current, true);
        var perApplication = adapter.PerfState;
        Assert.Equal("42", perApplication.CurrentGameId);
        Assert.Equal("42", perApplication.ActiveProfileGameId);
        Assert.True(perApplication.PerApp?.IsGamePerfProfileEnabled);
    }

    [Fact]
    public async Task DeltaForAnApplicationThatIsNoLongerCurrentIsRefused()
    {
        var profiles = Profiles(Config(60, 1));
        await using var service = Service(profiles);
        await service.RunAsync(profiles, new PerformanceApplicationTarget("steam:42", 42, "current.exe"));
        PerformanceServiceNativeQamAdapter adapter = new(service) { Profiles = profiles };
        using var payload = JsonDocument.Parse(
            """{"delta":{"gameid":41,"settings_delta":{"per_app":{"is_game_perf_profile_enabled":true}}}}""");
        Assert.True(SteamPerformanceDeltaReader.TryRead(
            payload.RootElement,
            out var delta,
            out _));

        var result = await adapter.ApplyAsync(
            delta,
            "test",
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("stale AppID 41", result.Error);
        Assert.False(service.Current.ApplicationProfileEnabled);
    }
}
