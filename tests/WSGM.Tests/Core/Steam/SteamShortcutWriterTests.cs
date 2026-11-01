using WSGM.Core;

namespace WSGM.Tests.Core;

/// <summary>
///     The only part of the importer that writes to somebody's live Steam library. The toolkit confirms
///     which entry an add made; this holds WSGM's policy over it: nothing is retried, a write once sent
///     runs to its answer, and a route change takes its start directory with it.
/// </summary>
public sealed class SteamShortcutWriterTests
{
    private static readonly ShortcutFields Fields =
        new("\"C:\\WSGM\\WSGM.PackagedLaunch.exe\"", "\"C:\\WSGM\"", "--aumid A_x!App --mode controller-only");

    private static SteamShortcutWriter Writer(
        ShortcutWriteResult added,
        List<string>? calls = null,
        bool setLaunchAccepted = true,
        bool removeAccepted = true,
        List<ShortcutFields>? written = null,
        List<CancellationToken>? tokens = null)
    {
        return new SteamShortcutWriter(
            (_, fields, token) =>
            {
                calls?.Add("add");
                written?.Add(fields);
                tokens?.Add(token);
                return Task.FromResult(added);
            },
            (_, fields, token) =>
            {
                calls?.Add("setLaunch");
                written?.Add(fields);
                tokens?.Add(token);
                return Task.FromResult(setLaunchAccepted);
            },
            (_, token) =>
            {
                calls?.Add("remove");
                tokens?.Add(token);
                return Task.FromResult(removeAccepted);
            });
    }

    [Fact]
    public async Task AConfirmedAddIsPassedOnWithWhatSteamDidNotKeep()
    {
        var result = await Writer(new ShortcutWriteResult(2147483650u, true, null, "Steam holds a different name."))
            .AddAsync("Game", Fields, CancellationToken.None);

        Assert.True(result.Confirmed);
        Assert.Equal(2147483650u, result.AppId);
        Assert.Equal("Steam holds a different name.", result.Mismatch);
    }

    [Fact]
    public async Task AnUnconfirmedAddIsNotRetried()
    {
        // The shortcut may well exist. Asking again would create a second one.
        List<string> calls = [];

        var result = await Writer(new ShortcutWriteResult(2147483650u, false, "Steam gained 2 entries at once."), calls)
            .AddAsync("Game", Fields, CancellationToken.None);

        Assert.False(result.Confirmed);
        Assert.Equal(["add"], calls);
    }

    [Fact]
    public async Task AnUpdateRewritesTheWholeCommandIncludingItsStartDirectory()
    {
        // Remove-and-re-add would lose the id and all its artwork; leaving the start directory behind
        // would run a new program from the old one's folder.
        List<string> calls = [];
        List<ShortcutFields> written = [];

        var result = await Writer(new ShortcutWriteResult(0, false, null), calls, written: written)
            .UpdateAsync(2147483650u, Fields, CancellationToken.None);

        Assert.True(result.Confirmed);
        Assert.Equal(["setLaunch"], calls);
        Assert.Equal(Fields, Assert.Single(written));
    }

    [Fact]
    public async Task ARefusedUpdateIsReportedRatherThanAssumed()
    {
        var result = await Writer(new ShortcutWriteResult(0, false, null), setLaunchAccepted: false)
            .UpdateAsync(2147483650u, Fields, CancellationToken.None);

        Assert.False(result.Confirmed);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public async Task ARefusedRemovalIsReportedRatherThanAssumed()
    {
        var result = await Writer(new ShortcutWriteResult(0, false, null), removeAccepted: false)
            .RemoveAsync(2147483650u, CancellationToken.None);

        Assert.False(result.Confirmed);
    }

    [Fact]
    public async Task StopBeforeAWriteIsSentSendsNothing()
    {
        using CancellationTokenSource stop = new();
        stop.Cancel();
        List<string> calls = [];
        var writer = Writer(new ShortcutWriteResult(9, true, null), calls);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.AddAsync("Game", Fields, stop.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.UpdateAsync(9, Fields, stop.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.RemoveAsync(9, stop.Token));
        Assert.Empty(calls);
    }

    [Fact]
    public async Task AWriteOnceSentIsNotCancelledByStop()
    {
        // Steam may already have changed the entry; the caller has to get its answer to record it.
        using CancellationTokenSource stop = new();
        List<CancellationToken> tokens = [];
        var writer = Writer(new ShortcutWriteResult(9, true, null), tokens: tokens);

        await writer.AddAsync("Game", Fields, stop.Token);
        await writer.UpdateAsync(9, Fields, stop.Token);
        await writer.RemoveAsync(9, stop.Token);

        Assert.All(tokens, token => Assert.False(token.CanBeCanceled));
    }
}
