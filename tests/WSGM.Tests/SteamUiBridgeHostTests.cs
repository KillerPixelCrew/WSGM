using System.Text.Json;
using WSGM.Core;

namespace WSGM.Tests;

public sealed class SteamUiBridgeHostTests
{
    // The bridge substitutes the configuration into whatever it is given and evaluates it; these
    // tests exercise the envelope handling around that, not the script, so the smallest asset that
    // still carries the placeholder is the honest fixture.
    private static readonly SteamUiInjectedAsset TestAsset =
        new("(()=>{return __WSGM_CONFIGURATION_JSON__;})()", "TESTASSETHASH");

    [Fact]
    public async Task CurrentRequestIsDeliveredOnceAndReplayIsRejected()
    {
        await using var transport = new BridgeTransport();
        await using var host = new SteamUiBridgeHost(transport, TestAsset);
        var received = new List<SteamUiBridgeRequest>();
        host.RequestReceived += (_, request) => received.Add(request);
        Assert.True(await host.BootstrapAsync());

        string request = RequestJson(transport.Generations, sequence: 1, actionGeneration: 1);
        transport.EmitBindingPayload(request);
        transport.EmitBindingPayload(request);
        transport.EmitBindingPayload(RequestJson(
            transport.Generations,
            sequence: 2,
            actionGeneration: 2));

        Assert.Equal([1L, 2L], received.Select(item => item.Sequence));
    }

    [Fact]
    public async Task MalformedAndNonBindingNotificationsNeverReachTheRouter()
    {
        await using var transport = new BridgeTransport();
        await using var host = new SteamUiBridgeHost(transport, TestAsset);
        var received = 0;
        host.RequestReceived += (_, _) => received++;
        Assert.True(await host.BootstrapAsync());

        transport.EmitRawParameters("[");
        transport.EmitBindingPayload("{");
        transport.EmitRawParameters("{\"name\":\"somebody-elses-binding\",\"payload\":\"{}\"}");
        transport.EmitRawParameters("{\"name\":\"__wsgmNativeBridge_v1_7b24d11c\"}");
        transport.EmitBindingPayload(new string('x', SteamUiBridgeHost.MaximumPayloadCharacters + 1));

        Assert.Equal(0, received);
    }

    [Fact]
    public async Task GenerationReplacementSuppressesTrafficUntilTheBridgeIsBootstrappedAgain()
    {
        await using var transport = new BridgeTransport();
        await using var host = new SteamUiBridgeHost(transport, TestAsset);
        var received = 0;
        host.RequestReceived += (_, _) => received++;
        Assert.True(await host.BootstrapAsync());

        SteamUiGenerations previous = transport.Generations;
        transport.AdvanceDocumentGeneration();
        int evaluationsAfterReplacement = transport.Expressions.Count;

        Assert.False(host.IsReady);
        Assert.False(await host.PublishStateAsync(
            "wsgm.native-qam.tdp",
            Json("{\"watts\":15}")));
        Assert.False(await host.RespondAsync(
            Request(previous, sequence: 1, actionGeneration: 1),
            ok: true,
            payload: null,
            error: null));
        transport.EmitBindingPayload(RequestJson(
            previous,
            sequence: 1,
            actionGeneration: 1),
            previous);
        transport.EmitBindingPayload(RequestJson(
            transport.Generations,
            sequence: 1,
            actionGeneration: 1));

        Assert.Equal(evaluationsAfterReplacement, transport.Expressions.Count);
        Assert.Equal(0, received);

        Assert.True(await host.BootstrapAsync());
        transport.EmitBindingPayload(RequestJson(
            transport.Generations,
            sequence: 1,
            actionGeneration: 1));

        Assert.Equal(1, received);
    }

    [Fact]
    public async Task StateAndResponsesRequireAReadyBridgeAndAnAllowlistedStateIdentity()
    {
        await using var transport = new BridgeTransport();
        await using var host = new SteamUiBridgeHost(transport, TestAsset);
        SteamUiBridgeRequest request = Request(
            transport.Generations,
            sequence: 1,
            actionGeneration: 1);

        Assert.False(await host.PublishStateAsync("wsgm.native-qam.tdp", Json("{}")));
        Assert.False(await host.RespondAsync(request, true, null, null));
        Assert.Empty(transport.Expressions);

        Assert.True(await host.BootstrapAsync());
        int afterBootstrap = transport.Expressions.Count;
        Assert.False(await host.PublishStateAsync("not.allowlisted", Json("{}")));
        Assert.False(await host.PublishStateAsync(
            "wsgm.native-qam.tdp",
            Json("{\"value\":\"" + new string('x', SteamUiBridgeHost.MaximumPayloadCharacters)
                + "\"}")));
        Assert.Equal(afterBootstrap, transport.Expressions.Count);

        Assert.True(await host.PublishStateAsync(
            "wsgm.native-qam.tdp",
            Json("{\"watts\":15}")));
        Assert.True(await host.RespondAsync(request, false, null, "refused"));
        Assert.Equal(afterBootstrap + 2, transport.Expressions.Count);
        Assert.All(
            transport.Expressions.Skip(afterBootstrap),
            expression => Assert.Contains("deliver", expression, StringComparison.Ordinal));
    }

