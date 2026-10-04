using WSGM.Core;
using WSGM.Settings;

namespace WSGM.Tests.Settings;

/// <summary>The Game Library's two settings on Settings' Steam page.</summary>
public sealed class GameLibrarySettingsTests
{
    [Fact]
    public void BothSettingsComeUpAsStoredAndSurviveASave()
    {
        var model = new SettingsViewModel(AppConfigRules.Normalize(new AppConfig
        {
            GameLibrary = new GameLibraryConfig { DefaultMode = ImportMode.ControllerOnly, ImportUnroutable = true }
        }).Value);
        Assert.Equal(1, model.GameLibraryDefaultModeIndex);
        Assert.True(model.GameLibraryImportUnroutable);

        model.GameLibraryDefaultModeIndex = 0;
        model.GameLibraryImportUnroutable = false;

        // The same ApplyTo the save path runs, through the window's own snapshot seam.
        var saved = AppConfigRules.Normalize(model.SnapshotForPreview()).Value;
        Assert.Equal(ImportMode.SteamIntegration, saved.GameLibrary.DefaultMode);
        Assert.False(saved.GameLibrary.ImportUnroutable);
    }

    [Fact]
    public void ANewInstallStartsSingleplayerTitlesOnTheSteamOverlay()
    {
        // The recorded decision: a title nothing says is multiplayer takes the Steam integration route.
        var library = AppConfigRules.Normalize(new AppConfig()).Value.GameLibrary;

        Assert.Equal(ImportMode.SteamIntegration, library.DefaultMode);
        Assert.False(library.ImportUnroutable);
    }

    [Fact]
    public void AModeNoReleaseHasIsRepairedRatherThanTrusted()
    {
        var config = AppConfigRules.Normalize(new AppConfig
        {
            GameLibrary = new GameLibraryConfig { DefaultMode = (ImportMode)42 }
        }).Value;

        Assert.Equal(ImportMode.SteamIntegration, config.GameLibrary.DefaultMode);
    }
}
