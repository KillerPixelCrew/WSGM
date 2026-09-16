using WSGM.Core;
using WSGM.Shell;

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
        Assert.Throws<ArgumentNullException>(() =>
            new SessionModes(new AppConfig(), null, null!));
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
}
