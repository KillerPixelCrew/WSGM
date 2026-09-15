using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Tests;

/// <summary>The library badge reading built from the card model.</summary>
public sealed class LibraryBadgesTests
{
    [Fact]
    public void EveryNamedCardWithGamesIsListedWithItsConnection()
    {
        AppConfig config = new();
        config.CardLibraries.Add(new CardLibraryConfig
        {
            ContentId = "blue",
            Name = "Blue card",
            AppIds = [70, 400],
        });
        config.CardLibraries.Add(new CardLibraryConfig
        {
            ContentId = "red",
            Name = "Red card",
            AppIds = [220],
        });

        SteamLibraryBadgeState state = LibraryBadges.Build(config, new HashSet<string> { "red" }, revision: 3);

        Assert.Equal(3, state.Revision);
        Assert.Equal("Internal", state.InternalLabel);
        Assert.Collection(
            state.Libraries,
            blue =>
            {
                Assert.Equal("Blue card", blue.Name);
                Assert.False(blue.Connected);
                Assert.Equal([70, 400], blue.AppIds);
            },
            red =>
            {
                Assert.Equal("Red card", red.Name);
                Assert.True(red.Connected);
            });
    }

    [Fact]
    public void AHiddenCardIsStillListedBecauseHidingGovernsTheTabNotWhereTheGameIs()
    {
        AppConfig config = new();
        config.CardLibraries.Add(new CardLibraryConfig
        {
            ContentId = "blue",
            Name = "Blue card",
            Hidden = true,
            Enabled = false,
            AppIds = [70],
        });

        SteamLibraryBadgeState state = LibraryBadges.Build(config, new HashSet<string>());

        SteamLibraryBadgeLibrary library = Assert.Single(state.Libraries);
        Assert.Equal("Blue card", library.Name);
        Assert.False(library.Connected);
    }

    [Fact]
    public void ACardWithNoGamesOrNoNameHasNothingToBadge()
    {
        AppConfig config = new();
        config.CardLibraries.Add(new CardLibraryConfig { ContentId = "empty", Name = "Empty" });
        config.CardLibraries.Add(new CardLibraryConfig { ContentId = "anon", Name = " ", AppIds = [1] });

        Assert.Empty(LibraryBadges.Build(config, new HashSet<string> { "empty", "anon" }).Libraries);
    }

    [Fact]
    public void UpdatingReplacesTheReadingAndRaisesChanged()
    {
        int raised = 0;
        void OnChanged() => raised++;
        LibraryBadges.Changed += OnChanged;
        try
        {
            AppConfig config = new();
            config.CardLibraries.Add(new CardLibraryConfig { ContentId = "c", Name = "Card", AppIds = [5] });

            LibraryBadges.Update(config, new HashSet<string> { "c" });
            long first = LibraryBadges.Current!.Revision;
            LibraryBadges.Update(config, new HashSet<string>());

            Assert.Equal(2, raised);
            Assert.True(LibraryBadges.Current!.Revision > first);
            Assert.False(Assert.Single(LibraryBadges.Current.Libraries).Connected);
        }
        finally
        {
            LibraryBadges.Changed -= OnChanged;
        }
    }

    [Fact]
    public async Task TheHomeLayoutReportIsAcceptedAndLoggedOnlyOnChange()
    {
        LibraryBadgeBackend backend = new();

        Assert.Null(backend.BigArt);
        Assert.True((await backend.HomeLayoutAsync(true, CancellationToken.None)).Succeeded);
        Assert.True((await backend.HomeLayoutAsync(true, CancellationToken.None)).Succeeded);
        Assert.Equal(true, backend.BigArt);

        await backend.HomeLayoutAsync(false, CancellationToken.None);
        Assert.Equal(false, backend.BigArt);
    }
}
