using System;
using System.Collections.Generic;
using System.IO;
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
            PerformanceApplicationTarget? target)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!policy.Enabled)
        {
            return (
                PerformanceValues.Empty,
                PerformancePolicyLayer.None,
                PerformancePolicyLayer.None);
        }

        var application = Find(policy, target);
        var persistent = application is null
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
        PerformanceApplicationTarget? target)
    {
        return Find(policy, target) is null
            ? PerformancePersistenceTarget.Global
            : PerformancePersistenceTarget.Application;
    }

    internal static PerformancePolicy Write(
        PerformancePolicy policy,
        PerformanceApplicationTarget? target,
        PerformancePersistenceTarget persistence,
        PerformanceControl control,
        int value)
    {
        if (persistence == PerformancePersistenceTarget.Global)
        {
            return policy with { Global = policy.Global.With(control, value) };
        }

        if (target is null)
        {
            throw new InvalidOperationException("An application edit requires an active application target.");
        }

        List<PerformanceApplicationPolicy> applications = [.. policy.Applications];
        var index = applications.FindIndex(item => string.Equals(
            item.ApplicationId,
            Find(policy, target)?.ApplicationId,
            StringComparison.Ordinal));
        var current = applications[index];
        applications[index] = current with
        {
            RtssProfileName = target.RtssProfileName ?? current.RtssProfileName,
            Values = current.Values.With(control, value)
        };
        return policy with { Applications = [.. applications] };
    }

    internal static PerformanceApplicationPolicy? Find(PerformancePolicy policy, PerformanceApplicationTarget? target)
    {
        var entry = FindStored(policy, target);
        return entry is { Enabled: true } ? entry : null;
    }

    internal static PerformanceApplicationPolicy? FindStored(PerformancePolicy policy,
        PerformanceApplicationTarget? target)
    {
        return ApplicationProfileRules.Match(policy.Applications, target?.ApplicationId, target?.RtssProfileName,
            item => item.ApplicationId, item => item.ProcessNames);
    }

    private static PerformancePolicyLayer LayerFor(int? application, int? global)
    {
        return application is not null
            ? PerformancePolicyLayer.Application
            : global is not null
                ? PerformancePolicyLayer.Global
                : PerformancePolicyLayer.None;
    }
}

/// <summary>
///     One session-owned RTSS service shared by every UI projection. Adapter access and commands are
///     serialized, polling runs only while a client holds an observation lease, and RTSS failures never
///     escape into shell/session transitions.
/// </summary>
internal sealed partial class PerformanceService : IAsyncDisposable
{
    // Commands and target transitions already read back immediately. This is the background
    // external-change/availability check, including while Steam keeps its observation lease.
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(2);

    /// <summary>The controls a readback is checked against the desired state for.</summary>
    private static readonly PerformanceControl[] DriftCheckedControls =
    [
        PerformanceControl.FrameLimit,
        PerformanceControl.OverlayLevel
    ];

    private static readonly RtssProbe InitialProbe = new(
        RtssAvailability.Unknown,
        null,
        null,
        0,
        null,
        "RTSS discovery has not run.");

    private readonly IRtssAdapter _adapter;
    private readonly SemaphoreSlim _adapterGate = new(1, 1);
    private readonly Dictionary<long, string> _commandProfiles = [];
    private readonly TimeSpan _commandTimeout;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly RtssLauncher _launcher;
    private readonly ObservationGate _observers = new();
    private readonly Func<PerformancePolicy, CancellationToken, Task> _persistPolicy;
    private readonly Task _pollTask;
    private readonly Lock _stateGate = new();
    private readonly TimeProvider _timeProvider;
    private long _commandSequence;
    private bool _disposed;
    private PerformancePolicy _policy;

    private PerformanceState? _raisedState;

    /// <summary>The desired values a drift repair has already been attempted for, or null.</summary>
    private PerformanceValues? _repairedDrift;

    private PerformanceState _state;

