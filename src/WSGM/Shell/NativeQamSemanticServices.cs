using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using SteamUiToolkit;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>UI-thread marshalling for services whose backing managers own observable UI state.</summary>
/// <remarks>
///     The radio and audio managers reconcile observable collections the taskbar binds to, so their
///     calls are UI-thread owned while bridge requests arrive off the bridge's own thread.
/// </remarks>
internal static class NativeQamUi
{
    /// <summary>How long a device command from Steam's rows may take.</summary>
    internal static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Normalizes an optional detail into renderable text, whole.</summary>
    /// <param name="value">The detail, which may be null or blank.</param>
    /// <returns>The empty string for nothing to say, otherwise the text as given.</returns>
    internal static string Text(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value;
    }

    /// <summary>The word Steam shows for a command's progress.</summary>
    /// <param name="progress">The progress to describe.</param>
    /// <returns>The word, or empty when idle.</returns>
    internal static string ProgressText(CommandProgress progress)
    {
        return progress switch
        {
            CommandProgress.Pending => "applying",
            CommandProgress.Completed => "completed",
            CommandProgress.Failed => "failed",
            CommandProgress.Uncertain => "uncertain",
            _ => string.Empty
        };
    }

    /// <summary>The bounded status line for a device control.</summary>
    /// <param name="view">The capability view.</param>
    /// <param name="available">Whether the control is usable now.</param>
    /// <param name="unavailable">The text when it is unusable and the device gave no reason.</param>
    /// <param name="outOfRange">The text when the desired value no longer fits the descriptor.</param>
    /// <returns>The status line.</returns>
    internal static string StatusText(DeviceCapabilityView view, bool available, string unavailable, string outOfRange)
    {
        var detail = view.LastResult?.Reason?.Detail
                     ?? view.Projection.State.Reason?.Detail;
        if (!available && string.IsNullOrWhiteSpace(detail))
        {
            detail = unavailable;
        }
        else if (view.Projection.DesiredValueOutOfRange)
        {
            detail = outOfRange;
        }

        return Text(detail);
    }

    /// <summary>The Steam command result for a finished device command.</summary>
    /// <param name="result">The device command result.</param>
    /// <param name="fallback">The failure text when the device gave no reason.</param>
    /// <returns>Success when the value reached the device, else the failure with its reason.</returns>
    internal static SteamUiCommandResult CommandResult(CapabilityCommandResult result, string fallback)
    {
        // Accepted is a native per-application value saved for the running game: its driver applies it.
        var succeeded = result.Outcome.IsApplied() || result.Outcome is CommandOutcome.Accepted;
        return new SteamUiCommandResult(succeeded, succeeded ? null : result.Reason?.Detail ?? fallback);
    }

    /// <summary>Runs one manager call on the UI thread.</summary>
    /// <param name="action">The call to make.</param>
    /// <param name="cancellationToken">Checked before dispatch; the call itself is not cancelled.</param>
    /// <returns>A task completing after it ran.</returns>
    internal static Task RunAsync(Action action, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Dispatcher.UIThread.InvokeAsync(action).GetTask();
    }
}

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
                current.ApplicationProfileEnabled,
                true,
                support.VariableRefreshRateEnabled,
                // Was hardcoded null, which advertised the manual refresh row in `limits` while
                // giving it no value in `settings` — half of what crashed the Performance tab.
                support.CurrentRefreshRateHz);
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

        int? minimum = panelMinimum > 0 ? panelMinimum : supported ? capabilities!.MinimumFrameLimit : null;
        int? maximum = panelMaximum > 0 ? panelMaximum : supported ? capabilities!.MaximumFrameLimit : null;
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
            return NativeQamUi.Text(state.Probe.Diagnostic ?? state.Probe.Availability switch
            {
                RtssAvailability.NotInstalled => "RTSS is not installed.",
                RtssAvailability.NotRunning => "RTSS is not running.",
                RtssAvailability.Incompatible => "The installed RTSS version is incompatible.",
                RtssAvailability.AdapterUnavailable => "The RTSS profile adapter is unavailable.",
                _ => "RTSS performance control is not currently available."
            });
        }

        return state.Target switch
        {
            null => "RTSS global profile",
            { RtssProfileName: { Length: > 0 } profile } =>
                NativeQamUi.Text($"RTSS application profile: {profile}"),
            { SteamAppId: { } appId } => NativeQamUi.Text(
                $"Steam AppID {appId}; waiting for its foreground executable."),
            _ => "Waiting for the foreground application's executable profile."
        };
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

