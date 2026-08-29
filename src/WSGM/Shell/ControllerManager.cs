using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Input;
using WSGM.Device.Sdk.Ipc;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Input;

namespace WSGM.Shell;

/// <summary>Truthful state of WSGM's controller management for one session.</summary>
internal enum ControllerManagementState
{
    /// <summary>The user has not enabled controller management, or the release gate is closed.</summary>
    Off,

    /// <summary>Enabled, but no usable backend exists on this machine.</summary>
    Unavailable,

    /// <summary>Enabled and ready, with no virtual target present.</summary>
    Idle,

    /// <summary>A virtual target exists and canonical samples reach it.</summary>
    Active,

    /// <summary>Management faulted for this run; input falls back to SDL and the Steam lease.</summary>
    Faulted,
}

/// <summary>The complete controller-management projection consumed by the overlay and diagnostics.</summary>
internal sealed record ControllerManagerStatus(
    ControllerManagementState State,
    ManagedControllerTarget? Target,
    ControllerTargetSource TargetSource,
    string? ApplicationId,
    UiInputSource UiSource,
    string Detail);

/// <summary>
/// The one owner of WSGM's controller management for a session.
/// </summary>
/// <remarks>
/// Everything WSGM does to the controller happens here: the virtual target and its replacement, the
/// haptic return path, WSGM's owned HidHide delta, the local UI capture, the source WSGM's own
/// surfaces navigate from, and the make-safe handoff. There is deliberately no second policy layer
/// between a setting and this object: the overlay, Settings, and the shared running-application
/// monitor all call it directly.
/// <para>
/// The plugin half stays where it is. <see cref="DeviceCoordinator"/> owns the conversation with
/// DeviceHost; this object owns WSGM's half and orders the two through
/// <see cref="ControllerMakeSafeSequence"/>.
/// </para>
/// </remarks>
internal sealed class ControllerManager : IAsyncDisposable
{
    private readonly IHidBackend _backend;
    private readonly HidHideOwnedDeltaManager _hidHide;
    private readonly ManagedControllerRouter _router;
    private readonly UiCaptureState _uiCapture = new();
    private readonly SemaphoreSlim _transition = new(1, 1);
    private readonly string _deviceHostApplication;
    private readonly object _stateGate = new();

    private IReadOnlyList<PhysicalDeviceIdentity> _physicalDevices = [];
    private ControllerSelection _selection = new(
        Enabled: false,
        ManagedControllerTarget.SteamDeckComposite,
        [],
        "Controller management has not started.");
    private ResolvedControllerTarget? _effective;
    private ZeroOutputTrigger _zeroTriggers = ZeroOutputTrigger.None;
    private CanonicalButtons _lastButtons;
    private long _sourceGeneration;
    private bool _outputActive;

    // Written under the transition gate but read from the sample path, which must not take it.
    private volatile bool _disposed;

    internal ControllerManager(
        IHidBackend backend,
        IPhysicalHapticSink hapticSink,
        HidHideOwnedDeltaManager hidHide,
        string deviceHostApplication,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(hapticSink);
        ArgumentNullException.ThrowIfNull(hidHide);
        _backend = backend;
        _hidHide = hidHide;
        _deviceHostApplication = deviceHostApplication;
        _router = new ManagedControllerRouter(backend, hapticSink, timeProvider);
    }

    /// <summary>Raised when the projection changes, for the overlay and Settings.</summary>
    internal event Action<ControllerManagerStatus>? StatusChanged;

    /// <summary>Raised for each canonical sample WSGM's own surfaces should navigate from.</summary>
    internal event Action<CanonicalControllerSample>? UiSampleReceived;

    /// <summary>Every physical sample, unfiltered, for diagnostics only.</summary>
    /// <remarks>
    /// Raised before routing and never used to drive input. It exists so a surface can show what
    /// the plugin actually reports — which is not what <see cref="UiSampleReceived"/> carries, since
    /// that one has the controls the UI is using filtered out.
    /// </remarks>
    internal event Action<CanonicalControllerSample>? PhysicalSampleObserved;

    /// <summary>Current state of controller management.</summary>
    internal ControllerManagementState State { get; private set; } = ControllerManagementState.Off;

    /// <summary>Why the current state holds, for logs and the overlay.</summary>
    internal string Detail { get; private set; } = "Controller management has not started.";

