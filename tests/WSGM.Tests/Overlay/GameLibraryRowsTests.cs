using WSGM.Overlay;
using WSGM.Shell;

namespace WSGM.Tests.Overlay;

/// <summary>What the overlay's Game Library view says. It reads the same state, and labels, the Steam page does.</summary>
public sealed class GameLibraryRowsTests
{
    private static readonly GameLibrarySource Xbox = new("xbox", "Xbox", "launcher", true, true, "Installed", 1);

    private static GameLibraryEntry Entry(
        string action = "Add",
        string actionLabel = "New",
        string mode = "SteamIntegration",
        bool selected = false,
        bool excluded = false,
        bool canIntegrate = true,
        bool requiresAcknowledgement = false,
        uint appId = 0,
        IReadOnlyList<GameLibraryArtworkSlot>? artwork = null,
        string artworkStatus = "ready",
        string artworkDetail = "",
        int? applied = null)
    {
        return new GameLibraryEntry("id", "Moonlit", "Xbox", "xbox", action, excluded ? "Left out" : actionLabel,
            "new", "Reason.", selected, !excluded, excluded, !excluded, true, mode, canIntegrate,
            requiresAcknowledgement, false, mode == "SteamIntegration" ? "Steam overlay" : "Controller only", false,
            [], string.Empty, appId, applied, artwork ?? [], artworkStatus, artworkDetail, string.Empty, false);
    }

    private static GameLibraryArtworkSlot Slot(string kind)
    {
        return new GameLibraryArtworkSlot("grid", kind, string.Empty, string.Empty, 0, 0);
    }

    private static GameLibraryState State(string phase, params GameLibraryEntry[] entries)
    {
        return new GameLibraryState([Xbox], ["Xbox"], phase, entries, 0, 0, 0, 50, true);
    }

    [Fact]
    public void ALineSaysWhatWouldHappenAndHowItLaunchesInTheServicesWords()
    {
        Assert.Equal("New · Steam overlay", GameLibraryRows.Describe(Entry()));
        Assert.Equal("Selected · Imported · Controller only",
            GameLibraryRows.Describe(Entry("Skip", "Imported", "ControllerOnly", true)));
    }

    [Fact]
    public void ALeftOutTitleSaysSoRatherThanWhatItWouldHaveDone()
    {
        Assert.StartsWith("Left out", GameLibraryRows.Describe(Entry(excluded: true)));
    }

    [Fact]
    public void ArtworkCountsTheSlotsThatWillShowAnImageTheCurrentOnesIncluded()
    {
        // A fully decorated imported title keeps every image it has; that is not "no images".
        Assert.Equal("5 of 5 artwork types have an image, 5 applied by WSGM", GameLibraryRows.Artwork(Entry(
            artwork: [Slot("keep"), Slot("keep"), Slot("keep"), Slot("keep"), Slot("keep")], applied: 5)));
        Assert.Equal("1 of 2 artwork types have an image",
            GameLibraryRows.Artwork(Entry(artwork: [Slot("default"), Slot("none")])));
    }

    [Fact]
    public void ArtworkThatCouldNotBeFetchedSaysWhy()
    {
        Assert.Equal("SteamGridDB rejected the API key.", GameLibraryRows.Artwork(Entry(
            artwork: [Slot("none")], artworkStatus: "failed", artworkDetail: "SteamGridDB rejected the API key.")));
        Assert.Equal("Finding images…",
            GameLibraryRows.Artwork(Entry(artwork: [Slot("loading")], artworkStatus: "loading")));
    }

    [Fact]
    public void TheLaunchRowNeverOffersARouteTheTitleDoesNotHave()
    {
        Assert.StartsWith("There is no validated way",
            GameLibraryRows.LaunchChoice(Entry(mode: "ControllerOnly", canIntegrate: false)));
        Assert.Contains("accept the risk",
            GameLibraryRows.LaunchChoice(Entry(mode: "ControllerOnly", requiresAcknowledgement: true)));
    }

    [Fact]
    public void TheSummaryPromptsForAScanBeforeThereIsOneAndCountsWhatTheEntriesSay()
    {
        Assert.Equal("Scan to see which games can be brought into Steam.", GameLibraryRows.Summary(State("idle")));
        Assert.Equal(
            "1 to add, 0 to update, 1 already imported, 1 with new artwork to save, 1 already in Steam to adopt",
            GameLibraryRows.Summary(State("review", Entry(), Entry("Skip", "Imported"), Entry("Artwork", "Artwork"),
                Entry("Adopt", "Adopt"), Entry(excluded: true))));
    }

    [Fact]
    public void OnlyATitleSteamHasAsOursCountsAsImported()
    {
        Assert.False(GameLibraryRows.InSteam(Entry()));
        Assert.True(GameLibraryRows.InSteam(Entry("Skip", appId: 77)));
        Assert.True(GameLibraryRows.InSteam(Entry("Artwork", appId: 77)));
        Assert.False(GameLibraryRows.InSteam(Entry("Adopt", appId: 77)));
        Assert.False(GameLibraryRows.InSteam(Entry("Remove", appId: 77)));
        Assert.False(GameLibraryRows.InSteam(Entry("Conflict", appId: 77)));
    }
}