/// <summary>Projects sustained and boost readback into Steam's power sliders.</summary>
/// <remarks>
///     With a null coordinator (device integration not active this session) the state is the constant
///     unavailable one and every write is refused with its reason, so the surface stays honest without
///     a separate stand-in implementation. Publication follows the coordinator's capability and
///     configuration changes, which the Steam UI host hears once for every device row.
/// </remarks>
internal sealed class DeviceCoordinatorNativeQamTdpService : ISteamPowerLimitBackend
{
    private const string UnavailableText = "Device Integration is not active in this session.";

    private readonly DeviceCoordinator? _coordinator;

    /// <summary>Creates the power-limit projection without acquiring device ownership.</summary>
    /// <param name="coordinator">Borrowed device coordinator, or null to publish unavailable state and refuse writes.</param>
    internal DeviceCoordinatorNativeQamTdpService(DeviceCoordinator? coordinator)
    {
        _coordinator = coordinator;
    }

    /// <summary>Both sliders follow device readback, including profile changes.</summary>
    internal SteamPowerLimitState PowerLimit
    {
        get
        {
            var views = _coordinator?.Capabilities.Snapshot() ?? [];
            var state = ProjectPowerLimits(views);
            var layers = _coordinator?.Profiles.Current.Layers;
            return state with
            {
                Sustained = state.Sustained with
                {
                    Accent = layers is { } resolved
                             && CapabilityProjection.OverrideId(resolved, resolved.PowerTargetKey) is not null
                },
                Boost = state.Boost with
                {
                    Accent = views.FirstOrDefault(view => view.Descriptor.Role is CapabilityRole.PowerSlowLimit)
                                 is { } boost
                             && CapabilityProjection.DeviceOverrideId(boost) is not null
                },
                Unified = _coordinator?.ManualTdpUnified == true,
                CanSelectMode = _coordinator?.ManualTdpMode.Available == true,
                ModeAccent =
                CapabilityProjection.OverrideId(layers, new ProfileSettingKey(ProfileField.TdpUnified)) is not null
            };
        }
    }

    /// <inheritdoc />
    /// <remarks>Profile changes republish the sliders. Cancellation is checked before the coordinator save; later cancellation does not revoke it. An unavailable mode returns its refusal reason.</remarks>
    public async Task<SteamUiCommandResult> SetUnifiedModeAsync(bool unified, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_coordinator is null)
        {
            return SteamUiCommandResult.Refused;
        }

        try
        {
            await _coordinator.SetManualTdpModeAsync(unified).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return new SteamUiCommandResult(false, ex.Message);
        }

