using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

internal sealed record NativeQamCommandResult(bool Succeeded, string? Error);

/// <summary>The display-resolution row's state, for the native quick-access menu.</summary>
/// <param name="Available">Whether the row can be drawn at all.</param>
/// <param name="Options">
/// Resolutions to offer, as <c>WIDTHxHEIGHT</c>. Empty hides the row: a picker with nothing to pick
/// is worse than no picker.
/// </param>
/// <param name="Current">The resolution in force, or empty when it cannot be read.</param>
/// <param name="StatusText">Why the row is unavailable, when it is.</param>
/// <remarks>
/// Hand-built rather than reactivated, unlike the frame limit and VRR rows: SteamOS drives
/// resolution through gamescope and this client ships no component for it, so there is nothing to
/// mount and the row is WSGM's own.
/// </remarks>
internal sealed record NativeQamResolutionState(
    bool Available,
    IReadOnlyList<string> Options,
    string Current,
    string StatusText);

/// <summary>One connected access point fed through Steam's own network-store ingestion path.</summary>
internal sealed record SteamNetworkAccessPointState(
    string Ssid,
    int Strength,
    bool Secured,
    bool Connected);

/// <summary>The connected network-store projection that drives Steam's header indicator.</summary>
internal sealed record SteamNetworkState(IReadOnlyList<SteamNetworkAccessPointState> Networks);

/// <summary>The unified frame-limit row, shaped like SteamOS's own.</summary>
/// <remarks>
/// One continuous slider bookended by the panel's limits, plus a separate switch for off — verified
/// against a Steam Deck showing "60 FPS (60 Hz)" between bookends 10 and 60. There are no notches
/// under any strategy: the cap is a free number and the PAIRING is what snaps to a mode the panel
/// can hold, which is exactly the merge Valve made when it unified the two rows.
/// </remarks>
internal sealed record NativeQamFrameLimitState(
    bool Available,
    int? MinimumFps,
    int? MaximumFps,
    int? DesiredFps,
    int? ObservedFps,
    bool SupportsReadback,
    string ReadbackQuality,
    string PolicyLayer,
    bool ApplicationTargetAvailable,
    string TargetProfile,
    string AdapterAvailability,
    string Progress,
    string Fault,
    string StatusText,
    bool LimitEnabled = false,
    IReadOnlyDictionary<int, int>? RefreshForCap = null,
    int? RefreshMinHz = null,
    int? RefreshMaxHz = null,
    int? CurrentRefreshHz = null,
    IReadOnlyList<int>? RefreshRates = null);

internal sealed record NativeQamTdpState(
    bool Available,
    int? MinimumWatts,
    int? MaximumWatts,
    int? StepWatts,
    int? DesiredWatts,
    int? ObservedWatts,
    string Progress,
    string StatusText);

/// <summary>The variable-refresh switch as WSGM's own row renders it.</summary>
/// <param name="Available">Whether a device capability backs the switch at all.</param>
/// <param name="Enabled">What the device reports now, not what was last asked for.</param>
/// <param name="Progress">Command progress in the shared vocabulary.</param>
/// <param name="StatusText">One line describing the state, or why the row cannot be operated.</param>
internal sealed record NativeQamVrrState(
    bool Available,
    bool Enabled,
    string Progress,
    string StatusText);

/// <summary>AutoTDP as Steam's own menu renders it.</summary>
/// <remarks>
/// Deliberately more than a boolean. A switch that only says "on" leaves a user watching the power
/// limit move with no way to tell control from a fault, so the state carries what AutoTDP is
/// actually doing: the watts it settled on, whether it is controlling, waiting, paused or unable to
/// run, and why.
/// </remarks>
/// <param name="Available">Whether the switch may be operated at all.</param>
/// <param name="Enabled">The stored setting, which is what the switch shows.</param>
/// <param name="Controlling">Whether AutoTDP is currently moving the power limit.</param>
/// <param name="Watts">The limit AutoTDP settled on, when it has one.</param>
/// <param name="Progress">Command progress in the shared vocabulary.</param>
/// <param name="StatusText">One line describing what it is doing, or why it cannot.</param>

internal sealed record NativeQamAutoTdpState(
    bool Available,
    bool Enabled,
    bool Controlling,
    int? Watts,
    string Progress,
    string StatusText);

internal sealed record NativeQamControllerTargetOption(
    string Id,
    string Label,
    bool Available);

internal sealed record NativeQamControllerTargetState(
    bool Available,
    IReadOnlyList<NativeQamControllerTargetOption> Targets,
    string SelectedTarget,
    string ObservedTarget,
    string Progress,
    string StatusText,
    bool ApplicationRestartRequired);

/// <summary>Shared shaping for the text these projections hand to Steam's page.</summary>
internal static class NativeQamText
{
    /// <summary>Longest status text a projection sends.</summary>
    /// <remarks>
    /// Plugin and driver messages have no useful display length guarantee. The page has one line,
    /// so longer text is truncated before delivery.
    /// </remarks>
    private const int MaximumLength = 240;

    /// <summary>Normalizes an optional detail into bounded, renderable text.</summary>
    /// <param name="value">The detail, which may be null, blank, or arbitrarily long.</param>
    /// <returns>The empty string for nothing to say, otherwise the text within the bound.</returns>
    internal static string Bound(string? value) => string.IsNullOrWhiteSpace(value)
        ? string.Empty
        : value.Length <= MaximumLength ? value : value[..MaximumLength];
}

