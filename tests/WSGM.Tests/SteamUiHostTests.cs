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
    public void MainWindowIsTheTopLevelWindowRatherThanAnyPopup()
    {
        // Every URL here is verbatim from /json/list on the reference Claw in game mode. Two things
        // this pins: the title is localized, so it cannot be matched on; and the URL reported by CDP
        // is the one the target was CREATED with, not the document address it later navigated to —
        // matching "https://steamloopback.host/index.html" found nothing, and the glyph stylesheet
        // reported "MainWindow target is absent" while the window was plainly open.
        Assert.True(SteamUiEndpointDiscovery.MatchesTarget(
            SteamUiTargetRole.MainWindow,
            "page",
            "Big-Picture-Modus",
            "about:blank?createflags=6292754&minwidth=853&minheight=534&pid=0&browser=-1&browserType="));

        // A browser-view popup: Quick Access, the main menu, notification toasts.
        Assert.False(SteamUiEndpointDiscovery.MatchesTarget(
            SteamUiTargetRole.MainWindow,
            "page",
            "QuickAccess_uid21",
            "about:blank?browserviewpopup=1&requestid=6&parentpopup=21"));

        // A context menu: created like a window but owned by one, and with no minimum size.
        Assert.False(SteamUiEndpointDiscovery.MatchesTarget(
            SteamUiTargetRole.MainWindow,
            "page",
            "Menu",
            "about:blank?createflags=5529866&pid=0&browser=-1&openerid=21"));

        // SharedJSContext is a different role and holds almost no DOM.
        Assert.False(SteamUiEndpointDiscovery.MatchesTarget(
            SteamUiTargetRole.MainWindow,
            "page",
            "SharedJSContext",
            "https://steamloopback.host/routes/app/489830/controllerconfigurator/summary"));
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

    [Fact]
    public void NativeQamComponentsUseValveFieldsWithoutPlatformOrDeviceSpoofing()
    {
        var source = SteamUiAssetCatalog.LoadNativeQamBootstrap();

        Assert.Contains("DialogSlider_Container", source, StringComparison.Ordinal);
        Assert.Contains("DropDownField", source, StringComparison.Ordinal);
        Assert.Contains("PanelSectionRow", source, StringComparison.Ordinal);
        Assert.Contains("LocalizeString", source, StringComparison.Ordinal);
        Assert.Contains("wsgm.native-qam.tdp", source, StringComparison.Ordinal);
        Assert.Contains("wsgm.native-qam.frame-limit", source, StringComparison.Ordinal);
        Assert.Contains("wsgm.native-qam.overlay-level", source, StringComparison.Ordinal);
        Assert.Contains("wsgm.native-qam.controller-target", source, StringComparison.Ordinal);
        Assert.Contains("setPrimaryLimit", source, StringComparison.Ordinal);
        Assert.Contains("setFrameLimit", source, StringComparison.Ordinal);
        Assert.Contains("setOverlayLevel", source, StringComparison.Ordinal);
        Assert.Contains("setControllerTarget", source, StringComparison.Ordinal);
        Assert.Contains("persistence: \"automatic\"", source, StringComparison.Ordinal);
        Assert.Contains("latestStates.set(envelope.patchId, envelope.payload)", source,
            StringComparison.Ordinal);
        Assert.Contains("callback(latestStates.get(patchId))", source, StringComparison.Ordinal);
        Assert.DoesNotContain("force_deck_perf_tab", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IS_STEAMOS =", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PLATFORM =", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SteamClient.SteamOSManager", source, StringComparison.Ordinal);
    }
}

public sealed class SteamUiPatchLifecycleTests
{
    [Theory]
    [InlineData("""{"ok":true,"rowClass":true,"logoClass":true}""", true)]
    [InlineData("""{"ok":true,"rowClass":false,"logoClass":true}""", false)]
    [InlineData("""{"ok":true,"rowClass":true,"logoClass":false}""", false)]
    [InlineData("""{"ok":true}""", false)]
    [InlineData("""{"ok":false,"rowClass":true,"logoClass":true}""", false)]
    public void ARequiredStructuralFlagIsPartOfCompatibilityRatherThanDecoration(
        string probe,
        bool expected)
    {
        // A probe that reports its own structural findings has to have them read. The glyph-style
        // probe returned whether each build-coupled selector class still exists while only "ok" —
        // which is !!document.head — decided compatibility, so a Steam build that renamed one was
        // still called compatible and the patch installed rules matching nothing.
        Assert.Equal(
            expected,
            SteamUiPatchEvaluation.IsSuccessful(probe, "rowClass", "logoClass"));
    }

    [Fact]
    public async Task AnAppliedPatchThatDoesNotVerifyIsRemovedRatherThanLeftInTheClient()
    {
        await using SteamUiPatchManager manager = new(new SilentTransport());
        RecordingPatch patch = new() { VerifySucceeds = false };
        manager.Register(patch);

        await manager.SynchronizeAsync();

        SteamUiPatchSnapshot snapshot = Assert.Single(manager.GetSnapshots());
        Assert.Equal(SteamUiPatchState.Degraded, snapshot.State);
        Assert.Equal(1, patch.RemoveCalls);
    }

    [Fact]
    public async Task APatchWhoseRemovalAlsoFailsReportsRemoveFailed()
    {
        await using SteamUiPatchManager manager = new(new SilentTransport());
        RecordingPatch patch = new() { VerifySucceeds = false, RemoveSucceeds = false };
        manager.Register(patch);

        await manager.SynchronizeAsync();

        SteamUiPatchSnapshot snapshot = Assert.Single(manager.GetSnapshots());
        Assert.Equal(SteamUiPatchState.RemoveFailed, snapshot.State);
    }

    [Fact]
    public async Task EveryPhaseGetsItsOwnDeclaredBudget()
    {
        // The bound is documented as the maximum duration of one phase. Sharing one source across
        // probe, apply and verify let a slow client spend most of it probing and have its otherwise
        // in-budget apply cancelled underneath it.
        await using SteamUiPatchManager manager = new(new SilentTransport());
        RecordingPatch patch = new()
        {
            Bounds = new SteamUiPatchBounds(TimeSpan.FromMilliseconds(400), 4096, 512),
            PhaseDelay = TimeSpan.FromMilliseconds(250),
        };
        manager.Register(patch);

        await manager.SynchronizeAsync();

        SteamUiPatchSnapshot snapshot = Assert.Single(manager.GetSnapshots());
        Assert.Equal(SteamUiPatchState.Verified, snapshot.State);
    }

    /// <summary>A patch whose phases are scripted, with no Steam and no evaluation.</summary>
    private sealed class RecordingPatch : ISteamUiPatch
    {
        public string Id => "wsgm-test-patch";

        public int Version => 1;

        public SteamUiTargetRole TargetRole => SteamUiTargetRole.SharedJsContext;

        public string ResourceKey => "test";

        public SteamUiPatchBounds Bounds { get; init; } = SteamUiPatchBounds.Default;

        internal bool VerifySucceeds { get; init; } = true;

        internal bool RemoveSucceeds { get; init; } = true;

        internal TimeSpan PhaseDelay { get; init; }

        internal int RemoveCalls { get; private set; }

        public async Task<SteamUiPatchProbeResult> ProbeAsync(
            SteamUiPatchContext context,
            CancellationToken cancellationToken)
        {
            await DelayAsync(cancellationToken);
            return new SteamUiPatchProbeResult(true, true, true, "fingerprint", null);
        }

        public async Task<SteamUiPatchOperationResult> ApplyAsync(
            SteamUiPatchContext context,
            CancellationToken cancellationToken)
        {
            await DelayAsync(cancellationToken);
            return new SteamUiPatchOperationResult(true, null);
        }

        public async Task<SteamUiPatchOperationResult> VerifyAsync(
            SteamUiPatchContext context,
            CancellationToken cancellationToken)
        {
            await DelayAsync(cancellationToken);
            return new SteamUiPatchOperationResult(VerifySucceeds, VerifySucceeds ? null : "no proof");
        }

        public async Task<SteamUiPatchOperationResult> RemoveAsync(
            SteamUiPatchContext context,
            CancellationToken cancellationToken)
        {
            RemoveCalls++;
            await DelayAsync(cancellationToken);
            return new SteamUiPatchOperationResult(
                RemoveSucceeds,
                RemoveSucceeds ? null : "removal unverified");
        }

        private Task DelayAsync(CancellationToken cancellationToken) =>
            PhaseDelay <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(PhaseDelay, cancellationToken);
    }

    /// <summary>A transport that subscribes and reports nothing, for patches that never evaluate.</summary>
    private sealed class SilentTransport : ISteamUiTransport
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

        public ValueTask<IAsyncDisposable> SubscribeAsync(
            SteamUiTargetRole role,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IAsyncDisposable>(new Lease());

        public Task<SteamUiEvaluationResult> EvaluateAsync(
            SteamUiTargetRole role,
            string expression,
            TimeSpan timeout,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(SteamUiEvaluationResult.Unavailable("not evaluated", default));

        public IReadOnlyList<SteamUiTransportSnapshot> GetSnapshots() => [];

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class Lease : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}

public sealed class NativeQamComponentPatchTests
{
    [Fact]
    public async Task TdpPatchRequiresEveryUniqueStructuralMatchBeforeInstall()
    {
        await using var transport = new NativeQamComponentTransport
        {
            TdpAvailabilityCount = 2,
        };
        await using var manager = new SteamUiPatchManager(transport);
        manager.Register(new NativeQamTdpPatch());

        await manager.SynchronizeAsync();

        SteamUiPatchSnapshot snapshot = Assert.Single(manager.GetSnapshots());
        Assert.Equal(SteamUiPatchState.Incompatible, snapshot.State);
        Assert.Equal(0, transport.InstallCount);
    }

    [Fact]
    public async Task PerformancePatchRequiresUniqueNativeActionModuleBeforeInstall()
    {
        await using var transport = new NativeQamComponentTransport
        {
            PerformanceActionsCount = 2,
        };
        await using var manager = new SteamUiPatchManager(transport);
        manager.Register(new NativeQamOverlayLevelPatch());

        await manager.SynchronizeAsync();

        SteamUiPatchSnapshot snapshot = Assert.Single(manager.GetSnapshots());
        Assert.Equal(SteamUiPatchState.Incompatible, snapshot.State);
        Assert.Equal(0, transport.InstallCount);
    }

    [Fact]
    public async Task NativeQamComponentsHaveIndependentVerifiedIdentities()
    {
        await using var transport = new NativeQamComponentTransport();
        await using var manager = new SteamUiPatchManager(transport);
        manager.Register(new NativeQamTdpPatch());
        manager.Register(new NativeQamFrameLimitPatch());
        manager.Register(new NativeQamOverlayLevelPatch());
        manager.Register(new NativeQamControllerTargetPatch());

        await manager.SynchronizeAsync();

        IReadOnlyDictionary<string, SteamUiPatchSnapshot> snapshots = manager.GetSnapshots()
            .ToDictionary(snapshot => snapshot.Id);
        Assert.Equal(SteamUiPatchState.Verified, snapshots["wsgm.native-qam.tdp"].State);
        Assert.Equal(
            SteamUiPatchState.Verified,
            snapshots["wsgm.native-qam.frame-limit"].State);
        Assert.Equal(
            SteamUiPatchState.Verified,
            snapshots["wsgm.native-qam.overlay-level"].State);
        Assert.Equal(
            SteamUiPatchState.Verified,
            snapshots["wsgm.native-qam.controller-target"].State);
        Assert.Equal(4, transport.InstallCount);
        Assert.Equal(4, snapshots.Values.Select(snapshot => snapshot.Fingerprint).Distinct().Count());
    }

    [Fact]
    public async Task DisablingTdpLeavesControllerTargetRegistered()
    {
        await using var transport = new NativeQamComponentTransport();
        await using var manager = new SteamUiPatchManager(transport);
        manager.Register(new NativeQamTdpPatch());
        manager.Register(new NativeQamControllerTargetPatch());
        await manager.SynchronizeAsync();

        manager.SetPatchEnabled("wsgm.native-qam.tdp", false);
        await manager.SynchronizeAsync();

        IReadOnlyDictionary<string, SteamUiPatchSnapshot> snapshots = manager.GetSnapshots()
            .ToDictionary(snapshot => snapshot.Id);
        Assert.Equal(SteamUiPatchState.Disabled, snapshots["wsgm.native-qam.tdp"].State);
        Assert.Equal(
            SteamUiPatchState.Verified,
            snapshots["wsgm.native-qam.controller-target"].State);
        Assert.Contains("tdp", transport.RemovedKinds);
        Assert.DoesNotContain("controllerTarget", transport.RemovedKinds);
    }

    [Fact]
    public async Task DisablingFrameLimitLeavesOverlayLevelRegistered()
    {
        await using var transport = new NativeQamComponentTransport();
        await using var manager = new SteamUiPatchManager(transport);
        manager.Register(new NativeQamFrameLimitPatch());
        manager.Register(new NativeQamOverlayLevelPatch());
        await manager.SynchronizeAsync();

        manager.SetPatchEnabled("wsgm.native-qam.frame-limit", false);
        await manager.SynchronizeAsync();

        IReadOnlyDictionary<string, SteamUiPatchSnapshot> snapshots = manager.GetSnapshots()
            .ToDictionary(snapshot => snapshot.Id);
        Assert.Equal(
            SteamUiPatchState.Disabled,
            snapshots["wsgm.native-qam.frame-limit"].State);
        Assert.Equal(
            SteamUiPatchState.Verified,
            snapshots["wsgm.native-qam.overlay-level"].State);
        Assert.Contains("frameLimit", transport.RemovedKinds);
        Assert.DoesNotContain("overlayLevel", transport.RemovedKinds);
    }

    private sealed class NativeQamComponentTransport : ISteamUiTransport
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

        internal int TdpAvailabilityCount { get; init; } = 1;

        internal int PerformanceActionsCount { get; init; } = 1;

        internal int InstallCount { get; private set; }

        internal List<string> RemovedKinds { get; } = [];

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
            if (expression.Contains("wsgm_native_tdp_probe_", StringComparison.Ordinal))
            {
                value = $$"""
                    {"tdpAvailability":{{TdpAvailabilityCount}},"tdpPresentation":1,"performanceRoot":1,"nativeFields":1,"nativeLayout":1,"localization":1,"react":1}
                    """;
            }
            else if (expression.Contains(
                "wsgm_native_controller_target_probe_",
                StringComparison.Ordinal))
            {
                value = """
                    {"controllerPresentation":1,"performanceRoot":1,"nativeFields":1,"nativeLayout":1,"localization":1,"react":1}
                    """;
            }
            else if (expression.Contains("wsgm_native_frame_limit_probe_", StringComparison.Ordinal)
                || expression.Contains("wsgm_native_overlay_level_probe_", StringComparison.Ordinal))
            {
                value = $$"""
                    {"performanceActions":{{PerformanceActionsCount}},"performanceRoot":1,"nativeFields":1,"nativeLayout":1,"localization":1,"react":1}
                    """;
            }
            else if (expression.Contains("nativeComponents.install", StringComparison.Ordinal))
            {
                InstallCount++;
                value = "{\"ok\":true}";
            }
            else if (expression.Contains("nativeComponents.remove", StringComparison.Ordinal))
            {
                string kind = expression.Contains("controllerTarget", StringComparison.Ordinal)
                    ? "controllerTarget"
                    : expression.Contains("frameLimit", StringComparison.Ordinal)
                        ? "frameLimit"
                        : expression.Contains("overlayLevel", StringComparison.Ordinal)
                            ? "overlayLevel"
                            : "tdp";
                RemovedKinds.Add(kind);
                value = "{\"ok\":true}";
            }
            else
            {
                value = "{\"ok\":true}";
            }

            return Task.FromResult(new SteamUiEvaluationResult(
                true,
                value,
                null,
                new(1, 1, 1, 1, 1, 1)));
        }

        public IReadOnlyList<SteamUiTransportSnapshot> GetSnapshots() =>
        [
            new(
                SteamUiTargetRole.SharedJsContext,
                SteamUiTransportHealth.Ready,
                new(1, 1, 1, 1, 1, 1),
                "fixture-target",
                null,
                0,
                1),
        ];

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class Lease : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
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
