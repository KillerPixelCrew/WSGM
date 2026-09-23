using WSGM.Core;

namespace WSGM.Tests.Core;

/// <summary>
///     The only part of the importer that writes to somebody's live Steam library. A new id is
///     confirmed by two sources and the library diff is the authority, because the client's return
///     value has never been verified across builds.
/// </summary>
public sealed class SteamShortcutWriterTests
{
    private static readonly PackagedLauncherShortcutFields Fields =
        new("\"C:\\WSGM\\WSGM.PackagedLaunch.exe\"", "\"C:\\WSGM\"", "--aumid A_x!App --mode controller-only");

    private static SteamShortcutWriter Writer(
        Queue<IReadOnlyList<uint>> listings,
        uint returned,
        List<string>? calls = null,
        bool setLaunchAccepted = true,
        bool removeAccepted = true)
    {
        return new SteamShortcutWriter(
            _ =>
            {
                calls?.Add("list");
                return Task.FromResult(listings.Count > 0 ? listings.Dequeue() : []);
            },
            (_, _, _, _, _) =>
            {
                calls?.Add("add");
                return Task.FromResult(returned);
            },
            (_, _, _, _) =>
            {
                calls?.Add("setLaunch");
                return Task.FromResult(setLaunchAccepted);
            },
            (_, _) =>
            {
                calls?.Add("remove");
                return Task.FromResult(removeAccepted);
            });
    }

    [Fact]
    public async Task AnAddIsConfirmedWhenTheReturnedIdAndTheDiffAgree()
    {
        Queue<IReadOnlyList<uint>> listings = new([[100u], [100u, 2147483650u]]);

        var result = await Writer(listings, 2147483650u).AddAsync("Game", Fields, CancellationToken.None);

        Assert.True(result.Confirmed);
        Assert.Equal(2147483650u, result.AppId);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task TheLibraryDiffIsTheAuthorityWhenTheClientReturnsNothing()
    {
        // Steam not answering with an id is not evidence that nothing was created.
        Queue<IReadOnlyList<uint>> listings = new([[100u], [100u, 2147483650u]]);

        var result = await Writer(listings, 0).AddAsync("Game", Fields, CancellationToken.None);

        Assert.True(result.Confirmed);
        Assert.Equal(2147483650u, result.AppId);
    }

    [Fact]
    public async Task ADisagreementBetweenTheReturnedIdAndTheDiffIsUnconfirmed()
    {
        Queue<IReadOnlyList<uint>> listings = new([[100u], [100u, 2147483650u]]);

        var result = await Writer(listings, 2147483651u).AddAsync("Game", Fields, CancellationToken.None);

        Assert.False(result.Confirmed);
        Assert.Contains("2147483650", result.Error);
    }

    [Fact]
    public async Task AnAddThatProducesNoNewEntryIsUnconfirmed()
    {
        Queue<IReadOnlyList<uint>> listings = new([[100u], [100u]]);

        var result = await Writer(listings, 2147483650u).AddAsync("Game", Fields, CancellationToken.None);

        Assert.False(result.Confirmed);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public async Task TwoEntriesAppearingAtOnceIsUnconfirmedRatherThanGuessed()
    {
        // Another client or another process added one at the same moment. Picking either would key
        // this title's record on something that may belong to someone else.
        Queue<IReadOnlyList<uint>> listings = new([[100u], [100u, 2147483650u, 2147483651u]]);

        var result = await Writer(listings, 2147483650u).AddAsync("Game", Fields, CancellationToken.None);

        Assert.False(result.Confirmed);
    }

    [Fact]
    public async Task AnUnconfirmedAddIsNotRetried()
    {
        // The shortcut may well exist. Asking again would create a second one.
        Queue<IReadOnlyList<uint>> listings = new([[100u], [100u]]);
        List<string> calls = [];

        await Writer(listings, 0, calls).AddAsync("Game", Fields, CancellationToken.None);

        Assert.Equal(1, calls.Count(call => call == "add"));
    }

    [Fact]
    public async Task AnUpdateRewritesRatherThanReplacing()
    {
        // Remove-and-re-add would lose the id and all its artwork.
        List<string> calls = [];

        var result = await Writer(new Queue<IReadOnlyList<uint>>(), 0, calls)
            .UpdateAsync(2147483650u, Fields, CancellationToken.None);

        Assert.True(result.Confirmed);
        Assert.Contains("setLaunch", calls);
        Assert.DoesNotContain("remove", calls);
        Assert.DoesNotContain("add", calls);
    }

    [Fact]
    public async Task ARefusedUpdateIsReportedRatherThanAssumed()
    {
        var result = await Writer(new Queue<IReadOnlyList<uint>>(), 0, setLaunchAccepted: false)
            .UpdateAsync(2147483650u, Fields, CancellationToken.None);

        Assert.False(result.Confirmed);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public async Task ARefusedRemovalIsReportedRatherThanAssumed()
    {
        var result = await Writer(new Queue<IReadOnlyList<uint>>(), 0, removeAccepted: false)
            .RemoveAsync(2147483650u, CancellationToken.None);

        Assert.False(result.Confirmed);
    }

    [Fact]
    public async Task StopAfterSteamAcceptedAnUpdateStillReturnsItsResult()
    {
        // Steam has changed the entry; stopping before the caller records it would make the next
        // scan call WSGM's own change a hand edit.
        using CancellationTokenSource stop = new();
        SteamShortcutWriter writer = new(
            _ => Task.FromResult<IReadOnlyList<uint>>([]),
            (_, _, _, _, _) => Task.FromResult(0u),
            (_, _, _, _) =>
            {
                stop.Cancel();
                return Task.FromResult(true);
            },
            (_, _) => Task.FromResult(true));

        var result = await writer.UpdateAsync(7, Fields, stop.Token);

        Assert.True(result.Confirmed);
    }

    [Fact]
    public async Task StopAfterSteamCreatedAShortcutStillConfirmsIt()
    {
        using CancellationTokenSource stop = new();
        Queue<IReadOnlyList<uint>> listings = new([[], [9u]]);
        SteamShortcutWriter writer = new(
            _ => Task.FromResult(listings.Count > 0 ? listings.Dequeue() : []),
            (_, _, _, _, _) =>
            {
                stop.Cancel();
                return Task.FromResult(9u);
            },
            (_, _, _, _) => Task.FromResult(true),
            (_, _) => Task.FromResult(true));

        var result = await writer.AddAsync("Moonlit", Fields, stop.Token);

        Assert.Equal((9u, true), (result.AppId, result.Confirmed));
    }
}
