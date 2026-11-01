using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class EmulatorUpdateSequenceTests
{
    private static readonly EmulatorPendingUpdate[] Updates =
    [
        new("retroarch", "RetroArch", "1.22.0"), new("cemu", "Cemu", "2.7"), new("dolphin", "Dolphin", "2506")
    ];

    [Fact]
    public async Task UpdatesRunOneAtATimeInOrderAndPublishEachStep()
    {
        List<string> order = [];
        List<EmulatorUpdateAllState> published = [];
        var running = 0;
        var result = await EmulatorUpdateSequence.RunAsync(Updates, async (update, _) =>
        {
            Assert.Equal(1, Interlocked.Increment(ref running));
            order.Add(update.InstallationId);
            await Task.Yield();
            Interlocked.Decrement(ref running);
        }, published.Add, CancellationToken.None);

        Assert.Equal(["retroarch", "cemu", "dolphin"], order);
        Assert.Equal(["RetroArch", "Cemu", "Dolphin"],
            published.Where(state => state.Running).Select(state => state.Current));
        Assert.Equal([0, 1, 2], published.Where(state => state.Running).Select(state => state.Done));
        Assert.False(result.Running);
        Assert.Same(result, published[^1]);
        Assert.Equal(3, result.Done);
        Assert.Empty(result.Failed);
        Assert.False(result.Cancelled);
        Assert.Equal("Updated RetroArch 1.22.0, Cemu 2.7 and Dolphin 2506.", result.Outcome);
    }

    [Fact]
    public async Task AFailedUpdateIsRecordedTheNextStillRunsAndTheOutcomeIsNotWordedAsSuccess()
    {
        List<string> attempted = [];
        var result = await EmulatorUpdateSequence.RunAsync(Updates, (update, _) =>
        {
            attempted.Add(update.InstallationId);
            return update.InstallationId == "cemu"
                ? Task.FromException(new InvalidOperationException("The download failed."))
                : Task.CompletedTask;
        }, _ => { }, CancellationToken.None);

        Assert.Equal(["retroarch", "cemu", "dolphin"], attempted);
        Assert.Equal(["RetroArch 1.22.0", "Dolphin 2506"], result.Updated);
        Assert.Equal(["Cemu: The download failed."], result.Failed);
        Assert.Equal(3, result.Done);
        Assert.Equal("Updated RetroArch 1.22.0 and Dolphin 2506. Cemu was not updated: The download failed.",
            result.Outcome);
    }

    [Fact]
    public async Task CancellationStopsBeforeTheNextUpdateAndNamesWhatWasNotReached()
    {
        using CancellationTokenSource cancellation = new();
        List<string> attempted = [];
        var result = await EmulatorUpdateSequence.RunAsync(Updates, (update, token) =>
        {
            attempted.Add(update.InstallationId);
            if (update.InstallationId == "cemu")
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
            }

            return Task.CompletedTask;
        }, _ => { }, cancellation.Token);

        Assert.Equal(["retroarch", "cemu"], attempted);
        Assert.True(result.Cancelled);
        Assert.Equal(["RetroArch 1.22.0"], result.Updated);
        Assert.Empty(result.Failed);
        Assert.Equal("Updated RetroArch 1.22.0. Update all was cancelled before Cemu and Dolphin. Their active "
                     + "versions are preserved.", result.Outcome);
    }
}
