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
        if (view is null || !DeviceCapabilityRouter.CanCommand(view.Projection.State))
        {
            return new SteamUiCommandResult(false, "That device control changed or is unavailable. Reopen the panel.");
        }

        CapabilityValue? requested = null;
        if (!view.Descriptor.SupportsAction)
        {
            if (!view.Descriptor.SupportsWrite || !TryReadControlValue(value, view.Descriptor, out requested))
            {
                return new SteamUiCommandResult(false, "The value is outside the device's current descriptor.");
            }

            if (requested.Kind == CapabilityValueKind.Curve)
            {
                var current = DeviceOverlayBridge.ToOverlayCapability(view, new HashSet<string>()).CurrentValue;
                if (current?.Kind != CapabilityValueKind.Curve
                    || !current.CurveValue.Select(point => point.Input)
                        .SequenceEqual(requested.CurveValue.Select(point => point.Input)))
                {
                    return new SteamUiCommandResult(false, "The fan curve's firmware breakpoints changed.");
                }
            }
        }

        var result = await _coordinator!.ExecuteCapabilityAsync(view.Descriptor.CapabilityId,
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

    internal static bool TryReadControlValue(JsonElement value, CapabilityDescriptor descriptor,
        out CapabilityValue requested)
    {
        requested = CapabilityValue.None();
        switch (descriptor.ValueKind)
        {
            case CapabilityValueKind.Boolean when value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                requested = new CapabilityValue { Kind = CapabilityValueKind.Boolean, BooleanValue = value.GetBoolean() };
                break;
            case CapabilityValueKind.Integer when value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number):
                requested = CapabilityValue.Integer(number);
                break;
            case CapabilityValueKind.Choice when value.ValueKind == JsonValueKind.String:
                requested = CapabilityValue.Choice(value.GetString()!);
                break;
            case CapabilityValueKind.Text when value.ValueKind == JsonValueKind.String:
                requested = new CapabilityValue { Kind = CapabilityValueKind.Text, TextValue = value.GetString() };
                break;
            case CapabilityValueKind.Color when value.ValueKind == JsonValueKind.String:
                var colorText = value.GetString();
                if (colorText is null || colorText.Length != 7 || colorText[0] != '#'
                    || !int.TryParse(colorText.AsSpan(1), System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out var color))
                {
                    return false;
                }

                requested = new CapabilityValue { Kind = CapabilityValueKind.Color, ColorValue = color };
                break;
            case CapabilityValueKind.Curve when value.ValueKind == JsonValueKind.Array && value.GetArrayLength() <= 128:
                List<CurvePoint> points = [];
                foreach (var point in value.EnumerateArray())
                {
                    if (point.ValueKind != JsonValueKind.Object || !point.TryGetProperty("input", out var input)
                        || input.ValueKind != JsonValueKind.Number || !input.TryGetInt32(out var temperature)
                        || !point.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Number
                        || !output.TryGetInt32(out var duty) || point.EnumerateObject().Count() != 2)
                    {
                        return false;
                    }

                    points.Add(new CurvePoint(temperature, duty));
                }

                requested = CapabilityValue.Curve(points);
                break;
            default:
                return false;
        }

        return CapabilityValueValidation.ValueMatches(requested, descriptor, out _);
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

        var matches = PrimaryRanges(_coordinator.Capabilities.Snapshot(), role);
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

        List<SteamSettingsRow> controls = [];
        foreach (var view in views)
        {
            var descriptor = view.Descriptor;
            var key = controlKey?.Invoke(view) ?? view.SettingKey.Id;
            var legacy = descriptor.Role is CapabilityRole.ChargeLimit or CapabilityRole.LightingBrightness
                         && PrimaryRanges(views, descriptor.Role) is [var primary]
                         && ReferenceEquals(primary.Descriptor, descriptor)
                         && WritableRange(view, descriptor.Role, out _, out _, out _)
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

    private static SteamSettingsRow ProjectControl(DeviceCapabilityView view, string key, FanCurvePresetTable presets)
    {
        var row = DeviceOverlayBridge.ToOverlayCapability(view, new HashSet<string>(), presets: presets);
        var descriptor = view.Descriptor;
        var kind = !descriptor.SupportsWrite && !descriptor.SupportsAction ? SteamSettingsRowKind.Note
            : descriptor.SupportsAction ? SteamSettingsRowKind.Action
            : descriptor.ValueKind switch
            {
                CapabilityValueKind.Integer => SteamSettingsRowKind.Range,
                CapabilityValueKind.Boolean => SteamSettingsRowKind.Boolean,
                CapabilityValueKind.Choice => SteamSettingsRowKind.Choice,
                CapabilityValueKind.Text => SteamSettingsRowKind.Text,
                CapabilityValueKind.Color => SteamSettingsRowKind.Color,
                CapabilityValueKind.Curve => SteamSettingsRowKind.Curve,
                _ => SteamSettingsRowKind.Note
            };
        var curve = row.CurrentValue?.CurveValue ?? [];
        var title = row.Title + (descriptor.InstanceId is { } instance ? $" ({instance})" : string.Empty);
        var description = row.OverrideId is null ? row.Description : NativeQamLayout.AccentDescription(row.Description);
        return new SteamSettingsRow(key, kind, title, description,
            Checked: row.CurrentValue?.BooleanValue,
            Text: kind == SteamSettingsRowKind.Note ? row.TrailingText
                : kind == SteamSettingsRowKind.Color ? $"#{row.CurrentValue?.ColorValue ?? 0xFFFFFF:X6}"
                : row.CurrentValue?.ChoiceValue ?? row.CurrentValue?.TextValue,
            Number: row.CurrentValue?.IntegerValue,
            Choices: descriptor.Choices.Select(choice => new SteamSettingsChoice(choice.Value,
                CapabilityDisplayLabels.For(choice.Display, choice.Value))).ToArray(),
            Minimum: descriptor.Minimum, Maximum: descriptor.Maximum, Step: descriptor.Step,
            Suffix: descriptor.Unit switch
            {
                CapabilityUnit.Watt => " W",
                CapabilityUnit.Percent => "%",
                CapabilityUnit.Celsius => " °C",
                CapabilityUnit.Rpm => " RPM",
                CapabilityUnit.Milliampere => " mA",
                CapabilityUnit.Millivolt => " mV",
                CapabilityUnit.Megahertz => " MHz",
                CapabilityUnit.Millisecond => " ms",
                _ => null
            },
            MaximumLength: descriptor.MaximumLength,
            Disabled: !DeviceCapabilityRouter.CanCommand(view.Projection.State)
                      || view.Projection.Progress == CommandProgress.Pending
                      || kind == SteamSettingsRowKind.Curve && curve.Count == 0,
            ButtonLabel: "Run", Accent: row.OverrideId is not null, ColorAlpha: false,
            Curve: curve.Select(point => new SteamSettingsCurvePoint(point.Input, point.Output)).ToArray(),
            CurvePresets: descriptor.Role == CapabilityRole.FanCurve
                ? Enum.GetValues<FanCurvePreset>().Select(preset => new SteamSettingsCurvePreset(
                    FanCurvePresets.Label(preset), FanCurvePresets.SampleOnto(presets, preset, curve)
                        .Select(point => new SteamSettingsCurvePoint(point.Input, point.Output)).ToArray())).ToArray()
                : null);
    }

    private static DeviceCapabilityView[] PrimaryRanges(IReadOnlyList<DeviceCapabilityView> views, CapabilityRole role)
    {
        var all = views.Where(view => view.Descriptor.Role == role).ToArray();
        var primary = all.Where(view => view.Descriptor.InstanceId is null).ToArray();
        return primary.Length > 0 ? primary : all;
    }

    private static SteamDeviceRangeState? ProjectUniqueRange(
        IReadOnlyList<DeviceCapabilityView> views,
        CapabilityRole role)
    {
        var matches = PrimaryRanges(views, role);
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
