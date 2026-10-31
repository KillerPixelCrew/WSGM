using System;
using System.Collections.Generic;
using System.Linq;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>Structural and semantic validation applied before plugin data enters WSGM state.</summary>
internal static class DeviceCapabilityValidation
{
    /// <summary>Validates unique identities, layout, power presets, and power-pair structure.</summary>
    /// <param name="set">Complete replacement descriptor set supplied by the current publisher.</param>
    /// <param name="error">Null on success, otherwise the first structural or semantic rejection reason.</param>
    /// <returns>Whether the set may enter host state; this does not test hardware support or native transport.</returns>
    internal static bool TryValidateDescriptorSet(
        CapabilityDescriptorSet set,
        out string? error)
    {
        var sections = DeviceSections.All.ToDictionary(section => section.SectionId, StringComparer.Ordinal);
        HashSet<string> declaredIds = new(StringComparer.Ordinal);
        foreach (var section in set.Sections)
        {
            // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
            if (section is null)
            {
                error = "Descriptor set contains a null section.";
                return false;
            }

            if (!section.TryValidate(out error))
            {
                return false;
            }

            if (!declaredIds.Add(section.SectionId))
            {
                error = $"Descriptor set declares section '{section.SectionId}' more than once.";
                return false;
            }

            sections[section.SectionId] = section;
        }

        HashSet<DeviceCapabilityKey> keys = [];
        foreach (var descriptor in set.Descriptors)
        {
            if (TryValidateDescriptor(descriptor, out error)
                && TryValidatePlacement(descriptor, sections, out error)
                && keys.Add(new DeviceCapabilityKey(
                    descriptor.CapabilityId,
                    descriptor.InstanceId)))
            {
                continue;
            }

            error ??= "Descriptor keys are duplicated.";
            return false;
        }

        return CapabilityLayout.TryValidate(set.Descriptors, out error)
               && DevicePowerPreset.TryValidate(set.Descriptors, out error)
               && DevicePowerPair.TryValidate(set.Descriptors, out error);
    }

