using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Input;

namespace WSGM.Shell;

/// <summary>Stable final-overlay section selected from a semantic capability role.</summary>
/// <remarks>
/// Each section is a page in the Device destination, not a heading in one long list. The split is
/// driven by capability role alone, so a plugin that publishes nothing for a section simply causes
/// that page to be absent — no section is a fixed fixture of the UI.
/// </remarks>
internal enum DeviceOverlaySection
{
    /// <summary>Identity, scenario mode, and anything that describes the device as a whole.</summary>
    Overview,

    /// <summary>Named hardware profiles the user selects between.</summary>
    Profiles,

    /// <summary>Power limits, fans, charge behaviour, and temperature readings.</summary>
    PowerAndThermals,

    /// <summary>The physical controller, its motion sensors, and haptics.</summary>
    ControllerAndMotion,

    /// <summary>Device-specific OEM buttons and their assignments.</summary>
    Oem,

    /// <summary>Lighting and any remaining device features.</summary>
    LightingAndFeatures,

    /// <summary>Physical glyph presentation, preview, and input test.</summary>
    Glyphs,

    /// <summary>Health, recovery, and anything that exists to be read rather than changed.</summary>
    Diagnostics,
}

/// <summary>Structured capability health rendered without parsing diagnostic prose.</summary>
internal enum DeviceOverlayStatus
{
    None,
    Available,
    Warning,
    Faulted,
    Stale,
    ExternallyOwned,
    Unsupported,
    Progress,
}

/// <summary>One presentation-only semantic capability row for the final Device destination.</summary>
internal sealed record DeviceOverlayCapability(
    string CapabilityId,
    string? InstanceId,
    DeviceOverlaySection Section,
    DeviceOverlayStatus Status,
    string Title,
    string Description,
    string TrailingText,
    bool CanInvoke,
    CapabilityValue? NextValue);

/// <summary>Presentation-only state for the WSGM-owned physical-glyph selection command.</summary>
internal sealed record DeviceOverlayGlyphSelection(
    DeviceOverlayStatus Status,
    string Title,
    string Description,
    string TrailingText,
    bool CanCycle);

/// <summary>Presentation-only state for the WSGM-owned AutoTDP switch.</summary>
/// <remarks>
/// AutoTDP is WSGM's, not a plugin capability: it moves the plugin's power limit rather than being
/// one. It gets its own row for the same reason glyph selection does — synthesizing a pseudo
/// capability would need a second dispatch path through the capability invoke.
/// </remarks>
internal sealed record DeviceOverlayAutoTdp(
    DeviceOverlayStatus Status,
    string Title,
    string Description,
    string TrailingText,
    bool CanToggle);

/// <summary>The managed-controller row on the Controller and motion page.</summary>
/// <param name="Status">How healthy controller management currently is.</param>
/// <param name="Title">Row title.</param>
/// <param name="Description">What it is doing, or why it is not.</param>
/// <param name="TrailingText">The target in effect, or why there is none.</param>
/// <param name="CanCycle">Whether selecting the row changes the target.</param>
internal sealed record DeviceOverlayController(
    DeviceOverlayStatus Status,
    string Title,
    string Description,
    string TrailingText,
    bool CanCycle);

/// <summary>The one recovery action the Diagnostics page offers, when there is one.</summary>
/// <remarks>
/// Deliberately a single row rather than a panel of buttons. A faulted device cycle has exactly one
/// user-facing remedy — try again — and everything else about recovery is automatic; offering more
/// controls would imply choices that do not exist.
/// </remarks>
/// <param name="Status">How serious the current cycle state is.</param>
/// <param name="Title">Row title.</param>
/// <param name="Description">The cycle state, and when an automatic retry is due.</param>
/// <param name="TrailingText">Short state label.</param>
/// <param name="CanRetry">Whether a manual retry is available right now.</param>
internal sealed record DeviceOverlayRecovery(
    DeviceOverlayStatus Status,
    string Title,
    string Description,
    string TrailingText,
    bool CanRetry);

/// <summary>Complete bounded Device-surface snapshot produced from coordinator-owned state.</summary>
internal sealed record DeviceOverlaySnapshot(
    bool Visible,
    string Status,
    string Detail,
    DeviceOverlayGlyphSelection? GlyphSelection,
    IReadOnlyList<DeviceOverlayCapability> Capabilities,
    DeviceOverlayAutoTdp? AutoTdp = null,
    DeviceOverlayController? Controller = null,
    DeviceOverlayRecovery? Recovery = null);

/// <summary>Closed semantic source consumed by the Device overlay destination.</summary>
internal interface IDeviceOverlaySource : IDisposable
{
    event Action? Changed;

