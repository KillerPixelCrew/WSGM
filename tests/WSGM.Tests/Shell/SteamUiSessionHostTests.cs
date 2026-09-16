using System.Text.Json;
using WSGM.Core;
using WSGM.Device.Sdk.Glyphs;
using WSGM.Shell;
using static WSGM.Tests.AsyncConditions;

namespace WSGM.Tests;

public sealed class SteamUiSessionHostTests
{
    [Fact]
    public async Task BridgeVocabularyComesFromTheDeclaredModulesIncludingDeviceControls()
    {
        await using var transport = new SessionHostTransport();
        await using var performance = new PerformanceService(
            new SimulatedRtssAdapter(),
            (_, _) => Task.CompletedTask);
        await using var host = new SteamUiSessionHost(
            transport,
            _ => Task.FromResult(true),
            null,
            performance);

        host.Apply(true);
        await transport.BridgeInstalled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitForAsync(() => transport.BridgeConfiguration is not null);

        Assert.Contains(
            "\"steam-ui.device-controls\":[\"setChargeLimit\","
                + "\"setLightingBrightness\",\"setLightingColor\"]",
            transport.BridgeConfiguration,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScreensaverRowsAreDeclaredOnlyWithASessionTimeoutOwnerAndRunWithoutQuickAccess()
    {
        await using var transport = new SessionHostTransport();
        await using var performance = new PerformanceService(
            new SimulatedRtssAdapter(),
            (_, _) => Task.CompletedTask);
        await using var without = new SteamUiSessionHost(
            transport,
            _ => Task.FromResult(true),
            null,
            performance);
        Assert.DoesNotContain(
            without.GetPatchSnapshots(),
            snapshot => snapshot.Id == SteamScreensaverSurface.PatchId);

        await using var screensaverTransport = new SessionHostTransport();
        await using var host = new SteamUiSessionHost(
            screensaverTransport,
            _ => Task.FromResult(true),
            null,
            performance,
            displayTimeouts: new DisplayTimeouts(_ => 600, (_, _) => true));

        host.ApplyScreensaverTimeouts(true);
        await screensaverTransport.BridgeInstalled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitForAsync(() => screensaverTransport.BridgeConfiguration is not null);

        Assert.Contains(
            "\"steam-ui.screensaver\":[\"report\",\"setTimeout\"]",
            screensaverTransport.BridgeConfiguration,
            StringComparison.Ordinal);
        Assert.Contains(host.GetPatchSnapshots(), snapshot => snapshot.Id == SteamScreensaverSurface.PatchId);
    }

    [Fact]
    public async Task SharedContextGenerationCancelsInflightSemanticRequest()
    {
        await using var transport = new SessionHostTransport();
        await using var performance = new PerformanceService(
            new SimulatedRtssAdapter(),
            (_, _) => Task.CompletedTask);
        var requestStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var requestCancelled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = new SteamUiSessionHost(
            transport,
            async cancellationToken =>
            {
                using var registration = cancellationToken.Register(
                    () => requestCancelled.TrySetResult());
                requestStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return true;
            },
            null,
            performance);
        host.Apply(true);
        await transport.BridgeInstalled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitForAsync(() => host.GetPatchSnapshots().Any(snapshot =>
            snapshot.Id == "steam-ui.bridge"
            && snapshot.State == SteamUiPatchState.Verified));

        transport.EmitToggleRequest();
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        transport.AdvanceGeneration(SteamUiTargetRole.SharedJsContext);

        await requestCancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task SharedContextGenerationQueuesDownloadPatchResynchronization()
    {
        await using var transport = new SessionHostTransport();
        await using var performance = new PerformanceService(
            new SimulatedRtssAdapter(),
            (_, _) => Task.CompletedTask);
        await using var host = new SteamUiSessionHost(
            transport,
            _ => Task.FromResult(true),
            null,
            performance);

        host.ApplyDownloadSort(true);
        await transport.FirstDownloadInstall.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitForAsync(() => host.GetPatchSnapshots().Any(snapshot =>
            snapshot.Id == "wsgm.download-sort"
            && snapshot.State == SteamUiPatchState.Verified));

        transport.AdvanceGeneration(SteamUiTargetRole.SharedJsContext);

        var completed = await Task.WhenAny(
            transport.SecondDownloadInstall.Task,
            Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.True(
            ReferenceEquals(completed, transport.SecondDownloadInstall.Task),
            $"Download install count was {transport.DownloadInstallations}; states: "
                + string.Join(", ", host.GetPatchSnapshots().Select(snapshot =>
                    $"{snapshot.Id}={snapshot.State}/{snapshot.Generations}")));
        await WaitForAsync(() => host.GetPatchSnapshots().Any(snapshot =>
            snapshot.Id == "wsgm.download-sort"
            && snapshot.State == SteamUiPatchState.Verified));
    }

    [Fact]
    public async Task MainWindowGenerationQueuesGlyphPatchResynchronization()
    {
        await using var transport = new SessionHostTransport();
        await using var performance = new PerformanceService(
            new SimulatedRtssAdapter(),
            (_, _) => Task.CompletedTask);
        await using var host = new SteamUiSessionHost(
            transport,
            _ => Task.FromResult(true),
            null,
            performance);
        var profile = new ImportedGlyphProfile
        {
            Manifest = new GlyphProfileManifest
            {
                SchemaVersion = 1,
                ProfileId = "fixture",
                DisplayName = "Fixture",
                Revision = 1,
                SourceRevision = "fixture",
                NoticePath = "NOTICE.txt",
                Controls =
                [
                    new GlyphControlMapping
                    {
                        Control = GlyphControlId.FaceSouth,
                        Presence = GlyphControlPresence.Present
                    }
                ]
            },
            Assets = new Dictionary<string, ImportedGlyphAsset>()
        };

        host.ApplyGlyphs(true, profile);
        await transport.FirstGlyphInstall.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitForAsync(() => host.GetPatchSnapshots().Any(snapshot =>
            snapshot.Id == SteamInputGlyphStylePatch.PatchId
            && snapshot.State == SteamUiPatchState.Verified));

        transport.AdvanceGeneration(SteamUiTargetRole.MainWindow);

        await transport.SecondGlyphInstall.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitForAsync(() => host.GetPatchSnapshots().Any(snapshot =>
            snapshot.Id == SteamInputGlyphStylePatch.PatchId
            && snapshot.State == SteamUiPatchState.Verified));
    }

    [Fact]
    public async Task NativeDisableCoversWholeRegistryButLeavesDownloadSortAndItsBridgeEnabled()
    {
        // Download sort registers its transform on the toolkit's shared JSX-runtime claim, which the
        // bridge serves, so the bridge outlives native Quick Access with it.
        await using var transport = new SessionHostTransport();
        await using var performance = new PerformanceService(
            new SimulatedRtssAdapter(),
            (_, _) => Task.CompletedTask);
        await using var host = new SteamUiSessionHost(
            transport,
            _ => Task.FromResult(true),
            null,
            performance);
        host.ApplyDownloadSort(true);
        host.Apply(true);

        host.Apply(false);

        var snapshots = host.GetPatchSnapshots();
        Assert.True(snapshots.Single(snapshot => snapshot.Id == "wsgm.download-sort").Enabled);
        Assert.True(snapshots.Single(snapshot => snapshot.Id == SteamUiBridgePatch.PatchId).Enabled);
        Assert.All(
            snapshots.Where(snapshot => snapshot.Id != "wsgm.download-sort"
                && snapshot.Id != SteamUiBridgePatch.PatchId
                && snapshot.Id != SteamInputGlyphStylePatch.PatchId),
            snapshot => Assert.False(snapshot.Enabled));
    }

    [Fact]
    public async Task SurfaceObservationSurvivesNativeRowDisableAndStopsWithCef()
    {
        await using var transport = new SessionHostTransport();
        await using var performance = new PerformanceService(
            new SimulatedRtssAdapter(), (_, _) => Task.CompletedTask);
        await using var host = new SteamUiSessionHost(
            transport, _ => Task.FromResult(true), null, performance);
        host.ApplySurfaceObservation(true);
        host.Apply(true);
        host.Apply(false);
        Assert.True(host.GetPatchSnapshots().Single(
            snapshot => snapshot.Id == "steam-ui.overlay-activation").Enabled);
        await host.DisableAsync();
        Assert.False(host.GetPatchSnapshots().Single(
            snapshot => snapshot.Id == "steam-ui.overlay-activation").Enabled);
    }

    [Fact]
    public async Task StorageSurfaceIsDeclaredOnlyWithABridgeBehindIt()
    {
        await using var transport = new SessionHostTransport();
        await using var performance = new PerformanceService(
            new SimulatedRtssAdapter(), (_, _) => Task.CompletedTask);
        using var drives = new RemovableDriveManager();
        var bridge = new SteamStorageBridge(drives, new SdFormatManager(), () => false);
        await using var host = new SteamUiSessionHost(
            transport, _ => Task.FromResult(true), null, performance, storage: bridge);

        host.Apply(true);
        await transport.BridgeInstalled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitForAsync(() => transport.BridgeConfiguration is not null);

        // The gate and the command vocabulary both reach the client, which is what a session
        // holding a bridge but never declaring the module would not do — the failure this covers.
        Assert.Contains(
            "\"steam-ui.storage\":[\"adopt\",\"unmount\",\"eject\",\"format\",\"trimall\"]",
            transport.BridgeConfiguration,
            StringComparison.Ordinal);
        Assert.Contains(
            host.GetPatchSnapshots(), snapshot => snapshot.Id == SteamStorageSurface.PatchId);
    }

    [Fact]
    public async Task StorageSurfaceIsAbsentWithoutOne()
    {
        await using var transport = new SessionHostTransport();
        await using var performance = new PerformanceService(
            new SimulatedRtssAdapter(), (_, _) => Task.CompletedTask);
        await using var host = new SteamUiSessionHost(
            transport, _ => Task.FromResult(true), null, performance);

        host.Apply(true);
        await transport.BridgeInstalled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitForAsync(() => transport.BridgeConfiguration is not null);

        // Overlay-test owns no storage managers. Installing the gate there would revive Steam's
        // pages onto a backend that cannot answer them. The asset always carries the gate's source,
        // so what says whether the surface exists is the command vocabulary, not the text.
        Assert.DoesNotContain(
            "\"steam-ui.storage\":[", transport.BridgeConfiguration, StringComparison.Ordinal);
        Assert.DoesNotContain(
            host.GetPatchSnapshots(), snapshot => snapshot.Id == SteamStorageSurface.PatchId);
    }

    [Fact]
    public async Task LibraryBadgeSurfaceIsDeclaredAndFollowsItsOwnSwitch()
    {
        await using var transport = new SessionHostTransport();
        await using var performance = new PerformanceService(
            new SimulatedRtssAdapter(), (_, _) => Task.CompletedTask);
        await using var host = new SteamUiSessionHost(
            transport, _ => Task.FromResult(true), null, performance);

        // The badge belongs to the card manager, not to native Quick Access: it comes up on its
        // own switch with Quick Access off, and its layout report is in the vocabulary.
        host.ApplyLibraryBadge(true);
        await transport.BridgeInstalled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitForAsync(() => transport.BridgeConfiguration is not null);

        Assert.Contains(
            "\"steam-ui.library-badge\":[\"homeLayout\"]",
            transport.BridgeConfiguration,
            StringComparison.Ordinal);
        var badge = Assert.Single(
            host.GetPatchSnapshots(), snapshot => snapshot.Id == SteamLibraryBadgeSurface.PatchId);
        Assert.NotEqual(SteamUiPatchState.Disabled, badge.State);
    }

    [Fact]
    public async Task HomeCarouselSurfaceIsDeclaredAndFollowsItsOwnSwitch()
    {
        await using var transport = new SessionHostTransport();
        await using var performance = new PerformanceService(
            new SimulatedRtssAdapter(), (_, _) => Task.CompletedTask);
        await using var host = new SteamUiSessionHost(
            transport, _ => Task.FromResult(true), null, performance);

        // The carousel is its own switch, independent of native Quick Access, and its report is in
        // the vocabulary the bridge allows.
        host.ApplyHomeCarousel(enabled: true, includeUninstalled: false);
        await transport.BridgeInstalled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitForAsync(() => transport.BridgeConfiguration is not null);

        Assert.Contains(
            "\"steam-ui.home-carousel\":[\"report\"]",
            transport.BridgeConfiguration,
            StringComparison.Ordinal);
        var carousel = Assert.Single(
            host.GetPatchSnapshots(), snapshot => snapshot.Id == SteamHomeCarouselSurface.PatchId);
        Assert.NotEqual(SteamUiPatchState.Disabled, carousel.State);
    }

    private sealed class SessionHostTransport : ISteamUiTransport
    {
        private readonly Dictionary<SteamUiTargetRole, SteamUiGenerations> _generations = new()
        {
            [SteamUiTargetRole.SharedJsContext] = new SteamUiGenerations(1, 1, 1, 1, 1, 1),
            [SteamUiTargetRole.MainWindow] = new SteamUiGenerations(1, 1, 1, 1, 1, 1)
        };
        private int _downloadInstallations;
        private int _glyphInstallations;
        private string? _bridgeConfiguration;

        public event EventHandler<SteamUiNotification>? NotificationReceived;

        public event EventHandler<SteamUiTransportSnapshot>? GenerationChanged;

        internal TaskCompletionSource BridgeInstalled { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource FirstDownloadInstall { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource SecondDownloadInstall { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource FirstGlyphInstall { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource SecondGlyphInstall { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal string? BridgeConfiguration => Volatile.Read(ref _bridgeConfiguration);

        internal int DownloadInstallations => Volatile.Read(ref _downloadInstallations);

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
            cancellationToken.ThrowIfCancellationRequested();
            if (expression.Contains("\"allowed\":", StringComparison.Ordinal))
            {
                Volatile.Write(ref _bridgeConfiguration, expression);
            }
            string value;
            if (expression.Contains("steam_ui_bridge_probe_", StringComparison.Ordinal))
            {
                value = "{\"tdpAvailability\":1,\"tdpComponent\":1,"
                    + "\"performanceActions\":1,\"profileProjection\":1}";
            }
            else if (expression.Contains("version:b&&b.version", StringComparison.Ordinal))
            {
                value = "{\"ok\":true,\"version\":1}";
            }
            else if (expression.Contains("absent:!window.__steamUi", StringComparison.Ordinal))
            {
                value = "{\"absent\":true}";
            }
            else if (expression.Contains("generation replaced", StringComparison.Ordinal)
                && expression.Contains("nativeComponents", StringComparison.Ordinal))
            {
                value = "{\"ok\":true}";
            }
            else if (expression.Contains(
                "runtime:!!window.webpackChunksteamui",
                StringComparison.Ordinal))
            {
                value = "{\"ok\":true,\"runtime\":true,\"owned\":false}";
            }
            else if (expression.Contains("dlSortInstall", StringComparison.Ordinal))
            {
                var count = Interlocked.Increment(ref _downloadInstallations);
                (count == 1 ? FirstDownloadInstall : SecondDownloadInstall).TrySetResult();
                value = "{\"ok\":true}";
            }
            else if (expression.Contains("dlSortPatched", StringComparison.Ordinal)
                || expression.Contains("dlSortRemove", StringComparison.Ordinal))
            {
                value = "{\"ok\":true}";
            }
            else if (expression.Contains("styleSheets", StringComparison.Ordinal)
                && expression.Contains("rowClass", StringComparison.Ordinal)
                && expression.Contains("logoClass", StringComparison.Ordinal))
            {
                value = "{\"ok\":true,\"rowClass\":true,\"logoClass\":true}";
            }
            else if (expression.Contains("document.head.append(style)", StringComparison.Ordinal))
            {
                var count = Interlocked.Increment(ref _glyphInstallations);
                (count == 1 ? FirstGlyphInstall : SecondGlyphInstall).TrySetResult();
                value = "{\"ok\":true}";
            }
            else if (expression.Contains("ruleCount", StringComparison.Ordinal)
                || expression.Contains("style.'+owned", StringComparison.Ordinal))
            {
                value = "{\"ok\":true}";
            }
            else
            {
                value = "{}";
            }

            return Task.FromResult(new SteamUiEvaluationResult(
                true,
                value,
                null,
                _generations[role]));
        }

        public Task SetRuntimeBindingAsync(
            SteamUiTargetRole role,
            string bindingName,
            bool installed,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (installed)
            {
                BridgeInstalled.TrySetResult();
            }
            return Task.CompletedTask;
        }

        public IReadOnlyList<SteamUiTransportSnapshot> GetSnapshots() =>
            _generations.Select(pair => new SteamUiTransportSnapshot(
                pair.Key,
                SteamUiTransportHealth.Ready,
                pair.Value,
                "fixture-" + pair.Key,
                null,
                0,
                1)).ToArray();

        internal void AdvanceGeneration(SteamUiTargetRole role)
        {
            var next = _generations[role] with
            {
                Session = _generations[role].Session + 1,
                Document = _generations[role].Document + 1
            };
            _generations[role] = next;
            GenerationChanged?.Invoke(this, Snapshot(role));
        }

        internal void EmitToggleRequest()
        {
            var generation = _generations[SteamUiTargetRole.SharedJsContext];
            var payload = JsonSerializer.Serialize(new
            {
                version = 1,
                type = "request",
                patchId = "wsgm.native-qam.shell",
                command = "toggleQuickAccess",
                sequence = 1,
                actionGeneration = 1,
                contextGeneration = generation.ExecutionContext,
                documentGeneration = generation.Document,
                payload = (object?)null
            });
            var parameters = JsonSerializer.Serialize(new
            {
                name = "__steamUiBridge_v1_7b24d11c",
                payload
            });
            NotificationReceived?.Invoke(this, new SteamUiNotification(
                SteamUiTargetRole.SharedJsContext,
                "Runtime.bindingCalled",
                parameters,
                generation));
        }

        private SteamUiTransportSnapshot Snapshot(SteamUiTargetRole role) => new(
            role,
            SteamUiTransportHealth.Ready,
            _generations[role],
            "fixture-" + role,
            null,
            0,
            1);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class Lease : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task RouterReturnsExplicitSuccessAndMalformedPayloadRefusal()
    {
        await using var transport = new RoutingTransport();
        await using var performance = new PerformanceService(
            new SimulatedRtssAdapter(),
            (_, _) => Task.CompletedTask);
        var toggles = 0;
        await using var host = new SteamUiSessionHost(
            transport,
            _ =>
            {
                toggles++;
                return Task.FromResult(true);
            },
            null,
            performance);
        host.Apply(true);
        await WaitForAsync(() => host.GetPatchSnapshots().Any(snapshot =>
            snapshot.Id == "steam-ui.bridge"
            && snapshot.State == SteamUiPatchState.Verified), timeoutSeconds: 3);

        transport.EmitRequest(
            "wsgm.native-qam.shell",
            "toggleQuickAccess",
            sequence: 1,
            actionGeneration: 1,
            payload: null);
        await WaitForAsync(() => transport.Responses.Count >= 1, timeoutSeconds: 3);
        transport.EmitRequest(
            "steam-ui.power-limit",
            "setPrimaryLimit",
            sequence: 2,
            actionGeneration: 1,
            payload: new { watts = "not-a-number" });
        await WaitForAsync(() => transport.Responses.Count >= 2, timeoutSeconds: 3);

        Assert.Equal(1, toggles);
        Assert.True(transport.Responses[0].GetProperty("ok").GetBoolean());
        Assert.False(transport.Responses[1].GetProperty("ok").GetBoolean());
        Assert.Equal(
            "The sustained power-limit payload is invalid.",
            transport.Responses[1].GetProperty("error").GetString());
    }

    [Fact]
    public async Task CancelStopsInflightWorkAndTheNextRequestStillCompletes()
    {
        await using var transport = new RoutingTransport();
        await using var performance = new PerformanceService(
            new SimulatedRtssAdapter(),
            (_, _) => Task.CompletedTask);
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCancelled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var routeDeadline = TimeSpan.FromSeconds(2);
        await using var host = new SteamUiSessionHost(
            transport,
            async cancellationToken =>
            {
                if (Interlocked.Increment(ref calls) > 1)
                {
                    return true;
                }

                using var registration = cancellationToken.Register(
                    () => firstCancelled.TrySetResult());
                firstStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return false;
            },
            null,
            performance);
        host.Apply(true);
        await WaitForAsync(() => host.GetPatchSnapshots().Any(snapshot =>
            snapshot.Id == "steam-ui.bridge"
            && snapshot.State == SteamUiPatchState.Verified), timeoutSeconds: 3);

        transport.EmitRequest(
            "wsgm.native-qam.shell",
            "toggleQuickAccess",
            sequence: 1,
            actionGeneration: 1,
            payload: null);
        await firstStarted.Task.WaitAsync(routeDeadline);
        transport.EmitRequest(
            "wsgm.native-qam.shell",
            "toggleQuickAccess",
            sequence: 1,
            actionGeneration: 1,
            payload: null,
            type: "cancel");
        await firstCancelled.Task.WaitAsync(routeDeadline);

        transport.EmitRequest(
            "wsgm.native-qam.shell",
            "toggleQuickAccess",
            sequence: 2,
            actionGeneration: 2,
            payload: null);
        await WaitForAsync(() => transport.Responses.Any(response =>
            response.GetProperty("sequence").GetInt64() == 2), timeoutSeconds: 3);

        Assert.Equal(2, calls);
        Assert.DoesNotContain(
            transport.Responses,
            response => response.GetProperty("sequence").GetInt64() == 1);
    }

    [Fact]
    public async Task PerformanceObservationExistsOnlyWhileRowsAndBridgeAreCurrent()
    {
        await using var transport = new RoutingTransport();
        await using var performance = new PerformanceService(
            new SimulatedRtssAdapter(),
            (_, _) => Task.CompletedTask);
        await using var host = new SteamUiSessionHost(
            transport,
            _ => Task.FromResult(true),
            null,
            performance);
        host.Apply(true);

        await WaitForAsync(() => host.GetPatchSnapshots().Any(snapshot =>
            snapshot.Id == "steam-ui.frame-limit"
            && snapshot.State == SteamUiPatchState.Verified), timeoutSeconds: 3);
        await WaitForAsync(() => performance.ObserverCount == 1, timeoutSeconds: 3);

        transport.BridgeHandshakeSucceeds = false;
        transport.AdvanceSharedGeneration();
        await WaitForAsync(() => performance.ObserverCount == 0, timeoutSeconds: 3);

        Assert.Equal(0, performance.ObserverCount);
    }

    private sealed class RoutingTransport : ISteamUiTransport
    {
        private readonly object _responseGate = new();
        private readonly Dictionary<SteamUiTargetRole, SteamUiGenerations> _generations = new()
        {
            [SteamUiTargetRole.SharedJsContext] = new SteamUiGenerations(1, 1, 1, 1, 1, 1),
            [SteamUiTargetRole.MainWindow] = new SteamUiGenerations(1, 1, 1, 1, 1, 1)
        };
        private readonly List<JsonElement> _responses = [];

        public event EventHandler<SteamUiNotification>? NotificationReceived;

        public event EventHandler<SteamUiTransportSnapshot>? GenerationChanged;

        internal bool BridgeHandshakeSucceeds { get; set; } = true;

        internal IReadOnlyList<JsonElement> Responses
        {
            get
            {
                lock (_responseGate)
                {
                    return [.. _responses];
                }
            }
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
            CaptureResponse(expression);
            string value;
            if (!BridgeHandshakeSucceeds
                && expression.Contains("maximumPending", StringComparison.Ordinal))
            {
                value = "{\"ok\":false}";
            }
            else if (expression.Contains("steam_ui_bridge_probe_", StringComparison.Ordinal))
            {
                value = "{\"tdpAvailability\":1,\"tdpComponent\":1,"
                    + "\"performanceActions\":1,\"profileProjection\":1}";
            }
            else if (expression.Contains("steam_ui_", StringComparison.Ordinal)
                && expression.Contains("_probe_", StringComparison.Ordinal))
            {
                value = "{\"performanceActions\":1,\"controllerPresentation\":1,"
                    + "\"tdpPresentation\":1,\"performanceRoot\":1,\"nativeFields\":1,"
                    + "\"nativeLayout\":1,\"localization\":1,\"react\":1}";
            }
            else if (expression.Contains("version:b&&b.version", StringComparison.Ordinal))
            {
                value = "{\"ok\":true,\"version\":1}";
            }
            else if (expression.Contains("absent:!window.__steamUi", StringComparison.Ordinal))
            {
                value = "{\"absent\":true}";
            }
            else if (expression.Contains("generation replaced", StringComparison.Ordinal)
                && expression.Contains("nativeComponents", StringComparison.Ordinal))
            {
                value = "{\"ok\":true}";
            }
            else if (expression.Contains(
                "runtime:!!window.webpackChunksteamui",
                StringComparison.Ordinal))
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
                _generations[role]));
        }

        public Task SetRuntimeBindingAsync(
            SteamUiTargetRole role,
            string bindingName,
            bool installed,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public IReadOnlyList<SteamUiTransportSnapshot> GetSnapshots() =>
            _generations.Select(pair => new SteamUiTransportSnapshot(
                pair.Key,
                SteamUiTransportHealth.Ready,
                pair.Value,
                "fixture-" + pair.Key,
                null,
                0,
                1)).ToArray();

        internal void EmitRequest(
            string patchId,
            string command,
            long sequence,
            long actionGeneration,
            object? payload,
            string type = "request")
        {
            var generation = _generations[SteamUiTargetRole.SharedJsContext];
            var envelope = JsonSerializer.Serialize(new
            {
                version = SteamUiBridgeHost.SchemaVersion,
                type,
                patchId,
                command,
                sequence,
                actionGeneration,
                contextGeneration = generation.ExecutionContext,
                documentGeneration = generation.Document,
                payload
            });
            var parameters = JsonSerializer.Serialize(new
            {
                name = "__steamUiBridge_v1_7b24d11c",
                payload = envelope
            });
            NotificationReceived?.Invoke(this, new SteamUiNotification(
                SteamUiTargetRole.SharedJsContext,
                "Runtime.bindingCalled",
                parameters,
                generation));
        }

        internal void AdvanceSharedGeneration()
        {
            var role = SteamUiTargetRole.SharedJsContext;
            _generations[role] = _generations[role] with
            {
                ExecutionContext = _generations[role].ExecutionContext + 1,
                Document = _generations[role].Document + 1
            };
            var generation = _generations[role];
            GenerationChanged?.Invoke(this, new SteamUiTransportSnapshot(
                role,
                SteamUiTransportHealth.Ready,
                generation,
                "fixture-" + role,
                null,
                0,
                1));
        }

        private void CaptureResponse(string expression)
        {
            const string marker = "JSON.parse(";
            var start = expression.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0)
            {
                return;
            }

            start += marker.Length;
            if (start >= expression.Length || expression[start] != '"')
            {
                return;
            }

            var escaped = false;
            for (var index = start + 1; index < expression.Length; index++)
            {
                var character = expression[index];
                if (!escaped && character == '"')
                {
                    var json = JsonSerializer.Deserialize<string>(expression[start..(index + 1)]);
                    if (json is null)
                    {
                        return;
                    }

                    using var document = JsonDocument.Parse(json);
                    if (!document.RootElement.TryGetProperty("type", out var type)
                        || type.GetString() != "response")
                    {
                        return;
                    }

                    lock (_responseGate)
                    {
                        _responses.Add(document.RootElement.Clone());
                    }
                    return;
                }

                escaped = !escaped && character == '\\';
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class Lease : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
