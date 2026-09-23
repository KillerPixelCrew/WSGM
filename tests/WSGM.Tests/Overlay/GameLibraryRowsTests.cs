using WSGM.Overlay;
using WSGM.Shell;

namespace WSGM.Tests.Overlay;

/// <summary>What the overlay's Game Library view says. It reads the same state the Steam page does.</summary>
public sealed class GameLibraryRowsTests
{
    private static GameLibraryEntry Entry(
        string action = "Add",
        string mode = "SteamIntegration",
        bool selected = false,
        bool excluded = false,
        bool canIntegrate = true,
        bool requiresAcknowledgement = false,
        int offered = 3,
        int? applied = null)
    {
        return new GameLibraryEntry("0", "Moonlit", "Xbox", "Publisher.Game_abc!App", @"C:\WindowsApps\Game",
            "UWP", true, "Evidence.", "SinglePlayer", "Evidence.", mode, canIntegrate, requiresAcknowledgement,
            false, action, "Reason.", selected, !excluded, excluded, [], 0, offered, applied);
    }

    private static GameLibraryState State(params GameLibraryEntry[] entries)
    {
        return new GameLibraryState(["Xbox"], "review", entries, 0, 2, 1, 0, 4, 0, 0, 0, 0, true);
    }

    [Fact]
    public void ALineSaysWhatWouldHappenHowItLaunchesAndWhichMode()
    {
        Assert.Equal("Add · UWP · Steam overlay", GameLibraryRows.Describe(Entry()));
        Assert.Equal("Selected · Already imported · UWP · Controller only",
            GameLibraryRows.Describe(Entry("Skip", "ControllerOnly", selected: true)));
    }

    [Fact]
    public void ALeftOutTitleSaysSoRatherThanWhatItWouldHaveDone()
    {
        Assert.StartsWith("Not importing", GameLibraryRows.Describe(Entry(excluded: true)));
    }

    [Fact]
    public void AnUnknownActionIsShownByItsOwnNameRatherThanHidden()
    {
        Assert.Equal("Rename", GameLibraryRows.Action("Rename"));
    }

    [Fact]
    public void ArtworkIsOfferedUntilImportedAndThenCounted()
    {
        Assert.Equal("3 Store images offered", GameLibraryRows.Artwork(Entry()));
        Assert.Equal("1 Store image offered, 1 applied", GameLibraryRows.Artwork(Entry(offered: 1, applied: 1)));
    }

    [Fact]
    public void TheModeRowNeverOffersARouteTheTitleDoesNotHave()
    {
        Assert.StartsWith("Controller only. There is no validated way",
            GameLibraryRows.ModeChoice(Entry(mode: "ControllerOnly", canIntegrate: false)));
        Assert.Contains("accept the risk",
            GameLibraryRows.ModeChoice(Entry(mode: "ControllerOnly", requiresAcknowledgement: true)));
    }

    [Fact]
    public void TheSummaryPromptsForAScanBeforeThereIsOne()
    {
        Assert.Equal("Scan to see which games can be brought into Steam.",
            GameLibraryRows.Summary(new GameLibraryState(["Xbox"], "idle", [], 0, 0, 0, 0, 0, 0, 0, 0, 0, true)));
        Assert.Equal("2 to add, 1 to update, 4 already imported", GameLibraryRows.Summary(State(Entry())));
    }

    [Fact]
    public void OnlyATitleSteamHasAsOursCountsAsImported()
    {
        Assert.False(GameLibraryRows.InSteam(Entry()));
        Assert.True(GameLibraryRows.InSteam(Entry("Skip") with { AppId = 77 }));
        Assert.False(GameLibraryRows.InSteam(Entry("Remove") with { AppId = 77 }));
        Assert.False(GameLibraryRows.InSteam(Entry("Conflict") with { AppId = 77 }));
    }
}