    DeviceOverlaySnapshot Snapshot();

    Task InvokeAsync(
        DeviceOverlayCapability capability,
        CancellationToken cancellationToken = default);

    Task CyclePhysicalGlyphSelectionAsync(CancellationToken cancellationToken = default);

    /// <summary>Turns AutoTDP on or off and persists the choice.</summary>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>A task completing once the new setting is persisted.</returns>
    Task ToggleAutoTdpAsync(CancellationToken cancellationToken = default);

    /// <summary>Moves the global default controller target to the next one and persists it.</summary>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>A task completing once the new target is persisted and applied.</returns>
    /// <remarks>
    /// Cycling rather than a picker, matching the glyph-selection row beside it. There are three
    /// targets and a controller has one button; a menu would cost a page for a choice a user makes
    /// once.
    /// </remarks>
    Task CycleControllerTargetAsync(CancellationToken cancellationToken = default);

    /// <summary>Retries a faulted device cycle now instead of waiting for the automatic retry.</summary>
    /// <param name="cancellationToken">Cancels the attempt.</param>
    /// <returns>A task completing once the attempt has been made.</returns>
    Task RetryDeviceCycleAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Adapts the authoritative coordinator to the overlay without exposing transport or plugin data.
/// </summary>
internal sealed class DeviceOverlayBridge : IDeviceOverlaySource
{
    private readonly DeviceCoordinator _coordinator;
    private readonly object _retryGate = new();
    private Timer? _retryTimer;
    private DateTimeOffset? _scheduledRetryAt;
    private bool _disposed;

    internal DeviceOverlayBridge(DeviceCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        _coordinator = coordinator;
        _coordinator.StateChanged += OnStateChanged;
        _coordinator.CapabilityViewsChanged += OnCapabilityViewsChanged;
        _coordinator.ConfigurationChanged += OnConfigurationChanged;
    }

    public event Action? Changed;

    public DeviceOverlaySnapshot Snapshot()
    {
        DeviceCycleState state = _coordinator.State;
        InstalledDevicePackage? package = _coordinator.InstalledPackage;
        List<DeviceOverlayCapability> capabilities = _coordinator.CapabilitySnapshot()
            .Take(128)
            .Select(ToOverlayCapability)
            .ToList();
        DeviceOverlayGlyphSelection glyphSelection = PhysicalGlyphSelectionView(
            _coordinator.PhysicalGlyphSelection,
            _coordinator.PhysicalGlyphSelectionSnapshot());
        DeviceOverlayAutoTdp autoTdp = AutoTdpView(
            _coordinator.AutoTdpEnabled,
            _coordinator.AutoTdpStatus);
        DateTimeOffset? retryAt = _coordinator.ManualRetryAvailableAt;
        ScheduleRetryRefresh(retryAt);
        DeviceOverlayRecovery? recovery = RecoveryView(state, retryAt, DateTimeOffset.UtcNow);
        DeviceOverlayController? controller = ControllerView(
            _coordinator.ControllerManagementEnabled,
            _coordinator.ControllerStatus);

        if (package is { Valid: false })
        {
            capabilities.Add(new DeviceOverlayCapability(
                $"wsgm.package.rejected.{package.Manifest?.Id ?? "unknown"}",
                package.Manifest?.Version,
                DeviceOverlaySection.Diagnostics,
                DeviceOverlayStatus.Unsupported,
                package.Manifest?.Id ?? "Invalid device package",
                package.Detail ?? "The installed package did not pass validation.",
                package.RejectionCode ?? "INVALID",
                false,
                null));
        }

        DevicePackageDiscovery discovery = _coordinator.PackageDiscovery;
        if (discovery.Inventory.Cardinality is DevicePackageCardinality.Multiple)
        {
            foreach (string packageRoot in discovery.Inventory.PackageRoots.Take(16))
            {
                capabilities.Add(new DeviceOverlayCapability(
                    $"wsgm.package.multiple.{Path.GetFileName(packageRoot)}",
                    null,
                    DeviceOverlaySection.Diagnostics,
                    DeviceOverlayStatus.Unsupported,
                    Path.GetFileName(packageRoot),
                    $"{discovery.Detail} Path: {packageRoot}",
                    discovery.ErrorCode ?? "MULTIPLE",
                    false,
                    null));
            }
        }
        capabilities = capabilities
            .Select((capability, index) => (Capability: capability, Index: index))
            .OrderBy(item => item.Capability.Section)
            .ThenBy(item => item.Index)
            .Select(item => item.Capability)
            .ToList();
        string detail = package is null
            ? state is DeviceCycleState.Detected or DeviceCycleState.Passive
                ? "No compatible verified device package is active."
                : "Device integration is waiting for a compatible handheld."
            : $"{package.Manifest?.Id} {package.Manifest?.Version}";
        return new DeviceOverlaySnapshot(
            _coordinator.IntegrationEnabled,
            LifecycleLabel(state),
            detail,
            glyphSelection,
            capabilities,
            autoTdp,
            controller,
            recovery);
    }

