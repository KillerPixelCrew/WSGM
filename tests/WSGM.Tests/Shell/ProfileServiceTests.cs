using WSGM.Core;
using WSGM.Shell;
using static WSGM.Tests.Builders.PerformanceBuilders;

namespace WSGM.Tests.Shell;

public sealed class ProfileServiceTests
{
    private static readonly PerformanceApplicationTarget Game = new("steam:42", 42, "game.exe");

    [Fact]
    public async Task AnEditMadeWhileAGameProfileIsOnLandsInTheGameOnly()
    {
        var profiles = Profiles(Config(60));
        profiles.SetRunningApplication(Game);
        await profiles.SetGameEnabledAsync(true);

        var snapshot = await profiles.SetAsync(ProfileField.FrameLimit, 40);

        Assert.Equal(60, snapshot.Config.Global.FrameLimit);
        Assert.Equal(40, snapshot.Game!.Values.FrameLimit);
        Assert.Equal(ProfileSource.Game, snapshot.Layers.Value(values => values.FrameLimit).Source);
    }

    [Fact]
    public async Task AnEditMadeWithoutAGameProfileLandsInGlobal()
    {
        var profiles = Profiles();
        profiles.SetRunningApplication(Game);

        var snapshot = await profiles.SetAsync(ProfileField.FrameLimit, 40);

        Assert.Equal(40, snapshot.Config.Global.FrameLimit);
        Assert.Empty(snapshot.Config.Games);
    }

    [Fact]
    public async Task AGlobalChangeReachesAGameThatDoesNotOverrideIt()
    {
        var profiles = Profiles(Config(60));
        profiles.SetRunningApplication(Game);
        await profiles.SetGameEnabledAsync(true);

        var snapshot = await profiles.SetAsync(values => values.FrameLimit = 45, "test", ProfileLayer.Global);

        Assert.Equal(new Resolved<int?>(45, ProfileSource.Global), snapshot.Layers.Value(values => values.FrameLimit));
    }

    [Fact]
    public async Task UseGlobalRemovesOnlyTheGameOverride()
    {
        var profiles = Profiles(Config(60));
        profiles.SetRunningApplication(Game);
        await profiles.SetGameEnabledAsync(true);
        await profiles.SetAsync(ProfileField.FrameLimit, 40);

        Assert.True(await profiles.ClearGameOverrideAsync(new ProfileSettingKey(ProfileField.FrameLimit), null));

        var snapshot = profiles.Current;
        Assert.Equal(new Resolved<int?>(60, ProfileSource.Global), snapshot.Layers.Value(values => values.FrameLimit));
        Assert.True(snapshot.EditsGame);
        Assert.False(await profiles.ClearGameOverrideAsync(new ProfileSettingKey(ProfileField.FrameLimit), null));
    }

    [Fact]
    public async Task UseGlobalNeverClearsGlobal()
    {
        var profiles = Profiles(Config(60));
        profiles.SetRunningApplication(Game);

        Assert.False(await profiles.ClearGameOverrideAsync(new ProfileSettingKey(ProfileField.FrameLimit), null));
        Assert.Equal(60, profiles.Current.Config.Global.FrameLimit);
    }

    [Fact]
    public async Task ResetClearsTheGameWhenItsProfileIsOnAndOnlyThePerformanceTabOtherwise()
    {
        var profiles = Profiles(Config(60));
        await profiles.SetAsync(values => values.ControllerTarget = ManagedControllerTarget.Xbox360, "test");
        profiles.SetRunningApplication(Game);
        await profiles.SetGameEnabledAsync(true);
        await profiles.SetAsync(ProfileField.FrameLimit, 40);

        Assert.True(await profiles.ResetAsync());
        Assert.Equal(0, profiles.Current.Game!.Values.Count());
        Assert.Equal(60, profiles.Current.Config.Global.FrameLimit);

        await profiles.SetGameEnabledAsync(false);
        Assert.True(await profiles.ResetAsync());
        Assert.Null(profiles.Current.Config.Global.FrameLimit);
        Assert.Equal(ManagedControllerTarget.Xbox360, profiles.Current.Config.Global.ControllerTarget);
    }

    [Fact]
    public async Task AWriteForAGameProfileAnotherProcessDeletedIsRefusedRatherThanWidenedToGlobal()
    {
        ProfileConfig store = new();
        var deleteFirst = false;
        ProfileService profiles = new(store, (edit, _) =>
        {
            if (deleteFirst)
            {
                // Settings removed the profile between this process's last load and this save.
                store.Games.Clear();
            }

            edit(store);
            return Task.FromResult(store.Copy());
        });
        profiles.SetRunningApplication(Game);
        await profiles.SetGameEnabledAsync(true);
        deleteFirst = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => profiles.SetAsync(ProfileField.FrameLimit, 40));

        Assert.Null(store.Global.FrameLimit);
    }

    [Fact]
    public void RunningTheSameApplicationTwiceRaisesOneChange()
    {
        var profiles = Profiles();
        var changes = 0;
        profiles.Changed += (_, _) => changes++;

        profiles.SetRunningApplication(Game);
        profiles.SetRunningApplication(Game with { });

        Assert.Equal(1, changes);
    }

    [Fact]
    public void AReloadThatChangesNothingRaisesNothing()
    {
        var profiles = Profiles(Config(60));
        var changes = 0;
        profiles.Changed += (_, _) => changes++;

        profiles.ApplyConfig(Config(60));
        profiles.ApplyConfig(Config(45));

        Assert.Equal(1, changes);
        Assert.Equal(45, profiles.Current.Config.Global.FrameLimit);
    }

    [Fact]
    public void TheSnapshotIsDetachedFromTheStoreItWasBuiltFrom()
    {
        var config = Config(60);
        var profiles = Profiles(config);

        config.Global.FrameLimit = 1;

        Assert.Equal(60, profiles.Current.Config.Global.FrameLimit);
    }

    [Fact]
    public async Task TurningTheSwitchOnIsAValueChangeNotAnApplicationChange()
    {
        var profiles = Profiles();
        profiles.SetRunningApplication(Game);
        List<ProfileChangeKind> kinds = [];
        profiles.Changed += (_, kind) => kinds.Add(kind);

        await profiles.SetGameEnabledAsync(true);

        Assert.Equal([ProfileChangeKind.Values], kinds);
    }
}
