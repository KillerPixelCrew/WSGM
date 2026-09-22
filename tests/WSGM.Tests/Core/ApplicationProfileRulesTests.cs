using WSGM.Core;
using WSGM.Shell;
using static WSGM.Tests.Builders.PerformanceBuilders;

namespace WSGM.Tests.Core;

public sealed class ApplicationProfileRulesTests
{
    [Fact]
    public async Task NamedProfileMatchesMultipleExecutablesAndTheSwitchKeepsItsValues()
    {
        var profiles = Profiles(Config(60, 2));
        await using var service = Service(profiles);
        var id = await profiles.SaveGameAsync(null, "My game", ["Game.exe", "Launcher.exe"], true);

        await service.RunAsync(profiles, new PerformanceApplicationTarget("steam:42", 42, "GAME.EXE"));
        await profiles.SetAsync(ProfileField.FrameLimit, 30);
        await service.ApplyProfilesAsync(profiles.Current, true);
        Assert.Equal(30, service.Current.Desired.FrameLimit);

        Assert.True(await profiles.SetGameEnabledAsync(false));
        await service.ApplyProfilesAsync(profiles.Current, true);
        Assert.False(Assert.Single(profiles.Current.Config.Games).Enabled);
        Assert.Equal(30, profiles.Current.Config.Games[0].Values.FrameLimit);
        Assert.Equal(60, service.Current.Desired.FrameLimit);

        Assert.True(await profiles.SetGameEnabledAsync(true));
        await service.ApplyProfilesAsync(profiles.Current, true);
        Assert.Equal(30, service.Current.Desired.FrameLimit);

        await service.RunAsync(profiles,
            new PerformanceApplicationTarget("process:launcher.exe", null, "launcher.exe"));
        Assert.Equal(30, service.Current.Desired.FrameLimit);

        Assert.True(await profiles.DeleteGameAsync(id));
        await service.ApplyProfilesAsync(profiles.Current, true);
        Assert.Empty(profiles.Current.Config.Games);
        Assert.Equal(60, service.Current.Desired.FrameLimit);
    }

    [Fact]
    public async Task FailedPersistenceLeavesProfileAndScopeUnchanged()
    {
        var profiles = new ProfileService(new ProfileConfig(),
            (_, _) => throw new IOException("Disk unavailable"));
        profiles.SetRunningApplication(new PerformanceApplicationTarget("steam:42", 42, "game.exe"));

        await Assert.ThrowsAsync<IOException>(() => profiles.SetGameEnabledAsync(true));

        Assert.Empty(profiles.Current.Config.Games);
        Assert.False(profiles.Current.EditsGame);
    }

    [Fact]
    public async Task TargetChangeRejectsStaleHeaderIntent()
    {
        var profiles = Profiles();
        profiles.SetRunningApplication(new PerformanceApplicationTarget("steam:2", 2, "other.exe"));

        Assert.False(await profiles.SetGameEnabledAsync(true, "steam:1"));
        Assert.Empty(profiles.Current.Config.Games);
    }

    [Fact]
    public async Task ConflictingExecutableIsRejectedEvenWhenOtherProfileIsDisabled()
    {
        var profiles = Profiles();
        await profiles.SaveGameAsync(null, "First", ["game.exe"], false);

        await Assert.ThrowsAsync<ArgumentException>(() => profiles.SaveGameAsync(null, "Second", ["GAME.EXE"], true));

        Assert.Single(profiles.Current.Config.Games);
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
    public void ExplicitRulesReplaceIdentityBindingAndAmbiguityFallsBackToGlobal()
    {
        var profile = Game("steam:42");
        ProfileConfig config = new() { Games = [profile] };
        Assert.Same(profile, ProfileResolver.Match(config, "steam:42", "game.exe"));

        profile.ProcessNames = ["different.exe"];
        Assert.Null(ProfileResolver.Match(config, "steam:42", "game.exe"));

        profile.ProcessNames = ["game.exe"];
        config.Games.Add(Game("profile:other", "game.exe"));
        Assert.Null(ProfileResolver.Match(config, "steam:42", "game.exe"));
    }

    [Fact]
    public void ADisabledEmptyNamedProfileSurvivesNormalizationAndRoundTrips()
    {
        var config = ConfigStore.Normalize(new AppConfig
        {
            Profiles = new ProfileConfig
            {
                Games = [new GameProfile { Id = "profile:empty", Name = "Empty", ProcessNames = ["game.exe"] }]
            }
        });
        var restored = ConfigStore.Normalize(ConfigStore.CloneJson(config, ConfigJsonContext.Default.AppConfig));

        var match = ProfileResolver.Match(restored.Profiles, "steam:42", "GAME.EXE");
        Assert.Equal("Empty", match!.Name);
        Assert.False(match.Enabled);
    }
}
