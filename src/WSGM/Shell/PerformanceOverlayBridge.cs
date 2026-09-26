using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Overlay;

namespace WSGM.Shell;

/// <summary>
///     Projects the session-owned performance service into closed overlay descriptors without owning
///     RTSS or retaining an overlay window.
/// </summary>
internal sealed class PerformanceOverlayBridge : IDisposable
{
    /// <summary>
    ///     The top of the frame-limit slider, in frames per second.
    /// </summary>
    /// <remarks>
    ///     280 rather than whatever RTSS reports it will accept (1000). The slider has to be crossable
    ///     on a thumbstick, and a range that reaches a thousand makes every rate anyone actually uses
    ///     live in its first third. This covers every panel a handheld drives, internal or attached.
    /// </remarks>
    private const int MaximumFrameLimit = 280;

    /// <summary>The processor boost row's id, shared with the value writer.</summary>
    internal const string CpuBoostRowId = "cpu-boost";

    /// <summary>The five overlay notches, named as WSGM renders them.</summary>
    /// <remarks>
    ///     These are WSGM's own OSD levels from <c>Core\RtssOsd.cs</c>, not Valve's wire enum — the
    ///     renderer behind them is ours, and the wire translation happens at the QAM boundary. The row
    ///     showed "On" for every one of 1 to 4 before this, which made four different overlays
    ///     indistinguishable from each other in the one place they are chosen.
    /// </remarks>
    private static readonly (int Level, string Label)[] OverlayLevelNames =
    [
        (0, "Off"),
        (1, "Minimal"),
        (2, "Extended"),
        (3, "Full"),
        (4, "Custom")
    ];

    private readonly Func<(int Minimum, int Maximum)?> _panelFrameLimitRange;
    private readonly ProfileService _profiles;
    private readonly ApplicationPerformanceReconciler? _reconciler;
    private readonly PerformanceService _service;
    private bool _disposed;

    /// <param name="service">The session-owned RTSS service this projects.</param>
    /// <param name="profiles">The profile owner the per-game switch, reset and profile editor write to.</param>
    /// <param name="panelFrameLimitRange">
    ///     The caps the display can actually be asked for, from the same pairing policy that bookends
    ///     the Quick Access row. Null while no display has been enumerated — overlay-test has no
    ///     pairing service at all — and the slider then falls back to what RTSS alone will accept.
    /// </param>
    /// <param name="reconciler">
    ///     The carrier of the per-application processor boost mode, which this projects as a third
    ///     row. Null, or one without processor boost, leaves the section to RTSS alone.
    /// </param>
    internal PerformanceOverlayBridge(
        PerformanceService service,
        ProfileService profiles,
        Func<(int Minimum, int Maximum)?>? panelFrameLimitRange = null,
        ApplicationPerformanceReconciler? reconciler = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _panelFrameLimitRange = panelFrameLimitRange ?? (static () => null);
        _reconciler = reconciler is { CpuBoostAvailable: true } ? reconciler : null;
        _service.StateChanged += OnStateChanged;
        _profiles.Changed += OnProfilesChanged;
        if (_reconciler is not null)
        {
            _reconciler.CpuBoostChanged += OnCpuBoostChanged;
        }
    }

    /// <summary>Every saved game profile.</summary>
    internal IReadOnlyList<GameProfile> Profiles => _profiles.Current.Config.Games;

    /// <summary>The profile store and the application it resolves for.</summary>
    internal ProfileSnapshot ProfileSnapshot => _profiles.Current;

    /// <summary>The RTSS state, with the running application and per-game switch taken from the profile owner.</summary>
    /// <remarks>
    ///     The profile owner publishes first and RTSS catches up through the fan-out, so the header and the
    ///     profile rows read the owner rather than showing the previous game or switch until then.
    /// </remarks>
    private PerformanceState Current
    {
        get
        {
            var profiles = _profiles.Current;
            return _service.Current with
            {
                Target = PerformanceService.TargetFor(profiles.Active),
                ApplicationProfileEnabled = profiles.EditsGame
            };
        }
    }

