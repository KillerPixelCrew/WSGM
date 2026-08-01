using System.Text.Json;
using WSGM.Core;

namespace WSGM.Tests;

public sealed class ConfigurationTests
{
    [Fact]
    public void NormalizeRepairsEveryNullableCollectionAndNestedSection()
    {
        var config = new AppConfig
        {
            StartupApps = null!,
            Hotkey = null!,
            GamepadChord = null!,
            Gestures = null!,
            SavedDisplayScales = null!,
            SavedDisplayScaleEntries = null!,
            PreviousConsoleLockSchemeValues = null!,
        };

        var normalized = ConfigStore.Normalize(config);

        Assert.NotNull(normalized.StartupApps);
        Assert.NotNull(normalized.Hotkey);
        Assert.NotNull(normalized.GamepadChord);
        Assert.NotNull(normalized.Gestures);
        Assert.NotNull(normalized.SavedDisplayScales);
        Assert.NotNull(normalized.SavedDisplayScaleEntries);
        Assert.NotNull(normalized.PreviousConsoleLockSchemeValues);
    }

    [Fact]
    public void SourceGeneratedConfigJsonRoundTripsSettingsAndSnapshots()
    {
        var original = new AppConfig
        {
            SteamAutoRelaunch = true,
            StartupDelayMs = 1234,
            GlyphStyle = GlyphStyle.Nintendo,
            PreviousShellSnapshotCaptured = true,
            PreviousShellValueExists = true,
            PreviousShellValue = "explorer.exe",
            StartupApps =
            [
                new StartupAppConfig { Path = "C:\\Tools\\companion.exe", Args = "--silent", Elevated = true },
            ],
            SavedDisplayScaleEntries =
            [
                new DisplayScaleEntry { DeviceName = "\\\\.\\DISPLAY1", Percent = 150 },
            ],
        };

        var json = JsonSerializer.Serialize(original, ConfigJsonContext.Default.AppConfig);
        var restored = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.AppConfig);

        Assert.Contains("\"GlyphStyle\": \"Nintendo\"", json);
        Assert.NotNull(restored);
        Assert.True(restored.SteamAutoRelaunch);
        Assert.Equal(1234, restored.StartupDelayMs);
        Assert.Equal(GlyphStyle.Nintendo, restored.GlyphStyle);
        Assert.Equal("explorer.exe", restored.PreviousShellValue);
        Assert.Single(restored.StartupApps);
        Assert.True(restored.StartupApps[0].Elevated);
        Assert.Equal(150, Assert.Single(restored.SavedDisplayScaleEntries).Percent);
    }
}
