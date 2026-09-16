using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class GameModeCardServicePolicyTests
{
    [Fact]
    public void Decide_InitialGameModeBootWithCefEnabled_StartsBothCardServices()
    {
        var state = GameModeCardServicePolicy.Decide(
            true, false, true);

        Assert.True(state.WatchAppManifests);
        Assert.True(state.ReconcileSteamLibraries);
    }

    [Fact]
    public void Decide_GameModeWithCefDisabled_OnlyWatchesAppManifests()
    {
        var state = GameModeCardServicePolicy.Decide(
            true, false, false);

        Assert.True(state.WatchAppManifests);
        Assert.False(state.ReconcileSteamLibraries);
    }

    [Fact]
    public void Decide_DesktopModeWithCefEnabled_ReconcilesButDoesNotWatchManifests()
    {
        // Steam's storage pages are live in either mode, so a card pulled from the reader has to
        // leave Steam's list from the desktop too. Manifest watching serves the library tabs, which
        // are a game-mode surface, and stays with game mode.
        var state = GameModeCardServicePolicy.Decide(
            false, false, true);

        Assert.False(state.WatchAppManifests);
        Assert.True(state.ReconcileSteamLibraries);
    }

    [Fact]
    public void Decide_OverlayTest_StartsNoCardServices()
    {
        // The safe mode must never drive the live Steam client or touch a card.
        var state = GameModeCardServicePolicy.Decide(
            true, true, true);

        Assert.False(state.WatchAppManifests);
        Assert.False(state.ReconcileSteamLibraries);
    }

    [Fact]
    public void Decide_DesktopModeWithCefDisabled_StartsNothing()
    {
        var state = GameModeCardServicePolicy.Decide(
            false, false, false);

        Assert.False(state.WatchAppManifests);
        Assert.False(state.ReconcileSteamLibraries);
    }
}
