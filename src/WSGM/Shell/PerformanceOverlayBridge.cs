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
/// Projects the session-owned performance service into closed overlay descriptors without owning
/// RTSS or retaining an overlay window.
/// </summary>
internal sealed class PerformanceOverlayBridge : IDisposable
{
    /// <summary>
    /// The top of the frame-limit slider, in frames per second.
    /// </summary>
    /// <remarks>
    /// 280 rather than whatever RTSS reports it will accept (1000). The slider has to be crossable
    /// on a thumbstick, and a range that reaches a thousand makes every rate anyone actually uses
    /// live in its first third. This covers every panel a handheld drives, internal or attached.
    /// </remarks>
    private const int MaximumFrameLimit = 280;

    /// <summary>The five overlay notches, named as WSGM renders them.</summary>
    /// <remarks>
    /// These are WSGM's own OSD levels from <c>Core\RtssOsd.cs</c>, not Valve's wire enum — the
    /// renderer behind them is ours, and the wire translation happens at the QAM boundary. The row
    /// showed "On" for every one of 1 to 4 before this, which made four different overlays
    /// indistinguishable from each other in the one place they are chosen.
    /// </remarks>
    private static readonly (int Level, string Label)[] OverlayLevelNames =
    [
        (0, "Off"),
        (1, "Minimal"),
        (2, "Extended"),
        (3, "Full"),
        (4, "Custom"),
    ];
    private readonly PerformanceService _service;
    private bool _disposed;

    internal PerformanceOverlayBridge(PerformanceService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _service.StateChanged += OnStateChanged;
    }

    public event Action? Changed;

