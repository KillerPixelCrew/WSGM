using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>
///     Finds the one variable-refresh capability every VRR surface drives, across the device package and
///     the graphics packages.
/// </summary>
/// <remarks>
///     Variable refresh belongs to the graphics driver, so a graphics package publishes it per display. The
///     Quick Access switch, the Device overlay row and the per-application restore all need one answer: the
///     only VRR capability when there is one, otherwise the one for the built-in panel, then the device
///     package's, then the lowest sort order, then the first by publisher and instance. A graphics package
///     marks the built-in panel's instance by starting its id with <c>internal</c>.
/// </remarks>
internal static class VariableRefreshCapabilities
{
    /// <summary>The instance id, or its prefix, of the built-in panel's capability.</summary>
    internal const string InternalDisplayInstance = "internal";

    /// <summary>Finds the writable variable-refresh capability.</summary>
    /// <param name="device">The device coordinator, or null without one.</param>
    /// <param name="gpu">The graphics coordinator, or null without one.</param>
    /// <param name="requireAvailable">Whether the capability must be available now.</param>
    /// <returns>The chosen capability, or null when none is published.</returns>
    internal static PublishedCapability? Find(DeviceCoordinator? device, GpuCoordinator? gpu, bool requireAvailable)
    {
        List<PublishedCapability> candidates = [];
        if (device is not null)
        {
            candidates.AddRange(device.Capabilities.Snapshot()
                .Where(view => Matches(view, requireAvailable))
                .Select(view => new PublishedCapability(view, null)));
        }

        if (gpu is not null)
        {
            candidates.AddRange(gpu.FindCapabilities(view => Matches(view, requireAvailable)));
        }

        return Select(candidates);
    }

    /// <summary>Chooses one capability among the published ones.</summary>
    /// <param name="candidates">Every writable VRR capability.</param>
    /// <returns>The chosen one, or null when there is none.</returns>
    internal static PublishedCapability? Select(IReadOnlyList<PublishedCapability> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count <= 1)
        {
            return candidates.FirstOrDefault();
        }

        var ordered = candidates
            .OrderBy(candidate => candidate.GpuPluginId is null ? 0 : 1)
            .ThenBy(candidate => candidate.View.Descriptor.SortOrder)
            .ThenBy(candidate => candidate.GpuPluginId, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.View.Descriptor.InstanceId, StringComparer.Ordinal)
            .ToArray();
        return ordered.FirstOrDefault(candidate =>
                   candidate.View.Descriptor.InstanceId?.StartsWith(InternalDisplayInstance,
                       StringComparison.OrdinalIgnoreCase) == true)
               ?? ordered[0];
    }

    /// <summary>Writes a variable-refresh state to whichever package publishes the chosen capability.</summary>
    /// <param name="target">The capability.</param>
    /// <param name="device">The device coordinator.</param>
    /// <param name="gpu">The graphics coordinator.</param>
    /// <param name="enabled">The requested state.</param>
    /// <param name="origin">Who asked.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The publisher's result.</returns>
    internal static Task<CapabilityCommandResult> ExecuteAsync(
        PublishedCapability target,
        DeviceCoordinator? device,
        GpuCoordinator? gpu,
        bool enabled,
        CapabilityCommandOrigin origin,
        CancellationToken cancellationToken)
    {
        CapabilityValue value = new() { Kind = CapabilityValueKind.Boolean, BooleanValue = enabled };
        var descriptor = target.View.Descriptor;
        if (target.GpuPluginId is { } pluginId)
        {
            return gpu is null
                ? Task.FromResult(Unavailable())
                : gpu.ExecuteAsync(pluginId, descriptor.CapabilityId, descriptor.InstanceId, value, origin,
                    cancellationToken);
        }

        return device is null
            ? Task.FromResult(Unavailable())
            : device.ExecuteCapabilityAsync(descriptor.CapabilityId, descriptor.InstanceId, value,
                TimeSpan.FromSeconds(5), origin, cancellationToken: cancellationToken);
    }

    private static bool Matches(DeviceCapabilityView view, bool requireAvailable)
    {
        return view.Descriptor is { Role: CapabilityRole.VariableRefreshRate, SupportsWrite: true }
               && (!requireAvailable || view.Projection.State.Available);
    }

    private static CapabilityCommandResult Unavailable()
    {
        return new CapabilityCommandResult
        {
            CommandId = Guid.NewGuid(),
            Outcome = CommandOutcome.Rejected,
            Reason = new CapabilityReason(CapabilityReasonCode.HostUnavailable,
                "The variable-refresh publisher is not running.", true),
            CompletedAt = DateTimeOffset.UtcNow
        };
    }
}
