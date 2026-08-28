using System.Text;
using System.Text.Json;
using WSGM.Core;

namespace WSGM.Tests;

public sealed class SteamUiTargetPolicyTests
{
    [Fact]
    public void SharedContextRequiresSteamLoopbackHttpsPage()
    {
        Assert.True(SteamUiEndpointDiscovery.MatchesTarget(
            SteamUiTargetRole.SharedJsContext,
            "page",
            "SharedJSContext",
            "https://steamloopback.host/index.html?PLATFORM=windows"));
        Assert.False(SteamUiEndpointDiscovery.MatchesTarget(
            SteamUiTargetRole.SharedJsContext,
            "page",
            "SharedJSContext",
            "https://example.test/index.html"));
        Assert.False(SteamUiEndpointDiscovery.MatchesTarget(
            SteamUiTargetRole.SharedJsContext,
            "worker",
            "SharedJSContext",
            "https://steamloopback.host/index.html"));
    }

    [Fact]
    public void QuickAccessRequiresExactControllerPopupShape()
    {
        const string url =
            "about:blank?createflags=1&browserviewpopup=1&openerid=3";
        Assert.True(SteamUiEndpointDiscovery.MatchesTarget(
            SteamUiTargetRole.QuickAccess, "page", "QuickAccess", url));
        Assert.False(SteamUiEndpointDiscovery.MatchesTarget(
            SteamUiTargetRole.QuickAccess, "page", "MainMenu", url));
        Assert.False(SteamUiEndpointDiscovery.MatchesTarget(
            SteamUiTargetRole.QuickAccess,
            "page",
            "QuickAccess",
            "about:blank?createflags=1&openerid=3"));
    }
}

public sealed class SteamUiBridgeAuthorizerTests
{
    private static readonly SteamUiGenerations Generations = new(1, 2, 3, 4, 5, 6);

    [Fact]
    public void AcceptsOnlyCurrentAllowlistedCommandOnce()
    {
        var authorizer = new SteamUiBridgeAuthorizer(Generations);
        var request = Request("wsgm.native-qam.tdp", "setPrimaryLimit", 1, 10);

        Assert.True(authorizer.Authorize(request).Accepted);
        Assert.False(authorizer.Authorize(request).Accepted);
        Assert.False(authorizer.Authorize(
            Request("wsgm.native-qam.tdp", "readRawWmi", 2, 11)).Accepted);
    }

    [Fact]
    public void RejectsStaleGenerationAndActionReplay()
    {
        var authorizer = new SteamUiBridgeAuthorizer(Generations);
        Assert.True(authorizer.Authorize(
            Request("wsgm.native-qam.frame-limit", "setFrameLimit", 1, 20)).Accepted);
        Assert.False(authorizer.Authorize(
            Request("wsgm.native-qam.frame-limit", "setFrameLimit", 2, 20)).Accepted);
        Assert.False(authorizer.Authorize(
            Request("wsgm.native-qam.frame-limit", "setFrameLimit", 3, 21) with
            {
                ContextGeneration = 99,
            }).Accepted);
    }

    [Fact]
    public void CancellationMustReferenceAcceptedSequence()
    {
        var authorizer = new SteamUiBridgeAuthorizer(Generations);
        Assert.False(authorizer.Authorize(
            Request("wsgm.native-qam.overlay-level", "setOverlayLevel", 5, 30) with
            {
                Type = "cancel",
            }).Accepted);
        Assert.True(authorizer.Authorize(
            Request("wsgm.native-qam.overlay-level", "setOverlayLevel", 5, 30)).Accepted);
        Assert.True(authorizer.Authorize(
            Request("wsgm.native-qam.overlay-level", "setOverlayLevel", 5, 30) with
            {
                Type = "cancel",
            }).Accepted);
    }

    private static SteamUiBridgeRequest Request(
        string patchId, string command, long sequence, long actionGeneration)
    {
        using var document = JsonDocument.Parse("{\"value\":15}");
        return new SteamUiBridgeRequest(
            SteamUiBridgeHost.SchemaVersion,
            "request",
            patchId,
            command,
            sequence,
            actionGeneration,
            Generations.ExecutionContext,
            Generations.Document,
            document.RootElement.Clone());
    }
}

