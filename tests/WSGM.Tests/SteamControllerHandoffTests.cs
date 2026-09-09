using SteamUiToolkit.Surfaces;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class SteamControllerHandoffTests
{
    private static SteamSideMenuSnapshot Closed => new(default,
        [new(0, 0, SteamSideMenu.None, false)]);
    private static SteamSideMenuSnapshot Open => new(default,
        [new(0, 0, SteamSideMenu.QuickAccess, false)]);

    [Fact]
    public void TargetSelectionRequiresOneExactGameOverlayAndNeverGuessesBetweenGames()
    {
        SteamWindowSideMenu main = new(0, 0, SteamSideMenu.None, false);
        SteamWindowSideMenu game = new(42, 123, SteamSideMenu.None, false);
        Assert.Equal(main, SteamControllerHandoff.SelectReplayTarget(new(default, [main]), true));
        Assert.Null(SteamControllerHandoff.SelectReplayTarget(new(default, [main]), false));
        Assert.Equal(game, SteamControllerHandoff.SelectReplayTarget(new(default, [main, game]), true));
        Assert.Null(SteamControllerHandoff.SelectReplayTarget(
            new(default, [main, game, new(43, 456, SteamSideMenu.None, false)]), true));
        Assert.Null(SteamControllerHandoff.SelectReplayTarget(new(default, null), true));
    }

    [Fact]
    public async Task ReleaseAndReplayPrecedeObservationAndOneRestoration()
    {
        List<string> calls = [];
        int observations = 0;
        await using SteamControllerHandoff owner = new(
            _ => { calls.Add("release"); return Task.FromResult(true); },
            _ => { calls.Add("restore"); return Task.FromResult(true); },
            _ => { calls.Add("observe"); return Task.FromResult(observations++ == 0 ? Open : Closed); },
            () => true, _ => { });

        Assert.True(owner.TryStart(_ => { calls.Add("replay"); return Task.FromResult(true); }));
        Assert.False(owner.TryStart(_ => throw new InvalidOperationException("duplicate replay")));
        await owner.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(["release", "replay", "observe", "observe", "restore"], calls);
        Assert.Equal(SteamControllerOwnership.Wsgm, owner.State);
    }

    [Fact]
    public async Task UnknownStateCannotTriggerReacquisitionEvenAfterOpenTimeout()
    {
        TaskCompletionSource observed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int restores = 0;
        await using SteamControllerHandoff owner = new(
            _ => Task.FromResult(true),
            _ => { restores++; return Task.FromResult(true); },
            _ => { observed.TrySetResult(); return Task.FromResult(new SteamSideMenuSnapshot(default, null)); },
            () => true, _ => { }, openTimeout: TimeSpan.Zero);

        owner.TryStart(_ => Task.FromResult(true));
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await owner.DisposeAsync();
        Assert.Equal(0, restores);
        Assert.Equal(SteamControllerOwnership.RecoveryRequired, owner.State);
    }

    [Fact]
    public async Task ReloadAndSwitchingSurfacesRetainOwnershipUntilAllAreClosed()
    {
        Queue<SteamSideMenuSnapshot> states = new([
            Open,
            new(default, null),
            new(default, [new(0, 0, SteamSideMenu.None, false), new(42, 5, SteamSideMenu.None, true)]),
            Closed,
        ]);
        int restores = 0;
        await using SteamControllerHandoff owner = new(
            _ => Task.FromResult(true),
            _ => { Assert.Empty(states); restores++; return Task.FromResult(true); },
            _ => Task.FromResult(states.Dequeue()), () => true, _ => { }, openTimeout: TimeSpan.Zero);
        owner.TryStart(_ => Task.FromResult(true));
        await owner.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, restores);
        Assert.Equal(SteamControllerOwnership.Wsgm, owner.State);
    }

    [Fact]
    public async Task FailedOpenRecoversOnlyWithVerifiedClosure()
    {
        int restores = 0;
        await using SteamControllerHandoff owner = new(
            _ => Task.FromResult(true),
            _ => { restores++; return Task.FromResult(true); },
            _ => Task.FromResult(Closed), () => true, _ => { }, openTimeout: TimeSpan.Zero);

        owner.TryStart(_ => Task.FromResult(true));
        await owner.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, restores);
        Assert.Equal(SteamControllerOwnership.Wsgm, owner.State);
    }

    [Fact]
    public async Task ReplacedSteamProcessRestoresEvenWhenTheLivenessPollNeverSawAnExit()
    {
        int restores = 0;
        await using SteamControllerHandoff owner = new(
            _ => Task.FromResult(true),
            _ => { restores++; return Task.FromResult(true); },
            _ => throw new InvalidOperationException("The replacement CEF must not extend the old interaction"),
            () => true, _ => { }, originalSteamExited: () => true);

        owner.TryStart(_ => Task.FromResult(true));
        await owner.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, restores);
        Assert.Equal(SteamControllerOwnership.Wsgm, owner.State);
    }

    [Fact]
    public async Task SteamExitRestoresWithoutTreatingCefFailureAsClosure()
    {
        int restores = 0;
        await using SteamControllerHandoff owner = new(
            _ => Task.FromResult(true),
            _ => { restores++; return Task.FromResult(true); },
            _ => throw new InvalidOperationException("Steam is gone"), () => false, _ => { });

        owner.TryStart(_ => Task.FromResult(true));
        await owner.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, restores);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnverifiedWritesAreNeverRetried(bool releaseSucceeds)
    {
        int releases = 0;
        int restores = 0;
        await using SteamControllerHandoff owner = new(
            _ => { releases++; return Task.FromResult(releaseSucceeds); },
            _ => { restores++; return Task.FromResult(false); },
            _ => Task.FromResult(Closed), () => false, _ => { });

        owner.TryStart(_ => Task.FromResult(true));
        await owner.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(owner.TryStart(_ => Task.FromResult(true)));
        Assert.Equal(1, releases);
        Assert.Equal(releaseSucceeds ? 1 : 0, restores);
        Assert.Equal(SteamControllerOwnership.RecoveryRequired, owner.State);
    }
}
