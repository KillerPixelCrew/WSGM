using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using WSGM.Core;
using WSGM.Device.Sdk.Glyphs;

namespace WSGM.Shell;

/// <summary>
/// Owns the narrow bridge and registered patches over the injected process-long Steam UI transport.
/// </summary>
internal sealed class SteamUiSessionHost : IAsyncDisposable
{
    private const string BootstrapPatchId = "wsgm.native-qam.bootstrap";
    private const string TdpPatchId = "wsgm.native-qam.tdp";
    private const string FrameLimitPatchId = "wsgm.native-qam.frame-limit";
    private const string OverlayLevelPatchId = "wsgm.native-qam.overlay-level";
    private const string AutoTdpPatchId = "wsgm.native-qam.auto-tdp";
    private const string ControllerTargetPatchId = "wsgm.native-qam.controller-target";
    private const string AudioPatchId = "wsgm.native-qam.audio";
    private const string NetworkGatePatchId = "wsgm.steam-network.gate";
    private const string BluetoothPatchId = "wsgm.steam-bluetooth.service";
    private const string GlyphStylePatchId = SteamInputGlyphStylePatch.PatchId;
    private readonly PersistentSteamUiTransport _transport;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _synchronizeSignal = new(0, 1);
    private readonly SemaphoreSlim _publicationSignal = new(0, 1);
    private readonly object _requestGate = new();
    private readonly object _observationGate = new();
    private readonly Dictionary<long, CancellationTokenSource> _inflightRequests = [];
    private readonly HashSet<Task> _requestTasks = [];
    private readonly Func<CancellationToken, Task<bool>> _toggleQuickAccess;
    private readonly INativeQamTdpService _tdp;

    /// <summary>
    /// Null when no audio manager exists for this session, which is the overlay-test case.
    /// </summary>
    /// <remarks>
    /// Unlike the semantic services above there is no "unavailable" stand-in, because audio is
    /// supplied as a namespace rather than drawn as a row: with nothing to supply, the right
    /// behaviour is to leave the namespace absent so Steam's own store stays unavailable, not to
    /// install one that answers with nothing.
    /// </remarks>
    private readonly INativeQamAudioService? _audio;

    /// <summary>
    /// The session's radio manager, borrowed rather than owned, or null in overlay-test.
    /// </summary>
    /// <remarks>
    /// Only its scanning lifetime is driven from here. Joining, forgetting and the radio toggles
    /// stay with the surfaces that already own them.
    /// </remarks>
    private readonly RadioManager? _radios;

    /// <summary>How long a burst of scan results is allowed to settle before one push.</summary>
    private static readonly TimeSpan NetworkPublishDelay = TimeSpan.FromMilliseconds(400);

    private int _networkPublishPending;
    private readonly PerformanceServiceNativeQamAdapter _performance;
    private readonly INativeQamAutoTdpService _autoTdp;
    private readonly INativeQamControllerTargetService _controllerTarget;
    private readonly SteamInputGlyphDeliveryState _glyphDeliveryState = new();
    private readonly SteamUiBridgeHost _bridge;
    private readonly SteamUiPatchManager _patches;
    private readonly Task _synchronization;
    private readonly Task _publication;
    private int _signalPending;
    private int _publicationPending;
    private IDisposable? _performanceObservation;
    private volatile bool _enabled;
    private volatile bool _glyphsEnabled;
    private volatile bool _glyphDeliveryEnabled;
    private volatile bool _disposed;