public sealed class SteamUiAssetTests
{
    [Fact]
    public void NativeQamBootstrapIsHashLockedAndHasNoBroadRuntimeAuthority()
    {
        var source = SteamUiAssetCatalog.LoadNativeQamBootstrap();

        Assert.Contains("__WSGM_CONFIGURATION_JSON__", source, StringComparison.Ordinal);
        Assert.DoesNotContain("eval(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("fetch(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("WebSocket", source, StringComparison.Ordinal);
        Assert.DoesNotContain("filesystem", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("performanceProfile", source, StringComparison.Ordinal);
    }
}

public sealed class SteamUiCdpConnectionTests
{
    private static readonly SteamUiEndpoint Endpoint = new(
        "browser-1",
        "target-1",
        SteamUiTargetRole.SharedJsContext,
        new Uri("ws://127.0.0.1:8080/devtools/page/target-1"),
        "page",
        "SharedJSContext",
        "https://steamloopback.host/index.html");

    [Fact]
    public async Task EvaluationIgnoresOrphanAndCompletesMatchingRequest()
    {
        var wire = new FakeWire();
        wire.Sent = request =>
        {
            using var document = JsonDocument.Parse(request);
            var id = document.RootElement.GetProperty("id").GetInt32();
            wire.Enqueue("{\"id\":999,\"result\":{}}"u8.ToArray());
            wire.Enqueue(Encoding.UTF8.GetBytes(
                $"{{\"id\":{id},\"result\":{{\"result\":{{\"type\":\"string\",\"value\":\"ok\"}}}}}}"));
        };
        await using var connection = new SteamUiCdpConnection(
            Endpoint, wire, (_, _) => { }, (_, _) => { });
        connection.Start();

        var value = await connection.EvaluateAsync(
            "JSON.stringify({ok:true})", TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Equal("ok", value);
    }

    [Fact]
    public async Task MalformedFrameFaultsPendingRequestAndChannel()
    {
        var wire = new FakeWire();
        wire.Sent = _ => wire.Enqueue("[]"u8.ToArray());
        Exception? closed = null;
        await using var connection = new SteamUiCdpConnection(
            Endpoint, wire, (_, _) => { }, (_, error) => closed = error);
        connection.Start();

        await Assert.ThrowsAnyAsync<Exception>(() => connection.EvaluateAsync(
            "'x'", TimeSpan.FromSeconds(1), CancellationToken.None));
        await connection.Completion.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.IsType<InvalidDataException>(closed);
    }

    [Fact]
    public async Task CallerCancellationDoesNotPoisonPersistentChannel()
    {
        var wire = new FakeWire();
        var sends = 0;
        wire.Sent = request =>
        {
            sends++;
            if (sends == 1)
            {
                return;
            }
            using var document = JsonDocument.Parse(request);
            var id = document.RootElement.GetProperty("id").GetInt32();
            wire.Enqueue(Encoding.UTF8.GetBytes(
                $"{{\"id\":{id},\"result\":{{\"result\":{{\"type\":\"string\",\"value\":\"second\"}}}}}}"));
        };
        await using var connection = new SteamUiCdpConnection(
            Endpoint, wire, (_, _) => { }, (_, _) => { });
        connection.Start();
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.EvaluateAsync(
            "'first'", TimeSpan.FromSeconds(1), cancellation.Token));
        var second = await connection.EvaluateAsync(
            "'second'", TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Equal("second", second);
    }

    private sealed class FakeWire : ISteamUiCdpWire
    {
        private readonly Queue<byte[]> _messages = new();
        private readonly SemaphoreSlim _available = new(0);

        internal Action<byte[]>? Sent { get; set; }

        internal void Enqueue(byte[] message)
        {
            lock (_messages)
            {
                _messages.Enqueue(message);
            }
            _available.Release();
        }

        public Task SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Sent?.Invoke(message.ToArray());
            return Task.CompletedTask;
        }

        public async Task<byte[]?> ReceiveAsync(CancellationToken cancellationToken)
        {
            await _available.WaitAsync(cancellationToken);
            lock (_messages)
            {
                return _messages.Dequeue();
            }
        }

        public ValueTask DisposeAsync()
        {
            _available.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

public sealed class SteamUiPatchManagerTests
{
    [Fact]
    public async Task PatchFailureDoesNotBlockIndependentPatch()
    {
        await using var transport = new FakeTransport();
        await using var manager = new SteamUiPatchManager(transport);
        var broken = new FakePatch("broken", "dom-a") { ThrowOnApply = true };
        var healthy = new FakePatch("healthy", "dom-b");
        manager.Register(broken);
        manager.Register(healthy);

        await manager.SynchronizeAsync();
        var snapshots = manager.GetSnapshots().ToDictionary(snapshot => snapshot.Id);

        Assert.Equal(SteamUiPatchState.Degraded, snapshots["broken"].State);
        Assert.Equal(SteamUiPatchState.Verified, snapshots["healthy"].State);
    }

    [Fact]
    public async Task IndividualKillSwitchRemovesOnlyOwnedPatch()
    {
        await using var transport = new FakeTransport();
        await using var manager = new SteamUiPatchManager(transport);
        var first = new FakePatch("first", "dom-a");
        var second = new FakePatch("second", "dom-b");
        manager.Register(first);
        manager.Register(second);
        await manager.SynchronizeAsync();

        manager.SetPatchEnabled("first", false);
        await manager.SynchronizeAsync();

        Assert.Equal(1, first.RemoveCount);
        Assert.Equal(0, second.RemoveCount);
        Assert.Equal(SteamUiPatchState.Disabled,
            manager.GetSnapshots().Single(snapshot => snapshot.Id == "first").State);
        Assert.Equal(SteamUiPatchState.Verified,
            manager.GetSnapshots().Single(snapshot => snapshot.Id == "second").State);
    }

    private sealed class FakeTransport : ISteamUiTransport
    {
        public event EventHandler<SteamUiNotification>? NotificationReceived;

        public event EventHandler<SteamUiTransportSnapshot>? GenerationChanged;

        public ValueTask<IAsyncDisposable> SubscribeAsync(
            SteamUiTargetRole role, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IAsyncDisposable>(new Lease());

        public Task<SteamUiEvaluationResult> EvaluateAsync(
            SteamUiTargetRole role,
            string expression,
            TimeSpan timeout,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SteamUiEvaluationResult(true, "{}", null, new(1, 1, 1, 1, 1, 1)));

        public IReadOnlyList<SteamUiTransportSnapshot> GetSnapshots() =>
        [
            new(
                SteamUiTargetRole.SharedJsContext,
                SteamUiTransportHealth.Ready,
                new(1, 1, 1, 1, 1, 1),
                "target",
                null,
                0,
                0),
        ];

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class Lease : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class FakePatch(string id, string resource) : ISteamUiPatch
    {
        public string Id { get; } = id;

        public int Version => 1;

        public SteamUiTargetRole TargetRole => SteamUiTargetRole.SharedJsContext;

        public string ResourceKey { get; } = resource;

        public SteamUiPatchBounds Bounds => SteamUiPatchBounds.Default;

        internal bool ThrowOnApply { get; init; }

        internal int RemoveCount { get; private set; }

        public Task<SteamUiPatchProbeResult> ProbeAsync(
            SteamUiPatchContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new SteamUiPatchProbeResult(
                true, true, true, "fixture-v1", null));

        public Task<SteamUiPatchOperationResult> ApplyAsync(
            SteamUiPatchContext context, CancellationToken cancellationToken) =>
            ThrowOnApply
                ? throw new InvalidOperationException("fixture apply failure")
                : Task.FromResult(new SteamUiPatchOperationResult(true, null));

        public Task<SteamUiPatchOperationResult> VerifyAsync(
            SteamUiPatchContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new SteamUiPatchOperationResult(true, null));

        public Task<SteamUiPatchOperationResult> RemoveAsync(
            SteamUiPatchContext context, CancellationToken cancellationToken)
        {
            RemoveCount++;
            return Task.FromResult(new SteamUiPatchOperationResult(true, null));
        }
    }
}