internal interface INativeQamTdpService : IDisposable
{
    event Action? StateChanged;

    NativeQamTdpState Current { get; }

    Task<NativeQamCommandResult> SetPrimaryLimitAsync(
        int watts,
        CancellationToken cancellationToken);
}

internal interface INativeQamAutoTdpService : IDisposable
{
    event Action? StateChanged;

    NativeQamAutoTdpState Current { get; }

    Task<NativeQamCommandResult> SetEnabledAsync(bool enabled, CancellationToken cancellationToken);
}

internal interface INativeQamControllerTargetService : IDisposable
{
    event Action? StateChanged;

    NativeQamControllerTargetState Current { get; }

    Task<NativeQamCommandResult> SetTargetAsync(
        string target,
        CancellationToken cancellationToken);
}

internal sealed class PerformanceServiceNativeQamAdapter : IDisposable
{
    private readonly PerformanceService _service;
    private bool _disposed;

    internal PerformanceServiceNativeQamAdapter(PerformanceService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _service.StateChanged += OnStateChanged;
    }

    internal event Action? StateChanged;

    internal NativeQamFrameLimitState FrameLimit => ProjectFrameLimit(
        _service.Current,
        _service.Enabled,
        PerfSupport?.Invoke());

    /// <summary>The variable-refresh switch, straight from the device capability.</summary>
    /// <remarks>
    /// Availability follows the plugin's published capability and nothing else: a machine whose
    /// device publishes no VRR capability has no switch, rather than one that refuses every press.
    /// </remarks>
    internal NativeQamVrrState Vrr
    {
        get
        {
            NativeQamPerfSupport? support = PerfSupport?.Invoke();
            bool available = support?.VariableRefreshRateSupported == true
                && ApplyVariableRefreshRate is not null;
            bool enabled = support?.VariableRefreshRateEnabled == true;
            return new NativeQamVrrState(
                available,
                enabled,
                "idle",
                available
                    ? enabled
                        ? "The panel follows the frame rate."
                        : "The panel holds a fixed refresh rate."
                    : "This device publishes no variable-refresh capability.");
        }
    }

    /// <summary>
    /// Supplies what the device can currently back, for the reactivated performance panel.
    /// </summary>
    /// <remarks>
    /// Injected rather than read here because the frame-limit options come from display-mode
    /// discovery and the VRR flag from the device plugin, neither of which this adapter owns. The
    /// default reports nothing supported, which hides every control rather than showing one that
    /// writes nowhere.
    /// </remarks>
    internal Func<NativeQamPerfSupport>? PerfSupport { get; set; }

    /// <summary>Applies a manually chosen refresh rate, when the session allows one.</summary>
    /// <remarks>
    /// Set only where a manual refresh rate is meaningful. Under the pairing strategies the frame
    /// cap owns the refresh rate, so this stays unset and a write is refused by name rather than
    /// fighting the pairing on the user's behalf — the row is hidden there anyway, because the
    /// projection omits its limits.
    /// </remarks>
    internal Func<int, bool>? ApplyRefreshRate { get; set; }

    /// <summary>Turns variable refresh on or off through the device capability.</summary>
    /// <param name="enabled">The wanted state.</param>
    /// <param name="cancellationToken">Cancels the device write.</param>
    /// <returns>Whether the device took it.</returns>
    /// <remarks>
    /// Awaited rather than fired and forgotten, like every other controlled switch here: reporting
    /// success before the device answered would show the switch move and then snap it back on the
    /// next publish.
    /// </remarks>
    internal Task<NativeQamCommandResult> ApplyVariableRefreshRateAsync(
        bool enabled,
        CancellationToken cancellationToken)
    {
        if (ApplyVariableRefreshRate is not { } apply)
        {
            const string reason = "This device publishes no variable-refresh capability.";
            Log.Warn($"Native QAM variable refresh {(enabled ? "on" : "off")} refused: {reason}");
            return Task.FromResult(new NativeQamCommandResult(false, reason));
        }

        return ApplyFlagAsync(apply, enabled, "variable refresh rate", cancellationToken);
    }

    /// <summary>Applies a refresh rate the user chose directly.</summary>
    /// <param name="hz">The rate to apply.</param>
    /// <param name="cancellationToken">Unused; the display call is synchronous.</param>
    /// <returns>Whether the display took it.</returns>
    /// <remarks>
    /// The unified row's other mode. With the frame limit off there is no cap to pair a rate to, so
    /// the slider becomes the refresh rate itself and writes here — which is why this is available
    /// under every strategy, unlike the manual-refresh row, whose whole problem was fighting a
    /// pairing that was still active.
    /// </remarks>
    internal Task<NativeQamCommandResult> ApplyRefreshRateAsync(
        int hz,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        if (ApplyRefreshRate is not { } apply)
        {
            const string reason = "This session cannot change the refresh rate.";
            Log.Warn($"Native QAM refresh rate {hz} Hz refused: {reason}");
            return Task.FromResult(new NativeQamCommandResult(false, reason));
        }

        return Task.FromResult(apply(hz)
            ? new NativeQamCommandResult(true, null)
            : new NativeQamCommandResult(false, $"The display refused {hz} Hz."));
    }

