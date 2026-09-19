// SPDX-License-Identifier: MIT

using WSGM.Core;

namespace WSGM.Device.Tests;

/// <summary>Creates fully customized splash configurations for serialization and theme tests.</summary>
internal static class SplashConfigBuilder
{
    public static SplashConfig FullyCustomized(string logoPath, string backgroundPath)
    {
        return new SplashConfig
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
            BackgroundImagePath = backgroundPath,
            LogoImagePath = logoPath,
            LogoMaxSize = 320,
            TextPlacement = new SplashElementPlacement
            {
                Mode = SplashPlacementMode.Anchor,
                Anchor = SplashPlacementAnchor.BottomLeft,
                PaddingX = 48,
                PaddingY = 160
            },
            SpinnerPlacement = new SplashElementPlacement
            {
                Mode = SplashPlacementMode.Absolute,
                X = 640,
                Y = 360
            },
            LogoPlacement = new SplashElementPlacement
            {
                Mode = SplashPlacementMode.Anchor,
                Anchor = SplashPlacementAnchor.TopCenter,
                PaddingX = 0,
                PaddingY = 96
            }
        };
    }
}
