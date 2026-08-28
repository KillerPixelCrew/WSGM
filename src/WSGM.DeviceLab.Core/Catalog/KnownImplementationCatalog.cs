using System;
using System.Collections.Generic;
using System.Linq;
using WSGM.Device.Contracts.Identity;
using WSGM.Device.Contracts.Modules;
using WSGM.Device.Contracts.Packaging;
using WSGM.DeviceLab.Core.Evidence;
using WSGM.DeviceLab.Core.Inventory;

namespace WSGM.DeviceLab.Core.Catalog;

/// <summary>
/// One catalog entry: a known implementation module plus what has to be true for it to be reusable.
/// </summary>
/// <remarks>
/// The catalog is developer tooling, not runtime. Normal WSGM device detection never consults it —
/// only Device Lab does, when a maintainer is asking "has anyone solved this hardware before".
/// </remarks>
public sealed record CatalogEntry
{
    /// <summary>The module this entry describes.</summary>
    public required ImplementationModule Module { get; init; }

    /// <summary>
    /// Observations that must hold before the module is considered at all.
    /// </summary>
    /// <remarks>
    /// Evaluated as hard constraints before any scoring. A wrong report length, an excluded firmware
    /// version, a missing WMI method, or an incompatible CPU family rejects the module outright
    /// rather than lowering its rank — the difference between "unlikely" and "cannot work".
    /// </remarks>
    public IReadOnlyList<IdentityObservation> CandidateMatching { get; init; } = [];

    /// <summary>Claims backing this module's constants.</summary>
    public IReadOnlyList<EvidenceClaim> Claims { get; init; } = [];

    /// <summary>
    /// Values that are specific to the devices this module was verified on.
    /// </summary>
    /// <remarks>
    /// Named explicitly, in plain language, so a developer reusing the module sees what does not come
    /// with it. "Reuse the transport, not the limits" is only actionable if the limits are listed.
    /// </remarks>
    public IReadOnlyList<string> NonInheritableValues { get; init; } = [];
}