    internal SteamUiSessionHost(
        PersistentSteamUiTransport transport,
        Func<CancellationToken, Task<bool>> toggleQuickAccess,
        DeviceCoordinator? deviceCoordinator,
        PerformanceService performance,
        AudioManager? audio = null,
        RadioManager? radios = null)
    {
        _radios = radios;
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        ArgumentNullException.ThrowIfNull(toggleQuickAccess);
        _toggleQuickAccess = toggleQuickAccess;
        _tdp = deviceCoordinator is null
            ? new UnavailableNativeQamTdpService()
            : new DeviceCoordinatorNativeQamTdpService(deviceCoordinator);
        _performance = new PerformanceServiceNativeQamAdapter(performance);
        _autoTdp = deviceCoordinator is null
            ? new UnavailableNativeQamAutoTdpService()
            : new DeviceCoordinatorNativeQamAutoTdpService(deviceCoordinator);
        _controllerTarget = deviceCoordinator is null
            ? new UnavailableNativeQamControllerTargetService()
            : new DeviceCoordinatorNativeQamControllerTargetService(deviceCoordinator);
        _audio = audio is null ? null : new AudioManagerNativeQamAudioService(audio);
        _bridge = new SteamUiBridgeHost(_transport);
        _patches = new SteamUiPatchManager(_transport);
        _patches.Register(new NativeQamBootstrapPatch(_bridge));
        _patches.Register(new NativeQamTdpPatch());
        _patches.Register(new NativeQamAutoTdpPatch());
        _patches.Register(new NativeQamFrameLimitPatch());
        _patches.Register(new NativeQamOverlayLevelPatch());
        _patches.Register(new NativeQamControllerTargetPatch());
        if (_audio is not null)
        {
            _patches.Register(new NativeQamAudioPatch());
        }

        // The gate reveals Steam's Wi-Fi surface, and the surface is only worth revealing if
        // something can populate it — which is the radio manager.
        if (_radios is not null)
        {
            _patches.Register(new SteamNetworkGatePatch());
            _patches.Register(new SteamBluetoothServicePatch());
        }

        _patches.Register(new SteamInputGlyphStylePatch(_glyphDeliveryState));
        SetPatchStates(bootstrap: false, components: false);
        SetGlyphDeliveryPatchStates();
        _patches.SetGlobalEnabled(false);
        _bridge.RequestReceived += OnRequestReceived;
        _transport.GenerationChanged += OnGenerationChanged;
        _tdp.StateChanged += OnSemanticStateChanged;
        _autoTdp.StateChanged += OnSemanticStateChanged;
        _performance.StateChanged += OnSemanticStateChanged;
        _controllerTarget.StateChanged += OnSemanticStateChanged;
        if (_audio is not null)
        {
            _audio.StateChanged += OnSemanticStateChanged;
        }

        _synchronization = Task.Run(SynchronizeLoopAsync);
        _publication = Task.Run(PublishLoopAsync);
    }

    internal void Apply(bool enabled)
    {
        if (_disposed || _enabled == enabled)
        {
            return;
        }

        _enabled = enabled;
        if (enabled)
        {
            _patches.SetGlobalEnabled(true);
            SetPatchStates(bootstrap: true, components: true);
        }
        else
        {
            CancelAllInflightRequests();
            ReleasePerformanceObservation();
            SetPatchStates(bootstrap: true, components: false);
        }
        QueueSynchronization();
    }

    /// <summary>
    /// Applies handheld glyph presentation: whether it is on, and what to draw.
    /// </summary>
    /// <param name="enabled">Whether WSGM presents handheld glyphs at all.</param>
    /// <param name="profile">The resolved plugin profile, or null for native Steam glyphs.</param>
    /// <remarks>
    /// One call because there is one thing to install. The profile is the plugin's and is the only
    /// source of artwork; WSGM turns it into a stylesheet. Either switch off, or a profile that
    /// supplies nothing to draw, removes WSGM's stylesheet and leaves native Valve glyphs in place.
    /// </remarks>
    internal void ApplyGlyphs(bool enabled, ImportedGlyphProfile? profile)
    {
        if (_disposed)
        {
            return;
        }

        _glyphsEnabled = enabled;
        _glyphDeliveryState.Update(enabled ? profile : null);
        SetGlyphDeliveryPatchStates();
        if (_glyphDeliveryEnabled)
        {
            _patches.SetGlobalEnabled(true);
        }

        QueueSynchronization();
    }

    internal async Task DisableAsync()
    {
        if (_disposed)
        {
            return;
        }

        _enabled = false;
        _glyphsEnabled = false;
        CancelAllInflightRequests();
        ReleasePerformanceObservation();
        SetPatchStates(bootstrap: true, components: false);
        SetGlyphDeliveryPatchStates();
        await _patches.SynchronizeAsync(_shutdown.Token).ConfigureAwait(false);
        _glyphDeliveryState.Update(null);
        SetPatchStates(bootstrap: false, components: false);
        _patches.SetGlobalEnabled(false);
        await _patches.SynchronizeAsync(_shutdown.Token).ConfigureAwait(false);
    }

    private void OnGenerationChanged(object? sender, SteamUiTransportSnapshot snapshot)
    {
        if (snapshot.Role == SteamUiTargetRole.SharedJsContext)
        {
            ReleasePerformanceObservation();
        }

        if ((_enabled || _glyphsEnabled || _glyphDeliveryEnabled)
            && snapshot.Role == SteamUiTargetRole.SharedJsContext)
        {
            QueueSynchronization();
        }
    }

    private void QueueSynchronization()
    {
        if (_disposed)
        {
            return;
        }

        if (Interlocked.Exchange(ref _signalPending, 1) == 0)
        {
            _synchronizeSignal.Release();
        }
    }

