using System;
using System.Collections.Generic;
using System.Threading;
using WSGM.DeviceLab.Inventory;
using WSGM.DeviceLab.Knowledge;

namespace WSGM.DeviceLab.Wizard;

/// <summary>The identity facts shown to the tester, with no serials or account data.</summary>
internal sealed record LabIdentityFacts
{
    /// <summary>Board manufacturer.</summary>
    public string? BoardManufacturer { get; init; }

    /// <summary>Board product.</summary>
    public string? BoardName { get; init; }

    /// <summary>Board version.</summary>
    public string? BoardVersion { get; init; }

    /// <summary>System model.</summary>
    public string? SystemModel { get; init; }

    /// <summary>System SKU.</summary>
    public string? SystemSku { get; init; }

    /// <summary>BIOS version.</summary>
    public string? BiosVersion { get; init; }

    /// <summary>Embedded controller version as SMBIOS reports it.</summary>
    public string? EmbeddedControllerVersion { get; init; }

    /// <summary>Processor name.</summary>
    public string? Processor { get; init; }
}

/// <summary>What the identity stage observed.</summary>
/// <param name="Facts">Facts shown to the tester.</param>
/// <param name="Matches">Knowledge records that matched, best first.</param>
/// <param name="Inventory">The full private inventory, stored in the project and redacted on export.</param>
internal sealed record LabIdentityObservation(
    LabIdentityFacts Facts,
    IReadOnlyList<DeviceKnowledgeMatch> Matches,
    MachineInventory Inventory);

/// <summary>Collects identity for the wizard's second stage.</summary>
internal static class LabIdentity
{
    /// <summary>Collects a read-only inventory and matches it against the knowledge base.</summary>
    /// <param name="knowledge">Knowledge base.</param>
    /// <param name="cancellationToken">Cancels collection.</param>
    /// <returns>What was observed.</returns>
    public static LabIdentityObservation Observe(DeviceKnowledgeBase knowledge, CancellationToken cancellationToken)
    {
        var inventory = WindowsInventoryCollector.Collect(
            DateTimeOffset.UtcNow,
            DeviceLabInventoryWorkflow.ProbedWmiClasses,
            cancellationToken);
        return Observe(knowledge, inventory);
    }

    /// <summary>Matches an inventory against the knowledge base.</summary>
    /// <param name="knowledge">Knowledge base.</param>
    /// <param name="inventory">Inventory.</param>
    /// <returns>What was observed.</returns>
    public static LabIdentityObservation Observe(DeviceKnowledgeBase knowledge, MachineInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(knowledge);
        ArgumentNullException.ThrowIfNull(inventory);
        var firmware = inventory.Firmware;
        var facts = new LabIdentityFacts
        {
            BoardManufacturer = firmware.BaseboardManufacturer,
            BoardName = firmware.BaseboardProduct,
            BoardVersion = firmware.BaseboardVersion,
            SystemModel = firmware.SystemProduct,
            SystemSku = firmware.SystemSku,
            BiosVersion = firmware.BiosVersion,
            EmbeddedControllerVersion = firmware.EmbeddedControllerVersion,
            Processor = inventory.Processor?.Name
        };
        return new LabIdentityObservation(
            facts,
            DeviceKnowledgeMatcher.Match(knowledge, DeviceKnowledgeIdentity.From(inventory)),
            inventory);
    }
}
