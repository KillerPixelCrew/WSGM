using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WSGM.Device.Sdk.Identity;
using WSGM.DeviceLab.Inventory;
using WSGM.DeviceLab.Probes;

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

/// <summary>Explained exact comparison against one curated record that has compiled read probes.</summary>
internal sealed record CandidateAssessment
{
    /// <summary>Logical device ID of the compiled probe family that was compared.</summary>
    public required string DeviceId { get; init; }

    /// <summary>Curated knowledge record the comparison used.</summary>
    public required string KnowledgeRecordId { get; init; }

    /// <summary>Human-readable device name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Whether every exact field matched.</summary>
    public required bool ExactMatch { get; init; }

    /// <summary>One deterministic pass or mismatch explanation per compared field.</summary>
    public IReadOnlyList<string> Explanations { get; init; } = [];

    /// <summary>Device-specific values that are not implied by a match.</summary>
    public IReadOnlyList<string> NonInheritableValues { get; init; } = [];
}

/// <summary>
///     The exact gate for compiled read probes: a curated record's identity rule, its controller USB
///     endpoints and its WMI provider must all be present, with every mismatch explained.
/// </summary>
/// <remarks>
///     Unlike <see cref="DeviceKnowledgeMatcher" />, which proposes records for a tester to confirm,
///     this admits hardware reads, so fallback rules never count and a record that declares no
///     controller endpoint or WMI provider fails closed.
/// </remarks>
internal static class DeviceKnowledgeAssessor
{
    /// <summary>Compares one inventory with the curated record a compiled probe family serves.</summary>
    /// <param name="knowledge">Knowledge base holding the record.</param>
    /// <param name="family">Compiled probe family.</param>
    /// <param name="inventory">Observed machine inventory.</param>
    /// <param name="targetDeviceId">Logical device ID requested by the caller.</param>
    /// <returns>Explained exact match result.</returns>
    /// <exception cref="InvalidDataException">The family names a missing or uncurated record.</exception>
    public static CandidateAssessment Assess(
        DeviceKnowledgeBase knowledge,
        CompiledReadProbeFamily family,
        MachineInventory inventory,
        string targetDeviceId)
    {
        ArgumentNullException.ThrowIfNull(knowledge);
        ArgumentNullException.ThrowIfNull(family);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDeviceId);

        var record = Record(knowledge, family);
        List<string> explanations = [];
        var exact = Field("logical device ID", family.DeviceId, targetDeviceId, explanations);
        exact &= IdentityMatches(record, DeviceKnowledgeIdentity.From(inventory), explanations);
        exact &= ControllerMatches(record, family, inventory, explanations);
        exact &= WmiMatches(record, inventory, explanations);