    internal PerformanceService(
        IRtssAdapter adapter,
        Func<PerformancePolicy, CancellationToken, Task> persistPolicy,
        PerformancePolicy? policy = null,
        TimeSpan? pollInterval = null,
        TimeSpan? commandTimeout = null,
        TimeProvider? timeProvider = null)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _launcher = new RtssLauncher();
        _persistPolicy = persistPolicy ?? throw new ArgumentNullException(nameof(persistPolicy));
        _policy = NormalizePolicy(policy ?? PerformancePolicy.Empty);
        PollInterval = BoundInterval(pollInterval ?? DefaultPollInterval);
        _commandTimeout = BoundTimeout(commandTimeout ?? DefaultCommandTimeout);
        _timeProvider = timeProvider ?? TimeProvider.System;
        var (desired, frameLimitLayer, overlayLevelLayer) = PerformancePolicyResolver.Resolve(
            _policy,
            null);
        _state = new PerformanceState(
            InitialProbe,
            null,
            false,
            frameLimitLayer,
            overlayLevelLayer,
            desired,
            PerformanceValues.Empty,
            PerformanceReadbackQuality.Unavailable,
            PerformanceReadbackQuality.Unavailable,
            null,
            PerformanceCommandState.Idle);
        _pollTask = Task.Run(PollAsync);
    }

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

    internal int ObserverCount => _observers.Count;

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

    internal TimeSpan PollInterval { get; }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _disposeCts.CancelAsync().ConfigureAwait(false);
        _observers.Signal();
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
        }
    }

    internal event Action<PerformanceState>? StateChanged;

    /// <summary>
    ///     Hands the Custom overlay's configuration (selector level 4) to the adapter's
    ///     renderer.
    /// </summary>
    /// <param name="settings">The widget order and per-widget detail.</param>
    /// <remarks>
    ///     Deliberately outside the adapter gate: it changes what the renderer draws on its
    ///     next tick, not RTSS state, and must stay applicable while a command is in flight.
    /// </remarks>
    internal void ApplyOsdCustomization(RtssOsdCustomSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _adapter.ApplyOsdCustomization(settings);
    }

    /// <summary>Hands the current power and AutoTDP projection to the OSD renderer.</summary>
    /// <param name="status">The cached session projection.</param>
    internal void ApplyOsdPowerStatus(RtssOsdPowerStatus status)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _adapter.ApplyOsdPowerStatus(status);
    }

    internal IDisposable AcquireObservation()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _observers.Acquire();
    }

    internal async Task UpdatePolicyAsync(
        PerformancePolicy policy,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(policy);
        var normalized = NormalizePolicy(policy);
        PerformanceState next;
        await _adapterGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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
        }
        finally
        {
            _adapterGate.Release();
        }

        RaiseStateChanged(next);
        await ApplyEffectiveDesiredAsync("policy-reload", cancellationToken).ConfigureAwait(false);
    }

    internal async Task SetTargetAsync(
        PerformanceApplicationTarget? target,
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
                RtssProfileName = target.RtssProfileName?.Trim()
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
        string origin,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        return SetCoreAsync(
            control,
            value,
            origin,
            correlationId,
            true,
            cancellationToken);
    }

    private async Task<PerformanceCommandState> SetCoreAsync(
        PerformanceControl control,
        int value,
        string origin,
        string correlationId,
        bool updateDesired,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        origin = SanitizeToken(origin, "unknown");
        correlationId = SanitizeToken(correlationId, Guid.NewGuid().ToString("N"));
        var sequence = Interlocked.Increment(ref _commandSequence);

        PerformanceCommandState Command(PerformanceCommandPhase phase, string? diagnostic = null)
        {
            return new PerformanceCommandState(sequence, origin, correlationId, control, value, phase, diagnostic);
        }

        UpdateCommand(Command(PerformanceCommandPhase.Queued));

        bool enabled;
        lock (_stateGate)
        {
            enabled = _policy.Enabled;
        }

        if (!enabled)
        {
            return UpdateCommand(Command(
                PerformanceCommandPhase.Rejected,
                "RTSS integration is disabled."));
        }

        using var admission = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _disposeCts.Token);
        try
        {
            await _adapterGate.WaitAsync(admission.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return UpdateCommand(Command(
                PerformanceCommandPhase.Rejected,
                _disposeCts.IsCancellationRequested
                    ? "RTSS is stopping."
                    : "Command was cancelled before it reached RTSS."));
        }

        try
        {
            // Rechecked after the wait, not only before it. A Settings or config update can switch
            // RTSS integration off while this command is queued, and that path takes no adapter
            // gate of its own — with a disabled policy there are no desired values to apply — so
            // without this the queued command still wrote its value into a switched-off feature.
            if (_disposed)
            {
                return UpdateCommand(Command(
                    PerformanceCommandPhase.Rejected,
                    "RTSS is stopping."));
            }

            lock (_stateGate)
            {
                enabled = _policy.Enabled;
            }

            if (!enabled)
            {
                return UpdateCommand(Command(
                    PerformanceCommandPhase.Rejected,
                    "RTSS integration was switched off while the command was queued."));
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _disposeCts.Token);
            timeout.CancelAfter(_commandTimeout);
            return await ApplyOneAsync(
                sequence,
                control,
                value,
                origin,
                correlationId,
                updateDesired,
                timeout.Token,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _adapterGate.Release();
        }
    }

    internal async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _disposeCts.Token);
        RtssProbe? launchProbe;
        await _adapterGate.WaitAsync(admission.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            launchProbe = await RefreshInsideGateAsync(admission.Token).ConfigureAwait(false);
        }
        finally
        {
            _adapterGate.Release();
        }

        // Starting RTSS can wait up to ten seconds for its tray process. Keep that settle outside
        // the adapter gate so UI commands can still observe and report the unavailable state.
        if (launchProbe is not null)
        {
            await _launcher.TryStartAsync(launchProbe, Enabled, admission.Token).ConfigureAwait(false);
        }

        // Also outside the gate, and for the same reason: the repair is an ordinary command and
        // takes the gate itself for every write it makes.
        if (DriftNeedsRepair())
        {
            await ApplyEffectiveDesiredAsync("drift-repair", admission.Token).ConfigureAwait(false);
        }
    }

    /// <summary>Whether RTSS is holding something other than the values WSGM last asked for.</summary>
    /// <returns>True when the effective desired values should be written again.</returns>
    /// <remarks>
    ///     The readback is the only evidence that a profile still says what WSGM wrote into it. RTSS
    ///     profiles are ordinary files its own UI, another overlay tool or a game's own installer can
    ///     edit, and none of them announce it — the frame limit simply stops being the one the user
    ///     chose, with the overlay and the Quick Access row still showing the value they asked for.
    ///     Every poll therefore compares what was asked for against what came back.
    ///     <para>
    ///         The re-apply happens ONCE per disagreement. A writer that takes the profile back every two
    ///         seconds is a fight WSGM cannot win and must not join, so a second consecutive disagreement
    ///         about the same desired values is reported and then left alone until the values change or the
    ///         readback agrees again.
    ///     </para>
    ///     <para>
    ///         Only the poll loop reaches this, so <c>_repairedDrift</c> needs no lock of its own; the
    ///         state it compares is taken as one snapshot.
    ///     </para>
    /// </remarks>
    private bool DriftNeedsRepair()
    {
        var snapshot = Current;
        if (!Enabled
            || snapshot.Command.Phase is PerformanceCommandPhase.Queued
                or PerformanceCommandPhase.Applying)
        {
            return false;
        }

        List<string> drift = [];
        foreach (var control in DriftCheckedControls)
        {
            // An unverified readback is not evidence of anything: RTSS either could not be read or
            // has no proven query for the property, and treating that as a mismatch would rewrite
            // the profile on every poll.
            if (QualityOf(snapshot, control) is not PerformanceReadbackQuality.Verified
                || snapshot.Desired.ValueFor(control) is not { } wanted)
            {
                continue;
            }

            var observed = snapshot.Observed.ValueFor(control);
            if (observed != wanted)
            {
                drift.Add($"{control} is {observed?.ToString() ?? "unreadable"} rather than {wanted}");
            }
        }

        if (drift.Count == 0)
        {
            if (_repairedDrift is null)
            {
                return false;
            }

            _repairedDrift = null;
            Log.Info("RTSS holds the values WSGM set again.");
            return false;
        }

        var detail = string.Join("; ", drift);
        if (_repairedDrift == snapshot.Desired)
        {
            Log.Change(
                "rtss.drift",
                $"RTSS still disagrees after a repair ({detail}); another writer owns the profile "
                + "and WSGM will not keep overwriting it.",
                LogLevel.Warn);
            return false;
        }

        _repairedDrift = snapshot.Desired;
        Log.Change(
            "rtss.drift",
            $"RTSS drifted from what WSGM set ({detail}); re-applying.",
            LogLevel.Warn);
        return true;
    }

    private static PerformanceReadbackQuality QualityOf(
        PerformanceState state,
        PerformanceControl control)
    {
        return control is PerformanceControl.FrameLimit
            ? state.FrameLimitQuality
            : state.OverlayLevelQuality;
    }

    private async Task<PerformanceCommandState> ApplyOneAsync(
        long sequence,
        PerformanceControl control,
        int value,
        string origin,
        string correlationId,
        bool updateDesired,
        CancellationToken boundedCancellation,
        CancellationToken callerCancellation)
    {
        PerformanceCommandState Command(PerformanceCommandPhase phase, string? diagnostic = null)
        {
            return new PerformanceCommandState(sequence, origin, correlationId, control, value, phase, diagnostic);
        }

        UpdateCommand(Command(PerformanceCommandPhase.Applying));

        try
        {
            var probe = await _adapter.ProbeAsync(boundedCancellation).ConfigureAwait(false);
            UpdateProbe(probe);
            if (probe.Availability != RtssAvailability.Ready || probe.Capabilities is null)
            {
                return UpdateCommand(Command(
                    PerformanceCommandPhase.Rejected,
                    probe.Diagnostic ?? "RTSS is unavailable."));
            }

            if (!probe.Capabilities.Supports(control)
                || !probe.Capabilities.IsValid(control, value))
            {
                return UpdateCommand(Command(
                    PerformanceCommandPhase.Rejected,
                    "The requested value is outside the adapter's verified bounds."));
            }

            PerformanceApplicationTarget? target;
            bool applicationOptedIn;
            PerformancePolicy? previousPolicy = null;
            PerformancePolicy? changedPolicy = null;
            lock (_stateGate)
            {
                target = _state.Target;
                if (updateDesired)
                {
                    previousPolicy = _policy;
                    _policy = PerformancePolicyResolver.Write(
                        _policy,
                        target,
                        PerformancePolicyResolver.ResolveEditTarget(_policy, target),
                        control,
                        value);
                    changedPolicy = _policy;
                    _state = WithResolvedDesired(_state);
                }

                applicationOptedIn = PerformancePolicyResolver.Find(
                    _policy,
                    target) is not null;
            }

            // Saving an RTSS profile that does not exist creates it, which sprayed a profile onto
            // every executable that ever took focus (device-observed 2026-09-02). A running
            // application's own profile is therefore written only when the user opted the
            // application in, or when RTSS already carries that profile — whose explicit values
            // would otherwise stay the stronger RTSS layer and silently override the global write.
            // Everything else goes to the global profile, which covers the application anyway.
            var profile = EffectiveRtssProfile(target, applicationOptedIn);
            lock (_stateGate)
            {
                _commandProfiles[sequence] = target is null
                    ? string.Empty
                    : target.RtssProfileName is null
                        ? $"pending application {target.ApplicationId}"
                        : profile;
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
                    return UpdateCommand(Command(
                        PerformanceCommandPhase.Failed,
                        "The performance preference could not be persisted."));
                }
            }

            RaiseStateChanged(Current);

            if (target is { RtssProfileName: null or "" })
            {
                return UpdateCommand(Command(
                    PerformanceCommandPhase.Deferred,
                    "The application preference was saved and will apply when its foreground "
                    + "executable is known."));
            }

            var applied = await _adapter.ApplyAsync(
                new RtssApplyRequest(profile, control, value, probe.Generation),
                boundedCancellation).ConfigureAwait(false);
            if (!applied.Applied)
            {
                return UpdateCommand(Command(
                    PerformanceCommandPhase.Rejected,
                    applied.Diagnostic ?? "RTSS rejected the profile update."));
            }

            var after = await _adapter.ProbeAsync(boundedCancellation).ConfigureAwait(false);
            if (after.Generation != probe.Generation || after.Availability != RtssAvailability.Ready)
            {
                UpdateProbe(after);
                return UpdateCommand(Command(
                    PerformanceCommandPhase.Indeterminate,
                    "RTSS restarted while the command was being applied."));
            }

            if (!probe.Capabilities.HasVerifiedReadback(control))
            {
                MarkAppliedUnverified(control, value);
                return UpdateCommand(Command(
                    PerformanceCommandPhase.AppliedUnverified,
                    "RTSS accepted the update but exposes no proven readback for this property."));
            }

            var readback = await _adapter.ReadAsync(
                profile,
                probe.Generation,
                boundedCancellation).ConfigureAwait(false);
            UpdateReadback(after, readback, false);
            if (readback.Values.ValueFor(control) != value)
            {
                return UpdateCommand(Command(
                    PerformanceCommandPhase.Failed,
                    "RTSS readback did not match the requested value; another profile writer may have won."));
            }

            return UpdateCommand(Command(PerformanceCommandPhase.SucceededVerified));
        }
        catch (OperationCanceledException) when (!callerCancellation.IsCancellationRequested)
        {
            return UpdateCommand(Command(
                PerformanceCommandPhase.TimedOut,
                "RTSS did not finish within the bounded command timeout."));
        }
        catch (OperationCanceledException)
        {
            return UpdateCommand(Command(
                PerformanceCommandPhase.Indeterminate,
                "The caller cancelled after RTSS command processing began."));
        }
        catch (Exception ex)
        {
            Log.Error("RTSS performance command failed", ex);
            MarkDegraded(ex.Message);
            return UpdateCommand(Command(PerformanceCommandPhase.Failed, ex.Message));
        }
    }

    private async Task ApplyEffectiveDesiredAsync(string origin, CancellationToken cancellationToken)
    {
        var snapshot = Current;
        if (snapshot.Desired.FrameLimit is { } frameLimit)
        {
            await SetCoreAsync(
                PerformanceControl.FrameLimit,
                frameLimit,
                origin,
                $"{origin}-frame-limit",
                false,
                cancellationToken).ConfigureAwait(false);
        }

        snapshot = Current;
        if (snapshot.Desired.OverlayLevel is { } overlayLevel)
        {
            await SetCoreAsync(
                PerformanceControl.OverlayLevel,
                overlayLevel,
                origin,
                $"{origin}-overlay-level",
                false,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PollAsync()
    {
        var cancellationToken = _disposeCts.Token;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_observers.Count == 0)
            {
                await _observers.WaitAsync(cancellationToken).ConfigureAwait(false);
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

            await Task.Delay(PollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<RtssProbe?> RefreshInsideGateAsync(CancellationToken cancellationToken)
    {
        var probe = await _adapter.ProbeAsync(cancellationToken).ConfigureAwait(false);
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
                    RefreshedAt = _timeProvider.GetUtcNow()
                });
                unavailable = _state;
            }

            LogProbeChange(previousProbe, probe);
            RaiseStateChanged(unavailable);

            return probe;
        }

        var target = Current.Target;
        if (target is { RtssProfileName: null or "" })
        {
            PerformanceState pending;
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
                    RefreshedAt = _timeProvider.GetUtcNow()
                });
                pending = _state;
            }

            LogProbeChange(previousProbe, probe);
            RaiseStateChanged(pending);
            return null;
        }

        bool applicationOptedIn;
        lock (_stateGate)
        {
            applicationOptedIn = PerformancePolicyResolver.Find(
                _policy,
                target) is not null;
        }

        // The same profile-selection rule as the apply path, so readback observes the profile the
        // writes actually target instead of reporting a phantom external change against a
        // never-written application profile.
        var readback = await _adapter.ReadAsync(
            EffectiveRtssProfile(target, applicationOptedIn),
            probe.Generation,
            cancellationToken).ConfigureAwait(false);
        UpdateReadback(probe, readback, true);
        return null;
    }

    /// <summary>The RTSS profile a command or readback for this target actually addresses.</summary>
    /// <param name="target">The running-application target, or null for global.</param>
    /// <param name="applicationOptedIn">Whether WSGM policy holds a per-application entry.</param>
    private string EffectiveRtssProfile(
        PerformanceApplicationTarget? target,
        bool applicationOptedIn)
    {
        var name = target?.RtssProfileName ?? string.Empty;
        if (name.Length == 0)
        {
            return string.Empty;
        }

        return applicationOptedIn || _adapter.ProfileExists(name) ? name : string.Empty;
    }

    private void UpdateReadback(RtssProbe probe, RtssReadback readback, bool detectExternalChange)
    {
        PerformanceState next;
        lock (_stateGate)
        {
            var changed = detectExternalChange
                          && _state.RefreshedAt is not null
                          && _state.Observed != readback.Values
                          && _state.Command.Phase is not PerformanceCommandPhase.Applying
                              and not PerformanceCommandPhase.Queued;
            var command = changed
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
                RefreshedAt = readback.Timestamp,
                Command = command
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
                RefreshedAt = _timeProvider.GetUtcNow()
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
    ///     The probe runs on every poll, so only transitions are logged. Each transition includes the
    ///     availability and diagnostic needed for remote RTSS diagnosis.
    /// </remarks>
    private static void LogProbeChange(RtssProbe previous, RtssProbe probe)
    {
        if (previous.Availability == probe.Availability
            && string.Equals(previous.Diagnostic, probe.Diagnostic, StringComparison.Ordinal))
        {
            return;
        }

        var version = string.IsNullOrWhiteSpace(probe.Version) ? "unknown" : probe.Version;
        var detail = string.IsNullOrWhiteSpace(probe.Diagnostic)
            ? string.Empty
            : $" - {probe.Diagnostic}";
        var line = $"RTSS: {probe.Availability}, version {version}{detail}";
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
                    Diagnostic = diagnostic
                }
            };
            next = _state;
        }

        RaiseStateChanged(next);
    }

    private PerformanceCommandState UpdateCommand(PerformanceCommandState command)
    {
        PerformanceState next;
        string? appliedProfile;
        lock (_stateGate)
        {
            command = UpdateCommandLocked(command);
            next = _state;
            _commandProfiles.TryGetValue(command.Sequence, out appliedProfile);
            if (command.Phase is not (PerformanceCommandPhase.Queued
                or PerformanceCommandPhase.Applying))
            {
                _commandProfiles.Remove(command.Sequence);
            }
        }

        LogCommandOutcome(command, next, appliedProfile);
        RaiseStateChanged(next);
        return command;
    }

    /// <summary>Records what one RTSS write actually did.</summary>
    /// <param name="command">The command that reached a terminal phase.</param>
    /// <param name="state">The state it left behind, for the profile it was written to.</param>
    /// <param name="appliedProfile">RTSS profile the command targeted.</param>
    /// <remarks>
    ///     Every terminal outcome is recorded through <see cref="Log.Change" /> keyed per control. The
    ///     profile is included because global and per-application writes target different RTSS files,
    ///     and the origin because a value nobody meant to set is otherwise unattributable — a stray
    ///     12 FPS cap took a whole evening to place, and the log could not say whether the overlay
    ///     slider, the Quick Access row or a profile reload had written it.
    /// </remarks>
    private static void LogCommandOutcome(
        PerformanceCommandState command,
        PerformanceState state,
        string? appliedProfile)
    {
        if (command.Phase
            is PerformanceCommandPhase.Idle
            or PerformanceCommandPhase.Queued
            or PerformanceCommandPhase.Applying)
        {
            return;
        }

        var profile = appliedProfile is not null
            ? string.IsNullOrWhiteSpace(appliedProfile) ? "the global profile" : appliedProfile
            : string.IsNullOrWhiteSpace(state.Target?.RtssProfileName)
                ? "the global profile"
                : state.Target!.RtssProfileName;
        var detail = string.IsNullOrWhiteSpace(command.Diagnostic)
            ? string.Empty
            : $" — {command.Diagnostic}";
        var succeeded = command.Phase
            is PerformanceCommandPhase.Deferred
            or PerformanceCommandPhase.SucceededVerified
            or PerformanceCommandPhase.AppliedUnverified;
        Log.Change(
            $"rtss.command.{command.Control}",
            $"RTSS {command.Control}={command.RequestedValue?.ToString() ?? "none"} on {profile} "
            + $"from {command.Origin}: {command.Phase}{detail}",
            succeeded ? LogLevel.Info : LogLevel.Warn);
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
        var (values, frameLimitLayer, overlayLevelLayer) = PerformancePolicyResolver.Resolve(
            _policy,
            state.Target);
        return state with
        {
            Desired = values,
            ApplicationProfileEnabled = PerformancePolicyResolver.Find(
                _policy,
                state.Target) is not null,
            FrameLimitLayer = frameLimitLayer,
            OverlayLevelLayer = overlayLevelLayer
        };
    }

    private void RaiseStateChanged(PerformanceState state)
    {
        // A poll that reads back the same values only moves RefreshedAt, which no subscriber shows.
        // Raising it anyway rebuilt the overlay rows and republished Steam's page on every poll.
        var previous = Interlocked.Exchange(ref _raisedState, state);
        if (previous is not null && previous == state with { RefreshedAt = previous.RefreshedAt })
        {
            return;
        }

        try
        {
            StateChanged?.Invoke(state);
        }
        catch (Exception ex)
        {
            Log.Error("RTSS state observer failed", ex);
        }
    }

    private static PerformanceControl ChangedControl(PerformanceValues old, PerformanceValues current)
    {
        return old.FrameLimit != current.FrameLimit
            ? PerformanceControl.FrameLimit
            : PerformanceControl.OverlayLevel;
    }

    private static PerformancePolicy NormalizePolicy(PerformancePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy.Global);
        List<PerformanceApplicationPolicy> applications = [];
        HashSet<string> identities = new(StringComparer.Ordinal);
        foreach (var application in policy.Applications)
        {
            var applicationId = application.ApplicationId.Trim();
            if (applicationId.Length == 0)
            {
                Log.Warn("RTSS policy entry dropped: the application identity was empty.");
                continue;
            }

            if (!identities.Add(applicationId))
            {
                Log.Warn(
                    $"RTSS policy entry dropped: duplicate application identity '{SanitizeToken(applicationId, "unknown")}'.");
                continue;
            }

            applications.Add(application with
            {
                ApplicationId = applicationId,
                RtssProfileName = ValidProfileName(application.RtssProfileName)
                    ? application.RtssProfileName.Trim()
                    : string.Empty
            });
        }

        return policy with { Applications = [.. applications] };
    }

    private static bool PoliciesEqual(PerformancePolicy left, PerformancePolicy right)
    {
        if (left.Enabled != right.Enabled
            || left.Global != right.Global
            || left.Applications.Count != right.Applications.Count)
        {
            return false;
        }

        return left.Applications.Zip(right.Applications).All(pair =>
            pair.First with { ProcessNames = pair.Second.ProcessNames } == pair.Second
            && pair.First.ProcessNames.SequenceEqual(pair.Second.ProcessNames, StringComparer.OrdinalIgnoreCase));
    }

    private static bool ValidTarget(PerformanceApplicationTarget target)
    {
        return !string.IsNullOrWhiteSpace(target.ApplicationId)
               && target.ApplicationId.Length <= 1024
               && (target.RtssProfileName is null || ValidProfileName(target.RtssProfileName))
               && target.ProcessId is null or > 0;
    }

    private static bool ValidProfileName(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
               && value.Length <= 128
               && string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal)
               && value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeToken(string value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        string sanitized = new(value.Where(character => !char.IsControl(character)).Take(80).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? fallback : sanitized;
    }

    private static TimeSpan BoundInterval(TimeSpan interval)
    {
        return TimeSpan.FromTicks(Math.Clamp(interval.Ticks, TimeSpan.TicksPerMillisecond * 250,
            TimeSpan.TicksPerSecond * 30));
    }

    private static TimeSpan BoundTimeout(TimeSpan timeout)
    {
        return TimeSpan.FromTicks(Math.Clamp(timeout.Ticks, TimeSpan.TicksPerMillisecond * 100,
            TimeSpan.TicksPerSecond * 10));
    }
}
