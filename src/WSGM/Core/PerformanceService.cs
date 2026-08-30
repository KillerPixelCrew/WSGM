using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Pure global/per-application RTSS policy and edit-target resolution.</summary>
internal static class PerformancePolicyResolver
{
    internal static (
        PerformanceValues Values,
        PerformancePolicyLayer FrameLimitLayer,
        PerformancePolicyLayer OverlayLevelLayer) Resolve(
        PerformancePolicy policy,
        RtssApplicationTarget? target)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!policy.Enabled)
        {
            return (
                PerformanceValues.Empty,
                PerformancePolicyLayer.None,
                PerformancePolicyLayer.None);
        }

        PerformanceApplicationPolicy? application = Find(policy, target?.ApplicationId);
        PerformanceValues persistent = application is null
            ? policy.Global
            : new PerformanceValues(
                application.Values.FrameLimit ?? policy.Global.FrameLimit,
                application.Values.OverlayLevel ?? policy.Global.OverlayLevel);
        return (
            persistent,
            LayerFor(application?.Values.FrameLimit, policy.Global.FrameLimit),
            LayerFor(application?.Values.OverlayLevel, policy.Global.OverlayLevel));
    }

    internal static PerformancePersistenceTarget ResolveEditTarget(
        PerformancePolicy policy,
        RtssApplicationTarget? target,
        PerformancePersistenceTarget requested)
    {
        if (requested != PerformancePersistenceTarget.Automatic)
        {
            return requested;
        }

        return Find(policy, target?.ApplicationId) is null
            ? PerformancePersistenceTarget.Global
            : PerformancePersistenceTarget.Application;
    }

    internal static PerformancePolicy Write(
        PerformancePolicy policy,
        RtssApplicationTarget? target,
        PerformancePersistenceTarget persistence,
        PerformanceControl control,
        int value)
    {
        if (persistence == PerformancePersistenceTarget.Global)
        {
            return policy with { Global = policy.Global.With(control, value) };
        }

        if (persistence != PerformancePersistenceTarget.Application || target is null)
        {
            throw new InvalidOperationException("An application edit requires an active application target.");
        }

        List<PerformanceApplicationPolicy> applications = [.. policy.Applications];
        int index = applications.FindIndex(item => string.Equals(
            item.ApplicationId,
            target.ApplicationId,
            StringComparison.Ordinal));
        if (index < 0)
        {
            applications.Add(new PerformanceApplicationPolicy(
                target.ApplicationId,
                target.RtssProfileName,
                PerformanceValues.Empty.With(control, value)));
        }
        else
        {
            PerformanceApplicationPolicy current = applications[index];
            applications[index] = current with
            {
                RtssProfileName = target.RtssProfileName,
                Values = current.Values.With(control, value),
            };
        }

        return policy with { Applications = applications.ToArray() };
    }

    internal static PerformanceApplicationPolicy? Find(
        PerformancePolicy policy,
        string? applicationId) => string.IsNullOrWhiteSpace(applicationId)
            ? null
            : policy.Applications.FirstOrDefault(item => string.Equals(
                item.ApplicationId,
                applicationId,
                StringComparison.Ordinal));

    private static PerformancePolicyLayer LayerFor(int? application, int? global) =>
        application is not null
            ? PerformancePolicyLayer.Application
            : global is not null ? PerformancePolicyLayer.Global : PerformancePolicyLayer.None;
}

