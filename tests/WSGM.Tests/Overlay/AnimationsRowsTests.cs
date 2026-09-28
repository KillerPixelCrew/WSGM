using WSGM.Overlay;
using WSGM.Shell;

namespace WSGM.Tests.Overlay;

public sealed class AnimationsRowsTests
{
    private static SteamAnimationsItem Item(string id, bool downloaded = true, bool custom = false)
    {
        return new SteamAnimationsItem(id, id, "Squishy", null, null, "", 4, 20, "2025-01-01", downloaded, custom);
    }

    private static SteamAnimationsState State(bool restart, string selected = "")
    {
        return new SteamAnimationsState("browse", selected, [Item("Neon")],
            new SteamAnimationsBrowse("Newest", "", [], 0, false, null, null), null,
            new SteamAnimationsSettings(false, @"C:\lib", @"C:\steam\movies", restart), false, null, null, 1);
    }

    [Fact]
    public void TheSummaryNamesTheBootMovieAndSaysWhenSteamMustRestart()
    {
        Assert.Equal("Big Picture starts with Steam's own movie.", AnimationsRows.Summary(State(false)));
        Assert.Equal("Big Picture starts with Neon. Restart Steam to see the change.",
            AnimationsRows.Summary(State(true, "Neon")));
    }

    [Fact]
    public void ALibraryLineNamesTheSourceTheAuthorAndWhetherItPlays()
    {
        Assert.Equal("SteamDeckRepo · Squishy · Plays at boot",
            AnimationsRows.Describe(Item("Neon"), State(false, "Neon")));
        Assert.Equal("Your file · Squishy", AnimationsRows.Describe(Item("b", custom: true), State(false)));
    }

    [Fact]
    public void AListingLineCarriesTheCountsAndWhetherTheLibraryHoldsIt()
    {
        Assert.Equal("Squishy · 2025-01-01 · 4 likes · 20 downloads · In the library",
            AnimationsRows.DescribeListing(Item("a")));
        Assert.Equal("Squishy · 2025-01-01 · 4 likes · 20 downloads",
            AnimationsRows.DescribeListing(Item("b", false)));
    }
}