        return new SteamUiCommandResult(true, null);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> SetPrimaryLimitAsync(int watts, CancellationToken cancellationToken)
    {
        return SetLimitAsync(CapabilityRole.PowerSustainedLimit, watts, cancellationToken);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> SetBoostLimitAsync(int watts, CancellationToken cancellationToken)
    {
        return SetLimitAsync(CapabilityRole.PowerSlowLimit, watts, cancellationToken);
    }

    /// <summary>Projects sustained and slow/boost capabilities into Steam slider ranges.</summary>
    /// <param name="views">Current descriptor and observation snapshot; no device reads or writes occur here.</param>
    /// <returns>Both range states, using observed watts, then desired watts, then the ceiling for the slider position.</returns>
    internal static SteamPowerLimitState ProjectPowerLimits(IReadOnlyList<DeviceCapabilityView> views)
    {
        return new SteamPowerLimitState(
            ToRange(PowerLimitProjection.Project(views)),
            ToRange(PowerLimitProjection.Project(views, CapabilityRole.PowerSlowLimit)));
    }

    /// <summary>The slider's view of a power limit.</summary>
    /// <param name="limit">The raw projection.</param>
    /// <returns>The range state Steam's slider reads.</returns>
    /// <remarks>
    ///     Only the slider falls back: it shows what was last read or written, else what the profile asks
    ///     for, else the ceiling, so a device that cannot read its limits still gets a slider position.
    ///     The overlay reads the raw projection and shows no figure in that case.
    /// </remarks>
    private static SteamPowerLimitRangeState ToRange(PowerLimitProjection limit)
    {
        return new SteamPowerLimitRangeState(
            limit.Available, limit.MinimumWatts, limit.MaximumWatts, limit.StepWatts,
            limit.ObservedWatts ?? limit.DesiredWatts ?? limit.MaximumWatts, limit.Progress, limit.StatusText);
    }

    private async Task<SteamUiCommandResult> SetLimitAsync(
        CapabilityRole role,
        int watts,
        CancellationToken cancellationToken)
    {
        if (_coordinator is null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new SteamUiCommandResult(false, UnavailableText);
        }

        var limit = PowerLimitProjection.Project(_coordinator.Capabilities.Snapshot(), role);
        if (!limit.Available
            || limit.CapabilityId is not { } capabilityId
            || limit.MinimumWatts is not { } minimum
            || limit.MaximumWatts is not { } maximum
            || limit.StepWatts is not { } step
            || CapabilityProjection.ValidInteger(CapabilityValue.Integer(watts), minimum, maximum, step) is null)
        {
            return new SteamUiCommandResult(false,
                "The requested power limit is unavailable or outside its current descriptor.");
        }

        var result = await _coordinator.ExecuteCapabilityAsync(
            capabilityId,
            limit.InstanceId,
            CapabilityValue.Integer(watts),
            NativeQamUi.CommandTimeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return NativeQamUi.CommandResult(result, OutcomeText(result.Outcome));
    }

    private static string OutcomeText(CommandOutcome outcome)
    {
        return outcome switch
        {
            CommandOutcome.Rejected => "The requested power-limit command was rejected.",
            CommandOutcome.TimedOut => "The requested power-limit command timed out.",
            CommandOutcome.Indeterminate => "The requested power-limit result is indeterminate.",
            _ => "The requested power-limit command did not complete."
        };
    }
}

/// <summary>Projects charge-limit and persistent lighting capabilities into Quick Settings.</summary>
/// <remarks>
///     The projection selects capabilities by their SDK semantic roles, never by a device package's
///     private ids. Commands re-resolve the descriptor at execution time and pass through the same
///     coordinator validation and readback path as the overlay and profiles. Publication follows the
///     coordinator's capability changes, which the Steam UI host hears once for every device row.
/// </remarks>
internal sealed class DeviceCoordinatorNativeQamDeviceControlsService : ISteamDeviceControlsBackend
{
    private readonly DeviceCoordinator? _coordinator;

    /// <summary>Creates a capability projection over the existing device owner.</summary>
    /// <param name="coordinator">Borrowed coordinator, or null when device integration is inactive.</param>
    internal DeviceCoordinatorNativeQamDeviceControlsService(DeviceCoordinator? coordinator)
    {
        _coordinator = coordinator;
    }

    /// <summary>Projects the current capability snapshot without reading hardware; absent roles remain unavailable.</summary>
    public SteamDeviceControlsState Current => Project(
        _coordinator?.Capabilities.Snapshot() ?? []);

    /// <inheritdoc />
    public Task<SteamUiCommandResult> SetChargeLimitAsync(
        int percent,
        CancellationToken cancellationToken)
    {
        return SetIntegerAsync(CapabilityRole.ChargeLimit, percent, cancellationToken);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> SetLightingBrightnessAsync(
        int percent,
        CancellationToken cancellationToken)
    {
        return SetIntegerAsync(CapabilityRole.LightingBrightness, percent, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<SteamUiCommandResult> SetLightingColorAsync(
        string zone,
        int color,
        CancellationToken cancellationToken)
    {
        if (_coordinator is null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new SteamUiCommandResult(false, "Device Integration is not active in this session.");
        }

        var matches = _coordinator.Capabilities.Snapshot()
            .Where(view => view.Descriptor.Role is CapabilityRole.LightingZoneColor
                           && string.Equals(view.Descriptor.InstanceId, zone, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length == 1 && WritableColor(matches[0]))
        {
            return await ExecuteAsync(
                matches[0],
                new CapabilityValue
                {
                    Kind = CapabilityValueKind.Color,
                    ColorValue = color
                },
                cancellationToken).ConfigureAwait(false);
        }

        Log.Warn($"Native QAM lighting color refused: zone='{zone}', matches={matches.Length}.");
        return new SteamUiCommandResult(false, "That lighting zone is unavailable or incompatible.");
    }

    private async Task<SteamUiCommandResult> SetIntegerAsync(
        CapabilityRole role,
        int value,
        CancellationToken cancellationToken)
    {
        if (_coordinator is null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new SteamUiCommandResult(false, "Device Integration is not active in this session.");
        }

        var matches = _coordinator.Capabilities.Snapshot()
            .Where(view => view.Descriptor.Role == role)
            .ToArray();
        if (matches.Length != 1 || !WritableRange(matches[0], role, out _, out _, out _))
        {
            Log.Warn($"Native QAM device range refused: role={role}, matches={matches.Length}.");
            return new SteamUiCommandResult(false, "That device control is unavailable or incompatible.");
        }

        var descriptor = matches[0].Descriptor;
        if (descriptor.Minimum is not { } minimum
            || descriptor.Maximum is not { } maximum
            || descriptor.Step is not { } step
            || CapabilityProjection.ValidInteger(CapabilityValue.Integer(value), minimum, maximum, step) is null)
        {
            return new SteamUiCommandResult(false, "The value is outside the device's current descriptor.");
        }

        return await ExecuteAsync(
            matches[0],
            new CapabilityValue
            {
                Kind = CapabilityValueKind.Integer,
                IntegerValue = value
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<SteamUiCommandResult> ExecuteAsync(
        DeviceCapabilityView view,
        CapabilityValue value,
        CancellationToken cancellationToken)
    {
        var result = await _coordinator!.ExecuteCapabilityAsync(
            view.Descriptor.CapabilityId,
            view.Descriptor.InstanceId,
            value,
            NativeQamUi.CommandTimeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return NativeQamUi.CommandResult(result, $"The device command ended as {result.Outcome}.");
    }

    /// <summary>Projects semantic charge, brightness and lighting-zone capabilities for Quick Settings.</summary>
    /// <param name="views">Current device capability views; ambiguous role/instance matches are not writable.</param>
    /// <returns>Range and per-zone state with desired/observed values and profile overrides; absent range roles are null.</returns>
    internal static SteamDeviceControlsState Project(
        IReadOnlyList<DeviceCapabilityView> views)
    {
        var charge = ProjectUniqueRange(
            views,
            CapabilityRole.ChargeLimit);
        var brightness = ProjectUniqueRange(
            views,
            CapabilityRole.LightingBrightness);
        var zoneGroups = views
            .Where(candidate => candidate.Descriptor.Role is CapabilityRole.LightingZoneColor
                                && !string.IsNullOrWhiteSpace(candidate.Descriptor.InstanceId))
            .GroupBy(
                candidate => candidate.Descriptor.InstanceId!,
                StringComparer.Ordinal);
        List<SteamLightingZoneState> zones =
        [
            .. zoneGroups
                .Where(candidate => candidate.Count() == 1)
                .Select(group =>
                {
                    var view = group.Single();
                    var descriptor = view.Descriptor;
                    var instanceId = group.Key;
                    var compatible = WritableColor(view);
                    return new SteamLightingZoneState(
                        instanceId,
                        descriptor.Display.Key is DisplayKey.Custom
                        && !string.IsNullOrWhiteSpace(descriptor.Display.CustomLabel)
                            ? NativeQamUi.Text(descriptor.Display.CustomLabel)
                            : instanceId,
                        compatible,
                        ValidColor(view.Projection.DesiredValue),
                        // White until something was read or written, as the overlay's editor starts.
                        ValidColor(view.Projection.State.ObservedValue)
                        ?? ValidColor(view.Projection.DesiredValue) ?? 0xFFFFFF,
                        NativeQamUi.ProgressText(view.Projection.Progress),
                        StatusText(view, compatible),
                        CapabilityProjection.DeviceOverrideId(view) is not null);
                })
        ];

        return new SteamDeviceControlsState(charge, brightness, zones);
    }

    private static SteamDeviceRangeState? ProjectUniqueRange(
        IReadOnlyList<DeviceCapabilityView> views,
        CapabilityRole role)
    {
        var matches = views
            .Where(view => view.Descriptor.Role == role)
            .ToArray();
        if (matches.Length == 0)
        {
            return null;
        }

        if (matches.Length != 1
            || !WritableRange(matches[0], role, out var minimum, out var maximum, out var step))
        {
            return new SteamDeviceRangeState(
                false, 0, 100, 1, null, null, string.Empty,
                $"The device published an incompatible or ambiguous {role} control.");
        }

        var view = matches[0];
        var desired = CapabilityProjection.ValidInteger(view.Projection.DesiredValue, minimum, maximum, step);
        return new SteamDeviceRangeState(
            true,
            minimum,
            maximum,
            step,
            desired,
            // A write-only charge limit or brightness still gets its row, starting from the maximum.
            CapabilityProjection.ValidInteger(view.Projection.State.ObservedValue, minimum, maximum, step) ?? desired
            ?? maximum,
            NativeQamUi.ProgressText(view.Projection.Progress),
            StatusText(view, true),
            CapabilityProjection.DeviceOverrideId(view) is not null);
    }

    private static bool WritableRange(
        DeviceCapabilityView view,
        CapabilityRole expectedRole,
        out int minimum,
        out int maximum,
        out int step)
    {
        var descriptor = view.Descriptor;
        minimum = descriptor.Minimum ?? 0;
        maximum = descriptor.Maximum ?? 0;
        step = descriptor.Step ?? 0;
        var state = view.Projection.State;
        return descriptor.Role == expectedRole
               && descriptor is
               {
                   ValueKind: CapabilityValueKind.Integer,
                   Unit: CapabilityUnit.Percent,
                   SupportsWrite: true
               }
               && minimum >= 0
               && maximum <= 100
               && minimum < maximum
               && step >= 1
               && step <= maximum - minimum
               && DeviceCapabilityRouter.CanCommand(state);
    }

    private static bool WritableColor(DeviceCapabilityView view)
    {
        var descriptor = view.Descriptor;
        var state = view.Projection.State;
        return descriptor is
               {
                   Role: CapabilityRole.LightingZoneColor,
                   ValueKind: CapabilityValueKind.Color,
                   SupportsWrite: true
               }
               && DeviceCapabilityRouter.CanCommand(state);
    }

    private static int? ValidColor(CapabilityValue? value)
    {
        return value is
        {
            Kind: CapabilityValueKind.Color,
            ColorValue: >= 0 and <= 0xFFFFFF
        }
            ? value.ColorValue
            : null;
    }

    private static string StatusText(DeviceCapabilityView view, bool available)
    {
        return NativeQamUi.StatusText(
            view,
            available,
            "The device control is not currently available.",
            "The desired value is outside the current descriptor.");
    }
}

/// <summary>
///     Projects WSGM's AutoTDP into Steam's native quick-access menu, beside the limit it moves.
/// </summary>
/// <remarks>
///     AutoTDP is a WSGM setting driving a plugin capability, not a capability of its own, so this reads
///     the coordinator directly rather than looking for a descriptor. One owner: this switch, the
///     overlay's Power and thermals row, and the Settings checkbox all move
///     <c>DeviceIntegration.AutoTdpEnabled</c> through the same method, and none of them holds a copy.
///     A null coordinator projects the constant unavailable state and refuses every write. The setting
///     and the power limit follow the coordinator's own changes, which the Steam UI host hears once for
///     every device row; this raises only AutoTDP's own status changes.
/// </remarks>
internal sealed class DeviceCoordinatorNativeQamAutoTdpService : ISteamAutoTdpBackend, IDisposable
{
    private static readonly SteamAutoTdpState UnavailableState = new(
        false,
        false,
        false,
        null,
        string.Empty,
        "Device Integration is not active in this session.");

    private readonly AutoTdpService? _autoTdp;
    private readonly DeviceCoordinator? _coordinator;
    private bool _disposed;

    /// <summary>Creates the projection over a running coordinator, or the unavailable one.</summary>
    /// <param name="coordinator">The coordinator owning the AutoTDP setting, or null.</param>
    /// <param name="autoTdp">The session's AutoTDP service, or null when it is not running.</param>
    internal DeviceCoordinatorNativeQamAutoTdpService(
        DeviceCoordinator? coordinator,
        AutoTdpService? autoTdp)
    {
        _coordinator = coordinator;
        _autoTdp = autoTdp;
        if (_autoTdp is not null)
        {
            // The setting is not the state: AutoTDP moves between idle, controlling and paused, and
            // its wattage and frametime detail change, with the stored setting and every capability
            // view untouched. Without this the row rendered whatever it last saw.
            _autoTdp.StatusChanged += OnAutoTdpStatusChanged;
        }
    }

    /// <summary>The state Steam should currently be rendering.</summary>
    public SteamAutoTdpState Current => _coordinator is null
        ? UnavailableState
        : Project(
            _coordinator.AutoTdpEnabled,
            _autoTdp?.Status,
            PowerLimitProjection.Project(_coordinator.Capabilities.Snapshot()).Available,
            _autoTdp?.Availability);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_autoTdp is not null)
        {
            _autoTdp.StatusChanged -= OnAutoTdpStatusChanged;
        }
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> SetAutoTdpAsync(bool enabled, CancellationToken cancellationToken)
    {
        return SetEnabledAsync(enabled, cancellationToken);
    }

    /// <summary>Raised when the projected state changes.</summary>
    public event Action? StateChanged;

    /// <summary>Stores the AutoTDP setting through its one owner.</summary>
    /// <param name="enabled">Requested AutoTDP state; disabling remains allowed when a coordinator exists even if control is unavailable.</param>
    /// <param name="cancellationToken">Cancels the coordinator's serialized setting transition.</param>
    /// <returns>Whether the setting was accepted, or the authoritative availability refusal.</returns>
    /// <exception cref="ObjectDisposedException">This adapter has been disposed.</exception>
    public async Task<SteamUiCommandResult> SetEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var state = Current;
        // Switching off is always allowed; only switching on needs something to control.
        if (_coordinator is null || (enabled && !state.Available))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new SteamUiCommandResult(false, state.StatusText);
        }

        // Idempotent rather than an error: the page and the store can disagree for one frame after
        // a change made somewhere else, and re-sending the value it already has is the harmless way
        // that resolves. The coordinator compares and sets under its own transition gate, so the
        // requested value is what lands even when another surface changed it in between — a toggle
        // decided from the snapshot above would invert that newer value instead.
        await _coordinator.SetAutoTdpEnabledAsync(enabled, cancellationToken).ConfigureAwait(false);
        return new SteamUiCommandResult(true, null);
    }

    /// <summary>Projects the stored setting and live status into the menu's vocabulary.</summary>
    /// <param name="enabled">The stored setting.</param>
    /// <param name="status">The running service's state, or null when it is not running.</param>
    /// <param name="powerLimitAvailable">Whether a primary power limit exists to drive.</param>
    /// <param name="availability">The service's authoritative admission state.</param>
    /// <returns>The state the menu renders.</returns>
    internal static SteamAutoTdpState Project(
        bool enabled,
        AutoTdpStatus? status,
        bool powerLimitAvailable,
        AutoTdpAvailability? availability = null)
    {
        if (availability is { Available: false })
        {
            return new SteamAutoTdpState(false, false, false, null, "failed", NativeQamUi.Text(availability.Detail));
        }

        // Without a power limit there is nothing to control, so the switch is not offered rather
        // than offered and then silently ineffective.
        if (!powerLimitAvailable)
        {
            return new SteamAutoTdpState(
                false,
                enabled,
                false,
                null,
                string.Empty,
                NativeQamUi.Text("No primary power limit is available to control."));
        }

        if (status is null)
        {
            return new SteamAutoTdpState(
                true,
                enabled,
                false,
                null,
                enabled ? "applying" : string.Empty,
                NativeQamUi.Text(enabled ? "Starting." : string.Empty));
        }

        var controlling = status.State is AutoTdpState.Controlling;
        return new SteamAutoTdpState(
            // A write that did not apply leaves the switch operable; whether AutoTDP can run at all is
            // the availability check above.
            true,
            enabled,
            controlling,
            status.Watts,
            status.State switch
            {
                AutoTdpState.Controlling => "completed",
                AutoTdpState.Unavailable => "failed",
                _ => string.Empty
            },
            NativeQamUi.Text(AutoTdpReason.Describe(status.Detail)));
    }

    // Raised from AutoTDP's own tick loop. Queueing a publication is thread-safe, so no UI-thread hop.
    private void OnAutoTdpStatusChanged(AutoTdpStatus status)
    {
        StateChanged?.Invoke();
    }
}

/// <summary>
///     Projects WSGM's own controller management into Steam's native quick-access menu.
/// </summary>
/// <remarks>
///     The controller target is WSGM's setting, not a plugin capability, so this reads
///     <see cref="ControllerManager" /> through the coordinator instead of looking for a capability
///     descriptor. That keeps one owner: the QAM control and the overlay's controller page move the same
///     stored default through the same method, and neither holds a copy of the target. A null
///     coordinator projects the constant unavailable state and refuses every write.
/// </remarks>
internal sealed class DeviceCoordinatorNativeQamControllerTargetService :
    ISteamControllerTargetBackend,
    IDisposable
{
    /// <summary>
    ///     Why the row is inert, stated as the one cause that can still produce it: the
    ///     session is running without device integration. Every build ships the component.
    /// </summary>
    internal const string UnavailableDetail =
        "Controller management is unavailable: this session is running without device integration.";

    /// <summary>What the row adds to its status while a running game holds the target it launched with.</summary>
    internal const string RestartToRebind = "Restart the application to rebind.";

    private static readonly SteamControllerTargetState UnavailableState = new(
        false,
        [],
        string.Empty,
        string.Empty,
        string.Empty,
        UnavailableDetail);

    private readonly DeviceCoordinator? _coordinator;
    private bool _disposed;

    /// <summary>Creates the projection over a running coordinator, or the unavailable one.</summary>
    /// <param name="coordinator">The coordinator owning controller management, or null.</param>
    internal DeviceCoordinatorNativeQamControllerTargetService(DeviceCoordinator? coordinator)
    {
        _coordinator = coordinator;
        if (_coordinator is not null)
        {
            _coordinator.Controllers.StatusChanged += OnControllerStatusChanged;
        }
    }

    /// <summary>The state Steam should currently be rendering.</summary>
    public SteamControllerTargetState Current => _coordinator is null
        ? UnavailableState
        : Project(
            _coordinator.ControllerManagementEnabled,
            _coordinator.Controllers.Snapshot(),
            _coordinator.InstalledPackage is not null,
            _coordinator.Controllers.SupportedTargets,
            _coordinator.ChosenControllerTarget());

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_coordinator is not null)
        {
            _coordinator.Controllers.StatusChanged -= OnControllerStatusChanged;
        }
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> SetControllerTargetAsync(
        string target,
        CancellationToken cancellationToken)
    {
        return SetTargetAsync(target, cancellationToken);
    }

    /// <summary>Raised when the projected state changes.</summary>
    public event Action? StateChanged;

    /// <summary>Stores and applies the chosen managed-controller target.</summary>
    private async Task<SteamUiCommandResult> SetTargetAsync(
        string target,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var state = Current;
        if (_coordinator is null || !state.Available)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new SteamUiCommandResult(false, state.StatusText);
        }

        if (!TryParseTarget(target, out var parsed))
        {
            return new SteamUiCommandResult(false, $"'{target}' is not a controller target.");
        }

        var status = await _coordinator
            .SetControllerTargetAsync(parsed, cancellationToken)
            .ConfigureAwait(false);

        // Truthful rather than optimistic: the setting is stored either way, but a manager that
        // could not bring the new target up is not a success the menu should show as one.
        var succeeded = status.State is not
            (ControllerManagementState.Faulted or ControllerManagementState.Unavailable);
        return new SteamUiCommandResult(succeeded, succeeded ? null : status.Detail);
    }

    /// <summary>Projects controller state into the menu's closed vocabulary.</summary>
    /// <param name="enabled">Whether controller management may run at all.</param>
    /// <param name="status">The manager's current truthful state.</param>
    /// <param name="packageInstalled">Whether a device package is installed.</param>
    /// <param name="supportedTargets">Targets the backend on this machine can create.</param>
    /// <param name="chosen">The stored choice, shown as selected while no target is live.</param>
    /// <returns>The state the menu renders.</returns>
    internal static SteamControllerTargetState Project(
        bool enabled,
        ControllerManagerStatus status,
        bool packageInstalled,
        IReadOnlyList<ManagedControllerTarget> supportedTargets,
        ManagedControllerTarget? chosen = null)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(supportedTargets);
        if (!enabled)
        {
            return new SteamControllerTargetState(
                false,
                [],
                string.Empty,
                string.Empty,
                string.Empty,
                NativeQamUi.Text(status.Detail));
        }

        // Only what the backend can actually build. These are WSGM's own virtual devices rather
        // than hardware, but a target still needs an encoder behind it: offering one that has none
        // meant the selection persisted, target creation was refused, and controller management
        // reported itself unavailable until the user found their way back to the setting.
        SteamControllerTargetOption[] targets =
        [
            .. new[]
                {
                    (Target: ManagedControllerTarget.SteamDeckComposite, Label: "Steam Deck"),
                    (Target: ManagedControllerTarget.Xbox360, Label: "Xbox 360"),
                    (Target: ManagedControllerTarget.DualShock4, Label: "DualShock 4")
                }
                .Where(option => supportedTargets.Contains(option.Target))
                .Select(option => new SteamControllerTargetOption(
                    option.Target.ToString(),
                    option.Label,
                    true))
        ];

        var available = status.State is
            ControllerManagementState.Idle or ControllerManagementState.Active;
        var selected = (status.Target ?? chosen) is { } target ? target.ToString() : string.Empty;

        // Observed is what a target actually exists for right now, which is only true while Active.
        // Reporting the selection back as if it were observed would hide a target that was chosen
        // but never came up.
        var observed = status.State is ControllerManagementState.Active ? selected : string.Empty;
        var detail = status.Detail;
        if (available && string.IsNullOrWhiteSpace(detail) && !packageInstalled)
        {
            detail = "No device package is installed, so no physical controller is being captured.";
        }

        // A running game holds the target it was launched with, so a change reaches it only on the
        // next launch. Saying so is the difference between a control that looks broken and one the
        // user understands.
        var statusText = NativeQamUi.Text(detail);
        if (status.ApplicationId is not null)
        {
            statusText = statusText.Length > 0 ? $"{statusText} {RestartToRebind}" : RestartToRebind;
        }

        return new SteamControllerTargetState(
            available,
            targets,
            selected,
            observed,
            ProgressFor(status.State),
            statusText,
            status.TargetSource is ProfileSource.Game);
    }

    /// <summary>Maps a stored target name back onto the enumeration.</summary>
    /// <param name="target">The name the menu sent.</param>
    /// <param name="parsed">Receives the parsed target.</param>
    /// <returns>Whether the name named a target.</returns>
    /// <remarks>
    ///     Ordinal and case-sensitive on purpose: the menu is sent these names from
    ///     <see cref="Project" />, so anything else is a caller defect rather than user input to be
    ///     forgiving about.
    /// </remarks>
    internal static bool TryParseTarget(string target, out ManagedControllerTarget parsed)
    {
        return Enum.TryParse(target, false, out parsed)
               && Enum.IsDefined(parsed);
    }

    private static string ProgressFor(ControllerManagementState state)
    {
        return state switch
        {
            ControllerManagementState.Active => "completed",
            ControllerManagementState.Idle => string.Empty,
            ControllerManagementState.Faulted => "failed",
            _ => string.Empty
        };
    }

    private void OnControllerStatusChanged(ControllerManagerStatus status)
    {
        StateChanged?.Invoke();
    }
}
