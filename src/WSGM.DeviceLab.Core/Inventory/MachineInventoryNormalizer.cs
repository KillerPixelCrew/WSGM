using System;
using System.Collections.Generic;
using System.Linq;

namespace WSGM.DeviceLab.Core.Inventory;

/// <summary>Deterministic bounds and structural checks shared by live and fixture inventories.</summary>
public static class MachineInventoryNormalizer
{
    /// <summary>Canonicalizes every Stage 1 lane without inventing missing observations.</summary>
    /// <param name="inventory">Raw read-only observations.</param>
    /// <returns>Bounded deterministic inventory.</returns>
    public static MachineInventory Normalize(MachineInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        return inventory with
        {
            GraphicsAdapters = OrderAndTake(inventory.GraphicsAdapters, item => item.InstanceId),
            UsbInterfaces = OrderAndTake(inventory.UsbInterfaces, item => item.InstanceId),
            WmiClasses = inventory.WmiClasses
                .OrderBy(item => item.Namespace, StringComparer.Ordinal)
                .ThenBy(item => item.ClassName, StringComparer.Ordinal)
                .Take(InventoryLimits.MaximumEndpointsPerLane)
                .ToArray(),
            SerialEndpoints = inventory.SerialEndpoints
                .Select(NormalizeSerial)
                .OrderBy(item => item.InstanceId, StringComparer.Ordinal)
                .Take(InventoryLimits.MaximumEndpointsPerLane)
                .ToArray(),
            Sensors = inventory.Sensors
                .Select(NormalizeSensor)
                .GroupBy(item => item.Api)
                .SelectMany(group => group
                    .OrderBy(item => item.InstanceId, StringComparer.Ordinal)
                    .Take(InventoryLimits.MaximumEndpointsPerLane))
                .OrderBy(item => item.Api)
                .ThenBy(item => item.InstanceId, StringComparer.Ordinal)
                .ToArray(),
            InputBackends = inventory.InputBackends
                .Select(backend => backend with
                {
                    Endpoints = backend.Endpoints
                        .Select(NormalizeInputEndpoint)
                        .OrderBy(endpoint => endpoint.EndpointId, StringComparer.Ordinal)
                        .Take(InventoryLimits.MaximumEndpointsPerLane)
                        .ToArray(),
                })
                .GroupBy(backend => (backend.Backend, backend.View))
                .Select(group => group
                    .OrderBy(backend => backend.Access)
                    .First())
                .OrderBy(backend => backend.Backend)
                .ThenBy(backend => backend.View)
                .Take(InventoryLimits.MaximumInputBackendViews)
                .ToArray(),
            NativeBinaries = inventory.NativeBinaries
                .Select(binary => binary with
                {
                    Exports = binary.Exports
                        .Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal)
                        .Take(InventoryLimits.MaximumNativeExports)
                        .ToArray(),
                })
                .OrderBy(binary => binary.Path, StringComparer.OrdinalIgnoreCase)
                .Take(InventoryLimits.MaximumSystemEntriesPerLane)
                .ToArray(),
            Processes = inventory.Processes
                .Select(process => process with
                {
                    LoadedModulePaths = process.LoadedModulePaths
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Order(StringComparer.OrdinalIgnoreCase)
                        .Take(InventoryLimits.MaximumEndpointsPerLane)
                        .ToArray(),
                })
                .OrderBy(process => process.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(process => process.ProcessId)
                .Take(InventoryLimits.MaximumSystemEntriesPerLane)
                .ToArray(),
            Services = inventory.Services
                .OrderBy(service => service.Name, StringComparer.OrdinalIgnoreCase)
                .Take(InventoryLimits.MaximumSystemEntriesPerLane)
                .ToArray(),
            ScheduledTasks = inventory.ScheduledTasks
                .OrderBy(task => task.Path, StringComparer.OrdinalIgnoreCase)
                .Take(InventoryLimits.MaximumSystemEntriesPerLane)
                .ToArray(),
            Providers = inventory.Providers
                .OrderBy(provider => provider.Kind, StringComparer.Ordinal)
                .ThenBy(provider => provider.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(provider => provider.Context, StringComparer.OrdinalIgnoreCase)
                .Take(InventoryLimits.MaximumSystemEntriesPerLane)
                .ToArray(),
            ResourceConflicts = inventory.ResourceConflicts
                .OrderBy(conflict => conflict.ResourceId, StringComparer.Ordinal)
                .ThenBy(conflict => conflict.Owner, StringComparer.OrdinalIgnoreCase)
                .Take(InventoryLimits.MaximumSystemEntriesPerLane)
                .ToArray(),
            TopologyGenerations = inventory.TopologyGenerations
                .OrderByDescending(observation => observation.Generation)
                .ThenBy(observation => observation.InstanceId, StringComparer.Ordinal)
                .Take(InventoryLimits.MaximumEndpointsPerLane)
                .OrderBy(observation => observation.Generation)
                .ThenBy(observation => observation.InstanceId, StringComparer.Ordinal)
                .ToArray(),
        };
    }

    private static SerialEndpointInventory NormalizeSerial(SerialEndpointInventory endpoint)
    {
        bool malformed = false;
        List<SerialFramingCandidate> candidates = [];
        foreach (SerialFramingCandidate candidate in endpoint.FramingCandidates)
        {
            if (candidate.BaudRate is 0 or > 16_000_000
                || candidate.DataBits is < 5 or > 8
                || candidate.Parity is > 4
                || candidate.StopBits is > 2)
            {
                malformed = true;
                continue;
            }

            candidates.Add(candidate);
        }

        malformed |= candidates.Count > InventoryLimits.MaximumFramingCandidates;

        return endpoint with
        {
            Access = malformed ? InventoryAccess.Malformed : endpoint.Access,
            FramingCandidates = candidates
                .OrderBy(candidate => candidate.BaudRate)
                .ThenBy(candidate => candidate.DataBits)
                .ThenBy(candidate => candidate.Parity)
                .ThenBy(candidate => candidate.StopBits)
                .ThenBy(candidate => candidate.Source, StringComparer.Ordinal)
                .Take(InventoryLimits.MaximumFramingCandidates)
                .ToArray(),
        };
    }

    private static SensorEndpointInventory NormalizeSensor(SensorEndpointInventory sensor)
    {
        uint[] intervals = sensor.SupportedReportIntervalsMilliseconds
            .Distinct()
            .Order()
            .Take(InventoryLimits.MaximumSensorIntervals)
            .ToArray();
        if (sensor.MinimumReportIntervalMilliseconds is { } minimum
            && !intervals.Contains(minimum))
        {
            intervals = intervals.Length < InventoryLimits.MaximumSensorIntervals
                ? intervals.Append(minimum).Order().ToArray()
                : intervals[..^1].Append(minimum).Order().ToArray();
        }

        return sensor with { SupportedReportIntervalsMilliseconds = intervals };
    }

    private static InputEndpointInventory NormalizeInputEndpoint(InputEndpointInventory endpoint)
    {
        bool malformed = endpoint.DescriptorAccess is InventoryAccess.Malformed
            || (endpoint.DescriptorAccess is InventoryAccess.Available
                && endpoint.ReportDescriptorSha256 is null)
            || InvalidReportLength(endpoint.InputReportBytes)
            || InvalidReportLength(endpoint.OutputReportBytes)
            || InvalidReportLength(endpoint.FeatureReportBytes)
            || (endpoint.ReportDescriptorSha256 is { } sha256 && !IsSha256(sha256));
        return endpoint with
        {
            DescriptorAccess = malformed ? InventoryAccess.Malformed : endpoint.DescriptorAccess,
            ReportDescriptorSha256 = malformed
                ? null
                : endpoint.ReportDescriptorSha256?.ToLowerInvariant(),
        };
    }

    private static bool InvalidReportLength(int? bytes) => bytes is <= 0 or > ushort.MaxValue;

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    private static IReadOnlyList<T> OrderAndTake<T>(IEnumerable<T> items, Func<T, string> key) =>
        items.OrderBy(key, StringComparer.Ordinal)
            .Take(InventoryLimits.MaximumEndpointsPerLane)
            .ToArray();
}