    private async Task SynchronizeLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                await _synchronizeSignal.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                Interlocked.Exchange(ref _signalPending, 0);
                await _patches.SynchronizeAsync(_shutdown.Token).ConfigureAwait(false);
                if (_enabled)
                {
                    UpdatePerformanceObservation();
                    QueueStatePublication();
                }
                else
                {
                    ReleasePerformanceObservation();
                    SetPatchStates(bootstrap: false, components: false);
                    _patches.SetGlobalEnabled(_glyphsEnabled || _glyphDeliveryEnabled);
                    await _patches.SynchronizeAsync(_shutdown.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warn($"Steam UI patch synchronization failed: {ex.Message}");
            }
        }
    }

    private async Task PublishLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                await _publicationSignal.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                Interlocked.Exchange(ref _publicationPending, 0);
                if (!_enabled || !_bridge.IsReady)
                {
                    continue;
                }

                JsonElement tdp = JsonSerializer.SerializeToElement(
                    _tdp.Current,
                    NativeQamSemanticJsonContext.Default.NativeQamTdpState);
                await _bridge.PublishStateAsync(TdpPatchId, tdp, _shutdown.Token)
                    .ConfigureAwait(false);
                JsonElement autoTdp = JsonSerializer.SerializeToElement(
                    _autoTdp.Current,
                    NativeQamSemanticJsonContext.Default.NativeQamAutoTdpState);
                await _bridge.PublishStateAsync(AutoTdpPatchId, autoTdp, _shutdown.Token)
                    .ConfigureAwait(false);
                if (_radios is { } radios)
                {
                    JsonElement bluetooth = JsonSerializer.SerializeToElement(
                        await ReadBluetoothStateAsync(radios).ConfigureAwait(false),
                        NativeQamSemanticJsonContext.Default.SteamBluetoothState);
                    await _bridge.PublishStateAsync(BluetoothPatchId, bluetooth, _shutdown.Token)
                        .ConfigureAwait(false);
                }

                JsonElement frameLimit = JsonSerializer.SerializeToElement(
                    _performance.FrameLimit,
                    NativeQamSemanticJsonContext.Default.NativeQamFrameLimitState);
                await _bridge.PublishStateAsync(FrameLimitPatchId, frameLimit, _shutdown.Token)
                    .ConfigureAwait(false);
                JsonElement overlayLevel = JsonSerializer.SerializeToElement(
                    _performance.OverlayLevel,
                    NativeQamSemanticJsonContext.Default.NativeQamOverlayLevelState);
                await _bridge.PublishStateAsync(OverlayLevelPatchId, overlayLevel, _shutdown.Token)
                    .ConfigureAwait(false);
                JsonElement controllerTarget = JsonSerializer.SerializeToElement(
                    _controllerTarget.Current,
                    NativeQamSemanticJsonContext.Default.NativeQamControllerTargetState);
                await _bridge.PublishStateAsync(
                    ControllerTargetPatchId,
                    controllerTarget,
                    _shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warn($"Steam UI semantic state publication failed: {ex.Message}");
            }
        }
    }

    private void OnSemanticStateChanged() => QueueStatePublication();

    private void QueueStatePublication()
    {
        if (_disposed)
        {
            return;
        }

        if (Interlocked.Exchange(ref _publicationPending, 1) == 0)
        {
            _publicationSignal.Release();
        }
    }

    private void SetPatchStates(bool bootstrap, bool components)
    {
        _patches.SetPatchEnabled(BootstrapPatchId, bootstrap);
        _patches.SetPatchEnabled(TdpPatchId, components);
        _patches.SetPatchEnabled(AutoTdpPatchId, components);
        _patches.SetPatchEnabled(FrameLimitPatchId, components);
        _patches.SetPatchEnabled(OverlayLevelPatchId, components);
        _patches.SetPatchEnabled(ControllerTargetPatchId, components);
    }

    /// <summary>
    /// Enables the one glyph stylesheet when the active plugin profile supplies something to draw.
    /// </summary>
    /// <remarks>
    /// One switch, because there is one stylesheet. The previous four independent tier switches
    /// existed to gate four separate mapping namespaces; a single stylesheet either has rules or it
    /// does not, and the patch itself refuses to apply an empty one.
    /// </remarks>
    private void SetGlyphDeliveryPatchStates()
    {
        SteamInputGlyphPresentation? presentation = _glyphDeliveryState.Current;
        // Absent controls count as rules. A reviewed profile may legitimately carry nothing but
        // them — hiding trackpad or extra-paddle rows on a handheld that has neither, while keeping
        // Valve's own artwork — and SteamGlyphCss.Build emits real hiding rules for exactly that.
        // Requiring a resource or an image left those profiles with no stylesheet at all, so the
        // controls the device does not have stayed on screen.
        bool deliver = _glyphsEnabled
            && presentation is not null
            && (presentation.StableResources.Count > 0
                || presentation.ControllerImages.Count > 0
                || presentation.AbsentControls.Count > 0);

        // Three independent conditions, and failing any of them leaves the Steam Input page showing
        // Valve's Steam Deck artwork instead of the handheld's own. The patch then reports itself
        // Disabled, which is honest but says nothing about which condition was missing — the
        // setting, a profile that never resolved, or a profile that resolved with nothing to draw.
        Log.Change(
            "steam.ui.glyphs",
            $"Steam Input glyph delivery {(deliver ? "enabled" : "disabled")}: "
                + $"setting={_glyphsEnabled}, profile={presentation is not null}, "
                + $"stableResources={presentation?.StableResources.Count ?? 0}, "
                + $"controllerImages={presentation?.ControllerImages.Count ?? 0}, "
                + $"absentControls={presentation?.AbsentControls.Count ?? 0}",
            deliver ? "info " : "warn ");
        _patches.SetPatchEnabled(GlyphStylePatchId, deliver);
        _glyphDeliveryEnabled = deliver;
    }

    private void UpdatePerformanceObservation()
    {
        // RTSS polling exists for rendered native controls, not merely for the session. A failed
        // fingerprint or lost bridge generation therefore releases the shared service lease.
        IReadOnlyList<SteamUiPatchSnapshot> snapshots = _patches.GetSnapshots();
        bool performancePatchVerified = false;
        foreach (SteamUiPatchSnapshot snapshot in snapshots)
        {
            performancePatchVerified |= (snapshot.Id is FrameLimitPatchId or OverlayLevelPatchId)
                && snapshot.State == SteamUiPatchState.Verified;
        }
        bool shouldObserve = _enabled && _bridge.IsReady && performancePatchVerified;
        if (!shouldObserve)
        {
            ReleasePerformanceObservation();
            return;
        }

        lock (_observationGate)
        {
            if (!_enabled || !_bridge.IsReady)
            {
                return;
            }

            _performanceObservation ??= _performance.AcquireObservation();
        }
    }

    private void ReleasePerformanceObservation()
    {
        IDisposable? observation;
        lock (_observationGate)
        {
            observation = _performanceObservation;
            _performanceObservation = null;
        }
        observation?.Dispose();
    }

    private void OnRequestReceived(object? sender, SteamUiBridgeRequest request)
    {
        if (request.Type == "cancel")
        {
            CancelInflightRequest(request.Sequence);
            return;
        }

        Task task = RespondToRequestAsync(request);
        lock (_requestGate)
        {
            _requestTasks.Add(task);
        }
        _ = ObserveRequestCompletionAsync(task);
    }

    private async Task RespondToRequestAsync(SteamUiBridgeRequest request)
    {
        using CancellationTokenSource requestCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        lock (_requestGate)
        {
            if (!_inflightRequests.TryAdd(request.Sequence, requestCancellation))
            {
                return;
            }
        }

        bool succeeded = false;
        string? error = null;
        JsonElement? responsePayload = null;
        try
        {
            if (!_enabled)
            {
                error = "The requested semantic service is not active.";
            }
            else if (request.PatchId == "wsgm.native-qam.shell"
                && request.Command == "toggleQuickAccess")
            {
                succeeded = await _toggleQuickAccess(requestCancellation.Token).ConfigureAwait(false);
                if (!succeeded)
                {
                    error = "Quick access is not currently available.";
                }
            }
            else if (request.PatchId == TdpPatchId
                && request.Command == "setPrimaryLimit")
            {
                if (!TryReadIntegerPayload(request.Payload, "watts", out int watts))
                {
                    error = "The primary power-limit payload is invalid.";
                }
                else
                {
                    NativeQamCommandResult result = await _tdp.SetPrimaryLimitAsync(
                        watts,
                        requestCancellation.Token).ConfigureAwait(false);
                    succeeded = result.Succeeded;
                    error = result.Error;
                }
            }
            else if (request.PatchId == FrameLimitPatchId
                && request.Command == "setFrameLimit")
            {
                if (!TryReadPerformancePayload(
                    request.Payload,
                    out int value,
                    out PerformancePersistenceTarget persistence))
                {
                    error = "The frame-limit payload is invalid.";
                }
                else
                {
                    NativeQamCommandResult result = await _performance.SetAsync(
                        PerformanceControl.FrameLimit,
                        value,
                        persistence,
                        CorrelationId(request),
                        requestCancellation.Token).ConfigureAwait(false);
                    succeeded = result.Succeeded;
                    error = result.Error;
                }
            }
            else if (request.PatchId == OverlayLevelPatchId
                && request.Command == "setOverlayLevel")
            {
                if (!TryReadPerformancePayload(
                    request.Payload,
                    out int value,
                    out PerformancePersistenceTarget persistence))
                {
                    error = "The overlay-level payload is invalid.";
                }
                else
                {
                    NativeQamCommandResult result = await _performance.SetAsync(
                        PerformanceControl.OverlayLevel,
                        value,
                        persistence,
                        CorrelationId(request),
                        requestCancellation.Token).ConfigureAwait(false);
                    succeeded = result.Succeeded;
                    error = result.Error;
                }
            }
            else if (request.PatchId == AutoTdpPatchId && request.Command == "setAutoTdp")
            {
                if (!TryReadEnabledPayload(request.Payload, out bool wanted))
                {
                    error = "The AutoTDP payload is invalid.";
                }
                else
                {
                    NativeQamCommandResult result = await _autoTdp.SetEnabledAsync(
                        wanted,
                        requestCancellation.Token).ConfigureAwait(false);
                    succeeded = result.Succeeded;
                    error = result.Error;
                }
            }
            else if (request.PatchId == ControllerTargetPatchId
                && request.Command == "setControllerTarget")
            {
                if (!TryReadTargetPayload(request.Payload, out string target))
                {
                    error = "The controller-target payload is invalid.";
                }
                else
                {
                    NativeQamCommandResult result = await _controllerTarget.SetTargetAsync(
                        target,
                        requestCancellation.Token).ConfigureAwait(false);
                    succeeded = result.Succeeded;
                    error = result.Error;
                }
            }
            else if (request.PatchId == AudioPatchId && _audio is { } audio)
            {
                switch (request.Command)
                {
                    case "getDevices":
                        // The only request that answers with data rather than an outcome. Steam
                        // re-reads it after every device add or removal, so it stays a projection of
                        // state already held rather than an enumeration.
                        responsePayload = SerializeAudioState(audio.Current);
                        succeeded = true;
                        break;
                    case "setDefaultDevice":
                        if (!TryReadAudioDevicePayload(request.Payload, out string id, out bool input))
                        {
                            error = "The audio device payload is invalid.";
                            break;
                        }

                        NativeQamCommandResult device = await audio.SetDefaultDeviceAsync(
                            id,
                            input,
                            requestCancellation.Token).ConfigureAwait(false);
                        succeeded = device.Succeeded;
                        error = device.Error;
                        break;
                    case "setVolume":
                        if (!TryReadAudioVolumePayload(request.Payload, out int percent))
                        {
                            error = "The audio volume payload is invalid.";
                            break;
                        }

                        NativeQamCommandResult volume = await audio.SetVolumeAsync(
                            percent,
                            requestCancellation.Token).ConfigureAwait(false);
                        succeeded = volume.Succeeded;
                        error = volume.Error;
                        break;
                    default:
                        error = "The requested semantic service is not active.";
                        break;
                }
            }
            else if (request.PatchId == NetworkGatePatchId && _radios is { } radios)
            {
                // Not a control. Steam is reporting that its own network UI opened or closed, and
                // WSGM scans for exactly that long: scanning on WSGM's own schedule would either
                // burn power with no list on screen, or leave the list stale while one is.
                switch (request.Command)
                {
                    case "startScan":
                        await RunUiAsync(() =>
                        {
                            radios.Networks.CollectionChanged -= OnScannedNetworksChanged;
                            radios.Networks.CollectionChanged += OnScannedNetworksChanged;
                            radios.StartScanning();
                        }).ConfigureAwait(false);
                        QueueNetworkPublish();
                        succeeded = true;
                        break;
                    case "stopScan":
                        await RunUiAsync(() =>
                        {
                            radios.Networks.CollectionChanged -= OnScannedNetworksChanged;
                            radios.StopScanning();
                        }).ConfigureAwait(false);
                        succeeded = true;
                        break;
                    default:
                        error = "The requested semantic service is not active.";
                        break;
                }
            }
            else if (request.PatchId == BluetoothPatchId && _radios is { } bluetooth)
            {
                (succeeded, error) = await ExecuteBluetoothAsync(
                    bluetooth,
                    request,
                    requestCancellation.Token).ConfigureAwait(false);
            }
            else
            {
                error = "The requested semantic service is not active.";
            }
        }
        catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested)
        {
            RemoveInflightRequest(request.Sequence);
            return;
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        try
        {
            if (requestCancellation.IsCancellationRequested)
            {
                return;
            }

            await _bridge.RespondAsync(
                    request,
                    succeeded,
                    responsePayload,
                    error,
                    requestCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"Steam UI bridge response failed: {ex.Message}");
        }
        finally
        {
            RemoveInflightRequest(request.Sequence);
        }
    }

    private void CancelInflightRequest(long sequence)
    {
        CancellationTokenSource? cancellation;
        lock (_requestGate)
        {
            _inflightRequests.TryGetValue(sequence, out cancellation);
        }

        CancelSafely(cancellation);
    }

    private void CancelAllInflightRequests()
    {
        CancellationTokenSource[] inflight;
        lock (_requestGate)
        {
            inflight = [.. _inflightRequests.Values];
        }

        foreach (CancellationTokenSource cancellation in inflight)
        {
            CancelSafely(cancellation);
        }
    }

    private static void CancelSafely(CancellationTokenSource? cancellation)
    {
        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The request completed between the bounded lookup and cancellation.
        }
    }

    private void RemoveInflightRequest(long sequence)
    {
        lock (_requestGate)
        {
            _inflightRequests.Remove(sequence);
        }
    }

    private async Task ObserveRequestCompletionAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"Steam UI semantic request failed unexpectedly: {ex.Message}");
        }
        finally
        {
            lock (_requestGate)
            {
                _requestTasks.Remove(task);
            }
        }
    }

    private static bool TryReadIntegerPayload(
        JsonElement payload,
        string propertyName,
        out int value)
    {
        value = default;
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty(propertyName, out JsonElement property)
            || property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt32(out value))
        {
            return false;
        }

        int propertyCount = 0;
        foreach (JsonProperty ignored in payload.EnumerateObject())
        {
            propertyCount++;
        }

        return propertyCount == 1;
    }

    /// <summary>Reads the one boolean an AutoTDP request may carry.</summary>
    /// <param name="payload">The request payload.</param>
    /// <param name="enabled">Receives the requested state.</param>
    /// <returns>Whether the payload was exactly one boolean named <c>enabled</c>.</returns>
    /// <remarks>
    /// Exact rather than lenient, matching the target payload beside it. The page is WSGM's own
    /// script, so anything else arriving here is either a defect or something that is not WSGM,
    /// and neither should reach a setting.
    /// </remarks>
    private static bool TryReadEnabledPayload(JsonElement payload, out bool enabled)
    {
        enabled = false;
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("enabled", out JsonElement property)
            || property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        int propertyCount = 0;
        foreach (JsonProperty ignored in payload.EnumerateObject())
        {
            propertyCount++;
        }

        if (propertyCount != 1)
        {
            return false;
        }

        enabled = property.GetBoolean();
        return true;
    }

    /// <remarks>
    /// Scan results arrive in bursts — a sweep adds rows one at a time — so publication is
    /// collapsed onto a single pending push rather than sent per row. Steam's list is rebuilt on
    /// each push, so a burst of ten additions and one push produce the same result as ten pushes.
    /// </remarks>
    private void OnScannedNetworksChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        QueueNetworkPublish();

    private void QueueNetworkPublish()
    {
        if (Interlocked.Exchange(ref _networkPublishPending, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                // One settle window per burst. Long enough that a sweep lands in a single push,
                // short enough that the list appears while the user is still looking at it.
                await Task.Delay(NetworkPublishDelay, _shutdown.Token).ConfigureAwait(false);
                Interlocked.Exchange(ref _networkPublishPending, 0);
                await PublishNetworksAsync(_shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Log.Warn($"Steam network list publish failed: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Reads the radio manager's Bluetooth view into the shape Steam's panel consumes.
    /// </summary>
    /// <param name="radios">The session's radio manager.</param>
    /// <returns>The state to publish.</returns>
    /// <remarks>
    /// Reported unavailable when the radio is off rather than as an empty device list. Steam's panel
    /// distinguishes the two — "Bluetooth is off" is a state a user can act on, while an empty list
    /// reads as "nothing found" and invites them to keep waiting for devices that will never arrive.
    /// </remarks>
    private static async Task<SteamBluetoothState> ReadBluetoothStateAsync(RadioManager radios)
    {
        List<SteamBluetoothDevice> devices = [];
        bool enabled = false;
        bool discovering = false;
        await RunUiAsync(() =>
        {
            enabled = radios.BluetoothOn;
            discovering = radios.BluetoothScanning;
            foreach (BluetoothDeviceEntry entry in radios.BluetoothDevices)
            {
                if (string.IsNullOrWhiteSpace(entry.Id))
                {
                    continue;
                }

                devices.Add(new SteamBluetoothDevice(
                    entry.Id,
                    string.IsNullOrWhiteSpace(entry.Name) ? entry.Id : entry.Name,
                    entry.Id,
                    // Steam's generic device type. WSGM does not classify Bluetooth devices, and a
                    // guessed class would put the wrong icon beside a real device.
                    0,
                    entry.Paired,
                    entry.Connected));
            }
        }).ConfigureAwait(false);

        return new SteamBluetoothState(enabled, enabled, discovering, devices);
    }

    /// <summary>
    /// Carries out one Bluetooth operation from Steam's own pairing UI.
    /// </summary>
    /// <param name="radios">The session's radio manager.</param>
    /// <param name="request">The bridge request.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Whether it succeeded, and why not when it did not.</returns>
    /// <remarks>
    /// Pairing has no direct call: <see cref="RadioManager"/> drives it through a prompt the user
    /// answers, and inventing a headless pair here would either bypass a PIN confirmation the
    /// device requires or silently fail on one that does. Steam's Pair button therefore starts
    /// discovery and lets the existing prompt flow run, which is the same path the taskbar uses.
    /// <para>
    /// Trusted and wake-allowed are accepted and do nothing. They are Linux BlueZ concepts with no
    /// Windows equivalent, and refusing them would make Steam's UI report a failure for a control
    /// that was never going to change anything.
    /// </para>
    /// </remarks>
    private static async Task<(bool Succeeded, string? Error)> ExecuteBluetoothAsync(
        RadioManager radios,
        SteamUiBridgeRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Command is "setDiscovering")
        {
            if (!TryReadEnabledPayload(request.Payload, out bool discovering))
            {
                return (false, "The discovery payload is invalid.");
            }

            // BluetoothScanning is manager-owned and driven by the same sweep as Wi-Fi, so
            // discovery goes through the scanning lifecycle rather than being set directly. One
            // sweep covering both radios is also what the taskbar's panel does.
            await RunUiAsync(() =>
            {
                if (discovering)
                {
                    radios.StartScanning();
                }
                else
                {
                    radios.StopScanning();
                }
            }).ConfigureAwait(false);
            return (true, null);
        }

        if (request.Command is "setTrusted" or "setWakeAllowed")
        {
            Log.Info($"Bluetooth: '{request.Command}' accepted with no Windows equivalent.");
            return (true, null);
        }

        if (!TryReadDeviceIdPayload(request.Payload, out string deviceId))
        {
            return (false, "The Bluetooth device payload is invalid.");
        }

        BluetoothDeviceEntry? device = null;
        await RunUiAsync(() => device = radios.BluetoothDevices.FirstOrDefault(entry =>
            string.Equals(entry.Id, deviceId, StringComparison.Ordinal))).ConfigureAwait(false);
        if (device is null)
        {
            Log.Warn($"Bluetooth: '{deviceId}' is no longer present.");
            return (false, "That device is no longer present.");
        }

        switch (request.Command)
        {
            case "connect":
                await radios.SetAudioConnectionAsync(device, connect: true).ConfigureAwait(false);
                return (true, null);
            case "disconnect":
                await radios.SetAudioConnectionAsync(device, connect: false).ConfigureAwait(false);
                return (true, null);
            case "forget":
                await radios.UnpairAsync(device).ConfigureAwait(false);
                return (true, null);
            case "pair":
                // Discovery drives the prompt; the user answers it exactly as they do from the
                // taskbar's radio panel.
                await RunUiAsync(radios.StartScanning).ConfigureAwait(false);
                return (true, null);
            case "cancelPair":
                await RunUiAsync(radios.StopScanning).ConfigureAwait(false);
                return (true, null);
            default:
                return (false, "The requested semantic service is not active.");
        }
    }

    private static bool TryReadDeviceIdPayload(JsonElement payload, out string deviceId)
    {
        deviceId = string.Empty;
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("device", out JsonElement property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        string? candidate = property.GetString();
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > 256)
        {
            return false;
        }

        deviceId = candidate;
        return true;
    }

    private async Task PublishNetworksAsync(CancellationToken cancellationToken)
    {
        if (_radios is not { } radios)
        {
            return;
        }

        List<SteamNetworkIndicator.SteamNetworkAccessPoint> networks = [];
        await RunUiAsync(() =>
        {
            foreach (WifiNetworkEntry entry in radios.Networks)
            {
                if (string.IsNullOrWhiteSpace(entry.Ssid))
                {
                    continue;
                }

                networks.Add(new SteamNetworkIndicator.SteamNetworkAccessPoint(
                    entry.Ssid,
                    entry.Signal,
                    entry.Security is not WifiSecurity.Open,
                    entry.Connected));
            }
        }).ConfigureAwait(false);

        if (networks.Count == 0)
        {
            return;
        }

        _ = await SteamNetworkIndicator.PushNetworksAsync(networks, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Runs a radio-manager call on the UI thread.
    /// </summary>
    /// <param name="action">The call to make.</param>
    /// <returns>A task completing after it ran.</returns>
    /// <remarks>
    /// <see cref="RadioManager"/> reconciles observable collections the taskbar binds to, so its
    /// scanning calls are UI-thread owned. Requests arrive off the bridge's own thread.
    /// </remarks>
    private static Task RunUiAsync(Action action) =>
        Dispatcher.UIThread.InvokeAsync(action).GetTask();

    /// <summary>Reads the endpoint and direction of a default-device change.</summary>
    /// <param name="payload">The request payload.</param>
    /// <param name="id">The endpoint identifier.</param>
    /// <param name="input">Whether the capture default is being set.</param>
    /// <returns><see langword="true"/> when the payload is exactly the expected shape.</returns>
    private static bool TryReadAudioDevicePayload(JsonElement payload, out string id, out bool input)
    {
        id = string.Empty;
        input = false;
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("id", out JsonElement idProperty)
            || idProperty.ValueKind != JsonValueKind.String
            || !payload.TryGetProperty("input", out JsonElement inputProperty)
            || inputProperty.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        string? candidate = idProperty.GetString();
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > 512)
        {
            return false;
        }

        id = candidate;
        input = inputProperty.ValueKind is JsonValueKind.True;
        return true;
    }

    /// <summary>Reads the target volume of a volume change.</summary>
    /// <param name="payload">The request payload.</param>
    /// <param name="percent">The requested volume, 0-100.</param>
    /// <returns><see langword="true"/> when the payload is exactly the expected shape.</returns>
    private static bool TryReadAudioVolumePayload(JsonElement payload, out int percent)
    {
        percent = 0;
        return payload.ValueKind is JsonValueKind.Object
            && payload.TryGetProperty("percent", out JsonElement property)
            && property.ValueKind is JsonValueKind.Number
            && property.TryGetInt32(out percent)
            && percent is >= 0 and <= 100;
    }

    /// <summary>Serializes the audio state into the shape Steam's store reads.</summary>
    /// <param name="state">The state to send.</param>
    /// <returns>The payload element.</returns>
    /// <remarks>
    /// The device shape here is deliberately WSGM's own; the bootstrap maps it into Steam's field
    /// names. Emitting Steam's names on this side would put its schema in two places, and the one
    /// that changes with a client rebuild is the injected half.
    /// </remarks>
    private static JsonElement SerializeAudioState(NativeQamAudioState state)
    {
        string json = JsonSerializer.Serialize(
            state,
            NativeQamSemanticJsonContext.Default.NativeQamAudioState);
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static bool TryReadTargetPayload(JsonElement payload, out string target)
    {
        target = string.Empty;
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("target", out JsonElement property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        int propertyCount = 0;
        foreach (JsonProperty ignored in payload.EnumerateObject())
        {
            propertyCount++;
        }

        string? candidate = property.GetString();
        if (propertyCount != 1
            || candidate is not { Length: >= 1 and <= 64 }
            || !ValidTargetId(candidate))
        {
            return false;
        }

        target = candidate;
        return true;
    }

    private static bool TryReadPerformancePayload(
        JsonElement payload,
        out int value,
        out PerformancePersistenceTarget persistence)
    {
        value = default;
        persistence = default;
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("value", out JsonElement valueProperty)
            || valueProperty.ValueKind != JsonValueKind.Number
            || !valueProperty.TryGetInt32(out value)
            || !payload.TryGetProperty("persistence", out JsonElement persistenceProperty)
            || persistenceProperty.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        persistence = persistenceProperty.GetString() switch
        {
            "automatic" => PerformancePersistenceTarget.Automatic,
            "global" => PerformancePersistenceTarget.Global,
            "application" => PerformancePersistenceTarget.Application,
            _ => (PerformancePersistenceTarget)(-1),
        };
        int propertyCount = 0;
        foreach (JsonProperty ignored in payload.EnumerateObject())
        {
            propertyCount++;
        }

        return propertyCount == 2
            && persistence is PerformancePersistenceTarget.Automatic
                or PerformancePersistenceTarget.Global
                or PerformancePersistenceTarget.Application;
    }

    private static string CorrelationId(SteamUiBridgeRequest request) =>
        $"native-qam:{request.ContextGeneration}:{request.DocumentGeneration}:"
        + $"{request.Sequence}:{request.ActionGeneration}";

    private static bool ValidTargetId(string target)
    {
        foreach (char character in target)
        {
            if (!(character is >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await DisableAsync().ConfigureAwait(false);
        _disposed = true;
        _transport.GenerationChanged -= OnGenerationChanged;
        _bridge.RequestReceived -= OnRequestReceived;
        _tdp.StateChanged -= OnSemanticStateChanged;
        _performance.StateChanged -= OnSemanticStateChanged;
        _autoTdp.StateChanged -= OnSemanticStateChanged;
        _controllerTarget.StateChanged -= OnSemanticStateChanged;
        if (_audio is not null)
        {
            _audio.StateChanged -= OnSemanticStateChanged;
        }

        // A session that ends while Steam's network page is open would otherwise leave the radio
        // sweeping and this host subscribed to a collection it no longer publishes.
        if (_radios is { } radios)
        {
            radios.Networks.CollectionChanged -= OnScannedNetworksChanged;
            await RunUiAsync(radios.StopScanning).ConfigureAwait(false);
        }

        _enabled = false;
        ReleasePerformanceObservation();
        CancelAllInflightRequests();
        _shutdown.Cancel();
        try
        {
            await Task.WhenAll(_synchronization, _publication).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        Task[] requestTasks;
        lock (_requestGate)
        {
            requestTasks = [.. _requestTasks];
        }
        try
        {
            await Task.WhenAll(requestTasks).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"Steam UI semantic request cleanup failed: {ex.Message}");
        }

        await _patches.DisposeAsync().ConfigureAwait(false);
        await _bridge.DisposeAsync().ConfigureAwait(false);
        _autoTdp.Dispose();
        _audio?.Dispose();
        _controllerTarget.Dispose();
        _performance.Dispose();
        _tdp.Dispose();
        _synchronizeSignal.Dispose();
        _publicationSignal.Dispose();
        _shutdown.Dispose();
    }
}
