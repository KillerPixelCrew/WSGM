using System;
using System.Globalization;
using Avalonia.Data.Converters;
using WSGM.Core;

namespace WSGM.Settings;

/// <summary>What the splash editor's selectors call each choice, instead of the setting's stored name.</summary>
public sealed class SplashChoiceNames : IValueConverter
{
    /// <summary>The shared instance the Appearance page binds through.</summary>
    public static SplashChoiceNames Instance { get; } = new();

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            SplashSpinnerStyle style => style switch
            {
                SplashSpinnerStyle.Ring => "Ring",
                SplashSpinnerStyle.LiArc => "Arc",
                SplashSpinnerStyle.LiArcs => "Arcs",
                SplashSpinnerStyle.LiArcsRing => "Arcs in a ring",
                SplashSpinnerStyle.LiDoubleBounce => "Double bounce",
                SplashSpinnerStyle.LiFlipPlane => "Flipping square",
                SplashSpinnerStyle.LiPulse => "Pulse",
                SplashSpinnerStyle.LiRing => "Thin ring",
                SplashSpinnerStyle.LiThreeDots => "Three dots",
                SplashSpinnerStyle.LiWave => "Wave",
                SplashSpinnerStyle.SweepLine => "Sweep",
                _ => "None"
            },
            SweepEdge edge => edge == SweepEdge.Top ? "Along the top" : "Along the bottom",
            SplashPlacementMode mode => mode switch
            {
                SplashPlacementMode.Anchor => "Anchored",
                SplashPlacementMode.Absolute => "Exact position",
                _ => "With the text"
            },
            SplashPlacementAnchor anchor => anchor switch
            {
                SplashPlacementAnchor.TopLeft => "Top left",
                SplashPlacementAnchor.TopCenter => "Top centre",
                SplashPlacementAnchor.TopRight => "Top right",
                SplashPlacementAnchor.CenterLeft => "Centre left",
                SplashPlacementAnchor.Center => "Centre",
                SplashPlacementAnchor.CenterRight => "Centre right",
                SplashPlacementAnchor.BottomLeft => "Bottom left",
                SplashPlacementAnchor.BottomCenter => "Bottom centre",
                _ => "Bottom right"
            },
            _ => value?.ToString()
        };
    }

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