    /// <summary>The cap the enable toggle applies when no cap is set yet.</summary>
    /// <remarks>
    /// The last cap the service still holds when there is one, else the lowest offered notch —
    /// which is also the value the projection shows on the disabled slider, so the cap that takes
    /// effect is the number the user was already looking at.
    /// </remarks>
    private int EnableFrameLimitWatts()
    {
        int desired = _service.Current.Desired.FrameLimit ?? 0;
        if (desired > 0)
        {
            return desired;
        }

        int lowest = 0;
        foreach (int option in PerfSupport?.Invoke().FrameLimitOptions ?? [])
        {
            if (option > 0 && (lowest == 0 || option < lowest))
            {
                lowest = option;
            }
        }

        return lowest;
    }

    /// <summary>Turns variable refresh rate on or off, when a device publishes it.</summary>
    /// <remarks>
    /// Unset on a machine whose plugin publishes no VRR capability, which is also when the
    /// projection omits <c>is_vrr_supported</c> and Valve's own row does not render. Both follow the
    /// same fact, from the same source, so the row cannot appear without a way to act on it.
    /// </remarks>
    internal Func<bool, CancellationToken, Task<bool>>? ApplyVariableRefreshRate { get; set; }

    /// <summary>The state Steam's own performance panel reads every control's value out of.</summary>
    internal NativeQamPerfState PerfState
    {
        get
        {
            PerformanceState current = _service.Current;
            NativeQamPerfSupport support = PerfSupport?.Invoke()
                ?? new NativeQamPerfSupport([], false, false, null, null);

            // Steam's per-game header needs an AppID, and only a Steam-launched title has one. A
            // foreground-only identity still carries its profile; the projection presents it as the
            // global one rather than naming a game WSGM cannot name.
            uint? appId = current.Target is not null
                && current.Target.ApplicationId.StartsWith("steam:", StringComparison.Ordinal)
                && uint.TryParse(current.Target.ApplicationId[6..], out uint parsed)
                    ? parsed
                    : null;

            return NativeQamPerfProjection.Project(
                current.Desired,
                support,
                appId,
                perApplicationProfileEnabled: current.Target is not null,
                advancedSettingsEnabled: true,
                variableRefreshRateEnabled: support.VariableRefreshRateEnabled,
                // Was hardcoded null, which advertised the manual refresh row in `limits` while
                // giving it no value in `settings` — half of what crashed the Performance tab.
                refreshRateHz: support.CurrentRefreshRateHz);
        }
    }

    /// <summary>Applies one change from Steam's own performance panel.</summary>
    /// <param name="change">The decoded change.</param>
    /// <param name="correlationId">Correlates the command across the log.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns>Whether the change was applied.</returns>
    /// <remarks>
    /// Only the settings behind a control WSGM mounts and can honour. Anything else is refused with
    /// its name, never accepted-and-dropped: a control that appears to work and does nothing is
    /// worse than one that never rendered.
    /// </remarks>
    internal Task<NativeQamCommandResult> ApplyPerfChangeAsync(
        NativeQamPerfChange change,
        string correlationId,
        CancellationToken cancellationToken) => change.Kind switch
        {
            NativeQamPerfSetting.FrameLimit => SetAsync(
                PerformanceControl.FrameLimit,
                change.Value,
                PerformancePersistenceTarget.Automatic,
                correlationId,
                cancellationToken),

            // Steam models the cap and its switch separately; RTSS has one value where zero is off.
            // Disabling writes zero. Enabling must WRITE A CAP: Valve's toggle sends only the flag,
            // and treating it as a no-op left the slider grey with a switch that snapped straight
            // back — there is no "enabled with no value" state on the RTSS side for it to mean.
            NativeQamPerfSetting.FrameLimitEnabled when !change.AsFlag => SetAsync(
                PerformanceControl.FrameLimit,
                0,
                PerformancePersistenceTarget.Automatic,
                correlationId,
                cancellationToken),
            NativeQamPerfSetting.FrameLimitEnabled => SetAsync(
                PerformanceControl.FrameLimit,
                EnableFrameLimitWatts(),
                PerformancePersistenceTarget.Automatic,
                correlationId,
                cancellationToken),

            NativeQamPerfSetting.OverlayLevel => SetAsync(
                PerformanceControl.OverlayLevel,
                change.Value,
                PerformancePersistenceTarget.Automatic,
                correlationId,
                cancellationToken),

            // Straight to the service that owns the policy: creating or removing the application
            // layer is policy, not a value write, and routing it through SetAsync would need a
            // control that does not exist.
            NativeQamPerfSetting.PerApplicationProfileEnabled => ApplyProfileToggleAsync(
                change.AsFlag,
                cancellationToken),

            NativeQamPerfSetting.VariableRefreshRate when
                ApplyVariableRefreshRate is { } applyVrr =>
                ApplyFlagAsync(applyVrr, change.AsFlag, "variable refresh rate", cancellationToken),

            NativeQamPerfSetting.RefreshRateHz when ApplyRefreshRate is { } applyRefresh =>
                Task.FromResult(applyRefresh(change.Value)
                    ? new NativeQamCommandResult(true, null)
                    : new NativeQamCommandResult(
                        false,
                        $"The display refused {change.Value} Hz.")),

            _ => Task.FromResult(
                new NativeQamCommandResult(
                    false,
                    $"The performance setting {change.Kind} has no WSGM backend yet.")),
        };

