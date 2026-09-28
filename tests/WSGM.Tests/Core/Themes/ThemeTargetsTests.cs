using WSGM.Core;

namespace WSGM.Tests.Core.Themes;

/// <summary>Tab names expand to the windows CSS Loader's own table stands them for.</summary>
public sealed class ThemeTargetsTests
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> NoMappings =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    [Fact]
    public void AliasesExpandThroughCssLoadersTableToTheirTargets()
    {
        Assert.Equal(
            [
                "~Valve Steam Gamepad/default~", "~Valve%20Steam%20Gamepad~", ThemeTargets.BigPictureWindowName,
                "QuickAccess.*", "MainMenu.*"
            ],
            ThemeTargets.Expand(["All"], NoMappings));
        // CSSLoader's legacy `SP` is Big Picture, which on Windows is the window named "SP BPM_uid<n>".
        Assert.Contains(ThemeTargets.BigPictureWindowName, ThemeTargets.Expand(["SP"], NoMappings));
        Assert.Equal(["QuickAccess.*"], ThemeTargets.Expand(["QuickAccess"], NoMappings));
        Assert.Equal(["Steam|SteamLibraryWindow"], ThemeTargets.Expand(["Steam"], NoMappings));
    }

    [Fact]
    public void AnUnknownNameIsATargetAsWritten()
    {
        Assert.Equal(["MyWindow_.*", "!some-class", "~https://x~"],
            ThemeTargets.Expand(["MyWindow_.*", "!some-class", "~https://x~"], NoMappings));
    }

    [Fact]
    public void AThemesOwnAliasesComeFirstAndAnEmptyListTakesItsDefault()
    {
        Dictionary<string, IReadOnlyList<string>> own = new(StringComparer.Ordinal)
        {
            ["default"] = ["bigpicture"],
            ["QuickAccess"] = ["Custom_.*"]
        };

        Assert.Equal(["Custom_.*"], ThemeTargets.Expand(["QuickAccess"], own));
        Assert.Equal(
            ["~Valve Steam Gamepad/default~", "~Valve%20Steam%20Gamepad~", ThemeTargets.BigPictureWindowName],
            ThemeTargets.Expand([], own));
        Assert.Empty(ThemeTargets.Expand([], NoMappings));
    }

    [Fact]
    public void AnAliasThatNamesItselfCannotHangTheLoader()
    {
        Dictionary<string, IReadOnlyList<string>> own = new(StringComparer.Ordinal) { ["loop"] = ["loop"] };

        Assert.Empty(ThemeTargets.Expand(["loop"], own));
    }

    [Fact]
    public void AWideAliasNamedAgainAndAgainExpandsOnceAndListsEachTargetOnce()
    {
        Dictionary<string, IReadOnlyList<string>> own = new(StringComparer.Ordinal)
        {
            ["x"] = ["x", "x", "x", "x", "y", "Custom_.*"],
            ["y"] = ["x", "Custom_.*", "Other"]
        };

        Assert.Equal(["Custom_.*", "Other"], ThemeTargets.Expand(["x", "y", "x"], own));
    }
}
