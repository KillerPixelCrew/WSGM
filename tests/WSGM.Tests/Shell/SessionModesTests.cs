using WSGM.Core;
using WSGM.Shell;
using WSGM.Tests.Fakes;

namespace WSGM.Tests.Shell;

public sealed class SessionModesTests
{
    [Fact]
    public void PreviewTransitionRequests_AreInert()
    {
        var modes = new SessionModes(new AppConfig(), null);
        var desktopStarting = 0;
        var gameModeEntered = 0;
        var warnings = 0;
        modes.DesktopModeStarting += () => desktopStarting++;
        modes.GameModeEntered += () => gameModeEntered++;
        modes.SteamStartFailed += _ => warnings++;

        modes.EnterDesktopMode();
        modes.EnterGameMode();

        Assert.False(modes.TransitionInProgress);
        Assert.Equal(0, desktopStarting);
        Assert.Equal(0, gameModeEntered);
        Assert.Equal(0, warnings);
    }

    [Fact]
    public void LiveConstructor_RequiresExplorerDesktopHost()
    {
        using var config = new TemporaryConfigStore();
        Assert.Throws<ArgumentNullException>(() =>
            new SessionModes(new AppConfig(), null, null!, config.Store, new SteamInputShim(), null!));
    }

    [Fact]
    public async Task ShutdownRequest_PreventsNewTransitions()
    {
        var modes = new SessionModes(new AppConfig(), null);

        modes.RequestShutdown();
        var accepted = modes.TryBeginTransition("test transition");
        await modes.WaitForTransitionAsync();

        Assert.False(accepted);
        Assert.False(modes.TransitionInProgress);
    }

    [Fact]
    public async Task WaitForTransitionAsync_CompletesOnlyAfterActiveTransitionEnds()
    {
        var modes = new SessionModes(new AppConfig(), null);
        modes.BeginTransition();

        var waiting = modes.WaitForTransitionAsync();

        Assert.False(waiting.IsCompleted);
        modes.EndTransition();
        await waiting.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void ShutdownRefusesAnEntryCommitThatWasAlreadyPosted()
    {
        var modes = new SessionModes(new AppConfig(), null);
        var entered = 0;
        modes.GameModeEntered += () => entered++;
        modes.RequestShutdown();

        Assert.ThrowsAny<OperationCanceledException>(modes.CommitGameMode);
        Assert.Equal(0, entered);
    }

    [Fact]
    public void DesktopSteamLaunchUsesTheCurrentConfigurationFlags()
    {
        var initial = new AppConfig
        {
            SteamInputManagementEnabled = true,
            SteamLaunchUnelevated = false
        };
        initial.Cef.Enabled = false;
        List<(bool ManageInput, bool Unelevated, bool Cef)> launches = [];
        var modes = new SessionModes(initial, null, static () => false, static () => true,
            (manageInput, unelevated, cef) =>
            {
                launches.Add((manageInput, unelevated, cef));
                return true;
            });

        modes.EnsureSteamDesktop();
        var current = new AppConfig
        {
            SteamInputManagementEnabled = false,
            SteamLaunchUnelevated = true
        };
        current.Cef.Enabled = true;
        modes.ApplyConfig(current);
        modes.EnsureSteamDesktop();

        Assert.Equal([(true, false, false), (false, true, true)], launches);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void DesktopSteamLaunchIsSkippedWhenRunningOrNotInstalled(bool running, bool installed)
    {
        var launches = 0;
        var modes = new SessionModes(new AppConfig(), null, () => running, () => installed,
            (_, _, _) =>
            {
                launches++;
                return true;
            });

        modes.EnsureSteamDesktop();

        Assert.Equal(0, launches);
    }

    [Fact]
    public void DesktopSteamLaunchAfterShutdownSkipsProbesAndLaunch()
    {
        var modes = new SessionModes(new AppConfig(), null,
            () => throw new InvalidOperationException("Shutdown must refuse the running probe."),
            () => throw new InvalidOperationException("Shutdown must refuse the installation probe."),
            (_, _, _) => throw new InvalidOperationException("Shutdown must refuse the launch."));
        modes.RequestShutdown();

        modes.EnsureSteamDesktop();
    }
}
