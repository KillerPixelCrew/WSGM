using System;
using System.Collections.Generic;
using System.Linq;
using WSGM.DeviceLab.Inventory;

namespace WSGM.DeviceLab.Knowledge;

/// <summary>The identity fields a knowledge rule can test, as read from one machine.</summary>
internal sealed record DeviceKnowledgeIdentity
{
    /// <summary>Win32_BaseBoard.Manufacturer.</summary>
    public string? BaseboardManufacturer { get; init; }

    /// <summary>Win32_BaseBoard.Product.</summary>
    public string? BaseboardProduct { get; init; }

    /// <summary>Win32_ComputerSystem.Model.</summary>
    public string? SystemModel { get; init; }

    /// <summary>Win32_ComputerSystem.SystemSKUNumber.</summary>
    public string? SystemSku { get; init; }

    /// <summary>Win32_Processor.Name.</summary>
    public string? ProcessorName { get; init; }

    /// <summary>Win32_BaseBoard.Version.</summary>
    public string? BaseboardVersion { get; init; }

    /// <summary>Reads the identity fields out of an inventory.</summary>
    /// <param name="inventory">Observed machine inventory.</param>
    /// <returns>The identity.</returns>
    public static DeviceKnowledgeIdentity From(MachineInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        return new DeviceKnowledgeIdentity
        {
            BaseboardManufacturer = inventory.Firmware.BaseboardManufacturer,
            BaseboardProduct = inventory.Firmware.BaseboardProduct,
            SystemModel = inventory.Firmware.SystemProduct,
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
        DeviceKnowledgeIdentity identity)
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

    private static DeviceKnowledgeMatch? BestRule(DeviceKnowledgeRecord record, DeviceKnowledgeIdentity identity)
    {
        DeviceKnowledgeMatch? fallback = null;
        foreach (var rule in record.Identity)
        {
            List<string> explanations = [];
            if (!Matches(rule, identity, explanations))
            {
                continue;
            }

            var match = new DeviceKnowledgeMatch
            {
                RecordId = record.Id,
                DisplayName = record.DisplayName,
                Status = record.Status,
                Fallback = rule.Fallback,
                Explanations = explanations
            };
            if (!rule.Fallback)
            {
                return match;
            }

            fallback ??= match;
        }

        return fallback;
    }

    private static bool Matches(DeviceIdentityRule rule, DeviceKnowledgeIdentity identity, List<string> explanations)
    {
        return Field("baseboard manufacturer", rule.BaseboardManufacturer, identity.BaseboardManufacturer,
                   explanations)
               && Field("baseboard product", rule.BaseboardProduct, identity.BaseboardProduct, explanations)
               && Field("system model", rule.SystemModel, identity.SystemModel, explanations)
               && Field("system SKU", rule.SystemSku, identity.SystemSku, explanations)
               && Field("processor", rule.ProcessorName, identity.ProcessorName, explanations)
               && Contains("processor", rule.ProcessorNameContains, identity.ProcessorName, explanations)
               && Field("baseboard version", rule.BaseboardVersion, identity.BaseboardVersion, explanations);
    }

    private static bool Contains(string label, string? expected, string? observed, List<string> explanations)
    {
        if (expected is null)
        {
            return true;
        }

        if (observed?.Contains(expected, StringComparison.OrdinalIgnoreCase) != true)
        {
            return false;
        }

        explanations.Add($"{label} contains '{expected}'.");
        return true;
    }

    private static bool Field(string label, string? expected, string? observed, List<string> explanations)
    {
        if (expected is null)
        {
            return true;
        }

        if (!string.Equals(expected.Trim(), observed?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        explanations.Add($"{label} matched '{expected}'.");
        return true;
    }
}