/// <summary>
/// One session-owned RTSS service shared by every UI projection. Adapter access and commands are
/// serialized, polling runs only while a client holds an observation lease, and RTSS failures never
/// escape into shell/session transitions.
/// </summary>
internal sealed class PerformanceService : IAsyncDisposable
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(2);
    private static readonly RtssProbe InitialProbe = new(
        RtssAvailability.Unknown,
        null,
        null,
        null,
        null,
        0,
        null,
        "RTSS discovery has not run.");

    private readonly IRtssAdapter _adapter;
    private readonly Func<PerformancePolicy, CancellationToken, Task> _persistPolicy;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _commandTimeout;
    private readonly TimeProvider _timeProvider;
    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _adapterGate = new(1, 1);
    private readonly SemaphoreSlim _observerSignal = new(0, 1);
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly RtssLauncher _launcher;
    private readonly Task _pollTask;
    private PerformancePolicy _policy;
    private PerformanceState _state;
    private int _observerCount;
    private long _commandSequence;
    private bool _disposed;

    internal PerformanceService(
        IRtssAdapter adapter,
        Func<PerformancePolicy, CancellationToken, Task> persistPolicy,
        PerformancePolicy? policy = null,
        TimeSpan? pollInterval = null,
        TimeSpan? commandTimeout = null,
        TimeProvider? timeProvider = null,
        RtssLauncher? launcher = null)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        // Injected so a test can assert the decision without a test ever starting a process. The
        // default one only starts the executable discovery already verified.
        _launcher = launcher ?? new RtssLauncher();
        _persistPolicy = persistPolicy ?? throw new ArgumentNullException(nameof(persistPolicy));
        _policy = NormalizePolicy(policy ?? PerformancePolicy.Empty);
        _pollInterval = BoundInterval(pollInterval ?? DefaultPollInterval);
        _commandTimeout = BoundTimeout(commandTimeout ?? DefaultCommandTimeout);
        _timeProvider = timeProvider ?? TimeProvider.System;
        (
            PerformanceValues desired,
            PerformancePolicyLayer frameLimitLayer,
            PerformancePolicyLayer overlayLevelLayer) = PerformancePolicyResolver.Resolve(
            _policy,
            null);
        _state = new PerformanceState(
            InitialProbe,
            null,
            frameLimitLayer,
            overlayLevelLayer,
            desired,
            PerformanceValues.Empty,
            PerformanceReadbackQuality.Unavailable,
            PerformanceReadbackQuality.Unavailable,
            RtssTelemetryHealth.Unavailable,
            null,
            PerformanceCommandState.Idle);
        _pollTask = Task.Run(PollAsync);
    }

    internal event Action<PerformanceState>? StateChanged;

    internal event Action<PerformancePolicy>? PolicyChanged;

    internal PerformanceState Current
    {
        get
        {
            lock (_stateGate)
            {
                return _state;
            }
        }
    }

    internal int ObserverCount => Volatile.Read(ref _observerCount);

    internal bool Enabled
    {
        get
        {
            lock (_stateGate)
            {
                return _policy.Enabled;
            }
        }
    }

    internal TimeSpan PollInterval => _pollInterval;

    internal IDisposable AcquireObservation()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Interlocked.Increment(ref _observerCount) == 1)
        {
            TrySignalObserver();
        }

        return new ObservationLease(this);
    }

    internal async Task UpdatePolicyAsync(
        PerformancePolicy policy,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(policy);
        PerformancePolicy normalized = NormalizePolicy(policy);
        PerformanceState next;
        lock (_stateGate)
        {
            if (PoliciesEqual(_policy, normalized))
            {
                return;
            }

            _policy = normalized;
            next = WithResolvedDesired(_state);
            _state = next;
        }

        RaiseStateChanged(next);
        await ApplyEffectiveDesiredAsync("policy-reload", cancellationToken).ConfigureAwait(false);
    }

    internal async Task SetTargetAsync(
        RtssApplicationTarget? target,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (target is not null && !ValidTarget(target))
        {
            throw new ArgumentException("The RTSS application target is invalid.", nameof(target));
        }

        if (target is not null)
        {
            target = target with
            {
                ApplicationId = target.ApplicationId.Trim(),
                RtssProfileName = target.RtssProfileName.Trim(),
            };
        }

        PerformanceState next;
        lock (_stateGate)
        {
            if (_state.Target == target)
            {
                return;
            }

            _state = WithResolvedDesired(_state with { Target = target });
            next = _state;
        }

        RaiseStateChanged(next);
        await ApplyEffectiveDesiredAsync("application-transition", cancellationToken).ConfigureAwait(false);
    }

    internal Task<PerformanceCommandState> SetAsync(
        PerformanceControl control,
        int value,
        PerformancePersistenceTarget persistence,
        string origin,
        string correlationId,
        CancellationToken cancellationToken = default) => SetCoreAsync(
            control,
            value,
            persistence,
            origin,
            correlationId,
            cancellationToken,
            updateDesired: true);

    private async Task<PerformanceCommandState> SetCoreAsync(
        PerformanceControl control,
        int value,
        PerformancePersistenceTarget persistence,
        string origin,
        string correlationId,
        CancellationToken cancellationToken,
        bool updateDesired)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        origin = SanitizeToken(origin, "unknown");
        correlationId = SanitizeToken(correlationId, Guid.NewGuid().ToString("N"));
        long sequence = Interlocked.Increment(ref _commandSequence);
        UpdateCommand(new(
            sequence,
            origin,
            correlationId,
            control,
            value,
            PerformanceCommandPhase.Queued,
            null));

        bool enabled;
        lock (_stateGate)
        {
            enabled = _policy.Enabled;
        }
        if (!enabled)
        {
            return UpdateCommand(new(
                sequence,
                origin,
                correlationId,
                control,
                value,
                PerformanceCommandPhase.Rejected,
                "RTSS integration is disabled."));
        }

        try
        {
            await _adapterGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return UpdateCommand(new(
                sequence,
                origin,
                correlationId,
                control,
                value,
                PerformanceCommandPhase.Rejected,
                "Command was cancelled before it reached RTSS."));
        }

        try
        {
            // Rechecked after the wait, not only before it. A Settings or config update can switch
            // RTSS integration off while this command is queued, and that path takes no adapter
            // gate of its own — with a disabled policy there are no desired values to apply — so
            // without this the queued command still wrote its value into a switched-off feature.
            lock (_stateGate)
            {
                enabled = _policy.Enabled;
            }

            if (!enabled)
            {
                return UpdateCommand(new(
                    sequence,
                    origin,
                    correlationId,
                    control,
                    value,
                    PerformanceCommandPhase.Rejected,
                    "RTSS integration was switched off while the command was queued."));
            }

            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _disposeCts.Token);
            timeout.CancelAfter(_commandTimeout);
            return await ApplyOneAsync(
                sequence,
                control,
                value,
                persistence,
                origin,
                correlationId,
                timeout.Token,
                cancellationToken,
                updateDesired).ConfigureAwait(false);
        }
        finally
        {
            _adapterGate.Release();
        }
    }

    internal async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _adapterGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RefreshInsideGateAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _adapterGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _disposeCts.Cancel();
        TrySignalObserver();
        try
        {
            await _pollTask.WaitAsync(_commandTimeout).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal service shutdown.
        }
        catch (TimeoutException)
        {
            Log.Warn("RTSS poll did not stop within its disposal budget; process exit will reclaim it.");
            return;
        }

        if (!await _adapterGate.WaitAsync(_commandTimeout).ConfigureAwait(false))
        {
            Log.Warn("RTSS adapter remained busy beyond its disposal budget; process exit will reclaim it.");
            return;
        }

        try
        {
            await _adapter.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _adapterGate.Release();
            _adapterGate.Dispose();
            _observerSignal.Dispose();
            _disposeCts.Dispose();
        }
    }

    private async Task<PerformanceCommandState> ApplyOneAsync(
        long sequence,
        PerformanceControl control,
        int value,
        PerformancePersistenceTarget requestedPersistence,
        string origin,
        string correlationId,
        CancellationToken boundedCancellation,
        CancellationToken callerCancellation,
        bool updateDesired)
    {
        UpdateCommand(new(
            sequence,
            origin,
            correlationId,
            control,
            value,
            PerformanceCommandPhase.Applying,
            null));

        try
        {
            RtssProbe probe = await _adapter.ProbeAsync(boundedCancellation).ConfigureAwait(false);
            UpdateProbe(probe);
            if (probe.Availability != RtssAvailability.Ready || probe.Capabilities is null)
            {
                return UpdateCommand(new(
                    sequence,
                    origin,
                    correlationId,
                    control,
                    value,
                    PerformanceCommandPhase.Rejected,
                    probe.Diagnostic ?? "RTSS is unavailable."));
            }

            if (!probe.Capabilities.Supports(control)
                || !probe.Capabilities.IsValid(control, value))
            {
                return UpdateCommand(new(
                    sequence,
                    origin,
                    correlationId,
                    control,
                    value,
                    PerformanceCommandPhase.Rejected,
                    "The requested value is outside the adapter's verified bounds."));
            }

            PerformancePersistenceTarget persistence;
            RtssApplicationTarget? target;
            string profile;
            PerformancePolicy? previousPolicy = null;
            PerformancePolicy? changedPolicy = null;
            PerformanceCommandState? targetRejection = null;
            lock (_stateGate)
            {
                target = _state.Target;
                persistence = updateDesired
                    ? PerformancePolicyResolver.ResolveEditTarget(
                        _policy,
                        target,
                        requestedPersistence)
                    : target is null
                        ? PerformancePersistenceTarget.Global
                        : PerformancePersistenceTarget.Application;
                if (persistence == PerformancePersistenceTarget.Application && target is null)
                {
                    targetRejection = new(
                        sequence,
                        origin,
                        correlationId,
                        control,
                        value,
                        PerformanceCommandPhase.Rejected,
                        "No active application can receive an application override.");
                }
                else if (updateDesired)
                {
                    previousPolicy = _policy;
                    _policy = PerformancePolicyResolver.Write(
                        _policy,
                        target,
                        persistence,
                        control,
                        value);
                    changedPolicy = _policy;
                }

                if (updateDesired)
                {
                    _state = WithResolvedDesired(_state);
                }
                profile = updateDesired
                    ? persistence == PerformancePersistenceTarget.Global
                        ? string.Empty
                        : target?.RtssProfileName ?? string.Empty
                    : ProfileForResolvedValue(target);
            }

            if (targetRejection is not null)
            {
                return UpdateCommand(targetRejection);
            }

            if (changedPolicy is not null)
            {
                try
                {
                    await _persistPolicy(changedPolicy, boundedCancellation).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    RestorePolicyAfterPersistenceFailure(changedPolicy, previousPolicy!);
                    throw;
                }
                catch (Exception ex)
                {
                    RestorePolicyAfterPersistenceFailure(changedPolicy, previousPolicy!);
                    Log.Error("Persisting RTSS performance policy failed", ex);
                    return UpdateCommand(new(
                        sequence,
                        origin,
                        correlationId,
                        control,
                        value,
                        PerformanceCommandPhase.Failed,
                        "The performance preference could not be persisted."));
                }
            }

            RaisePolicyChanged(changedPolicy);
            RaiseStateChanged(Current);

            RtssApplyResult applied = await _adapter.ApplyAsync(
                new RtssApplyRequest(profile, control, value, probe.Generation),
                boundedCancellation).ConfigureAwait(false);
            if (!applied.Applied)
            {
                return UpdateCommand(new(
                    sequence,
                    origin,
                    correlationId,
                    control,
                    value,
                    PerformanceCommandPhase.Rejected,
                    applied.Diagnostic ?? "RTSS rejected the profile update."));
            }

            RtssProbe after = await _adapter.ProbeAsync(boundedCancellation).ConfigureAwait(false);
            if (after.Generation != probe.Generation || after.Availability != RtssAvailability.Ready)
            {
                UpdateProbe(after);
                return UpdateCommand(new(
                    sequence,
                    origin,
                    correlationId,
                    control,
                    value,
                    PerformanceCommandPhase.Indeterminate,
                    "RTSS restarted while the command was being applied."));
            }

            if (!probe.Capabilities.HasVerifiedReadback(control))
            {
                MarkAppliedUnverified(control, value);
                return UpdateCommand(new(
                    sequence,
                    origin,
                    correlationId,
                    control,
                    value,
                    PerformanceCommandPhase.AppliedUnverified,
                    "RTSS accepted the update but exposes no proven readback for this property."));
            }

            RtssReadback readback = await _adapter.ReadAsync(
                profile,
                probe.Generation,
                boundedCancellation).ConfigureAwait(false);
            UpdateReadback(after, readback, detectExternalChange: false);
            if (readback.Values.ValueFor(control) != value)
            {
                return UpdateCommand(new(
                    sequence,
                    origin,
                    correlationId,
                    control,
                    value,
                    PerformanceCommandPhase.Failed,
                    "RTSS readback did not match the requested value; another profile writer may have won."));
            }

            return UpdateCommand(new(
                sequence,
                origin,
                correlationId,
                control,
                value,
                PerformanceCommandPhase.SucceededVerified,
                null));
        }
        catch (OperationCanceledException) when (!callerCancellation.IsCancellationRequested)
        {
            return UpdateCommand(new(
                sequence,
                origin,
                correlationId,
                control,
                value,
                PerformanceCommandPhase.TimedOut,
                "RTSS did not finish within the bounded command timeout."));
        }
        catch (OperationCanceledException)
        {
            return UpdateCommand(new(
                sequence,
                origin,
                correlationId,
                control,
                value,
                PerformanceCommandPhase.Indeterminate,
                "The caller cancelled after RTSS command processing began."));
        }
        catch (Exception ex)
        {
            Log.Error("RTSS performance command failed", ex);
            MarkDegraded(ex.Message);
            return UpdateCommand(new(
                sequence,
                origin,
                correlationId,
                control,
                value,
                PerformanceCommandPhase.Failed,
                ex.Message));
        }
    }

    private async Task ApplyEffectiveDesiredAsync(string origin, CancellationToken cancellationToken)
    {
        PerformanceState snapshot = Current;
        if (snapshot.Desired.FrameLimit is int frameLimit)
        {
            await SetCoreAsync(
                PerformanceControl.FrameLimit,
                frameLimit,
                PerformancePersistenceTarget.Automatic,
                origin,
                $"{origin}-frame-limit",
                cancellationToken,
                updateDesired: false).ConfigureAwait(false);
        }

        snapshot = Current;
        if (snapshot.Desired.OverlayLevel is int overlayLevel)
        {
            await SetCoreAsync(
                PerformanceControl.OverlayLevel,
                overlayLevel,
                PerformancePersistenceTarget.Automatic,
                origin,
                $"{origin}-overlay-level",
                cancellationToken,
                updateDesired: false).ConfigureAwait(false);
        }
    }

    private async Task PollAsync()
    {
        CancellationToken cancellationToken = _disposeCts.Token;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (Volatile.Read(ref _observerCount) == 0)
            {
                await _observerSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                await RefreshAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error("RTSS refresh failed", ex);
                MarkDegraded(ex.Message);
            }

            await Task.Delay(_pollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RefreshInsideGateAsync(CancellationToken cancellationToken)
    {
        RtssProbe probe = await _adapter.ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (probe.Availability != RtssAvailability.Ready || probe.Capabilities is null)
        {
            PerformanceState unavailable;
            RtssProbe previousProbe;
            lock (_stateGate)
            {
                previousProbe = _state.Probe;
                _state = WithResolvedDesired(_state with
                {
                    Probe = probe,
                    Observed = PerformanceValues.Empty,
                    FrameLimitQuality = PerformanceReadbackQuality.Unavailable,
                    OverlayLevelQuality = PerformanceReadbackQuality.Unavailable,
                    TelemetryHealth = RtssTelemetryHealth.Unavailable,
                    RefreshedAt = _timeProvider.GetUtcNow(),
                });
                unavailable = _state;
            }

            LogProbeChange(previousProbe, probe);
            RaiseStateChanged(unavailable);

            // The one unavailable state WSGM can fix by itself. Discovery has already accepted the
            // installation and found no process, which on a service boot is simply start order:
            // RTSS's own tray entry has not run yet. Awaited rather than fired and forgotten so the
            // next poll does not call it missing while it is still starting.
            await _launcher.TryStartAsync(probe, Enabled, cancellationToken).ConfigureAwait(false);
            return;
        }

        RtssApplicationTarget? target = Current.Target;
        RtssReadback readback = await _adapter.ReadAsync(
            target?.RtssProfileName ?? string.Empty,
            probe.Generation,
            cancellationToken).ConfigureAwait(false);
        UpdateReadback(probe, readback, detectExternalChange: true);
    }

    private void UpdateReadback(RtssProbe probe, RtssReadback readback, bool detectExternalChange)
    {
        PerformanceState next;
        lock (_stateGate)
        {
            bool changed = detectExternalChange
                && _state.RefreshedAt is not null
                && _state.Observed != readback.Values
                && _state.Command.Phase is not PerformanceCommandPhase.Applying
                    and not PerformanceCommandPhase.Queued;
            PerformanceCommandState command = changed
                ? new PerformanceCommandState(
                    Interlocked.Increment(ref _commandSequence),
                    "external",
                    "rtss-external-change",
                    ChangedControl(_state.Observed, readback.Values),
                    null,
                    PerformanceCommandPhase.ExternalChange,
                    "RTSS state changed outside WSGM.")
                : _state.Command;
            _state = WithResolvedDesired(_state with
            {
                Probe = probe,
                Observed = readback.Values,
                FrameLimitQuality = readback.FrameLimitQuality,
                OverlayLevelQuality = readback.OverlayLevelQuality,
                TelemetryHealth = readback.TelemetryHealth,
                RefreshedAt = readback.Timestamp,
                Command = command,
            });
            next = _state;
        }

        RaiseStateChanged(next);
    }

    private void MarkAppliedUnverified(PerformanceControl control, int value)
    {
        PerformanceState next;
        lock (_stateGate)
        {
            _state = _state with
            {
                Observed = _state.Observed.With(control, value),
                FrameLimitQuality = control == PerformanceControl.FrameLimit
                    ? PerformanceReadbackQuality.AppliedUnverified
                    : _state.FrameLimitQuality,
                OverlayLevelQuality = control == PerformanceControl.OverlayLevel
                    ? PerformanceReadbackQuality.AppliedUnverified
                    : _state.OverlayLevelQuality,
                RefreshedAt = _timeProvider.GetUtcNow(),
            };
            next = _state;
        }

        RaiseStateChanged(next);
    }

    private void RestorePolicyAfterPersistenceFailure(
        PerformancePolicy failedPolicy,
        PerformancePolicy previousPolicy)
    {
        PerformanceState next;
        lock (_stateGate)
        {
            if (ReferenceEquals(_policy, failedPolicy))
            {
                _policy = previousPolicy;
                _state = WithResolvedDesired(_state);
            }

            next = _state;
        }

        RaiseStateChanged(next);
    }

    private void UpdateProbe(RtssProbe probe)
    {
        PerformanceState next;
        RtssProbe previous;
        lock (_stateGate)
        {
            previous = _state.Probe;
            _state = _state with { Probe = probe };
            next = _state;
        }

        LogProbeChange(previous, probe);
        RaiseStateChanged(next);
    }

    /// <summary>Logs an RTSS probe result when it changes.</summary>
    /// <param name="previous">The probe this replaces.</param>
    /// <param name="probe">The new probe.</param>
    /// <remarks>
    /// On change only, because the probe runs on every poll and this would otherwise be the loudest
    /// line in the file. It has to be logged at all: a user reading an RTSS problem off the overlay
    /// previously found nothing whatsoever about it in the log, which made the subsystem WSGM is
    /// most often asked about the one that could not be diagnosed from a pasted log.
    /// </remarks>
    private static void LogProbeChange(RtssProbe previous, RtssProbe probe)
    {
        if (previous.Availability == probe.Availability
            && string.Equals(previous.Diagnostic, probe.Diagnostic, StringComparison.Ordinal))
        {
            return;
        }

        string version = string.IsNullOrWhiteSpace(probe.Version) ? "unknown" : probe.Version;
        string detail = string.IsNullOrWhiteSpace(probe.Diagnostic)
            ? string.Empty
            : $" - {probe.Diagnostic}";
        string line = $"RTSS: {probe.Availability}, version {version}{detail}";
        if (probe.Availability is RtssAvailability.Ready)
        {
            Log.Info(line);
        }
        else
        {
            Log.Warn(line);
        }
    }

    private void MarkDegraded(string diagnostic)
    {
        PerformanceState next;
        lock (_stateGate)
        {
            _state = _state with
            {
                Probe = _state.Probe with
                {
                    Availability = RtssAvailability.Degraded,
                    Diagnostic = diagnostic,
                },
                TelemetryHealth = RtssTelemetryHealth.Faulted,
            };
            next = _state;
        }

        RaiseStateChanged(next);
    }

    private PerformanceCommandState UpdateCommand(PerformanceCommandState command)
    {
        PerformanceState next;
        lock (_stateGate)
        {
            command = UpdateCommandLocked(command);
            next = _state;
        }

        RaiseStateChanged(next);
        return command;
    }

    private PerformanceCommandState UpdateCommandLocked(PerformanceCommandState command)
    {
        if (command.Sequence >= _state.Command.Sequence)
        {
            _state = _state with { Command = command };
        }

        return command;
    }

    private PerformanceState WithResolvedDesired(PerformanceState state)
    {
        (
            PerformanceValues values,
            PerformancePolicyLayer frameLimitLayer,
            PerformancePolicyLayer overlayLevelLayer) = PerformancePolicyResolver.Resolve(
            _policy,
            state.Target);
        return state with
        {
            Desired = values,
            FrameLimitLayer = frameLimitLayer,
            OverlayLevelLayer = overlayLevelLayer,
        };
    }

    private string ProfileForResolvedValue(RtssApplicationTarget? target)
    {
        PerformanceApplicationPolicy? application = PerformancePolicyResolver.Find(
            _policy,
            target?.ApplicationId);
        // RTSS application profiles are whole snapshots, while WSGM precedence is
        // per property. Once an application has any override, write every resolved
        // property into that one profile so a global fallback cannot be stranded in
        // the separate global profile behind an older application snapshot.
        return application is null
            ? string.Empty
            : target?.RtssProfileName ?? string.Empty;
    }

    private void ReleaseObservation()
    {
        int remaining = Interlocked.Decrement(ref _observerCount);
        if (remaining < 0)
        {
            Interlocked.Exchange(ref _observerCount, 0);
        }
    }

    private void TrySignalObserver()
    {
        try
        {
            if (_observerSignal.CurrentCount == 0)
            {
                _observerSignal.Release();
            }
        }
        catch (ObjectDisposedException)
        {
            // A racing observation release during disposal has no work left to wake.
        }
    }

    private void RaiseStateChanged(PerformanceState state)
    {
        try
        {
            StateChanged?.Invoke(state);
        }
        catch (Exception ex)
        {
            Log.Error("RTSS state observer failed", ex);
        }
    }

    private void RaisePolicyChanged(PerformancePolicy? policy)
    {
        if (policy is null)
        {
            return;
        }

        try
        {
            PolicyChanged?.Invoke(policy);
        }
        catch (Exception ex)
        {
            Log.Error("RTSS policy observer failed", ex);
        }
    }

    private static PerformanceControl ChangedControl(PerformanceValues old, PerformanceValues current)
        => old.FrameLimit != current.FrameLimit
            ? PerformanceControl.FrameLimit
            : PerformanceControl.OverlayLevel;

    private static PerformancePolicy NormalizePolicy(PerformancePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy.Global);
        List<PerformanceApplicationPolicy> applications = [];
        foreach (PerformanceApplicationPolicy application in policy.Applications ?? [])
        {
            if (application is null
                || string.IsNullOrWhiteSpace(application.ApplicationId)
                || application.ApplicationId.Length > 1024
                || !ValidProfileName(application.RtssProfileName)
                || application.Values is null
                || applications.Any(existing => string.Equals(
                    existing.ApplicationId,
                    application.ApplicationId,
                    StringComparison.Ordinal)))
            {
                continue;
            }

            applications.Add(application with
            {
                ApplicationId = application.ApplicationId.Trim(),
                RtssProfileName = application.RtssProfileName.Trim(),
            });
        }

        return new PerformancePolicy(policy.Global, applications.ToArray(), policy.Enabled);
    }

    private static bool PoliciesEqual(PerformancePolicy left, PerformancePolicy right)
    {
        if (left.Enabled != right.Enabled
            || left.Global != right.Global
            || left.Applications.Count != right.Applications.Count)
        {
            return false;
        }

        for (int index = 0; index < left.Applications.Count; index++)
        {
            if (left.Applications[index] != right.Applications[index])
            {
                return false;
            }
        }

        return true;
    }

    private static bool ValidTarget(RtssApplicationTarget target) =>
        !string.IsNullOrWhiteSpace(target.ApplicationId)
        && target.ApplicationId.Length <= 1024
        && ValidProfileName(target.RtssProfileName)
        && target.ProcessId is null or > 0;

    private static bool ValidProfileName(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 128
        && string.Equals(System.IO.Path.GetFileName(value), value, StringComparison.Ordinal)
        && value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    private static string SanitizeToken(string value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        string sanitized = new(value.Where(character => !char.IsControl(character)).Take(80).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? fallback : sanitized;
    }

    private static TimeSpan BoundInterval(TimeSpan interval) => interval < TimeSpan.FromMilliseconds(250)
        ? TimeSpan.FromMilliseconds(250)
        : interval > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : interval;

    private static TimeSpan BoundTimeout(TimeSpan timeout) => timeout < TimeSpan.FromMilliseconds(100)
        ? TimeSpan.FromMilliseconds(100)
        : timeout > TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : timeout;

    private sealed class ObservationLease : IDisposable
    {
        private PerformanceService? _owner;

        internal ObservationLease(PerformanceService owner)
        {
            _owner = owner;
        }

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseObservation();
    }
}
