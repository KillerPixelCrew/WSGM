using System;
using System.Collections.Generic;
using System.Linq;
using WSGM.Device.Sdk.Identity;
using WSGM.DeviceLab.Inventory;

namespace WSGM.DeviceLab.Knowledge;

/// <summary>Builds the SDK identity snapshot a knowledge rule is matched against.</summary>
internal static class DeviceKnowledgeIdentity
{
    /// <summary>Reads the identity fields out of an inventory.</summary>
    /// <param name="inventory">Observed machine inventory.</param>
    /// <returns>The identity, in the SDK shape WSGM and setup match against too.</returns>
    public static DeviceIdentitySnapshot From(MachineInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        return new DeviceIdentitySnapshot
        {
            BaseboardManufacturer = inventory.Firmware.BaseboardManufacturer,
            BaseboardProduct = inventory.Firmware.BaseboardProduct,
            SystemProduct = inventory.Firmware.SystemProduct,
            SystemSku = inventory.Firmware.SystemSku,
            ProcessorName = inventory.Processor?.Name,
            BaseboardVersion = inventory.Firmware.BaseboardVersion
        };
    }
}

/// <summary>One knowledge record that matched, and the rule that matched it.</summary>
internal sealed record DeviceKnowledgeMatch
{
    /// <summary>Matched record ID.</summary>
    public required string RecordId { get; init; }

    /// <summary>Product name for the tester to confirm.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Whether the record is curated or only extracted.</summary>
    public required DeviceKnowledgeStatus Status { get; init; }

    /// <summary>Whether only a vendor-wide default rule matched.</summary>
    public required bool Fallback { get; init; }

    /// <summary>One line per compared field of the matching rule.</summary>
    public IReadOnlyList<string> Explanations { get; init; } = [];
}

/// <summary>Matches a machine's identity against the knowledge base.</summary>
internal static class DeviceKnowledgeMatcher
{
    /// <summary>Returns every record whose rules match, exact matches before fallbacks.</summary>
    /// <param name="knowledge">Knowledge base to search.</param>
    /// <param name="identity">Observed identity.</param>
    /// <returns>Matches, curated before extracted within each rank, then by ID.</returns>
    /// <remarks>
    ///     A curated record hides the extracted records it supersedes. Fallback matches are returned
    ///     only when nothing matched exactly, because HC's <c>default</c> branch is never taken when
    ///     an exact case exists.
    /// </remarks>
    public static IReadOnlyList<DeviceKnowledgeMatch> Match(
        DeviceKnowledgeBase knowledge,
        DeviceIdentitySnapshot identity)
    {
        ArgumentNullException.ThrowIfNull(knowledge);
        ArgumentNullException.ThrowIfNull(identity);

        var superseded = knowledge.Records
            .SelectMany(record => record.Supersedes)
            .ToHashSet(StringComparer.Ordinal);
        List<DeviceKnowledgeMatch> matches = [];
        foreach (var record in knowledge.Records.Where(record => !superseded.Contains(record.Id)))
        {
            var match = BestRule(record, identity);
            if (match is not null)
            {
                matches.Add(match);
            }
        }

        var exact = matches.Where(match => !match.Fallback).ToArray();
        IEnumerable<DeviceKnowledgeMatch> selected = exact.Length > 0 ? exact : matches;
        return
        [
            .. selected
                .OrderBy(match => match.Status is DeviceKnowledgeStatus.Curated ? 0 : 1)
                .ThenBy(match => match.RecordId, StringComparer.Ordinal)
        ];
    }

    private static DeviceKnowledgeMatch? BestRule(DeviceKnowledgeRecord record, DeviceIdentitySnapshot identity)
    {
        var match = HardwareMatcher.Match(record.Identity, identity);
        return match is null
            ? null
            : new DeviceKnowledgeMatch
            {
                RecordId = record.Id,
                DisplayName = record.DisplayName,
                Status = record.Status,
                Fallback = match.Fallback,
                Explanations = match.Explanations
            };
    }
}