    /// <summary>Projects controller management into the Controller and motion page's own row.</summary>
    /// <param name="enabled">Whether management may run at all.</param>
    /// <param name="status">The manager's truthful state.</param>
    /// <returns>The row, or null when management is off and there is nothing to show.</returns>
    /// <remarks>
    /// A direct row rather than a synthesized capability, like the AutoTDP and glyph rows: the target
    /// is WSGM's own setting, so routing it through the plugin capability dispatch would mean a
    /// second meaning for a capability id and a branch inside the one invoke path.
    /// </remarks>
    internal static DeviceOverlayController? ControllerView(
        bool enabled,
        ControllerManagerStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (!enabled)
        {
            // Off is a setting, not a fault, and the page has other rows. Saying nothing here is
            // better than a permanently greyed control the user cannot act on from this page.
            return null;
        }

        DeviceOverlayStatus health = status.State switch
        {
            ControllerManagementState.Active => DeviceOverlayStatus.Available,
            ControllerManagementState.Idle => DeviceOverlayStatus.Stale,
            ControllerManagementState.Faulted => DeviceOverlayStatus.Warning,
            ControllerManagementState.Unavailable => DeviceOverlayStatus.Unsupported,
            _ => DeviceOverlayStatus.None,
        };
        string trailing = status.Target is { } target ? TargetLabel(target) : "NONE";
        string description = status.Detail;
        if (string.IsNullOrWhiteSpace(description))
        {
            description = status.State switch
            {
                ControllerManagementState.Active => "A virtual controller is present and receiving input",
                ControllerManagementState.Idle => "Ready · no virtual controller is present yet",
                _ => "Present the physical controller as a chosen virtual one",
            };
        }

        if (status.ApplicationId is { Length: > 0 })
        {
            // A game holds the target it launched with, so the row has to say that a change will not
            // reach the running one. Without this the control looks broken.
            description += " · restart the running game to change its target";
        }

        return new DeviceOverlayController(
            health,
            "Controller target",
            description,
            trailing,
            // Only when a change can actually take effect. Cycling into a target the backend cannot
            // bring up would replace one broken state with another.
            CanCycle: status.State is not ControllerManagementState.Unavailable);
    }

    /// <summary>Projects the device cycle's recoverable state into the Diagnostics page's own row.</summary>
    /// <param name="state">The current cycle state.</param>
    /// <param name="retryAvailableAt">When a manual retry becomes available, when one is pending.</param>
    /// <param name="now">The current time.</param>
    /// <returns>The row, or null when the cycle is healthy and there is nothing to recover.</returns>
    /// <remarks>
    /// Absent when healthy. A recovery control that is always present but almost always inert trains
    /// a user to ignore it, which is the opposite of what it is for.
    /// </remarks>
    internal static DeviceOverlayRecovery? RecoveryView(
        DeviceCycleState state,
        DateTimeOffset? retryAvailableAt,
        DateTimeOffset now)
    {
        if (retryAvailableAt is null)
        {
            return null;
        }

        bool available = now >= retryAvailableAt.Value;
        return new DeviceOverlayRecovery(
            available ? DeviceOverlayStatus.Warning : DeviceOverlayStatus.Progress,
            "Retry device integration",
            available
                ? $"{LifecycleLabel(state)} · starts one manual recovery attempt"
                : $"{LifecycleLabel(state)} · retry available at {retryAvailableAt.Value.ToLocalTime():t}",
            available ? "READY" : "WAIT",
            available);
    }

    private static string TargetLabel(ManagedControllerTarget target) => target switch
    {
        ManagedControllerTarget.SteamDeckComposite => "DECK",
        ManagedControllerTarget.Xbox360 => "XBOX",
        ManagedControllerTarget.DualShock4 => "DS4",
        _ => "NONE",
    };

