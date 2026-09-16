using WSGM.Core;

namespace WSGM.Tests;

public sealed class SteamDownloadSortPatchTests
{
    [Fact]
    public async Task DownloadSortUsesSharedContextPatchLifecycle()
    {
        await using var transport = new DownloadSortTransport();
        await using var manager = new SteamUiPatchManager(transport);
        manager.Register(new SteamDownloadSortPatch());

        await manager.SynchronizeAsync();
        var installed = Assert.Single(manager.GetSnapshots());
        Assert.True(
            installed.State == SteamUiPatchState.Verified,
            $"Download sort state was {installed.State}: {installed.LastFailure}");

        manager.SetPatchEnabled("wsgm.download-sort", false);
        await manager.SynchronizeAsync();

        var removed = Assert.Single(manager.GetSnapshots());
        Assert.Equal(SteamUiPatchState.Disabled, removed.State);
        Assert.True(transport.Removed);
    }

    private sealed class DownloadSortTransport : ISteamUiTransport
    {
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

        internal bool Removed { get; private set; }

        public ValueTask<IAsyncDisposable> SubscribeAsync(
            SteamUiTargetRole role,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IAsyncDisposable>(new Lease());

        public Task<SteamUiEvaluationResult> EvaluateAsync(
            SteamUiTargetRole role,
            string expression,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            string value;
            if (expression.Contains("dlSortRemove", StringComparison.Ordinal))
            {
                Removed = true;
                value = "{\"ok\":true}";
            }
            else if (expression.Contains("dlSortPatched", StringComparison.Ordinal)
                || expression.Contains("runtime:!!window.webpackChunksteamui", StringComparison.Ordinal))
            {
                value = "{\"ok\":true,\"runtime\":true,\"owned\":false}";
            }
            else
            {
                value = "{\"ok\":true}";
            }

            return Task.FromResult(new SteamUiEvaluationResult(
                true,
                value,
                null,
                new SteamUiGenerations(1, 1, 1, 1, 1, 1)));
        }

        public Task SetRuntimeBindingAsync(
            SteamUiTargetRole role,
            string bindingName,
            bool installed,
            TimeSpan timeout,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public IReadOnlyList<SteamUiTransportSnapshot> GetSnapshots() =>
        [
            new(
                SteamUiTargetRole.SharedJsContext,
                SteamUiTransportHealth.Ready,
                new SteamUiGenerations(1, 1, 1, 1, 1, 1),
                "fixture-target",
                null,
                0,
                1)
        ];

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class Lease : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