        return new CandidateAssessment
        {
            DeviceId = family.DeviceId,
            KnowledgeRecordId = record.Id,
            DisplayName = record.DisplayName,
            ExactMatch = exact,
            Explanations = explanations,
            NonInheritableValues = family.NonInheritableValues
        };
    }

    /// <summary>Names the machine: the logical ID of the family whose record matches exactly.</summary>
    /// <param name="knowledge">Knowledge base.</param>
    /// <param name="families">Compiled probe families.</param>
    /// <param name="inventory">Observed machine inventory.</param>
    /// <returns>The family's logical ID, or an <c>observed-</c> ID built from the baseboard product.</returns>
    public static string LogicalDeviceId(
        DeviceKnowledgeBase knowledge,
        IEnumerable<CompiledReadProbeFamily> families,
        MachineInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(knowledge);
        ArgumentNullException.ThrowIfNull(families);
        ArgumentNullException.ThrowIfNull(inventory);
        var identity = DeviceKnowledgeIdentity.From(inventory);
        foreach (var family in families)
        {
            if (Record(knowledge, family).Identity.Any(rule =>
                    !rule.Fallback && HardwareMatcher.Matches(rule, identity, [])))
            {
                return family.DeviceId;
            }
        }

        return $"observed-{(inventory.Firmware.BaseboardProduct ?? "unknown").ToLowerInvariant()}";
    }

    private static DeviceKnowledgeRecord Record(DeviceKnowledgeBase knowledge, CompiledReadProbeFamily family)
    {
        var record = knowledge.Records.FirstOrDefault(item =>
                         string.Equals(item.Id, family.KnowledgeRecordId, StringComparison.Ordinal))
                     ?? throw new InvalidDataException(
                         $"Probe family {family.FamilyId} names missing record {family.KnowledgeRecordId}.");
        return record.Status is DeviceKnowledgeStatus.Curated
            ? record
            : throw new InvalidDataException(
                $"Probe family {family.FamilyId} names {record.Id}, which is not curated.");
    }

    private static bool IdentityMatches(
        DeviceKnowledgeRecord record,
        DeviceIdentitySnapshot identity,
        List<string> explanations)
    {
        var rules = record.Identity.Where(rule => !rule.Fallback).ToArray();
        if (rules.Length == 0)
        {
            explanations.Add($"identity mismatch: {record.Id} declares no exact identity rule.");
            return false;
        }

        foreach (var rule in rules)
        {
            List<string> ruleExplanations = [];
            if (RuleMatches(rule, identity, ruleExplanations))
            {
                explanations.AddRange(ruleExplanations);
                return true;
            }
        }

        // Nothing matched: explain every exact rule so each mismatching field is visible.
        foreach (var rule in rules)
        {
            RuleMatches(rule, identity, explanations);
        }

        return false;
    }

    private static bool RuleMatches(HardwareMatchRule rule, DeviceIdentitySnapshot identity, List<string> explanations)
    {
        var matched = Field("SMBIOS baseboard manufacturer", rule.BaseboardManufacturer,
            identity.BaseboardManufacturer, explanations);
        matched &= Field("SMBIOS baseboard product", rule.BaseboardProduct, identity.BaseboardProduct, explanations);
        matched &= Field("SMBIOS system model", rule.SystemModel, identity.SystemProduct, explanations);
        matched &= Field("SMBIOS system SKU", rule.SystemSku, identity.SystemSku, explanations);
        matched &= Field("processor", rule.ProcessorName, identity.ProcessorName, explanations);
        matched &= Field("baseboard version", rule.BaseboardVersion, identity.BaseboardVersion, explanations);
        if (rule.ProcessorNameContains is not { } contains)
        {
            return matched;
        }

        var found = identity.ProcessorName?.Contains(contains.Trim(), StringComparison.OrdinalIgnoreCase) == true;
        explanations.Add(found
            ? $"processor contains '{contains}'."
            : $"processor mismatch: expected to contain '{contains}', observed '{identity.ProcessorName ?? "<missing>"}'.");
        return matched && found;
    }

    private static bool Field(string label, string? expected, string? actual, List<string> explanations)
    {
        if (expected is null)
        {
            return true;
        }

        var matched = string.Equals(expected.Trim(), actual?.Trim(), StringComparison.OrdinalIgnoreCase);
        explanations.Add(matched
            ? $"{label} matched '{expected}'."
            : $"{label} mismatch: expected '{expected}', observed '{actual ?? "<missing>"}'.");
        return matched;
    }

    private static bool ControllerMatches(
        DeviceKnowledgeRecord record,
        CompiledReadProbeFamily family,
        MachineInventory inventory,
        List<string> explanations)
    {
        var endpoints = record.HidEndpoints
            .Where(endpoint => string.Equals(endpoint.Role, "controller", StringComparison.Ordinal))
            .GroupBy(endpoint => endpoint.VendorId, StringComparer.OrdinalIgnoreCase)
            .Select(group => (VendorId: group.Key, ProductIds: group
                .SelectMany(endpoint => endpoint.ProductIds)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()))
            .ToArray();
        if (endpoints.Length == 0)
        {
            explanations.Add($"USB endpoint mismatch: {record.Id} declares no controller endpoint.");
            return false;
        }

        var matched = true;
        foreach (var (vendorId, productIds) in endpoints)
        {
            // The release (bcdDevice) is the controller firmware revision, which vendors update in the
            // field; it is reported for the record and compared against the reference, never required.
            var controller = inventory.UsbInterfaces.FirstOrDefault(endpoint =>
                string.Equals(endpoint.VendorId, vendorId, StringComparison.OrdinalIgnoreCase)
                && productIds.Contains(endpoint.ProductId ?? string.Empty, StringComparer.OrdinalIgnoreCase));
            var expected = $"{vendorId}:[{string.Join(", ", productIds)}]";
            explanations.Add(controller is not null
                ? $"USB endpoint matched {expected}, release {controller.DeviceRelease ?? "<missing>"} "
                  + $"(reference {family.ReferenceUsbDeviceRelease})."
                : $"USB endpoint mismatch: expected {expected}.");
            matched &= controller is not null;
        }

        return matched;
    }

    private static bool WmiMatches(DeviceKnowledgeRecord record, MachineInventory inventory, List<string> explanations)
    {
        var providers = record.Mechanisms
            .Where(mechanism => string.Equals(mechanism.Transport, "wmi-method", StringComparison.Ordinal))
            .Select(mechanism => (
                Namespace: mechanism.Parameters.GetValueOrDefault("namespace"),
                ClassName: mechanism.Parameters.GetValueOrDefault("class")))
            .Where(provider => provider is { Namespace: not null, ClassName: not null })
            .Distinct()
            .ToArray();
        if (providers.Length == 0)
        {
            explanations.Add($"WMI provider mismatch: {record.Id} declares no WMI method provider.");
            return false;
        }

        var matched = true;
        foreach (var (wmiNamespace, className) in providers)
        {
            var present = inventory.WmiClasses.Any(provider =>
                provider.Access is WmiAccess.Available or WmiAccess.AccessDenied
                && string.Equals(provider.Namespace, wmiNamespace, StringComparison.OrdinalIgnoreCase)
                && string.Equals(provider.ClassName, className, StringComparison.Ordinal));
            explanations.Add(present
                ? $"WMI provider matched {wmiNamespace}:{className}."
                : $"WMI provider mismatch: expected {wmiNamespace}:{className}.");
            matched &= present;
        }

        return matched;
    }
}
