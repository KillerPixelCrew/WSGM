using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Lifecycle;

namespace WSGM.Shell;

/// <summary>Drives the software fan curve and the authored fan profiles of the active device.</summary>
/// <remarks>
///     Owns the <see cref="DeviceFanCurveController" /> loop and decides which fans it runs on and with
///     which curve: the fan modes chosen as Software, and the authored profile, saved curve or the
///     device's Default preset. Fan writes go through <see cref="DeviceCapabilityCommands" />.
/// </remarks>
internal sealed class DeviceFanCurves
{
    private readonly Func<DeviceProfileScope?> _activeProfileScope;
    private readonly DeviceCapabilityRouter _capabilities;
    private readonly Func<bool> _closed;
    private readonly Func<bool> _integrationEnabled;
    private readonly Func<FanCurvePresetTable> _presets;
    private readonly CancellationToken _lifetime;
    private readonly ProfileService _profiles;
    private readonly Func<HandheldDeviceRuntime?> _runtime;
    private readonly Func<DeviceCycleState> _state;

    /// <summary>Composes the software fan curve for one device coordinator.</summary>
    /// <param name="capabilities">The device capability router fan controls are read from.</param>
    /// <param name="profiles">The profile owner the fan profile selection is saved to.</param>
    /// <param name="metrics">Samples RTSS sensors, one of the curve's temperature sources.</param>
    /// <param name="runtime">Reads the active handheld runtime, or null without a cycle.</param>
    /// <param name="presets">Reads the matched model's fan curve presets, resolved once with its definition.</param>
    /// <param name="integrationEnabled">Whether device integration is switched on.</param>
    /// <param name="state">Reads the device cycle state.</param>
    /// <param name="activeProfileScope">Reads the authored profiles of the exact matched model.</param>
    /// <param name="closed">Whether the coordinator closed admission.</param>
    /// <param name="lifetime">The coordinator's lifetime token, cancelled when admission closes.</param>
    /// <param name="execute">Routes an automatic fan write through the capability commands.</param>
    internal DeviceFanCurves(
        DeviceCapabilityRouter capabilities,
        ProfileService profiles,
        Func<RtssOsdMetrics> metrics,
        Func<HandheldDeviceRuntime?> runtime,
        Func<FanCurvePresetTable> presets,
        Func<bool> integrationEnabled,
        Func<DeviceCycleState> state,
        Func<DeviceProfileScope?> activeProfileScope,
        Func<bool> closed,
        CancellationToken lifetime,
        Func<string, string?, CapabilityValue?, CapabilityCommandOrigin, bool, CancellationToken,
            Task<CapabilityCommandResult>> execute)
    {
        _capabilities = capabilities;
        _profiles = profiles;
        _runtime = runtime;
        _presets = presets;
        _integrationEnabled = integrationEnabled;
        _state = state;
        _activeProfileScope = activeProfileScope;
        _closed = closed;
        _lifetime = lifetime;
        Controller = new DeviceFanCurveController(
            _ =>
            {
                // The handheld's own EC temperature is available outside games and independently
                // of RTSS. Preserve each source's sampling time instead of refreshing a cached value.
                var native = _capabilities.TryGetView(new DeviceCapabilityKey(CapabilityIds.Temperature, null));
                return SelectFanTemperature(native?.Projection.State, metrics(), DateTimeOffset.UtcNow);
            },
            async (instance, duty, token) =>
            {
                var fan = FanDutyView(_capabilities.Snapshot(), instance);
                if (fan is null)
                {
                    return new DeviceFanCurveWriteResult(false, "The fan duty control is unavailable.");
                }

                var result = await execute(fan.Descriptor.CapabilityId, fan.Descriptor.InstanceId,
                    CapabilityValue.Integer(duty), CapabilityCommandOrigin.AutomaticControl,
                    false, token).ConfigureAwait(false);
                return new DeviceFanCurveWriteResult(result.Outcome.IsApplied(), result.Reason?.Detail);
            },
            reason =>
            {
                _runtime()?.ReportSoftwareFanStatus(reason);
                Log.Change("device.fan-curve", reason ?? "Software fan curve is running.",
                    reason is null ? LogLevel.Info : LogLevel.Warn);
            },
            async (instance, token) =>
            {
                if (!_integrationEnabled() || _runtime() is not { IsActive: true })
                {
                    return;
                }

                var mode = FanModeView(_capabilities.Snapshot(), instance);
                var automatic = mode?.Descriptor.Choices.FirstOrDefault(choice => choice.Value == FanControlModes.Firmware);
                if (mode is not null && automatic is not null)
                {
                    var restored = await execute(mode.Descriptor.CapabilityId,
                        mode.Descriptor.InstanceId,
                        new CapabilityValue { Kind = CapabilityValueKind.Choice, ChoiceValue = automatic.Value },
                        CapabilityCommandOrigin.AutomaticControl,
                        false, token).ConfigureAwait(false);
                    if (restored.Outcome.IsApplied())
                    {
                        return;
                    }

                    if (restored.Outcome != CommandOutcome.Rejected)
                    {
                        throw new InvalidOperationException(restored.Reason?.Detail ?? "Fan restoration is uncertain.");
                    }
                }

                // Some firmware exposes a custom table without a reviewed factory-reset command.
                // A rejected restore wrote nothing. Use its declared maximum duty while temperature is absent.
                var fan = FanDutyView(_capabilities.Snapshot(), instance);
                if (fan is null)
                {
                    throw new InvalidOperationException("The fan fallback control is unavailable.");
                }

                var fallback = await execute(fan.Descriptor.CapabilityId, fan.Descriptor.InstanceId,
                    CapabilityValue.Integer(fan.Descriptor.Maximum ?? 100),
                    CapabilityCommandOrigin.AutomaticControl, false, token).ConfigureAwait(false);
                if (!fallback.Outcome.IsApplied())
                {
                    throw new InvalidOperationException(fallback.Reason?.Detail ??
                                                        "The fan fallback write was not applied.");
                }

                Log.Change("device.fan-fallback/" + instance,
                    "A firmware automatic restore is unavailable; the fan uses its declared maximum duty.",
                    LogLevel.Warn);
            });
    }

