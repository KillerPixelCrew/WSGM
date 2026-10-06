using System;
using static WSGM.Core.AppConfigDefaults;

namespace WSGM.Core;

internal static class SplashRules
{
    /// <summary>
    ///     Repairs explicit JSON nulls inside a splash section (see
    ///     <see cref="Normalize" />), bounds the display strings, and clamps every
    ///     numeric field into the range the Appearance editor enforces. Shared with
    ///     splash-theme import, which deserializes the same external contract from
    ///     archives.
    /// </summary>
    internal static ConfigRuleResult<SplashConfig> Normalize(SplashConfig splash)
    {
        ConfigRepair.NormalizeEnums(splash);
        return Repair(splash);
    }

    /// <summary>
    ///     <see cref="Normalize" /> without the enum walk, for a section whose enums
    ///     <see cref="AppConfigRules" /> has already repaired with the rest of the document.
    /// </summary>
    internal static ConfigRuleResult<SplashConfig> Repair(SplashConfig splash)
    {
        splash.Text ??= SplashFieldDefaults.Text;
        splash.TextColor ??= SplashFieldDefaults.TextColor;
        splash.Caption ??= SplashFieldDefaults.Caption;
        splash.CaptionColor ??= SplashFieldDefaults.CaptionColor;
        splash.SpinnerColor ??= SplashFieldDefaults.SpinnerColor;
        splash.BackgroundColor ??= SplashFieldDefaults.BackgroundColor;
        // "No image" has exactly one representation, "": every consumer tests these
        // with IsNullOrWhiteSpace, so a hand-edited config or an imported theme
        // carrying "   " means no image — and must not be persisted as whitespace by
        // the next save either (SplashAssets.PrepareSlot normalizes the same way).
        splash.BackgroundImagePath = Blank(splash.BackgroundImagePath);
        splash.LogoImagePath = Blank(splash.LogoImagePath);
        splash.TextPlacement ??= new SplashElementPlacement();
        splash.SpinnerPlacement ??= new SplashElementPlacement { Mode = SplashPlacementMode.WithText };
        splash.LogoPlacement ??= new SplashElementPlacement { Mode = SplashPlacementMode.WithText };

        splash.TitleFontSize = Math.Clamp(splash.TitleFontSize, MinFontSize, MaxTitleFontSize);
        splash.CaptionFontSize = Math.Clamp(splash.CaptionFontSize, MinFontSize, MaxCaptionFontSize);
        splash.SpinnerSize = Math.Clamp(splash.SpinnerSize, MinSpinnerSize, MaxSpinnerSize);
        splash.LogoMaxSize = Math.Clamp(splash.LogoMaxSize, MinLogoMaxSize, MaxLogoMaxSize);
        NormalizePlacement(splash.TextPlacement);
        NormalizePlacement(splash.SpinnerPlacement);
        NormalizePlacement(splash.LogoPlacement);
        return new ConfigRuleResult<SplashConfig>(splash, []);
    }

    /// <summary>
    ///     Maps a null or whitespace-only image path to the single "no image"
    ///     value, leaving every real path untouched (leading/trailing spaces are legal
    ///     in Windows path components, so nothing else is trimmed).
    /// </summary>
    private static string Blank(string? path)
    {
        return string.IsNullOrWhiteSpace(path) ? "" : path;
    }

    /// <summary>
    ///     Clamps one element placement into the editor's ranges and drops
    ///     unknown enum members back to their defaults.
    /// </summary>
    private static void NormalizePlacement(SplashElementPlacement placement)
    {
        placement.PaddingX = Math.Clamp(placement.PaddingX, MinPadding, MaxPadding);
        placement.PaddingY = Math.Clamp(placement.PaddingY, MinPadding, MaxPadding);
        placement.X = Math.Clamp(placement.X, MinAbsoluteCoordinate, MaxAbsoluteCoordinate);
        placement.Y = Math.Clamp(placement.Y, MinAbsoluteCoordinate, MaxAbsoluteCoordinate);
    }
}
