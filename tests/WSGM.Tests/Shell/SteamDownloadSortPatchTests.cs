using System.Text.Json;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Tests.Shell;

public sealed class SteamDownloadSortPatchTests
{
    [Fact]
    public async Task DownloadSortUsesSharedContextPatchLifecycle()
    {
        await using var transport = new DownloadSortTransport();
        await using var manager = new SteamUiPatchManager(transport);
        manager.Register(SteamDownloadSort.Patch);

        await manager.SynchronizeAsync();
        var installed = Assert.Single(manager.GetSnapshots());
        Assert.True(
            installed.State == SteamUiPatchState.Verified,
            $"Download sort state was {installed.State}: {installed.LastFailure}");

        await manager.SetPatchEnabledAsync("wsgm.download-sort", false);

        var removed = Assert.Single(manager.GetSnapshots());
        Assert.Equal(SteamUiPatchState.Disabled, removed.State);
        Assert.True(transport.Removed);
    }

    [Theory]
    [InlineData("{\"refused\":2,\"total\":3,\"first\":\"index refused\"}", true)]
    [InlineData("{\"refused\":1,\"total\":1,\"first\":\"\"}", true)]
    [InlineData("{\"refused\":4,\"total\":3,\"first\":\"x\"}", false)]
    [InlineData("{\"refused\":0,\"total\":3,\"first\":\"x\"}", false)]
    [InlineData("{\"refused\":1,\"total\":3}", false)]
    [InlineData("{\"refused\":1,\"total\":3,\"first\":\"x\",\"extra\":1}", false)]
    public void RefusedReportReadsOnlyAFinishedRunsCount(string json, bool accepted)
    {
        using var payload = JsonDocument.Parse(json);

        Assert.Equal(accepted, SteamDownloadSort.TryReadRefused(payload.RootElement, out _));
    }

    [Fact]
    public void TheGateReportsThroughTheCommandItsModuleDeclares()
    {
        var command = Assert.Single(SteamDownloadSort.Module().Commands);
        var source = SteamUiAssetCatalog.LoadNativeQamBootstrap().Source;
        var start = source.IndexOf("function createWsgmDownloadSort()", StringComparison.Ordinal);
        Assert.InRange(start, 0, source.Length - 1);
        var end = source.IndexOf("registerGate(\"wsgmDownloadSort\", createWsgmDownloadSort());", start,
            StringComparison.Ordinal);
        Assert.InRange(end, start + 1, source.Length - 1);
        var gate = source[start..end];

        Assert.Equal(SteamDownloadSort.PatchId, command.PatchId);
        Assert.Contains($"const patchId = \"{command.PatchId}\";", gate, StringComparison.Ordinal);
        Assert.Contains($"request(patchId, \"{command.Command}\",", gate, StringComparison.Ordinal);
    }

    private sealed class DownloadSortTransport : ISteamUiTransport
    {
        internal bool Removed { get; private set; }

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
            SteamUiTargetRole role,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<IAsyncDisposable>(new Lease());
        }

        public Task<SteamUiEvaluationResult> EvaluateAsync(
            SteamUiTargetRole role,
            string expression,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            string value;
            if (expression.Contains("bridge.remove()", StringComparison.Ordinal))
            {
                Removed = true;
                value = "{\"ok\":true}";
            }
            else if (expression.Contains("runtime:!!window.webpackChunksteamui", StringComparison.Ordinal))
            {
                value = "{\"ok\":true,\"runtime\":true}";
            }
            else
            {
                value = "{\"ok\":true}";
            }

            return Task.FromResult(new SteamUiEvaluationResult(
                SteamUiDispatch.Answered,
                value,
                null,
                new SteamUiGenerations(1, 1, 1, 1, 1, 1)));
        }

        public Task SetRuntimeBindingAsync(
            SteamUiTargetRole role,
            string bindingName,
            bool installed,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public IReadOnlyList<SteamUiTransportSnapshot> GetSnapshots()
        {
            return
            [
                new SteamUiTransportSnapshot(
                    SteamUiTargetRole.SharedJsContext,
                    SteamUiTransportHealth.Ready,
                    new SteamUiGenerations(1, 1, 1, 1, 1, 1),
                    "fixture-target",
                    null,
                    0,
                    1)
            ];
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        private sealed class Lease : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                return ValueTask.CompletedTask;
            }
        }
    }
}