    /// <summary>Projects AutoTDP's switch and live state into one row.</summary>
    /// <param name="enabled">The persisted setting.</param>
    /// <param name="status">Live state, or null when the service is not running.</param>
    /// <returns>The row.</returns>
    /// <remarks>
    /// The row reports what AutoTDP is actually doing, not merely that it is switched on. A user who
    /// turned it on and sees nothing happening needs to know whether it is waiting for a game, held
    /// by a manual power change, or unable to find a power limit at all.
    /// </remarks>
    internal static DeviceOverlayAutoTdp AutoTdpView(bool enabled, AutoTdpStatus? status)
    {
        if (!enabled)
        {
            return new DeviceOverlayAutoTdp(
                DeviceOverlayStatus.None,
                "AutoTDP",
                "Move the power limit from measured frame delivery",
                "OFF",
                CanToggle: true);
        }

        string detail = status?.Detail ?? "Starting.";
        string trailing = status?.Watts is { } watts
            ? watts.ToString(CultureInfo.InvariantCulture) + " W"
            : "ON";
        DeviceOverlayStatus health = status?.State switch
        {
            AutoTdpState.Controlling => DeviceOverlayStatus.Available,
            AutoTdpState.Paused => DeviceOverlayStatus.Warning,
            AutoTdpState.Unavailable => DeviceOverlayStatus.Unsupported,
            AutoTdpState.Idle => DeviceOverlayStatus.Stale,
            _ => DeviceOverlayStatus.None,
        };
        if (status?.FrametimeMs is { } frametime && status.TargetFrametimeMs is { } target)
        {
            // Invariant, like the watts above it. The surrounding sentence is English, and a row
            // that mixed a comma decimal separator with a full stop in one line would read as a
            // formatting bug rather than as localisation.
            detail = string.Create(
                CultureInfo.InvariantCulture,
                $"{frametime:F1} ms against a {target:F1} ms deadline · {detail}");
        }

        return new DeviceOverlayAutoTdp(health, "AutoTDP", detail, trailing, CanToggle: true);
    }

    public async Task InvokeAsync(
        DeviceOverlayCapability capability,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capability);
        if (!capability.CanInvoke)
        {
            return;
        }

        await _coordinator.ExecuteCapabilityAsync(
            capability.CapabilityId,
            capability.InstanceId,
            capability.NextValue,
            TimeSpan.FromSeconds(5),
            cancellationToken).ConfigureAwait(false);
    }

    public Task CyclePhysicalGlyphSelectionAsync(CancellationToken cancellationToken = default) =>
        _coordinator.CyclePhysicalGlyphSelectionAsync(cancellationToken);

    public Task ToggleAutoTdpAsync(CancellationToken cancellationToken = default) =>
        _coordinator.ToggleAutoTdpAsync(cancellationToken);

    public Task CycleControllerTargetAsync(CancellationToken cancellationToken = default) =>
        _coordinator.SetControllerTargetAsync(
            NextTarget(_coordinator.ControllerStatus.Target),
            cancellationToken);

    public Task RetryDeviceCycleAsync(CancellationToken cancellationToken = default) =>
        _coordinator.RetryAfterFaultAsync(cancellationToken);

