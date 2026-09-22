using WSGM.Core;

namespace WSGM.Tests.Core;

/// <summary>
///     The artwork section's normalization: the tab order is a permutation of the tabs that exist, so
///     a hand-edited value cannot hide one the show switches still say is visible, and the default tab
///     always names a tab the strip actually renders.
/// </summary>
public sealed class ArtworkConfigTests
{
    private static ArtworkConfig Normalized(ArtworkConfig artwork)
    {
        ConfigStore.NormalizeArtwork(artwork);
        return artwork;
    }

    [Fact]
    public void ADefaultSectionAlreadyNamesEveryTabInOrder()
    {
        var artwork = Normalized(new ArtworkConfig());

        Assert.Equal(ArtworkConfig.DefaultTabOrder, artwork.TabOrder);
        Assert.Equal("grid", artwork.DefaultTab);
    }

    [Fact]
    public void AnUnknownTabIsDroppedFromTheOrderRatherThanRendered()
    {
        var artwork = Normalized(new ArtworkConfig { TabOrder = "hero,nonsense,grid" });

        Assert.Equal("hero,grid,wide,logo,icon,manage", artwork.TabOrder);
    }

    [Fact]
    public void ATabMissingFromTheOrderIsAppendedRatherThanLost()
    {
        // The show switches decide visibility. An order that omits a tab would hide one the user
        // still has switched on, with nothing in the UI explaining why.
        var artwork = Normalized(new ArtworkConfig { TabOrder = "logo" });

        Assert.Equal("logo,grid,wide,hero,icon,manage", artwork.TabOrder);
    }

    [Fact]
    public void ARepeatedTabIsKeptOnceAtItsFirstPosition()
    {
        var artwork = Normalized(new ArtworkConfig { TabOrder = "icon,grid,icon" });

        Assert.Equal("icon,grid,wide,hero,logo,manage", artwork.TabOrder);
    }

    [Fact]
    public void ADefaultTabThatNamesNoTabFallsBackToTheFirstOneShown()
    {
        var artwork = Normalized(new ArtworkConfig { DefaultTab = "nonsense", TabOrder = "hero,grid" });

        Assert.Equal("hero", artwork.DefaultTab);
    }

    [Fact]
    public void CredentialsAreTrimmedSoAPastedKeyStillAuthenticates()
    {
        var artwork = Normalized(new ArtworkConfig
        {
            SteamGridDbApiKey = "  abc  ",
            ScreenscraperUser = " someone "
        });

        Assert.Equal("abc", artwork.SteamGridDbApiKey);
        Assert.Equal("someone", artwork.ScreenscraperUser);
    }

    [Fact]
    public void ThePasswordIsNotTrimmed()
    {
        // Leading and trailing spaces are legal in a password, and silently removing them would
        // authenticate as something the user did not type.
        var artwork = Normalized(new ArtworkConfig { ScreenscraperUserPassword = " pw " });

        Assert.Equal(" pw ", artwork.ScreenscraperUserPassword);
    }
}