    internal (PerformanceApplicationTarget? Target, bool Enabled) ProfileScope
    {
        get
        {
            var state = Current;
            return (state.Target, state.ApplicationProfileEnabled);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _service.StateChanged -= OnStateChanged;
        _profiles.Changed -= OnProfilesChanged;
        if (_reconciler is not null)
        {
            _reconciler.CpuBoostChanged -= OnCpuBoostChanged;
        }
    }

    public event Action? Changed;

    public IDisposable AcquireObservation()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_reconciler is not null)
        {
            // Windows is read when the panel opens, not on every render: the row then shows what
            // is in effect rather than what the last transition wrote, without a native call on
            // the UI thread.
            _ = RefreshCpuBoostAsync();
        }

        return _service.AcquireObservation();
    }

    private async Task RefreshCpuBoostAsync()
    {
        try
        {
            await _reconciler!.RefreshCpuBoostAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"Processor boost readback failed: {ex.Message}");
        }
    }

    internal Task<string> SaveProfileAsync(string? id, string name, IReadOnlyList<string> processNames,
        bool enabled, CancellationToken cancellationToken)
    {
        return _profiles.SaveGameAsync(id, name, processNames, enabled, cancellationToken);
    }

    internal Task<bool> DeleteProfileAsync(string id, CancellationToken cancellationToken)
    {
        return _profiles.DeleteGameAsync(id, cancellationToken);
    }

    /// <summary>Removes the running game's value for one setting, so it falls back to Global.</summary>
    /// <param name="overrideId">The id the row carried.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>Whether an override was removed.</returns>
    internal Task<bool> UseGlobalAsync(string overrideId, CancellationToken cancellationToken)
    {
        return ProfileSettingKey.TryParse(overrideId, out var key)
            ? _profiles.ClearGameOverrideAsync(key, null, cancellationToken)
            : Task.FromResult(false);
    }

    internal Task<bool> SetProfileScopeAsync(string applicationId, bool enabled,
        CancellationToken cancellationToken)
    {
        return _profiles.SetGameEnabledAsync(enabled, applicationId, cancellationToken);
    }

    public PerformanceOverlaySnapshot Snapshot()
    {
        var state = Current;
        var cpuBoost = BuildCpuBoostRow(state);
        if (!_service.Enabled && cpuBoost is null)
        {
            return new PerformanceOverlaySnapshot(false, string.Empty, [], []);
        }

        var capabilities = state.Probe.Capabilities;
        var ready = state.Probe.Availability == RtssAvailability.Ready && capabilities is not null;
        List<DescriptorRow> rows = [];
        if (cpuBoost is not null)
        {
            rows.Add(cpuBoost);
        }

        if (!_service.Enabled)
        {
            return new PerformanceOverlaySnapshot(true, "RTSS integration is off.", rows, BuildProfileRows(state));
        }

        rows.InsertRange(0,
        [
            BuildRow(
                    "frame-limit",
                    "Frame limit",
                    DescribeLayer(state.FrameLimitLayer, state.Target),
                    FormatFrameLimit(state),
                    ready && capabilities!.Supports(PerformanceControl.FrameLimit),
                    StatusFor(state, PerformanceControl.FrameLimit)) with
                {
                    Range = ready ? FrameLimitRange(capabilities!) : null,
                    Value = PreferredValue(state, PerformanceControl.FrameLimit) ?? 0,
                    OverrideId = state.FrameLimitLayer is ProfileSource.Game ? nameof(ProfileField.FrameLimit) : null
                },
            BuildRow(
                    "overlay-level",
                    "Performance overlay",
                    DescribeLayer(state.OverlayLevelLayer, state.Target),
                    FormatOverlayLevel(state),
                    ready && capabilities!.Supports(PerformanceControl.OverlayLevel),
                    StatusFor(state, PerformanceControl.OverlayLevel)) with
                {
                    Options = ready ? OverlayLevelOptions(capabilities!) : [],
                    Value = PreferredValue(state, PerformanceControl.OverlayLevel),
                    OverrideId = state.OverlayLevelLayer is ProfileSource.Game
                        ? nameof(ProfileField.OverlayLevel)
                        : null
                }
        ]);
        return new PerformanceOverlaySnapshot(true, DescribeStatus(state), rows, BuildProfileRows(state));
    }

    private List<DescriptorRow> BuildProfileRows(PerformanceState state)
    {
        return
        [
            BuildApplicationRow(state),
            BuildActiveProfileRow(state),
            BuildRow(
                    "application-profile",
                    "Per-application settings",
                    state.Target is null
                        ? "Start or focus an application to give it separate settings."
                        : "Keep separate performance values for the detected application.",
                    state.Target is null
                        ? "Unavailable"
                        : state.ApplicationProfileEnabled
                            ? "On"
                            : "Off",
                    state.Target is not null,
                    state.Target is null ? DescriptorStatus.Unsupported : DescriptorStatus.Available) with
                {
                    Options = [new DescriptorOption(0, "Off"), new DescriptorOption(1, "On")],
                    Value = state.ApplicationProfileEnabled ? 1 : 0
                },
            BuildRow(
                "reset-profile",
                "Reset performance profile",
                state.ApplicationProfileEnabled
                    ? "Clear every value this game overrides, so it uses Global again."
                    : "Clear the Global frame limit, overlay, power, refresh and processor boost values.",
                "Reset",
                true,
                DescriptorStatus.None)
        ];
    }

    /// <summary>The processor boost row, or null while this session has no readback to show.</summary>
    /// <remarks>
    ///     Windows policy, not RTSS: the row exists with RTSS off and with device integration off. The
    ///     value shown is the layer's preference when one is set, else what Windows reports, so a
    ///     game override reads as the mode the game asked for even before the transition wrote it.
    /// </remarks>
    private DescriptorRow? BuildCpuBoostRow(PerformanceState state)
    {
        if (_reconciler?.CpuBoostStatus is not { Supported: true } status)
        {
            return null;
        }

        var preference = _profiles.Current.Layers.Value(values => values.CpuBoost);
        var effective = preference.Value ?? status.OnAc;
        var layer = preference.Source switch
        {
            ProfileSource.Game when state.Target?.RtssProfileName is { Length: > 0 } profile =>
                $"Game override · {profile}",
            ProfileSource.Game => "Game override",
            ProfileSource.Global => "From Global",
            _ => "Not set · Windows keeps its own value"
        };
        var sources = status.OnAc == status.OnBattery
            ? "Applies to both plugged in and battery."
            : "Plugged in and battery currently differ; choosing sets both.";
        return BuildRow(
                CpuBoostRowId,
                "CPU boost mode",
                $"{layer} · {sources}",
                effective is { } mode ? CpuBoost.NameFor(mode) : "Not set by WSGM",
                true,
                DescriptorStatus.Available) with
            {
                Options = [.. CpuBoost.Offered.Select(option => new DescriptorOption((int)option.Mode, option.Name))],
                Value = effective is { } current ? (int)current : null,
                OverrideId = preference.Source is ProfileSource.Game ? nameof(ProfileField.CpuBoost) : null
            };
    }

    /// <summary>Writes an exact value to the control one of the value rows owns.</summary>
    /// <param name="rowId">The row being set, <c>frame-limit</c> or <c>overlay-level</c>.</param>
    /// <param name="value">The value the slider settled on, or the option that was chosen.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task completing once the write has been attempted.</returns>
    /// <remarks>
    ///     The counterpart to <see cref="InvokeAsync" />, which advances a row that is pressed. A row
    ///     carrying a range or options is not pressed, so it never reaches that path and never needs a
    ///     "what comes next" rule — the control already knows the value the user asked for.
    /// </remarks>
    internal async Task SetValueAsync(
        string rowId,
        int value,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (rowId == "application-profile")
        {
            if (Current.Target is { } target && value is 0 or 1)
            {
                await _profiles.SetGameEnabledAsync(value == 1, target.ApplicationId, cancellationToken)
                    .ConfigureAwait(false);
            }

            return;
        }

        if (rowId == CpuBoostRowId)
        {
            var mode = (CpuBoostMode)value;
            if (_reconciler is not null && Enum.IsDefined(mode))
            {
                await _reconciler.SetCpuBoostFromUserAsync(mode, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        var control = rowId switch
        {
            "frame-limit" => PerformanceControl.FrameLimit,
            "overlay-level" => PerformanceControl.OverlayLevel,
            _ => throw new InvalidOperationException($"The row '{rowId}' carries no value.")
        };

        var state = Current;
        if (state.Probe.Capabilities?.IsValid(control, value) is not true)
        {
            Log.Warn($"Performance {control} not set to {value}: RTSS does not accept that value.");
            return;
        }

        await _service.SetAsync(
            control,
            value,
            "overlay",
            Guid.NewGuid().ToString("N"),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task InvokeAsync(
        DescriptorRow row,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(row);
        PerformanceControl? control = row.Id switch
        {
            "overlay-level" => PerformanceControl.OverlayLevel,
            _ => null
        };
        if (control is { } performanceControl)
        {
            await SetNextAsync(performanceControl, "overlay", cancellationToken).ConfigureAwait(false);
            return;
        }

        switch (row.Id)
        {
            case "application-profile" when Current.Target is { } target:
                await _profiles.SetGameEnabledAsync(
                    !Current.ApplicationProfileEnabled,
                    target.ApplicationId,
                    cancellationToken).ConfigureAwait(false);
                return;
            case "reset-profile":
                await _profiles.ResetAsync(cancellationToken).ConfigureAwait(false);
                return;
            default:
                throw new InvalidOperationException("The performance row is not actionable.");
        }
    }

    /// <summary>Cycles the overlay level through the same policy the UI row owns.</summary>
    internal async Task<bool> CycleOverlayLevelAsync(
        string origin,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var state = Current;
        if (!_service.Enabled
            || state.Probe.Availability is not RtssAvailability.Ready
            || state.Probe.Capabilities?.Supports(PerformanceControl.OverlayLevel) is not true)
        {
            return false;
        }

        var result = await SetNextAsync(
            PerformanceControl.OverlayLevel,
            origin,
            cancellationToken).ConfigureAwait(false);
        return result.Phase is PerformanceCommandPhase.SucceededVerified
            or PerformanceCommandPhase.AppliedUnverified
            or PerformanceCommandPhase.Deferred;
    }

    /// <summary>Advances the overlay level, the one performance control that still cycles.</summary>
    /// <remarks>
    ///     The frame limit does not: it is a slider now, and an OEM button that stepped it one notch at
    ///     a time through a 280-value range would be a button that does nothing useful.
    /// </remarks>
    private async Task<PerformanceCommandState> SetNextAsync(
        PerformanceControl control,
        string origin,
        CancellationToken cancellationToken)
    {
        var state = Current;
        var capabilities = state.Probe.Capabilities
                           ?? throw new InvalidOperationException("RTSS capabilities are unavailable.");
        var next = NextOverlayLevel(state, capabilities);
        return await _service.SetAsync(
            control,
            next,
            origin,
            Guid.NewGuid().ToString("N"),
            cancellationToken).ConfigureAwait(false);
    }

    private void OnStateChanged(PerformanceState _)
    {
        Changed?.Invoke();
    }

    private void OnCpuBoostChanged()
    {
        Changed?.Invoke();
    }

    private void OnProfilesChanged(ProfileSnapshot snapshot, ProfileChangeKind kind)
    {
        Changed?.Invoke();
    }

    private static DescriptorRow BuildRow(
        string id,
        string title,
        string description,
        string trailing,
        bool canInvoke,
        DescriptorStatus status)
    {
        return new DescriptorRow(
            id,
            title,
            description,
            trailing,
            canInvoke,
            status);
    }

    private static string DescribeStatus(PerformanceState state)
    {
        return state.Command.Phase switch
        {
            PerformanceCommandPhase.Queued or PerformanceCommandPhase.Applying => "Applying RTSS performance setting…",
            PerformanceCommandPhase.Deferred => state.Command.Diagnostic
                                                ?? "The application setting is waiting for its foreground executable.",
            PerformanceCommandPhase.Rejected
                or PerformanceCommandPhase.TimedOut
                or PerformanceCommandPhase.Indeterminate
                or PerformanceCommandPhase.Failed => state.Command.Diagnostic ??
                                                     "The last RTSS command did not complete.",
            _ => state.Probe.Availability switch
            {
                RtssAvailability.Ready => state.Target switch
                {
                    null => "RTSS · global profile",
                    { RtssProfileName: { Length: > 0 } profile } => $"RTSS · {profile}",
                    { SteamAppId: { } appId } => $"Steam AppID {appId} · executable pending",
                    _ => "Foreground application · executable pending"
                },
                RtssAvailability.Unknown => "Checking RTSS…",
                RtssAvailability.NotInstalled => "RTSS is not installed.",
                RtssAvailability.NotRunning => "RTSS is not running.",
                RtssAvailability.Incompatible => "The installed RTSS version is not supported.",
                RtssAvailability.AdapterUnavailable => "The RTSS profile API is unavailable.",
                _ => state.Probe.Diagnostic ?? "RTSS performance controls are unavailable."
            }
        };
    }

    private static string DescribeLayer(
        ProfileSource layer,
        PerformanceApplicationTarget? target)
    {
        return layer switch
        {
            ProfileSource.Game when target?.RtssProfileName is { Length: > 0 } profile =>
                $"Game override · {profile}",
            ProfileSource.Game => "Game override · executable pending",
            ProfileSource.Global => "From Global",
            _ => "Not set · RTSS keeps its own value"
        };
    }

    private static DescriptorRow BuildApplicationRow(PerformanceState state)
    {
        var trailing = state.Target switch
        {
            null => "None",
            { SteamAppId: { } appId } => $"Steam {appId}",
            { RtssProfileName: { Length: > 0 } profile } => profile,
            _ => "Detected"
        };
        var description = state.Target switch
        {
            null => "No Steam game or usable foreground application is active.",
            { RtssProfileName: { Length: > 0 } profile, SteamAppId: { } appId } =>
                $"Steam AppID {appId} paired with foreground executable {profile}.",
            { SteamAppId: { } appId } =>
                $"Steam AppID {appId} is active; waiting for its foreground executable.",
            { RtssProfileName: { Length: > 0 } profile } =>
                $"Foreground application profile {profile}.",
            _ => "An application identity is active but its executable is not known yet."
        };
        return BuildRow(
            "detected-application",
            "Detected application",
            description,
            trailing,
            false,
            state.Target is null ? DescriptorStatus.Stale : DescriptorStatus.Available);
    }

    private static DescriptorRow BuildActiveProfileRow(PerformanceState state)
    {
        var description = state.ApplicationProfileEnabled
            ? state.Target?.RtssProfileName is { Length: > 0 } profile
                ? $"Settings are stored for {profile}."
                : "Settings are stored for this application and will reach RTSS once its executable is known."
            : "The detected application inherits WSGM's global performance settings.";
        return BuildRow(
            "active-profile",
            "Active performance profile",
            description,
            state.ApplicationProfileEnabled ? "Application" : "Global",
            false,
            state.ApplicationProfileEnabled && state.Target?.RtssProfileName is not { Length: > 0 }
                ? DescriptorStatus.Warning
                : DescriptorStatus.Available);
    }

    private static string FormatFrameLimit(PerformanceState state)
    {
        var value = PreferredValue(state, PerformanceControl.FrameLimit);
        return value switch
        {
            null => "Unavailable",
            0 => "Off",
            _ => string.Create(CultureInfo.InvariantCulture, $"{value} FPS")
        };
    }

    private static string FormatOverlayLevel(PerformanceState state)
    {
        var value = PreferredValue(state, PerformanceControl.OverlayLevel);
        return value switch
        {
            0 => "Off",
            1 => "On",
            null => "Unavailable",
            _ => value.Value.ToString(CultureInfo.InvariantCulture)
        };
    }

    private static int? PreferredValue(PerformanceState state, PerformanceControl control)
    {
        return state.Observed.ValueFor(control) ?? state.Desired.ValueFor(control);
    }

    private static DescriptorStatus StatusFor(PerformanceState state, PerformanceControl control)
    {
        if (state.Command.Control == control)
        {
            // ReSharper disable once SwitchStatementHandlesSomeKnownEnumValuesWithDefault
            switch (state.Command.Phase)
            {
                case PerformanceCommandPhase.Idle:
                case PerformanceCommandPhase.SucceededVerified:
                case PerformanceCommandPhase.AppliedUnverified:
                case PerformanceCommandPhase.ExternalChange:
                    break;
                case PerformanceCommandPhase.Queued:
                case PerformanceCommandPhase.Applying:
                    return DescriptorStatus.Progress;
                case PerformanceCommandPhase.Deferred:
                    return DescriptorStatus.Warning;
                case PerformanceCommandPhase.Rejected:
                case PerformanceCommandPhase.TimedOut:
                case PerformanceCommandPhase.Indeterminate:
                case PerformanceCommandPhase.Failed:
                    return DescriptorStatus.Faulted;
            }
        }

        var quality = control == PerformanceControl.FrameLimit
            ? state.FrameLimitQuality
            : state.OverlayLevelQuality;
        return quality switch
        {
            PerformanceReadbackQuality.Verified => DescriptorStatus.Available,
            PerformanceReadbackQuality.AppliedUnverified => DescriptorStatus.Warning,
            _ => state.Probe.Availability == RtssAvailability.Ready
                ? DescriptorStatus.Warning
                : DescriptorStatus.Unsupported
        };
    }

    /// <summary>The slider bounds, agreeing with the Quick Access row about what a legal cap is.</summary>
    /// <remarks>
    ///     RTSS accepts 0 to 1000 and the pairing policy offers a much narrower band — 30 up to the
    ///     highest rate the display accepted. Running the slider over RTSS's range instead let the
    ///     overlay set a 12 FPS cap that the Quick Access row could not represent, and that row
    ///     disappeared rather than drawing it (Claw, 2026-09-03). The two now bookend the same way.
    ///     <para>
    ///         Zero stays reachable, because zero is how this row is switched off and the overlay has no
    ///         separate switch for it; <see cref="DescriptorRange.OffBelow" /> carries the gap between it
    ///         and the lowest real cap.
    ///     </para>
    /// </remarks>
    private DescriptorRange FrameLimitRange(RtssCapabilities capabilities)
    {
        var ceiling = Math.Min(MaximumFrameLimit, capabilities.MaximumFrameLimit);
        DescriptorRange fallback = new(
            Math.Max(0, capabilities.MinimumFrameLimit),
            ceiling,
            1);
        if (_panelFrameLimitRange() is not { } panel)
        {
            return fallback;
        }

        var floor = Math.Max(capabilities.MinimumFrameLimit, panel.Minimum);
        var top = Math.Min(panel.Maximum, ceiling);
        return top >= floor ? new DescriptorRange(0, top, 1, floor) : fallback;
    }

    /// <summary>The named notches this RTSS build accepts, in order.</summary>
    private static IReadOnlyList<DescriptorOption> OverlayLevelOptions(RtssCapabilities capabilities)
    {
        return
        [
            .. OverlayLevelNames
                .Where(entry => capabilities.OverlayLevels.Contains(entry.Level))
                .Select(entry => new DescriptorOption(entry.Level, entry.Label))
        ];
    }

    private static int NextOverlayLevel(PerformanceState state, RtssCapabilities capabilities)
    {
        var current = PreferredValue(state, PerformanceControl.OverlayLevel) ?? int.MinValue;
        var choices = capabilities.OverlayLevels.Order().ToArray();
        return choices.Length == 0
            ? throw new InvalidOperationException("RTSS published no usable overlay levels.")
            : choices.FirstOrDefault(value => value > current, choices[0]);
    }
}