    /// <summary>Where WSGM's own surfaces are reading controller input from.</summary>
    /// <remarks>
    /// The managed source is used only while a healthy target is actually being driven. Every other
    /// state falls back to SDL with the Steam Input lease, which is why that path stays a permanent
    /// capability rather than a transitional one.
    /// </remarks>
    internal UiInputSource UiSource => State is ControllerManagementState.Active
        ? UiInputSource.ManagedCanonical
        : UiInputSource.SdlWithSteamLease;

    /// <summary>The target in effect and the layer that chose it.</summary>
    internal ResolvedControllerTarget? Effective => _effective;

    /// <summary>Returns the current projection.</summary>
    /// <returns>The controller-management projection.</returns>
    internal ControllerManagerStatus Snapshot() => new(
        State,
        _effective?.Target,
        _effective?.Source ?? ControllerTargetSource.GlobalDefault,
        _effective?.ApplicationId,
        UiSource,
        Detail);

    /// <summary>
    /// Starts controller management for the current plugin cycle.
    /// </summary>
    /// <param name="selection">The controller selection in effect.</param>
    /// <param name="physicalDevices">Physical devices the plugin owns and WSGM must hide.</param>
    /// <param name="applicationId">Canonical identity of the running application, when known.</param>
    /// <param name="sourceGeneration">Cycle generation the canonical samples carry.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    /// <returns>The resulting projection.</returns>
    /// <remarks>
    /// Fails open in every unavailable case. A missing backend, unhealthy HidHide, or a target that
    /// does not enumerate leaves the shell, the SDL path, and the Steam Input lease exactly as they
    /// were; it never changes global HidHide state and never removes an external owner's entries.
    /// </remarks>
    internal async Task<ControllerManagerStatus> StartAsync(
        ControllerSelection selection,
        IReadOnlyList<PhysicalDeviceIdentity> physicalDevices,
        string? applicationId,
        long sourceGeneration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(physicalDevices);
        await _transition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _physicalDevices = physicalDevices;
            _selection = selection;
            _sourceGeneration = sourceGeneration;

            if (!selection.Enabled)
            {
                return SetState(ControllerManagementState.Off, selection.DisabledDetail);
            }

            HidBackendHealth health = await _backend.DiscoverAsync(cancellationToken)
                .ConfigureAwait(false);
            if (health.State is not HidBackendHealthState.Ready || health.Capabilities is null)
            {
                return SetState(ControllerManagementState.Unavailable, health.Detail);
            }

            ResolvedControllerTarget resolved = ControllerTargetSelection.Resolve(
                selection.GlobalDefault,
                selection.Overrides,
                applicationId);
            if (!health.Capabilities.SupportedTargets.Contains(
                ControllerTargetSelection.ToVirtualTarget(resolved.Target)))
            {
                return SetState(
                    ControllerManagementState.Unavailable,
                    $"The backend cannot create a {resolved.Target} target.");
            }

            HidHideActivationResult hidHide = await _hidHide.StartAsync(
                controllerManagementEnabled: true,
                _deviceHostApplication,
                physicalDevices,
                sourceGeneration,
                cancellationToken).ConfigureAwait(false);
            if (!hidHide.Activated)
            {
                return SetState(ControllerManagementState.Unavailable, hidHide.Detail);
            }

            try
            {
                await CreateTargetUnderGateAsync(resolved, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Error("Controller management could not create its virtual target", ex);
                await CleanupHidHideUnderGateAsync(cancellationToken).ConfigureAwait(false);
                return SetState(ControllerManagementState.Faulted, ex.Message);
            }

            return SetState(
                ControllerManagementState.Active,
                $"Managed target {resolved.Target} is active ({resolved.Source}).");
        }
        finally
        {
            _transition.Release();
        }
    }

    /// <summary>
    /// Applies a changed selection, replacing the target when the effective target changed.
    /// </summary>
    /// <param name="selection">The new controller selection.</param>
    /// <param name="applicationId">Canonical identity of the running application, when known.</param>
    /// <param name="cancellationToken">Cancels the apply.</param>
    /// <returns>The resulting projection.</returns>
    /// <remarks>
    /// Turning management off here is not the same as a make-safe handoff and deliberately does not
    /// perform one: the caller that owns the plugin conversation runs
    /// <see cref="MakeSafeAsync"/> so the physical release is ordered against WSGM's own removal.
    /// </remarks>
    internal async Task<ControllerManagerStatus> ApplySelectionAsync(
        ControllerSelection selection,
        string? applicationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        await _transition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _selection = selection;
            return await ReconcileTargetUnderGateAsync(applicationId, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _transition.Release();
        }
    }

