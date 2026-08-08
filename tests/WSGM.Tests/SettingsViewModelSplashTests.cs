using System.Text.Json;
using WSGM.Core;
using WSGM.Settings;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class SettingsViewModelSplashTests
{
    private static string Json(SplashConfig splash) =>
        JsonSerializer.Serialize(splash, ConfigJsonContext.Default.SplashConfig);

    [Fact]
    public void LoadSplashThenBuildSplashConfigRoundTripsEveryField()
    {
        var source = new SplashConfig
        {
            Text = "Custom title",
            TextEnabled = false,
            TextColor = "#123456",
            TitleFontSize = 48,
            Caption = "custom caption",
            CaptionColor = "#654321",
            CaptionFontSize = 15,
            SpinnerStyle = SplashSpinnerStyle.LiWave,
            SpinnerColor = "#ABCDEF",
            SpinnerSize = 72,
            SweepEdge = SweepEdge.Top,
            BackgroundColor = "#0A0B0C",
            VignetteEnabled = true,
            BackgroundImagePath = @"C:\pics\bg.png",
            LogoImagePath = @"C:\pics\logo.png",
            LogoMaxSize = 160,
            TextPlacement = new SplashElementPlacement
            {
                Mode = SplashPlacementMode.Anchor,
                Anchor = SplashPlacementAnchor.BottomCenter,
                PaddingX = 10,
                PaddingY = 210,
                X = 5,
                Y = 6,
            },
            SpinnerPlacement = new SplashElementPlacement
            {
                Mode = SplashPlacementMode.Absolute,
                Anchor = SplashPlacementAnchor.TopRight,
                PaddingX = 11,
                PaddingY = 12,
                X = 640,
                Y = 480,
            },
            LogoPlacement = new SplashElementPlacement
            {
                Mode = SplashPlacementMode.WithText,
                Anchor = SplashPlacementAnchor.CenterLeft,
                PaddingX = 13,
                PaddingY = 14,
                X = 7,
                Y = 8,
            },
        };

        var viewModel = new SettingsViewModel();
        viewModel.LoadSplash(source);
        var rebuilt = viewModel.BuildSplashConfig();

        Assert.Equal(Json(source), Json(rebuilt));
    }

    [Fact]
    public void EveryPresetSurvivesTheViewModelRoundTripUnchanged()
    {
        var viewModel = new SettingsViewModel();
        foreach (var preset in SplashPresets.All)
        {
            var source = SplashPresets.Create(preset);
            viewModel.LoadSplash(source);
            Assert.Equal(Json(source), Json(viewModel.BuildSplashConfig()));
        }
    }

    [Fact]
    public void OutOfRangeSelectorIndicesClampIntoTheirEnumRanges()
    {
        var viewModel = new SettingsViewModel();
        viewModel.SplashSpinnerStyleIndex = 99;
        viewModel.SplashTextPlacementModeIndex = -5;
        viewModel.SplashTextAnchorIndex = 42;
        viewModel.SplashLogoPlacementModeIndex = 77;
        viewModel.SplashLogoAnchorIndex = -1;

        var splash = viewModel.BuildSplashConfig();

        Assert.Equal(SplashSpinnerStyle.Off, splash.SpinnerStyle);
        Assert.Equal(SplashPlacementMode.Anchor, splash.TextPlacement.Mode);
        Assert.Equal(SplashPlacementAnchor.BottomRight, splash.TextPlacement.Anchor);
        Assert.Equal(SplashPlacementMode.WithText, splash.LogoPlacement.Mode);
        Assert.Equal(SplashPlacementAnchor.TopLeft, splash.LogoPlacement.Anchor);
    }

    [Fact]
    public void SelectorLabelListsMatchTheirEnumMemberCounts()
    {
        var viewModel = new SettingsViewModel();
        Assert.Equal((int)SplashSpinnerStyle.Off + 1, viewModel.SplashSpinnerStyles.Count);
        Assert.Equal((int)SplashPlacementMode.WithText + 1, viewModel.SplashPlacementModes.Count);
        Assert.Equal((int)SplashPlacementAnchor.BottomRight + 1, viewModel.SplashPlacementAnchors.Count);
    }

    [Fact]
    public void SnapshotForTestCarriesSplashAndAccentAndStaysIsolatedFromLaterEdits()
    {
        var viewModel = new SettingsViewModel();
        viewModel.AccentColorHex = "#112233";
        viewModel.SplashText = "Snapshot title";
        viewModel.SplashSpinnerStyleIndex = (int)SplashSpinnerStyle.SweepLine;
        viewModel.SplashBackgroundColorHex = "#101010";

        var snapshot = viewModel.SnapshotForTest();

        Assert.Equal("#112233", snapshot.AccentColor);
        Assert.Equal("Snapshot title", snapshot.Splash.Text);
        Assert.Equal(SplashSpinnerStyle.SweepLine, snapshot.Splash.SpinnerStyle);
        Assert.Equal("#101010", snapshot.Splash.BackgroundColor);

        // A later edit must not leak into the already-taken snapshot (deep copy).
        viewModel.SplashText = "Changed afterwards";
        viewModel.AccentColorHex = "#FFFFFF";
        Assert.Equal("Snapshot title", snapshot.Splash.Text);
        Assert.Equal("#112233", snapshot.AccentColor);
    }
}
