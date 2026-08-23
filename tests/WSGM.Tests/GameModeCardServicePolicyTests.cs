using WSGM.Shell;

namespace WSGM.Tests;

public sealed class GameModeCardServicePolicyTests
{
    [Fact]
    public void Decide_InitialGameModeBootWithCefEnabled_StartsBothCardServices()
    {
        var state = GameModeCardServicePolicy.Decide(
            gameModeActive: true, overlayTestOnly: false, cefMasterEnabled: true);

        Assert.True(state.WatchAppManifests);
        Assert.True(state.ReconcileSteamLibraries);
    }

    [Fact]
    public void Decide_GameModeWithCefDisabled_OnlyWatchesAppManifests()
    {
        var state = GameModeCardServicePolicy.Decide(
            gameModeActive: true, overlayTestOnly: false, cefMasterEnabled: false);

        Assert.True(state.WatchAppManifests);
        Assert.False(state.ReconcileSteamLibraries);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Decide_NonLiveGameMode_DoesNotStartCardServices(
        bool gameModeActive, bool overlayTestOnly)
    {
        var state = GameModeCardServicePolicy.Decide(
            gameModeActive, overlayTestOnly, cefMasterEnabled: true);

        Assert.False(state.WatchAppManifests);
        Assert.False(state.ReconcileSteamLibraries);
    }
}
