using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Settings;
using WSGM.Overlay;

namespace WSGM.Shell;

/// <summary>Projects one capability view into the row every surface draws.</summary>
/// <remarks>
///     The overlay, Steam's Quick Access and settings pages, the graphics pages and the widgets read the
///     same <see cref="DeviceOverlayCapability" />, so the displayed value, status and invoke rules exist once.
/// </remarks>
internal static class CapabilityRowProjection
{
    internal static DeviceOverlayCapability ProjectManualTdp(DeviceOverlayCapability capability, bool unified)
    {
        return !unified
            ? capability
            : capability.Role switch
            {
                CapabilityRole.PowerSustainedLimit => capability with { Title = "TDP" },
                CapabilityRole.PowerSlowLimit => capability with { Writable = false, CanInvoke = false },
                _ => capability
            };
    }

    /// <summary>Projects one capability view into its row.</summary>
    /// <param name="view">The capability and its current projection.</param>
    /// <param name="declaredSections">Sections the publisher declared, so a row keeps only a section that exists.</param>
    /// <param name="presets">
    ///     The matched model's fan curve presets, as the device coordinator resolved them; a source without a
    ///     handheld passes the default table.
    /// </param>
    /// <param name="layers">Current profile layers for game-override sources, or null when unavailable.</param>
    /// <returns>The row every surface draws.</returns>
    internal static DeviceOverlayCapability ToOverlayCapability(
        DeviceCapabilityView view,
        IReadOnlySet<string> declaredSections,
        FanCurvePresetTable presets,
        ProfileLayers? layers = null)
    {
        ArgumentNullException.ThrowIfNull(presets);
        var descriptor = view.Descriptor;
        var projection = view.Projection;
        var state = projection.State;
        var displayed = projection.PendingValue
                        ?? state.ObservedValue
                        ?? projection.DesiredValue;
        if (descriptor is { ValueKind: CapabilityValueKind.Curve, SupportsWrite: true }
            && descriptor.CurveInputs.Count > 0
            && displayed?.CurveValue is not { Count: > 0 })
        {
            // An editor draft is not an observed firmware setting. Use the device's Default preset until Apply.
            displayed = CapabilityValue.Curve(FanCurvePresets.DefaultCurve(presets, descriptor.CurveInputs,
                descriptor.Minimum ?? 0, descriptor.Maximum ?? 100));
        }
        var actionOnlyReady = state is { Available: true, Reason: null, Quality: HardwareStateQuality.Unknown }
                              && descriptor is { SupportsAction: true, SupportsRead: false };
        var current = CapabilityFreshness.CanCommand(state);
        var next = NextValue(descriptor, displayed);
        // A color that was never read back still opens its editor, starting from white.
        var colorWrite = descriptor.ValueKind is CapabilityValueKind.Color;
        var curveWrite = descriptor.ValueKind is CapabilityValueKind.Curve
                         && displayed?.CurveValue is { Count: > 0 };
        var canInvoke = current
                        && (descriptor.SupportsAction
                            || (descriptor.SupportsWrite && (next is not null || colorWrite || curveWrite)));
        var description = projection.Progress switch
        {
            CommandProgress.Pending => "Applying requested value…",
            CommandProgress.Uncertain => "Last request did not report back",
            CommandProgress.Failed => view.LastResult?.Reason?.Detail ?? "Last request failed",
            _ when projection.DesiredValueOutOfRange =>
                "Saved value is outside the current firmware range",
            _ when state.Reason is not null => state.Reason.Detail,
            // What the action does, as its publisher explains it, while nothing more urgent needs saying.
            _ when descriptor is { SupportsAction: true, Display.Description: { Length: > 0 } explanation } =>
                explanation,
            _ when actionOnlyReady => "Ready",
            // A healthy value has nothing to explain; the row shows it without a caption.
            _ when state.Quality is HardwareStateQuality.Verified or HardwareStateQuality.Observed => string.Empty,
            _ => $"{QualityLabel(state.Quality)} · {PersistenceLabel(descriptor.Persistence)}"
        } ?? "Capability state is unavailable.";
        return new DeviceOverlayCapability(
            descriptor.CapabilityId,
            descriptor.InstanceId,
            SectionFor(descriptor.Role),
            StatusFor(projection),
            CapabilityDisplayLabels.For(descriptor.Display, "Device control"),
            description,
            descriptor.SupportsAction ? "RUN" : FormatValue(displayed, descriptor),
            canInvoke,
            displayed,
            descriptor.SupportsAction ? null : next)
        {
            Role = descriptor.Role,
            PluginSectionId = descriptor.SectionId is { } sectionId
                              && declaredSections.Contains(sectionId)
                ? sectionId
                : null,
            CategoryId = descriptor.SectionId is { } declared
                         && declaredSections.Contains(declared)
                ? descriptor.CategoryId
                : null,
            SortOrder = descriptor.SortOrder,
            OverrideId = OverrideIdFor(view, layers),
            Prominence = descriptor.Prominence,
            LayoutPair = descriptor.LayoutPair,
            ValueKind = descriptor.ValueKind,
            Writable = descriptor.SupportsWrite,
            SupportsAction = descriptor.SupportsAction,
            Minimum = descriptor.Minimum,
            Maximum = descriptor.Maximum,
            Step = descriptor.Step,
            Unit = descriptor.Unit,
            Choices = descriptor.Choices,
            CurveInputs = descriptor.CurveInputs,
            MaximumLength = descriptor.MaximumLength,
            Presets = descriptor.Role == CapabilityRole.FanCurve ? presets : null
        };
    }