    /// <summary>The next target in the cycle order.</summary>
    /// <param name="current">The target in effect, or null when none is.</param>
    /// <returns>The target the row moves to.</returns>
    /// <remarks>
    /// Steam Deck first from nothing, because it is the target that carries every control the
    /// canonical model defines; the other two exist for compatibility with software that does not
    /// understand it.
    /// </remarks>
    internal static ManagedControllerTarget NextTarget(ManagedControllerTarget? current) => current switch
    {
        ManagedControllerTarget.SteamDeckComposite => ManagedControllerTarget.Xbox360,
        ManagedControllerTarget.Xbox360 => ManagedControllerTarget.DualShock4,
        ManagedControllerTarget.DualShock4 => ManagedControllerTarget.SteamDeckComposite,
        _ => ManagedControllerTarget.SteamDeckComposite,
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_retryGate)
        {
            _retryTimer?.Dispose();
            _retryTimer = null;
            _scheduledRetryAt = null;
        }
        _coordinator.StateChanged -= OnStateChanged;
        _coordinator.CapabilityViewsChanged -= OnCapabilityViewsChanged;
        _coordinator.ConfigurationChanged -= OnConfigurationChanged;
    }

    private void OnStateChanged(DeviceCycleState _) => Changed?.Invoke();

    private void OnCapabilityViewsChanged(IReadOnlyList<DeviceCapabilityView> _) => Changed?.Invoke();

    private void OnConfigurationChanged() => Changed?.Invoke();

    private void ScheduleRetryRefresh(DateTimeOffset? retryAt)
    {
        lock (_retryGate)
        {
            if (_disposed || _scheduledRetryAt == retryAt)
            {
                return;
            }

            _retryTimer?.Dispose();
            _retryTimer = null;
            _scheduledRetryAt = retryAt;
            if (retryAt is null || retryAt <= DateTimeOffset.UtcNow)
            {
                return;
            }

            TimeSpan due = retryAt.Value - DateTimeOffset.UtcNow;
            _retryTimer = new Timer(_ =>
            {
                bool notify;
                lock (_retryGate)
                {
                    _retryTimer?.Dispose();
                    _retryTimer = null;
                    _scheduledRetryAt = null;
                    notify = !_disposed;
                }

                if (notify)
                {
                    Changed?.Invoke();
                }
            }, null, due, Timeout.InfiniteTimeSpan);
        }
    }

    private static DeviceOverlayCapability ToOverlayCapability(DeviceCapabilityView view)
    {
        CapabilityDescriptor descriptor = view.Descriptor;
        CapabilityProjection projection = view.Projection;
        CapabilityState state = projection.State;
        CapabilityValue? displayed = projection.PendingValue
            ?? projection.DesiredValue
            ?? state.ObservedValue;
        bool current = state.Available
            && state.Quality is HardwareStateQuality.Observed or HardwareStateQuality.Verified;
        CapabilityValue? next = NextValue(descriptor, displayed);
        bool canInvoke = current
            && (descriptor.SupportsAction
                || descriptor.SupportsWrite && next is not null);
        string description = (projection.Progress switch
        {
            CommandProgress.Pending => "Applying requested value…",
            CommandProgress.Uncertain => "Last request is unverified — refresh before retrying",
            CommandProgress.Failed => view.LastResult?.Reason?.Detail ?? "Last request failed",
            _ when projection.DesiredValueOutOfRange =>
                "Saved value is outside the current firmware range",
            _ when state.Reason is not null => state.Reason.Detail,
            _ => $"{QualityLabel(state.Quality)} · {PersistenceLabel(descriptor.Persistence)}",
        }) ?? "Capability state is unavailable.";
        return new DeviceOverlayCapability(
            descriptor.CapabilityId,
            descriptor.InstanceId,
            SectionFor(descriptor.Role),
            StatusFor(projection),
            DisplayLabel(descriptor.Display),
            description,
            FormatValue(displayed, descriptor.Unit),
            canInvoke,
            descriptor.SupportsAction ? null : next);
    }

    internal static DeviceOverlayGlyphSelection PhysicalGlyphSelectionView(
        DeviceGlyphSelection mode,
        PhysicalGlyphSelectionResult selection)
    {
        string trailing = mode switch
        {
            DeviceGlyphSelection.Automatic => "AUTO",
            DeviceGlyphSelection.NativeSteam => "STEAM",
            DeviceGlyphSelection.ManualReviewedProfile => "REVIEWED",
            _ => "AUTO",
        };
        string description;
        DeviceOverlayStatus status;
        if (selection.Profile is { } profile)
        {
            description = $"{profile.Manifest.DisplayName} · revision {profile.Manifest.Revision} · "
                + $"source {profile.Manifest.SourceRevision}"
                + (selection.FellBackFromMissingManualProfile
                    ? " · selected reviewed profile is missing; Automatic fallback"
                    : string.Empty);
            status = selection.FellBackFromMissingManualProfile
                ? DeviceOverlayStatus.Warning
                : DeviceOverlayStatus.Available;
        }
        else if (selection.FallbackReason is PhysicalGlyphFallbackReason.NativeSteamSelected)
        {
            description = "Steam and generic first-party glyphs remain unchanged.";
            status = DeviceOverlayStatus.Available;
        }
        else
        {
            description = selection.FallbackReason switch
            {
                PhysicalGlyphFallbackReason.DeviceIntegrationDisabled =>
                    "Device integration is off; generic glyphs remain active.",
                PhysicalGlyphFallbackReason.ExactDeviceMismatch =>
                    "The package profile does not match this exact device; generic glyphs remain active.",
                _ => "No reviewed physical profile is available; generic glyphs remain active.",
            };
            status = DeviceOverlayStatus.Warning;
        }

        return new DeviceOverlayGlyphSelection(
            status,
            "Physical glyphs",
            description,
            trailing,
            CanCycle: true);
    }

    private static DeviceOverlaySection SectionFor(CapabilityRole role) => role switch
    {
        CapabilityRole.ScenarioMode => DeviceOverlaySection.Overview,
        CapabilityRole.PowerSustainedLimit or CapabilityRole.PowerSlowLimit
            or CapabilityRole.PowerFastLimit or CapabilityRole.PowerPeakLimit
            or CapabilityRole.FanMode or CapabilityRole.FanDuty
            or CapabilityRole.FanTargetRpm or CapabilityRole.FanCurve
            or CapabilityRole.FanMeasuredRpm or CapabilityRole.ChargeLimit
            or CapabilityRole.ChargeProtectionMode or CapabilityRole.ChargeBypass
            or CapabilityRole.Telemetry => DeviceOverlaySection.PowerAndThermals,
        CapabilityRole.ControllerSource or CapabilityRole.MotionSource
            or CapabilityRole.HapticSink => DeviceOverlaySection.ControllerAndMotion,
        CapabilityRole.OemControl => DeviceOverlaySection.Oem,
        CapabilityRole.LightingPower or CapabilityRole.LightingBrightness
            or CapabilityRole.LightingZoneColor or CapabilityRole.LightingEffect
            or CapabilityRole.LightingEffectSpeed
            or CapabilityRole.GenericToggle or CapabilityRole.GenericRange
            or CapabilityRole.GenericChoice or CapabilityRole.GenericAction
            => DeviceOverlaySection.LightingAndFeatures,
        // A read-only value is something to consult, not to set, so it belongs with the rest of the
        // diagnostics rather than among the controls a user came to change.
        CapabilityRole.GenericReadOnly => DeviceOverlaySection.Diagnostics,
        _ => DeviceOverlaySection.Overview,
    };

    private static DeviceOverlayStatus StatusFor(CapabilityProjection projection)
    {
        if (projection.Progress is CommandProgress.Pending)
        {
            return DeviceOverlayStatus.Progress;
        }

        if (projection.Progress is CommandProgress.Failed
            || projection.State.Quality is HardwareStateQuality.Faulted
            || projection.State.Reason?.Code is CapabilityReasonCode.TransportFaulted)
        {
            return DeviceOverlayStatus.Faulted;
        }

        if (projection.Progress is CommandProgress.Uncertain || projection.DesiredValueOutOfRange)
        {
            return DeviceOverlayStatus.Warning;
        }

        if (projection.State.Quality is HardwareStateQuality.Stale
            || projection.State.Reason?.Code is CapabilityReasonCode.GenerationChanged
                or CapabilityReasonCode.ObservationExpired)
        {
            return DeviceOverlayStatus.Stale;
        }

        if (projection.State.Reason?.Code is CapabilityReasonCode.ResourceConflict
            or CapabilityReasonCode.ResourceReleased)
        {
            return DeviceOverlayStatus.ExternallyOwned;
        }

        if (projection.State.Reason?.Code is CapabilityReasonCode.Unsupported
            or CapabilityReasonCode.FirmwareNotVerified
            or CapabilityReasonCode.PrerequisiteMissing)
        {
            return DeviceOverlayStatus.Unsupported;
        }

        return projection.State.Available
            ? DeviceOverlayStatus.Available
            : DeviceOverlayStatus.Warning;
    }

    private static CapabilityValue? NextValue(
        CapabilityDescriptor descriptor,
        CapabilityValue? current) => descriptor.ValueKind switch
        {
            CapabilityValueKind.Boolean => new CapabilityValue
            {
                Kind = CapabilityValueKind.Boolean,
                BooleanValue = !(current?.BooleanValue ?? false),
            },
            CapabilityValueKind.Integer when descriptor.Minimum is { } minimum
                && descriptor.Maximum is { } maximum
                && descriptor.Step is { } step and > 0 => new CapabilityValue
                {
                    Kind = CapabilityValueKind.Integer,
                    IntegerValue = current?.IntegerValue is { } value && value + step <= maximum
                        ? value + step
                        : minimum,
                },
            CapabilityValueKind.Choice when descriptor.Choices.Count > 0 => new CapabilityValue
            {
                Kind = CapabilityValueKind.Choice,
                ChoiceValue = NextChoice(descriptor, current?.ChoiceValue),
            },
            _ => null,
        };

    private static string NextChoice(CapabilityDescriptor descriptor, string? current)
    {
        int index = descriptor.Choices.ToList().FindIndex(choice => string.Equals(
            choice.Value,
            current,
            StringComparison.Ordinal));
        return descriptor.Choices[(index + 1) % descriptor.Choices.Count].Value;
    }

    private static string DisplayLabel(CapabilityDisplay display) => display.Key switch
    {
        DisplayKey.Custom => display.CustomLabel ?? "Device control",
        DisplayKey.Tdp => "TDP",
        DisplayKey.SustainedPowerLimit => "Sustained power limit",
        DisplayKey.BoostPowerLimit => "Boost power limit",
        DisplayKey.PerformanceProfile => "Performance profile",
        DisplayKey.FanMode => "Fan mode",
        DisplayKey.FanSpeed => "Fan speed",
        DisplayKey.FanCurve => "Fan curve",
        DisplayKey.FanLeft => "Left fan",
        DisplayKey.FanRight => "Right fan",
        DisplayKey.ChargeLimit => "Charge limit",
        DisplayKey.BypassCharging => "Bypass charging",
        DisplayKey.Lighting => "Lighting",
        DisplayKey.Brightness => "Brightness",
        DisplayKey.LightingEffect => "Lighting effect",
        DisplayKey.LightingEffectSpeed => "Effect speed",
        DisplayKey.CpuTemperature => "CPU temperature",
        DisplayKey.Battery => "Battery",
        DisplayKey.Controller => "Controller",
        DisplayKey.Motion => "Motion",
        DisplayKey.Rumble => "Rumble",
        _ => "Device control",
    };

    private static string FormatValue(CapabilityValue? value, CapabilityUnit unit)
    {
        if (value is null)
        {
            return "—";
        }

        return value.Kind switch
        {
            CapabilityValueKind.Boolean => value.BooleanValue is true ? "ON" : "OFF",
            CapabilityValueKind.Integer => value.IntegerValue is { } integer
                ? $"{integer.ToString(CultureInfo.CurrentCulture)}{UnitSuffix(unit)}"
                : "—",
            CapabilityValueKind.Choice => value.ChoiceValue ?? "—",
            CapabilityValueKind.Color => value.ColorValue is { } color
                ? $"#{color:X6}"
                : "—",
            CapabilityValueKind.Curve => value.CurveValue.Count > 0
                ? $"{value.CurveValue.Count} points"
                : "—",
            _ => "RUN",
        };
    }

    private static string UnitSuffix(CapabilityUnit unit) => unit switch
    {
        CapabilityUnit.Watt => " W",
        CapabilityUnit.Percent => "%",
        CapabilityUnit.Celsius => " °C",
        CapabilityUnit.Rpm => " RPM",
        CapabilityUnit.Milliampere => " mA",
        CapabilityUnit.Millivolt => " mV",
        CapabilityUnit.Megahertz => " MHz",
        CapabilityUnit.Millisecond => " ms",
        _ => string.Empty,
    };

    private static string LifecycleLabel(DeviceCycleState state) => state switch
    {
        DeviceCycleState.Disabled => "Device integration off",
        DeviceCycleState.Detected => "Device detected",
        DeviceCycleState.Passive => "Device passive",
        DeviceCycleState.Activating => "Device activating",
        DeviceCycleState.Active => "Device active",
        DeviceCycleState.Degraded => "Device partly available",
        DeviceCycleState.Suspended => "Device suspended",
        DeviceCycleState.Deactivating => "Device deactivating",
        DeviceCycleState.Faulted => "Device faulted",
        _ => state.ToString(),
    };

    private static string QualityLabel(HardwareStateQuality quality) => quality switch
    {
        HardwareStateQuality.Verified => "Verified readback",
        HardwareStateQuality.Observed => "Observed",
        HardwareStateQuality.Stale => "Stale",
        HardwareStateQuality.Faulted => "Faulted",
        _ => "Unknown",
    };

    private static string PersistenceLabel(CapabilityPersistence persistence) => persistence switch
    {
        CapabilityPersistence.Volatile => "resets on device power loss",
        CapabilityPersistence.DevicePersistent => "stored on device",
        _ => "persistence unknown",
    };
}

