using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     Feeds Valve's Performance tab, the unified frame-limit row and the variable-refresh switch from
///     the RTSS-backed performance service, and applies what they write.
/// </summary>
internal sealed class PerformanceServiceNativeQamAdapter :
    ISteamPerformanceBackend,
    ISteamFrameLimitBackend,
    ISteamVariableRefreshBackend
{
    private readonly ProfileService _profiles;
    private readonly PerformanceService _service;

    /// <summary>Creates the adapter.</summary>
    /// <param name="service">The RTSS-backed performance service.</param>
    /// <param name="profiles">The profile owner Steam's per-game toggle and reset write to.</param>
    internal PerformanceServiceNativeQamAdapter(PerformanceService service, ProfileService profiles)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
    }

    /// <summary>Projects the latest RTSS state and display-supported limits without performing a new read.</summary>
    internal SteamFrameLimitState FrameLimit => ProjectFrameLimit(
        _service.Current,
        _service.Enabled,
        PerfSupport?.Invoke());

    /// <summary>The variable-refresh switch, straight from the device capability.</summary>
    /// <remarks>
    ///     Availability follows the plugin's published capability and nothing else: a machine whose
    ///     device publishes no VRR capability has no switch, rather than one that refuses every press.
    /// </remarks>
    internal SteamVariableRefreshState Vrr
    {
        get
        {
            var support = PerfSupport?.Invoke();
            var available = support?.VariableRefreshRateSupported == true
                            && ApplyVariableRefreshRate is not null;
            var enabled = support?.VariableRefreshRateEnabled == true;
            return new SteamVariableRefreshState(
                available,
                enabled,
                "idle",
                available
                    ? enabled
                        ? "The panel follows the frame rate."
                        : "The panel holds a fixed refresh rate."
                    : "This device publishes no variable-refresh capability.",
                CapabilityProjection.OverrideId(_profiles.Current.Layers,
                    new ProfileSettingKey(ProfileField.VariableRefreshRate)) is not null);
        }
    }

    /// <summary>
    ///     Supplies what the device can currently back, for the reactivated performance panel.
    /// </summary>
    /// <remarks>
    ///     Injected rather than read here because the frame-limit options come from display-mode
    ///     discovery and the VRR flag from the device plugin, neither of which this adapter owns. The
    ///     default reports nothing supported, which hides every control rather than showing one that
    ///     writes nowhere.
    /// </remarks>
    internal Func<NativeQamPerfSupport>? PerfSupport { get; init; }

    /// <summary>Applies a manually chosen refresh rate, when the session allows one.</summary>
    /// <remarks>
    ///     Set only where a manual refresh rate is meaningful. Under the pairing strategies the frame
    ///     cap owns the refresh rate, so this stays unset and a write is refused by name rather than
    ///     fighting the pairing on the user's behalf — the row is hidden there anyway, because the
    ///     projection omits its limits.
    /// </remarks>
    internal Func<int, bool>? ApplyRefreshRate { get; init; }

    /// <summary>Turns variable refresh rate on or off, when a device publishes it.</summary>
    /// <remarks>
    ///     Unset on a machine whose plugin publishes no VRR capability, which is also when the
    ///     projection omits <c>is_vrr_supported</c> and Valve's own row does not render. Both follow the
    ///     same fact, from the same source, so the row cannot appear without a way to act on it.
    /// </remarks>
    internal Func<bool, CancellationToken, Task<bool>>? ApplyVariableRefreshRate { get; init; }

    /// <summary>The state Steam's own performance panel reads every control's value out of.</summary>
    internal SteamPerformanceState PerfState
    {
        get
        {
            var current = _service.Current;
            var support = PerfSupport?.Invoke()
                          ?? new NativeQamPerfSupport([], false, false, null, null);

            return NativeQamPerfProjection.Project(
                current.Desired,
                support,
                current.Target?.SteamAppId,
                // The VRR state and the refresh rate in force are read from `support` inside, so
                // the manual refresh row cannot be advertised in `limits` without its value in
                // `settings`, which is half of what once crashed the Performance tab.
                current.ApplicationProfileEnabled);
        }
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> SetFrameLimitAsync(
        int fps,
        string correlationId,
        CancellationToken cancellationToken)
    {
        return SetAsync(
            PerformanceControl.FrameLimit,
            fps,
            correlationId,
            cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     The unified row's other mode. With the frame limit off there is no cap to pair a rate to, so
    ///     the slider becomes the refresh rate itself and writes here — which is why this is available
    ///     under every strategy, unlike the manual-refresh row, whose whole problem was fighting a
    ///     pairing that was still active.
    /// </remarks>
    public Task<SteamUiCommandResult> SetRefreshRateAsync(int hz, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        return Task.FromResult(SetRefreshRate(hz));
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Failures are collected rather than aborting: refusing the rest of a delta because one field
    ///     has no backend would drop settings WSGM can honour, and the panel's own state would then
    ///     disagree with the device until the next publish.
    /// </remarks>
    public async Task<SteamUiCommandResult> ApplyAsync(
        SteamPerformanceDelta delta,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (delta.Unsupported.Count > 0)
        {
            // Named, because the alternative is a control the user operates that quietly does
            // nothing and cannot be diagnosed from a pasted log.
            Log.Warn(
                "Native QAM performance delta carried fields with no WSGM backend: "
                + string.Join(", ", delta.Unsupported));
        }

        if (delta.SteamAppId is { } requestedAppId
            && _service.Current.Target?.SteamAppId != requestedAppId)
        {
            var current = _service.Current.Target?.SteamAppId is { } currentAppId
                ? $"AppID {currentAppId}"
                : "no Steam application";
            var error = $"The performance delta targets stale AppID {requestedAppId}; {current} is current.";
            Log.Warn($"Native QAM performance delta refused: {error}");
            return new SteamUiCommandResult(false, error);
        }

        if (delta.ResetToDefault)
        {
            Log.Info(
                "Native QAM performance reset requested for "
                + $"{(delta.SteamAppId is { } id ? $"AppID {id}" : "the global profile")}.");

            // A reset arrives on its own, not alongside value changes: Valve's button sends only
            // this flag. Returning here rather than falling through keeps that explicit.
            return await ResetProfileAsync(cancellationToken).ConfigureAwait(false);
        }

        if (delta.Recognized.Count == 0)
        {
            Log.Info(
                "Native QAM performance delta contained nothing WSGM backs; no change was made.");
            return new SteamUiCommandResult(false, "The performance delta carried no supported change.");
        }

        string? failure = null;
        foreach (var received in delta.Recognized)
        {
            // The overlay level travels as Valve's enum value, not the notch the user picked —
            // see SteamOverlayLevelWire. Everything downstream speaks notches.
            var change = received.Kind is SteamPerformanceSetting.OverlayLevel
                ? received with { Value = SteamOverlayLevelWire.ToNotch(received.Value) }
                : received;

            // Echo suppression, the volume/brightness rule: Steam's side re-sends values it did
            // not originate — a control committing its computed value after WSGM's own state
            // publication moved it, or a settings replay restating the store. Applying a
            // restatement made the level ping-pong between two writers at the poll cadence
            // (device log 2026-09-01, 21:42: OverlayLevel alternating 4/0 every ~2 s with the
            // user idle). A value that already equals WSGM's desired one changes nothing and is
            // dropped before it can re-enter the loop; a genuine user change always differs.
            if (RestatesDesired(change))
            {
                Log.Change(
                    $"native-qam-echo-{change.Kind}",
                    $"Native QAM delta restated {change.Kind}={change.Value}; already desired — skipped.");
                continue;
            }

            var result = await ApplyPerfChangeAsync(
                change,
                correlationId,
                cancellationToken).ConfigureAwait(false);
            if (result.Succeeded)
            {
                continue;
            }

            Log.Warn(
                $"Native QAM performance change {change.Kind}={change.Value} failed: "
                + (result.Error ?? "no reason reported"));
            failure ??= result.Error;
        }

        return new SteamUiCommandResult(failure is null, failure);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> SetVariableRefreshRateAsync(
        bool enabled,
        CancellationToken cancellationToken)
    {
        if (ApplyVariableRefreshRate is { } apply)
        {
            return ApplyFlagAsync(apply, enabled, "variable refresh rate", cancellationToken);
        }

        const string reason = "This device publishes no variable-refresh capability.";
        Log.Warn($"Native QAM variable refresh {(enabled ? "on" : "off")} refused: {reason}");
        return Task.FromResult(new SteamUiCommandResult(false, reason));
    }

    /// <summary>Applies a refresh rate from either of Steam's refresh controls.</summary>
    /// <param name="hz">The rate the control sent.</param>
    /// <returns>Success, or the refusal with its reason.</returns>
    private SteamUiCommandResult SetRefreshRate(int hz)
    {
        if (ApplyRefreshRate is null)
        {
            const string reason = "This session cannot change the refresh rate.";
            Log.Warn($"Native QAM refresh rate {hz} Hz refused: {reason}");
            return new SteamUiCommandResult(false, reason);
        }

        return ApplyRefreshRate(hz)
            ? new SteamUiCommandResult(true, null)
            : new SteamUiCommandResult(false, $"The display refused {hz} Hz.");
    }

    /// <summary>The cap the enable toggle applies when no cap is set yet.</summary>
    /// <remarks>
    ///     The last cap the service still holds when there is one, else the highest offered notch —
    ///     which is also the value the projection shows on the disabled slider, so the cap that takes
    ///     effect is the number the user was already looking at.
    /// </remarks>
    private int EnableFrameLimitFps()
    {
        var desired = _service.Current.Desired.FrameLimit ?? 0;
        return desired > 0
            ? desired
            : NativeQamPerfProjection.HighestOption(PerfSupport?.Invoke().FrameLimitOptions ?? []);
    }

    /// <summary>Whether a delta field only restates the value WSGM already wants.</summary>
    /// <param name="change">The decoded change.</param>
    /// <returns>True to drop the change as an echo.</returns>
    /// <remarks>
    ///     Only the RTSS-backed settings are judged here: their desired values live in the
    ///     performance service and are what the state publication told Steam in the first place. The
    ///     display-owned settings pass through; their owners are idempotent.
    /// </remarks>
    private bool RestatesDesired(SteamPerformanceChange change)
    {
        var desired = _service.Current.Desired;
        return change.Kind switch
        {
            SteamPerformanceSetting.OverlayLevel => desired.OverlayLevel == change.Value,
            SteamPerformanceSetting.FrameLimit => desired.FrameLimit == change.Value,
            SteamPerformanceSetting.FrameLimitEnabled =>
                change.AsFlag == desired.FrameLimit is > 0,
            _ => false
        };
    }

    /// <summary>Applies one change from Steam's own performance panel.</summary>
    /// <param name="change">The decoded change, with the overlay level already in notches.</param>
    /// <param name="correlationId">Correlates the command across the log.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns>Whether the change was applied.</returns>
    /// <remarks>
    ///     Only the settings behind a control WSGM mounts and can honour. Anything else is refused with
    ///     its name, never accepted-and-dropped: a control that appears to work and does nothing is
    ///     worse than one that never rendered.
    /// </remarks>
    internal Task<SteamUiCommandResult> ApplyPerfChangeAsync(
        SteamPerformanceChange change,
        string correlationId,
        CancellationToken cancellationToken)
    {
        return change.Kind switch
        {
            SteamPerformanceSetting.FrameLimit => SetAsync(
                PerformanceControl.FrameLimit,
                change.Value,
                correlationId,
                cancellationToken),

            // Steam models the cap and its switch separately; RTSS has one value where zero is off.
            // Disabling writes zero. Enabling must WRITE A CAP: Valve's toggle sends only the flag,
            // and treating it as a no-op left the slider grey with a switch that snapped straight
            // back — there is no "enabled with no value" state on the RTSS side for it to mean.
            SteamPerformanceSetting.FrameLimitEnabled when !change.AsFlag => SetAsync(
                PerformanceControl.FrameLimit,
                0,
                correlationId,
                cancellationToken),
            SteamPerformanceSetting.FrameLimitEnabled => SetAsync(
                PerformanceControl.FrameLimit,
                EnableFrameLimitFps(),
                correlationId,
                cancellationToken),

            SteamPerformanceSetting.OverlayLevel => SetAsync(
                PerformanceControl.OverlayLevel,
                change.Value,
                correlationId,
                cancellationToken),

            // Straight to the service that owns the policy: creating or removing the application
            // layer is policy, not a value write, and routing it through SetAsync would need a
            // control that does not exist.
            SteamPerformanceSetting.PerApplicationProfileEnabled => ApplyProfileToggleAsync(
                change.AsFlag,
                cancellationToken),

            SteamPerformanceSetting.VariableRefreshRate when
                ApplyVariableRefreshRate is { } applyVrr =>
                ApplyFlagAsync(applyVrr, change.AsFlag, "variable refresh rate", cancellationToken),

            SteamPerformanceSetting.RefreshRateHz when ApplyRefreshRate is not null =>
                Task.FromResult(SetRefreshRate(change.Value)),

            _ => Task.FromResult(
                new SteamUiCommandResult(
                    false,
                    $"The performance setting {change.Kind} has no WSGM backend yet."))
        };
    }

    private async Task<SteamUiCommandResult> SetAsync(
        PerformanceControl control,
        int value,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var result = await _service.SetAsync(
            control,
            value,
            "native-qam",
            correlationId,
            cancellationToken).ConfigureAwait(false);
        var succeeded = result.Phase is
            PerformanceCommandPhase.Deferred
            or PerformanceCommandPhase.Applied;
        return new SteamUiCommandResult(
            succeeded,
            succeeded
                ? null
                : NativeQamUi.Text(result.Diagnostic ?? PhaseFailure(result.Phase)));
    }

    /// <summary>Resets the profile in force to its defaults.</summary>
    /// <param name="cancellationToken">Cancels the reset.</param>
    /// <returns>Success once the reset ran.</returns>
    /// <remarks>
    ///     A reset that changes nothing because the profile is already at defaults is reported as a
    ///     success, unlike the toggle: the user asked for a state and that state is what they have.
    /// </remarks>
    private async Task<SteamUiCommandResult> ResetProfileAsync(
        CancellationToken cancellationToken)
    {
        await _profiles.ResetAsync(cancellationToken).ConfigureAwait(false);
        return new SteamUiCommandResult(true, null);
    }

    /// <remarks>
    ///     A refusal is reported rather than swallowed. The toggle is controlled, so an unreported
    ///     failure shows it moved and then snaps it back on the next publish with no explanation — and
    ///     "no application is running" is exactly the case a user hits by opening the menu on the
    ///     desktop.
    /// </remarks>
    private async Task<SteamUiCommandResult> ApplyProfileToggleAsync(
        bool enabled,
        CancellationToken cancellationToken)
    {
        if (_service.Current.Target is null)
        {
            return new SteamUiCommandResult(
                false,
                "The per-application profile could not be changed; no identifiable application is "
                + "running.");
        }

        // False means the running application changed under the request or the edit did not take,
        // not that nothing is running.
        var reached = await _profiles.SetGameEnabledAsync(enabled, _service.Current.Target.ApplicationId,
            cancellationToken).ConfigureAwait(false);
        return reached
            ? new SteamUiCommandResult(true, null)
            : new SteamUiCommandResult(false, "The per-application profile could not be changed.");
    }

    /// <remarks>
    ///     The device write is awaited rather than fired and forgotten: Steam's toggle is controlled, so
    ///     reporting success before the device answered would show it moved and then snap it back on the
    ///     next publish.
    /// </remarks>
    private static async Task<SteamUiCommandResult> ApplyFlagAsync(
        Func<bool, CancellationToken, Task<bool>> apply,
        bool enabled,
        string what,
        CancellationToken cancellationToken)
    {
        var applied = await apply(enabled, cancellationToken).ConfigureAwait(false);
        return applied
            ? new SteamUiCommandResult(true, null)
            : new SteamUiCommandResult(
                false,
                $"The device refused to turn {what} {(enabled ? "on" : "off")}.");
    }

    /// <summary>Builds frame-cap and display-refresh controls from current RTSS and panel capabilities.</summary>
    /// <param name="state">The performance service's current state.</param>
    /// <param name="enabled">Whether RTSS control is switched on at all.</param>
    /// <param name="support">
    ///     What the panel can hold. Its option list bookends the slider, because RTSS's own range is
    ///     0-1000 and a slider spanning that is not a control anyone can aim — the display decides
    ///     what a cap can usefully be, not the limiter.
    /// </param>
    /// <returns>The row's state.</returns>
    internal static SteamFrameLimitState ProjectFrameLimit(
        PerformanceState state,
        bool enabled,
        NativeQamPerfSupport? support = null)
    {
        var capabilities = state.Probe.Capabilities;
        var supported = capabilities?.Supports(PerformanceControl.FrameLimit) == true;
        var available = enabled
                        && state.Probe.Availability == RtssAvailability.Ready
                        && supported;

        // Zero is "off" and is never a slider position, so it is filtered out of both bookends.
        var caps = support?.FrameLimitOptions ?? [];
        var panelMinimum = NativeQamPerfProjection.LowestOption(caps);
        var panelMaximum = caps.Aggregate(0, Math.Max);

        // The overlay's bounds: the panel's band, else RTSS's floor, both under the shared ceiling.
        (int Minimum, int Maximum, bool FromPanel)? bounds = supported
            ? PerformanceProjection.FrameLimitBounds(capabilities!,
                panelMinimum > 0 && panelMaximum > 0 ? (panelMinimum, panelMaximum) : null)
            : null;
        var minimum = bounds?.Minimum;
        var maximum = bounds?.Maximum;
        return new SteamFrameLimitState(
            available,
            minimum,
            maximum,
            ValidValue(state.Desired.FrameLimit, capabilities, PerformanceControl.FrameLimit),
            ValidValue(state.Observed.FrameLimit, capabilities, PerformanceControl.FrameLimit),
            ProgressText(state.Command, PerformanceControl.FrameLimit),
            FaultText(state.Command, PerformanceControl.FrameLimit),
            StatusText(state, PerformanceControl.FrameLimit, available),
            // Off is a switch of its own, the way SteamOS's "Disable Frame Limit" is, so the
            // slider never has to spend a position on it and the cap the user last chose survives
            // being switched off and back on.
            state.Desired.FrameLimit is > 0,
            support?.RefreshForCap,
            // The bounds of the row's OTHER mode. Present whenever the display has rates to offer,
            // independent of RefreshRatesSelectable — that flag governs Valve's separate manual
            // row, which must stay hidden while a cap owns the rate. Here the cap is off, so there
            // is nothing to fight.
            support?.RefreshRateMinHz,
            support?.RefreshRateMaxHz,
            support?.CurrentRefreshRateHz,
            // The stops that mode slides between. Windows accepts a MODE, not a rate: it either
            // has 75 Hz or it does not, and asking for 72 gets a refusal, not the nearest thing.
            support?.RefreshRates,
            state.FrameLimitLayer is ProfileSource.Game);
    }

    private static int? ValidValue(
        int? value,
        RtssCapabilities? capabilities,
        PerformanceControl control)
    {
        return value is { } integer
               && capabilities?.IsValid(control, integer) == true
            ? integer
            : null;
    }

    private static string ProgressText(
        PerformanceCommandState command,
        PerformanceControl control)
    {
        if (command.Phase != PerformanceCommandPhase.Idle && command.Control != control)
        {
            return "idle";
        }

        return command.Phase switch
        {
            PerformanceCommandPhase.Queued => "queued",
            PerformanceCommandPhase.Applying => "applying",
            PerformanceCommandPhase.Deferred => "deferred",
            PerformanceCommandPhase.Applied => "applied",
            PerformanceCommandPhase.Rejected => "rejected",
            PerformanceCommandPhase.Failed => "failed",
            PerformanceCommandPhase.ExternalChange => "external-change",
            _ => "idle"
        };
    }

    private static string FaultText(
        PerformanceCommandState command,
        PerformanceControl control)
    {
        return command.Control == control
               && command.Phase is PerformanceCommandPhase.Rejected
                   or PerformanceCommandPhase.Failed
            ? NativeQamUi.Text(command.Diagnostic ?? PhaseFailure(command.Phase))
            : string.Empty;
    }

    private static string StatusText(
        PerformanceState state,
        PerformanceControl control,
        bool available)
    {
        var fault = FaultText(state.Command, control);
        if (!string.IsNullOrEmpty(fault))
        {
            return fault;
        }

        if (!available)
        {
            // The overlay's order: a known availability's sentence, else the probe's own diagnostic.
            return NativeQamUi.Text(PerformanceProjection.UnavailableText(state.Probe.Availability)
                                    ?? state.Probe.Diagnostic
                                    ?? "RTSS performance control is not currently available.");
        }

        return NativeQamUi.Text(PerformanceProjection.TargetText(state.Target));
    }

    private static string PhaseFailure(PerformanceCommandPhase phase)
    {
        return phase switch
        {
            PerformanceCommandPhase.Rejected => "The RTSS command was rejected.",
            PerformanceCommandPhase.Failed => "The RTSS command failed.",
            _ => "The RTSS command did not complete."
        };
    }
}