    /// <summary>The setting id while the running game's own profile supplies a capability's value.</summary>
    /// <remarks>
    ///     The sustained limit and variable refresh are stored as typed profile values rather than
    ///     device values, so their source is read from those; everything else from the desired value.
    /// </remarks>
    /// <param name="view">Capability with its effective desired-value source.</param>
    /// <param name="layers">Current profile layers for typed power/refresh values, or null when unavailable.</param>
    /// <returns>The setting identifier whose game override can be cleared, or null when no game override supplies it.</returns>
    internal static string? OverrideIdFor(DeviceCapabilityView view, ProfileLayers? layers)
    {
        return view.Descriptor.Role switch
        {
            CapabilityRole.PowerSustainedLimit => layers is { } resolved
                ? CapabilityProjection.OverrideId(resolved, resolved.PowerTargetKey)
                : null,
            CapabilityRole.VariableRefreshRate => CapabilityProjection.OverrideId(layers,
                new ProfileSettingKey(ProfileField.VariableRefreshRate)),
            CapabilityRole.LightingZoneColor when
                layers?.Reference(values => values.LightingProfileId).IsGameOverride == true =>
                nameof(ProfileField.LightingProfile),
            _ => CapabilityProjection.DeviceOverrideId(view)
        };
    }

    /// <summary>Projects the declared overlay sections for presentation, in declared order.</summary>
    /// <param name="sections">
    ///     Publisher sections. The device path passes them through <see cref="DeviceSectionLayout.IncludePredefined" />
    ///     first, as its router already does; the graphics path passes its own declarations only.
    /// </param>
    /// <returns>Sections and categories in sort order with declaration order breaking ties.</returns>
    internal static IReadOnlyList<DeviceOverlayPluginSection> ProjectSections(
        IReadOnlyList<CapabilitySection> sections)
    {
        return
        [
            .. sections
                .Select((section, index) => (Section: section, Index: index))
                .OrderBy(item => item.Section.SortOrder)
                .ThenBy(item => item.Index)
                .Select(item => new DeviceOverlayPluginSection(
                    item.Section.SectionId,
                    item.Section.SectionId switch
                    {
                        DeviceSections.RgbId => "RGB",
                        DeviceSections.InfoId => "Info",
                        _ => SectionTitle(item.Section.Key, item.Section.CustomTitle)
                    },
                    item.Section.CustomDescription ?? SectionDescription(item.Section.Key),
                    item.Section.Icon,
                    [
                        .. item.Section.Categories
                            .Select((category, categoryIndex) => (Category: category, Index: categoryIndex))
                            .OrderBy(entry => entry.Category.SortOrder)
                            .ThenBy(entry => entry.Index)
                            .Select(entry => new DeviceOverlayCategory(
                                entry.Category.CategoryId,
                                SectionTitle(entry.Category.Key, entry.Category.CustomTitle)))
                    ])
                {
                    Key = item.Section.Key
                })
        ];
    }

