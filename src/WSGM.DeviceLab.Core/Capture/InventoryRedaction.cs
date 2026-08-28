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
        };

        removed = redactor.Summarize();
        return shareable;
    }
}
