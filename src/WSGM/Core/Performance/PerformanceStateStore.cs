using System;
using System.Collections.Generic;
using System.Threading;

namespace WSGM.Core;

/// <summary>
///     The shared RTSS performance state: the profile snapshot in force, the master switch, the
///     resolved desired values, the latest probe and readback, and the command that last reported.
/// </summary>
/// <remarks>
///     Every mutation replaces one immutable <see cref="PerformanceState" /> under one lock, so a reader
///     never sees half an update. Subscribers hear only meaningful changes, on the calling worker.
///     <see cref="PerformanceService" /> owns the adapter and decides what to write; this owns what the
///     session believes and how every outcome is recorded.
/// </remarks>
internal sealed class PerformanceStateStore
{
    private static readonly RtssProbe InitialProbe = new(
        RtssAvailability.Unknown,
        null,
        null,
        0,
        null,
        "RTSS discovery has not run.");

    private readonly Dictionary<long, string> _commandProfiles = [];
    private readonly Lock _gate = new();
    private readonly TimeProvider _timeProvider;
    private long _commandSequence;
    private bool _enabled;
    private ProfileSnapshot _profiles;
    private PerformanceState? _raisedState;
    private PerformanceState _state;

    /// <summary>Creates the state for a profile snapshot and master switch.</summary>
    /// <param name="profiles">The initial profile snapshot.</param>
    /// <param name="enabled">Whether RTSS integration is switched on.</param>
    /// <param name="timeProvider">The clock refresh timestamps come from.</param>
    internal PerformanceStateStore(ProfileSnapshot profiles, bool enabled, TimeProvider timeProvider)
    {
        _profiles = profiles;
        _enabled = enabled;
        _timeProvider = timeProvider;
        _state = WithResolvedDesired(new PerformanceState(
            InitialProbe,
            PerformanceApplicationTarget.For(_profiles.Active),
            false,
            ProfileSource.None,
            ProfileSource.None,
            PerformanceValues.Empty,
            PerformanceValues.Empty,
            null,
            PerformanceCommandState.Idle));
    }

    /// <summary>Latest immutable state snapshot, safe to read across threads.</summary>
    internal PerformanceState Current
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>Whether the session currently permits RTSS control.</summary>
    internal bool Enabled
    {
        get
        {
            lock (_gate)
            {
                return _enabled;
            }
        }
    }

    /// <summary>Whether WSGM policy holds a per-application entry for the running application.</summary>
    internal bool ApplicationOptedIn
    {
        get
        {
            lock (_gate)
            {
                return _profiles.EditsGame;
            }
        }
    }

    /// <summary>Publishes meaningful state changes on the calling worker; UI subscribers must marshal to their dispatcher.</summary>
    internal event Action<PerformanceState>? StateChanged;

    /// <summary>Allocates the next command sequence number.</summary>
    /// <returns>A number greater than every earlier one.</returns>
    internal long NextSequence()
    {
        return Interlocked.Increment(ref _commandSequence);
    }

    /// <summary>Takes a new profile snapshot or master switch, unless it is older than the one in force.</summary>
    /// <param name="profiles">The profile store and the running application it resolves for.</param>
    /// <param name="enabled">Whether WSGM may change RTSS at all.</param>
    /// <param name="clearOverlay">Clears WSGM's OSD; called under the state lock when integration switches off.</param>
    /// <param name="previous">Receives the state before the change.</param>
    /// <param name="next">Receives the state after it.</param>
    /// <param name="enabledNow">Receives whether this switched integration on.</param>
    /// <returns>False when the snapshot was older or changed nothing, and no state moved.</returns>
    /// <remarks>Does not raise <see cref="StateChanged" />; the caller raises <paramref name="next" /> once its adapter gate is free.</remarks>
    internal bool TryApplyProfiles(
        ProfileSnapshot profiles,
        bool enabled,
        Action clearOverlay,
        out PerformanceState previous,
        out PerformanceState next,
        out bool enabledNow)
    {
        lock (_gate)
        {
            previous = _state;
            next = _state;
            enabledNow = false;
            if (profiles.Generation < _profiles.Generation
                || (ReferenceEquals(profiles, _profiles) && enabled == _enabled))
            {
                return false;
            }

            enabledNow = enabled && !_enabled;
            _profiles = profiles;
            _enabled = enabled;
            _state = WithResolvedDesired(_state with { Target = PerformanceApplicationTarget.For(profiles.Active) });
            if (!enabled)
            {
                clearOverlay();
                _state = _state with { Observed = _state.Observed.With(PerformanceControl.OverlayLevel, 0) };
            }

            next = _state;
            return true;
        }
    }

    /// <summary>Takes the snapshot a persisted command produced, unless a newer one is already in force.</summary>
    /// <param name="saved">The snapshot the profile store returned.</param>
    internal void AcceptSaved(ProfileSnapshot saved)
    {
        lock (_gate)
        {
            if (saved.Generation >= _profiles.Generation)
            {
                _profiles = saved;
                _state = WithResolvedDesired(_state with { Target = PerformanceApplicationTarget.For(saved.Active) });
            }
        }
    }

    /// <summary>Reads the target a command addresses together with the opt-in that selects its profile.</summary>
    /// <returns>The running-application target, or null for global, and whether the application is opted in.</returns>
    internal (PerformanceApplicationTarget? Target, bool ApplicationOptedIn) CommandTarget()
    {
        lock (_gate)
        {
            return (_state.Target, _profiles.EditsGame);
        }
    }