    /// <summary>The WSGM-owned title behind a section key; custom text is bounded plugin text.</summary>
    private static string SectionTitle(SettingSectionKey key, string? custom)
    {
        return key switch
        {
            SettingSectionKey.Custom => custom ?? "Device",
            SettingSectionKey.General => "General",
            SettingSectionKey.Power => "Power",
            SettingSectionKey.Fans => "Fans",
            SettingSectionKey.Lighting => "Lighting",
            SettingSectionKey.Controller => "Controller",
            SettingSectionKey.Display => "Display",
            SettingSectionKey.Advanced => "Advanced",
            SettingSectionKey.Diagnostics => "Diagnostics",
            _ => key.ToString()
        };
    }

    /// <summary>WSGM's own card description for a keyed section that supplies none.</summary>
    private static string SectionDescription(SettingSectionKey key)
    {
        return key switch
        {
            SettingSectionKey.Power => "Power and performance controls",
            SettingSectionKey.Fans => "Cooling control and readings",
            SettingSectionKey.Lighting => "Device lighting",
            SettingSectionKey.Controller => "Controller, motion, and rumble",
            SettingSectionKey.Display => "Display features",
            SettingSectionKey.Diagnostics => "Health and readings",
            _ => "Device controls"
        };
    }

    private static DeviceOverlaySection SectionFor(CapabilityRole role)
    {
        return role switch
        {
            CapabilityRole.ScenarioMode => DeviceOverlaySection.Overview,
            CapabilityRole.PowerSustainedLimit or CapabilityRole.PowerSlowLimit
                or CapabilityRole.PowerFastLimit or CapabilityRole.PowerPeakLimit
                or CapabilityRole.FanMode or CapabilityRole.FanDuty
                or CapabilityRole.FanTargetRpm or CapabilityRole.FanCurve
                or CapabilityRole.FanMeasuredRpm or CapabilityRole.ChargeLimit
                or CapabilityRole.ChargeProtectionMode or CapabilityRole.ChargeBypass
                or CapabilityRole.Telemetry
                // Variable refresh sits with the frame limit and power controls it interacts with,
                // not among the lighting oddments, because that is where a user goes to change how the
                // device performs.
                or CapabilityRole.VariableRefreshRate => DeviceOverlaySection.PowerAndThermals,
            CapabilityRole.ControllerSource or CapabilityRole.MotionSource
                or CapabilityRole.HapticSink => DeviceOverlaySection.ControllerAndMotion,
            CapabilityRole.OemControl => DeviceOverlaySection.Oem,
            CapabilityRole.LightingPower or CapabilityRole.LightingBrightness
                or CapabilityRole.LightingZoneColor or CapabilityRole.LightingEffect
                or CapabilityRole.LightingEffectSpeed
                or CapabilityRole.GenericToggle or CapabilityRole.GenericRange
                or CapabilityRole.GenericChoice or CapabilityRole.GenericAction
                or CapabilityRole.GenericText
                => DeviceOverlaySection.LightingAndFeatures,
            // A read-only value is something to consult, not to set, so it belongs with the rest of the
            // diagnostics rather than among the controls a user came to change.
            CapabilityRole.GenericReadOnly => DeviceOverlaySection.Diagnostics,
            _ => DeviceOverlaySection.Overview
        };
    }

    private static DescriptorStatus StatusFor(CapabilityProjection projection)
    {
        if (projection.Progress is CommandProgress.Pending)
        {
            return DescriptorStatus.Progress;
        }

        if (projection.Progress is CommandProgress.Failed
            || projection.State.Quality is HardwareStateQuality.Faulted
            || projection.State.Reason?.Code is CapabilityReasonCode.TransportFaulted)
        {
            return DescriptorStatus.Faulted;
        }

        if (projection.Progress is CommandProgress.Uncertain || projection.DesiredValueOutOfRange)
        {
            return DescriptorStatus.Warning;
        }

        if (projection.State.Quality is HardwareStateQuality.Stale
            || projection.State.Reason?.Code is CapabilityReasonCode.ObservationExpired)
        {
            return DescriptorStatus.Stale;
        }

        return projection.State.Reason?.Code switch
        {
            CapabilityReasonCode.ResourceConflict or CapabilityReasonCode.ResourceReleased =>
                DescriptorStatus.ExternallyOwned,
            CapabilityReasonCode.Unsupported
                or CapabilityReasonCode.FirmwareNotVerified
                or CapabilityReasonCode.PrerequisiteMissing => DescriptorStatus.Unsupported,
            _ => projection.State.Available
                ? DescriptorStatus.Available
                : DescriptorStatus.Warning
        };
    }

