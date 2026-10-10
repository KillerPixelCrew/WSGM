using System.Collections.Generic;
using System.Linq;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Settings;
using P = LibHandheld.Contracts;

namespace WSGM.Shell;

/// <summary>WSGM's presentation of native controls. Hardware and input values stay in LibHandheld.</summary>
internal static class HandheldUiProjection
{
    private const string ControlsId = "device-controls";

    private static IReadOnlyList<CapabilitySection> Sections { get; } =
    [
        DeviceSections.Power with
        {
            Categories =
            [
                Category("limits", "Limits", 0), Category("performance", "Performance", 1),
                Category("charging", "Charging", 2), Category("fans", "Fans", 3)
            ]
        },
        DeviceSections.Rgb with { Categories = [Category("zones", "Zones", 0)] },
        DeviceSections.Info with
        {
            Categories = [Category("ownership", "Device ownership", 0), Category("readings", "Readings", 1)]
        },
        new()
        {
            SectionId = ControlsId, Key = SettingSectionKey.Custom, CustomTitle = "Device controls",
            Icon = SectionIcon.Controller, SortOrder = 4
        }
    ];

    internal static CapabilityDescriptorSet Descriptors(P.CapabilityDescriptorSet value)
    {
        return new CapabilityDescriptorSet
        {
            Sections = Sections,
            Descriptors = value.Descriptors.Select(Descriptor).ToArray()
        };
    }

    private static CapabilityDescriptor Descriptor(P.CapabilityDescriptor value)
    {
        var role = (CapabilityRole)value.Role;
        var (section, category, order) = role switch
        {
            CapabilityRole.PowerSustainedLimit => (DeviceSections.PowerId, "limits", 0),
            CapabilityRole.PowerSlowLimit => (DeviceSections.PowerId, "limits", 1),
            CapabilityRole.PowerFastLimit => (DeviceSections.PowerId, "limits", 2),
            CapabilityRole.PowerPeakLimit => (DeviceSections.PowerId, "limits", 3),
            CapabilityRole.ScenarioMode => (DeviceSections.PowerId, "performance", 0),
            CapabilityRole.ChargeLimit or CapabilityRole.ChargeProtectionMode or CapabilityRole.ChargeBypass => (
                DeviceSections.PowerId, "charging", 0),
            CapabilityRole.FanMode => (DeviceSections.PowerId, "fans", 0),
            CapabilityRole.FanCurve => (DeviceSections.PowerId, "fans", 1),
            CapabilityRole.FanDuty or CapabilityRole.FanTargetRpm => (DeviceSections.PowerId, "fans", 2),
            CapabilityRole.FanMeasuredRpm => (DeviceSections.PowerId, "fans", 3),
            CapabilityRole.LightingPower => (DeviceSections.RgbId, null, 0),
            CapabilityRole.LightingBrightness => (DeviceSections.RgbId, null, 1),
            CapabilityRole.LightingEffect => (DeviceSections.RgbId, null, 2),
            CapabilityRole.LightingEffectSpeed => (DeviceSections.RgbId, null, 3),
            CapabilityRole.LightingZoneColor => (DeviceSections.RgbId, "zones",
                value.InstanceId is "right" or "right-ring" ? 1 : 0),
            CapabilityRole.ControllerSource => (DeviceSections.InfoId, "ownership", 0),
            CapabilityRole.MotionSource => (DeviceSections.InfoId, "ownership", 1),
            CapabilityRole.HapticSink => (DeviceSections.InfoId, "ownership", 2),
            CapabilityRole.VariableRefreshRate => (DeviceSections.PowerId, "performance", 1),
            _ when value.SupportsWrite || value.SupportsAction => (ControlsId, null, 0),
            _ => (DeviceSections.InfoId, "readings", 0)
        };
        return new CapabilityDescriptor
        {
            CapabilityId = value.CapabilityId, InstanceId = value.InstanceId, Role = role,
            ValueKind = (CapabilityValueKind)value.ValueKind, Display = Display(value.Display),
            SupportsRead = value.SupportsRead, SupportsWrite = value.SupportsWrite,
            SupportsAction = value.SupportsAction,
            Minimum = value.Minimum, Maximum = value.Maximum, Step = value.Step, Unit = (CapabilityUnit)value.Unit,
            PairedPowerLimitId = value.PairedPowerLimitId,
            Choices = value.Choices.Select(choice => new CapabilityChoice(choice.Value, Display(choice.Display)))
                .ToArray(),
            PowerPresets = value.PowerPresets.Select(preset => new DevicePowerPreset(preset.Id, preset.Name,
                    preset.SustainedWatts, preset.SlowWatts, (DevicePowerMode)preset.WindowsMode)
                { ScenarioOnAc = preset.ScenarioOnAc, ScenarioOnDc = preset.ScenarioOnDc }).ToArray(),
            MaximumLength = value.MaximumLength, AvailableOnAc = value.AvailableOnAc,
            AvailableOnDc = value.AvailableOnDc,
            Persistence = (CapabilityPersistence)value.Persistence,
            ProfileScope = (CapabilityProfileScope)value.ProfileScope,
            ApplyTiming = (CapabilityApplyTiming)value.ApplyTiming, SectionId = section, CategoryId = category,
            SortOrder = order,
            Prominence = role is CapabilityRole.PowerSustainedLimit or CapabilityRole.ScenarioMode
                ? CapabilityProminence.Primary
                : role is CapabilityRole.ControllerSource or CapabilityRole.MotionSource or CapabilityRole.HapticSink
                  || value.SupportsWrite || value.SupportsAction
                    ? CapabilityProminence.Normal
                    : CapabilityProminence.Compact,
            LayoutPair = role == CapabilityRole.PowerSustainedLimit && value.PairedPowerLimitId is { } pair
                ? new CapabilityLayoutPair(pair)
                : null
        };
    }

