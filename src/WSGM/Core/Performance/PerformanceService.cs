using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>
///     One session-owned RTSS service shared by every UI projection. Serializes adapter commands,
///     polls for the session lifetime, and projects adapter failures into shared state.
/// </summary>
/// <remarks>
///     This owns the adapter, its serialization gate and the write and drift-repair rules.
///     <see cref="PerformanceStateStore" /> holds what the session believes, and
///     <see cref="PerformanceRefreshLoop" /> runs the shared refresh and the poll.
/// </remarks>
internal sealed class PerformanceService : IAsyncDisposable
{
    // Commands and target transitions publish what they wrote. This is the readback: the external
    // change, drift and availability check, independent of visible performance controls.
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(2);

    /// <summary>The controls a readback is compared with the desired values for.</summary>
    private static readonly PerformanceControl[] DriftCheckedControls =
    [
        PerformanceControl.FrameLimit,
        PerformanceControl.OverlayLevel
    ];

    private readonly IRtssAdapter _adapter;
    private readonly SemaphoreSlim _adapterGate = new(1, 1);
    private readonly TimeSpan _commandTimeout;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly RtssLauncher _launcher;
    private readonly Func<ProfileField, int, CancellationToken, Task<ProfileSnapshot>> _persistValue;
    private readonly PerformanceRefreshLoop _refresh;
    private readonly PerformanceStateStore _store;
    private bool _disposed;

