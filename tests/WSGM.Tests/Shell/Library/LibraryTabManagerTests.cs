using SteamUiToolkit;
using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class LibraryTabManagerTests
{
    [Fact]
    public async Task ReadyLibraryUsesARequestWithinTheTransportDeadline()
    {
        await using var transport = new ReadinessTransport("true");

        Assert.True(await LibraryTabManager.WaitForLibraryAsync(new SteamClient(transport), CancellationToken.None));
        Assert.Equal(1, transport.Calls);
    }

    [Fact]
    public async Task AnUnreadyLibraryCanFinishLoadingDuringTheSameReadyEdge()
    {
        await using var transport = new ReadinessTransport("false", "true");

        Assert.True(await LibraryTabManager.WaitForLibraryAsync(new SteamClient(transport), CancellationToken.None));
        Assert.Equal(2, transport.Calls);
    }

    [Fact]
    public async Task ACancelledWaitDoesNotSendAReadinessRequest()
    {
        await using var transport = new ReadinessTransport();

        Assert.False(
            await LibraryTabManager.WaitForLibraryAsync(new SteamClient(transport), new CancellationToken(true)));
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task CancellationBetweenProbesStopsTheWait()
    {
        using var cancellation = new CancellationTokenSource();
        await using var transport = new ReadinessTransport("false") { OnEvaluate = cancellation.Cancel };

        Assert.False(await LibraryTabManager.WaitForLibraryAsync(new SteamClient(transport), cancellation.Token));
        Assert.Equal(1, transport.Calls);
    }

    private sealed class ReadinessTransport(params string[] replies) : ISteamUiTransport
    {
        internal int Calls { get; private set; }
        internal Action? OnEvaluate { get; init; }

        public event EventHandler<SteamUiNotification>? NotificationReceived
        {
            add { }
            remove { }
        }

        public event EventHandler<SteamUiTransportSnapshot>? GenerationChanged
        {
            add { }
            remove { }
        }

        public ValueTask<IAsyncDisposable> SubscribeAsync(
            SteamUiTargetRole role, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("A readiness read does not install a subscription.");
        }

        public Task<SteamUiEvaluationResult> EvaluateAsync(
            SteamUiTargetRole role, string expression, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Assert.Equal(SteamUiTargetRole.SharedJsContext, role);
            Assert.InRange(timeout, TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(30));
            Assert.True(cancellationToken.CanBeCanceled);
            var reply = replies[Calls++];
            OnEvaluate?.Invoke();
            return Task.FromResult(new SteamUiEvaluationResult(SteamUiDispatch.Answered, reply, null, default));
        }

        public Task SetRuntimeBindingAsync(
            SteamUiTargetRole role, string bindingName, bool installed, TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("A readiness read does not install a binding.");
        }

        public IReadOnlyList<SteamUiTransportSnapshot> GetSnapshots()
        {
            return [];
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