    /// <summary>Checks identity and observed-value compatibility before accepting a capability state.</summary>
    /// <param name="state">Candidate observation; verified quality requires a nonnull readback value.</param>
    /// <param name="descriptor">Accepted descriptor defining the value shape and range.</param>
    /// <param name="error">Null on success, otherwise the rejection reason.</param>
    /// <returns>Whether these structural checks pass; the publisher still owns the evidence behind its quality.</returns>
    internal static bool TryValidateState(
        CapabilityState state,
        CapabilityDescriptor descriptor,
        out string? error)
    {
        if (state.ObservedValue is not null
            && !CapabilityValueValidation.ValueMatches(state.ObservedValue, descriptor, out error))
        {
            return false;
        }

        if (state.Quality is HardwareStateQuality.Verified && state.ObservedValue is null)
        {
            error = "Verified state must carry a readback value.";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>Checks a descriptor's section and category references against the declared layout.</summary>
    /// <remarks>
    ///     A section declared in the set is the plugin authoring its own overlay surface, so any role
    ///     may be placed there. Outside that layout the old rule stands: a semantic role keeps the home
    ///     WSGM gives it, and only a generic role may name a settings-manifest section — an unknown id
    ///     there falls back to a WSGM-owned group instead of failing, which is why it is not an error.
    /// </remarks>
    private static bool TryValidatePlacement(
        CapabilityDescriptor descriptor,
        Dictionary<string, CapabilitySection> sections,
        out string? error)
    {
        CapabilitySection? home = null;
        if (descriptor.SectionId is { } sectionId
            && !sections.TryGetValue(sectionId, out home))
        {
            if (!descriptor.Role.IsGeneric())
            {
                // Named in the error, because from the plugin author's side this looks like a
                // section that was simply ignored.
                error =
                    $"Capability role {descriptor.Role} may not declare the undeclared section "
                    + $"'{sectionId}': a semantic role keeps the placement WSGM gives it on every "
                    + "device unless the descriptor set declares the layout.";
                return false;
            }

            home = null;
        }

        if (descriptor.CategoryId is { } categoryId
            && (home is null
                || !home.Categories.Any(category => string.Equals(
                    category.CategoryId,
                    categoryId,
                    StringComparison.Ordinal))))
        {
            error =
                $"Capability '{descriptor.CapabilityId}' names category '{categoryId}' that its "
                + "declared section does not carry.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryValidateDescriptor(CapabilityDescriptor descriptor, out string? error)
    {
        if (!PlainText.IsIdentifier(descriptor.CapabilityId)
            || (descriptor.InstanceId is not null && !PlainText.IsIdentifier(descriptor.InstanceId)))
        {
            error = "Capability or instance ID is invalid.";
            return false;
        }

        if (!descriptor.Display.TryValidate(out error))
        {
            return false;
        }

        if (descriptor.SectionId is { } sectionId
            && !PlainText.IsIdentifier(sectionId))
        {
            error = "Capability section ID is invalid.";
            return false;
        }

        if (descriptor.CategoryId is { } categoryId
            && !PlainText.IsIdentifier(categoryId))
        {
            error = "Capability category ID is invalid.";
            return false;
        }

        if (descriptor is { SupportsRead: false, SupportsWrite: false, SupportsAction: false })
        {
            error = "Descriptor exposes no readable, writable, or actionable operation.";
            return false;
        }

        if (descriptor.ValueKind is CapabilityValueKind.None != descriptor.SupportsAction
            || (descriptor.ValueKind is CapabilityValueKind.None
                && (descriptor.SupportsRead || descriptor.SupportsWrite)))
        {
            error = "Action and value-bearing descriptor shapes are inconsistent.";
            return false;
        }

        // ReSharper disable once SwitchStatementMissingSomeEnumCasesNoDefault
        switch (descriptor.ValueKind)
        {
            case CapabilityValueKind.Integer
                when descriptor.Minimum is null
                     || descriptor.Maximum is null
                     || descriptor.Minimum > descriptor.Maximum
                     || descriptor.Step is null or <= 0:
                error = "Integer descriptors require an ordered range and positive step.";
                return false;
            case CapabilityValueKind.Choice
                when descriptor.Choices.Count is 0
                     || descriptor.Choices.Any(choice => !PlainText.IsIdentifier(choice.Value))
                     || descriptor.Choices.Select(choice => choice.Value).Distinct(StringComparer.Ordinal)
                         .Count() != descriptor.Choices.Count:
                error = "Choice descriptor values are empty, invalid, or duplicated.";
                return false;
        }

        if (descriptor.ValueKind is not CapabilityValueKind.Choice && descriptor.Choices.Count != 0)
        {
            error = "Only choice descriptors may carry choices.";
            return false;
        }

        // Text is the one value shape with no natural bound, so the descriptor must supply one.
        // Without this a plugin could publish a text capability whose value is unbounded, which is
        // exactly the case PlainText exists to prevent.
        if (descriptor.ValueKind is CapabilityValueKind.Text
            && descriptor.MaximumLength is not > 0)
        {
            error = "Text descriptors require a positive maximumLength.";
            return false;
        }

        if (descriptor.ValueKind is not CapabilityValueKind.Text && descriptor.MaximumLength is not null)
        {
            error = "Only text descriptors may carry a maximumLength.";
            return false;
        }

        if (!RoleMatchesValueKind(descriptor.Role, descriptor.ValueKind))
        {
            error = "Capability role and value kind are inconsistent.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool RoleMatchesValueKind(CapabilityRole role, CapabilityValueKind kind)
    {
        return role switch
        {
            CapabilityRole.FanCurve => kind is CapabilityValueKind.Curve,
            CapabilityRole.GenericAction => kind is CapabilityValueKind.None,
            CapabilityRole.GenericToggle
                or CapabilityRole.LightingPower
                or CapabilityRole.VariableRefreshRate => kind is CapabilityValueKind.Boolean,
            CapabilityRole.ChargeBypass or CapabilityRole.ChargeProtectionMode =>
                kind is CapabilityValueKind.Boolean or CapabilityValueKind.Choice,
            CapabilityRole.GenericChoice
                or CapabilityRole.ScenarioMode
                or CapabilityRole.PowerBackend
                or CapabilityRole.FanMode
                or CapabilityRole.LightingEffect
                or CapabilityRole.ControllerSource
                or CapabilityRole.MotionSource => kind is CapabilityValueKind.Choice,
            CapabilityRole.LightingZoneColor => kind is CapabilityValueKind.Color,
            CapabilityRole.PowerSustainedLimit
                or CapabilityRole.PowerSlowLimit
                or CapabilityRole.PowerFastLimit
                or CapabilityRole.PowerPeakLimit
                or CapabilityRole.FanDuty
                or CapabilityRole.FanTargetRpm
                or CapabilityRole.FanMeasuredRpm
                or CapabilityRole.ChargeLimit
                or CapabilityRole.LightingBrightness
                or CapabilityRole.LightingEffectSpeed
                or CapabilityRole.GenericRange => kind is CapabilityValueKind.Integer,
            CapabilityRole.OemControl or CapabilityRole.HapticSink => kind is CapabilityValueKind.None,
            CapabilityRole.GenericText => kind is CapabilityValueKind.Text,
            CapabilityRole.Telemetry or CapabilityRole.GenericReadOnly =>
                kind is CapabilityValueKind.Boolean
                    or CapabilityValueKind.Integer
                    or CapabilityValueKind.Choice
                    // A read-only string — a firmware revision, a mode name the device reports.
                    or CapabilityValueKind.Text,
            _ => true
        };
    }
}