    /// <summary>Records which RTSS profile a command is about to write, for its outcome line.</summary>
    /// <param name="sequence">The command.</param>
    /// <param name="profile">The profile description; empty for the global profile.</param>
    internal void RecordCommandProfile(long sequence, string profile)
    {
        lock (_gate)
        {
            _commandProfiles[sequence] = profile;
        }
    }

    /// <summary>Publishes an unavailable probe: no readback, no presentation state.</summary>
    /// <param name="probe">The probe that was not ready.</param>
    internal void PublishUnavailable(RtssProbe probe)
    {
        PerformanceState unavailable;
        RtssProbe previousProbe;
        lock (_gate)
        {
            previousProbe = _state.Probe;
            _state = WithResolvedDesired(_state with
            {
                Probe = probe,
                Observed = PerformanceValues.Empty,
                OverlayPresentationEnabled = null,
                RefreshedAt = _timeProvider.GetUtcNow()
            });
            unavailable = _state;
        }

        LogProbeChange(previousProbe, probe);
        Raise(unavailable);
    }

    /// <summary>Publishes a ready probe's readback, recording a change nobody in WSGM made.</summary>
    /// <param name="probe">The ready probe.</param>
    /// <param name="readback">What RTSS reported.</param>
    internal void PublishReadback(RtssProbe probe, RtssReadback readback)
    {
        PerformanceState next;
        lock (_gate)
        {
            var changed = _state.RefreshedAt is not null
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
                OverlayPresentationEnabled = readback.OverlayPresentationEnabled,
                RefreshedAt = readback.Timestamp,
                Command = command
            });
            next = _state;
        }

        Raise(next);
    }

    /// <summary>Publishes a value WSGM just wrote as observed, until the next poll reads it back.</summary>
    /// <param name="control">The control written.</param>
    /// <param name="value">The value written.</param>
    internal void PublishWritten(PerformanceControl control, int value)
    {
        PerformanceState next;
        lock (_gate)
        {
            _state = _state with
            {
                Observed = _state.Observed.With(control, value),
                OverlayPresentationEnabled = control == PerformanceControl.OverlayLevel
                    ? value > 0 ? true : null
                    : _state.OverlayPresentationEnabled,
                RefreshedAt = _timeProvider.GetUtcNow()
            };
            next = _state;
        }

        Raise(next);
    }

    /// <summary>Publishes a command's probe.</summary>
    /// <param name="probe">The probe.</param>
    internal void PublishProbe(RtssProbe probe)
    {
        PerformanceState next;
        RtssProbe previous;
        lock (_gate)
        {
            previous = _state.Probe;
            _state = _state with { Probe = probe };
            next = _state;
        }

        LogProbeChange(previous, probe);
        Raise(next);
    }

    /// <summary>Marks RTSS degraded after a failure.</summary>
    /// <param name="diagnostic">What failed.</param>
    internal void MarkDegraded(string diagnostic)
    {
        PerformanceState next;
        lock (_gate)
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

        Raise(next);
    }

    /// <summary>Publishes a command's phase, unless a newer command already reported, and logs a terminal outcome.</summary>
    /// <param name="command">The command state.</param>
    /// <returns>The same command state, for the caller to return.</returns>
    internal PerformanceCommandState UpdateCommand(PerformanceCommandState command)
    {
        PerformanceState next;
        string? appliedProfile;
        lock (_gate)
        {
            if (command.Sequence >= _state.Command.Sequence)
            {
                _state = _state with { Command = command };
            }

            next = _state;
            _commandProfiles.TryGetValue(command.Sequence, out appliedProfile);
            if (command.Phase is not (PerformanceCommandPhase.Queued
                or PerformanceCommandPhase.Applying))
            {
                _commandProfiles.Remove(command.Sequence);
            }
        }

        LogCommandOutcome(command, next, appliedProfile);
        Raise(next);
        return command;
    }

    /// <summary>Raises <see cref="StateChanged" /> unless only the refresh timestamp moved.</summary>
    /// <param name="state">The state to announce.</param>
    internal void Raise(PerformanceState state)
    {
        // Timestamp-only refreshes do not change any subscriber's displayed state.
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
            or PerformanceCommandPhase.Applied;
        Log.Change(
            $"rtss.command.{command.Control}",
            $"RTSS {command.Control}={command.RequestedValue?.ToString() ?? "none"} on {profile} "
            + $"from {command.Origin}: {command.Phase}{detail}",
            succeeded ? LogLevel.Info : LogLevel.Warn);
    }

    private static PerformanceControl ChangedControl(PerformanceValues old, PerformanceValues current)
    {
        return old.FrameLimit != current.FrameLimit
            ? PerformanceControl.FrameLimit
            : PerformanceControl.OverlayLevel;
    }

    private PerformanceState WithResolvedDesired(PerformanceState state)
    {
        if (!_enabled)
        {
            return state with
            {
                Desired = PerformanceValues.Empty,
                ApplicationProfileEnabled = false,
                FrameLimitLayer = ProfileSource.None,
                OverlayLevelLayer = ProfileSource.None
            };
        }

        var layers = _profiles.Layers;
        var frameLimit = layers.Value(values => values.FrameLimit);
        var overlayLevel = layers.Value(values => values.OverlayLevel);
        return state with
        {
            Desired = new PerformanceValues(frameLimit.Value, overlayLevel.Value),
            ApplicationProfileEnabled = _profiles.EditsGame,
            FrameLimitLayer = frameLimit.Source,
            OverlayLevelLayer = overlayLevel.Source
        };
    }
}