    /// <summary>The software fan loop; the coordinator suspends, resumes, stops and disposes it with the cycle.</summary>
    internal DeviceFanCurveController Controller { get; }

    /// <summary>Picks the hottest fresh temperature the native sensor and RTSS report.</summary>
    /// <param name="native">The handheld's own temperature state, or null when it publishes none.</param>
    /// <param name="metrics">The RTSS sensor snapshot.</param>
    /// <param name="now">The current time, which decides freshness.</param>
    /// <returns>The hottest plausible fresh reading with its sampling time, or no reading.</returns>
    internal static DeviceFanCurveTemperature SelectFanTemperature(CapabilityState? native,
        RtssOsdMetrics metrics, DateTimeOffset now)
    {
        var selected = new DeviceFanCurveTemperature(null, DateTimeOffset.MinValue);
        void Consider(double? value, DateTimeOffset sampledAt)
        {
            var age = now - sampledAt;
            if (value is not { } celsius || !double.IsFinite(celsius) || celsius is < 0 or > 150
                || age < TimeSpan.Zero || age > DeviceFanCurveController.MaximumTemperatureAge)
            {
                return;
            }
            if (selected.Celsius is null || celsius > selected.Celsius)
            {
                selected = new DeviceFanCurveTemperature(celsius, sampledAt);
            }
        }
        if (native is { Available: true, Quality: HardwareStateQuality.Observed or HardwareStateQuality.Verified,
                ObservedAt: { } at })
        {
            Consider(native.ObservedValue?.IntegerValue, at);
        }
        Consider(metrics.CpuTemperatureC, metrics.CapturedAt);
        Consider(metrics.GpuTemperatureC, metrics.CapturedAt);
        return selected;
    }

    /// <summary>Runs the software curve on exactly the fans whose chosen mode is Software.</summary>
    /// <param name="curve">The saved curve; the Default preset is used while none is saved.</param>
    /// <param name="explicitSelection">Whether the user chose this now, which clears a failed-write latch.</param>
    /// <param name="change">A fan mode being chosen now, before its desired value is recorded.</param>
    /// <param name="stopAll">Hands every fan back regardless of mode, for a refused or firmware-owned curve.</param>
    /// <returns>Completion once the loop runs with the new selection.</returns>
    internal Task ConfigureAsync(IReadOnlyList<CurvePoint>? curve, bool explicitSelection,
        (string Instance, bool Software)? change = null, bool stopAll = false)
    {
        var views = _capabilities.Snapshot();
        var fans = views.Where(view => view.Descriptor is
                { Role: CapabilityRole.FanDuty, SupportsWrite: true })
            .Select(view => new DeviceFanCurveFan(view.Descriptor.InstanceId ?? string.Empty,
                view.Descriptor.Minimum ?? 0, view.Descriptor.Maximum ?? 100))
            .Where(fan => !stopAll && InSoftwareMode(views, fan.InstanceId, change)).ToArray();
        var table = _presets();
        // The editor's Default preset for this device on the software curve's inputs, until the user saves one.
        var effective = curve is { Count: > 0 }
            ? curve
            : FanCurvePresets.DefaultCurve(table, FanCurvePresets.SoftwareCurveInputs, minimum: 0, maximum: 100);
        _runtime()?.SetSoftwareFans([.. fans.Select(fan => fan.InstanceId)], fans.Length > 0 ? effective : curve ?? []);
        return Controller.ConfigureAsync(fans.Length > 0 ? effective : null, fans,
            _integrationEnabled() && _state() is DeviceCycleState.Active or DeviceCycleState.Degraded,
            explicitSelection, table.Smoothing);
    }