    private static CapabilityValue? NextValue(
        CapabilityDescriptor descriptor,
        CapabilityValue? current)
    {
        return descriptor.ValueKind switch
        {
            CapabilityValueKind.Boolean => new CapabilityValue
            {
                Kind = CapabilityValueKind.Boolean,
                BooleanValue = !(current?.BooleanValue ?? false)
            },
            CapabilityValueKind.Integer when descriptor is
            {
                Minimum: { } minimum,
                Maximum: { } maximum,
                Step: { } step and > 0
            } => new CapabilityValue
            {
                Kind = CapabilityValueKind.Integer,
                IntegerValue = current?.IntegerValue is { } value && value + step <= maximum
                    ? value + step
                    : minimum
            },
            CapabilityValueKind.Choice when descriptor.Choices.Count > 0 => new CapabilityValue
            {
                Kind = CapabilityValueKind.Choice,
                ChoiceValue = NextChoice(descriptor, current?.ChoiceValue)
            },
            _ => null
        };
    }

    private static string NextChoice(CapabilityDescriptor descriptor, string? current)
    {
        var index = descriptor.Choices.ToList().FindIndex(choice => string.Equals(
            choice.Value,
            current,
            StringComparison.Ordinal));
        return descriptor.Choices[(index + 1) % descriptor.Choices.Count].Value;
    }

    private static string FormatValue(CapabilityValue? value, CapabilityDescriptor descriptor)
    {
        if (value is null)
        {
            return "—";
        }

        var unit = descriptor.Unit;

        return value.Kind switch
        {
            CapabilityValueKind.Boolean => value.BooleanValue is true ? "ON" : "OFF",
            CapabilityValueKind.Integer => value.IntegerValue is { } integer
                ? $"{integer.ToString(CultureInfo.CurrentCulture)}{UnitSuffix(unit)}"
                : "—",
            CapabilityValueKind.Choice => ChoiceLabel(value.ChoiceValue, descriptor),
            CapabilityValueKind.Color => value.ColorValue is { } color
                ? $"#{color:X6}"
                : "—",
            CapabilityValueKind.Curve => value.CurveValue.Count > 0
                ? $"{value.CurveValue.Count} points"
                : "—",
            _ => "RUN"
        };
    }

    /// <summary>The published label of a choice, so a reading shows what the plugin named it, not its id.</summary>
    private static string ChoiceLabel(string? value, CapabilityDescriptor descriptor)
    {
        if (value is null)
        {
            return "—";
        }

        foreach (var choice in descriptor.Choices)
        {
            if (string.Equals(choice.Value, value, StringComparison.Ordinal))
            {
                return CapabilityDisplayLabels.For(choice.Display, value);
            }
        }

        return value;
    }

    /// <summary>What follows a value in a unit, with its leading space where one belongs.</summary>
    /// <param name="unit">Declared measurement unit.</param>
    /// <returns>A formatted suffix with spacing, or empty for units without a supported textual suffix.</returns>
    internal static string UnitSuffix(CapabilityUnit unit)
    {
        return unit switch
        {
            CapabilityUnit.Watt => " W",
            CapabilityUnit.Percent => "%",
            CapabilityUnit.Celsius => " °C",
            CapabilityUnit.Rpm => " RPM",
            CapabilityUnit.Milliampere => " mA",
            CapabilityUnit.Millivolt => " mV",
            CapabilityUnit.Megahertz => " MHz",
            CapabilityUnit.Millisecond => " ms",
            _ => string.Empty
        };
    }

    private static string QualityLabel(HardwareStateQuality quality)
    {
        return quality switch
        {
            HardwareStateQuality.Stale => "Stale",
            HardwareStateQuality.Faulted => "Faulted",
            _ => "Ready"
        };
    }

    private static string PersistenceLabel(CapabilityPersistence persistence)
    {
        return persistence switch
        {
            CapabilityPersistence.Volatile => "resets on device power loss",
            CapabilityPersistence.DevicePersistent => "stored on device",
            _ => "persistence unknown"
        };
    }
}