    public IDisposable AcquireObservation()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _service.AcquireObservation();
    }

    public PerformanceOverlaySnapshot Snapshot()
    {
        PerformanceState state = _service.Current;
        if (!_service.Enabled)
        {
            return new PerformanceOverlaySnapshot(false, string.Empty, [], []);
        }

        RtssCapabilities? capabilities = state.Probe.Capabilities;
        bool ready = state.Probe.Availability == RtssAvailability.Ready && capabilities is not null;
        List<DescriptorRow> rows = [];
        rows.Add(BuildRow(
            "frame-limit",
            "Frame limit",
            DescribeLayer(state.FrameLimitLayer, state.Target),
            FormatFrameLimit(state),
            ready && capabilities!.Supports(PerformanceControl.FrameLimit),
            StatusFor(state, PerformanceControl.FrameLimit)) with
        {
            Range = ready ? FrameLimitRange(capabilities!) : null,
            Value = PreferredValue(state, PerformanceControl.FrameLimit) ?? 0,
        });
        rows.Add(BuildRow(
            "overlay-level",
            "Performance overlay",
            DescribeLayer(state.OverlayLevelLayer, state.Target),
            FormatOverlayLevel(state),
            ready && capabilities!.Supports(PerformanceControl.OverlayLevel),
            StatusFor(state, PerformanceControl.OverlayLevel)) with
        {
            Options = ready ? OverlayLevelOptions(capabilities!) : [],
            Value = PreferredValue(state, PerformanceControl.OverlayLevel),
        });
        List<DescriptorRow> profileRows =
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
                    : state.ApplicationProfileEnabled ? "On" : "Off",
                state.Target is not null,
                state.Target is null ? DescriptorStatus.Unsupported : DescriptorStatus.Available),
            BuildRow(
                "reset-profile",
                "Reset performance profile",
                state.ApplicationProfileEnabled
                    ? "Clear this application's overrides without turning its profile off."
                    : "Clear the global performance defaults.",
                "Reset",
                true,
                DescriptorStatus.None),
        ];
        return new PerformanceOverlaySnapshot(true, DescribeStatus(state), rows, profileRows);
    }

    /// <summary>Writes an exact value to the control one of the value rows owns.</summary>
    /// <param name="rowId">The row being set, <c>frame-limit</c> or <c>overlay-level</c>.</param>
    /// <param name="value">The value the slider settled on, or the option that was chosen.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task completing once the write has been attempted.</returns>
    /// <remarks>
    /// The counterpart to <see cref="InvokeAsync"/>, which advances a row that is pressed. A row
    /// carrying a range or options is not pressed, so it never reaches that path and never needs a
    /// "what comes next" rule — the control already knows the value the user asked for.
    /// </remarks>
    internal async Task SetValueAsync(
        string rowId,
        int value,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        PerformanceControl control = rowId switch
        {
            "frame-limit" => PerformanceControl.FrameLimit,
            "overlay-level" => PerformanceControl.OverlayLevel,
            _ => throw new InvalidOperationException($"The row '{rowId}' carries no value."),
        };

        PerformanceState state = _service.Current;
        if (state.Probe.Capabilities?.IsValid(control, value) is not true)
        {
            Log.Warn($"Performance {control} not set to {value}: RTSS does not accept that value.");
            return;
        }

        await _service.SetAsync(
            control,
            value,
            PerformancePersistenceTarget.Automatic,
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
            _ => null,
        };
        if (control is { } performanceControl)
        {
            await SetNextAsync(performanceControl, "overlay", cancellationToken).ConfigureAwait(false);
            return;
        }

        switch (row.Id)
        {
            case "application-profile" when _service.Current.Target is not null:
                await _service.SetApplicationProfileEnabledAsync(
                    !_service.Current.ApplicationProfileEnabled,
                    cancellationToken).ConfigureAwait(false);
                return;
            case "reset-profile":
                await _service.ResetProfileAsync(cancellationToken).ConfigureAwait(false);
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
        PerformanceState state = _service.Current;
        if (!_service.Enabled
            || state.Probe.Availability is not RtssAvailability.Ready
            || state.Probe.Capabilities?.Supports(PerformanceControl.OverlayLevel) is not true)
        {
            return false;
        }

        PerformanceCommandState result = await SetNextAsync(
            PerformanceControl.OverlayLevel,
            origin,
            cancellationToken).ConfigureAwait(false);
        return result.Phase is PerformanceCommandPhase.SucceededVerified
            or PerformanceCommandPhase.AppliedUnverified
            or PerformanceCommandPhase.Deferred;
    }

    /// <summary>Advances the overlay level, the one performance control that still cycles.</summary>
    /// <remarks>
    /// The frame limit does not: it is a slider now, and an OEM button that stepped it one notch at
    /// a time through a 280-value range would be a button that does nothing useful.
    /// </remarks>
    private async Task<PerformanceCommandState> SetNextAsync(
        PerformanceControl control,
        string origin,
        CancellationToken cancellationToken)
    {
        PerformanceState state = _service.Current;
        RtssCapabilities capabilities = state.Probe.Capabilities
            ?? throw new InvalidOperationException("RTSS capabilities are unavailable.");
        int next = NextOverlayLevel(state, capabilities);
        return await _service.SetAsync(
            control,
            next,
            PerformancePersistenceTarget.Automatic,
            origin,
            Guid.NewGuid().ToString("N"),
            cancellationToken).ConfigureAwait(false);
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

    private void OnStateChanged(PerformanceState _) => Changed?.Invoke();

    private static DescriptorRow BuildRow(
        string id,
        string title,
        string description,
        string trailing,
        bool canInvoke,
        DescriptorStatus status) => new(
        id,
        title,
        description,
        trailing,
        canInvoke,
        status);

    private static string DescribeStatus(PerformanceState state)
    {
        if (state.Command.Phase is PerformanceCommandPhase.Queued or PerformanceCommandPhase.Applying)
        {
            return "Applying RTSS performance setting…";
        }

        if (state.Command.Phase is PerformanceCommandPhase.Deferred)
        {
            return state.Command.Diagnostic
                ?? "The application setting is waiting for its foreground executable.";
        }

        if (state.Command.Phase is PerformanceCommandPhase.Rejected
            or PerformanceCommandPhase.TimedOut
            or PerformanceCommandPhase.Indeterminate
            or PerformanceCommandPhase.Failed)
        {
            return state.Command.Diagnostic ?? "The last RTSS command did not complete.";
        }

        return state.Probe.Availability switch
        {
            RtssAvailability.Ready => state.Target switch
            {
                null => "RTSS · global profile",
                { RtssProfileName: { Length: > 0 } profile } => $"RTSS · {profile}",
                { SteamAppId: { } appId } => $"Steam AppID {appId} · executable pending",
                _ => "Foreground application · executable pending",
            },
            RtssAvailability.Unknown => "Checking RTSS…",
            RtssAvailability.NotInstalled => "RTSS is not installed.",
            RtssAvailability.NotRunning => "RTSS is not running.",
            RtssAvailability.Incompatible => "The installed RTSS version is not supported.",
            RtssAvailability.AdapterUnavailable => "The RTSS profile API is unavailable.",
            _ => state.Probe.Diagnostic ?? "RTSS performance controls are unavailable.",
        };
    }

    private static string DescribeLayer(
        PerformancePolicyLayer layer,
        PerformanceApplicationTarget? target) => layer switch
        {
            PerformancePolicyLayer.Application when target?.RtssProfileName is { Length: > 0 } profile =>
                $"Application override · {profile}",
            PerformancePolicyLayer.Application => "Application override · executable pending",
            PerformancePolicyLayer.Global => "Global default",
            _ => "RTSS profile",
        };

    private static DescriptorRow BuildApplicationRow(PerformanceState state)
    {
        string trailing = state.Target switch
        {
            null => "None",
            { SteamAppId: { } appId } => $"Steam {appId}",
            { RtssProfileName: { Length: > 0 } profile } => profile,
            _ => "Detected",
        };
        string description = state.Target switch
        {
            null => "No Steam game or usable foreground application is active.",
            { RtssProfileName: { Length: > 0 } profile, SteamAppId: { } appId } =>
                $"Steam AppID {appId} paired with foreground executable {profile}.",
            { SteamAppId: { } appId } =>
                $"Steam AppID {appId} is active; waiting for its foreground executable.",
            { RtssProfileName: { Length: > 0 } profile } =>
                $"Foreground application profile {profile}.",
            _ => "An application identity is active but its executable is not known yet.",
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
        string description = state.ApplicationProfileEnabled
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
        int? value = PreferredValue(state, PerformanceControl.FrameLimit);
        return value switch
        {
            null => "Unavailable",
            0 => "Off",
            _ => string.Create(CultureInfo.InvariantCulture, $"{value} FPS"),
        };
    }

    private static string FormatOverlayLevel(PerformanceState state)
    {
        int? value = PreferredValue(state, PerformanceControl.OverlayLevel);
        return value switch
        {
            0 => "Off",
            1 => "On",
            null => "Unavailable",
            _ => value.Value.ToString(CultureInfo.InvariantCulture),
        };
    }

    private static int? PreferredValue(PerformanceState state, PerformanceControl control)
        => state.Observed.ValueFor(control) ?? state.Desired.ValueFor(control);

    private static DescriptorStatus StatusFor(PerformanceState state, PerformanceControl control)
    {
        if (state.Command.Control == control)
        {
            switch (state.Command.Phase)
            {
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

        PerformanceReadbackQuality quality = control == PerformanceControl.FrameLimit
            ? state.FrameLimitQuality
            : state.OverlayLevelQuality;
        return quality switch
        {
            PerformanceReadbackQuality.Verified => DescriptorStatus.Available,
            PerformanceReadbackQuality.AppliedUnverified => DescriptorStatus.Warning,
            _ => state.Probe.Availability == RtssAvailability.Ready
                ? DescriptorStatus.Warning
                : DescriptorStatus.Unsupported,
        };
    }

    /// <summary>The slider bounds RTSS will actually accept, clamped to a crossable range.</summary>
    private static DescriptorRange FrameLimitRange(RtssCapabilities capabilities) => new(
        Math.Max(0, capabilities.MinimumFrameLimit),
        Math.Min(MaximumFrameLimit, capabilities.MaximumFrameLimit),
        Step: 1);

    /// <summary>The named notches this RTSS build accepts, in order.</summary>
    private static IReadOnlyList<DescriptorOption> OverlayLevelOptions(RtssCapabilities capabilities) =>
        [.. OverlayLevelNames
            .Where(entry => capabilities.OverlayLevels.Contains(entry.Level))
            .Select(entry => new DescriptorOption(entry.Level, entry.Label))];

    private static int NextOverlayLevel(PerformanceState state, RtssCapabilities capabilities)
    {
        int current = PreferredValue(state, PerformanceControl.OverlayLevel) ?? int.MinValue;
        int[] choices = capabilities.OverlayLevels.Order().ToArray();
        if (choices.Length == 0)
        {
            throw new InvalidOperationException("RTSS published no usable overlay levels.");
        }

        return choices.FirstOrDefault(value => value > current, choices[0]);
    }
}