    /// <summary>The software curve saved as the desired fan curve, or null when none is saved.</summary>
    /// <returns>The saved curve points.</returns>
    internal IReadOnlyList<CurvePoint>? SavedSoftwareCurve()
    {
        return _capabilities.TryGetView(new DeviceCapabilityKey(CapabilityIds.FanCurve, null))?.Projection
            .DesiredValue?.CurveValue;
    }

    /// <summary>Authored curves for the current exact model and the effective per-game selection.</summary>
    /// <returns>The authored fan profiles and the selection, or null when the model has none.</returns>
    internal (IReadOnlyList<DeviceAuthoredProfile> Profiles, Resolved<string?> Selected)? AuthoredProfileSelection()
    {
        var profiles = _activeProfileScope()?.Profiles.Where(profile => profile.CapabilityId == CapabilityIds.FanCurve)
            .ToArray();
        return profiles is not { Length: > 0 }
            ? null
            : (profiles, _profiles.Current.Layers.Reference(values => values.FanCurveProfileId));
    }

    /// <summary>Saves an authored fan profile selection in the layer in force and applies it.</summary>
    /// <param name="next">The profile to select, or null for none.</param>
    /// <param name="cancellationToken">Cancels the save; coordinator shutdown also cancels it.</param>
    /// <returns>Completion once the selection is saved and applied, or immediately for an unknown profile.</returns>
    internal async Task SelectAuthoredProfileAsync(string? next, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_closed(), typeof(DeviceCoordinator));
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime);
        var scope = _activeProfileScope();
        if (scope is null || (next is not null && !scope.Profiles.Any(profile => profile.ProfileId == next
                && profile.CapabilityId == CapabilityIds.FanCurve)))
        {
            return;
        }

        var snapshot = await _profiles.SetAsync(values => values.FanCurveProfileId = next,
            $"fan profile {next ?? "(none)"}", cancellationToken: admission.Token).ConfigureAwait(false);
        await ApplyAuthoredProfilesAsync(snapshot, admission.Token, true).ConfigureAwait(false);
    }

    /// <summary>Runs the software curve from the selected authored profile, or from the saved curve without one.</summary>
    /// <param name="snapshot">The profiles the selection resolves from.</param>
    /// <param name="token">Cancels before a valid profile is applied.</param>
    /// <param name="explicitSelection">Whether the user chose this now, which clears a failed-write latch.</param>
    /// <returns>Completion once the loop runs with the resolved curve.</returns>
    internal async Task ApplyAuthoredProfilesAsync(ProfileSnapshot snapshot, CancellationToken token,
        bool explicitSelection = false)
    {
        if (_closed())
        {
            return;
        }

        var selected = snapshot.Layers.Reference(values => values.FanCurveProfileId);
        var profile = _activeProfileScope()?.Profiles.FirstOrDefault(item => item.ProfileId == selected.Value
                                                                            && item.CapabilityId ==
                                                                            CapabilityIds.FanCurve);
        // Every WSGM authored curve uses duty control. Native firmware curves remain library operations.
        if (profile is null)
        {
            await ConfigureAsync(SavedSoftwareCurve(), explicitSelection).ConfigureAwait(false);
            return;
        }

        var descriptor = _capabilities.TryGetView(new DeviceCapabilityKey(CapabilityIds.FanCurve, null))?.Descriptor;
        if (DeviceProfileValidation.Validate(profile, descriptor, out var reason) != DeviceProfileRejection.None)
        {
            Log.Warn($"Fan profile '{profile.ProfileId}' refused: {reason}.");
            await ConfigureAsync(null, false, stopAll: true).ConfigureAwait(false);
            return;
        }

        token.ThrowIfCancellationRequested();
        await ConfigureAsync(profile.Curve.Select(point => new CurvePoint(point.Input, point.Output)).ToArray(),
            explicitSelection).ConfigureAwait(false);
    }

    private static bool InSoftwareMode(IReadOnlyList<DeviceCapabilityView> views, string instance,
        (string Instance, bool Software)? change)
    {
        if (change is { } chosen && chosen.Instance == instance)
        {
            return chosen.Software;
        }

        return FanModeView(views, instance)?.Projection.DesiredValue?.ChoiceValue == FanControlModes.Software;
    }

    // The duty control of one fan; an unnamed fan is the empty instance.
    private static DeviceCapabilityView? FanDutyView(IReadOnlyList<DeviceCapabilityView> views, string instance)
    {
        return views.FirstOrDefault(view => view.Descriptor.Role == CapabilityRole.FanDuty
                                            && (view.Descriptor.InstanceId ?? string.Empty) == instance);
    }

    // The mode control of one fan, or the shared one when the device has a single mode for all fans.
    private static DeviceCapabilityView? FanModeView(IReadOnlyList<DeviceCapabilityView> views, string instance)
    {
        var modes = views.Where(view => view.Descriptor.Role == CapabilityRole.FanMode)
            .ToArray();
        return modes.FirstOrDefault(view => (view.Descriptor.InstanceId ?? string.Empty) == instance)
               ?? modes.FirstOrDefault(view => view.Descriptor.InstanceId is null);
    }
}