/// <summary>In-memory Device surface used only by the explicitly safe overlay-test mode.</summary>
internal sealed class SimulatedDeviceOverlaySource : IDeviceOverlaySource
{
    private int _tdp = 15;
    private bool _lighting = true;
    private int _fanMode;
    private int _glyphSelection;
    private bool _autoTdp;
    private ManagedControllerTarget _controllerTarget = ManagedControllerTarget.SteamDeckComposite;

    public event Action? Changed;

    public DeviceOverlaySnapshot Snapshot()
    {
        string[] fanModes = ["Automatic", "Sport"];
        return new DeviceOverlaySnapshot(
            Visible: true,
            Status: "Simulated handheld",
            Detail: "Preview data only · no package, host, IPC, hook, or device handle",
            GlyphSelection: new DeviceOverlayGlyphSelection(
                DeviceOverlayStatus.Available,
                "Physical glyphs",
                "Preview-only physical presentation selection",
                _glyphSelection switch
                {
                    0 => "AUTO",
                    1 => "STEAM",
                    _ => "REVIEWED",
                },
                CanCycle: true),
            AutoTdp: DeviceOverlayBridge.AutoTdpView(
                _autoTdp,
                _autoTdp
                    ? new AutoTdpStatus(
                        AutoTdpState.Controlling,
                        15,
                        14.2,
                        16.6,
                        "steam:preview",
                        "Preview only; no power write is made.")
                    : null),
            Controller: DeviceOverlayBridge.ControllerView(
                enabled: true,
                new ControllerManagerStatus(
                    ControllerManagementState.Active,
                    _controllerTarget,
                    ControllerTargetSource.GlobalDefault,
                    null,
                    UiInputSource.ManagedCanonical,
                    "Preview only; no virtual controller is created.")),
            // The recovery row is deliberately shown in the preview even though nothing is faulted,
            // because laying it out is exactly what --overlay-test is for. Pressing it does nothing.
            Recovery: DeviceOverlayBridge.RecoveryView(
                DeviceCycleState.Faulted,
                DateTimeOffset.UtcNow.AddMinutes(-1),
                DateTimeOffset.UtcNow),
            Capabilities:
            [
                new DeviceOverlayCapability(
                    "preview.power.tdp",
                    null,
                    DeviceOverlaySection.PowerAndThermals,
                    DeviceOverlayStatus.Available,
                    "TDP",
                    "Verified readback · resets on device power loss",
                    $"{_tdp} W",
                    CanInvoke: true,
                    NextValue: new CapabilityValue
                    {
                        Kind = CapabilityValueKind.Integer,
                        IntegerValue = _tdp >= 30 ? 8 : _tdp + 1,
                    }),
                new DeviceOverlayCapability(
                    "preview.fan.mode",
                    null,
                    DeviceOverlaySection.PowerAndThermals,
                    DeviceOverlayStatus.Available,
                    "Fan mode",
                    "Observed · stored on device",
                    fanModes[_fanMode],
                    CanInvoke: true,
                    NextValue: new CapabilityValue
                    {
                        Kind = CapabilityValueKind.Choice,
                        ChoiceValue = fanModes[(_fanMode + 1) % fanModes.Length],
                    }),
                new DeviceOverlayCapability(
                    "preview.lighting",
                    null,
                    DeviceOverlaySection.LightingAndFeatures,
                    DeviceOverlayStatus.Available,
                    "Lighting",
                    "Verified readback · stored on device",
                    _lighting ? "ON" : "OFF",
                    CanInvoke: true,
                    NextValue: new CapabilityValue
                    {
                        Kind = CapabilityValueKind.Boolean,
                        BooleanValue = !_lighting,
                    }),
                new DeviceOverlayCapability(
                    "preview.temperature.cpu",
                    null,
                    DeviceOverlaySection.PowerAndThermals,
                    DeviceOverlayStatus.Available,
                    "CPU temperature",
                    "Observed · read only",
                    "54 °C",
                    CanInvoke: false,
                    NextValue: null),
                new DeviceOverlayCapability(
                    "preview.rumble",
                    null,
                    DeviceOverlaySection.ControllerAndMotion,
                    DeviceOverlayStatus.Available,
                    "Rumble",
                    "Short bounded preview action",
                    "RUN",
                    CanInvoke: true,
                    NextValue: null),
            ]);
    }

