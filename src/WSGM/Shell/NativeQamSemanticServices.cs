using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Contracts.Capabilities;

namespace WSGM.Shell;

internal sealed record NativeQamCommandResult(bool Succeeded, string? Error);

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
    string StatusText);

internal sealed record NativeQamOverlayLevelState(
    bool Available,
    IReadOnlyList<int> Levels,
    int? DesiredLevel,
    int? ObservedLevel,
    bool SupportsReadback,
    string ReadbackQuality,
    string PolicyLayer,
    bool ApplicationTargetAvailable,
    string TargetProfile,
    string AdapterAvailability,
    string Progress,
    string Fault,
    string StatusText);

internal sealed record NativeQamTdpState(
    bool Available,
    int? MinimumWatts,
    int? MaximumWatts,
    int? StepWatts,
    int? DesiredWatts,
    int? ObservedWatts,
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

internal interface INativeQamTdpService : IDisposable
{
    event Action? StateChanged;

    NativeQamTdpState Current { get; }

    Task<NativeQamCommandResult> SetPrimaryLimitAsync(
        int watts,
        CancellationToken cancellationToken);
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
        _service.Enabled);

    internal NativeQamOverlayLevelState OverlayLevel => ProjectOverlayLevel(
        _service.Current,
        _service.Enabled);

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

    internal static NativeQamFrameLimitState ProjectFrameLimit(
        PerformanceState state,
        bool enabled)
    {
        RtssCapabilities? capabilities = state.Probe.Capabilities;
        bool supported = capabilities?.Supports(PerformanceControl.FrameLimit) == true;
        bool available = enabled
            && state.Probe.Availability == RtssAvailability.Ready
            && supported;
        int? minimum = supported ? capabilities!.MinimumFrameLimit : null;
        int? maximum = supported ? capabilities!.MaximumFrameLimit : null;
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
            StatusText(state, PerformanceControl.FrameLimit, available));
    }

    internal static NativeQamOverlayLevelState ProjectOverlayLevel(
        PerformanceState state,
        bool enabled)
    {
        RtssCapabilities? capabilities = state.Probe.Capabilities;
        int[] levels = capabilities is null
            ? []
            : capabilities.OverlayLevels
                .Where(value => capabilities.IsValid(PerformanceControl.OverlayLevel, value))
                .Distinct()
                .Order()
                .ToArray();
        bool supported = levels.Length > 0;
        bool available = enabled
            && state.Probe.Availability == RtssAvailability.Ready
            && supported;
        return new NativeQamOverlayLevelState(
            available,
            levels,
            ValidValue(state.Desired.OverlayLevel, capabilities, PerformanceControl.OverlayLevel),
            ValidValue(state.Observed.OverlayLevel, capabilities, PerformanceControl.OverlayLevel),
            supported && capabilities!.OverlayLevelReadback,
            ReadbackText(state.OverlayLevelQuality),
            LayerText(state.OverlayLevelLayer),
            state.Target is not null,
            Bound(state.Target?.RtssProfileName),
            AvailabilityText(state.Probe.Availability),
            ProgressText(state.Command, PerformanceControl.OverlayLevel),
            FaultText(state.Command, PerformanceControl.OverlayLevel),
            StatusText(state, PerformanceControl.OverlayLevel, available));
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

    private static string Bound(string? value) => string.IsNullOrWhiteSpace(value)
        ? string.Empty
        : value.Length <= 240 ? value : value[..240];
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

    private static string Bound(string? value) => string.IsNullOrWhiteSpace(value)
        ? string.Empty
        : value.Length <= 240 ? value : value[..240];

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

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(NativeQamTdpState))]
[JsonSerializable(typeof(NativeQamControllerTargetState))]
[JsonSerializable(typeof(NativeQamFrameLimitState))]
[JsonSerializable(typeof(NativeQamOverlayLevelState))]
internal sealed partial class NativeQamSemanticJsonContext : JsonSerializerContext;
