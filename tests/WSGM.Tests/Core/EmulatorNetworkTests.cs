using WSGM.Core;

namespace WSGM.Tests.Core;

public sealed class EmulatorNetworkTests
{
    [Fact]
    public async Task NetworkTimeoutIsDistinctFromCallerCancellation()
    {
        using var handler = new CancelledResponse();
        using var network = new EmulatorNetwork(handler);
        var failure = await Assert.ThrowsAsync<TimeoutException>(() =>
            network.OpenAsync("https://example.test/release", CancellationToken.None));
        Assert.Contains("example.test", failure.Message);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            network.OpenAsync("https://example.test/release", cancelled.Token));
    }

    private sealed class CancelledResponse : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromException<HttpResponseMessage>(new TaskCanceledException("Simulated HTTP timeout."));
        }
    }
}
