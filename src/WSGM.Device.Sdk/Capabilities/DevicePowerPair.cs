using System;
using System.Collections.Generic;
using System.Linq;

namespace WSGM.Device.Sdk.Capabilities;

/// <summary>Validation for optional coordinated sustained/boost power commands.</summary>
public static class DevicePowerPair
{
    /// <summary>Validates every declared pair against the complete descriptor set.</summary>
    /// <param name="descriptors">The current capability descriptors.</param>
    /// <param name="error">The invalid declaration, or null on success.</param>
    /// <returns>Whether all pairs have two distinct compatible watt controls.</returns>
    public static bool TryValidate(IReadOnlyList<CapabilityDescriptor> descriptors, out string? error)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        error =
            "A power pair requires unique readable and writable sustained/boost watt controls with valid bounds and step.";
        foreach (var primary in descriptors)
        {
            if (primary.PairedPowerLimitId is null)
            {
                continue;
            }

            if (primary.Role != CapabilityRole.PowerSustainedLimit || !IsLimit(primary)
                                                                   || string.IsNullOrWhiteSpace(
                                                                       primary.PairedPowerLimitId))
            {
                return false;
            }

            var peers = descriptors.Where(d => d.CapabilityId == primary.PairedPowerLimitId).ToArray();
            if (peers.Length != 1 || !IsLimit(peers[0]) || peers[0].Role != CapabilityRole.PowerSlowLimit
                || peers[0].PairedPowerLimitId is not null || peers[0].CapabilityId == primary.CapabilityId
                || descriptors.Count(d => d.PairedPowerLimitId == primary.PairedPowerLimitId) != 1)
            {
                return false;
            }
        }

        error = null;
        return true;
    }

    private static bool IsLimit(CapabilityDescriptor descriptor)
    {
        return descriptor is
               {
                   InstanceId: null,
                   SupportsRead: true,
                   SupportsWrite: true,
                   ValueKind: CapabilityValueKind.Integer,
                   Unit: CapabilityUnit.Watt,
                   Minimum: > 0,
                   Step: > 0
               }
               && descriptor.Maximum >= descriptor.Minimum;
    }
}
