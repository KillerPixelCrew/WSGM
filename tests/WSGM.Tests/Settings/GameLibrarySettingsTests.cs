using WSGM.Core;
using WSGM.Settings;

namespace WSGM.Tests.Settings;

/// <summary>The Game Library's two settings on Settings' Steam page.</summary>
public sealed class GameLibrarySettingsTests
{
    [Fact]
    public void BothSettingsComeUpAsStoredAndSurviveASave()
    {
        var model = new SettingsViewModel(ConfigStore.Normalize(new AppConfig
        {
            GameLibrary = new GameLibraryConfig { DefaultMode = ImportMode.ControllerOnly, ImportUnroutable = true }
        }));
        Assert.Equal(1, model.GameLibraryDefaultModeIndex);
        Assert.True(model.GameLibraryImportUnroutable);

        model.GameLibraryDefaultModeIndex = 0;
        model.GameLibraryImportUnroutable = false;

        // The same ApplyTo the save path runs, through the window's own snapshot seam.
        var saved = ConfigStore.Normalize(model.SnapshotForPreview());
        Assert.Equal(ImportMode.SteamIntegration, saved.GameLibrary.DefaultMode);
        Assert.False(saved.GameLibrary.ImportUnroutable);
    }

    [Fact]
    public void ANewInstallStartsSingleplayerTitlesOnTheSteamOverlay()
    {
        // The recorded decision: a title nothing says is multiplayer takes the Steam integration route.
        var library = ConfigStore.Normalize(new AppConfig()).GameLibrary;

        Assert.Equal(ImportMode.SteamIntegration, library.DefaultMode);
        Assert.False(library.ImportUnroutable);
    }

    [Fact]
    public void AModeNoReleaseHasIsRepairedRatherThanTrusted()
    {
        var config = ConfigStore.Normalize(new AppConfig
        {
            GameLibrary = new GameLibraryConfig { DefaultMode = (ImportMode)42 }
        });

        Assert.Equal(ImportMode.SteamIntegration, config.GameLibrary.DefaultMode);
    }
}