    internal IDisposable AcquireObservation()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _service.AcquireObservation();
    }

    internal async Task<NativeQamCommandResult> SetAsync(
        PerformanceControl control,
        int value,
        PerformancePersistenceTarget persistence,
        string correlationId,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        PerformanceCommandState result = await _service.SetAsync(
            control,
            value,
            persistence,
            "native-qam",
            correlationId,
            cancellationToken).ConfigureAwait(false);
        bool succeeded = result.Phase is
            PerformanceCommandPhase.SucceededVerified
            or PerformanceCommandPhase.AppliedUnverified;
        return new NativeQamCommandResult(
            succeeded,
            succeeded ? null : Bound(result.Diagnostic ?? PhaseFailure(result.Phase)));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _service.StateChanged -= OnStateChanged;
    }

    /// <remarks>
    /// A refusal is reported rather than swallowed. The toggle is controlled, so an unreported
    /// failure shows it moved and then snaps it back on the next publish with no explanation — and
    /// "no application is running" is exactly the case a user hits by opening the menu on the
    /// desktop.
    /// </remarks>
    /// <summary>Resets the profile in force to its defaults.</summary>
    /// <param name="cancellationToken">Cancels the reset.</param>
    /// <returns>Whether anything changed.</returns>
    /// <remarks>
    /// A reset that changes nothing because the profile is already at defaults is reported as a
    /// success, unlike the toggle: the user asked for a state and that state is what they have.
    /// </remarks>
    internal async Task<NativeQamCommandResult> ResetProfileAsync(
        CancellationToken cancellationToken)
    {
        await _service.ResetProfileAsync(cancellationToken).ConfigureAwait(false);
        return new NativeQamCommandResult(true, null);
    }

    private async Task<NativeQamCommandResult> ApplyProfileToggleAsync(
        bool enabled,
        CancellationToken cancellationToken)
    {
        bool changed = await _service.SetApplicationProfileEnabledAsync(enabled, cancellationToken)
            .ConfigureAwait(false);
        return changed
            ? new NativeQamCommandResult(true, null)
            : new NativeQamCommandResult(
                false,
                "The per-application profile could not be changed; no identifiable application is "
                + "running.");
    }

    /// <remarks>
    /// The device write is awaited rather than fired and forgotten: Steam's toggle is controlled, so
    /// reporting success before the device answered would show it moved and then snap it back on the
    /// next publish.
    /// </remarks>
    private static async Task<NativeQamCommandResult> ApplyFlagAsync(
        Func<bool, CancellationToken, Task<bool>> apply,
        bool enabled,
        string what,
        CancellationToken cancellationToken)
    {
        bool applied = await apply(enabled, cancellationToken).ConfigureAwait(false);
        return applied
            ? new NativeQamCommandResult(true, null)
            : new NativeQamCommandResult(
                false,
                $"The device refused to turn {what} {(enabled ? "on" : "off")}.");
    }

    /// <param name="state">The performance service's current state.</param>
    /// <param name="enabled">Whether RTSS control is switched on at all.</param>
    /// <param name="support">
    /// What the panel can hold. Its option list bookends the slider, because RTSS's own range is
    /// 0-1000 and a slider spanning that is not a control anyone can aim — the display decides
    /// what a cap can usefully be, not the limiter.
    /// </param>
    /// <returns>The row's state.</returns>
    internal static NativeQamFrameLimitState ProjectFrameLimit(
        PerformanceState state,
        bool enabled,
        NativeQamPerfSupport? support = null)
    {
        RtssCapabilities? capabilities = state.Probe.Capabilities;
        bool supported = capabilities?.Supports(PerformanceControl.FrameLimit) == true;
        bool available = enabled
            && state.Probe.Availability == RtssAvailability.Ready
            && supported;

        // Zero is "off" and is never a slider position, so it is filtered out of both bookends.
        IReadOnlyList<int> caps = support?.FrameLimitOptions ?? [];
        int panelMinimum = 0;
        int panelMaximum = 0;
        foreach (int cap in caps)
        {
            if (cap <= 0)
            {
                continue;
            }

            if (panelMinimum == 0 || cap < panelMinimum)
            {
                panelMinimum = cap;
            }

            if (cap > panelMaximum)
            {
                panelMaximum = cap;
            }
        }

        int? minimum = panelMinimum > 0 ? panelMinimum : supported ? capabilities!.MinimumFrameLimit : null;
        int? maximum = panelMaximum > 0 ? panelMaximum : supported ? capabilities!.MaximumFrameLimit : null;
        return new NativeQamFrameLimitState(
            available,
            minimum,
            maximum,
            ValidValue(state.Desired.FrameLimit, capabilities, PerformanceControl.FrameLimit),
            ValidValue(state.Observed.FrameLimit, capabilities, PerformanceControl.FrameLimit),
            supported && capabilities!.FrameLimitReadback,
            ReadbackText(state.FrameLimitQuality),
            LayerText(state.FrameLimitLayer),
            state.Target is not null,
            Bound(state.Target?.RtssProfileName),
            AvailabilityText(state.Probe.Availability),
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
            support?.RefreshRates);
    }

    private void OnStateChanged(PerformanceState state) => StateChanged?.Invoke();

    private static int? ValidValue(
        int? value,
        RtssCapabilities? capabilities,
        PerformanceControl control) => value is int integer
        && capabilities?.IsValid(control, integer) == true
            ? integer
            : null;

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
            PerformanceCommandPhase.SucceededVerified => "succeeded-verified",
            PerformanceCommandPhase.AppliedUnverified => "applied-unverified",
            PerformanceCommandPhase.Rejected => "rejected",
            PerformanceCommandPhase.TimedOut => "timed-out",
            PerformanceCommandPhase.Indeterminate => "indeterminate",
            PerformanceCommandPhase.Failed => "failed",
            PerformanceCommandPhase.ExternalChange => "external-change",
            _ => "idle",
        };
    }

    private static string FaultText(
        PerformanceCommandState command,
        PerformanceControl control) => command.Control == control
        && command.Phase is PerformanceCommandPhase.Rejected
            or PerformanceCommandPhase.TimedOut
            or PerformanceCommandPhase.Indeterminate
            or PerformanceCommandPhase.Failed
                ? Bound(command.Diagnostic ?? PhaseFailure(command.Phase))
                : string.Empty;

    private static string StatusText(
        PerformanceState state,
        PerformanceControl control,
        bool available)
    {
        string fault = FaultText(state.Command, control);
        if (!string.IsNullOrEmpty(fault))
        {
            return fault;
        }

        if (!available)
        {
            return Bound(state.Probe.Diagnostic ?? (state.Probe.Availability switch
            {
                RtssAvailability.NotInstalled => "RTSS is not installed.",
                RtssAvailability.NotRunning => "RTSS is not running.",
                RtssAvailability.Incompatible => "The installed RTSS version is incompatible.",
                RtssAvailability.AdapterUnavailable => "The RTSS profile adapter is unavailable.",
                _ => "RTSS performance control is not currently available.",
            }));
        }

        return state.Target is null
            ? "RTSS global profile"
            : Bound($"RTSS application profile: {state.Target.RtssProfileName}");
    }

    private static string PhaseFailure(PerformanceCommandPhase phase) => phase switch
    {
        PerformanceCommandPhase.Rejected => "The RTSS command was rejected.",
        PerformanceCommandPhase.TimedOut => "The RTSS command timed out.",
        PerformanceCommandPhase.Indeterminate => "The RTSS command result is indeterminate.",
        PerformanceCommandPhase.Failed => "The RTSS command failed.",
        _ => "The RTSS command did not complete.",
    };

    private static string ReadbackText(PerformanceReadbackQuality quality) => quality switch
    {
        PerformanceReadbackQuality.Verified => "verified",
        PerformanceReadbackQuality.AppliedUnverified => "applied-unverified",
        PerformanceReadbackQuality.Stale => "stale",
        _ => "unavailable",
    };

    private static string LayerText(PerformancePolicyLayer layer) => layer switch
    {
        PerformancePolicyLayer.Global => "global",
        PerformancePolicyLayer.Application => "application",
        _ => "none",
    };

    private static string AvailabilityText(RtssAvailability availability) => availability switch
    {
        RtssAvailability.NotInstalled => "not-installed",
        RtssAvailability.NotRunning => "not-running",
        RtssAvailability.Incompatible => "incompatible",
        RtssAvailability.AdapterUnavailable => "adapter-unavailable",
        RtssAvailability.Ready => "ready",
        RtssAvailability.Degraded => "degraded",
        _ => "unknown",
    };

    private static string Bound(string? value) => NativeQamText.Bound(value);
}

