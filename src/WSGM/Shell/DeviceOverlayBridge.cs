using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Contracts.Capabilities;
using WSGM.Device.Contracts.Lifecycle;

namespace WSGM.Shell;

/// <summary>One presentation-only semantic capability row for the provisional Device surface.</summary>
internal sealed record DeviceOverlayCapability(
    string CapabilityId,
    string? InstanceId,
    string Title,
    string Description,
    string TrailingText,
    bool CanInvoke,
    CapabilityValue? NextValue);

/// <summary>Complete bounded Device-surface snapshot produced from coordinator-owned state.</summary>
internal sealed record DeviceOverlaySnapshot(
    bool Visible,
    string Status,
    string Detail,
    IReadOnlyList<DeviceOverlayCapability> Capabilities);

/// <summary>Closed semantic source consumed by the Device overlay destination.</summary>
internal interface IDeviceOverlaySource : IDisposable
{
    event Action? Changed;

    DeviceOverlaySnapshot Snapshot();

    Task InvokeAsync(
        DeviceOverlayCapability capability,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Adapts the authoritative coordinator to the overlay without exposing transport or plugin data.
/// </summary>
internal sealed class DeviceOverlayBridge : IDeviceOverlaySource
{
    private readonly DeviceCoordinator _coordinator;
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
        DevicePackageCandidate? package = _coordinator.SelectedPackage;
        List<DeviceOverlayCapability> capabilities = _coordinator.CapabilitySnapshot()
            .Take(128)
            .Select(ToOverlayCapability)
            .ToList();
        DevicePackageCandidate? staged = _coordinator.StagedPackageUpdate;
        if (staged?.Manifest is { } stagedManifest)
        {
            capabilities.Insert(0, new DeviceOverlayCapability(
                "wsgm.package.apply-update",
                null,
                "Apply staged device update",
                "Runs full device deactivation, then starts the verified replacement",
                stagedManifest.Version,
                CanInvoke: true,
                NextValue: null));
        }

        DevicePackageCandidate? rollback = _coordinator.RollbackPackage;
        if (rollback?.Manifest is { } rollbackManifest)
        {
            capabilities.Insert(staged is null ? 0 : 1, new DeviceOverlayCapability(
                "wsgm.package.rollback",
                null,
                "Roll back device package",
                "Runs full device deactivation and pins the retained previous version",
                rollbackManifest.Version,
                CanInvoke: true,
                NextValue: null));
        }
        string detail = package is null
            ? state is DeviceCycleState.Detected or DeviceCycleState.Passive
                ? "No compatible verified device package is active."
                : "Device integration is waiting for a compatible handheld."
            : $"{package.Manifest?.Id} {package.Manifest?.Version} · {package.TrustTier}";
        return new DeviceOverlaySnapshot(
            _coordinator.IntegrationEnabled,
            LifecycleLabel(state),
            detail,
            capabilities);
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

        if (capability.CapabilityId is "wsgm.package.apply-update")
        {
            await _coordinator.ApplyStagedPackageNowAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (capability.CapabilityId is "wsgm.package.rollback")
        {
            await _coordinator.RollbackPackageAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await _coordinator.ExecuteCapabilityAsync(
            capability.CapabilityId,
            capability.InstanceId,
            capability.NextValue,
            TimeSpan.FromSeconds(5),
            cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _coordinator.StateChanged -= OnStateChanged;
        _coordinator.CapabilityViewsChanged -= OnCapabilityViewsChanged;
        _coordinator.ConfigurationChanged -= OnConfigurationChanged;
    }

    private void OnStateChanged(DeviceCycleState _) => Changed?.Invoke();

    private void OnCapabilityViewsChanged(IReadOnlyList<DeviceCapabilityView> _) => Changed?.Invoke();

    private void OnConfigurationChanged() => Changed?.Invoke();

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
        string description = projection.Progress switch
        {
            CommandProgress.Pending => "Applying requested value…",
            CommandProgress.Uncertain => "Last request is unverified — refresh before retrying",
            CommandProgress.Failed => view.LastResult?.Reason?.Detail ?? "Last request failed",
            _ when projection.DesiredValueOutOfRange =>
                "Saved value is outside the current firmware range",
            _ when state.Reason is not null => state.Reason.Detail,
            _ => $"{QualityLabel(state.Quality)} · {PersistenceLabel(descriptor.Persistence)}",
        };
        return new DeviceOverlayCapability(
            descriptor.CapabilityId,
            descriptor.InstanceId,
            DisplayLabel(descriptor.Display),
            description,
            FormatValue(displayed, descriptor.Unit),
            canInvoke,
            descriptor.SupportsAction ? null : next);
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
        DeviceCycleState.Quarantined => "Device quarantined",
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

    public event Action? Changed;

    public DeviceOverlaySnapshot Snapshot()
    {
        string[] fanModes = ["Automatic", "Sport"];
        return new DeviceOverlaySnapshot(
            Visible: true,
            Status: "Simulated handheld",
            Detail: "Preview data only · no package, host, IPC, hook, or device handle",
            Capabilities:
            [
                new DeviceOverlayCapability(
                    "preview.power.tdp",
                    null,
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
                    "CPU temperature",
                    "Observed · read only",
                    "54 °C",
                    CanInvoke: false,
                    NextValue: null),
                new DeviceOverlayCapability(
                    "preview.rumble",
                    null,
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

    public void Dispose()
    {
    }
}
