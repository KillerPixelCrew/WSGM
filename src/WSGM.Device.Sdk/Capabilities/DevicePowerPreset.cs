using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace WSGM.Device.Sdk.Capabilities;

/// <summary>The Windows power mode requested by a device power preset, independent of power plans.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DevicePowerMode>))]
public enum DevicePowerMode
{
    /// <summary>Prefer battery life.</summary>
    BetterBattery,
    /// <summary>Use balanced performance.</summary>
    Balanced,
    /// <summary>Prefer performance.</summary>
    BestPerformance,
}

/// <summary>A device-authored shortcut for its sustained/slow power limits and Windows power mode.</summary>
/// <param name="Id">Stable identifier, up to 64 ASCII letters, digits, dots, underscores or hyphens.</param>
/// <param name="Name">Single-line display name, up to 120 characters.</param>
/// <param name="SustainedWatts">Target for the descriptor carrying this preset.</param>
/// <param name="SlowWatts">Target for the single-instance PowerSlowLimit capability.</param>
/// <param name="WindowsMode">Windows power mode applied by the host.</param>
/// <remarks>Declare at most 16 on a single-instance sustained watt limit. Both targets must fit the
/// current writable descriptors. Presets are shortcuts, never policies to reapply after drift.
/// Hosts derive Custom from observed values; a failed multi-control application may be partial.</remarks>
public sealed record DevicePowerPreset(
    string Id, string Name, int SustainedWatts, int SlowWatts, DevicePowerMode WindowsMode)
{
    /// <summary>Optional ScenarioMode choice applied before watt limits on AC power.</summary>
    /// <remarks>Declare both scenario targets or neither. Choices belong to the single readable,
    /// writable ScenarioMode descriptor; the host must confirm the current power source.</remarks>
    public string? ScenarioOnAc { get; init; }

    /// <summary>Optional ScenarioMode choice applied before watt limits on battery power.</summary>
    public string? ScenarioOnDc { get; init; }

    /// <summary>Validates all presets against their current power descriptors.</summary>
    /// <param name="descriptors">The complete descriptor set.</param>
    /// <param name="error">The first invalid declaration, if any.</param>
    /// <returns>Whether the declarations are safe to offer.</returns>
    public static bool TryValidate(IReadOnlyList<CapabilityDescriptor> descriptors, out string? error)
    {
        error = "Power presets require one readable, writable sustained/slow watt pair with valid targets.";
        foreach (CapabilityDescriptor descriptor in descriptors)
        {
            if (descriptor.PowerPresets is null || descriptor.PowerPresets.Count > 16) { return false; }
            if (descriptor.PowerPresets.Count == 0) { continue; }
            CapabilityDescriptor[] sustained = descriptors.Where(d => d.Role == CapabilityRole.PowerSustainedLimit).ToArray();
            CapabilityDescriptor[] slow = descriptors.Where(d => d.Role == CapabilityRole.PowerSlowLimit).ToArray();
            if (sustained.Length != 1 || slow.Length != 1 || sustained[0] != descriptor
                || !IsPowerLimit(descriptor) || !IsPowerLimit(slow[0])) { return false; }
            HashSet<string> ids = new(StringComparer.Ordinal);
            foreach (DevicePowerPreset preset in descriptor.PowerPresets)
            {
                if (preset is null || string.IsNullOrEmpty(preset.Id) || preset.Id.Length > 64
                    || preset.Id == "custom" || !preset.Id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')
                    || !ids.Add(preset.Id) || !PlainText.TryValidate(preset.Name, 120, "preset name", out _)
                    || !Enum.IsDefined(preset.WindowsMode) || preset.SustainedWatts > preset.SlowWatts
                    || !Fits(preset.SustainedWatts, descriptor) || !Fits(preset.SlowWatts, slow[0])
                    || !ValidScenario(preset, descriptors)) { return false; }
            }
        }
        error = null;
        return true;
    }

    private static bool IsPowerLimit(CapabilityDescriptor descriptor) =>
        descriptor.InstanceId is null && descriptor.SupportsRead && descriptor.SupportsWrite
        && descriptor.ValueKind == CapabilityValueKind.Integer && descriptor.Unit == CapabilityUnit.Watt
        && descriptor.Minimum is > 0 && descriptor.Maximum >= descriptor.Minimum && descriptor.Step is > 0;

    private static bool ValidScenario(DevicePowerPreset preset, IReadOnlyList<CapabilityDescriptor> descriptors)
    {
        if (preset.ScenarioOnAc is null && preset.ScenarioOnDc is null) { return true; }
        CapabilityDescriptor[] scenarios = descriptors.Where(d => d.Role == CapabilityRole.ScenarioMode).ToArray();
        return scenarios.Length == 1 && scenarios[0].InstanceId is null
            && scenarios[0].SupportsRead && scenarios[0].SupportsWrite
            && scenarios[0].AvailableOnAc && scenarios[0].AvailableOnDc
            && scenarios[0].ValueKind == CapabilityValueKind.Choice
            && scenarios[0].Choices is not null
            && !string.IsNullOrEmpty(preset.ScenarioOnAc) && !string.IsNullOrEmpty(preset.ScenarioOnDc)
            && scenarios[0].Choices.Any(choice => choice.Value == preset.ScenarioOnAc)
            && scenarios[0].Choices.Any(choice => choice.Value == preset.ScenarioOnDc);
    }

    private static bool Fits(int watts, CapabilityDescriptor descriptor) =>
        watts >= descriptor.Minimum && watts <= descriptor.Maximum
        && (watts - descriptor.Minimum) % descriptor.Step == 0;
}