internal sealed class DeviceCoordinatorNativeQamTdpService : INativeQamTdpService
{
    private const string CapabilityId = "power.primary-limit";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);
    private readonly DeviceCoordinator _coordinator;
    private bool _disposed;

    internal DeviceCoordinatorNativeQamTdpService(DeviceCoordinator coordinator)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _coordinator.CapabilityViewsChanged += OnCapabilityViewsChanged;
    }

    public event Action? StateChanged;

    public NativeQamTdpState Current => Project(_coordinator.CapabilitySnapshot()).State;

    public async Task<NativeQamCommandResult> SetPrimaryLimitAsync(
        int watts,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        TdpProjection projection = Project(_coordinator.CapabilitySnapshot());
        if (!projection.State.Available
            || projection.State.MinimumWatts is not int minimum
            || projection.State.MaximumWatts is not int maximum
            || projection.State.StepWatts is not int step
            || watts < minimum
            || watts > maximum
            || (watts - minimum) % step != 0)
        {
            return new NativeQamCommandResult(false,
                "The primary power limit is unavailable or outside its current descriptor.");
        }

        CapabilityCommandResult result = await _coordinator.ExecuteCapabilityAsync(
            CapabilityId,
            projection.InstanceId,
            new CapabilityValue
            {
                Kind = CapabilityValueKind.Integer,
                IntegerValue = watts,
            },
            CommandTimeout,
            // A person moved the TDP control in the Steam menu, so AutoTDP steps aside for it.
            CapabilityCommandOrigin.User,
            cancellationToken).ConfigureAwait(false);
        bool succeeded = result.Outcome is
            CommandOutcome.AppliedVerified or CommandOutcome.AppliedUnverified;
        return new NativeQamCommandResult(
            succeeded,
            succeeded ? null : result.Reason?.Detail ?? OutcomeText(result.Outcome));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _coordinator.CapabilityViewsChanged -= OnCapabilityViewsChanged;
    }

    internal static TdpProjection Project(IReadOnlyList<DeviceCapabilityView> views)
    {
        DeviceCapabilityView[] matches = views
            .Where(view => string.Equals(
                view.Descriptor.CapabilityId,
                CapabilityId,
                StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
        {
            string detail = matches.Length == 0
                ? "The active device does not publish a primary power limit."
                : "The active device published an ambiguous primary power limit.";
            return new TdpProjection(Unavailable(detail), null);
        }

        DeviceCapabilityView view = matches[0];
        CapabilityDescriptor descriptor = view.Descriptor;
        CapabilityProjection projection = view.Projection;
        CapabilityState state = projection.State;
        if (descriptor.Role is not CapabilityRole.PowerSustainedLimit
            || descriptor.ValueKind is not CapabilityValueKind.Integer
            || descriptor.Unit is not CapabilityUnit.Watt
            || !descriptor.SupportsRead
            || !descriptor.SupportsWrite
            || descriptor.Minimum is not int minimum
            || descriptor.Maximum is not int maximum
            || descriptor.Step is not int step
            || minimum < 1
            || maximum > 200
            || minimum >= maximum
            || step < 1
            || step > maximum - minimum)
        {
            return new TdpProjection(
                Unavailable("The primary power-limit descriptor is incompatible."),
                descriptor.InstanceId);
        }

        int? desired = ValidInteger(projection.DesiredValue, minimum, maximum, step);
        int? observed = ValidInteger(state.ObservedValue, minimum, maximum, step);
        bool available = state.Available
            && state.Quality is HardwareStateQuality.Observed or HardwareStateQuality.Verified
            && (desired.HasValue || observed.HasValue);
        string status = StatusText(view, available);
        return new TdpProjection(
            new NativeQamTdpState(
                available,
                minimum,
                maximum,
                step,
                desired,
                observed,
                ProgressText(projection.Progress),
                status),
            descriptor.InstanceId);
    }

    private void OnCapabilityViewsChanged(IReadOnlyList<DeviceCapabilityView> views) =>
        StateChanged?.Invoke();

    private static int? ValidInteger(
        CapabilityValue? value,
        int minimum,
        int maximum,
        int step)
    {
        if (value?.Kind is not CapabilityValueKind.Integer
            || value.IntegerValue is not int integer
            || integer < minimum
            || integer > maximum
            || (integer - minimum) % step != 0)
        {
            return null;
        }

        return integer;
    }

    private static string ProgressText(CommandProgress progress) => progress switch
    {
        CommandProgress.Pending => "applying",
        CommandProgress.Completed => "completed",
        CommandProgress.Failed => "failed",
        CommandProgress.Uncertain => "uncertain",
        _ => string.Empty,
    };

    private static string StatusText(DeviceCapabilityView view, bool available)
    {
        string? detail = view.LastResult?.Reason?.Detail
            ?? view.Projection.State.Reason?.Detail;
        if (!available && string.IsNullOrWhiteSpace(detail))
        {
            detail = "The primary power limit is not currently available.";
        }
        else if (view.Projection.DesiredValueOutOfRange)
        {
            detail = "The desired power limit is outside the current descriptor.";
        }

        return Bound(detail);
    }

    private static NativeQamTdpState Unavailable(string detail) => new(
        false,
        null,
        null,
        null,
        null,
        null,
        string.Empty,
        Bound(detail));

    private static string OutcomeText(CommandOutcome outcome) => outcome switch
    {
        CommandOutcome.Rejected => "The primary power-limit command was rejected.",
        CommandOutcome.TimedOut => "The primary power-limit command timed out.",
        CommandOutcome.Indeterminate => "The primary power-limit result is indeterminate.",
        _ => "The primary power-limit command did not complete.",
    };

    private static string Bound(string? value) => NativeQamText.Bound(value);

    internal sealed record TdpProjection(NativeQamTdpState State, string? InstanceId);
}

internal sealed class UnavailableNativeQamTdpService : INativeQamTdpService
{
    public event Action? StateChanged
    {
        add { }
        remove { }
    }

    public NativeQamTdpState Current { get; } = new(
        false,
        null,
        null,
        null,
        null,
        null,
        string.Empty,
        "Device Integration is not active in this session.");

    public Task<NativeQamCommandResult> SetPrimaryLimitAsync(
        int watts,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new NativeQamCommandResult(false, Current.StatusText));
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// Projects WSGM's AutoTDP into Steam's native quick-access menu, beside the limit it moves.
/// </summary>
/// <remarks>
/// AutoTDP is a WSGM setting driving a plugin capability, not a capability of its own, so this reads
/// the coordinator directly rather than looking for a descriptor. One owner: this switch, the
/// overlay's Power and thermals row, and the Settings checkbox all move
/// <c>DeviceIntegration.AutoTdpEnabled</c> through the same method, and none of them holds a copy.
/// </remarks>
internal sealed class DeviceCoordinatorNativeQamAutoTdpService : INativeQamAutoTdpService
{
    private readonly DeviceCoordinator _coordinator;
    private bool _disposed;

    /// <summary>Creates the projection over a running coordinator.</summary>
    /// <param name="coordinator">The coordinator owning the AutoTDP setting and status.</param>
    internal DeviceCoordinatorNativeQamAutoTdpService(DeviceCoordinator coordinator)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _coordinator.ConfigurationChanged += OnChanged;
        _coordinator.CapabilityViewsChanged += OnCapabilityViewsChanged;
        // The setting is not the state: AutoTDP moves between idle, controlling and paused, and its
        // wattage and frametime detail change, with the stored setting and every capability view
        // untouched. Without this the row rendered whatever it last saw.
        _coordinator.AutoTdpStatusChanged += OnChanged;
    }

    /// <inheritdoc/>
    public event Action? StateChanged;

    /// <inheritdoc/>
    public NativeQamAutoTdpState Current => Project(
        _coordinator.AutoTdpEnabled,
        _coordinator.AutoTdpStatus,
        DeviceCoordinatorNativeQamTdpService.Project(_coordinator.CapabilitySnapshot())
            .State.Available);

    /// <inheritdoc/>
    public async Task<NativeQamCommandResult> SetEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        NativeQamAutoTdpState state = Current;
        if (!state.Available)
        {
            return new NativeQamCommandResult(false, state.StatusText);
        }

        // Idempotent rather than an error: the page and the store can disagree for one frame after
        // a change made somewhere else, and re-sending the value it already has is the harmless way
        // that resolves. The coordinator compares and sets under its own transition gate, so the
        // requested value is what lands even when another surface changed it in between — a toggle
        // decided from the snapshot above would invert that newer value instead.
        await _coordinator.SetAutoTdpEnabledAsync(enabled, cancellationToken).ConfigureAwait(false);
        return new NativeQamCommandResult(true, null);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _coordinator.ConfigurationChanged -= OnChanged;
        _coordinator.CapabilityViewsChanged -= OnCapabilityViewsChanged;
        _coordinator.AutoTdpStatusChanged -= OnChanged;
    }

    /// <summary>Projects the stored setting and live status into the menu's vocabulary.</summary>
    /// <param name="enabled">The stored setting.</param>
    /// <param name="status">The running service's state, or null when it is not running.</param>
    /// <param name="powerLimitAvailable">Whether a primary power limit exists to drive.</param>
    /// <returns>The state the menu renders.</returns>
    internal static NativeQamAutoTdpState Project(
        bool enabled,
        AutoTdpStatus? status,
        bool powerLimitAvailable)
    {
        // Without a power limit there is nothing to control, so the switch is not offered rather
        // than offered and then silently ineffective.
        if (!powerLimitAvailable)
        {
            return new NativeQamAutoTdpState(
                false,
                enabled,
                false,
                null,
                string.Empty,
                NativeQamText.Bound("No primary power limit is available to control."));
        }

        if (status is null)
        {
            return new NativeQamAutoTdpState(
                true,
                enabled,
                false,
                null,
                enabled ? "applying" : string.Empty,
                NativeQamText.Bound(enabled ? "Starting." : string.Empty));
        }

        bool controlling = status.State is AutoTdpState.Controlling;
        return new NativeQamAutoTdpState(
            // Unavailable is the one state where the switch must not be operable: it means AutoTDP
            // cannot run on this device however the setting is left.
            status.State is not AutoTdpState.Unavailable,
            enabled,
            controlling,
            status.Watts,
            status.State switch
            {
                AutoTdpState.Controlling => "completed",
                AutoTdpState.Unavailable => "failed",
                _ => string.Empty,
            },
            NativeQamText.Bound(status.Detail));
    }

    private void OnCapabilityViewsChanged(IReadOnlyList<DeviceCapabilityView> views) => OnChanged();

    private void OnChanged() => StateChanged?.Invoke();
}

