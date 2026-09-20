using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Tests.Core;

public sealed class ApplicationProfileRulesTests
{
    [Fact]
    public async Task NamedProfileMatchesMultipleExecutablesAndTogglePersistsWithoutLosingValues()
    {
        PerformancePolicy? saved = null;
        await using var service = new PerformanceService(new SimulatedRtssAdapter(), (policy, _) =>
        {
            saved = policy;
            return Task.CompletedTask;
        }, new PerformancePolicy(new PerformanceValues(60, 2), []));
        var profile = new PerformanceApplicationPolicy("profile:test", "", new PerformanceValues(30, 1))
        {
            Name = "My game", ProcessNames = ["Game.exe", "Launcher.exe"]
        };
        Assert.True(await service.SaveProfileAsync(profile));
        await service.SetTargetAsync(new PerformanceApplicationTarget("steam:42", 42, "GAME.EXE"));
        Assert.Equal(30, service.Current.Desired.FrameLimit);
        Assert.True(await service.SetApplicationProfileEnabledAsync(false));
        Assert.False(Assert.Single(saved!.Applications).Enabled);
        Assert.Equal(30, saved.Applications[0].Values.FrameLimit);
        Assert.Equal(60, service.Current.Desired.FrameLimit);
        Assert.True(await service.SetApplicationProfileEnabledAsync(true));
        Assert.Equal(30, service.Current.Desired.FrameLimit);
        await service.SetTargetAsync(new PerformanceApplicationTarget("process:launcher.exe", null, "launcher.exe"));
        Assert.Equal(30, service.Current.Desired.FrameLimit);
        Assert.True(await service.DeleteProfileAsync(profile.ApplicationId));
        Assert.Empty(saved!.Applications);
        Assert.Equal(60, service.Current.Desired.FrameLimit);
    }

    [Fact]
    public async Task FailedPersistenceLeavesProfileAndScopeUnchanged()
    {
        await using var service = new PerformanceService(new SimulatedRtssAdapter(),
            (_, _) => throw new IOException("Disk unavailable"));
        await service.SetTargetAsync(new PerformanceApplicationTarget("steam:42", 42, "game.exe"));
        await Assert.ThrowsAsync<IOException>(() => service.SetApplicationProfileEnabledAsync(true));
        Assert.Empty(service.Profiles);
        Assert.False(service.Current.ApplicationProfileEnabled);
    }

    [Fact]
    public async Task TargetChangeRejectsStaleHeaderIntent()
    {
        await using var service = new PerformanceService(new SimulatedRtssAdapter(), (_, _) => Task.CompletedTask);
        await service.SetTargetAsync(new PerformanceApplicationTarget("steam:2", 2, "other.exe"));
        Assert.False(await service.SetApplicationProfileEnabledAsync(true, expectedApplicationId: "steam:1"));
        Assert.Empty(service.Profiles);
    }

    [Fact]
    public async Task ConflictingExecutableIsRejectedEvenWhenOtherProfileIsDisabled()
    {
        await using var service = new PerformanceService(new SimulatedRtssAdapter(), (_, _) => Task.CompletedTask);
        var first = new PerformanceApplicationPolicy("profile:1", "", PerformanceValues.Empty)
            { Name = "First", ProcessNames = ["game.exe"], Enabled = false };
        await service.SaveProfileAsync(first);
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveProfileAsync(first with
        {
            ApplicationId = "profile:2", ProcessNames = ["GAME.EXE"]
        }));
        Assert.Single(service.Profiles);
    }

    [Theory]
    [InlineData("game")]
    [InlineData("C:\\Games\\game.exe")]
    [InlineData("*.exe")]
    public void InvalidProcessRulesAreRefused(string process)
    {
        Assert.Throws<ArgumentException>(() => ApplicationProfileRules.ValidateProcesses([process]));
    }

    [Fact]
    public void ExplicitRulesReplaceLegacyIdentityBindingAndAmbiguityFallsBackToGlobal()
    {
        var profile = new PerformanceApplicationPolicy("steam:42", "game.exe", new PerformanceValues(30, 1));
        var target = new PerformanceApplicationTarget("steam:42", 42, "game.exe");
        Assert.Equal(profile,
            PerformancePolicyResolver.Find(new PerformancePolicy(PerformanceValues.Empty, [profile]), target));
        profile = profile with { ProcessNames = ["different.exe"] };
        Assert.Null(PerformancePolicyResolver.Find(new PerformancePolicy(PerformanceValues.Empty, [profile]), target));
        profile = profile with { ProcessNames = ["game.exe"] };
        Assert.Null(PerformancePolicyResolver.Find(new PerformancePolicy(PerformanceValues.Empty,
            [profile, profile with { ApplicationId = "profile:other" }]), target));
    }

    [Fact]
    public void DisabledNamedProfileWithInheritedValuesSurvivesNormalization()
    {
        var config = ConfigStore.Normalize(new AppConfig
        {
            Performance = new PerformanceConfig
            {
                Applications =
                [
                    new PerformanceApplicationConfig
                    {
                        ApplicationId = "profile:empty", Name = "Empty", ProcessNames = ["game.exe"]
                    }
                ]
            }
        });
        Assert.Single(config.Performance.Applications);
    }

    [Fact]
    public void ConfigRoundTripRetainsActivationRulesAndRemovalDeletesEntry()
    {
        var config = new AppConfig
        {
            Performance = new PerformanceConfig
            {
                Applications =
                [
                    new PerformanceApplicationConfig
                    {
                        ApplicationId = "profile:1", Name = "My game", ProcessNames = ["game.exe", "launcher.exe"],
                        UsePerGameProfile = false, TdpWatts = 18
                    }
                ]
            }
        };
        var restored = ConfigStore.Normalize(ConfigStore.CloneJson(config, ConfigJsonContext.Default.AppConfig));
        var match = ApplicationProfileRules.Match(restored.Performance, "steam:42", "GAME.EXE");
        Assert.Equal("My game", match!.Name);
        Assert.Equal(18, match.TdpWatts);
        ShellSession.MergePerformancePolicy(restored.Performance, PerformancePolicy.Empty);
        Assert.Empty(restored.Performance.Applications);
    }
}