    /// <summary>
    /// Applies a running-application change from the one shared monitor.
    /// </summary>
    /// <param name="snapshot">The canonical running-application snapshot.</param>
    /// <param name="cancellationToken">Cancels the apply.</param>
    /// <returns>The resulting projection.</returns>
    /// <remarks>
    /// The same monitor resolves the RTSS profile, so the controller target and the performance
    /// profile can never disagree about which application is running.
    /// </remarks>
    internal async Task<ControllerManagerStatus> ApplyRunningApplicationAsync(
        RunningApplicationTargetSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await _transition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await ReconcileTargetUnderGateAsync(snapshot.ApplicationId, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _transition.Release();
        }
    }

    /// <summary>
    /// Forwards one canonical sample published by the plugin.
    /// </summary>
    /// <param name="sample">The sample the plugin published.</param>
    /// <remarks>
    /// A captured sample never reaches the virtual target. It reaches WSGM's own surfaces with the
    /// controls held at capture filtered out, so the chord that opened the overlay cannot activate
    /// whatever now has focus underneath it.
    /// </remarks>
    internal void Submit(CanonicalControllerSample sample) =>
        Observe(RouteAsync(sample, CancellationToken.None), "sample route");

    /// <summary>Routes one canonical sample and reports whether it reached the virtual target.</summary>
    /// <param name="sample">The sample the plugin published.</param>
    /// <param name="cancellationToken">Cancels the route.</param>
    /// <returns><see langword="true"/> when the sample reached the virtual target.</returns>
    internal async Task<bool> RouteAsync(
        CanonicalControllerSample sample,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sample);

        // Raised before any routing decision and deliberately unfiltered, because this is what the
        // plugin reported. The filtered stream that follows is what the UI may act on; a diagnostic
        // that showed only that would hide the controls the UI had swallowed, which are exactly the
        // ones someone checking a mapping needs to see. Read-only: an observer cannot change what
        // is routed, so it is not a second input path.
        PhysicalSampleObserved?.Invoke(sample);

        bool toUi;
        CanonicalButtons uiButtons;
        lock (_stateGate)
        {
            if (_disposed)
            {
                return false;
            }

            _lastButtons = sample.Buttons;
            // Forwarding resumes only on a clean boundary: every control the UI used has to be
            // released first, or the game sees a press whose start it never saw.
            toUi = _uiCapture.IsCaptured
                || _zeroTriggers is not ZeroOutputTrigger.None
                || !_uiCapture.CanResumeForwarding(sample.Buttons);
            uiButtons = toUi ? _uiCapture.FilterForUi(sample.Buttons) : sample.Buttons;
        }

        if (toUi)
        {
            UiSampleReceived?.Invoke(sample with { Buttons = uiButtons });
            return false;
        }

        // Capture and every other zero trigger leave the target neutral rather than removed, so the
        // first clean sample after they clear is what re-arms forwarding. Doing it here, on the
        // sample that proved the boundary is clean, is the only point at which resuming is safe.
        if (_router.State is ManagedTargetState.Neutral && _router.Target is not null)
        {
            _router.ActivateSource(_sourceGeneration);
        }