internal sealed class UnavailableNativeQamAutoTdpService : INativeQamAutoTdpService
{
    public event Action? StateChanged
    {
        add { }
        remove { }
    }

    public NativeQamAutoTdpState Current { get; } = new(
        false,
        false,
        false,
        null,
        string.Empty,
        "Device Integration is not active in this session.");

    public Task<NativeQamCommandResult> SetEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new NativeQamCommandResult(false, Current.StatusText));
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// Projects WSGM's own controller management into Steam's native quick-access menu.
/// </summary>
/// <remarks>
/// The controller target is WSGM's setting, not a plugin capability, so this reads
/// <see cref="ControllerManager"/> through the coordinator instead of looking for a capability
/// descriptor. That keeps one owner: the QAM control and the overlay's controller page move the same
/// stored default through the same method, and neither holds a copy of the target.
/// </remarks>
internal sealed class DeviceCoordinatorNativeQamControllerTargetService
    : INativeQamControllerTargetService
{
    private readonly DeviceCoordinator _coordinator;
    private bool _disposed;

    /// <summary>Creates the projection over a running coordinator.</summary>
    /// <param name="coordinator">The coordinator owning controller management.</param>
    internal DeviceCoordinatorNativeQamControllerTargetService(DeviceCoordinator coordinator)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _coordinator.ControllerStatusChanged += OnControllerStatusChanged;
    }

    /// <inheritdoc/>
    public event Action? StateChanged;

    /// <inheritdoc/>
    public NativeQamControllerTargetState Current => Project(
        _coordinator.ControllerManagementEnabled,
        _coordinator.ControllerStatus,
        _coordinator.InstalledPackage is not null,
        _coordinator.SupportedControllerTargets);

    /// <inheritdoc/>
    public async Task<NativeQamCommandResult> SetTargetAsync(
        string target,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        NativeQamControllerTargetState state = Current;
        if (!state.Available)
        {
            return new NativeQamCommandResult(false, state.StatusText);
        }

        if (!TryParseTarget(target, out ManagedControllerTarget parsed))
        {
            return new NativeQamCommandResult(false, $"'{target}' is not a controller target.");
        }

        ControllerManagerStatus status = await _coordinator
            .SetControllerTargetAsync(parsed, cancellationToken)
            .ConfigureAwait(false);

        // Truthful rather than optimistic: the setting is stored either way, but a manager that
        // could not bring the new target up is not a success the menu should show as one.
        bool succeeded = status.State is not
            (ControllerManagementState.Faulted or ControllerManagementState.Unavailable);
        return new NativeQamCommandResult(succeeded, succeeded ? null : status.Detail);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _coordinator.ControllerStatusChanged -= OnControllerStatusChanged;
    }

    /// <summary>Projects controller state into the menu's closed vocabulary.</summary>
    /// <param name="enabled">Whether controller management may run at all.</param>
    /// <param name="status">The manager's current truthful state.</param>
    /// <param name="packageInstalled">Whether a device package is installed.</param>
    /// <param name="supportedTargets">Targets the backend on this machine can create.</param>
    /// <returns>The state the menu renders.</returns>
    internal static NativeQamControllerTargetState Project(
        bool enabled,
        ControllerManagerStatus status,
        bool packageInstalled,
        IReadOnlyList<ManagedControllerTarget> supportedTargets)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(supportedTargets);
        if (!enabled)
        {
            return new NativeQamControllerTargetState(
                false,
                Array.Empty<NativeQamControllerTargetOption>(),
                string.Empty,
                string.Empty,
                string.Empty,
                NativeQamText.Bound(status.Detail),
                false);
        }

        // Only what the backend can actually build. These are WSGM's own virtual devices rather
        // than hardware, but a target still needs an encoder behind it: offering one that has none
        // meant the selection persisted, target creation was refused, and controller management
        // reported itself unavailable until the user found their way back to the setting.
        NativeQamControllerTargetOption[] targets =
        [
            .. new[]
            {
                (Target: ManagedControllerTarget.SteamDeckComposite, Label: "Steam Deck"),
                (Target: ManagedControllerTarget.Xbox360, Label: "Xbox 360"),
                (Target: ManagedControllerTarget.DualShock4, Label: "DualShock 4"),
            }
                .Where(option => supportedTargets.Contains(option.Target))
                .Select(option => new NativeQamControllerTargetOption(
                    option.Target.ToString(),
                    option.Label,
                    true)),
        ];

        bool available = status.State is
            ControllerManagementState.Idle or ControllerManagementState.Active;
        string selected = status.Target is { } target ? target.ToString() : string.Empty;

        // Observed is what a target actually exists for right now, which is only true while Active.
        // Reporting the selection back as if it were observed would hide a target that was chosen
        // but never came up.
        string observed = status.State is ControllerManagementState.Active ? selected : string.Empty;
        string detail = status.Detail;
        if (available && string.IsNullOrWhiteSpace(detail) && !packageInstalled)
        {
            detail = "No device package is installed, so no physical controller is being captured.";
        }

        return new NativeQamControllerTargetState(
            available,
            targets,
            selected,
            observed,
            ProgressFor(status.State),
            NativeQamText.Bound(detail),
            // A running game holds the target it was launched with, so a change reaches it only on
            // the next launch. Saying so is the difference between a control that looks broken and
            // one the user understands.
            ApplicationRestartRequired: status.ApplicationId is not null);
    }

    /// <summary>Maps a stored target name back onto the enumeration.</summary>
    /// <param name="target">The name the menu sent.</param>
    /// <param name="parsed">Receives the parsed target.</param>
    /// <returns>Whether the name named a target.</returns>
    /// <remarks>
    /// Ordinal and case-sensitive on purpose: the menu is sent these names from
    /// <see cref="Project"/>, so anything else is a caller defect rather than user input to be
    /// forgiving about.
    /// </remarks>
    internal static bool TryParseTarget(string target, out ManagedControllerTarget parsed) =>
        Enum.TryParse(target, ignoreCase: false, out parsed)
            && Enum.IsDefined(parsed);

    private static string ProgressFor(ControllerManagementState state) => state switch
    {
        ControllerManagementState.Active => "completed",
        ControllerManagementState.Idle => string.Empty,
        ControllerManagementState.Faulted => "failed",
        _ => string.Empty,
    };

    private void OnControllerStatusChanged(ControllerManagerStatus status) =>
        StateChanged?.Invoke();
}

