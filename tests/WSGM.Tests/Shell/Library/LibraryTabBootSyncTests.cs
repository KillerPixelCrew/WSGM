using SteamUiToolkit;
using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class LibraryTabBootSyncTests
{
    [Fact]
    public async Task ANewReadySharedContextGenerationRunsAPass()
    {
        // The 3.0.0 audit: on the desktop the readiness gate opened before Steam had a UI context, the one
        // pass found no target, and nothing ran another one once the context existed.
        using var lifetime = new CancellationTokenSource();
        var transport = new GenerationTransport();
        var passes = 0;
        var ran = new SemaphoreSlim(0);
        var sync = new LibraryTabBootSync(() =>
        {
            Interlocked.Increment(ref passes);
            ran.Release();
            return null;
        }, new SteamUiReadiness(), () => false, lifetime.Token);
        using var follow = sync.Follow(transport, () => true);

        transport.Raise(SteamUiTargetRole.SharedJsContext, SteamUiTransportHealth.Ready);
        Assert.True(await ran.WaitAsync(TimeSpan.FromSeconds(5)));
        // Steam replaced its UI: the tabs went with the old document, so they are installed again.
        transport.Raise(SteamUiTargetRole.SharedJsContext, SteamUiTransportHealth.Ready);
        Assert.True(await ran.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(2, Volatile.Read(ref passes));
        await lifetime.CancelAsync();
        await sync.Completion;
    }

    [Fact]
    public void OtherTargetsUnreadyContextsRefusedWorkAndAnEndedFollowRunNoPass()
    {
        using var lifetime = new CancellationTokenSource();
        var transport = new GenerationTransport();
        var admitted = false;
        var sync = new LibraryTabBootSync(() => null, new SteamUiReadiness(), () => false, lifetime.Token);
        var follow = sync.Follow(transport, () => admitted);

        transport.Raise(SteamUiTargetRole.MainWindow, SteamUiTransportHealth.Ready);
        transport.Raise(SteamUiTargetRole.SharedJsContext, SteamUiTransportHealth.Connecting);
        transport.Raise(SteamUiTargetRole.SharedJsContext, SteamUiTransportHealth.Ready);
        admitted = true;
        follow.Dispose();
        transport.Raise(SteamUiTargetRole.SharedJsContext, SteamUiTransportHealth.Ready);

        // No request ever started the worker.
        Assert.True(sync.Completion.IsCompleted);
        Assert.Equal(0, transport.Subscribers);
    }

    private sealed class GenerationTransport : ISteamUiTransport
    {
        private long _generation;

        internal int Subscribers => GenerationChanged?.GetInvocationList().Length ?? 0;

        public event EventHandler<SteamUiNotification>? NotificationReceived
        {
            add { }
            remove { }
        }

        public event EventHandler<SteamUiTransportSnapshot>? GenerationChanged;

        public ValueTask<IAsyncDisposable> SubscribeAsync(SteamUiTargetRole role,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<SteamUiEvaluationResult> EvaluateAsync(SteamUiTargetRole role, string expression,
            TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task SetRuntimeBindingAsync(SteamUiTargetRole role, string bindingName, bool installed,
            TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public IReadOnlyList<SteamUiTransportSnapshot> GetSnapshots()
        {
            return [];
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        internal void Raise(SteamUiTargetRole role, SteamUiTransportHealth health)
        {
            var generation = Interlocked.Increment(ref _generation);
            GenerationChanged?.Invoke(this, new SteamUiTransportSnapshot(role, health,
                new SteamUiGenerations(generation, generation, generation, generation, generation, generation),
                "target", null, 0, 1));
        }
    }
}