    internal static CapabilityDisplay Display(P.CapabilityDisplay display)
    {
        return new CapabilityDisplay
        {
            Key = (DisplayKey)display.Key, CustomLabel = display.CustomText
        };
    }

    internal static CapabilityState State(P.CapabilityState value)
    {
        return new CapabilityState
        {
            CapabilityId = value.CapabilityId, InstanceId = value.InstanceId, Available = value.Available,
            Reason = value.Reason is null ? null : Reason(value.Reason),
            ObservedValue = value.ObservedValue is null ? null : Value(value.ObservedValue),
            Quality = (HardwareStateQuality)value.Quality, ObservedAt = value.ObservedAt
        };
    }

    internal static CapabilityValue Value(P.CapabilityValue value)
    {
        return new CapabilityValue
        {
            Kind = (CapabilityValueKind)value.Kind, BooleanValue = value.BooleanValue,
            IntegerValue = value.IntegerValue,
            ChoiceValue = value.ChoiceValue, ColorValue = value.ColorValue, TextValue = value.TextValue,
            CurveValue = value.CurveValue.Select(point => new CurvePoint(point.Input, point.Output)).ToArray()
        };
    }

    internal static P.CapabilityValue NativeValue(CapabilityValue value)
    {
        return new P.CapabilityValue
        {
            Kind = (P.CapabilityValueKind)value.Kind, BooleanValue = value.BooleanValue,
            IntegerValue = value.IntegerValue,
            ChoiceValue = value.ChoiceValue, ColorValue = value.ColorValue, TextValue = value.TextValue,
            CurveValue = value.CurveValue.Select(point => new P.CurvePoint(point.Input, point.Output)).ToArray()
        };
    }

    internal static CapabilityReason Reason(P.CapabilityReason value)
    {
        return new CapabilityReason((CapabilityReasonCode)value.Code, value.Detail, value.Retryable);
    }

    internal static CommandOutcome Outcome(P.CommandOutcome outcome)
    {
        return outcome switch
        {
            P.CommandOutcome.Accepted => CommandOutcome.Accepted,
            P.CommandOutcome.Applied => CommandOutcome.Applied,
            P.CommandOutcome.Rejected => CommandOutcome.Rejected,
            P.CommandOutcome.TimedOut => CommandOutcome.TimedOut,
            _ => CommandOutcome.Indeterminate
        };
    }

    internal static CapabilityCommandResult Result(P.CapabilityCommandResult result)
    {
        return new CapabilityCommandResult
        {
            CommandId = result.CommandId, Outcome = Outcome(result.Outcome),
            Reason = result.Reason is null ? null : Reason(result.Reason), CompletedAt = result.CompletedAt
        };
    }

    private static CapabilityCategory Category(string id, string title, int order)
    {
        return new CapabilityCategory
        {
            CategoryId = id, Key = SettingSectionKey.Custom, CustomTitle = title, SortOrder = order
        };
    }
}