    /// <summary>Creates the service and starts its RTSS poll.</summary>
    /// <param name="adapter">The RTSS adapter owned and disposed by this service.</param>
    /// <param name="launcher">Starts and watches RTSS; the service disposes it.</param>
    /// <param name="persistValue">Saves one changed value to the profile layer in force.</param>
    /// <param name="profiles">The initial profile snapshot, or null for an empty snapshot.</param>
    /// <param name="enabled">Whether RTSS integration is switched on.</param>
    /// <param name="pollInterval">The RTSS poll interval; must be positive.</param>
    /// <param name="commandTimeout">How long one RTSS command may take; must be positive.</param>
    /// <param name="timeProvider">The clock.</param>
    internal PerformanceService(
        IRtssAdapter adapter,
        RtssLauncher launcher,
        Func<ProfileField, int, CancellationToken, Task<ProfileSnapshot>> persistValue,
        ProfileSnapshot? profiles = null,
        bool enabled = true,
        TimeSpan? pollInterval = null,
        TimeSpan? commandTimeout = null,
        TimeProvider? timeProvider = null)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _persistValue = persistValue ?? throw new ArgumentNullException(nameof(persistValue));
        PollInterval = pollInterval ?? DefaultPollInterval;
        _commandTimeout = commandTimeout ?? DefaultCommandTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(PollInterval, TimeSpan.Zero, nameof(pollInterval));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_commandTimeout, TimeSpan.Zero, nameof(commandTimeout));
        var clock = timeProvider ?? TimeProvider.System;
        _store = new PerformanceStateStore(profiles ?? ProfileSnapshot.Empty, enabled, clock);
        _refresh = new PerformanceRefreshLoop(
            RefreshCoreAsync,
            RefreshFailed,
            PollInterval,
            clock,
            _disposeCts.Token);
        _refresh.StartPolling();
    }

    /// <summary>Latest immutable state snapshot, safe to read across threads.</summary>
    internal PerformanceState Current => _store.Current;

    /// <summary>Whether the session currently permits RTSS control.</summary>
    internal bool Enabled => _store.Enabled;

    /// <summary>Interval between background availability and drift checks.</summary>
    internal TimeSpan PollInterval { get; }

    /// <summary>Closes admission and cancels polling before disposing the owned adapter.</summary>
    /// <returns>Completion of shutdown; a busy worker exceeding the wait budget retains its resources.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        var stopped = _refresh.Close();
        if (stopped is null)
        {
            return;
        }

        _disposed = true;
        _launcher.Dispose();
        await _disposeCts.CancelAsync().ConfigureAwait(false);
        try
        {
            await stopped.WaitAsync(_commandTimeout).ConfigureAwait(false);
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

    /// <summary>Publishes meaningful state changes on the calling worker; UI subscribers must marshal to their dispatcher.</summary>
    internal event Action<PerformanceState>? StateChanged
    {
        add => _store.StateChanged += value;
        remove => _store.StateChanged -= value;
    }

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

    /// <summary>Reads the session's one sensor sample, for AutoTDP as well as the OSD.</summary>
    /// <returns>The latest sample, or an empty one after disposal or without a provider.</returns>
    internal RtssOsdMetrics SampleSensors()
    {
        return _disposed ? RtssOsdMetrics.Empty : _adapter.SampleSensors();
    }

    /// <summary>Applies a new profile snapshot or master switch and writes what now resolves.</summary>
    /// <param name="profiles">The profile store and the running application it resolves for.</param>
    /// <param name="enabled">Whether WSGM may change RTSS at all.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <returns>Completion of the apply attempt; unavailable or unchanged values can be skipped.</returns>
    /// <remarks>
    ///     The running application comes from the snapshot rather than a separate target, so RTSS and
    ///     every other per-game consumer agree about what is running. An older snapshot than the one in
    ///     force is ignored: a value the user just set is already applied from the newer one.
    /// </remarks>
    internal async Task ApplyProfilesAsync(
        ProfileSnapshot profiles,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(profiles);
        PerformanceState previous;
        PerformanceState next;
        bool enabledNow;
        await _adapterGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_store.TryApplyProfiles(
                    profiles,
                    enabled,
                    _adapter.ClearOverlay,
                    out previous,
                    out next,
                    out enabledNow))
            {
                return;
            }
        }
        finally
        {
            _adapterGate.Release();
        }

        _store.Raise(next);
        if (enabledNow)
        {
            _refresh.Kick();
        }

        if (previous.Desired != next.Desired || previous.Target != next.Target
                                             || previous.ApplicationProfileEnabled != next.ApplicationProfileEnabled)
        {
            await ApplyEffectiveDesiredAsync(
                previous.Target != next.Target ? "application-transition" : "profile-change",
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Persists a validated preference, then attempts its serialized RTSS write.</summary>
    /// <param name="control">The control to update.</param>
    /// <param name="value">Value to validate against the ready adapter's bounds.</param>
    /// <param name="origin">Submitting surface or workflow; sanitized for diagnostics.</param>
    /// <param name="correlationId">Request token; an empty token is replaced.</param>
    /// <param name="cancellationToken">Cancels queue admission or cooperative processing.</param>
    /// <returns>Terminal command state, including refusal, failure, or deferral until an executable is known.</returns>
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

    /// <summary>Joins or starts the shared availability and readback refresh.</summary>
    /// <param name="cancellationToken">Cancels only this caller's wait, not the shared refresh.</param>
    /// <returns>A task completing when the shared refresh finishes.</returns>
    internal Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        return _refresh.RefreshAsync(cancellationToken);
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
        var sequence = _store.NextSequence();

        PerformanceCommandState Command(PerformanceCommandPhase phase, string? diagnostic = null)
        {
            return new PerformanceCommandState(sequence, origin, correlationId, control, value, phase, diagnostic);
        }

        _store.UpdateCommand(Command(PerformanceCommandPhase.Queued));

        if (!_store.Enabled)
        {
            return _store.UpdateCommand(Command(
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
            return _store.UpdateCommand(Command(
                PerformanceCommandPhase.Rejected,
                _disposeCts.IsCancellationRequested
                    ? "RTSS is stopping."
                    : "Command was cancelled before it reached RTSS."));
        }

        try
        {
            // A queued command must recheck shutdown and policy after acquiring the adapter gate.
            if (_disposed)
            {
                return _store.UpdateCommand(Command(
                    PerformanceCommandPhase.Rejected,
                    "RTSS is stopping."));
            }

            if (!_store.Enabled)
            {
                return _store.UpdateCommand(Command(
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

    private void RefreshFailed(Exception ex)
    {
        Log.Error("RTSS refresh failed", ex);
        _store.MarkDegraded(ex.Message);
    }

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
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

        _launcher.Watch(Current.Probe, _refresh.Kick);

        // Starting RTSS can wait up to ten seconds for its tray process. Keep that settle outside
        // the adapter gate so UI commands can still observe and report the unavailable state.
        if (launchProbe is not null)
        {
            await _launcher.TryStartAsync(launchProbe, Enabled, RefreshStartedRtssAsync, admission.Token)
                .ConfigureAwait(false);
            _launcher.Watch(Current.Probe, _refresh.Kick);
        }

        // Also outside the gate, and for the same reason: the repair is an ordinary command and
        // takes the gate itself for every write it makes.
        if (Drifted())
        {
            await ApplyEffectiveDesiredAsync("drift-repair", admission.Token).ConfigureAwait(false);
        }
    }

    private async Task<bool> RefreshStartedRtssAsync(CancellationToken cancellationToken)
    {
        await _adapterGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RefreshInsideGateAsync(cancellationToken).ConfigureAwait(false);
            return Current.Probe.Availability == RtssAvailability.Ready;
        }
        finally
        {
            _adapterGate.Release();
        }
    }

    /// <summary>Whether RTSS reads back something other than the values WSGM wants.</summary>
    /// <returns>True when the effective desired values should be written again.</returns>
    /// <remarks>
    ///     HandheldCompanion's RTSS watchdog rule (<c>RTSSPlatform.Watchdog_Elapsed</c>): an RTSS
    ///     profile is a file its own UI, another overlay tool or a game's installer can rewrite without
    ///     announcing it, so every poll compares the readback with the desired values and writes them
    ///     again when they differ. A control without a desired value or without a readback is left
    ///     alone, so a user who set no frame limit is never fought over one.
    /// </remarks>
    private bool Drifted()
    {
        var snapshot = Current;
        if (!Enabled
            || snapshot.Command.Phase is PerformanceCommandPhase.Queued
                or PerformanceCommandPhase.Applying)
        {
            return false;
        }

        List<string> drift = [];
        if (snapshot.Desired.OverlayLevel is > 0 && snapshot.OverlayPresentationEnabled == false)
        {
            drift.Add("RTSS OSD presentation is disabled");
        }

        foreach (var control in DriftCheckedControls)
        {
            if (snapshot.Desired.ValueFor(control) is { } wanted
                && snapshot.Observed.ValueFor(control) is { } observed
                && observed != wanted)
            {
                drift.Add($"{control} is {observed} rather than {wanted}");
            }
        }

        if (drift.Count == 0)
        {
            return false;
        }

        Log.Change(
            "rtss.drift",
            $"RTSS drifted from what WSGM set ({string.Join("; ", drift)}); writing it again.",
            LogLevel.Warn);
        return true;
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
        // The RTSS profile this command addresses, for its outcome line; null until it is known.
        string? appliedProfile = null;

        PerformanceCommandState Report(PerformanceCommandPhase phase, string? diagnostic = null)
        {
            return _store.UpdateCommand(
                new PerformanceCommandState(sequence, origin, correlationId, control, value, phase, diagnostic),
                appliedProfile);
        }

        Report(PerformanceCommandPhase.Applying);

        try
        {
            var probe = await _adapter.ProbeAsync(boundedCancellation).ConfigureAwait(false);
            _store.PublishProbe(probe);
            if (probe.Availability != RtssAvailability.Ready || probe.Capabilities is null)
            {
                return Report(
                    PerformanceCommandPhase.Rejected,
                    probe.Diagnostic ?? "RTSS is unavailable.");
            }

            if (!probe.Capabilities.Supports(control)
                || !probe.Capabilities.IsValid(control, value))
            {
                return Report(
                    PerformanceCommandPhase.Rejected,
                    "The requested value is outside the adapter's verified bounds.");
            }

            if (updateDesired)
            {
                // Saved before RTSS is touched. The profile store decides which layer the value lands
                // in, and a write that could not be saved must not leave RTSS holding a value the
                // configuration does not name.
                ProfileSnapshot saved;
                try
                {
                    saved = await _persistValue(
                        control is PerformanceControl.FrameLimit ? ProfileField.FrameLimit : ProfileField.OverlayLevel,
                        value,
                        boundedCancellation).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Log.Error("Persisting the RTSS profile value failed", ex);
                    return Report(
                        PerformanceCommandPhase.Failed,
                        ex is ArgumentException or InvalidOperationException
                            ? ex.Message
                            : "The performance preference could not be persisted.");
                }

                _store.AcceptSaved(saved);
            }

            var (target, applicationOptedIn) = _store.CommandTarget();

            // Avoid creating per-executable profiles without opt-in. Existing profiles still need
            // updating because their explicit values override the global profile.
            var profile = EffectiveRtssProfile(target, applicationOptedIn);
            appliedProfile = target is null
                ? string.Empty
                : target.RtssProfileName is null
                    ? $"pending application {target.ApplicationId}"
                    : profile;

            _store.Raise(Current);

            if (control == PerformanceControl.FrameLimit && target is { RtssProfileName: null or "" })
            {
                return Report(
                    PerformanceCommandPhase.Deferred,
                    "The application preference was saved and will apply when its foreground "
                    + "executable is known.");
            }

            // HandheldCompanion's RTSSPlatform.SetTargetFPS: load the profile, set the property, save
            // it, update the profiles and move on. The written value is published as observed until
            // the next poll reads the profile back.
            var applied = await _adapter.ApplyAsync(
                new RtssApplyRequest(profile, control, value),
                boundedCancellation).ConfigureAwait(false);
            if (!applied.Applied)
            {
                return Report(
                    PerformanceCommandPhase.Rejected,
                    applied.Diagnostic ?? "RTSS rejected the profile update.");
            }

            _store.PublishWritten(control, value);
            return Report(PerformanceCommandPhase.Applied);
        }
        catch (OperationCanceledException)
        {
            return Report(
                PerformanceCommandPhase.Failed,
                callerCancellation.IsCancellationRequested
                    ? "The caller cancelled after RTSS command processing began."
                    : "RTSS did not finish within the bounded command timeout.");
        }
        catch (Exception ex)
        {
            Log.Error("RTSS performance command failed", ex);
            _store.MarkDegraded(ex.Message);
            return Report(PerformanceCommandPhase.Failed, ex.Message);
        }
    }

    private async Task ApplyEffectiveDesiredAsync(string origin, CancellationToken cancellationToken)
    {
        var snapshot = Current;
        if (snapshot.Desired.FrameLimit is { } frameLimit
            && (origin != "drift-repair" || snapshot.Target is not { RtssProfileName: null or "" }))
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

    private async Task<RtssProbe?> RefreshInsideGateAsync(CancellationToken cancellationToken)
    {
        var probe = await _adapter.ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (probe.Availability != RtssAvailability.Ready || probe.Capabilities is null)
        {
            _store.PublishUnavailable(probe);
            return probe;
        }

        var target = Current.Target;
        var applicationOptedIn = _store.ApplicationOptedIn;

        // The same profile-selection rule as the apply path, so readback observes the profile the
        // writes actually target instead of reporting a phantom external change against a
        // never-written application profile.
        var readback = await _adapter.ReadAsync(
            EffectiveRtssProfile(target, applicationOptedIn),
            cancellationToken).ConfigureAwait(false);
        if (target is { RtssProfileName: null or "" })
        {
            readback = readback with { Values = readback.Values with { FrameLimit = null } };
        }

        _store.PublishReadback(probe, readback);
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

    private static string SanitizeToken(string value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        string sanitized = new(value.Where(character => !char.IsControl(character)).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? fallback : sanitized;
    }
}
