using WSGM.Shell;

namespace WSGM.Tests.Shell.Steam;

public sealed class SteamUiReadinessTests
{
    [Fact]
    public async Task NextReadyWaitsForAFreshEdgeEvenWhileTheTransportIsOpen()
    {
        var readiness = new SteamUiReadiness();
        readiness.Observe(true);

        // The card monitor spun on this: "when ready" answers at once while the gate is open.
        Assert.True(readiness.WhenReadyAsync(CancellationToken.None).IsCompleted);
        var next = readiness.WhenNextReadyAsync(CancellationToken.None);
        Assert.False(next.IsCompleted);

        readiness.Observe(false);
        Assert.False(next.IsCompleted);

        readiness.Observe(true);
        await next;
    }

    [Fact]
    public async Task NextReadyHonoursCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var next = new SteamUiReadiness().WhenNextReadyAsync(cancellation.Token);

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next);
    }
}
