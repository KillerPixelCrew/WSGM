using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>Projects every device descriptor and charging/lighting controls into Quick Settings.</summary>
/// <remarks>
///     The projection selects capabilities by their SDK semantic roles, never by a device package's
///     private ids. Commands re-resolve the descriptor at execution time and pass through the same
///     coordinator validation and readback path as the overlay and profiles. Publication follows the
///     coordinator's capability changes, which the Steam UI host hears once for every device row.
/// </remarks>
internal sealed class DeviceCoordinatorNativeQamDeviceControlsService : ISteamDeviceControlsBackend
{
    // Quick Access lists every control in one section, so no plugin section is declared to it.
    private static readonly IReadOnlySet<string> NoSections = new HashSet<string>();

    private readonly DeviceCoordinator? _coordinator;
    private readonly Lock _controlGate = new();
    private readonly Dictionary<CapabilityDescriptor, string> _keys = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, CapabilityDescriptor> _controls = new(StringComparer.Ordinal);
    private string? _published;
    private long _revision;
    private ActiveProfile? _activeProfile;

    /// <summary>Creates a capability projection over the existing device owner.</summary>
    /// <param name="coordinator">Borrowed coordinator, or null when device integration is inactive.</param>
    internal DeviceCoordinatorNativeQamDeviceControlsService(DeviceCoordinator? coordinator)
    {
        _coordinator = coordinator;
    }

    /// <summary>Projects the current capability snapshot without reading hardware; absent roles remain unavailable.</summary>
    public SteamDeviceControlsState Current
    {
        get
        {
            var views = _coordinator?.PresentedCapabilities() ?? [];
            lock (_controlGate)
            {
                var activeProfile = _coordinator?.Profiles.Current.Active;
                if (_activeProfile != activeProfile)
                {
                    _keys.Clear();
                    _controls.Clear();
                    _activeProfile = activeProfile;
                }

                var active = views.Select(view => view.Descriptor).ToHashSet(ReferenceEqualityComparer.Instance);
                foreach (var descriptor in _keys.Keys.Where(descriptor => !active.Contains(descriptor)).ToArray())
                {
                    _controls.Remove(_keys[descriptor]);
                    _keys.Remove(descriptor);
                }

                var state = Project(views, view =>
                {
                    if (!_keys.TryGetValue(view.Descriptor, out var key))
                    {
                        key = Guid.NewGuid().ToString("N");
                        _keys.Add(view.Descriptor, key);
                        _controls.Add(key, view.Descriptor);
                    }

                    return key;
                }, FanCurvePresets.For(_coordinator?.DeviceDefinition?.Id));
                var content = SteamDeviceControlsRow.Serialize(state).GetRawText();
                if (!string.Equals(content, _published, StringComparison.Ordinal))
                {
                    _published = content;
                    _revision++;
                }

                return state with { Revision = _revision };
            }
        }
    }

