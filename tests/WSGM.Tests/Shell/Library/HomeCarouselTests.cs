using SteamUiToolkit;
using WSGM.Shell;

namespace WSGM.Tests.Shell;

/// <summary>WSGM's instruction to Big Picture Home's carousel, built from the card reading.</summary>
public sealed class HomeCarouselTests
{
    [Fact]
    public void GamesOnADisconnectedLibraryAreListedUnlessAnAttachedLibraryAlsoHoldsThem()
    {
        SteamLibraryBadgeState libraries = new(
            [
                new SteamLibraryBadgeLibrary("Blue card", true, [1, 2]),
                new SteamLibraryBadgeLibrary("Red card", false, [4, 3, 2, 4])
            ],
            7);

        var state = HomeCarousel.Build(libraries, false);

        // Game 2 is on both cards and one of them is in the reader, so it stays.
        Assert.Equal([3, 4], state.DisconnectedAppIds);
        Assert.False(state.IncludeUninstalled);
        Assert.Equal(7, state.Revision);
    }

    [Fact]
    public void BeforeTheCardModelIsReadNothingIsDisconnected()
    {
        var state = HomeCarousel.Build(null, true);

        Assert.Empty(state.DisconnectedAppIds);
        Assert.True(state.IncludeUninstalled);
    }

    [Fact]
    public void AReadingWithEveryCardAttachedExcludesNothing()
    {
        SteamLibraryBadgeState libraries = new(
            [new SteamLibraryBadgeLibrary("Blue card", true, [1, 2])]);

        Assert.Empty(HomeCarousel.Build(libraries, false).DisconnectedAppIds);
    }

    [Fact]
    public async Task TheCarouselsReportIsAccepted()
    {
        HomeCarouselBackend backend = new();
        SteamHomeCarouselReport report = new(12, 1, 9, 2, 4, true, false);

        Assert.True((await backend.ReportAsync(report, CancellationToken.None)).Succeeded);
        Assert.True((await backend.ReportAsync(report with { Fallback = true }, CancellationToken.None)).Succeeded);
    }
}
