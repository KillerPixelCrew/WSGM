using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Lifecycle;
using CapabilityRole = LibHandheld.Contracts.CapabilityRole;
using CapabilityValue = WSGM.Device.Sdk.Capabilities.CapabilityValue;
using CapabilityValueKind = WSGM.Device.Sdk.Capabilities.CapabilityValueKind;
using CommandOutcome = WSGM.Device.Sdk.Capabilities.CommandOutcome;
using CurvePoint = WSGM.Device.Sdk.Capabilities.CurvePoint;
using Deadline = LibHandheld.Contracts.Deadline;

namespace WSGM.Shell;

internal sealed partial class DeviceCoordinator
{
    private DeviceChargeLimitController _chargeLimits = null!;
    private DeviceFanCurveController _fanCurves = null!;

    private void InitializeFanCurves(Func<RtssOsdMetrics> metrics)
    {
        _chargeLimits = new DeviceChargeLimitController(() =>
            {
                var sample = metrics();
                return _client?.Device.Controls.Controls.Any(control => control.Role == CapabilityRole.ChargeBypass
                                                                        && control.State?.Available == true) == true
                    ? sample
                    : sample with { BatteryPercent = null };
            },
            async (bypass, token) =>
            {
                var client = _client;
                if (client is not { IsActive: true } || !IntegrationEnabled)
                {
                    return false;
                }

                var result = await client.Device.SetChargeBypassAsync(bypass,
                    Deadline.After(TimeSpan.FromSeconds(5)), token).ConfigureAwait(false);
                if (!result.Applied)
                {
                    Log.Warn(
                        $"Charge ceiling bypass write stopped: {result.Reason?.Detail ?? result.Outcome.ToString()}.");
                }

                return result.Applied;
            });
        _fanCurves = new DeviceFanCurveController(
            _ =>
            {
                // The handheld's own EC temperature is available outside games and independently
                // of RTSS. Preserve each source's sampling time instead of refreshing a cached value.
                var native = Capabilities.TryGetView(new DeviceCapabilityKey(CapabilityIds.Temperature, null));
                return SelectFanTemperature(native?.Projection.State, metrics(), DateTimeOffset.UtcNow);
            },
            async (instance, duty, token) =>
            {
                var fan = FanDutyView(Capabilities.Snapshot(), instance);
                if (fan is null)
                {
                    return new DeviceFanCurveWriteResult(false, "The fan duty control is unavailable.");
                }

                var result = await ExecuteCapabilityAsync(fan.Descriptor.CapabilityId, fan.Descriptor.InstanceId,
                    CapabilityValue.Integer(duty), TimeSpan.FromSeconds(5), CapabilityCommandOrigin.AutomaticControl,
                    cancellationToken: token).ConfigureAwait(false);
                return new DeviceFanCurveWriteResult(result.Outcome.IsApplied(), result.Reason?.Detail);
            },
            reason =>
            {
                Volatile.Read(ref _client)?.ReportSoftwareFanStatus(reason);
                Log.Change("device.fan-curve", reason ?? "Software fan curve is running.",
                    reason is null ? LogLevel.Info : LogLevel.Warn);
            },
            async (instance, token) =>
            {
                if (!IntegrationEnabled || _client is not { IsActive: true })
                {
                    return;
                }

                var mode = FanModeView(Capabilities.Snapshot(), instance);
                var automatic = mode?.Descriptor.Choices.FirstOrDefault(choice => choice.Value == "automatic");
                if (mode is not null && automatic is not null)
                {
                    var restored = await ExecuteCapabilityAsync(mode.Descriptor.CapabilityId,
                        mode.Descriptor.InstanceId,
                        new CapabilityValue { Kind = CapabilityValueKind.Choice, ChoiceValue = automatic.Value },
                        TimeSpan.FromSeconds(5), CapabilityCommandOrigin.AutomaticControl,
                        cancellationToken: token).ConfigureAwait(false);
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
                var fan = FanDutyView(Capabilities.Snapshot(), instance);
                if (fan is null)
                {
                    throw new InvalidOperationException("The fan fallback control is unavailable.");
                }

                var fallback = await ExecuteCapabilityAsync(fan.Descriptor.CapabilityId, fan.Descriptor.InstanceId,
                    CapabilityValue.Integer(fan.Descriptor.Maximum ?? 100), TimeSpan.FromSeconds(5),
                    CapabilityCommandOrigin.AutomaticControl, cancellationToken: token).ConfigureAwait(false);
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
    private Task ConfigureFanCurveAsync(IReadOnlyList<CurvePoint>? curve, bool explicitSelection,
        (string Instance, bool Software)? change = null, bool stopAll = false)
    {
        var views = Capabilities.Snapshot();
        var fans = views.Where(view => view.Descriptor is
                { Role: Device.Sdk.Capabilities.CapabilityRole.FanDuty, SupportsWrite: true })
            .Select(view => new DeviceFanCurveFan(view.Descriptor.InstanceId ?? string.Empty,
                view.Descriptor.Minimum ?? 0, view.Descriptor.Maximum ?? 100))
            .Where(fan => !stopAll && InSoftwareMode(views, fan.InstanceId, change)).ToArray();
        var table = FanCurvePresets.For(DeviceDefinition?.Id);
        var effective = curve is { Count: > 0 } ? curve : DefaultSoftwareCurve(table);
        _client?.SetSoftwareFans([.. fans.Select(fan => fan.InstanceId)], fans.Length > 0 ? effective : curve ?? []);
        return _fanCurves.ConfigureAsync(fans.Length > 0 ? effective : null, fans,
            IntegrationEnabled && State is DeviceCycleState.Active or DeviceCycleState.Degraded, explicitSelection,
            table.Smoothing);
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
        return views.FirstOrDefault(view => view.Descriptor.Role == Device.Sdk.Capabilities.CapabilityRole.FanDuty
                                            && (view.Descriptor.InstanceId ?? string.Empty) == instance);
    }

    // The mode control of one fan, or the shared one when the device has a single mode for all fans.
    private static DeviceCapabilityView? FanModeView(IReadOnlyList<DeviceCapabilityView> views, string instance)
    {
        var modes = views.Where(view => view.Descriptor.Role == Device.Sdk.Capabilities.CapabilityRole.FanMode)
            .ToArray();
        return modes.FirstOrDefault(view => (view.Descriptor.InstanceId ?? string.Empty) == instance)
               ?? modes.FirstOrDefault(view => view.Descriptor.InstanceId is null);
    }

    private IReadOnlyList<CurvePoint>? SavedSoftwareCurve()
    {
        return Capabilities.TryGetView(new DeviceCapabilityKey(CapabilityIds.FanCurve, null))?.Projection
            .DesiredValue?.CurveValue;
    }

    // The editor's Default preset for this device on the software curve's inputs, until the user saves a curve.
    private static CurvePoint[] DefaultSoftwareCurve(FanCurvePresetTable table)
    {
        return
        [
            .. Enumerable.Range(0, 11).Select(index =>
                new CurvePoint(index * 10, FanCurvePresets.DutyAt(table, FanCurvePreset.Default, index * 10)))
        ];
    }

    private Task ConfigureChargeLimitAsync(int? percent, bool explicitSelection)
    {
        return _chargeLimits.ConfigureAsync(percent,
            _client is { UsesSoftwareChargeLimit: true, IsActive: true } && IntegrationEnabled, explicitSelection);
    }

    private Task ReconcileChargeLimitAsync()
    {
        return _client is { UsesSoftwareChargeLimit: true }
            ? ConfigureChargeLimitAsync(Capabilities
                .TryGetView(new DeviceCapabilityKey(CapabilityIds.ChargeLimit, null))
                ?.Projection.DesiredValue?.IntegerValue, false)
            : _chargeLimits.StopAsync();
    }

    /// <summary>Authored curves for the current exact model and the effective per-game selection.</summary>
    internal (IReadOnlyList<DeviceAuthoredProfile> Profiles, Resolved<string?> Selected)? AuthoredProfileSelection()
    {
        var profiles = ActiveProfileScope()?.Profiles.Where(profile => profile.CapabilityId == CapabilityIds.FanCurve)
            .ToArray();
        return profiles is not { Length: > 0 }
            ? null
            : (profiles, Profiles.Current.Layers.Reference(values => values.FanCurveProfileId));
    }

    internal Task CycleAuthoredProfileAsync(CancellationToken cancellationToken = default)
    {
        var selection = AuthoredProfileSelection();
        return SelectAuthoredProfileAsync(selection is { } current
            ? ProfileEdits.NextAuthoredProfile(current.Profiles.Select(profile => profile.ProfileId).ToArray(),
                current.Selected.Value)
            : null, cancellationToken);
    }

    internal async Task SelectAuthoredProfileAsync(string? next, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var scope = ActiveProfileScope();
        if (scope is null || (next is not null && !scope.Profiles.Any(profile => profile.ProfileId == next
                && profile.CapabilityId == CapabilityIds.FanCurve)))
        {
            return;
        }

        var snapshot = await Profiles.SetAsync(values => values.FanCurveProfileId = next,
            $"fan profile {next ?? "(none)"}", cancellationToken: admission.Token).ConfigureAwait(false);
        await ApplyAuthoredProfilesAsync(snapshot, admission.Token, true).ConfigureAwait(false);
    }

    private async Task ApplyAuthoredProfilesAsync(ProfileSnapshot snapshot, CancellationToken token,
        bool explicitSelection = false)
    {
        if (_disposed)
        {
            return;
        }

        var selected = snapshot.Layers.Reference(values => values.FanCurveProfileId);
        var profile = ActiveProfileScope()?.Profiles.FirstOrDefault(item => item.ProfileId == selected.Value
                                                                            && item.CapabilityId ==
                                                                            CapabilityIds.FanCurve);
        var descriptor = Capabilities.Snapshot().FirstOrDefault(view =>
            view.Descriptor.CapabilityId == CapabilityIds.FanCurve
            && view.Descriptor.InstanceId is null)?.Descriptor;
        // Every WSGM authored curve uses duty control. Native firmware curves remain library operations.
        if (profile is null)
        {
            var desired = Capabilities.TryGetView(new DeviceCapabilityKey(CapabilityIds.FanCurve, null))?.Projection
                .DesiredValue;
            await ConfigureFanCurveAsync(desired?.CurveValue, explicitSelection).ConfigureAwait(false);
            return;
        }

        if (DeviceProfileValidation.Validate(profile, descriptor, out var reason) != DeviceProfileRejection.None)
        {
            Log.Warn($"Fan profile '{profile.ProfileId}' refused: {reason}.");
            await ConfigureFanCurveAsync(null, false, stopAll: true).ConfigureAwait(false);
            return;
        }

        token.ThrowIfCancellationRequested();
        await ConfigureFanCurveAsync(profile.Curve.Select(point => new CurvePoint(point.Input, point.Output)).ToArray(),
            explicitSelection).ConfigureAwait(false);
    }

    private DeviceProfileScope? ActiveProfileScope()
    {
        return DeviceDefinition is { } definition
            ? _config.DeviceIntegration.DeviceProfiles.FirstOrDefault(scope => scope.DeviceDefinitionId == definition.Id
                                                                               && scope.FamilyId == definition.FamilyId)
            : null;
    }
}
