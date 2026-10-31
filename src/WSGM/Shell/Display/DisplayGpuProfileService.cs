using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LibGPUDriverInteract;
using WindowsDeviceControl;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;

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
        if (preference.Target is null)
        {
            return null;
        }

        var matches = current.Where(capability => capability.PluginId == preference.PluginId
                                                  && capability.Descriptor?.CapabilityId == preference.CapabilityId
                                                  && capability.Target?.Matches(preference.Target) == true)
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

    /// <summary>The physical monitor identity of one driver output, named after its display section.</summary>
    /// <param name="output">An output that carries a device path or an EDID identity.</param>
    /// <param name="sectionTitle">The publisher's title for the output's section, or null.</param>
    /// <returns>The identity a saved layout preference is matched against.</returns>
    internal static DisplayTargetIdentity Target(GpuDisplayTarget output, string? sectionTitle)
    {
        return new DisplayTargetIdentity(output.DevicePath ?? "", null, null, sectionTitle ?? "Display",
            output.AdapterLowPart ?? 0, output.AdapterHighPart ?? 0, output.TargetId ?? 0)
        {
            EdidIdentity = output.EdidIdentity
        };
    }
}
