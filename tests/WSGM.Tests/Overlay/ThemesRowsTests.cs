using WSGM.Overlay;
using WSGM.Shell;

namespace WSGM.Tests.Overlay;

/// <summary>What the overlay's Themes view says, without a window.</summary>
public sealed class ThemesRowsTests
{
    private static SteamThemesInstalled Theme(bool enabled = false, string status = "installed", string? latest = null,
        bool hidden = false, string author = "Squishy")
    {
        return new SteamThemesInstalled("id", "Dark", "Dark Deck", "v2.1", author, enabled, false, hidden, status,
            latest,
            [], [], []);
    }

    private static SteamThemesState State(params SteamThemesInstalled[] themes)
    {
        return new SteamThemesState("browse", themes, [], "",
            new SteamThemesBrowse("All", "Last Updated", "", new Dictionary<string, int>(), [], [], 0, 0, false, null),
            null, new SteamThemesSettings(true, "auto", false, 0, null, "", ""), [], false, null, null,
            themes.Count(theme => theme.Status == "outdated"), 1);
    }

    [Fact]
    public void TheSummaryCountsWhatIsOnAndWhatCanBeUpdated()
    {
        Assert.Equal("Restyle Big Picture with CSS Loader themes from DeckThemes.", ThemesRows.Summary(State()));
        Assert.Equal("1 of 2 themes on, 1 update available.",
            ThemesRows.Summary(State(Theme(true), Theme(status: "outdated", latest: "v3"))));
        var off = State(Theme(true)) with { Settings = new SteamThemesSettings(false, "auto", false, 0, null, "", "") };
        Assert.Equal("Themes are off in Settings, so nothing is installed into Steam.", ThemesRows.Summary(off));
    }

    [Fact]
    public void AThemesLineSaysOnOffVersionAuthorAndUpdate()
    {
        Assert.Equal("Off · v2.1 · Squishy", ThemesRows.Describe(Theme()));
        Assert.Equal("On · Update available (v3) · Squishy · Hidden from Quick Access",
            ThemesRows.Describe(Theme(true, "outdated", "v3", true)));
        Assert.Equal("On · v2.1", ThemesRows.Describe(Theme(true, author: "")));
    }

    [Fact]
    public void AListingsLineSaysWhatTheCardShows()
    {
        var item = new SteamThemesStoreItem("id", "Dark", "Dark Deck", "v2.1", "Steam Deck", ["Steam Deck"], "Squishy",
            null, 12480, 318, "12 Sep 2026", "outdated");

        Assert.Equal("v2.1 · Squishy · Steam Deck · 12480 downloads · Installed, update available",
            ThemesRows.DescribeListing(item));
    }
}