internal sealed class UnavailableNativeQamControllerTargetService
    : INativeQamControllerTargetService
{
    public event Action? StateChanged
    {
        add { }
        remove { }
    }

    public NativeQamControllerTargetState Current { get; } = new(
        false,
        Array.Empty<NativeQamControllerTargetOption>(),
        string.Empty,
        string.Empty,
        string.Empty,
        DeviceFeatureAvailability.ControllerManagementDetail,
        false);

    public Task<NativeQamCommandResult> SetTargetAsync(
        string target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new NativeQamCommandResult(false, Current.StatusText));
    }

    public void Dispose()
    {
    }
}

/// <summary>The panel backlight level Steam's brightness slider shows.</summary>
/// <param name="Percent">The level, 0 to 100, read from the panel itself.</param>
internal sealed record SteamBrightnessState(int Percent);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SteamBrightnessState))]
[JsonSerializable(typeof(SteamNetworkState))]
[JsonSerializable(typeof(SteamNetworkAccessPointState))]
[JsonSerializable(typeof(NativeQamTdpState))]
[JsonSerializable(typeof(NativeQamAutoTdpState))]
[JsonSerializable(typeof(NativeQamControllerTargetState))]
[JsonSerializable(typeof(NativeQamFrameLimitState))]
[JsonSerializable(typeof(NativeQamResolutionState))]
[JsonSerializable(typeof(NativeQamVrrState))]
[JsonSerializable(typeof(SteamBluetoothState))]
[JsonSerializable(typeof(SteamBluetoothDevice))]
[JsonSerializable(typeof(NativeQamAudioState))]
[JsonSerializable(typeof(NativeQamAudioDevice))]
internal sealed partial class NativeQamSemanticJsonContext : JsonSerializerContext;
