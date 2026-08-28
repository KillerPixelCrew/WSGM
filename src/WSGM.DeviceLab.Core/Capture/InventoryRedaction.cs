using System;
using System.Collections.Generic;
using System.Linq;
using WSGM.DeviceLab.Core.Inventory;

namespace WSGM.DeviceLab.Core.Capture;

/// <summary>
/// Produces the shareable form of a machine inventory.
/// </summary>
/// <remarks>
/// The private capture keeps everything, because a maintainer diagnosing their own machine needs the
/// real identifiers. Only the shareable projection is redacted, and it is a separate value rather
/// than an in-place edit so the two cannot be confused: a function that redacted in place would leave
/// no way to tell whether the object in hand is safe to send.
/// </remarks>
public static class InventoryRedaction
{
    /// <summary>
    /// Returns a copy of the inventory safe to send to someone else.
    /// </summary>
    /// <param name="inventory">The private inventory.</param>
    /// <param name="removed">What was redacted, for the bundle's redaction manifest.</param>
    /// <returns>The shareable inventory.</returns>
    public static MachineInventory ToShareable(
        MachineInventory inventory,
        out IReadOnlyList<RedactionSummary> removed)
    {
        ArgumentNullException.ThrowIfNull(inventory);

        CaptureRedactor redactor = new();

        MachineInventory shareable = inventory with
        {
            GraphicsAdapters = [.. inventory.GraphicsAdapters.Select(adapter => adapter with
            {
                InstanceId = redactor.Redact(adapter.InstanceId),
            })],
            UsbInterfaces = [.. inventory.UsbInterfaces.Select(i => i with
            {
                InstanceId = redactor.Redact(i.InstanceId),

                // Both location paths describe which physical port this unit has the device plugged
                // into. They are the continuation key at runtime and are meaningless to anyone else,
                // so they are dropped rather than tokenized - a token would imply they could be
                // compared across machines.
                LocationPath = null,
                DeviceLevelLocationPath = null,
            })],
            SerialEndpoints = [.. inventory.SerialEndpoints.Select(endpoint => endpoint with
            {
                InstanceId = redactor.Redact(endpoint.InstanceId),
                LocationPath = null,
            })],
            Sensors = [.. inventory.Sensors.Select(sensor => sensor with
            {
                InstanceId = redactor.Redact(sensor.InstanceId),
                AssociationId = sensor.AssociationId is null ? null : redactor.Redact(sensor.AssociationId),
            })],
            InputBackends = [.. inventory.InputBackends.Select(backend => backend with
            {
                Endpoints = [.. backend.Endpoints.Select(endpoint => endpoint with
                {
                    EndpointId = redactor.Redact(endpoint.EndpointId),
                    InstanceId = endpoint.InstanceId is null ? null : redactor.Redact(endpoint.InstanceId),
                })],
            })],
            NativeBinaries = [.. inventory.NativeBinaries.Select(binary => binary with
            {
                Path = binary.Name,
            })],
            Processes = [.. inventory.Processes.Select(process => process with
            {
                Path = null,
                CommandLine = null,
                LoadedModulePaths = [.. process.LoadedModulePaths.Select(System.IO.Path.GetFileName)],
            })],
            Services = [.. inventory.Services.Select(service => service with
            {
                PathName = null,
            })],
            ScheduledTasks = [.. inventory.ScheduledTasks.Select(task => task with
            {
                Path = redactor.Redact(task.Path),
            })],
        };

        removed = redactor.Summarize();
        return shareable;
    }
}
