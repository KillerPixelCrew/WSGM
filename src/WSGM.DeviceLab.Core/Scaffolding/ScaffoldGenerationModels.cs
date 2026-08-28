using System;
using System.Collections.Generic;
using WSGM.Device.Contracts.Packaging;
using WSGM.DeviceLab.Core.Catalog;
using WSGM.DeviceLab.Core.Evidence;

namespace WSGM.DeviceLab.Core.Scaffolding;

/// <summary>Exact device facts emitted into the generated detector.</summary>
public sealed record ScaffoldExactIdentity
{
    /// <summary>Required SMBIOS system manufacturer.</summary>
    public required string SystemManufacturer { get; init; }

    /// <summary>Required SMBIOS baseboard product.</summary>
    public required string BaseboardProduct { get; init; }

    /// <summary>Allowlisted exact BIOS or firmware identities.</summary>
    public required IReadOnlyList<string> FirmwareIdentities { get; init; }

    /// <summary>Required endpoint identifier.</summary>
    public required string EndpointId { get; init; }

    /// <summary>Endpoint role shown in diagnostics.</summary>
    public required string EndpointRole { get; init; }

    /// <summary>USB vendor ID as four uppercase hexadecimal digits.</summary>
    public required string VendorId { get; init; }

    /// <summary>Exact USB product IDs for this endpoint.</summary>
    public required IReadOnlyList<string> ProductIds { get; init; }
}

/// <summary>One version-pinned implementation module and its architectural layer.</summary>
public sealed record ScaffoldModuleSelection
{
    /// <summary>Stable module ID.</summary>
    public required string ModuleId { get; init; }

    /// <summary>Exact module version.</summary>
    public required int Version { get; init; }

    /// <summary>Transport, protocol, layout, policy, or capability layer.</summary>
    public required ModuleLayer Layer { get; init; }
}

/// <summary>One independently owned resource in the generated skeleton.</summary>
public sealed record ScaffoldResourceSelection
{
    /// <summary>Stable resource ID.</summary>
    public required string ResourceId { get; init; }

    /// <summary>Semantic transport class.</summary>
    public required ResourceKind Kind { get; init; }

    /// <summary>Requested access, narrowed to read when evidence cannot authorize writes.</summary>
    public required ResourceAccess RequestedAccess { get; init; }

    /// <summary>Exact endpoint binding.</summary>
    public string? EndpointId { get; init; }

    /// <summary>Fields the plugin must retain in its recovery journal for this resource.</summary>
    public IReadOnlyList<string> RecoveryJournalFields { get; init; } = [];
}

/// <summary>One semantic capability considered for generated registration or explicit unavailability.</summary>
public sealed record ScaffoldCapabilitySelection
{
    /// <summary>Stable semantic capability ID.</summary>
    public required string CapabilityId { get; init; }

    /// <summary>Independently owned resource serving the capability.</summary>
    public required string ResourceId { get; init; }

    /// <summary>Claim IDs required for parsing and behavior.</summary>
    public required IReadOnlyList<string> RequiredClaimIds { get; init; }

    /// <summary>Candidate write eligibility derived independently of reuse rank.</summary>
    public required WriteEligibility WriteEligibility { get; init; }

    /// <summary>Whether verified parsing code should be generated when evidence qualifies.</summary>
    public required bool GenerateParser { get; init; }
}

/// <summary>Complete deterministic scaffold request built from a sanitized capture and accepted evidence.</summary>
public sealed record ScaffoldGenerationRequest
{
    /// <summary>Frozen versioned input manifest.</summary>
    public required ScaffoldInputManifest Input { get; init; }

    /// <summary>Stable package ID.</summary>
    public required string PackageId { get; init; }

    /// <summary>Root C# namespace.</summary>
    public required string RootNamespace { get; init; }

    /// <summary>Human-readable device/package name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Publisher label; package trust is still not granted.</summary>
    public required string Publisher { get; init; }

    /// <summary>Exact detector facts.</summary>
    public required ScaffoldExactIdentity Identity { get; init; }

    /// <summary>Version-pinned module composition.</summary>
    public required IReadOnlyList<ScaffoldModuleSelection> Modules { get; init; }

    /// <summary>Independent resource graph.</summary>
    public required IReadOnlyList<ScaffoldResourceSelection> Resources { get; init; }

    /// <summary>Semantic capabilities with exact evidence requirements.</summary>
    public required IReadOnlyList<ScaffoldCapabilitySelection> Capabilities { get; init; }

    /// <summary>Accepted claim ledger entries from the source capture.</summary>
    public required IReadOnlyList<EvidenceClaim> Claims { get; init; }
}

/// <summary>One generated file with content plus its regeneration ownership.</summary>
public sealed record ScaffoldGeneratedFile
{
    /// <summary>Canonical relative path.</summary>
    public required string Path { get; init; }

    /// <summary>Regeneration ownership.</summary>
    public required ScaffoldFileOwnership Ownership { get; init; }

    /// <summary>Canonical UTF-8 text content with LF line endings.</summary>
    public required string Content { get; init; }
}

/// <summary>Complete in-memory scaffold plan, still incapable of touching hardware.</summary>
public sealed record ScaffoldGenerationPlan
{
    /// <summary>Validated frozen input.</summary>
    public required ScaffoldInputManifest Input { get; init; }

    /// <summary>Canonical evidence lock emitted beside generated code.</summary>
    public required EvidenceLock EvidenceLock { get; init; }

    /// <summary>Files in ordinal path order.</summary>
    public required IReadOnlyList<ScaffoldGeneratedFile> Files { get; init; }

    /// <summary>Output manifest with file hashes and ownership.</summary>
    public required ScaffoldOutputManifest Output { get; init; }

    /// <summary>Capabilities deliberately emitted unavailable and why.</summary>
    public IReadOnlyDictionary<string, string> UnavailableCapabilities { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>Result of comparing a previous and regenerated scaffold plan.</summary>
public sealed record ScaffoldRegenerationReview
{
    /// <summary>Evidence changes requiring semantic review.</summary>
    public IReadOnlyList<EvidenceLockChange> EvidenceChanges { get; init; } = [];

    /// <summary>Fixture IDs added or removed.</summary>
    public IReadOnlyList<string> FixtureChanges { get; init; } = [];

    /// <summary>Generated files whose content changed.</summary>
    public IReadOnlyList<string> GeneratedFileChanges { get; init; } = [];

    /// <summary>Whether an explicit semantic acceptance is required before writing.</summary>
    public required bool RequiresExplicitReview { get; init; }
}
