using WSGM.Core;
using WSGM.Testing;

namespace WSGM.Tests.Settings;

/// <summary>The Game Library's two settings on Settings' Steam page.</summary>
public sealed class GameLibrarySettingsTests
{
    [Fact]
    public void BothSettingsComeUpAsStoredAndSurviveASave()
    {
        var model = SettingsTestServices.Model(AppConfigRules.Normalize(new AppConfig
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
}
