using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LibGPUDriverInteract;
using WindowsDeviceControl;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Applies display-layout driver preferences through the resident graphics owner.</summary>
internal static class DisplayGpuProfileService
{
    /// <summary>Activates HDR outputs in SDR while their explicit color encoding is being changed.</summary>
    internal static (DisplayLayout Activation, IReadOnlyList<DisplayLayoutOutput> FinalHdr) PrepareLayout(
        DisplayLayout layout, IReadOnlyList<DisplayGpuPreference> preferences)
    {
        var finalHdr = layout.Outputs.Where(output => output.Hdr == true && preferences.Any(
            preference => preference.Target?.Matches(output.Target) == true
                          && (preference.CapabilityId?.StartsWith("display.color.", StringComparison.Ordinal) == true
                              || preference.CapabilityId == "display.hdr-output-mode"))).ToArray();
        var activation = finalHdr.Length == 0 ? layout : new DisplayLayout(
            [.. layout.Outputs.Select(output => finalHdr.Contains(output) ? output with { Hdr = false } : output)]);
        return (activation, finalHdr);
    }

    /// <summary>Applies final HDR signaling once the requested driver color data has been written.</summary>
    internal static Task<string?> ApplyHdrAsync(IReadOnlyList<DisplayLayoutOutput> outputs,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            var failures = 0;
            foreach (var output in outputs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = DisplayColor.SetHdr(output.Target, output.Hdr == true);
                Log.Info($"Display profile final HDR: {output.Target.FriendlyName}, enabled={output.Hdr}, "
                         + $"outcome={result.Outcome}, nativeStatus={result.NativeStatus}.");
                if (!result.Succeeded)
                {
                    failures++;
                    Log.Warn($"Display profile HDR for {output.Target.FriendlyName}: "
                             + DisplayText.Hdr(result.Outcome, result.NativeStatus));
                }
            }

            return failures == 0 ? null : "HDR could not be applied to every display. Details are in wsgm.log.";
        }, CancellationToken.None);
    }

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
        // Encoding first, then the other fields, with bit depth last. A later encoding write must
        // not invalidate the requested bit depth before Windows enables the final HDR state.
        foreach (var preference in preferences.OrderBy(preference => preference.CapabilityId switch
                 {
                     "display.color.format" => 0,
                     "display.color.depth" => 2,
                     _ => 1
                 }))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await graphics.ExecuteDisplayPreferenceAsync(preference, cancellationToken)
                .ConfigureAwait(false);
            if (!result.Outcome.IsApplied())
            {
                failures.Add($"{preference.Target?.FriendlyName ?? "Driver-wide"} / {preference.CapabilityId}: "
                             + (result.Reason?.Detail ?? result.Outcome.ToString()));
            }
        }

        if (failures.Count == 0)
        {
            return null;
        }

        Log.Warn("Display driver preferences: " + string.Join(" ", failures));
        return $"{failures.Count} display setting(s) could not be applied. Details are in wsgm.log.";
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