    /// <inheritdoc />
    public async Task<SteamUiCommandResult> SetControlAsync(string key, JsonElement value,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var view = ResolveControl(key);
        var row = view is null
            ? null
            : CapabilityRowProjection.ToOverlayCapability(view, NoSections,
                presets: FanCurvePresets.For(_coordinator?.DeviceDefinition?.Id));
        if (view is null || row is not { CanInvoke: true })
        {
            return new SteamUiCommandResult(false, "That device control changed or is unavailable. Reopen the panel.");
        }

        if (!SteamCapabilityRows.TryValue(row, value, out var requested))
        {
            return new SteamUiCommandResult(false, "The value is outside the device's current descriptor.");
        }

        var result = await _coordinator!.Commands.ExecuteAsync(view.Descriptor.CapabilityId,
            view.Descriptor.InstanceId, requested, NativeQamUi.CommandTimeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return NativeQamUi.CommandResult(result, $"The device command ended as {result.Outcome}.");
    }

    private DeviceCapabilityView? ResolveControl(string key)
    {
        lock (_controlGate)
        {
            return _activeProfile == _coordinator?.Profiles.Current.Active
                   && _controls.TryGetValue(key, out var descriptor)
                ? _coordinator?.Capabilities.Snapshot().SingleOrDefault(view =>
                    ReferenceEquals(view.Descriptor, descriptor))
                : null;
        }
    }

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
            return await ExecuteAsync(matches[0].Descriptor.CapabilityId, matches[0].Descriptor.InstanceId,
                CapabilityValue.Color(color), cancellationToken).ConfigureAwait(false);
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

        var range = IntegerRangeProjection.Percent(_coordinator.Capabilities.Snapshot(), role);
        if (range is not { Available: true, CapabilityId: { } capabilityId })
        {
            Log.Warn($"Native QAM device range refused: role={role}, status='{range.StatusText}'.");
            return new SteamUiCommandResult(false, "That device control is unavailable or incompatible.");
        }

        if (range is not { Minimum: { } minimum, Maximum: { } maximum, Step: { } step }
            || CapabilityProjection.ValidInteger(CapabilityValue.Integer(value), minimum, maximum, step) is null)
        {
            return new SteamUiCommandResult(false, "The value is outside the device's current descriptor.");
        }

        return await ExecuteAsync(capabilityId, range.InstanceId, CapabilityValue.Integer(value), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<SteamUiCommandResult> ExecuteAsync(
        string capabilityId,
        string? instanceId,
        CapabilityValue value,
        CancellationToken cancellationToken)
    {
        var result = await _coordinator!.Commands.ExecuteAsync(
            capabilityId,
            instanceId,
            value,
            NativeQamUi.CommandTimeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return NativeQamUi.CommandResult(result, $"The device command ended as {result.Outcome}.");
    }

    /// <summary>Projects device descriptors alongside semantic charge, brightness and lighting-zone controls.</summary>
    /// <param name="views">Current device capability views; ambiguous role/instance matches are not writable.</param>
    /// <param name="controlKey">Optional descriptor identity allocator; the live service supplies generation-scoped keys.</param>
    /// <param name="presets">The device's fan curve preset shapes; HandheldCompanion's when omitted.</param>
    /// <returns>Native range/zone state and settings descriptors sharing the Overlay's values, status and profile overrides.</returns>
    internal static SteamDeviceControlsState Project(
        IReadOnlyList<DeviceCapabilityView> views,
        Func<DeviceCapabilityView, string>? controlKey = null,
        FanCurvePresetTable? presets = null)
    {
        var table = presets ?? FanCurvePresetTable.HandheldCompanion;
        var chargeRange = IntegerRangeProjection.Percent(views, CapabilityRole.ChargeLimit);
        var brightnessRange = IntegerRangeProjection.Percent(views, CapabilityRole.LightingBrightness);
        var charge = RangeState(views, CapabilityRole.ChargeLimit, chargeRange);
        var brightness = RangeState(views, CapabilityRole.LightingBrightness, brightnessRange);
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
                        // The overlay row's value; white until something was read or written, as its editor starts.
                        ValidColor(CapabilityRowProjection.ToOverlayCapability(view, NoSections, presets: table)
                            .CurrentValue) ?? 0xFFFFFF,
                        NativeQamUi.ProgressText(view.Projection.Progress),
                        StatusText(view, compatible),
                        CapabilityProjection.DeviceOverrideId(view) is not null);
                })
        ];

        List<SteamSettingsRow> controls = [];
        foreach (var view in views)
        {
            var descriptor = view.Descriptor;
            var key = controlKey?.Invoke(view) ?? view.SettingKey.Id;
            var legacy = descriptor.Role is CapabilityRole.ChargeLimit && Covers(chargeRange, descriptor)
                         || descriptor.Role is CapabilityRole.LightingBrightness && Covers(brightnessRange, descriptor)
                         || descriptor.Role == CapabilityRole.LightingZoneColor
                         && zones.Any(zone => zone.Id == descriptor.InstanceId);
            if (!legacy)
            {
                controls.Add(ProjectControl(view, key, table));
            }
        }

        return new SteamDeviceControlsState(charge, brightness, zones,
            controls.Count == 0 ? [] : [new SteamSettingsSection("Device controls", controls, "deviceControls")]);
    }

    // The shared Steam row; Quick Access names the instance in its one flat list and holds a row while it applies.
    private static SteamSettingsRow ProjectControl(DeviceCapabilityView view, string key, FanCurvePresetTable presets)
    {
        var row = SteamCapabilityRows.Row(
            CapabilityRowProjection.ToOverlayCapability(view, NoSections, presets: presets), key);
        return row with
        {
            Label = row.Label + (view.Descriptor.InstanceId is { } instance ? $" ({instance})" : string.Empty),
            Disabled = row.Disabled || view.Projection.Progress == CommandProgress.Pending
        };
    }

    // Whether Quick Access's own slider already draws this control.
    private static bool Covers(IntegerRangeProjection range, CapabilityDescriptor descriptor)
    {
        return range.Available
               && range.CapabilityId == descriptor.CapabilityId
               && range.InstanceId == descriptor.InstanceId;
    }

    private static SteamDeviceRangeState? RangeState(
        IReadOnlyList<DeviceCapabilityView> views,
        CapabilityRole role,
        IntegerRangeProjection range)
    {
        if (views.All(view => view.Descriptor.Role != role))
        {
            return null;
        }

        return range is { Available: true, Minimum: { } minimum, Maximum: { } maximum, Step: { } step }
            ? new SteamDeviceRangeState(
                true,
                minimum,
                maximum,
                step,
                range.Desired,
                // A write-only charge limit or brightness still gets its row, starting from the maximum.
                range.Observed ?? range.Desired ?? maximum,
                range.Progress,
                range.StatusText,
                range.GameOverride)
            : new SteamDeviceRangeState(
                false, 0, 100, 1, null, null, string.Empty, IntegerRangeProjection.Incompatible(role));
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
               && CapabilityFreshness.CanCommand(state);
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