/// <summary>
/// Matches a machine against known implementation modules.
/// </summary>
/// <remarks>
/// Offline and deterministic: no device handle is opened, and the same inventory produces the same
/// ranking regardless of the order entries were registered. Determinism matters because a developer
/// comparing two sweeps needs the difference to mean something about the hardware.
/// </remarks>
public static class CandidateMatcher
{
    /// <summary>
    /// Ranks catalog entries against one machine.
    /// </summary>
    /// <param name="inventory">The observed machine.</param>
    /// <param name="catalog">Known modules to consider.</param>
    /// <param name="targetDeviceId">
    /// The device definition being built, used to decide whether a device-scoped module applies.
    /// </param>
    /// <param name="quarantinedResources">Resources blocked after a failed restoration.</param>
    /// <returns>Every entry assessed, best reuse rank first, then module ID for stability.</returns>
    public static IReadOnlyList<CandidateAssessment> Rank(
        MachineInventory inventory,
        IReadOnlyList<CatalogEntry> catalog,
        string targetDeviceId,
        IReadOnlySet<string>? quarantinedResources = null)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDeviceId);

        DeviceIdentitySnapshot snapshot = InventorySnapshot.From(inventory);
        List<CandidateAssessment> assessments = [];

        foreach (CatalogEntry entry in catalog)
        {
            assessments.Add(Assess(entry, snapshot, targetDeviceId, quarantinedResources));
        }

        // Sorted by rank, then by module ID. The tiebreak is what makes the output identical across
        // runs when two modules score the same.
        assessments.Sort((a, b) =>
        {
            int byRank = b.ReuseRank.CompareTo(a.ReuseRank);
            return byRank != 0 ? byRank : string.CompareOrdinal(a.ModuleId, b.ModuleId);
        });

        return assessments;
    }

    private static CandidateAssessment Assess(
        CatalogEntry entry,
        DeviceIdentitySnapshot snapshot,
        string targetDeviceId,
        IReadOnlySet<string>? quarantinedResources)
    {
        DeviceDefinition probe = new()
        {
            Id = targetDeviceId,
            DisplayName = entry.Module.DisplayName,
            Identity = entry.CandidateMatching,
        };

        IdentityMatchResult match = IdentityMatcher.Match(probe, snapshot);
        List<string> explanations = [.. match.Explanations.Select(e => e.Explanation)];

        // A rejected module keeps rank 0 rather than its score. The score would invite reading a
        // rejection as "close", and closeness is exactly what must not influence anything here.
        bool rejected = match.Outcome is IdentityMatchOutcome.Rejected;

        // Device scope is checked as a hard constraint of its own: a layout or policy module
        // verified elsewhere cannot be reused here however well its identity predicates match,
        // because its constants belong to another board.
        bool deviceSpecific = entry.Module.Layer is ModuleLayer.Layout or ModuleLayer.Policy;
        if (deviceSpecific
            && !entry.Module.VerifiedDeviceIds.Contains(targetDeviceId, StringComparer.OrdinalIgnoreCase))
        {
            rejected = true;
            explanations.Add(
                $"{entry.Module.Layer} module was verified on "
                    + $"[{string.Join(", ", entry.Module.VerifiedDeviceIds)}], not on '{targetDeviceId}'.");
        }

        EvidenceGrade grade = CandidateGrading.GradeFor(ScopedClaims(entry, snapshot));

        bool quarantined = quarantinedResources is not null
            && entry.Module.Capabilities.Any(quarantinedResources.Contains);

        return new CandidateAssessment
        {
            ModuleId = entry.Module.Id,
            ModuleVersion = entry.Module.Version,
            ReuseRank = rejected ? 0 : match.Score,
            EvidenceGrade = rejected ? EvidenceGrade.None : grade,
            WriteEligibility = rejected
                ? WriteEligibility.ReadOnly
                : CandidateGrading.EligibilityFor(grade, quarantined),
            Explanations = explanations,
            NonInheritableValues = entry.NonInheritableValues,
        };
    }

    /// <summary>
    /// Returns only the claims that actually apply to the observed board.
    /// </summary>
    /// <remarks>
    /// A claim proven on another board says nothing here, so counting it would inflate the grade with
    /// evidence about different hardware — the exact mistake the scope field exists to prevent.
    /// </remarks>
    private static IReadOnlyList<EvidenceClaim> ScopedClaims(
        CatalogEntry entry,
        DeviceIdentitySnapshot snapshot) =>
        [.. entry.Claims.Where(claim =>
            IdentityText.Matches(snapshot.BaseboardProduct, claim.Scope.BaseboardProduct))];
}

/// <summary>Projects a machine inventory into the identity snapshot the matcher consumes.</summary>
public static class InventorySnapshot
{
    /// <summary>
    /// Converts an inventory into a matchable snapshot.
    /// </summary>
    /// <param name="inventory">The observed machine.</param>
    /// <returns>The snapshot form.</returns>
    /// <remarks>
    /// A class that could not be reached still contributes its signature, because presence is what a
    /// predicate gates on and an access-denied provider is present. Only <see cref="WmiAccess.NotFound"/>
    /// means absent.
    /// </remarks>
    public static DeviceIdentitySnapshot From(MachineInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);

        return new DeviceIdentitySnapshot
        {
            SystemManufacturer = inventory.Firmware.SystemManufacturer,
            SystemProduct = inventory.Firmware.SystemProduct,
            SystemSku = inventory.Firmware.SystemSku,
            SystemFamily = inventory.Firmware.SystemFamily,
            BaseboardProduct = inventory.Firmware.BaseboardProduct,
            BaseboardVersion = inventory.Firmware.BaseboardVersion,
            BiosVersion = inventory.Firmware.BiosVersion,
            CpuIdentity = inventory.Processor?.NormalizedIdentity,
            UsbEndpoints = [.. inventory.UsbInterfaces
                .Where(i => i.VendorId is not null && i.ProductId is not null)
                .Select(i => new UsbEndpointObservation
                {
                    VendorId = i.VendorId!,
                    ProductId = i.ProductId!,
                    InterfaceNumber = i.InterfaceNumber,
                    LocationPath = i.LocationPath,
                })],
            WmiProviderSignatures = [.. inventory.WmiClasses
                .Where(c => c.Access is not WmiAccess.NotFound)
                .Select(c => $"{c.Namespace}:{c.ClassName}")],
        };
    }
}