    [Fact]
    public async Task DisposalRetractsTheBindingAndDetachesNotifications()
    {
        await using var transport = new BridgeTransport();
        var host = new SteamUiBridgeHost(transport, TestAsset);
        var received = 0;
        host.RequestReceived += (_, _) => received++;
        Assert.True(await host.BootstrapAsync());

        await host.DisposeAsync();
        await host.DisposeAsync();
        transport.EmitBindingPayload(RequestJson(
            transport.Generations,
            sequence: 1,
            actionGeneration: 1));

        Assert.Equal([true, false], transport.BindingStates);
        Assert.Equal(0, received);
        Assert.False(host.IsReady);
    }

    private static SteamUiBridgeRequest Request(
        SteamUiGenerations generations,
        long sequence,
        long actionGeneration) => new(
            SteamUiBridgeHost.SchemaVersion,
            "request",
            "wsgm.native-qam.tdp",
            "setPrimaryLimit",
            sequence,
            actionGeneration,
            generations.ExecutionContext,
            generations.Document,
            Json("{\"watts\":15,\"enabled\":true}"));

    private static string RequestJson(
        SteamUiGenerations generations,
        long sequence,
        long actionGeneration) => JsonSerializer.Serialize(new
        {
            version = SteamUiBridgeHost.SchemaVersion,
            type = "request",
            patchId = "wsgm.native-qam.tdp",
            command = "setPrimaryLimit",
            sequence,
            actionGeneration,
            contextGeneration = generations.ExecutionContext,
            documentGeneration = generations.Document,
            payload = new { watts = 15, enabled = true },
        });

    private static JsonElement Json(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class BridgeTransport : ISteamUiTransport
    {
        private EventHandler<SteamUiNotification>? _notificationReceived;
        private EventHandler<SteamUiTransportSnapshot>? _generationChanged;

        internal SteamUiGenerations Generations { get; private set; } = new(1, 1, 1, 1, 1, 1);

        internal List<string> Expressions { get; } = [];

        internal List<bool> BindingStates { get; } = [];

        public event EventHandler<SteamUiNotification>? NotificationReceived
        {
            add => _notificationReceived += value;
            remove => _notificationReceived -= value;
        }

        public event EventHandler<SteamUiTransportSnapshot>? GenerationChanged
        {
            add => _generationChanged += value;
            remove => _generationChanged -= value;
        }

        public ValueTask<IAsyncDisposable> SubscribeAsync(
            SteamUiTargetRole role,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IAsyncDisposable>(new Lease());
        }

        public Task<SteamUiEvaluationResult> EvaluateAsync(
            SteamUiTargetRole role,
            string expression,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Expressions.Add(expression);
            return Task.FromResult(new SteamUiEvaluationResult(
                true,
                "{\"ok\":true}",
                null,
                Generations));
        }

        public Task SetRuntimeBindingAsync(
            SteamUiTargetRole role,
            string bindingName,
            bool installed,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BindingStates.Add(installed);
            return Task.CompletedTask;
        }

        public IReadOnlyList<SteamUiTransportSnapshot> GetSnapshots() => [Snapshot()];

        internal void AdvanceDocumentGeneration()
        {
            Generations = Generations with
            {
                ExecutionContext = Generations.ExecutionContext + 1,
                Document = Generations.Document + 1,
            };
            _generationChanged?.Invoke(this, Snapshot());
        }

        internal void EmitBindingPayload(
            string payload,
            SteamUiGenerations? generations = null) => EmitRawParameters(
                JsonSerializer.Serialize(new
                {
                    name = "__wsgmNativeBridge_v1_7b24d11c",
                    payload,
                }),
                generations);

        internal void EmitRawParameters(
            string parameters,
            SteamUiGenerations? generations = null) => _notificationReceived?.Invoke(
                this,
                new SteamUiNotification(
                    SteamUiTargetRole.SharedJsContext,
                    "Runtime.bindingCalled",
                    parameters,
                    generations ?? Generations));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private SteamUiTransportSnapshot Snapshot() => new(
            SteamUiTargetRole.SharedJsContext,
            SteamUiTransportHealth.Ready,
            Generations,
            "fixture-shared",
            null,
            0,
            1);

        private sealed class Lease : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
