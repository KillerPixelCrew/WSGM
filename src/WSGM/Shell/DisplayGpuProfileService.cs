using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Plugin.Sdk;

namespace WSGM.Shell;

/// <summary>Applies display-layout driver preferences through the resident graphics owner.</summary>
internal static class DisplayGpuProfileService
{
    /// <summary>Refreshes output routes after Windows accepted the layout, then writes each explicit preference once.</summary>
    internal static async Task<string?> ApplyAsync(GpuCoordinator? graphics,
        IReadOnlyList<DisplayGpuPreference> preferences, CancellationToken cancellationToken)
    {
        if (preferences.Count == 0)
        {
            return null;
        }

        if (graphics is null)
        {
            return "The graphics drivers are unavailable; the saved display controls were kept.";
        }

        await graphics.RefreshDisplayTopologyAsync(cancellationToken).ConfigureAwait(false);
        List<string> failures = [];
        foreach (var preference in preferences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await graphics.ExecuteDisplayPreferenceAsync(preference, cancellationToken)
                .ConfigureAwait(false);
            if (result.Outcome is not (CommandOutcome.Applied or CommandOutcome.AppliedUnverified
                or CommandOutcome.AppliedVerified))
            {
                failures.Add($"{preference.Target?.FriendlyName ?? "Driver-wide"} / {preference.CapabilityId}: "
                             + (result.Reason?.Detail ?? result.Outcome.ToString()));
            }
        }

        return failures.Count == 0 ? null : string.Join(" ", failures);
    }

    /// <summary>Finds one current output by stable physical identity; an old instance ID never chooses a monitor.</summary>
    internal static DisplayGpuCapability? Resolve(DisplayGpuPreference preference,
        IReadOnlyList<DisplayGpuCapability> current)
    {
        var matches = current.Where(capability => capability.PluginId == preference.PluginId
                                                  && capability.Descriptor?.CapabilityId == preference.CapabilityId
                                                  && (preference.Target is null
                                                      ? capability.Target is null
                                                      : capability.Target?.Matches(preference.Target) == true))
            .ToArray();
        if (matches.Length == 1)
        {
            return matches[0];
        }

        // Duplicate OEM serials cannot choose a connector after hotplug. An exact current device
        // path may disambiguate matches, but an old opaque driver instance is never a monitor ID.
        var exact = preference.Target is { DevicePath.Length: > 0 } target
            ? matches.Where(match => string.Equals(match.Target?.DevicePath, target.DevicePath,
                StringComparison.OrdinalIgnoreCase)).ToArray()
            : [];
        return exact.Length == 1 ? exact[0] : null;
    }

    /// <summary>Converts only the primitive declared by a fresh descriptor and validates its current bounds.</summary>
    internal static CapabilityValue? ToCapabilityValue(PluginValue value, CapabilityDescriptor descriptor)
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

    /// <summary>Copies a driver observation into the serializable primitive used by the layout editor.</summary>
    internal static PluginValue? ToPreferenceValue(CapabilityValue? value)
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
