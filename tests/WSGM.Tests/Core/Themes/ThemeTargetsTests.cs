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
            ["~Valve Steam Gamepad/default~", "~Valve%20Steam%20Gamepad~", "QuickAccess", "MainMenu"],
            ThemeTargets.Expand(["All"], NoMappings));
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
        Assert.Equal(["~Valve Steam Gamepad/default~", "~Valve%20Steam%20Gamepad~"], ThemeTargets.Expand([], own));
        Assert.Empty(ThemeTargets.Expand([], NoMappings));
    }

    [Fact]
    public void AnAliasThatNamesItselfCannotHangTheLoader()
    {
        Dictionary<string, IReadOnlyList<string>> own = new(StringComparer.Ordinal) { ["loop"] = ["loop"] };

        Assert.Empty(ThemeTargets.Expand(["loop"], own));
    }
}