    public Task InvokeAsync(
        DeviceOverlayCapability capability,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capability);
        cancellationToken.ThrowIfCancellationRequested();
        switch (capability.CapabilityId)
        {
            case "preview.power.tdp":
                _tdp = capability.NextValue?.IntegerValue ?? _tdp;
                break;
            case "preview.fan.mode":
                _fanMode = (_fanMode + 1) % 2;
                break;
            case "preview.lighting":
                _lighting = capability.NextValue?.BooleanValue ?? _lighting;
                break;
        }

        Changed?.Invoke();
        return Task.CompletedTask;
    }

    public Task ToggleAutoTdpAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _autoTdp = !_autoTdp;
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    public Task CyclePhysicalGlyphSelectionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _glyphSelection = (_glyphSelection + 1) % 3;
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    public Task CycleControllerTargetAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _controllerTarget = DeviceOverlayBridge.NextTarget(_controllerTarget);
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>Preview-only: there is no device cycle to recover, so this reports and does nothing.</summary>
    /// <param name="cancellationToken">Cancels the attempt.</param>
    /// <returns>A completed task.</returns>
    /// <remarks>
    /// `--overlay-test` exists to lay out the surfaces without starting anything, so the recovery
    /// row is rendered but must stay inert. It is deliberately not an exception: a preview that
    /// threw when a control was pressed would be worse at its one job than one that does nothing.
    /// </remarks>
    public Task RetryDeviceCycleAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
    }
}
