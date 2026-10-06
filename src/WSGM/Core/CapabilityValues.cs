using System;
using System.Linq;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Core;

/// <summary>Pure capability value comparisons shared by device and graphics paths.</summary>
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
}
