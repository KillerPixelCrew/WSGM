using System;
using System.Linq;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Plugin.Sdk;

namespace WSGM.Core;

/// <summary>Pure capability value comparisons and conversions shared by device and graphics paths.</summary>
internal static class CapabilityValues
{
    /// <summary>Compares two capability values, including curves, by content.</summary>
    /// <param name="observed">What the device reports.</param>
    /// <param name="desired">What WSGM wants.</param>
    /// <returns><see langword="true" /> when a write would change nothing.</returns>
    internal static bool Same(CapabilityValue observed, CapabilityValue desired)
    {
        if (observed.Kind != desired.Kind)
        {
            return false;
        }

        // Field by field rather than record equality: CurveValue is compared by reference there,
        // which would report every curve as different and rewrite a fan table on each pass.
        return observed.Kind switch
        {
            CapabilityValueKind.Boolean => observed.BooleanValue == desired.BooleanValue,
            CapabilityValueKind.Integer => observed.IntegerValue == desired.IntegerValue,
            CapabilityValueKind.Choice => string.Equals(
                observed.ChoiceValue,
                desired.ChoiceValue,
                StringComparison.Ordinal),
            CapabilityValueKind.Color => observed.ColorValue == desired.ColorValue,
            CapabilityValueKind.Curve => observed.CurveValue.SequenceEqual(desired.CurveValue),
            _ => false
        };
    }

    /// <summary>Converts only the primitive declared by a fresh descriptor and validates its current bounds.</summary>
    /// <param name="value">A serializable primitive from a widget or a saved preference.</param>
    /// <param name="descriptor">The capability's current descriptor.</param>
    /// <returns>The matching capability value, or <see langword="null" /> when it does not fit the descriptor.</returns>
    internal static CapabilityValue? FromPluginValue(PluginValue value, CapabilityDescriptor descriptor)
    {
        if (!value.IsValid)
        {
            return null;
        }

        var converted = descriptor.ValueKind switch
        {
            CapabilityValueKind.Boolean when value.Boolean is { } flag => CapabilityValue.Boolean(flag),
            CapabilityValueKind.Integer when value.Number is { } number && number >= int.MinValue
                                                                        && number <= int.MaxValue
                                                                        && Math.Truncate(number) == number =>
                CapabilityValue.Integer((int)number),
            CapabilityValueKind.Choice when value.Text is { } choice => CapabilityValue.Choice(choice),
            _ => null
        };
        return converted is not null && CapabilityValueValidation.ValueMatches(converted, descriptor, out _)
            ? converted
            : null;
    }

    /// <summary>Copies a boolean, integer or choice observation into its serializable primitive.</summary>
    /// <param name="value">The observation, if any.</param>
    /// <returns>The primitive, or <see langword="null" /> for a missing value or another kind.</returns>
    internal static PluginValue? ToPluginValue(CapabilityValue? value)
    {
        if (value is null)
        {
            return null;
        }

        return value.Kind switch
        {
            CapabilityValueKind.Boolean when value.BooleanValue is { } flag => new PluginValue(flag),
            CapabilityValueKind.Integer when value.IntegerValue is { } number => new PluginValue(Number: number),
            CapabilityValueKind.Choice when value.ChoiceValue is { } choice => new PluginValue(Text: choice),
            _ => null
        };
    }
}