        return await _router.RouteAsync(sample, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Claims controller input for one WSGM surface.</summary>
    /// <param name="surfaceId">Identifier of the claiming surface.</param>
    /// <param name="cancellationToken">Cancels the claim.</param>
    /// <returns>A task completing once the target has been left neutral.</returns>
    internal Task ClaimUiAsync(string surfaceId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(surfaceId);
        bool started;
        lock (_stateGate)
        {
            started = _uiCapture.Claim(surfaceId, _lastButtons);
        }

        return started
            ? AddZeroTriggerAsync(ZeroOutputTrigger.UiCaptureClaimed, "ui-capture", cancellationToken)
            : Task.CompletedTask;
    }

    /// <summary>Releases one surface's claim on controller input.</summary>
    /// <param name="surfaceId">Identifier of the releasing surface.</param>
    /// <remarks>
    /// Releasing the last claim does not resume forwarding by itself. Forwarding resumes on the
    /// first sample in which every control the UI used is up, so the press that closed the surface
    /// never arrives in the game as a fresh input.
    /// </remarks>
    internal void ReleaseUi(string surfaceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(surfaceId);
        bool released;
        lock (_stateGate)
        {
            released = _uiCapture.Release(surfaceId);
        }

        if (released)
        {
            RemoveZeroTrigger(ZeroOutputTrigger.UiCaptureClaimed);
        }
    }

    /// <summary>Adds a reason the virtual target must be left in a neutral state.</summary>
    /// <param name="trigger">The trigger to add.</param>
    /// <param name="reason">Diagnostic reason recorded with the stop.</param>
    /// <param name="cancellationToken">Cancels the neutralization.</param>
    /// <returns>A task completing once the target has been left neutral.</returns>
    internal Task AddZeroTriggerAsync(
        ZeroOutputTrigger trigger,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        bool neutralize;
        lock (_stateGate)
        {
            ZeroOutputTrigger previous = _zeroTriggers;
            _zeroTriggers |= trigger;
            neutralize = previous != _zeroTriggers
                && (OutputRouting.RequiresStop(_zeroTriggers, _outputActive)
                    || State is ControllerManagementState.Active);
            _outputActive = false;
        }

        return neutralize
            ? _router.NeutralizeAsync(reason, cancellationToken)
            : Task.CompletedTask;
    }

    /// <summary>Removes a reason the virtual target must be left in a neutral state.</summary>
    /// <param name="trigger">The trigger to remove.</param>
    internal void RemoveZeroTrigger(ZeroOutputTrigger trigger)
    {
        lock (_stateGate)
        {
            _zeroTriggers &= ~trigger;
        }
    }

    /// <summary>
    /// Runs the complete make-safe handoff and returns its combined result.
    /// </summary>
    /// <param name="scope">Whether only the controller or the whole cycle is being released.</param>
    /// <param name="releasePhysicalAsync">Asks the plugin to stop reading and restore its mode.</param>
    /// <param name="cancellationToken">Cancels the handoff.</param>
    /// <returns>The handoff response describing both halves of the sequence.</returns>
    /// <remarks>
    /// The returned response is WSGM's, not the plugin's: it reports how far the whole sequence got,
    /// including the WSGM-owned removal that runs after an unverified or failed plugin answer. The
    /// user's stop request is always honoured; the result records whether it could be verified.
    /// </remarks>
    internal async Task<DeviceControllerHandoffResponse> MakeSafeAsync(
        HandoffScope scope,
        Func<CancellationToken, Task<DeviceControllerHandoffResponse>> releasePhysicalAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(releasePhysicalAsync);
        await _transition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await MakeSafeUnderGateAsync(scope, releasePhysicalAsync, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _transition.Release();
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _transition.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }
        finally
        {
            _transition.Release();
        }

        // Order matters here exactly as it does in the make-safe sequence: the router removes the
        // virtual target first, and only then are WSGM's HidHide entries dropped.
        await _router.DisposeAsync().ConfigureAwait(false);
        await CleanupHidHideUnderGateAsync(CancellationToken.None).ConfigureAwait(false);
        _transition.Dispose();
    }

    private async Task<DeviceControllerHandoffResponse> MakeSafeUnderGateAsync(
        HandoffScope scope,
        Func<CancellationToken, Task<DeviceControllerHandoffResponse>> releasePhysicalAsync,
        CancellationToken cancellationToken)
    {
        ControllerMakeSafeSequence sequence = new();
        IReadOnlyList<PhysicalDeviceIdentity> released = [];

        try
        {
            await _router.NeutralizeAsync("make-safe", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Controller make-safe could not verify a neutral target: {ex.Message}");
        }

        lock (_stateGate)
        {
            _zeroTriggers |= ZeroOutputTrigger.TargetRemoved;
            _outputActive = false;
        }

        sequence.RecordNeutralized();

        try
        {
            DeviceControllerHandoffResponse plugin = await releasePhysicalAsync(cancellationToken)
                .ConfigureAwait(false);
            released = plugin.ReleasedDevices;
            sequence.RecordPluginRelease(plugin.Step, plugin.Result);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            sequence.RecordPluginReleaseUnobserved();
            Log.Warn($"Controller make-safe: the plugin release was unverified: {ex.Message}");
        }

        try
        {
            await _router.RemoveAsync("make-safe", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Controller make-safe could not verify target removal: {ex.Message}");
        }

        // Recorded even when removal was unverified: leaving WSGM's HidHide entries behind would
        // hide the physical controller from every application with nothing driving it, which is a
        // worse outcome than an unverified removal the result already reports.
        sequence.RecordTargetRemoved();
        sequence.RecordHidHideRemoved(
            await CleanupHidHideUnderGateAsync(cancellationToken).ConfigureAwait(false));

        ControllerHandoffResult result = sequence.Complete();
        SetState(
            scope is HandoffScope.FullDeactivation
                ? ControllerManagementState.Off
                : ControllerManagementState.Idle,
            $"Controller make-safe completed: {sequence.Step}, {result}.");
        Log.Info(
            $"Controller make-safe: scope={scope}, step={sequence.Step}, result={result}, "
            + $"targetRemoved={sequence.TargetRemoved}, hidHideRemoved={sequence.HidHideRemoved}.");
        return new DeviceControllerHandoffResponse
        {
            Step = sequence.Step,
            Result = result,
            ReleasedDevices = released,
        };
    }

    private async Task<bool> CleanupHidHideUnderGateAsync(CancellationToken cancellationToken)
    {
        try
        {
            HidHideCleanupResult cleanup = await _hidHide.CleanupAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!cleanup.Verified)
            {
                Log.Warn($"Controller HidHide cleanup was unverified: {cleanup.Detail}");
            }

            return cleanup.Verified;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error("Controller HidHide cleanup failed", ex);
            return false;
        }
    }

    private async Task<ControllerManagerStatus> ReconcileTargetUnderGateAsync(
        string? applicationId,
        CancellationToken cancellationToken)
    {
        ResolvedControllerTarget resolved = ControllerTargetSelection.Resolve(
            _selection.GlobalDefault,
            _selection.Overrides,
            applicationId);
        // A disabled selection is not reconciled here. Removing the target without ordering it
        // against the plugin's physical release is the duplicate-input window make-safe exists to
        // prevent, so the caller that owns the plugin conversation runs that sequence instead.
        if (State is not ControllerManagementState.Active || !_selection.Enabled)
        {
            return Snapshot();
        }

        if (_effective is { } current && current.Target == resolved.Target)
        {
            _effective = resolved;
            return Snapshot();
        }

        try
        {
            // Replacement is one operation on purpose: the old target is neutralized and removed
            // before the new one is created, so no window exists in which both are enumerated.
            HidTargetHandle target = await _router.ReplaceAsync(
                ControllerTargetSelection.ToVirtualTarget(resolved.Target),
                _sourceGeneration,
                cancellationToken).ConfigureAwait(false);
            _router.ActivateSource(_sourceGeneration);
            _effective = resolved;
            Log.Info(
                $"Managed controller target replaced: {resolved.Target} ({resolved.Source}), "
                + $"generation={target.Generation}.");
            return SetState(
                ControllerManagementState.Active,
                $"Managed target {resolved.Target} is active ({resolved.Source}).");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error("Managed controller target replacement failed", ex);
            await CleanupHidHideUnderGateAsync(cancellationToken).ConfigureAwait(false);
            return SetState(ControllerManagementState.Faulted, ex.Message);
        }
    }

    private async Task CreateTargetUnderGateAsync(
        ResolvedControllerTarget resolved,
        CancellationToken cancellationToken)
    {
        HidTargetHandle target = await _router.CreateAsync(
            ControllerTargetSelection.ToVirtualTarget(resolved.Target),
            _sourceGeneration,
            cancellationToken).ConfigureAwait(false);
        _router.ActivateSource(_sourceGeneration);
        _effective = resolved;
        lock (_stateGate)
        {
            _zeroTriggers = ZeroOutputTrigger.None;
            _outputActive = false;
        }

        Log.Info(
            $"Managed controller target created: {resolved.Target} ({resolved.Source}), "
            + $"generation={target.Generation}, devices={_physicalDevices.Count}.");
    }

    private ControllerManagerStatus SetState(ControllerManagementState state, string detail)
    {
        State = state;
        Detail = detail;
        if (state is not (ControllerManagementState.Active or ControllerManagementState.Idle))
        {
            _effective = null;
        }

        ControllerManagerStatus status = Snapshot();
        StatusChanged?.Invoke(status);
        return status;
    }

    private static void Observe(Task task, string operation)
    {
        _ = ObserveAsync(task, operation);

        static async Task ObserveAsync(Task task, string operation)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Log.Warn($"Controller {operation} failed: {ex.Message}");
            }
        }
    }
}
