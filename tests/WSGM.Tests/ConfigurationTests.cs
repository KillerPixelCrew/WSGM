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
            AccentColor = null!,
            Splash = null!,
        };

        var normalized = ConfigStore.Normalize(config);

        Assert.NotNull(normalized.StartupApps);
        Assert.NotNull(normalized.Hotkey);
        Assert.NotNull(normalized.GamepadChord);
        Assert.NotNull(normalized.Gestures);
        Assert.NotNull(normalized.SavedDisplayScales);
        Assert.NotNull(normalized.SavedDisplayScaleEntries);
        Assert.NotNull(normalized.PreviousConsoleLockSchemeValues);
        Assert.Equal("#FFFF9D3D", normalized.AccentColor);
        Assert.NotNull(normalized.Splash);
    }

    [Fact]
    public void NormalizeRepairsExplicitNullsInsideAnExistingSplashSection()
    {
        var config = new AppConfig
        {
            Splash = new SplashConfig
            {
                Text = null!,
                TextColor = null!,
                Caption = null!,
                CaptionColor = null!,
                SpinnerColor = null!,
                BackgroundColor = null!,
                BackgroundImagePath = null!,
                LogoImagePath = null!,
                TextPlacement = null!,
                SpinnerPlacement = null!,
                LogoPlacement = null!,
            },
        };

        var splash = ConfigStore.Normalize(config).Splash;

        Assert.Equal("Please wait", splash.Text);
        Assert.Equal("#FFFFFF", splash.TextColor);
        Assert.Equal("", splash.Caption);
        Assert.Equal("#666666", splash.CaptionColor);
        Assert.Equal("#FFFFFF", splash.SpinnerColor);
        Assert.Equal("#000000", splash.BackgroundColor);
        Assert.Equal("", splash.BackgroundImagePath);
        Assert.Equal("", splash.LogoImagePath);
        Assert.NotNull(splash.TextPlacement);
        Assert.Equal(SplashPlacementMode.Anchor, splash.TextPlacement.Mode);
        Assert.NotNull(splash.SpinnerPlacement);
        Assert.Equal(SplashPlacementMode.WithText, splash.SpinnerPlacement.Mode);
        Assert.NotNull(splash.LogoPlacement);
        Assert.Equal(SplashPlacementMode.WithText, splash.LogoPlacement.Mode);
    }

    [Fact]
    public void SplashDefaultsReproduceTheClassicBootSplashLook()
    {
        var splash = new SplashConfig();

        Assert.Equal("#000000", splash.BackgroundColor);
        Assert.False(splash.VignetteEnabled);
        Assert.Equal("", splash.BackgroundImagePath);
        Assert.True(splash.TextEnabled);
        Assert.Equal("Please wait", splash.Text);
        Assert.Equal("#FFFFFF", splash.TextColor);
        Assert.Equal(26, splash.TitleFontSize);
        Assert.Equal("", splash.Caption);
        Assert.Equal("#666666", splash.CaptionColor);
        Assert.Equal(12, splash.CaptionFontSize);
        Assert.Equal(SplashSpinnerStyle.Ring, splash.SpinnerStyle);
        Assert.Equal("#FFFFFF", splash.SpinnerColor);
        Assert.Equal(36, splash.SpinnerSize);
        Assert.Equal(SweepEdge.Bottom, splash.SweepEdge);
        Assert.Equal("", splash.LogoImagePath);
        Assert.Equal(200, splash.LogoMaxSize);
        Assert.Equal(SplashPlacementMode.Anchor, splash.TextPlacement.Mode);
        Assert.Equal(SplashPlacementAnchor.Center, splash.TextPlacement.Anchor);
        Assert.Equal(SplashPlacementMode.WithText, splash.SpinnerPlacement.Mode);
        Assert.Equal(SplashPlacementMode.WithText, splash.LogoPlacement.Mode);
    }

    [Fact]
    public void FullyCustomizedSplashConfigRoundTripsWithStringEnums()
    {
        var original = new AppConfig
        {
            Splash = new SplashConfig
            {
                Text = "WSGM",
                TextEnabled = false,
                TextColor = "#FF9D3D",
                TitleFontSize = 48,
                Caption = "STARTING STEAM",
                CaptionColor = "#AAAAAA",
                CaptionFontSize = 14,
                SpinnerStyle = SplashSpinnerStyle.SweepLine,
                SpinnerColor = "#00FF00",
                SpinnerSize = 72,
                SweepEdge = SweepEdge.Top,
                BackgroundColor = "#101010",
                VignetteEnabled = true,
                BackgroundImagePath = "C:\\Images\\bg.png",
                LogoImagePath = "C:\\Images\\logo.png",
                LogoMaxSize = 320,
                TextPlacement = new SplashElementPlacement
                {
                    Mode = SplashPlacementMode.Anchor,
                    Anchor = SplashPlacementAnchor.BottomLeft,
                    PaddingX = 32,
                    PaddingY = 160,
                },
                SpinnerPlacement = new SplashElementPlacement
                {
                    Mode = SplashPlacementMode.Absolute,
                    X = 640,
                    Y = 360,
                },
                LogoPlacement = new SplashElementPlacement
                {
                    Mode = SplashPlacementMode.Anchor,
                    Anchor = SplashPlacementAnchor.TopRight,
                },
            },
        };

        var json = JsonSerializer.Serialize(original, ConfigJsonContext.Default.AppConfig);
        var restored = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.AppConfig);

        Assert.Contains("\"SpinnerStyle\": \"SweepLine\"", json);
        Assert.NotNull(restored);
        var splash = restored.Splash;
        Assert.Equal("WSGM", splash.Text);
        Assert.False(splash.TextEnabled);
        Assert.Equal("#FF9D3D", splash.TextColor);
        Assert.Equal(48, splash.TitleFontSize);
        Assert.Equal("STARTING STEAM", splash.Caption);
        Assert.Equal("#AAAAAA", splash.CaptionColor);
        Assert.Equal(14, splash.CaptionFontSize);
        Assert.Equal(SplashSpinnerStyle.SweepLine, splash.SpinnerStyle);
        Assert.Equal("#00FF00", splash.SpinnerColor);
        Assert.Equal(72, splash.SpinnerSize);
        Assert.Equal(SweepEdge.Top, splash.SweepEdge);
        Assert.Equal("#101010", splash.BackgroundColor);
        Assert.True(splash.VignetteEnabled);
        Assert.Equal("C:\\Images\\bg.png", splash.BackgroundImagePath);
        Assert.Equal("C:\\Images\\logo.png", splash.LogoImagePath);
        Assert.Equal(320, splash.LogoMaxSize);
        Assert.Equal(SplashPlacementAnchor.BottomLeft, splash.TextPlacement.Anchor);
        Assert.Equal(32, splash.TextPlacement.PaddingX);
        Assert.Equal(160, splash.TextPlacement.PaddingY);
        Assert.Equal(SplashPlacementMode.Absolute, splash.SpinnerPlacement.Mode);
        Assert.Equal(640, splash.SpinnerPlacement.X);
        Assert.Equal(360, splash.SpinnerPlacement.Y);
        Assert.Equal(SplashPlacementMode.Anchor, splash.LogoPlacement.Mode);
        Assert.Equal(SplashPlacementAnchor.TopRight, splash.LogoPlacement.Anchor);
    }

    [Fact]
    public void AccentColorRoundTripsAndDefaultsToTheWsgmOrange()
    {
        Assert.Equal("#FFFF9D3D", new AppConfig().AccentColor);

        var original = new AppConfig { AccentColor = "#FF2266CC" };

        var json = JsonSerializer.Serialize(original, ConfigJsonContext.Default.AppConfig);
        var restored = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.AppConfig);

        Assert.NotNull(restored);
        Assert.Equal("#FF2266CC", restored.AccentColor);
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

    [Fact]
    public void GameModeBootDefaultsMatchTheInstallerIntent()
    {
        var config = new AppConfig();

        Assert.True(config.GameModeBootEnabled);
        Assert.Equal(5000, config.ExplorerLogonSettleMs);
    }

    [Fact]
    public void GameModeBootFieldsRoundTripThroughSourceGeneratedJson()
    {
        var original = new AppConfig { GameModeBootEnabled = false, ExplorerLogonSettleMs = 250 };

        var json = JsonSerializer.Serialize(original, ConfigJsonContext.Default.AppConfig);
        var restored = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.AppConfig);

        Assert.NotNull(restored);
        Assert.False(restored.GameModeBootEnabled);
        Assert.Equal(250, restored.ExplorerLogonSettleMs);
    }
}
