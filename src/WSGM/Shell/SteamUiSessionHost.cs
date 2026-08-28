using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

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
    private const string ControllerTargetPatchId = "wsgm.native-qam.controller-target";
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
    private readonly PerformanceServiceNativeQamAdapter _performance;
    private readonly INativeQamControllerTargetService _controllerTarget;
    private readonly SteamUiBridgeHost _bridge;
    private readonly SteamUiPatchManager _patches;
    private readonly Task _synchronization;
    private readonly Task _publication;
    private int _signalPending;
    private int _publicationPending;
    private IDisposable? _performanceObservation;
    private volatile bool _enabled;
    private volatile bool _disposed;

    internal SteamUiSessionHost(
        PersistentSteamUiTransport transport,
        Func<CancellationToken, Task<bool>> toggleQuickAccess,
        DeviceCoordinator? deviceCoordinator,
        PerformanceService performance)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        ArgumentNullException.ThrowIfNull(toggleQuickAccess);
        _toggleQuickAccess = toggleQuickAccess;
        _tdp = deviceCoordinator is null
            ? new UnavailableNativeQamTdpService()
            : new DeviceCoordinatorNativeQamTdpService(deviceCoordinator);
        _performance = new PerformanceServiceNativeQamAdapter(performance);
        _controllerTarget = new UnavailableNativeQamControllerTargetService();
        _bridge = new SteamUiBridgeHost(_transport);
        _patches = new SteamUiPatchManager(_transport);
        _patches.Register(new NativeQamBootstrapPatch(_bridge));
        _patches.Register(new NativeQamTdpPatch());
        _patches.Register(new NativeQamFrameLimitPatch());
        _patches.Register(new NativeQamOverlayLevelPatch());
        _patches.Register(new NativeQamControllerTargetPatch());
        _bridge.RequestReceived += OnRequestReceived;
        _transport.GenerationChanged += OnGenerationChanged;
        _tdp.StateChanged += OnSemanticStateChanged;
        _performance.StateChanged += OnSemanticStateChanged;
        _controllerTarget.StateChanged += OnSemanticStateChanged;
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

    internal async Task DisableAsync()
    {
        if (_disposed)
        {
            return;
        }

        _enabled = false;
        CancelAllInflightRequests();
        ReleasePerformanceObservation();
        SetPatchStates(bootstrap: true, components: false);
        await _patches.SynchronizeAsync(_shutdown.Token).ConfigureAwait(false);
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

        if (_enabled && snapshot.Role == SteamUiTargetRole.SharedJsContext)
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
                    _patches.SetGlobalEnabled(false);
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
        _patches.SetPatchEnabled(FrameLimitPatchId, components);
        _patches.SetPatchEnabled(OverlayLevelPatchId, components);
        _patches.SetPatchEnabled(ControllerTargetPatchId, components);
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
            else if (request.PatchId == ControllerTargetPatchId
                && request.Command == "setControllerTarget")
            {
                if (!TryReadTargetPayload(request.Payload, out string? target))
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

            await _bridge.RespondAsync(request, succeeded, null, error, requestCancellation.Token)
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

    private static bool TryReadTargetPayload(JsonElement payload, out string? target)
    {
        target = null;
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

        target = property.GetString();
        return propertyCount == 1 && target is { Length: >= 1 and <= 64 } && ValidTargetId(target);
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
        _controllerTarget.StateChanged -= OnSemanticStateChanged;
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
        _controllerTarget.Dispose();
        _performance.Dispose();
        _tdp.Dispose();
        _synchronizeSignal.Dispose();
        _publicationSignal.Dispose();
        _shutdown.Dispose();
    }
}
